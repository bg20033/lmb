using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LajmeBot.Discovery;
using LajmeBot.Images;
using LajmeBot.Stories;
using LajmeBot.Text;

namespace LajmeBot.Ai;

public sealed record ArticleRequest(
    SiteConfig Site,
    Story Story,
    IReadOnlyList<SourceText> Sources,
    IReadOnlyList<string> RecentTitles,
    string? Feedback,
    string? Facts = null);

public sealed record ArticleDraft(
    bool Skip,
    string? SkipReason,
    string Title,
    string Description,
    string Category,
    List<string> Tags,
    string BodyMarkdown,
    string ImageMotif);

public interface IArticleWriter
{
    Task<ArticleDraft> WriteAsync(ArticleRequest req, CancellationToken ct);

    /// <summary>
    /// Optional first step: turn the source articles into a short list of facts, so the article is written
    /// from facts instead of from the original prose (much less copying with small models). null = not supported.
    /// </summary>
    Task<string?> ExtractFactsAsync(Story story, IReadOnlyList<SourceText> sources, CancellationToken ct) => Task.FromResult<string?>(null);
}

/// <summary>Writes an original article with the Anthropic Messages API, using a forced tool call for structured output.</summary>
public sealed class ClaudeWriter : IArticleWriter, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly AiConfig _cfg;
    private readonly Log _log;

    public ClaudeWriter(AiConfig cfg, string apiKey, Log log)
    {
        _cfg = cfg;
        _log = log;
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public async Task<ArticleDraft> WriteAsync(ArticleRequest req, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _cfg.Model,
            ["max_tokens"] = _cfg.MaxTokens,
            ["system"] = Prompts.System(req.Site, _cfg),
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = Prompts.User(req) }),
            ["tools"] = new JsonArray(Prompts.Tool(req.Site)),
            ["tool_choice"] = new JsonObject { ["type"] = "tool", ["name"] = Prompts.ToolName },
        };
        if (_cfg.Temperature is { } temp) body["temperature"] = temp;

        for (var attempt = 1; ; attempt++)
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            HttpResponseMessage resp;
            string text;
            try
            {
                resp = await _http.PostAsync(_cfg.Endpoint + "/v1/messages", content, ct);
                text = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException && !ct.IsCancellationRequested)
            {
                if (attempt >= 3) throw new InvalidOperationException($"Claude API: {ex.Message}");
                _log.Warn($"Claude API: {ex.Message}; retrying");
                continue;
            }
            using var _ = resp;
            var status = (int)resp.StatusCode;
            if ((status == 429 || status == 529 || status >= 500) && attempt < 4)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt * attempt);
                _log.Warn($"Claude API {status}, retrying in {wait.TotalSeconds:0}s");
                await Task.Delay(wait, ct);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Claude API {status}: {Truncate(text, 400)}");

            var root = JsonNode.Parse(text)!;
            var stop = root["stop_reason"]?.GetValue<string>();
            var toolUse = root["content"]?.AsArray().FirstOrDefault(c => c?["type"]?.GetValue<string>() == "tool_use");
            if (toolUse?["input"] is not JsonObject input)
                throw new InvalidOperationException($"Claude returned no tool call (stop_reason={stop})");
            if (stop == "max_tokens")
                throw new InvalidOperationException("Claude hit max_tokens; raise Ai.MaxTokens");
            var usage = root["usage"];
            _log.Debug($"tokens in={usage?["input_tokens"]} out={usage?["output_tokens"]}");
            return Prompts.ParseDraft(input);
        }
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Offline stand-in used for tests. It never reuses source sentences: the body is a
/// clearly marked placeholder, so a mock run can never publish copied text.
/// </summary>
public sealed class MockWriter : IArticleWriter
{
    public Task<ArticleDraft> WriteAsync(ArticleRequest req, CancellationToken ct)
    {
        var lead = req.Story.Lead;
        var cat = req.Site.Categories.Contains("politike") ? "politike" : req.Site.Categories[0];
        var title = $"TEST {req.Site.Name}: {lead.Title}";
        if (title.Length > 110) title = title[..110];
        var para = "Ky është një paragraf testues i krijuar nga modaliteti pa inteligjencë artificiale. "
                 + "Ai shërben vetëm për të kontrolluar që artikulli ruhet saktë, që imazhi krijohet dhe që faqja ndërtohet pa gabime. ";
        var sb = new StringBuilder();
        for (var s = 1; s <= 4; s++)
        {
            sb.AppendLine($"## Seksioni testues {s}").AppendLine();
            for (var p = 0; p < 3; p++) sb.AppendLine(string.Concat(Enumerable.Repeat(para, 2)).Trim()).AppendLine();
        }
        return Task.FromResult(new ArticleDraft(false, null, title,
            TextUtil.TruncateWords($"Artikull testues për „{lead.Title}“, i krijuar pa AI për të verifikuar publikimin dhe ndërtimin e faqes.", 170),
            cat, new() { "Test", "Kosova" }, sb.ToString(), HeroImageGenerator.Motifs[(int)(Text.TextUtil.StableHash(title) % (ulong)HeroImageGenerator.Motifs.Length)]));
    }
}

