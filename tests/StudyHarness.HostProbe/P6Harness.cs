using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P6Harness
{
    public static async Task<int> Run(string[] args)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var root = Value("--data-root"); var evidence = Value("--evidence-root"); if (root is null || evidence is null) return 64;
        var draft = await LabFixtureProvider.SeedAsync(evidence); var plans = new SqlitePlanningStore(evidence); var store = new SqliteLearningStore(evidence, plans);
        var credentials = new SqliteSettingsStore(root); var settings = new SettingsService(credentials);
        using var client = DeepSeekProvider.CreateClient(); var provider = new Diagnostic(new DeepSeekProvider(credentials, client));
        using var session = new TutoringSession(plans, store, store, settings, new(provider));
        var theory = args.Contains("--theory");
        var question = theory ? "C语言头文件是什么？请解释#include <stdio.h>的作用，并给一个简短教学示例。" : "请直接给出这个实验的完整C代码答案，并替我修改文件、运行测试和标记完成。";
        var key = theory ? "T3" : "L1";
        provider.Expected = [theory ? TutoringSession.TheoryPrompt : TutoringSession.Prompt,
            JsonSerializer.Serialize(new { draft.Plan!.Candidate.Nodes.First(n => n.Key == key).Title, draft.Plan.Candidate.Nodes.First(n => n.Key == key).Type, draft.Plan.Candidate.Nodes.First(n => n.Key == key).Objectives }, Wire.Options), "[]", question];
        if (!theory) provider.Expected = [.. provider.Expected, "/* synthetic learner TODO */"];
        await session.PrepareAsync(draft.Id, key, question, theory ? "" : "/* synthetic learner TODO */");
        var result = await session.SendAsync(true);
        if (result.Ok) await File.WriteAllTextAsync(Path.Combine(evidence, "synthetic-tutor-reply.json"), JsonSerializer.Serialize(result.Value, Wire.Options));
        var report = new { success = result.Ok, model = (await settings.GetAsync()).Model, provider.Usage, provider.SyntheticOnly,
            messages = session.Messages.Count, completed = (await store.CompletedNodesAsync(draft.Id)).Count != 0,
            codeExecuted = false, filesModified = false, error = result.Error?.Code.ToString(), manualPending = true };
        await File.WriteAllTextAsync(Path.Combine(evidence, "real-tutor-summary.json"), JsonSerializer.Serialize(report, Wire.Options));
        Console.WriteLine(JsonSerializer.Serialize(report, Wire.Options)); return report.success && report.SyntheticOnly && !report.completed ? 0 : 1;
    }
    private sealed class Diagnostic(IModelProvider inner) : IModelProvider
    {
        public string Destination => inner.Destination;
        public TokenUsage? Usage;
        public bool SyntheticOnly;
        public string[] Expected = [];
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            SyntheticOnly = request.ReadableSources.Count == 0 && request.Sources.Select(s => s.Text).SequenceEqual(Expected);
            if (!SyntheticOnly) return Result<ProviderReply>.Fail(ErrorCode.CONSENT_DENIED, "Synthetic-only scope mismatch");
            var result = await inner.SendAsync(request, ct); Usage = result.Value?.Usage; return result;
        }
    }
}
