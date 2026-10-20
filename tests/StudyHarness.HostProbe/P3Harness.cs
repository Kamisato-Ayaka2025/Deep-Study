using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P3Harness
{
    public static async Task<int> Run(string[] args)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var root = Value("--data-root"); if (root is null) return 64;
        if (args.Contains("--p3-seed"))
        {
            var (knowledge, refs) = await Materials(root);
            var store = new SqlitePlanningStore(root);
            foreach (var title in new[] { "P3 已确认合成数学", "P3 待确认合成数学" })
            {
                var input = new PlanningInput("集合与关系", "零基础", "理解集合与关系", 120, "Math", refs);
                var draft = (await store.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
                var candidate = new PlanCandidate(title, input.Background!, input.Goal!, 120, "Math", [], [], ["合成测试；无正文"], ["s1"], [
                    new("n1", "集合", "theory", ["解释集合"], [], 30, null),
                    new("n2", "关系", "theory", ["解释关系"], ["n1"], 30, null)]);
                var doc = PlanRules.Validate(draft.Id, input, candidate, [new("s1", refs[0], true)], null).Value!;
                var proposed = await store.SaveProposalAsync(new(Guid.NewGuid(), 0, doc), draft.Id);
                if (!proposed.Ok) return 1;
                if (title.Contains("已确认") && !(await store.ConfirmAsync(new(Guid.NewGuid(), proposed.Value!.Revision, new(draft.Id, doc.Hash, true)))).Ok) return 1;
            }
            Console.WriteLine("P3 synthetic seed: two subjects, one confirmed, one proposal, zero lessons."); return 0;
        }
        if (args.Contains("--p3-real"))
        {
            var evidence = Value("--evidence-root"); if (evidence is null) return 64;
            var credentials = new SqliteSettingsStore(root); var config = await credentials.ReadAsync(default);
            if (config.Model != "deepseek-flash") { Console.WriteLine("blocked: P3 acceptance requires saved model deepseek-flash"); return 2; }
            if (!config.HasApiKey) { Console.WriteLine("blocked: configure API key in desktop settings"); return 2; }
            var (knowledge, refs) = await Materials(evidence);
            // The math fragment is for desktop multi-subject fixtures, not the C plan.
            // Deliberately selecting incompatible preferred material correctly causes clarification.
            var cReferences = new List<SourceRef>();
            foreach (var reference in refs)
                if ((await knowledge.ResolveAsync(reference)).Text?.StartsWith("简单C语言", StringComparison.Ordinal) == true) cReferences.Add(reference);
            if (cReferences.Count == 0) throw new InvalidOperationException("Synthetic C material missing");
            // H5: read-only synthetic project, never sent to a compiler or Runner.
            var project = Path.Combine(evidence, "synthetic-c-project"); Directory.CreateDirectory(project);
            await File.WriteAllTextAsync(Path.Combine(project, "sum.h"), "int sum(int a, int b);\n");
            await File.WriteAllTextAsync(Path.Combine(project, "sum.c"), "#include \"sum.h\"\nint sum(int a, int b) { return a + b; }\n");
            var projectImport = await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromProjectPicker(project)), (await knowledge.CurrentAsync())!.Id);
            if (!projectImport.Ok || projectImport.Value!.References?.Count != 2) throw new InvalidOperationException("Synthetic project import failed");
            cReferences.AddRange(projectImport.Value.References);
            using var client = DeepSeekProvider.CreateClient();
            var store = new SqlitePlanningStore(evidence);
            var capture = new SyntheticCapture(new DeepSeekProvider(credentials, client), evidence);
            var service = new PlanningService(store, knowledge, new SettingsService(credentials), new ModelEgress(capture), new PlanParser());
            var input = args.Contains("--h2")
                ? new PlanningInput("学习C语言", null, null, null, "Auto", cReferences)
                : new PlanningInput("零基础学习简单C语言。安排精简课程，含独立实验。材料为合成测试资料。", "零基础", "能解释C变量并编写简单整数求和程序", 120, "SimpleC", cReferences);
            if (Value("--h3-minutes") is { } weekly)
                input = new("学习C语言", null, null, int.Parse(weekly, System.Globalization.CultureInfo.InvariantCulture), "Auto", cReferences);
            var draft = (await service.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
            var preview = await service.PrepareAsync(draft.Id, 0);
            if (!preview.Ok) { Console.WriteLine(preview.Error!.Code); return 1; }
            var result = await service.ProposeAsync(preview.Value!, true);
            Result<PlanningDraft>? confirmed = null;
            if (result.Ok) confirmed = await service.ConfirmAsync(new(Guid.NewGuid(), result.Value!.Revision, new(draft.Id, result.Value.Plan!.Hash, true)));
            var saved = await store.GetAsync(draft.Id);
            Console.WriteLine(JsonSerializer.Serialize(new { success = result.Ok && confirmed?.Ok == true, code = result.Error?.Code.ToString() ?? confirmed?.Error?.Code.ToString(),
                errorMessage = result.Error?.Message ?? confirmed?.Error?.Message,
                model = config.Model, usage = capture.Reply?.Usage, readSourceIds = capture.Reply?.ReadSourceIds, toolCalls = capture.Reply?.ToolCalls, nodes = result.Value?.Plan?.Candidate.Nodes.Count,
                labs = result.Value?.Plan?.Candidate.Nodes.Count(n => n.Type == "lab"), defaults = result.Value?.Plan?.Candidate.Defaults, confirmed = saved?.Confirmed,
                hash = saved?.Plan?.Hash, isFake = false, time = DateTimeOffset.UtcNow, realCodeEnabled = false }, Wire.Options));
            return result.Ok && confirmed?.Ok == true ? 0 : 1;
        }
        return 64;
    }
    // Only this synthetic harness records model output; production never logs prompts or replies.
    private sealed class SyntheticCapture(IModelProvider inner, string root) : IModelProvider
    {
        public string Destination => inner.Destination;
        public ProviderReply? Reply { get; private set; }
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            var result = await inner.SendAsync(request, ct); Reply = result.Value;
            if (result.Ok) await File.WriteAllTextAsync(Path.Combine(root, "synthetic-model-output.json"), result.Value!.Text, ct);
            return result;
        }
    }
    private static async Task<(KnowledgeStore Store, SourceRef[] Refs)> Materials(string root)
    {
        Directory.CreateDirectory(root); var library = Path.Combine(root, "p3-library"); Directory.CreateDirectory(library);
        var path = Path.Combine(root, "p3-synthetic.md");
        await File.WriteAllTextAsync(path, "# 合成学习素材\n\n纯数学：集合是对象的集合；二元关系是笛卡尔积的子集。\n\n简单C语言：int变量保存整数，赋值更新变量；printf输出整数，scanf读取整数。程序从main开始，使用stdio.h。简单求和可用两个整数相加并输出，练习覆盖零和负数。MSVC和MinGW均可编译这种简单C程序。独立实验目标为读入两个整数，计算并输出其和。此资料仅用于测试，无真实个人数据。");
        var knowledge = new KnowledgeStore(root); var current = await knowledge.CurrentAsync();
        var selected = await knowledge.SwitchAsync(new(Guid.NewGuid(), current?.Revision ?? 0, HostSelection.FromFolderPicker(library)));
        if (!selected.Ok) throw new InvalidOperationException("Synthetic library setup failed");
        var imported = await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFilePicker(path)), selected.Value!.Id);
        if (!imported.Ok) throw new InvalidOperationException("Synthetic import failed");
        return (knowledge, (await knowledge.SearchAsync(selected.Value.Id, "", [])).Fragments.Select(f => f.Reference).ToArray());
    }
}