public static class Prompts
{
    public const string ToolName = "publish_article";

    public static string System(SiteConfig site, AiConfig ai) => $"""
        You are a senior editor at "{site.Name}", an Albanian-language online newsroom in Kosovo.
        You write ORIGINAL news articles in standard Albanian (gjuha standarde shqipe, with correct ë and ç).

        Editorial voice for {site.Name}: {site.Style}

        Hard rules:
        1. Use ONLY facts that appear in the provided source material. Never invent names, numbers, dates, quotes, places or reactions.
           If sources disagree, say so and attribute each version ("sipas Kohës…", "Insajderi raporton se…").
        2. Write in your own words. Do NOT copy sentences or long phrases from the sources: outside quotation marks never reuse more than 8 consecutive words of a source — restructure every sentence. Short direct quotes of people
           (max ~15 words, in quotation marks, attributed) are allowed only if they appear in a source.
        3. Attribute key facts to the outlet that reported them. Do not claim to have witnessed or independently confirmed anything.
        4. Neutral, factual tone. No clickbait, no sensationalism, no insults, no speculation about guilt.
           For crime, accidents or court cases: presumption of innocence, use initials unless the person is a public figure,
           never publish details that identify victims or minors.
        5. Structure: a strong first paragraph (who/what/when/where), then 3–5 sections with "## " subheadings,
           background/context from the sources, and what happens next if the sources say so.
           Length {ai.MinWords}–{ai.MaxWords} words. Markdown only: paragraphs, "## " headings, "- " lists, **bold** sparingly.
           Do NOT include the title as a heading, do NOT add a sources list or a disclaimer — those are added automatically.
        6. Headline: 40–100 characters, informative, sentence case, no quotes around it, no ALL CAPS.
           Description (meta/dek): 90–165 characters, one or two full sentences, not a copy of the first paragraph.
        7. If the material is too thin to write at least {ai.MinWords} honest words, is only gossip/horoscope/advertising,
           or would require speculation, set skip=true and explain in skip_reason.

        Always answer by calling the {ToolName} tool.
        """;

