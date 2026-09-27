using System.Text.Json;
using System.Text.Json.Serialization;

namespace LajmeBot;

public sealed class BotConfig
{
    /// <summary>Contact string sent in the User-Agent so site owners can reach you.</summary>
    public string UserAgent { get; set; } = "LajmeBot/1.0 (+mailto:redaksia@example.com)";
    public int RequestTimeoutSeconds { get; set; } = 25;
    /// <summary>Minimum pause between two requests to the same host.</summary>
    public int MinDelayPerHostMs { get; set; } = 1500;
    public bool RespectRobotsTxt { get; set; } = true;

    /// <summary>Only stories whose newest item is younger than this are considered.</summary>
    public int MaxStoryAgeHours { get; set; } = 10;
    /// <summary>How many new stories to write per run (in total).</summary>
    public int MaxStoriesPerRun { get; set; } = 3;
    /// <summary>How many of the sites receive their own version of each story.</summary>
    public int SitesPerStory { get; set; } = 2;
    /// <summary>Source texts fetched per story (the AI reads them for facts only).</summary>
    public int MaxSourcesPerStory { get; set; } = 4;
    /// <summary>Titles with Jaccard similarity at or above this are treated as the same story.</summary>
    public double ClusterSimilarity { get; set; } = 0.3;
    /// <summary>Days a published story is remembered to avoid writing it twice.</summary>
    public int StateRetentionDays { get; set; } = 10;
    /// <summary>A story with at least this many independent sources is marked "featured".</summary>
    public int FeaturedMinSources { get; set; } = 3;

    public AiConfig Ai { get; set; } = new();
    public List<SourceConfig> Sources { get; set; } = new();
    public List<SiteConfig> Sites { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static BotConfig Load(string path)
    {
        var cfg = JsonSerializer.Deserialize<BotConfig>(File.ReadAllText(path), Json)
                  ?? throw new InvalidOperationException($"Empty config: {path}");
        if (cfg.Sites.Count == 0) throw new InvalidOperationException("Config has no sites.");
        if (cfg.Sources.Count == 0) throw new InvalidOperationException("Config has no sources.");
        return cfg;
    }
}

public sealed class AiConfig
{
    /// <summary>"openrouter" (hundreds of models, one key) or "anthropic" (Claude directly).</summary>
    public string Provider { get; set; } = "openrouter";

    /// <summary>
    /// Model id for the chosen provider.
    /// OpenRouter: e.g. google/gemini-3.8-flash, deepseek/deepseek-v4.1-flash — https://openrouter.ai/models
    /// Anthropic: e.g. claude-sonnet-5 — https://platform.claude.com/docs/en/about-claude/models/overview
    /// </summary>
    public string Model { get; set; } = "google/gemini-3.8-flash";

    /// <summary>OpenRouter only: models tried in order if the main one is down or rate-limited.</summary>
    public List<string> FallbackModels { get; set; } = new();

    public int MaxTokens { get; set; } = 8000;
    /// <summary>Optional; leave null to use the model default.</summary>
    public double? Temperature { get; set; }
    /// <summary>Environment variable with the API key. Default: OPENROUTER_API_KEY or ANTHROPIC_API_KEY.</summary>
    public string? ApiKeyEnv { get; set; }
    /// <summary>Default: https://openrouter.ai/api/v1 or https://api.anthropic.com</summary>
    public string? BaseUrl { get; set; }
    public int MinWords { get; set; } = 450;
    public int MaxWords { get; set; } = 800;

    public bool IsOpenRouter => Provider.Equals("openrouter", StringComparison.OrdinalIgnoreCase);
    public string KeyEnv => ApiKeyEnv ?? (IsOpenRouter ? "OPENROUTER_API_KEY" : "ANTHROPIC_API_KEY");
    public string Endpoint => (BaseUrl ?? (IsOpenRouter ? "https://openrouter.ai/api/v1" : "https://api.anthropic.com")).TrimEnd('/');
}

public sealed class SourceConfig
{
    public string Name { get; set; } = "";
    /// <summary>Home page; used to auto-discover the feed if the feed URLs fail.</summary>
    public string HomeUrl { get; set; } = "";
    public List<string> FeedUrls { get; set; } = new();
    public bool Enabled { get; set; } = true;
    /// <summary>Max feed items taken from this source per run.</summary>
    public int MaxItems { get; set; } = 30;
}

public sealed class SiteConfig
{
    public string Slug { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    /// <summary>GitHub "owner/repo" — used by the workflow, not by the bot itself.</summary>
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "main";
    /// <summary>Brand accent for the generated illustrations, e.g. #0057b8.</summary>
    public string Accent { get; set; } = "#cc0000";
    /// <summary>Optional colour every illustration background is shifted toward.</summary>
    public string? Tint { get; set; }
    public double TintAmount { get; set; }
    /// <summary>Max articles this site receives per run.</summary>
    public int MaxArticlesPerRun { get; set; } = 2;
    /// <summary>Editorial voice passed to the AI.</summary>
    public string Style { get; set; } = "";
    public List<string> Categories { get; set; } = new() { "bota", "politike", "ekonomi", "teknologji", "shkence", "kulture", "sport" };
    public string AuthorId { get; set; } = "redaksia";
    public string AuthorName { get; set; } = "Redaksia";
    /// <summary>true = articles are written with draft: true (not shown on the site until you remove it).</summary>
    public bool PublishAsDraft { get; set; }

    [JsonIgnore] public string Root { get; set; } = "";
}
