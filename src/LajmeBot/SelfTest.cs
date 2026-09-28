using LajmeBot.Ai;
using LajmeBot.Discovery;
using LajmeBot.Http;
using LajmeBot.Images;
using LajmeBot.Publishing;
using LajmeBot.Stories;
using LajmeBot.Text;

namespace LajmeBot;

/// <summary>Dependency-free checks: `dotnet run --project src/LajmeBot -- selftest`.</summary>
public static class SelfTest
{
    private static int _failed, _passed;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) { _passed++; Console.WriteLine($"  ✓ {name}"); }
        else { _failed++; Console.WriteLine($"  ✗ {name}{(detail is null ? "" : " — " + detail)}"); }
    }

    public static int Run()
    {
        Console.WriteLine("LajmeBot self-test");

        // Text
        Check("slug transliterates Albanian letters", TextUtil.Slugify("Vëllazëria Çlirimtare e Kosovës!") == "vellazeria-clirimtare-e-kosoves", TextUtil.Slugify("Vëllazëria Çlirimtare e Kosovës!"));
        Check("fold strips diacritics", TextUtil.Fold("ËÇëç") == "ecec");
        var a = TextUtil.Tokens("Kuvendi miraton rezolutën për dialogun me Serbinë");
        var b = TextUtil.Tokens("Kuvendi i Kosovës miratoi rezolutën për dialogun");
        var c = TextUtil.Tokens("Kombëtarja fiton ndeshjen miqësore në Tiranë");
        Check("similar headlines cluster", TextUtil.Jaccard(a, b) >= 0.34, TextUtil.Jaccard(a, b).ToString("0.00"));
        Check("stemmer collapses Albanian endings", TextUtil.Stem("pagën") == TextUtil.Stem("paga") && TextUtil.Stem("rritet") == TextUtil.Stem("rrit"));
        Check("same story, different wording", TextUtil.Jaccard(TextUtil.Tokens("Qeveria rrit pagën minimale nga viti i ardhshëm"), TextUtil.Tokens("Paga minimale rritet, reagojnë bizneset")) >= 0.34);
        Check("different headlines do not", TextUtil.Jaccard(a, c) < 0.1);
        Check("copy guard finds long shared runs",
            TextUtil.LongestSharedRun("Ai tha se Kuvendi i Kosovës ka miratuar sot rezolutën për dialogun me shumicë votash dhe pa kundërshtime",
                new[] { "Kuvendi i Kosovës ka miratuar sot rezolutën për dialogun me shumicë votash dhe pa kundërshtime." }) >= 12);
        Check("copy guard ignores paraphrase",
            TextUtil.LongestSharedRun("Deputetët votuan pro dokumentit që përcakton qëndrimin e shtetit në bisedime",
                new[] { "Kuvendi i Kosovës ka miratuar sot rezolutën për dialogun me shumicë votash." }) == 0);
        Check("quotes of people are not counted as copying",
            TextUtil.LongestSharedRun(TextUtil.WithoutShortQuotes("Ministri deklaroi: “Kuvendi i Kosovës ka miratuar sot rezolutën për dialogun me shumicë votash dhe pa kundërshtime”, tha ai."),
                new[] { "Kuvendi i Kosovës ka miratuar sot rezolutën për dialogun me shumicë votash dhe pa kundërshtime." }) == 0);
        Check("copy guard reports the passage",
            TextUtil.LongestSharedPassage("Sot Kuvendi i Kosovës ka miratuar sot rezolutën për dialogun me shumicë votash dhe pa kundërshtime",
                new[] { "Kuvendi i Kosovës ka miratuar sot rezolutën për dialogun me shumicë votash dhe pa kundërshtime." }).passage.StartsWith("kuvendi i kosoves"));
        Check("truncate keeps word boundary", TextUtil.TruncateWords("një dy tre katër pesë gjashtë", 14) == "një dy tre…");
        Check("yaml string escaping", TextUtil.YamlString("Ai tha \"po\"") == "\"Ai tha \\\"po\\\"\"");

        // Feeds
        const string rss = """
            <?xml version="1.0" encoding="UTF-8"?><rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/"><channel><title>T</title>
            <item><title>Kuvendi miraton rezolutën për dialogun me Serbinë</title><link>https://example.com/2026/09/27/kuvendi/?utm_source=rss</link>
            <pubDate>Sun, 27 Sep 2026 16:42:00 +0200</pubDate><description><![CDATA[<p>Kuvendi ka miratuar sot rezolutën.</p>]]></description></item>
            <item><title>Shkurt</title><link>https://example.com/x</link></item></channel></rss>
            """;
        var items = FeedReader.Parse("Test", rss, "https://example.com/").ToList();
        Check("rss parses items and skips junk", items.Count == 1);
        Check("rss strips query string", items.Count == 1 && items[0].Url == "https://example.com/2026/09/27/kuvendi/");
        Check("rss date with offset", items.Count == 1 && items[0].Published == new DateTimeOffset(2026, 9, 27, 14, 42, 0, TimeSpan.Zero));
        Check("rss summary is plain text", items.Count == 1 && items[0].Summary == "Kuvendi ka miratuar sot rezolutën.");
        const string atomXml = """
            <feed xmlns="http://www.w3.org/2005/Atom"><entry><title>Qeveria miraton buxhetin për vitin e ardhshëm</title>
            <link rel="alternate" href="/lajme/buxheti"/><updated>2026-09-27T10:00:00Z</updated><summary>Buxheti u miratua.</summary></entry></feed>
            """;
        var atomItems = FeedReader.Parse("Atom", atomXml, "https://example.org/").ToList();
        Check("atom parses relative links", atomItems.Count == 1 && atomItems[0].Url == "https://example.org/lajme/buxheti");
        Check("RFC822 named zone", FeedReader.ParseDate("Sun, 27 Sep 2026 18:42:00 CEST") == new DateTimeOffset(2026, 9, 27, 16, 42, 0, TimeSpan.Zero));

        // Website crawling (the production discovery path; RSS is not used there)
        var listingHtml = "<nav><a href=\"/sport/\">Sport</a></nav><article><a href=\"/kosove/1001/?utm_source=home\"><span>Kuvendi miraton rezolutën për dialogun me Serbinë</span></a></article>";
        var crawledLinks = WebCrawler.ExtractArticleLinks(listingHtml, "https://example.com/").ToList();
        Check("crawler finds same-site article links", crawledLinks.Count == 1 && crawledLinks[0].Url == "https://example.com/kosove/1001/" && crawledLinks[0].Title.StartsWith("Kuvendi"));

        // Extraction
        var body = string.Concat(Enumerable.Repeat("Kjo është një fjali e gjatë e trupit të artikullit me mjaft fjalë për testim. ", 6));
        var jsonLdHtml = "<html><head><script type=\"application/ld+json\">{\"@graph\":[{\"@type\":\"NewsArticle\",\"articleBody\":\"" + body + "\"}]}</script></head><body></body></html>";
        Check("extract JSON-LD articleBody", ArticleFetcher.Extract(jsonLdHtml).StartsWith("Kjo është"));
        var metadataHtml = "<script type=\"application/ld+json\">{\"@type\":\"NewsArticle\",\"headline\":\"Kuvendi miraton rezolutën për dialogun me Serbinë\",\"description\":\"Përshkrim i shkurtër për artikullin e sotëm.\",\"datePublished\":\"2026-09-28T10:15:00+02:00\"}</script>";
        var metadata = ArticleFetcher.ExtractMetadata(metadataHtml);
        Check("crawler reads article metadata", metadata.Title?.StartsWith("Kuvendi") == true && metadata.Published == new DateTimeOffset(2026, 9, 28, 8, 15, 0, TimeSpan.Zero));
        var wpHtml = "<html><body><nav><p>Menu menu menu menu menu menu menu menu menu menu menu menu menu menu</p></nav><div class=\"entry-content\"><p>" + body + "</p><p>" + body + "</p><div class=\"share\"><p>Shpërndaje</p></div></div></body></html>";
        var wp = ArticleFetcher.Extract(wpHtml);
        Check("extract entry-content paragraphs", wp.Contains("Kjo është") && !wp.Contains("Menu"));
        var navHtml = "<html><body><header><nav><p>Kryefaqja Lajme Sport Ekonomi Kultura Bota Showbiz Magazina Video Foto Kontakt</p></nav></header><p>" + body + "</p><p>" + body + "</p><footer><p>Të gjitha të drejtat e rezervuara nga portali, ndalohet kopjimi pa leje.</p></footer></body></html>";
        var nav = ArticleFetcher.Extract(navHtml);
        Check("extract ignores nav/header/footer", nav.Contains("Kjo është") && !nav.Contains("Kryefaqja") && !nav.Contains("drejtat"));
        var articleHtml = """
            <html><head><meta content="Titulli &amp; i saktë" property="og:title"><meta name="description" content="Përshkrimi i artikullit."></head><body>
            <main><div class="article__content"><p>Ky është paragrafi i parë i artikullit me informacion të qartë dhe të dobishëm për lexuesit që ndjekin zhvillimin e ngjarjes.</p>
            <p>Paragrafi i dytë jep hollësi shtesë, shpjegon reagimet dhe e vendos zhvillimin në kontekstin e debatit publik.</p>
            <p>Në pjesën e fundit përmenden hapat e ardhshëm dhe çfarë pritet nga institucionet gjatë ditëve në vijim.</p>
            <section class="related-news"><p>Ky tekst i lajmeve të ngjashme nuk duhet të hyjë kurrë në artikullin e nxjerrë nga roboti.</p></section></div></main></body></html>
            """;
        var extractedArticle = ArticleFetcher.Extract(articleHtml);
        Check("extractor chooses article prose over related widgets", extractedArticle.Contains("Paragrafi i dytë") && !extractedArticle.Contains("lajmeve të ngjashme"));
        var fallbackMetadata = ArticleFetcher.ExtractMetadata(articleHtml);
        Check("extractor reads Open Graph metadata in either attribute order", fallbackMetadata.Title == "Titulli & i saktë" && fallbackMetadata.Description == "Përshkrimi i artikullit.");
        var arrayTypeJson = "<script type=\"application/ld+json\">{\"@type\":[\"NewsArticle\",\"Thing\"],\"headline\":\"Titull nga skema\",\"datePublished\":\"2026-09-28T12:00:00Z\"}</script>";
        Check("extractor supports JSON-LD type arrays", ArticleFetcher.ExtractMetadata(arrayTypeJson).Title == "Titull nga skema");

        // OpenRouter / OpenAI-style responses
        var toolMsg = System.Text.Json.Nodes.JsonNode.Parse("""{"tool_calls":[{"type":"function","function":{"name":"publish_article","arguments":"{\"title\":\"T\"}"}}]}""");
        Check("openrouter: tool call arguments as string", OpenRouterWriter.ExtractArguments(toolMsg)?["title"]?.GetValue<string>() == "T");
        var fenced = System.Text.Json.Nodes.JsonNode.Parse("""{"content":"Ja artikulli:\n```json\n{\"title\": \"X\", \"skip\": false}\n```"}""");
        Check("openrouter: JSON inside a fenced reply", OpenRouterWriter.ExtractArguments(fenced)?["title"]?.GetValue<string>() == "X");
        Check("openrouter: no JSON -> null", OpenRouterWriter.ExtractArguments(System.Text.Json.Nodes.JsonNode.Parse("""{"content":"Më vjen keq."}""")) is null);

        // Plain-text output format (free models)
        var txt = "Ja artikulli:\n**TITLE:** Kuvendi miraton rezolutën për dialogun\nDESCRIPTION: Deputetët votuan dokumentin pas një debati të gjatë në seancë.\nCATEGORY: Politikë\nTAGS: Kosova, Kuvendi, #Dialogu\nIMAGE: columns\nBODY:\n## Seanca\n\nTeksti i artikullit.\n\n## Votimi\n\nMë shumë tekst.";
        var td = Prompts.ParseTextDraft(txt);
        Check("text format: fields parsed", td is { Skip: false } && td.Title == "Kuvendi miraton rezolutën për dialogun" && td.Category == "politike" && td.Tags.Count == 3 && td.ImageMotif == "columns");
        Check("text format: body kept with headings", td != null && td.BodyMarkdown.StartsWith("## Seanca") && td.BodyMarkdown.Contains("Më shumë tekst"));
        Check("text format: skip", Prompts.ParseTextDraft("SKIP: yes — vetëm thashetheme") is { Skip: true });
        Check("text format: garbage -> null", Prompts.ParseTextDraft("Më vjen keq, nuk mundem.") is null);

        // Clustering + state
        var feedItems = new List<FeedItem>
        {
            new("Koha", "Kuvendi miraton rezolutën për dialogun me Serbinë", "https://a/1", "", DateTimeOffset.UtcNow),
            new("Insajderi", "Kuvendi i Kosovës miratoi rezolutën për dialogun", "https://b/1", "", DateTimeOffset.UtcNow),
            new("Sinjali", "Kombëtarja fiton ndeshjen miqësore në Tiranë", "https://c/1", "", DateTimeOffset.UtcNow),
        };
        var stories = StoryClusterer.Cluster(feedItems, 0.34);
        Check("clusterer groups same event", stories.Count == 2 && stories.Any(s => s.SourceCount == 2));
        var st = new BotState();
        st.Published.Add(new PublishedStory { At = DateTimeOffset.UtcNow, SourceUrls = new() { "https://a/1" }, Tokens = new() });
        Check("state remembers published stories", st.AlreadyCovered(stories.First(s => s.SourceCount == 2), 0.34));

        // Image
        var tmp = Path.Combine(Path.GetTempPath(), $"lajmebot-{Guid.NewGuid():N}");
        foreach (var motif in HeroImageGenerator.Motifs)
        {
            var path = Path.Combine(tmp, motif + ".png");
            HeroImageGenerator.Generate(path, "seed-" + motif, "politike", motif, "#0057b8", "#081e48", 0.35);
            var bytes = File.ReadAllBytes(path);
            Check($"image '{motif}' is a PNG ({bytes.Length / 1024} KB)", bytes.Length > 5000 && bytes[1] == 'P' && bytes[2] == 'N' && bytes[3] == 'G');
        }

        // Validation
        var site = new SiteConfig { Slug = "t", Name = "Test", Root = tmp };
        var pub = new SitePublisher(site, new AiConfig { MinWords = 50 }, new Log(false));
        var okBody = string.Join("\n\n", Enumerable.Range(0, 8).Select(i => $"Paragrafi {i} përmban fakte të rishkruara me fjalë krejt të reja dhe të ndryshme për lexuesin."));
        var draft = new ArticleDraft(false, null, "Kuvendi miraton rezolutën për dialogun", "Deputetët votuan dokumentin që përcakton qëndrimin e institucioneve në bisedimet me Serbinë, raportojnë mediat.", "politike", new() { "Kosova" }, okBody, "columns");
        var src = new List<SourceText> { new("Koha", "Kuvendi miraton", "https://a/1", "Kuvendi i Kosovës ka miratuar sot rezolutën për dialogun me shumicë votash.") };
        Check("valid draft passes", pub.Validate(draft, src).Count == 0, string.Join("; ", pub.Validate(draft, src)));
        Check("bad category rejected", pub.Validate(draft with { Category = "lifestyle" }, src).Count == 1);
        Check("copied text rejected", pub.Validate(draft with { BodyMarkdown = okBody + "\n\nThanë se Kuvendi i Kosovës ka miratuar sot rezolutën për dialogun me shumicë votash dhe gjithçka." }, src).Any(p => p.Contains("copies")));
        Check("Kosovo time zone available", SitePublisher.ToKosovoTime(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero)).Offset == TimeSpan.FromHours(2));

        try { Directory.Delete(tmp, true); } catch { }
        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
