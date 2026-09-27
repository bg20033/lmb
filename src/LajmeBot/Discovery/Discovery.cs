using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using LajmeBot.Http;
using LajmeBot.Text;

namespace LajmeBot.Discovery;

public sealed record FeedItem(string Source, string Title, string Url, string Summary, DateTimeOffset? Published)
{
    public HashSet<string> Tokens { get; } = TextUtil.Tokens(Title);
}

public sealed record SourceText(string Source, string Title, string Url, string Text);

public sealed partial class FeedReader
{
    private readonly IFetcher _fetcher;
    private readonly Log _log;
    public FeedReader(IFetcher fetcher, Log log) { _fetcher = fetcher; _log = log; }

    [GeneratedRegex(@"<link\b[^>]*type=[""']application/(rss|atom)\+xml[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex AlternateLink();
    [GeneratedRegex(@"href=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    public async Task<List<FeedItem>> ReadSourceAsync(SourceConfig src, CancellationToken ct)
    {
        foreach (var url in src.FeedUrls)
        {
            var items = await TryFeedAsync(src, url, ct);
            if (items.Count > 0) return items;
        }
        // Fallback: auto-discover <link rel="alternate" type="application/rss+xml"> on the home page.
        if (!string.IsNullOrWhiteSpace(src.HomeUrl))
        {
            var home = await _fetcher.GetAsync(src.HomeUrl, ct);
            if (home.Ok)
            {
                foreach (Match m in AlternateLink().Matches(home.Body))
                {
                    var href = Href().Match(m.Value);
                    if (!href.Success) continue;
                    var abs = new Uri(new Uri(home.FinalUrl), System.Net.WebUtility.HtmlDecode(href.Groups[1].Value)).ToString();
                    if (src.FeedUrls.Contains(abs)) continue;
                    var items = await TryFeedAsync(src, abs, ct);
                    if (items.Count > 0) { _log.Info($"{src.Name}: feed auto-discovered at {abs} — add it to FeedUrls"); return items; }
                }
            }
        }
        _log.Warn($"{src.Name}: no readable feed");
        return new();
    }

    private async Task<List<FeedItem>> TryFeedAsync(SourceConfig src, string url, CancellationToken ct)
    {
        var res = await _fetcher.GetAsync(url, ct);
        if (!res.Ok) { _log.Debug($"{src.Name}: {url} -> {res.Error}"); return new(); }
        try
        {
            var items = Parse(src.Name, res.Body, res.FinalUrl).Take(src.MaxItems).ToList();
            _log.Info($"{src.Name}: {items.Count} items from {url}");
            return items;
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException)
        {
            _log.Debug($"{src.Name}: {url} is not a feed ({ex.Message})");
            return new();
        }
    }

    public static IEnumerable<FeedItem> Parse(string source, string xml, string baseUrl)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml.TrimStart('﻿', ' ', '\n', '\r', '\t')), settings);
        var doc = XDocument.Load(reader);
        var root = doc.Root ?? throw new InvalidOperationException("empty");
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace content = "http://purl.org/rss/1.0/modules/content/";

        if (root.Name.LocalName == "feed")
        {
            foreach (var e in root.Elements(atom + "entry"))
            {
                var link = e.Elements(atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value;
                var item = Make(source, e.Element(atom + "title")?.Value, link, e.Element(atom + "summary")?.Value ?? e.Element(atom + "content")?.Value,
                    e.Element(atom + "published")?.Value ?? e.Element(atom + "updated")?.Value, baseUrl);
                if (item != null) yield return item;
            }
            yield break;
        }

        var channel = root.Name.LocalName == "rss" ? root.Element("channel") : root;
        if (channel is null) throw new InvalidOperationException("not rss");
        var found = false;
        foreach (var e in channel.Descendants().Where(x => x.Name.LocalName == "item"))
        {
            found = true;
            var summary = e.Element("description")?.Value ?? e.Element(content + "encoded")?.Value;
            var item = Make(source, e.Element("title")?.Value, e.Element("link")?.Value ?? e.Element("guid")?.Value, summary,
                e.Element("pubDate")?.Value ?? e.Elements().FirstOrDefault(x => x.Name.LocalName == "date")?.Value, baseUrl);
            if (item != null) yield return item;
        }
        if (!found) throw new InvalidOperationException("no items");
    }

    private static FeedItem? Make(string source, string? title, string? link, string? summary, string? date, string baseUrl)
    {
        title = TextUtil.CleanInline(title);
        if (title.Length < 12 || string.IsNullOrWhiteSpace(link)) return null;
        if (!Uri.TryCreate(new Uri(baseUrl), link.Trim(), out var uri)) return null;
        var sum = TextUtil.TruncateWords(TextUtil.CleanInline(summary), 600);
        return new FeedItem(source, title, NormalizeUrl(uri), sum, ParseDate(date));
    }

    /// <summary>Drops the fragment and tracking parameters (utm_*, fbclid, gclid…), keeps real query parameters like ?id=.</summary>
    public static string NormalizeUrl(Uri uri)
    {
        var kept = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !Regex.IsMatch(p, @"^(utm_[a-z]+|fbclid|gclid|mc_[a-z]+|ref|amp)(=|$)", RegexOptions.IgnoreCase))
            .ToList();
        return uri.GetLeftPart(UriPartial.Path) + (kept.Count > 0 ? "?" + string.Join('&', kept) : "");
    }

    public static DateTimeOffset? ParseDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)) return d;
        // RFC 822 with named zones, e.g. "Sat, 27 Sep 2026 18:42:00 CEST"
        var m = Regex.Match(s, @"^(?:\w{3},\s*)?(\d{1,2}\s+\w{3}\s+\d{4}\s+\d{1,2}:\d{2}(?::\d{2})?)\s*([A-Z]{2,5})?$");
        if (m.Success && DateTime.TryParseExact(m.Groups[1].Value, new[] { "d MMM yyyy HH:mm:ss", "d MMM yyyy HH:mm" },
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            var offset = m.Groups[2].Value switch { "CEST" => 2, "CET" => 1, "EST" => -5, "EDT" => -4, _ => 0 };
            return new DateTimeOffset(dt, TimeSpan.FromHours(offset));
        }
        return null;
    }
}

