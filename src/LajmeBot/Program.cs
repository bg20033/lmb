using System.Text;
using System.Text.Json;
using LajmeBot;
using LajmeBot.Ai;
using LajmeBot.Discovery;
using LajmeBot.Http;
using LajmeBot.Publishing;
using LajmeBot.Stories;
using LajmeBot.Text;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
Console.OutputEncoding = Encoding.UTF8;

var opts = Options.Parse(args);
if (opts.Command == "selftest") return SelfTest.Run();
if (opts.Command == "sites")
{
    // Used by the GitHub workflow: one "slug repo branch" line per enabled site.
    foreach (var s in BotConfig.Load(opts.ConfigPath).Sites.Where(s => s.Enabled))
        Console.WriteLine($"{s.Slug} {s.Repo} {s.Branch}");
    return 0;
}
if (opts.Command == "discover")
{
    // Live check of feeds, clustering and text extraction — no API key, no sites, writes nothing.
    return await DiscoverCommand.RunAsync(BotConfig.Load(opts.ConfigPath), opts.FixturesDir, opts.ReportPath, opts.Top, opts.Verbose);
}
if (opts.Command != "run")
{
    Console.WriteLine("""
        LajmeBot — automatic news writer for Astro news sites

        Usage:
          dotnet run --project src/LajmeBot -- run [options]
          dotnet run --project src/LajmeBot -- selftest
          dotnet run --project src/LajmeBot -- discover [--top 5] [--report file.md]
                                     (live test of feeds + text extraction, no AI, writes nothing)
          dotnet run --project src/LajmeBot -- sites        (lists enabled sites for the workflow)

        Options:
          --config <file>       bot configuration (default: config/bot.json)
          --state <file>        memory of published stories (default: state/state.json)
          --sites-root <dir>    folder containing one checkout per site, named by site slug (default: sites)
          --site <slug>         only publish to this site (repeatable)
          --max-stories <n>     override MaxStoriesPerRun
          --provider <name>     override Ai.Provider (openrouter | anthropic)
          --model <id>          override Ai.Model, e.g. deepseek/deepseek-v4.1-flash
          --dry-run             write nothing to the sites; full drafts go to --drafts-dir
          --drafts-dir <dir>    where dry-run drafts are saved (default: out/drafts)
          --mock-ai             offline placeholder writer (for testing without an API key)
          --fixtures <dir>      read feeds/pages from local files instead of the internet (tests)
          --summary <file>      write a JSON summary of what was published
          --verbose             debug logging
        """);
    return 1;
}

var log = new Log(opts.Verbose);
var cfg = BotConfig.Load(opts.ConfigPath);
if (opts.MaxStories is { } ms) cfg.MaxStoriesPerRun = ms;
if (!string.IsNullOrWhiteSpace(opts.Provider)) cfg.Ai.Provider = opts.Provider;
if (!string.IsNullOrWhiteSpace(opts.Model)) { cfg.Ai.Model = opts.Model; cfg.Ai.FallbackModels.Clear(); }

// ---- sites -------------------------------------------------------------------------------------
var sites = new List<(SiteConfig cfg, SitePublisher pub)>();
foreach (var s in cfg.Sites.Where(s => s.Enabled && (opts.OnlySites.Count == 0 || opts.OnlySites.Contains(s.Slug))))
{
    s.Root = Path.GetFullPath(Path.Combine(opts.SitesRoot, s.Slug));
    var pub = new SitePublisher(s, cfg.Ai, log);
    if (!pub.Exists)
    {
        // A dry-run only needs the site's settings from config, so it also works without the checkout.
        if (!opts.DryRun) { log.Warn($"{s.Slug}: no checkout at {s.Root} — skipped"); continue; }
        log.Info($"{s.Slug}: no checkout — dry-run continues without the site's recent titles");
    }
    sites.Add((s, pub));
}
if (sites.Count == 0) { log.Error("No site checkouts found. Nothing to do."); return 2; }

// ---- services ----------------------------------------------------------------------------------
IFetcher fetcher = opts.FixturesDir != null ? new FixtureFetcher(opts.FixturesDir) : new PoliteFetcher(cfg);
IArticleWriter writer;
if (opts.MockAi) writer = new MockWriter();
else
{
    var key = Environment.GetEnvironmentVariable(cfg.Ai.KeyEnv);
    if (string.IsNullOrWhiteSpace(key)) { log.Error($"Missing API key: set the {cfg.Ai.KeyEnv} environment variable (or use --mock-ai)."); return 2; }
    writer = cfg.Ai.IsOpenRouter ? new OpenRouterWriter(cfg.Ai, key, log) : new ClaudeWriter(cfg.Ai, key, log);
    log.Info($"AI: {cfg.Ai.Provider} / {cfg.Ai.Model}" + (cfg.Ai.IsOpenRouter && cfg.Ai.FallbackModels.Count > 0 ? $" (rezervë: {string.Join(", ", cfg.Ai.FallbackModels)})" : ""));
}

