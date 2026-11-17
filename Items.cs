namespace PitzkiLauncher;

public enum Kind { App, File, Folder, Setting, Command, Calc, Web }

public class Item
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public string Sub { get; init; } = "";
    public Kind Kind { get; init; }
    /// <summary>What to open: a path, a URI, or "shell:AppsFolder\AUMID".</summary>
    public string Target { get; init; } = "";
    /// <summary>A real file/folder on disk (for "open folder" and run as admin).</summary>
    public string? FilePath { get; init; }
    /// <summary>Shell parsing name used to load the icon.</summary>
    public string? IconPath { get; init; }
    /// <summary>Segoe MDL2 glyph for items without a shell icon.</summary>
    public string Glyph { get; init; } = "";
    public string[] Keywords { get; init; } = Array.Empty<string>();
    public bool Confirm { get; init; }
    public Action? Run { get; init; }
    public int KindBoost { get; init; }

    // search helpers (filled by Prep)
    public string Lower = "";
    public string[] Words = Array.Empty<string>();
    public string Initials = "";

    public Item Prep()
    {
        Lower = Title.ToLowerInvariant();
        Words = Search.SplitWords(Lower);
        Initials = string.Concat(Words.Select(w => w[0]));
        for (int i = 0; i < Keywords.Length; i++) Keywords[i] = Keywords[i].ToLowerInvariant();
        return this;
    }
}

public static class Search
{
    public static string[] SplitWords(string s)
    {
        var words = new List<string>();
        int st = -1;
        for (int i = 0; i <= s.Length; i++)
        {
            bool ok = i < s.Length && char.IsLetterOrDigit(s[i]);
            if (ok && st < 0) st = i;
            else if (!ok && st >= 0) { words.Add(s[st..i]); st = -1; }
        }
        return words.ToArray();
    }

    /// <summary>Score how well the (lowercase, trimmed) query matches an item. 0 = no match.</summary>
    public static int Score(Item it, string q, string[] qWords, bool fuzzy)
    {
        int best = Text(it.Lower, it.Words, it.Initials, q, qWords, fuzzy);
        foreach (var k in it.Keywords)
        {
            if (k == q) best = Math.Max(best, 900);
            else if (k.StartsWith(q, StringComparison.Ordinal)) best = Math.Max(best, 760);
            else if (q.Length >= 3 && k.Contains(q, StringComparison.Ordinal)) best = Math.Max(best, 450);
        }
        return best;
    }

    private static int Text(string text, string[] words, string initials, string q, string[] qWords, bool fuzzy)
    {
        if (text == q) return 1000;
        if (text.StartsWith(q, StringComparison.Ordinal)) return 850 - Math.Min(text.Length - q.Length, 50);
        foreach (var w in words)
            if (w.StartsWith(q, StringComparison.Ordinal)) return 700;
        if (q.Length >= 2 && initials.StartsWith(q, StringComparison.Ordinal)) return 650;
        if (qWords.Length > 1 && qWords.All(qw => words.Any(w => w.StartsWith(qw, StringComparison.Ordinal)))) return 620;
        int idx = text.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0) return 500 - Math.Min(idx, 50);
        if (fuzzy && q.Length >= 2)
        {
            // letters in order (e.g. "vsc" -> "visual studio code")
            int ti = 0, gaps = 0, last = -1;
            foreach (char c in q)
            {
                if (c == ' ') continue;
                int f = text.IndexOf(c, ti);
                if (f < 0) return 0;
                if (last >= 0) gaps += f - last - 1;
                last = f; ti = f + 1;
            }
            return Math.Max(100, 300 - gaps * 8);
        }
        return 0;
    }

    // Hebrew keyboard layout -> English keys (when someone types "chrome" in Hebrew by mistake)
    private static readonly Dictionary<char, char> HebToEng = new()
    {
        ['/'] = 'q', ['\''] = 'w', ['ק'] = 'e', ['ר'] = 'r', ['א'] = 't', ['ט'] = 'y', ['ו'] = 'u', ['ן'] = 'i', ['ם'] = 'o', ['פ'] = 'p',
        ['ש'] = 'a', ['ד'] = 's', ['ג'] = 'd', ['כ'] = 'f', ['ע'] = 'g', ['י'] = 'h', ['ח'] = 'j', ['ל'] = 'k', ['ך'] = 'l', ['ף'] = ';',
        ['ז'] = 'z', ['ס'] = 'x', ['ב'] = 'c', ['ה'] = 'v', ['נ'] = 'b', ['מ'] = 'n', ['צ'] = 'm', ['ת'] = ',', ['ץ'] = '.',
    };

    public static string? FixLayout(string q)
    {
        if (!q.Any(c => c >= 'א' && c <= 'ת')) return null;
        return new string(q.Select(c => HebToEng.TryGetValue(c, out var e) ? e : c).ToArray());
    }
}
