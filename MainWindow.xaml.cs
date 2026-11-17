using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace PitzkiLauncher;

public class ResultVm
{
    public required Item It { get; init; }
    public string? SubOverride { get; init; }
    public ImageSource? Icon { get; init; }
    public string Title => It.Title;
    public string Sub => SubOverride ?? It.Sub;
    public string Glyph => Icon == null ? It.Glyph : "";
    public string KindText => It.Kind switch
    {
        Kind.App => "App",
        Kind.File => "File",
        Kind.Folder => "Folder",
        Kind.Setting => "Setting",
        Kind.Command => "Action",
        Kind.Calc => "Calculator",
        _ => "Web"
    };
}

public partial class MainWindow : Window
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "PitzkiLauncher";
    private const int HotkeyId = 0x5A17;
    private const int MaxResults = 8;
    private const string DefaultFooter = "↑↓ select · Enter open · Ctrl+Enter open folder · Ctrl+Shift+Enter as admin · Esc close";

    private static readonly (string Label, uint Mods, uint Vk)[] Hotkeys =
    {
        ("Alt + Space", Native.MOD_ALT, 0x20),
        ("Ctrl + Alt + Space", Native.MOD_CONTROL | Native.MOD_ALT, 0x20),
        ("Ctrl + Shift + Space", Native.MOD_CONTROL | Native.MOD_SHIFT, 0x20),
        ("Alt + Q", Native.MOD_ALT, 0x51),
    };

    private readonly LauncherData _data = LauncherData.Load();
    private readonly Indexer _index = new();
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ToolStripMenuItem _hotkeyMenu;
    private IntPtr _hwnd;
    private int _activeHotkey = -1;
    private bool _exiting;
    private string? _confirmKey;

    public MainWindow(bool startHidden)
    {
        InitializeComponent();
        LoadPitzki();
        Footer.Text = DefaultFooter;

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        // tray
        _tray = new Forms.NotifyIcon { Text = "Pitzki Launcher", Icon = LoadTrayIcon(), Visible = true };
        TrimSoon();
        var menu = new Forms.ContextMenuStrip { RightToLeft = Forms.RightToLeft.No };
        menu.Items.Add("Open search", null, (_, _) => ShowLauncher(false));
        _hotkeyMenu = new Forms.ToolStripMenuItem("Hotkey");
        for (int i = 0; i < Hotkeys.Length; i++)
        {
            int idx = i;
            var mi = new Forms.ToolStripMenuItem(Hotkeys[i].Label);
            mi.Click += (_, _) => { if (TryHotkey(idx)) { _data.HotkeyIndex = idx; _data.Save(); } else Balloon($"{Hotkeys[idx].Label} is taken by another program"); };
            _hotkeyMenu.DropDownItems.Add(mi);
        }
        menu.Items.Add(_hotkeyMenu);
        var startup = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        menu.Items.Add(startup);
        menu.Items.Add("Refresh search", null, (_, _) => { _index.Refresh(true); Balloon("Pitzki is rescanning the PC"); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowLauncher(false); };

        // first run: start with Windows by default (a launcher is useless if it isn't running)
        bool firstRun = !_data.FirstRunDone;
        if (firstRun)
        {
            SetStartup(true);
            _data.FirstRunDone = true;
            _data.Save();
        }
        startup.Checked = IsInStartup();

        // hotkey (fall back to the next free one)
        int want = Math.Clamp(_data.HotkeyIndex, 0, Hotkeys.Length - 1);
        if (!TryHotkey(want))
        {
            for (int i = 0; i < Hotkeys.Length; i++)
            {
                if (i != want && TryHotkey(i))
                {
                    Balloon($"{Hotkeys[want].Label} is taken, so Pitzki opens with {Hotkeys[i].Label}");
                    break;
                }
            }
        }

        QueryBox.TextChanged += (_, _) => { _confirmKey = null; Hint.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; UpdateResults(); };
        PreviewKeyDown += OnKey;
        // soft key click on every typed or deleted character (not arrows / Enter / Esc)
        QueryBox.PreviewTextInput += (_, e) => { if (e.Text.Length > 0 && !char.IsControl(e.Text[0])) Sfx.Key(); };
        QueryBox.PreviewKeyDown += (_, e) => { if (e.Key is Key.Back or Key.Delete) Sfx.Key(); };
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Sfx).TypeHandle);   // make + load the sounds now, not on first open
        // ignore the brief focus flicker right after opening; it used to hide the window and show it again
        Deactivated += (_, _) => { if (!_exiting && DateTime.UtcNow - _shownAt > TimeSpan.FromMilliseconds(300)) HideLauncher(); };
        _index.Updated += () => Dispatcher.BeginInvoke(() => { if (IsVisible) UpdateResults(); });
        _index.Refresh(true);

        if (!startHidden) ShowLauncher(firstRun);
    }

    // ---------------- show / hide ----------------

    public void ShowLauncher(bool welcome)
    {
        if (DateTime.UtcNow - _shownAt < TimeSpan.FromMilliseconds(250)) return;   // ignore a doubled open request
        if (IsVisible && IsActive) { HideLauncher(); return; }   // pressing the hotkey again closes it
        _shownAt = DateTime.UtcNow;
        _confirmKey = null;
        QueryBox.Text = "";
        Footer.Text = welcome
            ? $"Hi! I'm Pitzki Launcher. From now on {HotkeyLabel()} opens me from anywhere. Type the name of an app, file, setting or a math problem."
            : DefaultFooter;
        Footer.Foreground = welcome ? new SolidColorBrush(Color.FromRgb(0xB9, 0xA6, 0xFF)) : new SolidColorBrush(Color.FromRgb(0x6B, 0x6F, 0x82));
        UpdateResults();
        PositionOnScreen();
        // start fully see-through, so the old frame never flashes before the fade in
        Root.BeginAnimation(OpacityProperty, null);
        Root.Opacity = 0;
        Show();
        Sfx.Open();   // mechanical key "clack" (async, preloaded)
        UpdateLayout();
        Activate();
        Native.SetForegroundWindow(_hwnd);
        QueryBox.Focus();
        Keyboard.Focus(QueryBox);
        Animate();

        var now = DateTime.UtcNow;
        bool files = now - _index.FilesAt > TimeSpan.FromMinutes(15);
        if (files || now - _index.AppsAt > TimeSpan.FromMinutes(3)) _index.Refresh(files);
    }

    private DateTime _shownAt;

    private void HideLauncher()
    {
        if (IsVisible)
        {
            // Paint one see-through frame before hiding, so the next Show() can't flash the old picture.
            Root.BeginAnimation(OpacityProperty, null);
            Root.Opacity = 0;
            Slide.BeginAnimation(TranslateTransform.YProperty, null);
            Slide.Y = -10;
            var shownAt = _shownAt;
            Dispatcher.BeginInvoke(() => { if (_shownAt == shownAt && IsVisible) Hide(); },
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        TrimSoon();
    }

    // While Pitzki waits hidden in the tray it gives its spare memory back to Windows.
    private System.Windows.Threading.DispatcherTimer? _trim;
    [System.Runtime.InteropServices.DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr process);
    private void TrimSoon()
    {
        _trim ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(3), System.Windows.Threading.DispatcherPriority.Background, (_, _) =>
        {
            _trim!.Interval = TimeSpan.FromMinutes(2);
            if (IsVisible) return;
            GC.Collect(); GC.WaitForPendingFinalizers();
            try { EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle); } catch { }
        }, Dispatcher);
        // give memory back only after it sat hidden a while, so opening it right away stays instant
        _trim.Interval = TimeSpan.FromMinutes(2);
        _trim.Stop(); _trim.Start();
    }

    private string HotkeyLabel() => _activeHotkey >= 0 ? Hotkeys[_activeHotkey].Label : "clicking the tray icon";

    private void PositionOnScreen()
    {
        var scr = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double left = scr.Left / dpi.DpiScaleX, top = scr.Top / dpi.DpiScaleY;
        double w = scr.Width / dpi.DpiScaleX, h = scr.Height / dpi.DpiScaleY;
        Left = left + (w - Width) / 2;
        Top = top + h * 0.2;
    }

    private void Animate()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)));
        Slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        Jump();
    }

    private void Jump()
    {
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(-9, KeyTime.FromPercent(0.4), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new BounceEase { Bounces = 1, Bounciness = 3, EasingMode = EasingMode.EaseOut }));
        PitzkiJump.BeginAnimation(TranslateTransform.YProperty, a);
    }

    // ---------------- search ----------------

    private void UpdateResults()
    {
        var raw = QueryBox.Text;
        var q = raw.Trim().ToLowerInvariant();
        var scored = new List<(Item It, int Score, string? Sub)>();

        if (q.Length == 0)
        {
            // recently used
            var byKey = _index.ByKey;
            foreach (var (key, u) in _data.Usage.OrderByDescending(p => p.Value.Last))
            {
                if (scored.Count >= 6) break;
                if (byKey.TryGetValue(key, out var it)) scored.Add((it, 1, "Recently used"));
            }
        }
        else
        {
            // calculator
            if (Calculator.TryEval(raw, out _, out var calcText))
            {
                scored.Add((new Item { Key = "calc", Title = "= " + calcText, Sub = "Enter to copy the result", Kind = Kind.Calc, Target = calcText, Glyph = "\uE8EF" }, 100000, null));
            }

            // special prefixes
            if (q.StartsWith("yt ") && q.Length > 3)
                scored.Add((WebItem("Search YouTube: " + raw.Trim()[3..], "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(raw.Trim()[3..])), 90000, null));
            else if (q.StartsWith("g ") && q.Length > 2)
                scored.Add((WebItem("Search Google: " + raw.Trim()[2..], Google(raw.Trim()[2..])), 90000, null));

            // looks like a web address
            if (LooksLikeUrl(q))
                scored.Add((WebItem("Open " + raw.Trim(), q.StartsWith("http") ? raw.Trim() : "https://" + raw.Trim()), 1200, null));

            var qWords = Search.SplitWords(q);
            var alt = Search.FixLayout(q);
            var altWords = alt != null ? Search.SplitWords(alt) : null;

            void Consider(Item it, bool fuzzy)
            {
                int s = Search.Score(it, q, qWords, fuzzy);
                if (alt != null) s = Math.Max(s, Search.Score(it, alt, altWords!, fuzzy) - 30);
                if (s <= 0) return;
                scored.Add((it, s + it.KindBoost + _data.Boost(it.Key), null));
            }

            foreach (var it in _index.Builtins) Consider(it, true);
            foreach (var it in _index.Apps) Consider(it, true);
            if (q.Length >= 2) foreach (var it in _index.Files) Consider(it, false);

            scored = scored.OrderByDescending(x => x.Score).ThenBy(x => x.It.Title.Length).Take(MaxResults - 1).ToList();
            // always offer a web search at the end
            if (!q.StartsWith("g ") && !q.StartsWith("yt "))
                scored.Add((WebItem("Search Google: " + raw.Trim(), Google(raw.Trim())), 0, null));
        }

        var vms = scored.Take(MaxResults).Select(x => new ResultVm { It = x.It, SubOverride = x.Sub, Icon = IconCache.Get(x.It) }).ToList();
        Results.ItemsSource = vms;
        Results.SelectedIndex = vms.Count > 0 ? 0 : -1;
        Divider.Visibility = vms.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static Item WebItem(string title, string url) =>
        new() { Key = "web", Title = title, Sub = url.Length > 80 ? url[..80] + "…" : url, Kind = Kind.Web, Target = url, Glyph = "\uE774" };

    private static string Google(string q) => "https://www.google.com/search?q=" + Uri.EscapeDataString(q);

    private static bool LooksLikeUrl(string q)
    {
        if (q.Contains(' ')) return false;
        if (q.StartsWith("http://") || q.StartsWith("https://")) return true;
        int dot = q.LastIndexOf('.');
        if (dot <= 0 || dot >= q.Length - 2) return false;
        var tld = q[(dot + 1)..].Split('/')[0];
        return tld.Length is >= 2 and <= 6 && tld.All(char.IsLetter) && !File.Exists(q)
               && tld is not ("exe" or "txt" or "png" or "jpg" or "pdf" or "zip" or "mp4" or "mp3" or "docx" or "lnk");
    }

    // ---------------- keys & launching ----------------

    private void OnKey(object sender, KeyEventArgs e)
    {
        int n = Results.Items.Count;
        switch (e.Key)
        {
            case Key.Escape:
                if (QueryBox.Text.Length > 0) QueryBox.Text = "";
                else HideLauncher();
                e.Handled = true;
                break;
            case Key.Down:
                if (n > 0) { Results.SelectedIndex = (Results.SelectedIndex + 1) % n; Results.ScrollIntoView(Results.SelectedItem); }
                e.Handled = true;
                break;
            case Key.Up:
                if (n > 0) { Results.SelectedIndex = (Results.SelectedIndex - 1 + n) % n; Results.ScrollIntoView(Results.SelectedItem); }
                e.Handled = true;
                break;
            case Key.Enter:
                if (Results.SelectedItem is ResultVm vm)
                {
                    var mods = Keyboard.Modifiers;
                    bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
                    Launch(vm.It, folder: ctrl && !shift, admin: ctrl && shift);
                }
                e.Handled = true;
                break;
        }
    }

    private void Results_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement fe && fe.DataContext is ResultVm vm)
        {
            Results.SelectedItem = vm;
            Launch(vm.It, folder: false, admin: false);
        }
    }

    private void Launch(Item it, bool folder, bool admin)
    {
        try
        {
            if (it.Kind == Kind.Calc)
            {
                Clipboard.SetText(it.Target);
                HideLauncher();
                return;
            }
            if (folder)
            {
                if (it.FilePath == null) { Say("This item has no folder", warn: true); return; }
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{it.FilePath}\"") { UseShellExecute = true });
            }
            else if (it.Kind == Kind.Command)
            {
                if (it.Confirm && _confirmKey != it.Key)
                {
                    _confirmKey = it.Key;
                    Say($"Sure? Press Enter again to: {it.Title}", warn: true);
                    return;
                }
                HideLauncher();
                it.Run?.Invoke();
            }
            else if (admin && it.FilePath != null)
            {
                Process.Start(new ProcessStartInfo(it.FilePath) { UseShellExecute = true, Verb = "runas" });
            }
            else if (it.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", it.Target) { UseShellExecute = true });
            }
            else
            {
                Process.Start(new ProcessStartInfo(it.Target) { UseShellExecute = true });
            }

            Sfx.Launch();   // keeps playing after the window hides
            if (it.Kind != Kind.Web) _data.Used(it.Key);
            HideLauncher();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // user pressed "No" on the admin prompt
        }
        catch
        {
            Say("Couldn't open that 😕", warn: true);
        }
    }

    private void Say(string text, bool warn)
    {
        Footer.Text = text;
        Footer.Foreground = warn ? new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D)) : new SolidColorBrush(Color.FromRgb(0x6B, 0x6F, 0x82));
        if (warn) Jump();
    }

    // ---------------- hotkey ----------------

    private bool TryHotkey(int idx)
    {
        if (_activeHotkey >= 0) Native.UnregisterHotKey(_hwnd, HotkeyId);
        var (_, mods, vk) = Hotkeys[idx];
        if (Native.RegisterHotKey(_hwnd, HotkeyId, mods | Native.MOD_NOREPEAT, vk))
        {
            _activeHotkey = idx;
            UpdateHotkeyMenu();
            return true;
        }
        // put the old one back
        if (_activeHotkey >= 0)
        {
            var (_, m2, v2) = Hotkeys[_activeHotkey];
            if (!Native.RegisterHotKey(_hwnd, HotkeyId, m2 | Native.MOD_NOREPEAT, v2)) _activeHotkey = -1;
        }
        UpdateHotkeyMenu();
        return false;
    }

    private void UpdateHotkeyMenu()
    {
        if (_hotkeyMenu == null) return;
        for (int i = 0; i < _hotkeyMenu.DropDownItems.Count; i++)
            ((Forms.ToolStripMenuItem)_hotkeyMenu.DropDownItems[i]).Checked = i == _activeHotkey;
        _tray.Text = _activeHotkey >= 0 ? $"Pitzki Launcher ({Hotkeys[_activeHotkey].Label})" : "Pitzki Launcher";
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ShowLauncher(false);
            handled = true;
        }
        return IntPtr.Zero;
    }

    // ---------------- misc ----------------

    private void LoadPitzki()
    {
        // pitzki.png is the app icon (window); pitzki-face.png is the Pitzki character beside the search box.
        static BitmapImage? Png(string name)
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, name);
                if (!File.Exists(path)) return null;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 88;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }
        var icon = Png("pitzki.png");
        if (icon != null) Icon = icon;
        PitzkiImg.Source = Png("pitzki-face.png") ?? icon;
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var ico = Path.Combine(AppContext.BaseDirectory, "pitzki.ico");
            if (File.Exists(ico)) return new System.Drawing.Icon(ico);
            var exe = Environment.ProcessPath;
            if (exe != null && System.Drawing.Icon.ExtractAssociatedIcon(exe) is { } i) return i;
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    private void Balloon(string text)
    {
        try { _tray.ShowBalloonTip(4000, "Pitzki Launcher", text, Forms.ToolTipIcon.Info); } catch { }
    }

    private static bool IsInStartup()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(AppName) != null;
    }

    private static void SetStartup(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue(AppName, $"\"{Environment.ProcessPath}\" --tray");
            else k.DeleteValue(AppName, false);
        }
        catch { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting) { e.Cancel = true; HideLauncher(); return; }
        base.OnClosing(e);
    }

    private void ExitApp()
    {
        _exiting = true;
        Native.UnregisterHotKey(_hwnd, HotkeyId);
        _tray.Visible = false;
        _tray.Dispose();
        Close();
        Application.Current.Shutdown();
    }
}
