using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class WebHarness
{
    public static async Task<int> Run(string[] args)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var credentialsRoot = Value("--data-root"); var root = Value("--evidence-root"); if (credentialsRoot is null || root is null) return 64;
        Directory.CreateDirectory(root); var checks = new List<string>(); var results = new List<object>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidDataException(name); checks.Add(name); }
        try
        {
            if (args.Contains("--schema-smoke"))
            {
                var credentialStore = new SqliteSettingsStore(credentialsRoot);
                using var schemaClient = DeepSeekProvider.CreateClient();
                var schemaEgress = new ModelEgress(new DeepSeekTheoryProvider(credentialStore, schemaClient));
                var review = schemaEgress.Prepare((await new SettingsService(credentialStore).GetAsync()).Model,
                    [new("theory-v3-short", 3, ModelSourceKind.UserText, TheoryPrompt.Text + "\n只讲二分查找每轮缩小区间，sourceKeys=[s1]，正文简短。"),
                     new("s1", 0, ModelSourceKind.OriginalMaterial, "二分查找每轮把有序候选区间减半。")], 2048, true);
                var reply = await schemaEgress.SendAsync(review, schemaEgress.Approve(review, ConsentMode.Each), default);
                await File.WriteAllTextAsync(Path.Combine(root, "schema-smoke.json"), JsonSerializer.Serialize(reply, Wire.Options));
                Check(reply.Ok, "schema-endpoint-" + reply.Error?.Message);
                using var parsed = JsonDocument.Parse(reply.Value!.Text);
                Console.WriteLine("Schema endpoint returned parseable JSON."); return 0;
            }
            var draft = await StartAsync(root, credentialsRoot, args.Contains("--oi-wiki")); var plans = new SqlitePlanningStore(root); var store = new SqliteLearningStore(root, plans);
            var credentials = new SqliteSettingsStore(credentialsRoot); var settings = new SettingsService(credentials);
            using var client = DeepSeekProvider.CreateClient(); var diagnostic = new PublicWebProvider(new DeepSeekProvider(credentials, client), root);
            var egress = new ModelEgress(diagnostic); var knowledge = new KnowledgeStore(root);
            var planning = new PlanningService(plans, knowledge, settings, egress, new PlanParser());
            var preview = await planning.PrepareAsync(draft.Id, draft.Revision); Check(preview.Ok, "prepare-plan");
            var proposal = await planning.ProposeAsync(preview.Value!, true); Check(proposal.Ok, "real-plan-" + proposal.Error?.Message);
            Check(proposal.Value!.Plan!.Candidate.Algorithms?.Count == 2, "two-requested-algorithms");
            Check(proposal.Value.Plan.Candidate.Nodes.Count == 4 && proposal.Value.Plan.Candidate.Nodes.Count(n => n.Type == "lab") == 2, "bounded-four-node-course");
            var frozen = await planning.ConfirmAsync(new(Guid.NewGuid(), proposal.Value.Revision, new(draft.Id, proposal.Value.Plan.Hash, true)));
            Check(frozen.Ok, "freeze-covered-plan"); draft = frozen.Value!;
            await File.WriteAllTextAsync(Path.Combine(root, "coverage.md"), PlanRules.AlgorithmCoverage(draft.Plan!.Candidate));
            await File.WriteAllTextAsync(Path.Combine(root, "confirmed-plan.json"), JsonSerializer.Serialize(draft, Wire.Options));
            var generation = new GenerationService(plans, store, store, knowledge, settings,
                new(new PublicWebProvider(new DeepSeekTheoryProvider(credentials, client), root, "public-theory")), new TheoryLessonParser());
            var labs = new LabService(plans, store, store, knowledge, settings, egress, new LabZipExporter());
            var graph = PlanRules.Graph(draft.Id, draft.Plan.Candidate);
            foreach (var node in draft.Plan.Candidate.Nodes.OrderBy(n => graph.Layers[PlanRules.NodeId(draft.Id, n.Key)]))
            {
                if (node.Type == "theory")
                {
                    Check((await generation.GenerateAsync(draft.Id, node.Key)).Ok, "request-" + node.Key);
                    for (var i = 0; i < 3; i++) { await generation.TickAsync(); if (!(await store.TasksAsync()).Any(t => t.NodeKey == node.Key && t.Status == GenerationStatus.Queued)) break; await Task.Delay(3200); }
                    var lesson = await store.GetLessonAsync(draft.Id, node.Key);
                    await File.WriteAllTextAsync(Path.Combine(root, "theory-tasks.json"), JsonSerializer.Serialize(await store.TasksAsync(), Wire.Options));
                    Check(lesson.Ok, "real-theory-" + node.Key);
                    // Synthetic fixture traversal only; does not represent a human's learning evidence.
                    foreach (var q in lesson.Value!.Questions)
                        foreach (var option in q.Options)
                            if ((await store.SubmitAnswerAsync(new(Guid.NewGuid(), 0, new(lesson.Value.Identity, q.Id, option.Id)))).Value?.Correct == true) break;
                    Check((await store.CompletedNodesAsync(draft.Id)).Contains(node.Key), "synthetic-theory-progression-" + node.Key);
                }
                else
                {
                    var material = await labs.GenerateAsync(draft.Id, node.Key); Check(material.Ok, "real-cpp-" + node.Key + "-" + material.Error?.Message);
                    Check(material.Value!.Material.SchemaVersion == 4 && material.Value.Material.ExperimentKind == "exercise", "cpp-exercise-" + node.Key);
                    Check(!(await store.CompletedNodesAsync(draft.Id)).Contains(node.Key), "generation-not-completion-" + node.Key);
                    Check((await labs.ExportAsync(draft.Id, node.Key, Path.GetFullPath(Path.Combine(root, node.Key + ".zip")))).Ok, "export-" + node.Key);
                    results.Add(new { node.Key, node.AlgorithmKeys, material.Value.Hash, cases = material.Value.Material.Cases.Count, job = await store.GetLabJobAsync(draft.Id, node.Key) });
                    Check((await labs.ConfirmAsync(draft.Id, node.Key, material.Value.Hash, true)).Ok, "synthetic-attestation-only-" + node.Key);
                }
            }
            var restarted = new SqliteLearningStore(root, new SqlitePlanningStore(root));
            Check((await restarted.CompletedNodesAsync(draft.Id)).Count == draft.Plan.Candidate.Nodes.Count, "restart-synthetic-progress");
            await File.WriteAllTextAsync(Path.Combine(root, "web-real-summary.json"), JsonSerializer.Serialize(new { success = true, checks, results,  model = (await settings.GetAsync()).Model, theoryTasks = await store.TasksAsync(), codeExecuted = false, syntheticProgressOnly = true, manualPending = true }, Wire.Options));
            Console.WriteLine($"P7 real synthetic course passed: {checks.Count} checks; no code execution."); return 0;
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "web-real-failure.json"), JsonSerializer.Serialize(new { checks, results, failure = e.Message, codeExecuted = false }, Wire.Options));
            Console.WriteLine("P7 real check failed; inspect synthetic evidence."); return 1;
        }
    }
    private sealed class PublicWebProvider(IModelProvider inner, string root, string prefix = "public-web") : IModelProvider
    {
        public string Destination => inner.Destination;
        private int calls;
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            var reply = await inner.SendAsync(request, ct);
            await File.WriteAllTextAsync(Path.Combine(root, $"{prefix}-reply-{++calls}.json"), JsonSerializer.Serialize(reply, Wire.Options), ct);
            return reply;
        }
    }
    private static async Task<PlanningDraft> StartAsync(string root, string credentialsRoot, bool site)
    {
        var knowledge = new KnowledgeStore(root);
        if (await knowledge.CurrentAsync() is not null) throw new InvalidOperationException("Fresh empty evidence library required");
        var library = Path.Combine(root, "web-library"); Directory.CreateDirectory(library);
        using var client = DeepSeekWebSources.CreateClient();
        var web = new WebLearningService(new LoggedWeb(new DeepSeekWebSources(new SqliteSettingsStore(credentialsRoot), client), root), knowledge, knowledge, library);
        var acquired = await web.AcquireAsync("二分查找 前缀和", site ? "oi-wiki.org" : "", true, null, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(root, "web-sources.txt"), acquired.Report);
        return (await new SqlitePlanningStore(root).StartAsync(new(Guid.NewGuid(), 0,
            new("学习C++算法竞赛，只学习二分查找和一维前缀和，恰好两节理论和两道独立题，共4节点，禁止综合或拓展练习", "已掌握C++基础", "掌握二分查找和一维前缀和，每算法一节理论与一道独立题；总共4节点，不安排其他课程", 120, "CppAlgorithms", acquired.References)))).Value!;
    }
    private sealed class LoggedWeb(IWebSources inner, string root) : IWebSources
    {
        public async Task<IReadOnlyList<WebHit>> SearchAsync(string query, CancellationToken ct)
        {
            var hits = await inner.SearchAsync(query, ct);
            await File.WriteAllTextAsync(Path.Combine(root, "search-results.json"), JsonSerializer.Serialize(new { query, hits }, Wire.Options), ct);
            return hits;
        }
        public Task<WebPage> FetchAsync(string url, CancellationToken ct) => inner.FetchAsync(url, ct);
    }
}