using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(25));
var ct = cts.Token;
var state = BotState.Load(opts.StatePath);
var now = DateTimeOffset.UtcNow;

// ---- 1. discover -------------------------------------------------------------------------------
var feeds = new FeedReader(fetcher, log);
var allItems = (await Task.WhenAll(cfg.Sources.Where(s => s.Enabled).Select(async s =>
{
    try { return await feeds.ReadSourceAsync(s, ct); }
    catch (Exception ex) when (ex is not OperationCanceledException) { log.Warn($"{s.Name}: {ex.Message}"); return new List<FeedItem>(); }
}))).SelectMany(x => x).ToList();
log.Info($"Discovered {allItems.Count} items from {allItems.Select(i => i.Source).Distinct().Count()} sources");

// ---- 2. cluster + pick ---------------------------------------------------------------------------
var maxAge = TimeSpan.FromHours(cfg.MaxStoryAgeHours);
var fresh = allItems.Where(i => i.Published is null || now - i.Published.Value <= maxAge).ToList();
var stories = StoryClusterer.Cluster(fresh, cfg.ClusterSimilarity)
    .Where(s => !state.AlreadyCovered(s, cfg.ClusterSimilarity))
    .OrderByDescending(s => s.Score(now))
    .ToList();
log.Info($"{stories.Count} new candidate stories (" + string.Join(", ", stories.Take(5).Select(s => $"{s.SourceCount}× \"{s.Lead.Title}\"")) + ")");

// ---- 3. write + publish ------------------------------------------------------------------------
var articleFetcher = new ArticleFetcher(fetcher, log);
var capacity = sites.ToDictionary(s => s.cfg.Slug, s => s.cfg.MaxArticlesPerRun);
var recent = sites.ToDictionary(s => s.cfg.Slug, s => s.pub.RecentTitles());
var published = new List<PublishedArticle>();
var storiesDone = 0;
var aiCalls = 0;
var aiCallBudget = Math.Max(4, cfg.MaxStoriesPerRun * cfg.SitesPerStory * 3); // hard cap on API spend per run

if (!opts.DryRun) foreach (var (_, pub) in sites) pub.EnsureAuthor();

foreach (var story in stories)
{
    if (storiesDone >= cfg.MaxStoriesPerRun || capacity.Values.All(v => v <= 0) || ct.IsCancellationRequested) break;
    if (aiCalls >= aiCallBudget) { log.Warn($"AI call budget ({aiCallBudget}) used up for this run"); break; }

    // Sources: at most one item per outlet, richest summary first.
    var picks = story.Items.GroupBy(i => i.Source).Select(g => g.OrderByDescending(i => i.Summary.Length).First())
        .OrderByDescending(i => i.Summary.Length).Take(cfg.MaxSourcesPerStory).ToList();
    var sources = (await Task.WhenAll(picks.Select(p => articleFetcher.FetchAsync(p, ct)))).Where(s => s != null).Select(s => s!).ToList();
    if (sources.Sum(s => s.Text.Length) < 400)
    {
        log.Info($"skip (too little source text): {story.Lead.Title}");
        continue;
    }

    // Rotate which sites get this story so every site gets a different mix.
    var ordered = Enumerable.Range(0, sites.Count).Select(k => sites[(state.RotationCursor + k) % sites.Count]).ToList();
    state.RotationCursor = (state.RotationCursor + 1) % Math.Max(1, sites.Count);
    var targets = ordered.Where(s => capacity[s.cfg.Slug] > 0).Take(cfg.SitesPerStory).ToList();

    var record = new PublishedStory
    {
        At = now,
        Title = story.Lead.Title,
        Tokens = story.Lead.Tokens.ToList(),
        SourceUrls = story.Items.Select(i => i.Url).ToList(),
    };
    var skippedByAi = false;
    var rejectedByChecks = false;

    foreach (var (site, pub) in targets)
    {
        string? feedback = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            if (aiCalls >= aiCallBudget) break;
            aiCalls++;
            ArticleDraft draft;
            try { draft = await writer.WriteAsync(new ArticleRequest(site, story, sources, recent[site.Slug], feedback), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Error($"{site.Slug}: writer failed: {ex.Message}");
                break;
            }
            if (draft.Skip)
            {
                log.Info($"{site.Slug}: AI skipped \"{story.Lead.Title}\": {draft.SkipReason}");
                skippedByAi = true;
                break;
            }
            var problems = pub.Validate(draft, sources);
            if (problems.Count > 0)
            {
                feedback = string.Join("\n", problems.Select(p => "- " + p));
                log.Warn($"{site.Slug}: draft rejected (attempt {attempt}): {string.Join(" | ", problems)}");
                if (attempt == 2) rejectedByChecks = true;
                continue;
            }
            if (opts.DryRun)
            {
                var preview = pub.WritePreview(draft, sources, story.SourceCount >= cfg.FeaturedMinSources, DateTimeOffset.UtcNow, opts.DraftsDir);
                log.Info($"[dry-run] {site.Slug}: {draft.Category} | {draft.Title} ({TextUtil.WordCount(draft.BodyMarkdown)} fjalë)\n  {draft.Description}\n  → {preview}");
                record.Articles[site.Slug] = "(dry-run)";
            }
            else
            {
                var art = pub.Write(draft, sources, story.SourceCount >= cfg.FeaturedMinSources, DateTimeOffset.UtcNow);
                published.Add(art);
                record.Articles[site.Slug] = art.Slug;
                recent[site.Slug].Insert(0, art.Title);
                log.Info($"{site.Slug}: published /{art.Category}/{art.Slug}");
            }
            capacity[site.Slug]--;
            break;
        }
        if (skippedByAi) break;
    }

    // Remember the story even when the AI skipped it or the drafts kept failing the checks,
    // so the next run does not pay for it again. (API/network errors are retried next run.)
    if (record.Articles.Count > 0 || skippedByAi || rejectedByChecks) state.Published.Add(record);
    if (record.Articles.Count > 0) storiesDone++;
}

