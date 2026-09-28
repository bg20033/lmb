# LajmeBot

Backend automatik që publikon lajme në **Raporti Sot**, **InfoKosove24**, **Kosova Aktuale**, **Kosova Fakt** dhe **Lajme për Kosovë**.
Punon falas në **GitHub Actions** (pa server, pa databazë) dhe është shkruar në **.NET 8**, pa asnjë paketë NuGet.

```
Faqet kryesore + kategoritë ──► linkat e artikujve ──► hapja e artikujve të plotë
      ──► grupim i lajmeve të njëjta ──► leximi i fakteve nga 2–4 burime
      ──► AI shkruan artikull origjinal OSE modaliteti licensed-copy publikon tekstin e licencuar
      ──► kontroll: skema e faqes, gjatësia, kopjimi i teksteve
      ──► .md + ilustrim PNG në src/content/news  ──► astro build + SEO audit
      ──► git push  ──► Vercel/Netlify e publikon vetë
```

## Si punon

1. **Zbulimi.** Hyn te faqja kryesore dhe faqet e kategorive të çdo portali në `config/bot.json`, gjen linkat e artikujve të rinj dhe hap deri në 24 artikuj për portal. Nga çdo artikull nxjerr titullin, datën, përshkrimin dhe tekstin e plotë. RSS/Atom nuk përdoret.
2. **Grupimi.** Titujt që flasin për të njëjtën ngjarje bashkohen në një "histori" (me stemming të thjeshtë shqip). Historitë me më shumë burime dhe më të freskëta vijnë të parat.
3. **Faktet / teksti i licencuar.** Për çdo histori hap 2–4 artikuj burimorë. Në modalitetin AI, teksti përdoret vetëm për fakte; në `licensed-copy`, publikohet teksti i plotë i burimit të licencuar.
4. **Shkrimi.** AI shkruan artikull të ri me fjalët e veta; ose `licensed-copy` punon pa API/AI, merr tekstin e plotë nga burimi me licencë dhe e publikon me emrin e portalit dhe linkun origjinal. Në `licensed-copy`, çdo histori shpërndahet në mënyrë të barabartë në një nga pesë faqet.
5. **Kontrolli.** Nëse titulli/përshkrimi nuk i plotëson rregullat e faqes, ose nëse teksti ka ≥12 fjalë radhazi të kopjuara nga një burim, draft-i refuzohet dhe AI e rishkruan një herë.
6. **Publikimi.** Shkruan `src/content/news/<slug>.md` dhe një ilustrim abstrakt `src/assets/news/<slug>.png`. Në `licensed-copy`, fundi ka **Burimi origjinal**, emrin e portalit, linkun dhe shënimin e licencës.
7. **Siguria.** Workflow-i e ndërton faqen dhe e kalon SEO audit **para** push-it. Nëse diçka dështon, ajo faqe nuk publikohet.
8. **Kujtesa.** `state/state.json` mban mend çka u publikua (10 ditë), që e njëjta histori të mos shkruhet dy herë.

## Konfigurimi (një herë)

1. **Krijo repo-t e faqeve në GitHub** (`raporti-sot` ekziston). Për tri të tjerat:
   ```bash
   cd infokosove24 && git init -b main && git add -A && git commit -m "Faqja e parë"
   git remote add origin https://github.com/bg20033/infokosove24.git && git push -u origin main
   ```
   (njëjtë për `kosova-aktuale` dhe `kosova-fakt`) dhe lidhi me Vercel, secilën me `PUBLIC_SITE_URL` të vet.
2. **Krijo repo për bot-in** (p.sh. `bg20033/lajme-bot`) dhe shtyje këtë folder. Nëse repo është **publik**, minutat e GitHub Actions janë falas pa limit. Nëse është privat, limiti është 2 000 min/muaj, që mjafton për rreth 8–9 ekzekutime në ditë.
3. **Çelësi i AI.** Në repo-n e bot-it: *Settings → Secrets and variables → Actions → New repository secret*
   - `OPENROUTER_API_KEY`: nga https://openrouter.ai/keys. Është standardi, me një çelës i ke të gjitha modelet. Mbushe llogarinë me pak kredit.
   - ose `ANTHROPIC_API_KEY`: nëse do Claude direkt, dhe te config vendos `"Provider": "anthropic"`, `"Model": "claude-sonnet-5"`.
   - `SITES_TOKEN`: GitHub fine-grained token (*Settings → Developer settings → Fine-grained tokens*) me qasje vetëm te 4 repo-t e faqeve, leja **Contents: Read and write**.
4. Ndrysho në `config/bot.json`:
   - `UserAgent` → vendos email-in tënd real
   - `Repo` e secilës faqe, nëse emrat në GitHub janë ndryshe