    public static string User(ArticleRequest req)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Today is {DateTimeOffset.UtcNow:yyyy-MM-dd} (Kosovo time zone Europe/Belgrade).");
        if (!string.IsNullOrWhiteSpace(req.Facts))
        {
            sb.AppendLine("Write an original article for our site about this story, using ONLY the facts below.");
            sb.AppendLine($"The facts were collected from: {string.Join(", ", req.Sources.Select(x => x.Source).Distinct())}.");
            sb.AppendLine();
            sb.AppendLine("<facts>");
            sb.AppendLine(req.Facts.Trim());
            sb.AppendLine("</facts>");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("Write an original article for our site about this story. Source material follows (for facts only — do not copy wording).");
            sb.AppendLine();
            var i = 1;
            foreach (var s in req.Sources)
            {
                sb.AppendLine($"<source index=\"{i++}\" outlet=\"{s.Source}\" url=\"{s.Url}\">");
                sb.AppendLine($"Headline: {s.Title}");
                sb.AppendLine(s.Text);
                sb.AppendLine("</source>");
                sb.AppendLine();
            }
        }
        if (req.RecentTitles.Count > 0)
        {
            sb.AppendLine("Our site recently published these headlines — do not repeat them word for word:");
            foreach (var t in req.RecentTitles.Take(15)) sb.AppendLine($"- {t}");
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(req.Feedback))
        {
            sb.AppendLine("Your previous attempt was rejected by the automatic checker. Fix this and try again:");
            sb.AppendLine(req.Feedback);
        }
        return sb.ToString();
    }

    public const string FactsSystem = """
        You are a news researcher for an Albanian newsroom. Read the source articles and list the verifiable facts.
        Answer in standard Albanian as a list of "- " bullet points (10–30 bullets), nothing else.
        Each bullet is ONE fact in your own short words (at most ~20 words): who, what, when, where, numbers, decisions, next steps.
        Do not copy sentences from the sources. Direct quotes of people only inside quotation marks, with the speaker's name.
        Add the outlet that reported the fact in parentheses, e.g. (Koha). If outlets disagree, write both versions.
        No opinions and no background that is not in the sources.
        """;

    public static string FactsUser(Story story, IReadOnlyList<SourceText> sources)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Story: {story.Lead.Title}");
        sb.AppendLine();
        foreach (var s in sources)
        {
            sb.AppendLine($"<source outlet=\"{s.Source}\">");
            sb.AppendLine($"Headline: {s.Title}");
            sb.AppendLine(s.Text);
            sb.AppendLine("</source>");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Plain labelled-text output format (robust with small/free models).</summary>
    public static string TextFormat(SiteConfig site, AiConfig ai) => $"""
        OUTPUT FORMAT — plain text exactly like this, no JSON and no code fences:
        SKIP: no
        TITLE: <Albanian headline, 40–100 characters>
        DESCRIPTION: <Albanian meta description, 90–165 characters>
        CATEGORY: <exactly one of: {string.Join(", ", site.Categories)}>
        TAGS: <2–5 short Albanian tags, separated by commas>
        IMAGE: <exactly one of: {string.Join(", ", HeroImageGenerator.Motifs)}>
        BODY:
        <the full article in Markdown: at least {ai.MinWords} words, 3–5 sections with "## " subheadings>

        The BODY must be long — at least {ai.MinWords} words. Shorter articles are rejected automatically.
        If the story must be skipped, answer only with the line: SKIP: yes — <reason>
        """;

    private static readonly global::System.Text.RegularExpressions.Regex FieldLine =
        new(@"^[ \t>*_#-]*(SKIP|TITLE|DESCRIPTION|CATEGORY|TAGS|IMAGE|BODY)[ \t*_]*:[ \t]*(.*)$",
            global::System.Text.RegularExpressions.RegexOptions.Multiline | global::System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Parses the labelled text format; null if it cannot be read.</summary>
    public static ArticleDraft? ParseTextDraft(string raw)
    {
        var s = raw.Replace("\r\n", "\n").Trim();
        s = global::System.Text.RegularExpressions.Regex.Replace(s, @"^```[a-zA-Z]*\s*\n|\n```\s*$", "");
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? body = null;
        foreach (global::System.Text.RegularExpressions.Match m in FieldLine.Matches(s))
        {
            var key = m.Groups[1].Value.ToUpperInvariant();
            if (fields.ContainsKey(key) || (key != "BODY" && body != null)) continue;
            if (key == "BODY")
            {
                body = (m.Groups[2].Value + "\n" + s[(m.Index + m.Length)..]).Trim();
                break;
            }
            fields[key] = m.Groups[2].Value.Trim();
        }

        static string Clean(string? v) => (v ?? "").Trim().Trim('*', '_', '"', '“', '”', '„', '\'', ' ').Trim();
        var skip = Clean(fields.GetValueOrDefault("SKIP"));
        if (global::System.Text.RegularExpressions.Regex.IsMatch(skip, @"^(yes|po|true|y)\b", global::System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return new ArticleDraft(true, global::System.Text.RegularExpressions.Regex.Replace(skip, @"^\w+\W*", ""), "", "", "", new(), "", "");

        var title = Clean(fields.GetValueOrDefault("TITLE"));
        if (title.Length == 0 || string.IsNullOrWhiteSpace(body)) return null;

        var catRaw = Text.TextUtil.Fold(Clean(fields.GetValueOrDefault("CATEGORY")));
        catRaw = global::System.Text.RegularExpressions.Regex.Replace(catRaw, "[^a-z]", "");
        var tags = Clean(fields.GetValueOrDefault("TAGS")).Split(',', ';')
            .Select(t => Clean(t).TrimStart('#')).Where(t => t.Length > 1).Distinct().Take(5).ToList();
        var motif = Clean(fields.GetValueOrDefault("IMAGE")).ToLowerInvariant();
        return new ArticleDraft(false, null, title, Clean(fields.GetValueOrDefault("DESCRIPTION")), catRaw, tags, body!, motif);
    }

    public static JsonObject Tool(SiteConfig site)
    {
        static JsonObject Str(string d) => new() { ["type"] = "string", ["description"] = d };
        var cats = new JsonArray(site.Categories.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray());
        var motifs = new JsonArray(HeroImageGenerator.Motifs.Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());
        return new JsonObject
        {
            ["name"] = ToolName,
            ["description"] = "Publish the finished article (or skip the story).",
            ["input_schema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["skip"] = new JsonObject { ["type"] = "boolean", ["description"] = "true if the story should not be published" },
                    ["skip_reason"] = Str("why the story was skipped (empty if not skipped)"),
                    ["title"] = Str("Albanian headline, 40–100 characters"),
                    ["description"] = Str("Albanian meta description / dek, 90–165 characters"),
                    ["category"] = new JsonObject
                    {
                        ["type"] = "string", ["enum"] = cats,
                        ["description"] = "bota=world, politike=Kosovo/Albania/region politics & institutions, ekonomi=economy, teknologji=tech, shkence=science/health/climate, kulture=culture, sport=sport",
                    },
                    ["tags"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "2–5 short Albanian topic tags, e.g. \"Kosova\", \"Kuvendi\"" },
                    ["body_markdown"] = Str("article body in Markdown (no title, no sources list)"),
                    ["image_motif"] = new JsonObject { ["type"] = "string", ["enum"] = motifs, ["description"] = "abstract illustration that best fits the topic" },
                },
                ["required"] = new JsonArray("skip", "title", "description", "category", "tags", "body_markdown", "image_motif"),
            },
        };
    }

    public static ArticleDraft ParseDraft(JsonObject o)
    {
        string S(string k) => o[k]?.GetValueKind() == JsonValueKind.String ? o[k]!.GetValue<string>().Trim() : "";
        var tags = o["tags"] is JsonArray arr
            ? arr.Where(t => t?.GetValueKind() == JsonValueKind.String).Select(t => t!.GetValue<string>().Trim()).Where(t => t.Length > 1).Distinct().Take(5).ToList()
            : new List<string>();
        var skip = o["skip"]?.GetValueKind() == JsonValueKind.True;
        return new ArticleDraft(skip, S("skip_reason"), S("title"), S("description"), S("category"), tags, S("body_markdown"), S("image_motif"));
    }
}
