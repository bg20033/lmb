using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LajmeBot.Ai;
using LajmeBot.Discovery;
using LajmeBot.Text;

namespace LajmeBot.Publishing;

/// <summary>
/// Publisher for Lajme për Kosovë. Its React newsroom reads articles from a JSON data file,
/// rather than Astro content collections, so this adapter writes that file directly.
/// </summary>
public sealed class LajmePerKosovePublisher : INewsPublisher
{
    private readonly SiteConfig _site;
    private readonly Log _log;
    private string DataFile => Path.Combine(_site.Root, "src", "data", "importedArticles.json");
    public bool Exists => Directory.Exists(Path.Combine(_site.Root, "src", "data"));

    public LajmePerKosovePublisher(SiteConfig site, Log log) { _site = site; _log = log; }

    public List<string> RecentTitles(int max = 20) => Read()
        .Select(n => n?["title"]?.GetValue<string>() ?? "")
        .Where(x => x.Length > 0).TakeLast(max).Reverse().ToList();

    // The React newsroom already owns its Redaksia author record.
    public void EnsureAuthor(bool licensedCopy = false) { }

    public List<string> Validate(ArticleDraft draft, IReadOnlyList<SourceText> sources, bool licensedCopy = false)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(draft.Title)) problems.Add("Title is empty.");
        if (TextUtil.WordCount(draft.BodyMarkdown) == 0) problems.Add("Article body is empty.");
        if (!licensedCopy && draft.BodyMarkdown.Length < 300) problems.Add("Article body is too short.");
        return problems;
    }

    public PublishedArticle Write(ArticleDraft draft, IReadOnlyList<SourceText> sources, bool featured, DateTimeOffset now, bool licensedCopy = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
        var rows = Read();
        var slug = UniqueSlug(TextUtil.Slugify(draft.Title), rows);
        var source = sources.FirstOrDefault();
        var local = SitePublisher.ToKosovoTime(now).ToUniversalTime().ToString("O");
        var textBlocks = draft.BodyMarkdown.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => (JsonNode)new JsonObject { ["type"] = "paragraph", ["text"] = p.Trim() }).ToList();
        if (source != null)
            textBlocks.Add(new JsonObject { ["type"] = "paragraph", ["text"] = $"Burimi origjinal: {source.Source} — {source.Url}" });

        rows.Add(new JsonObject
        {
            ["id"] = "imported-" + slug,
            ["title"] = draft.Title,
            ["subtitle"] = draft.Description,
            ["slug"] = slug,
            ["excerpt"] = draft.Description,
            ["body"] = new JsonArray(textBlocks.ToArray()),
            ["categoryId"] = CategoryId(draft.Category),
            ["authorId"] = "au-redaksia",
            ["status"] = "published",
            // Category illustration in the site's public/images/kategori (no random stock photos).
            ["image"] = $"/images/kategori/{CategoryId(draft.Category)["cat-".Length..]}-{Illustration("imported-" + slug)}.jpg",
            ["imageAlt"] = "Ilustrim për " + draft.Title,
            ["imageCaption"] = "Ilustrim",
            ["imageCredit"] = "",
            ["tags"] = new JsonArray(draft.Tags.Select(tag => (JsonNode?)JsonValue.Create(tag)).ToArray()),
            ["publishedAt"] = local,
            ["scheduledFor"] = null,
            ["updatedAt"] = local,
            ["createdAt"] = local,
            ["readingTime"] = Math.Max(1, (int)Math.Ceiling(TextUtil.WordCount(draft.BodyMarkdown) / 220d)),
            ["views"] = 0,
            ["commentCount"] = 0,
            ["shares"] = 0,
            ["featured"] = featured,
            ["breaking"] = false,
            ["isOpinion"] = false,
            ["hasVideo"] = false,
            ["placement"] = "river",
            ["seo"] = new JsonObject
            {
                ["title"] = draft.Title + " | Lajme për Kosovë",
                ["description"] = draft.Description,
                ["slug"] = slug,
                ["canonicalUrl"] = "https://lajmeperkosove.com/article/" + slug,
                ["focusKeyword"] = draft.Tags.FirstOrDefault() ?? "Lajme",
            },
        });
        File.WriteAllText(DataFile, rows.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        _log.Info($"{_site.Slug}: added React newsroom article /article/{slug}");
        return new PublishedArticle(_site.Slug, slug, draft.Title, draft.Category, DataFile, "");
    }

    public string WritePreview(ArticleDraft draft, IReadOnlyList<SourceText> sources, bool featured, DateTimeOffset now, string previewDir, bool licensedCopy = false)
    {
        Directory.CreateDirectory(previewDir);
        var slug = TextUtil.Slugify(draft.Title);
        var path = Path.Combine(previewDir, _site.Slug + "--" + slug + ".md");
        var source = sources.FirstOrDefault();
        var content = new StringBuilder().AppendLine("# " + draft.Title).AppendLine().AppendLine(draft.BodyMarkdown).AppendLine()
            .AppendLine("## Burimi origjinal").AppendLine().AppendLine(source == null ? "" : $"- {source.Source}: [{source.Title}]({source.Url})").ToString();
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private JsonArray Read()
    {
        if (!File.Exists(DataFile)) return new JsonArray();
        try { return JsonNode.Parse(File.ReadAllText(DataFile))?.AsArray() ?? new JsonArray(); }
        catch (JsonException) { return new JsonArray(); }
    }

    private static string UniqueSlug(string baseSlug, JsonArray rows)
    {
        if (baseSlug.Length < 3) baseSlug = "lajm";
        var used = rows.Select(n => n?["slug"]?.GetValue<string>()).Where(s => s != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var slug = baseSlug;
        for (var n = 2; used.Contains(slug); n++) slug = baseSlug + "-" + n;
        return slug;
    }

    /// <summary>Same hash as illustrationFor() in the site's src/lib/categorize.ts (3 variants per category).</summary>
    private static int Illustration(string seed)
    {
        uint h = 0;
        foreach (var ch in seed) h = unchecked(h * 31 + ch);
        return (int)(h % 3);
    }

    private static string CategoryId(string category) => category switch
    {
        "sport" => "cat-sport", "ekonomi" => "cat-ekonomi", "teknologji" => "cat-teknologji",
        "shkence" => "cat-shkence", "kulture" => "cat-kulture", "bota" => "cat-bota",
        "rajoni" => "cat-rajoni", "magazine" => "cat-jetese", "kosova" => "cat-kosova",
        _ => "cat-politike",
    };
}
