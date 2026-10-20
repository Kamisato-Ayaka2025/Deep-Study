using System.Diagnostics;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class PlanReliabilityHarness
{
    public static async Task<int> Run(string[] args)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i < 0 || i + 1 == args.Length ? null : args[i + 1]; }
        var credentialsRoot = Value("--data-root"); var output = Value("--evidence-root"); var scenario = Value("--scenario");
        var cases = new Dictionary<string, (string Goal, int? Minutes)> { ["algorithms"] = ("我想学习算法竞赛", 120), ["linear"] = ("我想学习线性代数", null), ["cpp"] = ("我想学习C++", null) };
        if (credentialsRoot is null || output is null || scenario is null || !cases.TryGetValue(scenario, out var sample)) return 64;
        var round = int.Parse(Value("--round") ?? "1"); if (round is < 1 or > 3) return 64;
        Directory.CreateDirectory(output);
        var roundPath = Path.Combine(output, $"round-{round}"); if (Directory.Exists(roundPath)) return 64;
        Directory.CreateDirectory(roundPath);
        var records = new List<object>(); var passed = 0; var firstPassed = 0; var rawPassed = 0; var calls = 0;
        try
        {
            var credentials = new SqliteSettingsStore(credentialsRoot); var settings = new SettingsService(credentials);
            using var webClient = DeepSeekWebSources.CreateClient(); var web = new DeepSeekWebSources(credentials, webClient);
            var knowledge = new KnowledgeStore(output, web); var libraryPath = Path.Combine(output, "library"); Directory.CreateDirectory(libraryPath);
            var library = await knowledge.CurrentAsync() ?? (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(libraryPath)))).Value!;
            var referencesPath = Path.Combine(output, "references.json"); IReadOnlyList<SourceRef> references;
            if (File.Exists(referencesPath)) references = JsonSerializer.Deserialize<SourceRef[]>(await File.ReadAllTextAsync(referencesPath), Wire.Options)!;
            else
            {
                // Goal-only user path. No hidden subject/domain hints or synthetic textbook in the prompt.
                var acquired = await new WebLearningService(web, knowledge, knowledge, libraryPath).AcquireAsync(sample.Goal, "", true, null, default);
                references = acquired.References;
                await File.WriteAllTextAsync(referencesPath, JsonSerializer.Serialize(references, Wire.Options));
                await File.WriteAllTextAsync(Path.Combine(output, "acquisition.txt"), acquired.Report);
            }
            var store = new SqlitePlanningStore(output); var learning = new SqliteLearningStore(output, store);
            using var client = DeepSeekProvider.CreateClient(); using var metaClient = DeepSeekCapabilities.CreateClient();
            var capabilities = new DeepSeekCapabilities(output, credentials, metaClient);
            for (var i = 1; i <= 10 && passed < 5; i++)
            {
                var taskPath = Path.Combine(roundPath, "task-" + i); Directory.CreateDirectory(taskPath);
                var provider = new RecordingProvider(new DeepSeekProvider(credentials, client), taskPath);
                var service = new PlanningService(store, knowledge, settings, new(provider, capabilities), new PlanParser(), learning);
                var input = new PlanningInput(sample.Goal, null, null, sample.Minutes, "Adaptive", references);
                var draft = (await service.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
                var watch = Stopwatch.StartNew(); var preview = await service.PrepareAsync(draft.Id, draft.Revision);
                Result<PlanningDraft> result = preview.Ok ? await service.ProposeAsync(preview.Value!, true) : new(false, null, preview.Error);
                var raw = provider.Replies.Count == 0 || !provider.Replies[0].Ok ? null : new PlanParser().ParseRaw(provider.Replies[0].Value!.Text);
                var sources = references.Select((r, j) => new PlanSource("s" + (j + 1), r, true)).ToArray();
                var rawValid = raw?.Ok == true && PlanRules.Validate(draft.Id, input, raw.Value!, sources, null).Ok;
                if (rawValid) rawPassed++;
                if (result.Ok) passed++;
                if (result.Ok && provider.Replies.Count == 1) firstPassed++;
                calls += provider.Replies.Count;
                records.Add(new { task = i, success = result.Ok, firstRequestSuccess = result.Ok && provider.Replies.Count == 1, rawFirstValid = rawValid,
                    calls = provider.Replies.Count, seconds = watch.Elapsed.TotalSeconds, error = result.Error, usage = provider.Replies.Select(r => r.Value?.Usage).ToArray(),
                    nodes = result.Value?.Plan?.Candidate.Nodes.Count, algorithms = result.Value?.Plan?.Candidate.Algorithms?.Count });
                if (result.Ok) await File.WriteAllTextAsync(Path.Combine(taskPath, "plan.json"), JsonSerializer.Serialize(result.Value!.Plan, Wire.Options));
                await Save();
                Console.WriteLine($"{scenario} round {round} task {i}: success={result.Ok} calls={provider.Replies.Count}, passed={passed}/5; {result.Error?.Message}");
                // Six failures make five successes impossible within this round; avoid futile paid attempts.
                if (i - passed >= 6) break;
            }
            return passed >= 5 ? 0 : 1;
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(roundPath, "failure.txt"), e.GetType().Name); await Save(); Console.WriteLine(e.GetType().Name); return 1;
        }
        async Task Save() => await File.WriteAllTextAsync(Path.Combine(roundPath, "summary.json"), JsonSerializer.Serialize(new {
            scenario, goal = sample.Goal, weeklyMinutes = sample.Minutes, round, attempted = records.Count, passed, firstPassed, rawPassed, calls,
            passedThreshold = passed >= 5, taskLimit = 10, dispatchLimitPerTask = 3, unrun = 10 - records.Count,
            manualPending = true, codeExecuted = false, records
        }, new JsonSerializerOptions(Wire.Options) { WriteIndented = true }));
    }
    private sealed class RecordingProvider(IModelProvider inner, string output) : IModelProvider
    {
        public string Destination => inner.Destination;
        public List<Result<ProviderReply>> Replies { get; } = [];
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            var reply = await inner.SendAsync(request, ct); Replies.Add(reply);
            await File.WriteAllTextAsync(Path.Combine(output, $"reply-{Replies.Count}.json"), JsonSerializer.Serialize(new { request.Model, request.MaxOutputTokens, reply }, Wire.Options), ct);
            return reply;
        }
    }
}
