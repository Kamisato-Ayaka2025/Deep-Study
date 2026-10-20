using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P7Harness
{
    public static async Task<int> Run(string[] args)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var credentialsRoot = Value("--data-root"); var root = Value("--evidence-root"); if (credentialsRoot is null || root is null) return 64;
        Directory.CreateDirectory(root); var checks = new List<string>(); var results = new List<object>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidDataException(name); checks.Add(name); }
        try
        {
            var draft = await CppFixture.StartAsync(root); var plans = new SqlitePlanningStore(root); var store = new SqliteLearningStore(root, plans);
            var credentials = new SqliteSettingsStore(credentialsRoot); var settings = new SettingsService(credentials);
            using var client = DeepSeekProvider.CreateClient(); var diagnostic = new SyntheticProvider(new DeepSeekProvider(credentials, client), root);
            var egress = new ModelEgress(diagnostic); var knowledge = new KnowledgeStore(root);
            var planning = new PlanningService(plans, knowledge, settings, egress, new PlanParser());
            var preview = await planning.PrepareAsync(draft.Id, draft.Revision); Check(preview.Ok, "prepare-plan");
            var proposal = await planning.ProposeAsync(preview.Value!, true); Check(proposal.Ok, "real-plan-" + proposal.Error?.Message);
            Check(proposal.Value!.Plan!.Candidate.Algorithms?.Count == 2, "two-requested-algorithms");
            var frozen = await planning.ConfirmAsync(new(Guid.NewGuid(), proposal.Value.Revision, new(draft.Id, proposal.Value.Plan.Hash, true)));
            Check(frozen.Ok, "freeze-covered-plan"); draft = frozen.Value!;
            await File.WriteAllTextAsync(Path.Combine(root, "coverage.md"), PlanRules.AlgorithmCoverage(draft.Plan!.Candidate));
            await File.WriteAllTextAsync(Path.Combine(root, "confirmed-plan.json"), JsonSerializer.Serialize(draft, Wire.Options));
            var theoryDiagnostic = new SyntheticProvider(new DeepSeekTheoryProvider(credentials, client), root, "theory");
            var generation = new GenerationService(plans, store, store, knowledge, settings, new(theoryDiagnostic), new TheoryLessonParser());
            var labs = new LabService(plans, store, store, knowledge, settings, egress, new LabZipExporter());
            var graph = PlanRules.Graph(draft.Id, draft.Plan.Candidate);
            foreach (var node in draft.Plan.Candidate.Nodes.OrderBy(n => graph.Layers[PlanRules.NodeId(draft.Id, n.Key)]))
            {
                if (node.Type == "theory")
                {
                    Check((await generation.GenerateAsync(draft.Id, node.Key)).Ok, "request-" + node.Key);
                    for (var i = 0; i < 3; i++) { await generation.TickAsync(); if (!(await store.TasksAsync()).Any(t => t.NodeKey == node.Key && t.Status == GenerationStatus.Queued)) break; await Task.Delay(3200); }
                    var lesson = await store.GetLessonAsync(draft.Id, node.Key); Check(lesson.Ok, "real-theory-" + node.Key);
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
            await File.WriteAllTextAsync(Path.Combine(root, "p7-real-summary.json"), JsonSerializer.Serialize(new { success = true, checks, results, calls = diagnostic.Calls + theoryDiagnostic.Calls, model = (await settings.GetAsync()).Model, theoryTasks = await store.TasksAsync(), codeExecuted = false, syntheticProgressOnly = true, manualPending = true }, Wire.Options));
            Console.WriteLine($"P7 real synthetic course passed: {checks.Count} checks; {diagnostic.Calls + theoryDiagnostic.Calls} requests; no code execution."); return 0;
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(root, "p7-real-failure.json"), JsonSerializer.Serialize(new { checks, results, failure = e.Message, codeExecuted = false }, Wire.Options));
            Console.WriteLine("P7 real check failed; inspect synthetic evidence."); return 1;
        }
    }
    private sealed class SyntheticProvider(IModelProvider inner, string root, string prefix = "synthetic") : IModelProvider
    {
        public string Destination => inner.Destination;
        public int Calls { get; private set; }
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            // Only the isolated harness textbook, never user files or credentials as prompt data.
            var sources = request.Sources.Concat(request.ReadableSources).Where(s => s.Kind == ModelSourceKind.OriginalMaterial).ToArray();
            if (sources.Length == 0 || sources.Any(s => !CppFixture.SourceText.Contains(s.Text.StartsWith("[资料id=", StringComparison.Ordinal) ? s.Text[(s.Text.IndexOf('\n') + 1)..].Trim() : s.Text.Trim(), StringComparison.Ordinal)))
                return Result<ProviderReply>.Fail(ErrorCode.CONSENT_DENIED, "Synthetic scope mismatch");
            var reply = await inner.SendAsync(request, ct); Calls++;
            await File.WriteAllTextAsync(Path.Combine(root, $"{prefix}-reply-{Calls}.json"), JsonSerializer.Serialize(reply, Wire.Options), ct);
            return reply;
        }
    }
}
