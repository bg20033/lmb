using System.Globalization;
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
public sealed record ArticleMetadata(string? Title, string? Description, DateTimeOffset? Published);

/// <summary>
/// Finds new articles by crawling public front/category pages. The crawler never reads RSS/Atom:
/// it follows only same-domain article links, opens a capped number of candidates, and uses the
/// article page itself for the title, date and summary used by the story clusterer.
/// </summary>
public sealed partial class WebCrawler
{
    private readonly IFetcher _fetcher;
    private readonly Log _log;

    public WebCrawler(IFetcher fetcher, Log log) { _fetcher = fetcher; _log = log; }

    [GeneratedRegex(@"<a\b[^>]*\bhref\s*=\s*[""']([^""'#]+)[""'][^>]*>([\s\S]*?)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex Anchor();
    [GeneratedRegex(@"/(?:tag|tags|category|categories|author|page|search|kontakt|contact|rreth-nesh|privacy|privatesia|wp-admin|wp-json|feed)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex NonArticlePath();
    [GeneratedRegex(@"\.(?:jpg|jpeg|png|gif|webp|svg|pdf|zip|mp4|mp3)$", RegexOptions.IgnoreCase)]
    private static partial Regex FileLink();

    public async Task<List<FeedItem>> CrawlSourceAsync(SourceConfig src, CancellationToken ct)
    {
        var listingUrls = new[] { src.HomeUrl }.Concat(src.CategoryUrls)
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var maxFromEachListing = Math.Max(1, (int)Math.Ceiling(Math.Max(1, src.MaxArticlePages) / (double)Math.Max(1, listingUrls.Count)));

        foreach (var listing in listingUrls)
        {
            var page = await _fetcher.GetAsync(listing, ct);
            if (!page.Ok) { _log.Debug($"{src.Name}: listing {listing} -> {page.Error}"); continue; }
            var taken = 0;
            foreach (var (url, title) in ExtractArticleLinks(page.Body, page.FinalUrl))
            {
                if (!candidates.TryGetValue(url, out var old) || title.Length > old.Length) candidates[url] = title;
                if (++taken >= maxFromEachListing) break;
            }
        }

        var picks = candidates.Select(x => (Url: x.Key, Title: x.Value)).Take(Math.Max(1, src.MaxArticlePages)).ToList();
        var articles = await Task.WhenAll(picks.Select(p => ReadArticleAsync(src.Name, p.Url, p.Title, ct)));
        var items = articles.Where(x => x is not null).Select(x => x!).ToList();
        _log.Info($"{src.Name}: {items.Count} articles from {listingUrls.Count} page(s) ({candidates.Count} links found)");
        return items;
    }

    private async Task<FeedItem?> ReadArticleAsync(string source, string url, string linkedTitle, CancellationToken ct)
    {
        var page = await _fetcher.GetAsync(url, ct);
        if (!page.Ok) return null;
        var body = ArticleFetcher.Extract(page.Body);
        if (body.Length < 160) return null;
        var meta = ArticleFetcher.ExtractMetadata(page.Body);
        var title = TextUtil.CleanInline(meta.Title);
        if (title.Length < 12) title = linkedTitle;
        if (title.Length < 12) return null;
        var summary = TextUtil.CleanInline(meta.Description);
        if (summary.Length < 80) summary = TextUtil.TruncateWords(body, 600);
        return new FeedItem(source, title, FeedReader.NormalizeUrl(new Uri(page.FinalUrl)), summary, meta.Published);
    }

    public static IEnumerable<(string Url, string Title)> ExtractArticleLinks(string html, string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var home)) yield break;
        foreach (Match match in Anchor().Matches(html))
        {
            var raw = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
            if (!Uri.TryCreate(home, raw, out var uri) || uri.Scheme is not ("http" or "https")) continue;
            if (!SameHost(home, uri) || uri.AbsolutePath.Length < 10 || NonArticlePath().IsMatch(uri.AbsolutePath) || FileLink().IsMatch(uri.AbsolutePath)) continue;
            var title = TextUtil.CleanInline(match.Groups[2].Value);
            if (title.Length < 18 || title.Length > 240 || TextUtil.Tokens(title).Count < 3) continue;
            yield return (FeedReader.NormalizeUrl(uri), title);
        }
    }

    private static bool SameHost(Uri a, Uri b) => Host(a).Equals(Host(b), StringComparison.OrdinalIgnoreCase);
    private static string Host(Uri u) => u.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? u.Host[4..] : u.Host;
}

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

    /// <param name="maxChars">AI needs a bounded source; licensed republication can request the full extracted text.</param>
    public async Task<SourceText?> FetchAsync(FeedItem item, CancellationToken ct, int maxChars = 7000)
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
        return new SourceText(item.Source, item.Title, item.Url, TextUtil.TruncateWords(text, maxChars));
    }

    public static string Extract(string html)
        => HtmlArticleExtractor.Extract(html);

    /// <summary>Gets canonical news metadata from JSON-LD before falling back to page-level description.</summary>
    public static ArticleMetadata ExtractMetadata(string html)
        => HtmlArticleExtractor.ExtractMetadata(html);

}
