using System.Text;
using LajmeBot.Discovery;
using LajmeBot.Http;
using LajmeBot.Stories;
using LajmeBot.Text;

namespace LajmeBot;

/// <summary>
/// `discover`: checks the whole reading side of the bot against the live internet —
/// crawling, clustering and text extraction — without an API key and without the sites.
/// Nothing is written or published.
/// </summary>
public static class DiscoverCommand
{
    public static async Task<int> RunAsync(BotConfig cfg, string? fixturesDir, string? reportPath, int topStories, bool verbose)
    {
        var log = new Log(verbose);
        IFetcher fetcher = fixturesDir != null ? new FixtureFetcher(fixturesDir) : new PoliteFetcher(cfg);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var ct = cts.Token;
        var now = DateTimeOffset.UtcNow;
        var md = new StringBuilder();

        // 1. Crawl the public front page/category pages, then inspect each candidate article.
        var crawler = new WebCrawler(fetcher, log);
        var results = await Task.WhenAll(cfg.Sources.Where(s => s.Enabled).Select(async s =>
        {
            try { return (s, items: await crawler.CrawlSourceAsync(s, ct), error: (string?)null); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { return (s, items: new List<FeedItem>(), error: ex.Message); }
        }));

        md.AppendLine("## 1. Burimet (crawling i faqeve)").AppendLine();
        md.AppendLine("| Portali | Lajme | Më i fundit | Statusi |").AppendLine("|---|---:|---|---|");
        foreach (var (s, items, error) in results)
        {
            var newest = items.Where(i => i.Published != null).Select(i => i.Published!.Value).DefaultIfEmpty().Max();
            var age = newest == default ? "–" : $"{(now - newest).TotalHours:0.#} orë më parë";
            var status = error != null ? $"❌ {error}" : items.Count == 0 ? "❌ asnjë artikull (kontrollo HomeUrl/CategoryUrls)" : "✅";
            md.AppendLine($"| {s.Name} | {items.Count} | {age} | {status} |");
        }
        var all = results.SelectMany(r => r.items).ToList();
        var okSources = results.Count(r => r.items.Count > 0);

        // 2. Stories
        var fresh = all.Where(i => i.Published is null || now - i.Published.Value <= TimeSpan.FromHours(cfg.MaxStoryAgeHours)).ToList();
        var stories = StoryClusterer.Cluster(fresh, cfg.ClusterSimilarity).OrderByDescending(s => s.Score(now)).ToList();
        md.AppendLine().AppendLine($"## 2. Historitë ({stories.Count} nga {fresh.Count} lajme të {cfg.MaxStoryAgeHours} orëve të fundit)").AppendLine();
        md.AppendLine("| # | Burime | Titulli |").AppendLine("|---:|---:|---|");
        foreach (var (s, i) in stories.Take(15).Select((s, i) => (s, i + 1)))
            md.AppendLine($"| {i} | {s.SourceCount} | {Esc(s.Lead.Title)} <br><sub>{string.Join(", ", s.Items.Select(x => x.Source).Distinct())}</sub> |");

        // 3. Text extraction for the top stories
        var fetcherArticles = new ArticleFetcher(fetcher, log);
        md.AppendLine().AppendLine($"## 3. Leximi i teksteve (top {topStories} histori)").AppendLine();
        var extractionOk = 0;
        var extractionTotal = 0;
        foreach (var story in stories.Take(topStories))
        {
            md.AppendLine($"### {Esc(story.Lead.Title)}").AppendLine();
            var picks = story.Items.GroupBy(i => i.Source).Select(g => g.First()).Take(cfg.MaxSourcesPerStory).ToList();
            foreach (var item in picks)
            {
                extractionTotal++;
                var text = await fetcherArticles.FetchAsync(item, ct);
                var len = text?.Text.Length ?? 0;
                var words = text == null ? 0 : TextUtil.WordCount(text.Text);
                var ok = words >= 120;
                if (ok) extractionOk++;
                var preview = text == null ? "(asgjë)" : TextUtil.TruncateWords(text.Text.Replace('\n', ' '), 220);
                md.AppendLine($"- {(ok ? "✅" : "⚠️")} **{item.Source}** — {words} fjalë · [{Esc(item.Title)}]({item.Url})");
                md.AppendLine($"  <sub>{Esc(preview)}</sub>");
            }
            md.AppendLine();
        }

        md.AppendLine("## Përfundimi").AppendLine();
        md.AppendLine($"- Portale që punojnë: **{okSources}/{results.Length}**");
        md.AppendLine($"- Lajme të lexuara: **{all.Count}**, histori të freskëta: **{stories.Count}**");
        md.AppendLine($"- Tekste të lexuara mirë (≥120 fjalë): **{extractionOk}/{extractionTotal}**");
        md.AppendLine();
        md.AppendLine("Kjo provë nuk përdor AI dhe nuk shkruan asgjë në faqe.");

        var report = md.ToString();
        Console.WriteLine();
        Console.WriteLine(report);
        if (reportPath != null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, "# Prova e LajmeBot (pa AI)\n\n" + report);
        }
        (fetcher as IDisposable)?.Dispose();
        return okSources > 0 ? 0 : 1;
    }

    private static string Esc(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
