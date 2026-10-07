using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LajmeBot.Text;

/// <summary>
/// Keyword categorizer for news items (title + first 60 words), driven by config/categories.json.
/// The same rules file is used by the Lajme për Kosovë site (src/data/kategorite.json).
/// </summary>
public static class Categorizer
{
    private sealed record Rules(List<string> Order, string Fallback, Dictionary<string, List<Regex>> Patterns);

    private static Rules? _rules;
    private static bool _loaded;

    public static string? Categorize(string title, string body, string rulesPath = "config/categories.json")
    {
        var rules = Load(rulesPath);
        if (rules == null) return null;
        var t = Norm(title);
        var b = Norm(string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(60)));
        var score = rules.Patterns.ToDictionary(kv => kv.Key, kv => 4 * Count(t, kv.Value) + Math.Min(3, Count(b, kv.Value)));
        var best = score.Values.DefaultIfEmpty(0).Max();
        if (best == 0) return rules.Fallback;
        return rules.Order.FirstOrDefault(c => score.TryGetValue(c, out var s) && s == best) ?? rules.Fallback;
    }

    /// <summary>Maps a categorizer result to a category the site actually has.</summary>
    public static string Resolve(string? category, IReadOnlyCollection<string> siteCategories)
    {
        if (category != null && siteCategories.Contains(category)) return category;
        var alt = category switch { "rajoni" => "bota", "magazine" => "kulture", "kosova" => "politike", _ => null };
        if (alt != null && siteCategories.Contains(alt)) return alt;
        return siteCategories.Contains("politike") ? "politike" : siteCategories.First();
    }

    private static int Count(string text, List<Regex> list) => list.Sum(re => re.Matches(text).Count);

    private static Rules? Load(string path)
    {
        if (_loaded) return _rules;
        _loaded = true;
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var order = root.GetProperty("order").EnumerateArray().Select(e => e.GetString()!).ToList();
        var fallback = root.GetProperty("fallback").GetString()!;
        var patterns = new Dictionary<string, List<Regex>>();
        foreach (var cat in root.GetProperty("keywords").EnumerateObject())
        {
            patterns[cat.Name] = cat.Value.EnumerateArray()
                .Select(e => e.GetString() ?? "")
                .Select(w => (w, k: Norm(w).Trim()))
                .Where(x => x.k.Length > 0)
                .Select(x => new Regex("(?<![a-z0-9])" + Regex.Escape(x.k) + (x.w.EndsWith(' ') ? "(?![a-z0-9])" : ""), RegexOptions.CultureInvariant))
                .ToList();
        }
        return _rules = new Rules(order, fallback, patterns);
    }

    public static string Norm(string s)
    {
        s = s.ToLowerInvariant().Replace('ë', 'e').Replace('ç', 'c').Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(s.Length + 2).Append(' ');
        foreach (var ch in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' || char.IsWhiteSpace(ch) ? ch : ' ');
        }
        return sb.Append(' ').ToString();
    }
}
