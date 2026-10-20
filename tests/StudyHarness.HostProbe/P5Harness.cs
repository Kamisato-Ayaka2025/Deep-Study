using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Infrastructure;
using StudyHarness.Contracts;

namespace StudyHarness.HostProbe;

internal static class P5Harness
{
    public static async Task<int> Run(string[] args)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var root = Value("--data-root"); var evidence = Value("--evidence-root"); if (root is null || evidence is null) return 64;
        var draft = await LabFixtureProvider.SeedAsync(evidence); var plans = new SqlitePlanningStore(evidence); var store = new SqliteLearningStore(evidence, plans);
        var preview = args.Contains("--p5-preview");
        var credentials = new SqliteSettingsStore(preview ? evidence : root); var settings = new SettingsService(credentials);
        using var client = DeepSeekProvider.CreateClient();
        var service = new LabService(plans, store, store, new KnowledgeStore(evidence), settings,
            new ModelEgress(new SyntheticDiagnosticProvider(preview ? new LabFixtureProvider() : new DeepSeekProvider(credentials, client), evidence)), new LabZipExporter());
        var generated = await service.GenerateAsync(draft.Id, "L1");
        var exported = generated.Ok && (await service.ExportAsync(draft.Id, "L1", Path.GetFullPath(Path.Combine(evidence, "real-materials.zip")))).Ok;
        var job = await store.GetLabJobAsync(draft.Id, "L1");
        var report = new { success = generated.Ok && exported, exported, isFake = preview, model = (await settings.GetAsync()).Model,
            job, sources = generated.Value?.Material.SourceFiles.Count, tests = generated.Value?.Material.Cases.Count,
            codeExecuted = false, completed = (await store.CompletedNodesAsync(draft.Id)).Count != 0, error = generated.Error?.Code.ToString(), manualPending = true };
        await File.WriteAllTextAsync(Path.Combine(evidence, "real-lab-summary.json"), JsonSerializer.Serialize(report, Wire.Options));
        Console.WriteLine(JsonSerializer.Serialize(report, Wire.Options)); return report.success && !report.completed ? 0 : 1;
    }
    private sealed class SyntheticDiagnosticProvider(IModelProvider inner, string evidence) : IModelProvider
    {
        public string Destination => inner.Destination;
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            var expectedPrompt = LabPrompt.Text + "\n本课冻结定义：" + JsonSerializer.Serialize(LabFixtureProvider.FirstNode, Wire.Options) + "\n可引用资料id：s1";
            var safe = request.ReadableSources.Count == 0 && request.Sources.Count == 2 && request.Sources[0].Id == "lab-v3" &&
                request.Sources[0].Kind == ModelSourceKind.UserText && request.Sources[0].Text == expectedPrompt &&
                request.Sources[1].Id == "s1" && request.Sources[1].Kind == ModelSourceKind.OriginalMaterial &&
                request.Sources[1].Text.StartsWith("[资料id=s1]\n", StringComparison.Ordinal) &&
                request.Sources[1].Text.Length > 20 && LabFixtureProvider.SourceText.Contains(request.Sources[1].Text[10..].Trim(), StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(evidence, "egress-scope.json"), JsonSerializer.Serialize(new { syntheticOnly = safe,
                sources = request.Sources.Select(s => new { s.Id, s.Kind, characters = s.Text.Length, hash = PlanRules.Hash(s.Text) }), credentialUse = "HTTPS Authorization header only; not a model source" }, Wire.Options), ct);
            if (!safe) return Result<ProviderReply>.Fail(ErrorCode.CONSENT_DENIED, "Synthetic-only scope mismatch; request not sent.");
            var result = await inner.SendAsync(request, ct);
            // This harness sends only its own synthetic textbook. Never installed in product composition.
            if (result.Ok) await File.WriteAllTextAsync(Path.Combine(evidence, "synthetic-model-material.json"), result.Value!.Text, ct);
            return result;
        }
    }
}
