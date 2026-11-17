using System.Diagnostics;
using System.IO;

namespace PitzkiLauncher;

/// <summary>Builds the list of things to search: apps, files/folders, settings and actions.</summary>
public class Indexer
{
    public volatile List<Item> Apps = new();
    public volatile List<Item> Files = new();
    public readonly List<Item> Builtins = BuildBuiltins();
    public volatile Dictionary<string, Item> ByKey = new();
    public DateTime AppsAt = DateTime.MinValue, FilesAt = DateTime.MinValue;
    public event Action? Updated;
    private int _busy;

    private const int MaxFiles = 40000;
    // Files/folders rank below apps, settings and actions that match just as well
    // ("lock" -> Lock the PC, "chrome" -> Chrome, not ChromeSetup.exe). An exact file name still wins.
    private const int FilePenalty = -250;
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".vs", ".vscode", "obj", "bin", "packages", "__pycache__", ".idea", "AppData",
        "$RECYCLE.BIN", "System Volume Information", "venv", ".venv", "target", "cache", ".cache"
    };

    public void Refresh(bool files)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        var t = new Thread(() =>
        {
            try
            {
                Apps = ScanApps();
                AppsAt = DateTime.UtcNow;
                RebuildKeys();
                Updated?.Invoke();
                if (files)
                {
                    Files = ScanFiles();
                    FilesAt = DateTime.UtcNow;
                    RebuildKeys();
                    Updated?.Invoke();
                }
                foreach (var a in Apps) IconCache.Get(a);   // warm the icons so typing feels instant
            }
            catch { }
            finally { Interlocked.Exchange(ref _busy, 0); }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal };
        t.SetApartmentState(ApartmentState.STA);   // Shell COM objects want STA
        t.Start();
    }

    private void RebuildKeys()
    {
        var d = new Dictionary<string, Item>();
        foreach (var it in Builtins.Concat(Apps).Concat(Files)) d.TryAdd(it.Key, it);
        ByKey = d;
    }

    // ---------------- apps ----------------

    private static bool Junk(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("uninstall") || n.Contains("הסרת התקנה") || n.Contains("הסר התקנה") || n.StartsWith("readme")
               || n.Contains("release notes") || n.EndsWith(" website");
    }

    private static List<Item> ScanApps()
    {
        var list = new List<Item>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Every app Windows knows (Start menu + Store apps) through shell:AppsFolder
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type != null)
            {
                dynamic shell = Activator.CreateInstance(type)!;
                dynamic folder = shell.NameSpace("shell:AppsFolder");
                dynamic items = folder.Items();
                int n = items.Count;
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        dynamic fi = items.Item(i);
                        string name = (string)fi.Name;
                        string path = (string)fi.Path;
                        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path) || Junk(name)) continue;
                        if (!seen.Add(name)) continue;
                        string target = "shell:AppsFolder\\" + path;
                        string? file = Path.IsPathRooted(path) && File.Exists(path) ? path : null;
                        var kw = EnglishKeywords(path);
                        // Windows in Hebrew names Calculator "מחשבון": show the English name under it
                        bool latin = name.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
                        string sub = !latin && kw.Length > 0 ? "App · " + Cap(kw[0]) : "App";
                        list.Add(new Item
                        {
                            Key = "app:" + name.ToLowerInvariant(), Title = name, Sub = sub, Kind = Kind.App,
                            Target = target, IconPath = target, FilePath = file, KindBoost = 60, Keywords = kw
                        }.Prep());
                    }
                    catch { }
                }
            }
        }
        catch { }

        // 2) Shortcuts on the Start menu / desktop (fallback, and things AppsFolder hides)
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        // the demo leaves out desktop shortcuts: they are the person's own
        foreach (var d in dirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Take(Demo.On ? 2 : 4))
        {
            Walk(d, 0, 4, f =>
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".lnk" or ".url" or ".appref-ms" or ".exe")) return;
                var name = Path.GetFileNameWithoutExtension(f);
                if (Junk(name) || !seen.Add(name)) return;
                list.Add(new Item
                {
                    Key = "app:" + name.ToLowerInvariant(), Title = name, Sub = "Shortcut", Kind = Kind.App,
                    Target = f, IconPath = f, FilePath = f, KindBoost = 60
                }.Prep());
            }, null);
        }

        // 3) The Pitzki apps in C:\Users\Public\Downloads
        try
        {
            var root = @"C:\Users\Public\Downloads";
            var found = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(root))
            {
                Walk(root, 0, 6, f =>
                {
                    var fn = Path.GetFileName(f);
                    if (!fn.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return;
                    if (!(fn.StartsWith("Pitzki", StringComparison.OrdinalIgnoreCase) || fn.StartsWith("Piztki", StringComparison.OrdinalIgnoreCase))) return;
                    if (f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase)) return;
                    var fi = new FileInfo(f);
                    var nm = Path.GetFileNameWithoutExtension(fn);
                    if (!found.TryGetValue(nm, out var old) || Prefer(fi, old)) found[nm] = fi;
                }, null, allowBin: true);
            }
            foreach (var (nm, fi) in found)
            {
                var title = Pretty(nm);
                if (!seen.Add(title)) continue;
                list.Add(new Item
                {
                    Key = "app:" + title.ToLowerInvariant(), Title = title, Sub = "Pitzki app · " + fi.DirectoryName,
                    Kind = Kind.App, Target = fi.FullName, IconPath = fi.FullName, FilePath = fi.FullName, KindBoost = 70,
                    Keywords = new[] { "pitzki" }
                }.Prep());
            }
        }
        catch { }

        // the demo shows only apps everyone has, never the person's own games and programs
        if (Demo.On)
        {
            string[] everyone = { "calculator", "paint", "notepad", "settings", "photos", "camera", "clock", "weather", "microsoft store", "spotify", "discord", "task manager", "file explorer" };
            list = list.Where(i => i.Title.StartsWith("Pitzki", StringComparison.OrdinalIgnoreCase)
                || everyone.Contains(i.Title.ToLowerInvariant())
                || (i.Keywords?.Any(k => everyone.Contains(k.ToLowerInvariant())) ?? false)).ToList();
        }
        return list;
    }

    // Friendly English names for built-in apps whose shown name may be in another language.
    // Key = lowercase piece of the AppsFolder parsing name (AUMID or exe path).
    private static readonly (string Part, string[] Kw)[] KnownApps =
    {
        ("windowscalculator_", new[] { "calculator", "calc" }),
        ("windows.photos_", new[] { "photos", "pictures", "gallery" }),
        ("windowscamera_", new[] { "camera" }),
        ("windowsalarms_", new[] { "clock", "alarms", "timer", "stopwatch" }),
        ("windowslive.mail", new[] { "mail", "email" }),
        ("windowslive.calendar", new[] { "calendar" }),
        ("windowsmaps_", new[] { "maps" }),
        ("windowsstore_", new[] { "microsoft store", "store" }),
        ("microsoftstickynotes_", new[] { "sticky notes", "notes" }),
        ("windowssoundrecorder_", new[] { "sound recorder", "voice recorder" }),
        ("screensketch_", new[] { "snip and sketch", "snipping" }),
        ("zunemusic_", new[] { "media player", "music" }),
        ("zunevideo_", new[] { "movies and tv", "video" }),
        ("bingweather_", new[] { "weather" }),
        ("yourphone_", new[] { "phone link" }),
        ("getstarted_", new[] { "tips" }),
        ("windowsfeedbackhub_", new[] { "feedback hub" }),
        ("gethelp_", new[] { "get help" }),
        ("mspaint_", new[] { "paint 3d" }),
        ("microsoft3dviewer_", new[] { "3d viewer" }),
        ("sechealthui_", new[] { "windows security", "antivirus", "defender" }),
        ("immersivecontrolpanel", new[] { "settings" }),
        ("windowsterminal_", new[] { "terminal" }),
        ("windowsnotepad_", new[] { "notepad" }),
        ("windowsbackup", new[] { "windows backup", "backup" }),
        ("\\notepad.exe", new[] { "notepad" }),
        ("\\mspaint.exe", new[] { "paint" }),
        ("\\cmd.exe", new[] { "command prompt", "cmd" }),
        ("\\snippingtool.exe", new[] { "snipping tool" }),
        ("\\wordpad.exe", new[] { "wordpad" }),
        ("\\charmap.exe", new[] { "character map" }),
        ("\\cleanmgr.exe", new[] { "disk cleanup" }),
        ("\\magnify.exe", new[] { "magnifier" }),
        ("\\osk.exe", new[] { "on-screen keyboard", "keyboard" }),
        ("windows.explorer", new[] { "file explorer", "explorer" }),
        ("windows.controlpanel", new[] { "control panel" }),
        ("windows.computer", new[] { "this pc", "my computer" }),
        ("windows.shell.rundialog", new[] { "run" }),
        ("windows.remotedesktop", new[] { "remote desktop" }),
    };

    /// <summary>English search words for an AppsFolder item, from its AUMID / exe name
    /// (so "calc" finds "מחשבון" = Microsoft.WindowsCalculator).</summary>
    private static string[] EnglishKeywords(string path)
    {
        var kw = new List<string>();
        var lp = path.ToLowerInvariant();
        foreach (var (part, words) in KnownApps)
            if (lp.Contains(part)) kw.AddRange(words);

        string? raw = null;
        int bang = path.IndexOf('!');
        if (bang > 0)
        {
            // "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App" -> "WindowsCalculator"
            var pkg = path[..bang];
            int us = pkg.IndexOf('_');
            if (us > 0) pkg = pkg[..us];
            var segs = pkg.Split('.', StringSplitOptions.RemoveEmptyEntries);
            raw = string.Join(" ", segs.Length > 1 ? segs.Skip(1) : segs);
        }
        else if (path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            raw = Path.GetFileNameWithoutExtension(path);   // "SnippingTool"
        }
        if (raw != null)
        {
            var phrase = Pretty(raw).Replace('.', ' ').ToLowerInvariant();
            var words = Search.SplitWords(phrase).Where(w => w is not ("microsoft" or "windows" or "app" or "desktop") && w.Count(char.IsDigit) < 3).ToArray();
            if (words.Length > 0 && words.Any(w => w.Length >= 3))
            {
                kw.Add(string.Join(" ", words));
                kw.AddRange(words.Where(w => w.Length >= 3));
            }
        }
        return kw.Distinct().ToArray();
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    // dist builds beat bin builds; otherwise the newest wins
    private static bool Prefer(FileInfo a, FileInfo b)
    {
        bool ad = a.FullName.Contains(@"\dist\", StringComparison.OrdinalIgnoreCase);
        bool bd = b.FullName.Contains(@"\dist\", StringComparison.OrdinalIgnoreCase);
        if (ad != bd) return ad;
        return a.LastWriteTimeUtc > b.LastWriteTimeUtc;
    }

    // "PitzkiWidgets" -> "Pitzki Widgets"
    private static string Pretty(string n)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < n.Length; i++)
        {
            if (i > 0 && char.IsUpper(n[i]) && char.IsLower(n[i - 1])) sb.Append(' ');
            sb.Append(n[i]);
        }
        return sb.ToString();
    }

    // ---------------- files ----------------

    private static List<Item> ScanFiles()
    {
        var list = new List<Item>();
        var home = Demo.On ? Demo.Folder! : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // recently opened files (Windows keeps shortcuts to them)
        if (!Demo.On) try
        {
            var recent = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
            if (Directory.Exists(recent))
            {
                foreach (var f in new DirectoryInfo(recent).EnumerateFiles("*.lnk").OrderByDescending(x => x.LastWriteTimeUtc).Take(300))
                {
                    var name = Path.GetFileNameWithoutExtension(f.Name);
                    list.Add(new Item
                    {
                        Key = "recent:" + name.ToLowerInvariant(), Title = name, Sub = "Recently opened", Kind = Kind.File,
                        Target = f.FullName, IconPath = f.FullName, KindBoost = FilePenalty + 30
                    }.Prep());
                }
            }
        }
        catch { }

        var roots = Demo.On ? new List<string> { Demo.Folder! } : new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var root in roots)
        {
            if (list.Count >= MaxFiles) break;
            Walk(root, 0, 5, f =>
            {
                if (list.Count >= MaxFiles) return;
                var ext = Path.GetExtension(f);
                if (ext.Equals(".ini", StringComparison.OrdinalIgnoreCase) || ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase)) return;
                list.Add(new Item
                {
                    Key = "file:" + f.ToLowerInvariant(), Title = Path.GetFileName(f), Sub = Short(Path.GetDirectoryName(f) ?? "", home),
                    Kind = Kind.File, Target = f, IconPath = f, FilePath = f, KindBoost = FilePenalty
                }.Prep());
            }, d =>
            {
                if (list.Count >= MaxFiles) return;
                list.Add(new Item
                {
                    Key = "file:" + d.ToLowerInvariant(), Title = Path.GetFileName(d), Sub = Short(Path.GetDirectoryName(d) ?? "", home),
                    Kind = Kind.Folder, Target = d, IconPath = d, FilePath = d, KindBoost = FilePenalty + 10
                }.Prep());
            });
        }
        return list;
    }

    private static string Short(string dir, string home) =>
        dir.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? dir[home.Length..].TrimStart('\\').Replace("\\", " › ") : dir;

    private static void Walk(string dir, int depth, int maxDepth, Action<string> onFile, Action<string>? onDir, bool allowBin = false)
    {
        try
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            foreach (var f in Directory.EnumerateFiles(dir, "*", opts)) onFile(f);
            if (depth >= maxDepth) return;
            foreach (var d in Directory.EnumerateDirectories(dir, "*", opts))
            {
                var n = Path.GetFileName(d);
                if (SkipDirs.Contains(n) && !(allowBin && n.Equals("bin", StringComparison.OrdinalIgnoreCase))) continue;
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                onDir?.Invoke(d);
                Walk(d, depth + 1, maxDepth, onFile, onDir, allowBin);
            }
        }
        catch { }
    }

    // ---------------- settings & actions ----------------

    private static void Shell(string target, string? args = null)
    {
        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (args != null) psi.Arguments = args;
        Process.Start(psi);
    }

    private static void Hidden(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });

    private static List<Item> BuildBuiltins()
    {
        var l = new List<Item>();
        void S(string title, string uri, string glyph, params string[] kw) =>
            l.Add(new Item { Key = "set:" + uri, Title = title, Sub = "Windows settings", Kind = Kind.Setting, Target = uri, Glyph = glyph, Keywords = kw, KindBoost = 40 }.Prep());
        void C(string title, string sub, string glyph, Action run, bool confirm, params string[] kw) =>
            l.Add(new Item { Key = "cmd:" + title, Title = title, Sub = sub, Kind = Kind.Command, Glyph = glyph, Run = run, Confirm = confirm, Keywords = kw, KindBoost = 40 }.Prep());

        S("Wi-Fi and network", "ms-settings:network-wifi", "\uE701", "wifi", "wi-fi", "network", "internet");
        S("Bluetooth", "ms-settings:bluetooth", "\uE702", "bluetooth", "headphones");
        S("Sound", "ms-settings:sound", "\uE767", "sound", "audio", "speaker", "mic", "microphone");
        S("Display and brightness", "ms-settings:display", "\uE7F4", "display", "screen", "resolution", "brightness", "scale");
        S("Night light", "ms-settings:nightlight", "\uE708", "night light", "night", "blue light");
        S("Windows Update", "ms-settings:windowsupdate", "\uE895", "windows update", "update");
        S("Installed apps", "ms-settings:appsfeatures", "\uE71D", "apps", "uninstall", "programs", "installed");
        S("Startup apps", "ms-settings:startupapps", "\uE7B5", "startup", "startup apps");
        S("Desktop background", "ms-settings:personalization-background", "\uE771", "background", "wallpaper");
        S("Colors and dark mode", "ms-settings:colors", "\uE790", "dark mode", "colors", "theme", "light mode");
        S("Power and battery", "ms-settings:powersleep", "\uE83F", "power", "battery", "sleep settings");
        S("Storage", "ms-settings:storagesense", "\uEDA2", "storage", "disk", "space");
        S("Mouse", "ms-settings:mousetouchpad", "\uE962", "mouse", "touchpad", "cursor");
        S("Language and keyboard", "ms-settings:regionlanguage", "\uE765", "language", "keyboard", "hebrew");
        S("Notifications", "ms-settings:notifications", "\uEA8F", "notifications", "focus", "do not disturb");
        S("Date and time", "ms-settings:dateandtime", "\uE823", "time", "date", "clock", "timezone");
        S("Printers", "ms-settings:printers", "\uE749", "printer", "print");
        S("Privacy and camera", "ms-settings:privacy", "\uE72E", "privacy", "camera");
        S("Game mode", "ms-settings:gaming-gamemode", "\uE7FC", "game mode", "gaming");
        S("Default apps", "ms-settings:defaultapps", "\uE71D", "default apps", "default browser");
        S("About this PC", "ms-settings:about", "\uE7F8", "about", "pc name", "specs", "system info");
        S("Settings", "ms-settings:", "\uE713", "settings");
        S("Control Panel", "control", "\uE713", "control panel");
        S("Device Manager", "devmgmt.msc", "\uE964", "device manager", "drivers");

        C("Lock the PC", "Action", "\uE72E", () => Native.LockWorkStation(), false, "lock");
        C("Sleep", "Action · needs confirmation", "\uE708", () => Native.SetSuspendState(false, false, false), true, "sleep");
        C("Restart", "Action · needs confirmation", "\uE72C", () => Hidden("shutdown", "/r /t 0"), true, "restart", "reboot");
        C("Shut down", "Action · needs confirmation", "\uE7E8", () => Hidden("shutdown", "/s /t 0"), true, "shutdown", "shut down", "power off");
        C("Sign out", "Action · needs confirmation", "\uE77B", () => Hidden("shutdown", "/l"), true, "sign out", "logout", "log off");
        C("Empty the Recycle Bin", "Action · Windows will ask to confirm", "\uE74D", () => Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0), false, "empty recycle bin", "empty trash");
        C("Recycle Bin", "Open", "\uE74D", () => Shell("explorer.exe", "shell:RecycleBinFolder"), false, "recycle bin", "trash");
        C("Task Manager", "Action", "\uE9D9", () => Shell("taskmgr"), false, "task manager", "taskmgr");
        C("Screenshot", "Action", "\uE722", () => Shell("ms-screenclip:"), false, "screenshot", "snip");
        C("This PC", "Open", "\uE7F8", () => Shell("explorer.exe", "shell:MyComputerFolder"), false, "this pc", "my computer", "explorer");
        C("Downloads", "Folder", "\uE896", () => Shell(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")), false, "downloads");
        return l;
    }
}
