using System.IO;
using System.Text.Json;

namespace PitzkiLauncher;

public class UsageEntry
{
    public int Count { get; set; }
    public DateTime Last { get; set; }
}

/// <summary>
/// PITZKI_LAUNCHER_DEMO=&lt;folder&gt;: for the README clips. Files come only from that folder (no recent files, no desktop),
/// usage is kept in the folder too, it runs next to the real copy, and it never touches "start with Windows".
/// </summary>
public static class Demo
{
    public static readonly string? Folder = Environment.GetEnvironmentVariable("PITZKI_LAUNCHER_DEMO");
    public static bool On => !string.IsNullOrEmpty(Folder);
}

public class LauncherData
{
    public int HotkeyIndex { get; set; }
    public bool FirstRunDone { get; set; }
    public Dictionary<string, UsageEntry> Usage { get; set; } = new();

    private static string Dir => Demo.On ? Path.Combine(Demo.Folder!, ".launcher-data")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PitzkiLauncher");
    private static string FilePath => Path.Combine(Dir, "data.json");

    public static LauncherData Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<LauncherData>(File.ReadAllText(FilePath)) ?? new LauncherData();
        }
        catch { }
        return new LauncherData();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch { }
    }

    public void Used(string key)
    {
        if (!Usage.TryGetValue(key, out var u)) Usage[key] = u = new UsageEntry();
        u.Count++;
        u.Last = DateTime.UtcNow;
        if (Usage.Count > 400)
        {
            foreach (var old in Usage.OrderBy(p => p.Value.Last).Take(Usage.Count - 400).Select(p => p.Key).ToList())
                Usage.Remove(old);
        }
        Save();
    }

    public int Boost(string key)
    {
        if (!Usage.TryGetValue(key, out var u)) return 0;
        int b = Math.Min(u.Count * 25, 250);
        var age = DateTime.UtcNow - u.Last;
        if (age < TimeSpan.FromHours(2)) b += 80;
        else if (age < TimeSpan.FromDays(1)) b += 50;
        else if (age < TimeSpan.FromDays(7)) b += 20;
        return b;
    }
}