5. **Provat** (*Actions → Publiko lajme → Run workflow*, zgjedh `mode`):

   | mode | AI? | Sekretet | Çka bën |
   |---|---|---|---|
   | `discover` | jo | asnjë | Lexon portalet live, grupon lajmet, lexon tekstet dhe shfaq raport (cili portal punon, sa fjalë u lexuan). Nuk prek faqet. |
   | `test` | jo | `SITES_TOKEN` (s'duhet nëse repo-t e faqeve janë publike) | Shkruan artikuj **TEST** në kopje të faqeve, gjeneron ilustrimet, i ndërton dhe i kalon SEO audit. **Nuk publikon** dhe nuk e prek kujtesën e bot-it. |
   | `dry-run` | po | vetëm çelësi i AI | AI shkruan artikuj të vërtetë (1 histori nëse s'jep numër). I sheh të plotë te **Summary** e ekzekutimit, ilustrimet te **Artifacts → drafts**. Nuk publikon e nuk e prek kujtesën. |
   | `publish` | po | të dyja | Publikon artikuj të shkruar me AI. |
   | `licensed-copy` | jo | `SITES_TOKEN` | Publikon tekstin e plotë të burimit të licencuar, me atribim + link. Ky është modaliteti automatik. |

   Rendi i rekomanduar: `discover` → `test` → `dry-run` → `publish`.
   Derisa të mos i vendosësh sekretet, ekzekutimet automatike çdo 2 orë mbarojnë pa bo asgjë (pa gabime).

Pas kësaj punon vetë çdo 2 orë, prej orës 07 deri në 23 me orën e Kosovës (`cron` në `.github/workflows/publish.yml`).

## Modeli dhe kostoja

Modeli ndërrohet te `config/bot.json` → `Ai.Model`, ose vetëm për një ekzekutim te fusha **model** e workflow-it.
Çmimet për milion token (hyrje / dalje, shtator 2026). Një artikull merr afërsisht 5k token hyrje dhe 2k dalje:

| Modeli (OpenRouter) | Çmimi | Për artikull | ~54 artikuj/ditë |
|---|---|---|---|
| `google/gemini-3.8-flash` (standardi) | $0.75 / $3.75 | ~1 cent | ~$0.60/ditë |
| `qwen/qwen3.8-flash` | $0.15 / $0.47 | ~0.2 cent | ~$0.10/ditë |
| `deepseek/deepseek-v4.1-flash` (rezerva) | $0.035 / $0.29 | ~0.08 cent | ~$0.05/ditë |
| `claude-sonnet-5` (Anthropic direkt) | $2 / $10 | ~3 cent | ~$1.60/ditë |

Modelet më të lira shpesh e shkruajnë shqipen më dobët. Provoji me `dry-run` (fusha **model**) dhe krahasoji para se ta ndërrosh standardin.
Nëse modeli kryesor bie, OpenRouter kalon vetë te `FallbackModels`. Log-u e shkruan koston e çdo ekzekutimi.

## Komandat lokale

```bash
dotnet run --project src/LajmeBot -- selftest                        # testet, pa internet
dotnet run --project src/LajmeBot -- discover --top 5                # provë live e portaleve, pa AI, s'shkruan asgjë
dotnet run --project src/LajmeBot -- run --sites-root .. --dry-run   # me OPENROUTER_API_KEY: draft-et në out/drafts, s'publikon
dotnet run --project src/LajmeBot -- run --sites-root .. --dry-run --model deepseek/deepseek-v4.1-flash
dotnet run --project src/LajmeBot -- run --sites-root .. --site kosova-fakt --max-stories 1
dotnet run --project src/LajmeBot -- run --sites-root /tmp/kopje --fixtures tests/fixtures --mock-ai   # provë offline
dotnet run --project src/LajmeBot -- run --licensed-copy --sites-root .. --dry-run # provë pa API, pa publikim
```

`--sites-root ..` punon kur `lajme-bot` rri në të njëjtin folder me faqet (`lajme/raporti-sot`, `lajme/infokosove24`…).
**Mos e përdor `--mock-ai` në faqet e vërteta**: krijon artikuj "TEST".

## Struktura

```
config/bot.json                  burimet, faqet, stili i secilës faqe, modeli, limitet
state/state.json                 kujtesa e bot-it (commit-ohet automatikisht)
src/LajmeBot/
  Program.cs                     CLI dhe rrjedha: discover → cluster → write → validate → publish
  Http/Fetcher.cs                HttpClient me pauzë për host dhe retry 429/5xx
  Discovery/Discovery.cs         crawling i listimeve + API publike e nxjerrjes së artikujve
  Discovery/HtmlArticleExtractor.cs  extractor i fortë HTML (JSON-LD/metadata → artikull i vlerësuar → pastrim i widget-eve)
  Stories/Stories.cs             grupimi i lajmeve dhe kujtesa
  Ai/OpenRouterWriter.cs         OpenRouter (function calling, me kalim automatik në JSON)
  Ai/Writers.cs                  Claude direkt (Messages API), prompt-et + mock
  Publishing/SitePublisher.cs    validimi sipas skemës Astro, shkrimi i .md, autori
  Images/HeroImageGenerator.cs   ilustrime PNG në C# të pastër (8 motive)
  DiscoverCommand.cs             komanda "discover" (provë pa AI)
  SelfTest.cs                    testet
tests/fixtures/                  faqe listimi dhe artikuj për testim offline
.github/workflows/publish.yml    orari, build, SEO audit, push
```

## Shto një portal ose një faqe të re

- **Portal:** shto një rresht te `Sources` (`Name`, `HomeUrl`, `CategoryUrls`, `MaxArticlePages`).
- **Faqe:** shto një objekt te `Sites` (`Slug`, `Repo`, `Accent`, `Style`…). Faqja duhet të ketë të njëjtën strukturë Astro (`src/content/news`, `src/assets/news`, `src/content/authors.json`).

## Rregullat editoriale që bot-i i ndjek

- Asnjë tekst ose foto nuk kopjohet nga portalet: merren vetëm faktet, shkruhen me fjalë të reja dhe lidhen me burimin.
- Asnjë fakt, citim apo emër i shpikur. Kur burimet s'mjaftojnë, AI e anashkalon historinë (`skip`).
- Prezumimi i pafajësisë, pa të dhëna për viktima ose të mitur.
- Çdo artikull e thotë hapur që është përgatitur me AI dhe i liston burimet.

Megjithatë **AI mund të gabojë**. Shiko herë pas here çka publikohet, sidomos për tema politike dhe gjyqësore.
Nëse do kontroll para publikimit, vendos `"PublishAsDraft": true` te faqja në `config/bot.json`: artikujt shkruhen me `draft: true` dhe dalin në faqe vetëm kur e heq atë rresht.
