using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LajmeBot.Ai;
using LajmeBot.Discovery;
using LajmeBot.Images;
using LajmeBot.Text;

namespace LajmeBot.Publishing;

public sealed record PublishedArticle(string Site, string Slug, string Title, string Category, string MarkdownPath, string ImagePath);

/// <summary>Reads and writes one Astro site checkout (src/content/news + src/assets/news).</summary>
public sealed partial class SitePublisher
{
    private readonly SiteConfig _site;
    private readonly AiConfig _ai;
    private readonly Log _log;

    public SitePublisher(SiteConfig site, AiConfig ai, Log log) { _site = site; _ai = ai; _log = log; }

    private string NewsDir => Path.Combine(_site.Root, "src", "content", "news");
    private string AssetsDir => Path.Combine(_site.Root, "src", "assets", "news");
    private string AuthorsFile => Path.Combine(_site.Root, "src", "content", "authors.json");

    public bool Exists => Directory.Exists(NewsDir);

    [GeneratedRegex(@"^title:\s*""?(.*?)""?\s*$", RegexOptions.Multiline)]
    private static partial Regex TitleLine();

    /// <summary>Titles of the newest articles on the site (so the AI avoids repeating them).</summary>
    public List<string> RecentTitles(int max = 20) => !Directory.Exists(NewsDir) ? new() :
        Directory.EnumerateFiles(NewsDir, "*.md")
            .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).Take(max)
            .Select(f => TitleLine().Match(File.ReadAllText(f.FullName)))
            .Where(m => m.Success).Select(m => m.Groups[1].Value.Replace("\\\"", "\"")).ToList();

    /// <summary>Adds the automated-newsroom author to authors.json if it is missing.</summary>
    public void EnsureAuthor(bool licensedCopy = false)
    {
        var arr = JsonNode.Parse(File.ReadAllText(AuthorsFile))!.AsArray();
        var author = arr.FirstOrDefault(a => a?["id"]?.GetValue<string>() == _site.AuthorId) as JsonObject;
        if (author != null && !licensedCopy) return;
        if (author == null)
        {
            author = new JsonObject { ["id"] = _site.AuthorId };
            arr.Add(author);
        }
        author["name"] = _site.AuthorName;
        author["role"] = "Redaksia automatike";
        author["bio"] = licensedCopy
            ? $"Artikujt e nënshkruar nga {_site.AuthorName} publikohen automatikisht me leje/licencë dhe me atribuim të qartë te burimi origjinal."
            : $"Artikujt e nënshkruar nga {_site.AuthorName} përgatiten automatikisht me ndihmën e inteligjencës artificiale, duke u bazuar vetëm në burime publike që citohen me lidhje në fund të çdo artikulli.";
        File.WriteAllText(AuthorsFile, arr.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }) + "\n");
        _log.Info($"{_site.Slug}: added author '{_site.AuthorId}' to authors.json");
    }

    /// <summary>Checks a draft against the site's content schema and our own quality rules. Returns problems (empty = OK).</summary>
    public List<string> Validate(ArticleDraft d, IReadOnlyList<SourceText> sources, bool licensedCopy = false)
    {
        var p = new List<string>();
        if (!licensedCopy && d.Title.Length is < 20 or > 120) p.Add($"Title must be 20–120 characters (was {d.Title.Length}).");
        if (!licensedCopy && d.Description.Length < 70) p.Add($"Description must be at least 90 characters (was {d.Description.Length}).");
        if (!_site.Categories.Contains(d.Category)) p.Add($"Category '{d.Category}' is not one of: {string.Join(", ", _site.Categories)}.");
        var words = TextUtil.WordCount(d.BodyMarkdown);
        if (licensedCopy)
        {
            if (words == 0) p.Add("Licensed source text is empty.");
        }
        else
        {
            if (words < _ai.MinWords * 0.8) p.Add($"Body is too short: {words} words, need at least {_ai.MinWords}.");
            var own = TextUtil.WithoutShortQuotes(d.Title + "\n" + d.Description + "\n" + d.BodyMarkdown);
            var (run, passage) = TextUtil.LongestSharedPassage(own, sources.Select(s => s.Text + "\n" + s.Title), 8);
            if (run >= 12)
                p.Add($"The text copies a {run}-word passage from a source word for word: \"{TextUtil.TruncateWords(passage, 220)}\". " +
                      "Rewrite that part — and every other sentence — in your own words (change the sentence structure, not just single words). " +
                      "Short direct quotes of people are fine only inside quotation marks.");
        }
        return p;
    }

    public PublishedArticle Write(ArticleDraft d, IReadOnlyList<SourceText> sources, bool featured, DateTimeOffset now, bool licensedCopy = false)
    {
        Directory.CreateDirectory(NewsDir);
        Directory.CreateDirectory(AssetsDir);

        var slug = UniqueSlug(TextUtil.Slugify(d.Title));
        var imagePath = Path.Combine(AssetsDir, slug + ".png");
        var motif = MotifOf(d);
        HeroImageGenerator.Generate(imagePath, _site.Slug + "/" + slug, d.Category, motif, _site.Accent, _site.Tint, _site.TintAmount);

        var mdPath = Path.Combine(NewsDir, slug + ".md");
        File.WriteAllText(mdPath, Render(d, sources, featured, now, slug, licensedCopy), new UTF8Encoding(false));
        return new PublishedArticle(_site.Slug, slug, d.Title, d.Category, mdPath, imagePath);
    }

    /// <summary>
    /// Dry-run: writes the complete article (and its illustration) to a preview folder
    /// instead of the site, so it can be read in full without publishing anything.
    /// </summary>
    public string WritePreview(ArticleDraft d, IReadOnlyList<SourceText> sources, bool featured, DateTimeOffset now, string previewDir, bool licensedCopy = false)
    {
        Directory.CreateDirectory(previewDir);
        var slug = TextUtil.Slugify(d.Title);
        if (slug.Length < 3) slug = "lajm";
        var baseName = $"{_site.Slug}--{slug}";
        HeroImageGenerator.Generate(Path.Combine(previewDir, baseName + ".png"), _site.Slug + "/" + slug, d.Category, MotifOf(d), _site.Accent, _site.Tint, _site.TintAmount);
        var path = Path.Combine(previewDir, baseName + ".md");
        File.WriteAllText(path, Render(d, sources, featured, now, slug, licensedCopy), new UTF8Encoding(false));
        return path;
    }

    private static string MotifOf(ArticleDraft d) =>
        HeroImageGenerator.Motifs.Contains(d.ImageMotif) ? d.ImageMotif : HeroImageGenerator.Motifs[0];

    private string Render(ArticleDraft d, IReadOnlyList<SourceText> sources, bool featured, DateTimeOffset now, string slug, bool licensedCopy)
    {
        var motif = MotifOf(d);
        var description = d.Description.Length > 175 ? TextUtil.TruncateWords(d.Description, 174) : d.Description;
        var tags = d.Tags.Select(t => TextUtil.YamlString(TextUtil.TruncateWords(t, 40)));
        var local = ToKosovoTime(now);

        var md = new StringBuilder();
        md.AppendLine("---");
        md.AppendLine($"title: {TextUtil.YamlString(d.Title)}");
        md.AppendLine($"description: {TextUtil.YamlString(description)}");
        md.AppendLine($"category: {d.Category}");
        md.AppendLine($"author: {_site.AuthorId}");
        md.AppendLine($"publishedAt: {local:yyyy-MM-ddTHH:mm:sszzz}");
        md.AppendLine($"heroImage: ../../assets/news/{slug}.png");
        md.AppendLine($"heroImageAlt: {TextUtil.YamlString(HeroImageGenerator.AltText(motif))}");
        md.AppendLine($"tags: [{string.Join(", ", tags)}]");
        if (featured) md.AppendLine("featured: true");
        if (_site.PublishAsDraft) md.AppendLine("draft: true");
        md.AppendLine("---");
        md.AppendLine();
        md.AppendLine(CleanBody(d.BodyMarkdown));
        md.AppendLine();
        md.AppendLine(licensedCopy ? "## Burimi origjinal" : "## Burimet");
        md.AppendLine();
        foreach (var s in sources.DistinctBy(s => s.Url))
            md.AppendLine($"- {s.Source}: [{EscapeLinkText(s.Title)}]({s.Url})");
        md.AppendLine();
        md.AppendLine(licensedCopy
            ? $"*Ky tekst publikohet nga {_site.AuthorName} me leje/licencë. Burimi origjinal është i lidhur më sipër.*"
            : $"*Ky artikull është përgatitur automatikisht nga {_site.AuthorName} me ndihmën e inteligjencës artificiale, duke u bazuar në burimet e mësipërme. Nëse vëreni ndonjë pasaktësi, na shkruani.*");
        return md.ToString();
    }

    private string UniqueSlug(string baseSlug)
    {
        if (baseSlug.Length < 3) baseSlug = "lajm";
        var slug = baseSlug;
        for (var i = 2; File.Exists(Path.Combine(NewsDir, slug + ".md")); i++) slug = $"{baseSlug}-{i}";
        return slug;
    }

    private static string CleanBody(string body)
    {
        var lines = body.Replace("\r\n", "\n").Trim().Split('\n').ToList();
        // Drop a leading heading that just repeats the title, and any "Burimet"/sources section the model added anyway.
        if (lines.Count > 0 && lines[0].StartsWith('#')) lines.RemoveAt(0);
        var cut = lines.FindIndex(l => Regex.IsMatch(l, @"^#{1,3}\s*(Burimet|Burime|Sources)\b", RegexOptions.IgnoreCase));
        if (cut >= 0) lines = lines.Take(cut).ToList();
        var text = string.Join('\n', lines).Trim();
        return Regex.Replace(text, @"^# ", "## ", RegexOptions.Multiline);
    }

    private static string EscapeLinkText(string s) => s.Replace("[", "(").Replace("]", ")");

    public static DateTimeOffset ToKosovoTime(DateTimeOffset utc)
    {
        foreach (var id in new[] { "Europe/Belgrade", "Europe/Pristina", "Central European Standard Time" })
        {
            try { return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(id)); }
            catch (TimeZoneNotFoundException) { }
        }
        return utc;
    }
}