/// <summary>Downloads an article page and pulls out its readable text (for the AI to read facts from).</summary>
public sealed partial class ArticleFetcher
{
    private readonly IFetcher _fetcher;
    private readonly Log _log;
    public ArticleFetcher(IFetcher fetcher, Log log) { _fetcher = fetcher; _log = log; }

    [GeneratedRegex(@"<script[^>]*type=[""']application/ld\+json[""'][^>]*>([\s\S]*?)</script>", RegexOptions.IgnoreCase)]
    private static partial Regex JsonLd();
    [GeneratedRegex(@"<(div|section|article)\b[^>]*class=[""'][^""']*\b(entry-content|article-content|article-body|article__body|post-content|td-post-content|single-content|news-content|content-text|article-text)\b[^""']*[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ContentContainer();
    [GeneratedRegex(@"<p\b[^>]*>([\s\S]*?)</p>", RegexOptions.IgnoreCase)]
    private static partial Regex Paragraph();
    [GeneratedRegex(@"<meta\b[^>]*(?:property|name)=[""'](?:og:description|description)[""'][^>]*content=[""']([^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex MetaDescription();
    [GeneratedRegex(@"(lexo edhe|lexo më shumë|na ndiqni|shpërndaje|share|cookie|abonohu|reklama|advertisement|të ngjashme)", RegexOptions.IgnoreCase)]
    private static partial Regex Boilerplate();

    public async Task<SourceText?> FetchAsync(FeedItem item, CancellationToken ct)
    {
        var res = await _fetcher.GetAsync(item.Url, ct);
        if (!res.Ok)
        {
            _log.Debug($"{item.Source}: {item.Url} -> {res.Error}; using feed summary");
            return item.Summary.Length > 80 ? new SourceText(item.Source, item.Title, item.Url, item.Summary) : null;
        }
        var text = Extract(res.Body);
        if (text.Length < 200 && item.Summary.Length > text.Length) text = item.Summary;
        if (text.Length < 80) return null;
        return new SourceText(item.Source, item.Title, item.Url, TextUtil.TruncateWords(text, 7000));
    }

    public static string Extract(string html)
    {
        // 1) JSON-LD articleBody (most WordPress/Yoast sites publish it)
        foreach (Match m in JsonLd().Matches(html))
        {
            try
            {
                using var doc = JsonDocument.Parse(m.Groups[1].Value.Trim());
                var body = FindArticleBody(doc.RootElement);
                if (body is { Length: > 300 }) return TextUtil.HtmlToText(body);
            }
            catch (JsonException) { }
        }
        // 2) Known content containers → paragraphs inside them
        html = TextUtil.StripNoise(html);
        var c = ContentContainer().Match(html);
        if (c.Success)
        {
            var slice = html.Substring(c.Index, Math.Min(120_000, html.Length - c.Index));
            var text = Paragraphs(slice);
            if (text.Length > 150) return text;
        }
        // 3) All reasonably long paragraphs on the page
        var all = Paragraphs(html);
        if (all.Length > 200) return all;
        // 4) Meta description
        var meta = MetaDescription().Match(html);
        return meta.Success ? TextUtil.CleanInline(meta.Groups[1].Value) : "";
    }

    private static string Paragraphs(string html)
    {
        var parts = Paragraph().Matches(html)
            .Select(m => TextUtil.CleanInline(m.Groups[1].Value))
            .Where(p => p.Length >= 60 && !(p.Length < 160 && Boilerplate().IsMatch(p)))
            .Distinct()
            .ToList();
        return string.Join("\n\n", parts);
    }

    private static string? FindArticleBody(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty("articleBody", out var b) && b.ValueKind == JsonValueKind.String) return b.GetString();
                foreach (var p in e.EnumerateObject())
                    if (FindArticleBody(p.Value) is { } found) return found;
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray())
                    if (FindArticleBody(x) is { } found) return found;
                break;
        }
        return null;
    }
}
