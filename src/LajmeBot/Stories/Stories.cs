using System.Text.Json;
using LajmeBot.Discovery;
using LajmeBot.Text;

namespace LajmeBot.Stories;

/// <summary>One real-world event, reported by one or more sources.</summary>
public sealed class Story
{
    public List<FeedItem> Items { get; } = new();
    public HashSet<string> Tokens { get; } = new(StringComparer.Ordinal);

    public FeedItem Lead => Items.OrderByDescending(i => i.Summary.Length).First();
    public int SourceCount => Items.Select(i => i.Source).Distinct().Count();
    public DateTimeOffset Newest => Items.Max(i => i.Published ?? DateTimeOffset.MinValue);
    public string Key => string.Join(' ', Tokens.OrderBy(t => t).Take(12));

    public void Add(FeedItem item)
    {
        Items.Add(item);
        Tokens.UnionWith(item.Tokens);
    }

    public double Score(DateTimeOffset now)
    {
        var ageHours = Math.Max(0, (now - Newest).TotalHours);
        return SourceCount * 3.0 + Math.Min(Items.Count, 6) - ageHours * 0.35;
    }
}

public static class StoryClusterer
{
    /// <summary>Greedy single-link clustering on headline token similarity.</summary>
    public static List<Story> Cluster(IEnumerable<FeedItem> items, double threshold)
    {
        var stories = new List<Story>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.OrderByDescending(i => i.Published ?? DateTimeOffset.MinValue))
        {
            if (!seenUrls.Add(item.Url) || item.Tokens.Count < 2) continue;
            Story? best = null;
            var bestSim = 0.0;
            foreach (var s in stories)
            {
                var sim = s.Items.Max(x => TextUtil.Jaccard(x.Tokens, item.Tokens));
                if (sim > bestSim) { bestSim = sim; best = s; }
            }
            if (best != null && bestSim >= threshold) best.Add(item);
            else { var s = new Story(); s.Add(item); stories.Add(s); }
        }
        return stories;
    }
}

public sealed class PublishedStory
{
    public DateTimeOffset At { get; set; }
    public string Title { get; set; } = "";
    public List<string> Tokens { get; set; } = new();
    public List<string> SourceUrls { get; set; } = new();
    /// <summary>site slug → article slug</summary>
    public Dictionary<string, string> Articles { get; set; } = new();
}

public sealed class BotState
{
    public int RotationCursor { get; set; }
    public List<PublishedStory> Published { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static BotState Load(string path)
    {
        if (!File.Exists(path)) return new BotState();
        try { return JsonSerializer.Deserialize<BotState>(File.ReadAllText(path), Json) ?? new BotState(); }
        catch (JsonException) { return new BotState(); }
    }

    public void Save(string path, int retentionDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-retentionDays);
        Published = Published.Where(p => p.At >= cutoff).OrderByDescending(p => p.At).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>True if this story (or one very close to it) has already been written.</summary>
    public bool AlreadyCovered(Story story, double threshold)
    {
        var urls = new HashSet<string>(story.Items.Select(i => i.Url), StringComparer.OrdinalIgnoreCase);
        foreach (var p in Published)
        {
            if (p.SourceUrls.Any(urls.Contains)) return true;
            var tokens = new HashSet<string>(p.Tokens, StringComparer.Ordinal);
            if (story.Items.Any(i => TextUtil.Jaccard(i.Tokens, tokens) >= threshold + 0.1)) return true;
        }
        return false;
    }
}
