using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LajmeBot.Text;

public static partial class TextUtil
{
    [GeneratedRegex(@"<(script|style|noscript|svg|iframe|form|nav|header|footer|aside|figure)\b[^>]*>[\s\S]*?</\1\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseBlocks();
    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex Comments();
    [GeneratedRegex(@"<br\s*/?>|</(p|div|li|h[1-6]|blockquote|tr)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnds();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();
    [GeneratedRegex(@"[ \t ]+")]
    private static partial Regex Spaces();
    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();
    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonWord();

    /// <summary>Converts an HTML fragment to readable plain text with paragraph breaks.</summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = Comments().Replace(html, " ");
        s = NoiseBlocks().Replace(s, " ");
        s = BlockEnds().Replace(s, "\n");
        s = Tags().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = Spaces().Replace(s, " ");
        s = string.Join('\n', s.Split('\n').Select(l => l.Trim()));
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    public static string CleanInline(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(s, " ")), " ").Replace('\n', ' ').Trim();

    /// <summary>Lower-case, strips diacritics (ë→e, ç→c).</summary>
    public static string Fold(string s)
    {
        var norm = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(norm.Length);
        foreach (var ch in norm)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "dhe","e","te","ne","me","per","nga","se","qe","i","u","si","ka","kane","do","nje","ky","kjo","keto","ato",
        "por","edhe","pas","para","mbi","nen","deri","tek","prej","sot","dje","eshte","jane","ishte","kishte","po",
        "nuk","mos","ma","mu","ai","ajo","ata","tij","saj","tyre","cili","cila","cfare","pse","kur","ku","sa","vetem",
        "ose","apo","gjate","lidhje","thote","tha","deklaron","deklaroi","video","foto","live","ekskluzive","lajm",
        "the","a","an","of","to","in","on","and","for","is"
    };

    /// <summary>Distinctive word tokens of a headline, used for clustering and duplicate checks.</summary>
    public static HashSet<string> Tokens(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in NonWord().Split(Fold(s)))
        {
            if (w.Length < 3 || Stop.Contains(w)) continue;
            set.Add(Stem(w));
        }
        return set;
    }

    private static readonly string[] Suffixes =
        { "eve", "ave", "ite", "ise", "ine", "ese", "ene", "ja", "je", "it", "in", "is", "es", "en", "et", "ve", "a", "e", "i", "u" };

    /// <summary>
    /// Crude Albanian stemmer: strips the most common case/definiteness endings and keeps 5 letters,
    /// so "paga", "pagën", "pagës" or "rritet", "rrit" collapse to the same token.
    /// </summary>
    public static string Stem(string w)
    {
        w = Fold(w);
        if (w.Length >= 4 && !char.IsDigit(w[0]))
            foreach (var suf in Suffixes)
                if (w.EndsWith(suf, StringComparison.Ordinal) && w.Length - suf.Length >= 3) { w = w[..^suf.Length]; break; }
        return w.Length > 5 ? w[..5] : w;
    }

    public static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var inter = a.Count(b.Contains);
        return inter / (double)(a.Count + b.Count - inter);
    }

    /// <summary>URL-safe slug with Albanian letters transliterated.</summary>
    public static string Slugify(string title, int maxLength = 80)
    {
        var s = Fold(title);
        s = NonWord().Replace(s, "-").Trim('-');
        s = Regex.Replace(s, "-{2,}", "-");
        if (s.Length > maxLength)
        {
            s = s[..maxLength];
            var cut = s.LastIndexOf('-');
            if (cut > 30) s = s[..cut];
        }
        return s.Trim('-');
    }

    public static int WordCount(string s) =>
        s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetterOrDigit));

    /// <summary>Cuts text at a word boundary so it is at most <paramref name="max"/> characters.</summary>
    public static string TruncateWords(string s, int max)
    {
        s = s.Trim();
        if (s.Length <= max) return s;
        var cut = s[..max];
        var sp = cut.LastIndexOf(' ');
        if (sp > max / 2) cut = cut[..sp];
        return cut.TrimEnd(',', ';', ':', ' ', '-', '—') + "…";
    }

    /// <summary>
    /// Longest run of consecutive words shared by <paramref name="text"/> and any source.
    /// Used as a guard against the AI copying sentences from the source articles.
    /// </summary>
    public static int LongestSharedRun(string text, IEnumerable<string> sources, int minRun = 8)
    {
        static string[] Words(string t) => NonWord().Split(Fold(t)).Where(w => w.Length > 0).ToArray();
        var w = Words(text);
        var best = 0;
        foreach (var src in sources)
        {
            var sw = Words(src);
            if (sw.Length < minRun || w.Length < minRun) continue;
            var shingles = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i + minRun <= sw.Length; i++) shingles.Add(string.Join(' ', sw, i, minRun));
            var run = 0;
            for (var i = 0; i + minRun <= w.Length; i++)
            {
                if (shingles.Contains(string.Join(' ', w, i, minRun))) { run = run == 0 ? minRun : run + 1; best = Math.Max(best, run); }
                else run = 0;
            }
        }
        return best;
    }

    public static ulong StableHash(string s)
    {
        // FNV-1a 64 — string.GetHashCode() is randomised per process, this is not.
        var h = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 1099511628211UL; }
        return h;
    }

    public static string YamlString(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ") + "\"";
}