if (!opts.DryRun) state.Save(opts.StatePath, cfg.StateRetentionDays);
if (opts.SummaryPath != null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(opts.SummaryPath))!);
    File.WriteAllText(opts.SummaryPath, JsonSerializer.Serialize(new
    {
        at = now,
        stories = storiesDone,
        articles = published.Select(p => new { p.Site, p.Slug, p.Title, p.Category }),
        sites = published.Select(p => p.Site).Distinct(),
    }, new JsonSerializerOptions { WriteIndented = true }));
}
log.Info($"Done: {published.Count} articles for {storiesDone} stories.");
if (writer is OpenRouterWriter orw && orw.TotalCost > 0) log.Info($"Kosto e AI në këtë ekzekutim: ${orw.TotalCost:0.0000}");
(writer as IDisposable)?.Dispose();
(fetcher as IDisposable)?.Dispose();
return 0;


sealed class Options
{
    public string Command { get; set; } = "";
    public string ConfigPath { get; set; } = "config/bot.json";
    public string StatePath { get; set; } = "state/state.json";
    public string SitesRoot { get; set; } = "sites";
    public HashSet<string> OnlySites { get; } = new();
    public int? MaxStories { get; set; }
    public string? Provider { get; set; }
    public string? Model { get; set; }
    public bool DryRun { get; set; }
    public string DraftsDir { get; set; } = "out/drafts";
    public bool MockAi { get; set; }
    public bool Verbose { get; set; }
    public string? FixturesDir { get; set; }
    public string? SummaryPath { get; set; }
    public string? ReportPath { get; set; }
    public int Top { get; set; } = 5;

    public static Options Parse(string[] args)
    {
        var o = new Options { Command = args.FirstOrDefault() ?? "" };
        for (var i = 1; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--config": o.ConfigPath = Next(); break;
                case "--state": o.StatePath = Next(); break;
                case "--sites-root": o.SitesRoot = Next(); break;
                case "--site": o.OnlySites.Add(Next()); break;
                case "--max-stories": o.MaxStories = int.Parse(Next()); break;
                case "--provider": o.Provider = Next(); break;
                case "--model": o.Model = Next(); break;
                case "--dry-run": o.DryRun = true; break;
                case "--drafts-dir": o.DraftsDir = Next(); break;
                case "--mock-ai": o.MockAi = true; break;
                case "--fixtures": o.FixturesDir = Next(); break;
                case "--summary": o.SummaryPath = Next(); break;
                case "--report": o.ReportPath = Next(); break;
                case "--top": o.Top = int.Parse(Next()); break;
                case "--verbose": o.Verbose = true; break;
                default: throw new ArgumentException($"Unknown option {args[i]}");
            }
        }
        return o;
    }
}
