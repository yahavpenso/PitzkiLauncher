using System.IO;
using System.Text.Json;

namespace PitzkiLauncher;

public class UsageEntry
{
    public int Count { get; set; }
    public DateTime Last { get; set; }
}

public class LauncherData
{
    public int HotkeyIndex { get; set; }
    public bool FirstRunDone { get; set; }
    public Dictionary<string, UsageEntry> Usage { get; set; } = new();

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PitzkiLauncher");
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
