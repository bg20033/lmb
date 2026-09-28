using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using LajmeBot.Text;

namespace LajmeBot.Discovery;

/// <summary>
/// Dependency-free readable-article extractor. It deliberately does not try to be a browser:
/// instead it scores semantic/article-shaped HTML containers and keeps only substantial prose.
/// This makes it resilient to the different WordPress, React and custom layouts used by outlets.
/// </summary>
internal static partial class HtmlArticleExtractor
{
    [GeneratedRegex(@"<script\b[^>]*type\s*=\s*[""']application/ld\+json[^>]*>([\s\S]*?)</script\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex JsonLd();
    [GeneratedRegex(@"<meta\b(?=[^>]*(?:property|name|itemprop)\s*=\s*[""'](?<key>[^""']+)[""'])[^>]*content\s*=\s*[""'](?<value>[^""']*)[""'][^>]*>|<meta\b(?=[^>]*content\s*=\s*[""'](?<value2>[^""']*)[""'])[^>]*(?:property|name|itemprop)\s*=\s*[""'](?<key2>[^""']+)[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Meta();
    [GeneratedRegex(@"<title\b[^>]*>([\s\S]*?)</title\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleTag();
    [GeneratedRegex(@"<time\b[^>]*datetime\s*=\s*[""']([^""']+)[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex TimeTag();
    [GeneratedRegex(@"<(p|blockquote|li)\b[^>]*>([\s\S]*?)</\1\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex TextBlock();
    [GeneratedRegex(@"<(?:div|section|aside|ul|ol|footer)\b(?<attrs>[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex MaybeUnwantedBlock();
    [GeneratedRegex(@"(?:^|[^a-z0-9])(related|recommended|popular|share|social|advert|ads?|reklam|cookie|consent|newsletter|subscribe|abon|comment|sidebar|widget|footer|breadcrumb|author[-_]box|read[-_]more|latest|trending)(?:$|[^a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Negative();
    [GeneratedRegex(@"(?:^|[^a-z0-9])(article|post|entry|story|single|content|body|news|text|main)(?:$|[^a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex Positive();
    [GeneratedRegex(@"(?:^|[^a-z0-9])(related|share|social|advert|reklam|cookie|newsletter|comment|sidebar|widget|footer|breadcrumb|menu|nav)(?:$|[^a-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex BadContainer();

    private sealed record Element(string Tag, string Attributes, int ContentStart, int End);
    private sealed record OpenElement(string Tag, string Attributes, int ContentStart);

    public static string Extract(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        // Structured data is generally the cleanest rendition and is not affected by page chrome.
        var structured = FindArticleBody(html);
        if (TextUtil.WordCount(structured) >= 45) return structured;

        var cleaned = RemoveUnwantedBlocks(TextUtil.StripNoise(html));
        var candidates = ParseElements(cleaned)
            .Where(e => e.End > e.ContentStart && IsCandidate(e))
            .Select(e => (element: e, text: ParagraphText(cleaned[e.ContentStart..e.End])))
            .Where(x => x.text.Length >= 180)
            .Select(x => (x.text, score: Score(x.element, x.text, cleaned[x.element.ContentStart..x.element.End])))
            .OrderByDescending(x => x.score)
            .ToList();

        if (candidates.Count > 0 && TextUtil.WordCount(candidates[0].text) >= 45)
            return candidates[0].text;

        // Some compact templates have no article wrapper. Use page paragraphs as the last prose fallback.
        var all = ParagraphText(cleaned);
        if (TextUtil.WordCount(all) >= 45) return all;
        return FindMeta(html, "description", "og:description") ?? "";
    }

    public static ArticleMetadata ExtractMetadata(string html)
    {
        var structured = FindArticleMetadata(html);
        var title = structured?.Title ?? FindMeta(html, "og:title", "twitter:title", "headline") ?? TitleTag().Match(html).Groups[1].Value;
        var description = structured?.Description ?? FindMeta(html, "og:description", "description", "twitter:description");
        var date = structured?.Published
            ?? FeedReader.ParseDate(FindMeta(html, "article:published_time", "datepublished", "date", "publishdate", "datecreated"))
            ?? FeedReader.ParseDate(TimeTag().Match(html).Groups[1].Value);
        return new ArticleMetadata(TextUtil.CleanInline(title), TextUtil.CleanInline(description), date);
    }

    private static bool IsCandidate(Element e) =>
        e.Tag is "article" or "main" || e.Attributes.Contains("role=\"main\"", StringComparison.OrdinalIgnoreCase) || e.Attributes.Contains("role='main'", StringComparison.OrdinalIgnoreCase) ||
        (e.Tag is "div" or "section" && Positive().IsMatch(e.Attributes) && !BadContainer().IsMatch(e.Attributes));

    private static double Score(Element e, string text, string raw)
    {
        var words = TextUtil.WordCount(text);
        var paragraphs = TextBlock().Matches(raw).Count;
        var links = Regex.Matches(raw, @"<a\b", RegexOptions.IgnoreCase).Count;
        var linkText = Regex.Matches(raw, @"<a\b[^>]*>([\s\S]*?)</a\s*>", RegexOptions.IgnoreCase)
            .Cast<Match>().Sum(m => TextUtil.WordCount(m.Groups[1].Value));
        var score = words + paragraphs * 28 - linkText * 1.6 - links * 2;
        if (e.Tag == "article") score += 140;
        if (e.Tag == "main") score += 60;
        if (Positive().IsMatch(e.Attributes)) score += 90;
        if (BadContainer().IsMatch(e.Attributes)) score -= 500;
        return score;
    }

    private static string ParagraphText(string html)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<string>();
        foreach (Match match in TextBlock().Matches(html))
        {
            var text = TextUtil.CleanInline(match.Groups[2].Value);
            if (text.Length < 45 || IsBoilerplate(text) || !seen.Add(TextUtil.Fold(text))) continue;
            parts.Add(text);
        }
        return string.Join("\n\n", parts);
    }

    private static bool IsBoilerplate(string text)
    {
        var folded = TextUtil.Fold(text);
        return folded.Contains("te gjitha te drejtat e rezervuara") ||
               folded.Contains("lexo edhe") || folded.Contains("na ndiqni") ||
               folded.Contains("prano cookies") || folded.Contains("shperndaje");
    }

    private static string RemoveUnwantedBlocks(string html)
    {
        // Remove whole widgets, not just their tags. This prevents a "related" card's paragraphs
        // from being scored as article prose by its parent <main>.
        var removals = new List<(int start, int end)>();
        foreach (Match m in MaybeUnwantedBlock().Matches(html))
        {
            if (!Negative().IsMatch(m.Groups["attrs"].Value)) continue;
            var end = FindElementEnd(html, m.Index, TagName(m.Value));
            if (end > m.Index) removals.Add((m.Index, end));
        }
        // Nested widgets are already covered by their parent. Keeping only outermost ranges also
        // keeps offsets valid while the string is edited from right to left.
        var outermost = removals.Where(r => !removals.Any(other => other.start < r.start && other.end >= r.end));
        foreach (var (start, end) in outermost.OrderByDescending(x => x.start))
            html = html.Remove(start, end - start).Insert(start, " ");
        return html;
    }

    private static List<Element> ParseElements(string html)
    {
        var elements = new List<Element>();
        var stack = new Stack<OpenElement>();
        foreach (Match tag in Regex.Matches(html, @"<\s*(/)?\s*([a-zA-Z][\w:-]*)([^>]*)>", RegexOptions.Singleline))
        {
            var closing = tag.Groups[1].Success;
            var name = tag.Groups[2].Value.ToLowerInvariant();
            var tail = tag.Groups[3].Value;
            if (!closing && !tail.TrimEnd().EndsWith('/') && name is not ("br" or "img" or "meta" or "link" or "input" or "source" or "hr"))
            {
                stack.Push(new OpenElement(name, tail, tag.Index + tag.Length));
                continue;
            }
            if (!closing) continue;
            var opened = stack.FirstOrDefault(x => x.Tag == name);
            if (opened == null) continue;
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current.Tag == name)
                {
                    elements.Add(new Element(name, current.Attributes, current.ContentStart, tag.Index));
                    break;
                }
            }
        }
        return elements;
    }

    private static int FindElementEnd(string html, int openingStart, string name)
    {
        var depth = 0;
        foreach (Match tag in Regex.Matches(html[openingStart..], @"<\s*(/)?\s*([a-zA-Z][\w:-]*)([^>]*)>", RegexOptions.Singleline))
        {
            if (!tag.Groups[2].Value.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (tag.Groups[1].Success) { if (--depth == 0) return openingStart + tag.Index + tag.Length; }
            else if (!tag.Groups[3].Value.TrimEnd().EndsWith('/')) depth++;
        }
        return -1;
    }

    private static string TagName(string tag) => Regex.Match(tag, @"<\s*([a-zA-Z][\w:-]*)").Groups[1].Value;

    private static string? FindMeta(string html, params string[] names)
    {
        foreach (Match m in Meta().Matches(html))
        {
            var key = (m.Groups["key"].Success ? m.Groups["key"].Value : m.Groups["key2"].Value).Trim();
            if (!names.Any(n => n.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
            var value = m.Groups["value"].Success ? m.Groups["value"].Value : m.Groups["value2"].Value;
            if (!string.IsNullOrWhiteSpace(value)) return WebUtility.HtmlDecode(value.Trim());
        }
        return null;
    }

    private static string FindArticleBody(string html)
    {
        foreach (var root in JsonRoots(html))
        {
            var best = FindStrings(root, "articleBody").OrderByDescending(TextUtil.WordCount).FirstOrDefault();
            if (best != null) return TextUtil.HtmlToText(best);
        }
        return "";
    }

    private static ArticleMetadata? FindArticleMetadata(string html)
    {
        foreach (var root in JsonRoots(html))
        {
            var found = FindArticle(root);
            if (found != null) return found;
        }
        return null;
    }

    private static IEnumerable<JsonElement> JsonRoots(string html)
    {
        foreach (Match m in JsonLd().Matches(html))
        {
            var json = WebUtility.HtmlDecode(m.Groups[1].Value.Trim().TrimStart('\uFEFF'));
            if (json.StartsWith("<![CDATA[", StringComparison.Ordinal)) json = json[9..].TrimEnd(']', '>').Trim();
            JsonDocument? doc = null;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { /* A broken schema must not stop HTML extraction. */ }
            if (doc == null) continue;
            using (doc) yield return doc.RootElement.Clone();
        }
    }

    private static IEnumerable<string> FindStrings(JsonElement value, string property)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty(property, out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { } s) yield return s;
            foreach (var child in value.EnumerateObject()) foreach (var found in FindStrings(child.Value, property)) yield return found;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) foreach (var found in FindStrings(child, property)) yield return found;
    }

    private static ArticleMetadata? FindArticle(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in value.EnumerateArray()) if (FindArticle(child) is { } result) return result;
            return null;
        }
        if (value.ValueKind != JsonValueKind.Object) return null;
        if (HasArticleType(value))
        {
            var title = StringValue(value, "headline") ?? StringValue(value, "name");
            if (!string.IsNullOrWhiteSpace(title))
                return new ArticleMetadata(title, StringValue(value, "description"), FeedReader.ParseDate(StringValue(value, "datePublished") ?? StringValue(value, "dateModified")));
        }
        foreach (var child in value.EnumerateObject()) if (FindArticle(child.Value) is { } result) return result;
        return null;
    }

    private static string? StringValue(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool HasArticleType(JsonElement item)
    {
        if (!item.TryGetProperty("@type", out var type)) return false;
        var types = type.ValueKind == JsonValueKind.String ? new[] { type.GetString() } :
            type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()) : Enumerable.Empty<string?>();
        return types.Any(t => t?.Contains("Article", StringComparison.OrdinalIgnoreCase) == true || t?.Contains("News", StringComparison.OrdinalIgnoreCase) == true);
    }
}
