using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P9Harness
{
    public static int AuditPlanReply(string[] args)
    {
        string Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException(key); }
        var acquired = JsonSerializer.Deserialize<WebAcquisition>(File.ReadAllText(Value("--sources")), Wire.Options)!;
        var reply = JsonSerializer.Deserialize<Result<ProviderReply>>(File.ReadAllText(Value("--reply")), Wire.Options)!;
        var parsed = new PlanParser().Parse(reply.Value!.Text);
        if (!parsed.Ok) { Console.WriteLine(parsed.Error!.Message); return 1; }
        var input = new PlanningInput(Value("--goal"), "初学者", "以实际取得资料为边界入门；资料未覆盖部分明确列为缺口，不推测已读。", 120, "Adaptive", acquired.References);
        var result = PlanRules.Validate(Guid.NewGuid(), input, parsed.Value!, acquired.References.Select((r, i) => new PlanSource("s" + (i + 1), r, true)).ToArray(), reply.Value.Usage);
        Console.WriteLine(result.Ok ? "Plan rules passed; content review still required" : result.Error!.Message);
        return result.Ok ? 0 : 1;
    }
    public static async Task<int> AuditFetch(string[] args)
    {
        string Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException(key); }
        var output = Value("--evidence");
        if (File.Exists(output)) throw new IOException("Audit evidence already exists");
        var url = WebAddress.Parse(Value("--url")).AbsoluteUri;
        var timer = Stopwatch.StartNew();
        using var client = DeepSeekWebSources.CreateClient();
        var web = new DeepSeekWebSources(new SqliteSettingsStore(Value("--data-root")), client);
        try
        {
            var page = await web.FetchAsync(url, default);
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                page, seconds = timer.Elapsed.TotalSeconds, fetchCalls = 1, modelCalls = 0,
                usage = "not returned by fetch service", qualityReviewPending = true
            }, Wire.Options));
            Console.WriteLine($"P9 single fetch: {page.Content.Length} characters; content review pending");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
            {
                url, seconds = timer.Elapsed.TotalSeconds, fetchCalls = 1, modelCalls = 0,
                usage = "unknown", error = ex.Message
            }, Wire.Options));
            Console.WriteLine("P9 single fetch failed; see sanitized evidence");
            return 1;
        }
    }
    public static async Task<int> AuditSelection(string[] args)
    {
        string Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException(key); }
        var input = Value("--cached-root"); var output = Value("--evidence");
        if (File.Exists(output)) throw new IOException("Audit evidence already exists");
        var planPath = Path.Combine(input, "confirmed-plan.json");
        var draft = JsonSerializer.Deserialize<PlanningDraft>(await File.ReadAllTextAsync(planPath), Wire.Options)!;
        var plan = draft.Plan ?? throw new InvalidDataException("Missing frozen plan");
        // No web client, credentials or model provider is constructed in this offline path.
        var knowledge = new KnowledgeStore(input);
        var lessons = new List<object>();
        foreach (var node in plan.Candidate.Nodes.Where(n => n.Type == "theory"))
        {
            var selected = await CourseMaterials.SelectAsync(knowledge, plan, node, default);
            var fragments = new List<object>();
            foreach (var source in selected)
            {
                var resolved = await knowledge.ResolveAsync(source.Reference);
                if (resolved.Text is null) throw new IOException("Frozen source unavailable");
                fragments.Add(new { source.Key, source.Reference, text = resolved.Text });
            }
            lessons.Add(new { node.Key, node.Title, node.Objectives, fragments });
        }
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new
        {
            planSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(planPath))),
            applicationSha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(typeof(LessonSourceSelector).Assembly.Location))),
            networkCalls = 0, modelCalls = 0, qualityReviewPending = true, lessons
        }, Wire.Options));
        Console.WriteLine($"P9 offline selection audit: {lessons.Count} lessons; no network/model calls; content review pending");
        return 0;
    }
    public static int AuditSource(string[] args)
    {
        string Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException(key); }
        var path = Value("--page"); if (new FileInfo(path).Length > 512 * 1024) return 64;
        var page = JsonSerializer.Deserialize<WebPage>(File.ReadAllText(path), Wire.Options)!;
        var assessment = TeachingPages.Assess(page, Value("--goal"));
        var expected = !args.Contains("--expect-rejected");
        File.WriteAllText(Value("--evidence"), JsonSerializer.Serialize(new { page.Url, assessment, expected, passed = assessment.UsableForTeaching == expected, networkCalls = 0 }, Wire.Options));
        Console.WriteLine($"P9 cached source audit: {assessment.PageKind}, usable={assessment.UsableForTeaching}; network calls 0");
        return assessment.UsableForTeaching == expected ? 0 : 1;
    }
    public static async Task<int> Run(string[] args)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var credentialRoot = Value("--data-root"); var output = Value("--evidence-root"); var scenario = Value("--scenario");
        var goals = new Dictionary<string, string> { ["algorithms"] = "我想学习算法竞赛", ["linear"] = "我想学习线性代数", ["cpp"] = "我想学习C++" };
        var resume = args.Contains("--resume-acquisition"); var sourceOnly = args.Contains("--source-only");
        var broadPlan = args.Contains("--broad-plan-only");
        if (output is null || credentialRoot is null || scenario is null || !goals.TryGetValue(scenario, out var goal) || Directory.Exists(output) && !resume || resume && !File.Exists(Path.Combine(output, "acquisition.json"))) return 64;
        Directory.CreateDirectory(output); var checks = new List<string>(); var timings = new List<object>();
        var calls = new List<object>(); var webCalls = new List<object>(); var total = Stopwatch.StartNew(); var previousAcquisitionSeconds = 0d;
        void Check(bool value, string name) { if (!value) throw new InvalidDataException(name); checks.Add(name); }
        async Task Save(string name, object value) => await File.WriteAllTextAsync(Path.Combine(output, name), JsonSerializer.Serialize(value, Wire.Options));
        var modules = new[] { typeof(PlanRules).Assembly, typeof(KnowledgeStore).Assembly, typeof(PlanDocument).Assembly }
            .Select(a => new { name = Path.GetFileName(a.Location), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).ToArray();
        try
        {
            var credentials = new SqliteSettingsStore(credentialRoot); var settings = new SettingsService(credentials);
            var web = new RecordedWeb(new DeepSeekWebSources(credentials, DeepSeekWebSources.CreateClient()), webCalls, output);
            Directory.CreateDirectory(Path.Combine(output, "library"));
            var knowledge = new KnowledgeStore(output, web); var timer = Stopwatch.StartNew();
            WebAcquisition acquired;
            if (resume)
            {
                using var cached = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "acquisition.json")));
                Check(cached.RootElement.GetProperty("goal").GetString() == goal, "cached-goal-matches");
                acquired = JsonSerializer.Deserialize<WebAcquisition>(await File.ReadAllTextAsync(Path.Combine(output, "sources.json")), Wire.Options)!;
                webCalls.AddRange(cached.RootElement.GetProperty("webCalls").EnumerateArray().Select(e => (object)e.Clone()));
                if (args.Contains("--reindex-acquisition"))
                {
                    var pages = new List<WebPage>();
                    for (var i = 1; i <= 3; i++)
                    {
                        var file = Path.Combine(output, $"page-{i}.json"); if (!File.Exists(file)) continue;
                        Check(new FileInfo(file).Length <= 512 * 1024, "cached-page-size");
                        var page = JsonSerializer.Deserialize<WebPage>(await File.ReadAllTextAsync(file), Wire.Options)!;
                        var assessment = TeachingPages.Assess(page, goal);
                        if (assessment.UsableForTeaching) pages.Add(page with { Content = TeachingPages.Extract(page, assessment), OriginalContent = page.Content, Assessment = assessment });
                    }
                    Check(pages.Count > 0, "cached-real-body-remains-eligible");
                    var library = await knowledge.CurrentAsync() ?? throw new InvalidDataException("Missing cached library");
                    var suffix = Guid.NewGuid().ToString("N");
                    File.Copy(Path.Combine(output, "sources.json"), Path.Combine(output, "sources-before-reindex-" + suffix + ".json"));
                    File.Copy(Path.Combine(output, "acquisition.json"), Path.Combine(output, "acquisition-before-reindex-" + suffix + ".json"));
                    if (broadPlan)
                    {
                        var site = (await knowledge.CollectionsAsync(library.Id)).Single(c => c.Collection.SourceKind == "site");
                        var index = new SiteDirectory(site.Manifest.DirectoryHash, site.Collection.CreatedAt,
                            site.Manifest.Entries.Select(e => new WebHit(e.Heading, e.Locator)).ToArray(), site.Manifest.Entries.Count, site.Manifest.Entries.Count);
                        var snapshot = await knowledge.SaveSiteAsync(library.Id, index, pages, default);
                        acquired = new(snapshot.References.Take(24).ToArray(), acquired.Report + "\n离线保留原目录与原网页快照；未新增抓取。");
                    }
                    else acquired = new(await knowledge.SaveWebAsync(library.Id, pages, goal, default), acquired.Report + "\n仅使用已有公开快照离线重建语义索引，未新增网络/OCR调用。");
                    await Save("sources.json", acquired);
                    await Save("acquisition.json", new { goal, modules, seconds = cached.RootElement.GetProperty("seconds").GetDouble(), webCalls, offlineReindex = true });
                }
                else Check(cached.RootElement.GetProperty("modules").GetRawText() == JsonSerializer.Serialize(modules, Wire.Options), "cached-acquisition-module-hashes");
                previousAcquisitionSeconds = cached.RootElement.GetProperty("seconds").GetDouble();
                timings.Add(new { phase = "acquire", seconds = previousAcquisitionSeconds, reused = true });
            }
            else if (Value("--cached-page") is { } cachedPath)
            {
                Check(new FileInfo(cachedPath).Length <= 512 * 1024, "cached-page-size");
                using var cachedPage = JsonDocument.Parse(await File.ReadAllTextAsync(cachedPath));
                var element = cachedPage.RootElement.TryGetProperty("page", out var envelope) ? envelope : cachedPage.RootElement;
                var page = element.Deserialize<WebPage>(Wire.Options)!;
                var assessment = TeachingPages.Assess(page, goal);
                Check(assessment.UsableForTeaching, "cached-page-teaching-eligible");
                var library = (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(Path.Combine(output, "library"))))).Value!;
                var prepared = page with { Content = TeachingPages.Extract(page, assessment), OriginalContent = page.Content, Assessment = assessment };
                acquired = new(await knowledge.SaveWebAsync(library.Id, [prepared], goal, default), "复用先前实际服务端抓取的固定原文；本次没有搜索或抓取。");
                webCalls.Add(new { kind = "cached-fetch", page.Url, page.RetrievedAt, sourceFile = Path.GetFullPath(cachedPath),
                    sha256 = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(cachedPath))), usage = "previous-fetch-not-returned" });
                await Save("page-1.json", page);
                await Save("sources.json", acquired);
                await Save("acquisition.json", new { goal, modules, seconds = 0d, webCalls, cachedSourceReuse = true });
                timings.Add(new { phase = "acquire", seconds = timer.Elapsed.TotalSeconds, cachedSourceReuse = true });
            }
            else
            {
                using var directoryClient = OiDirectory.CreateClient();
                acquired = await new WebLearningService(web, knowledge, knowledge, Path.Combine(output, "library"),
                    broadPlan ? new OiDirectory(directoryClient) : null).AcquireAsync(goal, "", true, null, default);
                await Save("sources.json", acquired);
                Check(web.Searches <= 2 && web.Fetches <= 3 && timer.Elapsed.TotalSeconds <= 180, "shared-acquisition-budget");
                timings.Add(new { phase = "acquire", seconds = timer.Elapsed.TotalSeconds });
                await Save("acquisition.json", new { goal, modules, seconds = timer.Elapsed.TotalSeconds, webCalls });
            }
            Check(acquired.References.Count > 0, "goal-only-no-import-source");
            var resolvedBodies = new List<(SourceRef Reference, string Text)>();
            foreach (var reference in acquired.References)
            {
                var source = await knowledge.ResolveAsync(reference);
                Check(source.Text is not null, "frozen-source-resolves");
                resolvedBodies.Add((reference, source.Text!));
            }
            // Lossless indexing retains standalone section headings. They must resolve,
            // but only the complete page and the later lesson selection need body evidence.
            foreach (var page in resolvedBodies.GroupBy(s => s.Reference.Locator.SourceLabel))
                Check(page.Any(s => LessonSourceSelector.HasBody(s.Text)), "non-catalog-page-evidence");
            for (var i = 1; i <= 3; i++)
            {
                var file = Path.Combine(output, $"page-{i}.json");
                if (!File.Exists(file)) continue;
                var original = JsonSerializer.Deserialize<WebPage>(await File.ReadAllTextAsync(file), Wire.Options)!;
                var matching = resolvedBodies.Where(s => s.Reference.Locator.SourceLabel.Contains(" · " + original.Url + " · ", StringComparison.Ordinal)).ToArray();
                if (matching.Length == 0) continue; // rejected discovery pages are not teaching sources
                var assessment = TeachingPages.Assess(original, goal);
                Check(assessment.UsableForTeaching, "cached-page-still-eligible");
                Check(string.Concat(matching.Select(s => s.Text)) == TeachingPages.Extract(original, assessment), "source-text-order-and-coverage");
            }
            await Save("resolved-sources.json", resolvedBodies.Select((s, i) => new
            {
                key = "s" + (i + 1), s.Reference, s.Text,
                modelText = SourceText.ForModel(s.Reference.Locator.SourceLabel, s.Text)
            }).ToArray());
            if (sourceOnly) { Console.WriteLine($"P9 {scenario}: sources frozen for review; no model dispatch."); return 0; }
            var plans = new SqlitePlanningStore(output); var learning = new SqliteLearningStore(output, plans);
            using var client = DeepSeekProvider.CreateClient(); using var metadata = DeepSeekCapabilities.CreateClient();
            var capabilities = new DeepSeekCapabilities(output, credentials, metadata);
            var provider = new Recording(new DeepSeekProvider(credentials, client), calls, output, broadPlan ? "plan-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") : "plan");
            var theory = new Recording(new DeepSeekTheoryProvider(credentials, client), calls, output, "theory");
            var planning = new PlanningService(plans, knowledge, settings, new(provider, capabilities), new PlanParser(), learning);
            // Natural goal remains the only source-choice input. Limit the teaching scope to acquired evidence.
            var draft = (await planning.StartAsync(new(Guid.NewGuid(), 0, new(goal, broadPlan ? null : "初学者",
                broadPlan ? null : "以实际取得资料为边界入门；资料未覆盖部分明确列为缺口，不推测已读。", broadPlan ? 480 : 120, "Adaptive", acquired.References)))).Value!;
            timer.Restart(); var preview = await planning.PrepareAsync(draft.Id, draft.Revision); Check(preview.Ok, "plan-preview-" + preview.Error?.Message);
            var proposed = await planning.ProposeAsync(preview.Value!, true); Check(proposed.Ok, "plan-valid-" + proposed.Error?.Message);
            Check(timer.Elapsed.TotalSeconds <= 600, "plan-target-600s"); timings.Add(new { phase = "plan", seconds = timer.Elapsed.TotalSeconds });
            if (broadPlan)
            {
                await Save("proposed-plan.json", proposed.Value!);
                await Save("broad-plan-summary.json", new { modules, checks, timings, calls, webCalls, qualityReviewPending = true, manualPending = true });
                Console.WriteLine("Broad goal plan saved for scope review; not confirmed."); return 0;
            }
            draft = (await planning.ConfirmAsync(new(Guid.NewGuid(), proposed.Value!.Revision, new(draft.Id, proposed.Value.Plan!.Hash, true)))).Value!;
            Check(draft.Confirmed, "explicit-plan-freeze"); await Save("confirmed-plan.json", draft);
            var node = draft.Plan!.Candidate.Nodes.First(n => n.Type == "theory" && n.PrerequisiteKeys.Count == 0);
            var generation = new GenerationService(plans, learning, learning, knowledge, settings, new(theory, capabilities), new TheoryLessonParser());
            timer.Restart(); Check((await generation.GenerateAsync(draft.Id, node.Key)).Ok, "representative-lesson-request");
            for (var i = 0; i < 3; i++)
            {
                await generation.TickAsync();
                if (!(await learning.TasksAsync()).Any(t => t.NodeKey == node.Key && t.Status == GenerationStatus.Queued)) break;
                await Task.Delay(3200);
            }
            var reader = await learning.GetLessonAsync(draft.Id, node.Key); Check(reader.Ok, "representative-lesson-ready");
            Check(reader.Value!.Questions.Count > 0, "complete-quiz-present"); Check(timer.Elapsed.TotalSeconds <= 300, "lesson-target-300s");
            timings.Add(new { phase = "lesson", seconds = timer.Elapsed.TotalSeconds }); await Save("reader.json", reader.Value);
            var selection = await learning.SelectionAsync(draft.Id, node.Key); Check(selection is { Sources.Count: > 0 }, "selected-evidence-persisted");
            await Save("selection.json", selection!);
            foreach (var source in selection!.Sources) Check((await knowledge.ResolveAsync(source.Reference)).Text is not null, "source-resolves-" + source.Key);
            var selectedBodies = new List<string>();
            foreach (var source in selection.Sources) selectedBodies.Add((await knowledge.ResolveAsync(source.Reference)).Text!);
            Check(selectedBodies.Any(LessonSourceSelector.HasBody), "selected-teaching-body-present");
            Check((await new SqlitePlanningStore(output).GetAsync(draft.Id))!.Plan!.Hash == draft.Plan.Hash, "restart-hash-stable");
            Check((await learning.CompletedNodesAsync(draft.Id)).Count == 0, "generated-is-not-mastery");
            Check(calls.Count <= 6, "at-most-one-plan-and-one-lesson-with-shared-retries");
            await Save("p9-real-summary.json", new { success = true, scenario, goal, modules, checks, timings, calls, webCalls,
                totalSeconds = total.Elapsed.TotalSeconds + previousAcquisitionSeconds, reviewPauseNotCounted = resume, codeExecuted = false, newOcrCalls = 0, qualityReviewPending = true, manualPending = true });
            Console.WriteLine($"P9 {scenario}: {checks.Count} checks; {calls.Count} model dispatches; sources {acquired.References.Count}."); return 0;
        }
        catch (Exception e)
        {
            await Save("p9-real-failure.json", new { scenario, checks, timings, calls, webCalls, modules, failure = e.Message, codeExecuted = false });
            Console.WriteLine("P9 real failed; inspect preserved evidence: " + output); return 1;
        }
    }
    private sealed class Recording(IModelProvider inner, List<object> calls, string output, string prefix) : IModelProvider
    {
        public string Destination => inner.Destination;
        private int count;
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            var started = Stopwatch.StartNew(); var attempt = ++count;
            await File.WriteAllTextAsync(Path.Combine(output, $"{prefix}-dispatch-{attempt}.json"),
                JsonSerializer.Serialize(new { startedAt = DateTimeOffset.UtcNow, request.Model, request.MaxOutputTokens,
                    repairInstructions = request.PlanRepair?.Instructions, receiptPending = true }, Wire.Options), ct);
            var reply = await inner.SendAsync(request, ct);
            calls.Add(new { kind = prefix, attempt, request.Model, request.MaxOutputTokens, seconds = started.Elapsed.TotalSeconds, success = reply.Ok, failure = reply.Error?.Code.ToString(), reply.Value?.Usage });
            await File.WriteAllTextAsync(Path.Combine(output, $"{prefix}-reply-{attempt}.json"), JsonSerializer.Serialize(reply, Wire.Options)); return reply;
        }
    }
    private sealed class RecordedWeb(IWebSources inner, List<object> calls, string output) : IWebSources
    {
        public int Searches { get; private set; }
        public int Fetches { get; private set; }
        public async Task<IReadOnlyList<WebHit>> SearchAsync(string query, CancellationToken ct)
        {
            Searches++; var hits = await inner.SearchAsync(query, ct); calls.Add(new { kind = "search", query, count = hits.Count, usage = "not-returned" }); return hits;
        }
        public async Task<WebPage> FetchAsync(string url, CancellationToken ct)
        {
            var attempt = ++Fetches;
            try
            {
                var page = await inner.FetchAsync(url, ct); calls.Add(new { kind = "fetch", url, characters = page.Content.Length, success = true, usage = "not-returned" });
                await File.WriteAllTextAsync(Path.Combine(output, $"page-{attempt}.json"), JsonSerializer.Serialize(page, Wire.Options), ct); return page;
            }
            catch { calls.Add(new { kind = "fetch", url, success = false, usage = "unknown" }); throw; }
        }
    }
}
