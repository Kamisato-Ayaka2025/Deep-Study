using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.HostProbe;

internal static class P4Harness
{
    public static async Task<int> Run(string[] args)
    {
        string? Value(string key) { var i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var root = Value("--data-root"); if (root is null) return 64;
        var real = args.Contains("--p4-real"); var target = real ? Value("--evidence-root") : root;
        if (target is null) return 64;
        var draft = await Seed(target); var planning = new SqlitePlanningStore(target); var learning = new SqliteLearningStore(target, planning);
        if (!real) { Console.WriteLine("P4 synthetic seed ready; no learner data."); return 0; }
        var credentials = new SqliteSettingsStore(root); var settings = new SettingsService(credentials);
        var config = await settings.GetAsync(); if (!config.HasApiKey) { Console.WriteLine("AUTH_INVALID: saved credential required"); return 2; }
        using var client = DeepSeekProvider.CreateClient();
        var diagnostic = new DiagnosticProvider(new DeepSeekTheoryProvider(credentials, client));
        var generation = new GenerationService(planning, learning, learning, new KnowledgeStore(target), settings,
            new ModelEgress(diagnostic), new TheoryLessonParser());
        var requested = await generation.GenerateAsync(draft.Id, "A");
        if (!requested.Ok) { Console.WriteLine(requested.Error!.Code); return 1; }
        // Bounded by persistent three-dispatch policy, not an unbounded repair loop.
        for (var i = 0; i < 3; i++)
        {
            await generation.TickAsync(); var task = (await learning.TasksAsync()).Single();
            if (task.Status != GenerationStatus.Queued) break;
            await Task.Delay(3200);
        }
        var read = await learning.GetLessonAsync(draft.Id, "A");
        if (read.Ok)
        {
            // Deterministic host grading of real generated questions, without reading private answers.
            foreach (var q in read.Value!.Questions)
                foreach (var option in q.Options)
                {
                    var result = await learning.SubmitAnswerAsync(new(Guid.NewGuid(), 0, new(read.Value.Identity, q.Id, option.Id)));
                    if (result.Value?.Correct == true) break;
                }
        }
        var completed = await learning.GetLessonAsync(draft.Id, "A");
        if (completed.Value?.CompletedAt is not null)
        {
            await generation.GenerateAsync(draft.Id, "A", true);
            for (var i = 0; i < 3; i++)
            {
                await generation.TickAsync();
                if ((await learning.TasksAsync()).Last().Status != GenerationStatus.Queued) break;
                await Task.Delay(3200);
            }
        }
        var final = await learning.GetLessonAsync(draft.Id, "A"); var tasks = await learning.TasksAsync();
        var history = await learning.HistoryAsync(draft.Id, "A");
        var changed = final.Value?.Identity.LessonHash != completed.Value?.Identity.LessonHash;
        var preserved = !changed || (history.Count == 1 && final.Value!.Questions.All(q => !q.EverCorrect));
        var report = new { success = final.Value?.CompletedAt is not null && tasks.Count == 2 && tasks.All(t => t.Status == GenerationStatus.Ready) && preserved,
            regenerated = tasks.Count == 2, historyCount = history.Count, model = config.Model, isFake = false,
            tasks = tasks.Select(t => new { t.Status, t.Attempts, t.Failure, t.Usage }), questions = final.Value?.Questions.Count,
            completed = final.Value?.CompletedAt is not null, providerError = diagnostic.Error?.Message, realCodeEnabled = false, time = DateTimeOffset.UtcNow };
        await File.WriteAllTextAsync(Path.Combine(target, "real-theory-summary.json"), JsonSerializer.Serialize(report, Wire.Options));
        Console.WriteLine(JsonSerializer.Serialize(report, Wire.Options)); return report.success ? 0 : 1;
    }
    private sealed class DiagnosticProvider(IModelProvider inner) : IModelProvider
    {
        public string Destination => inner.Destination;
        public ApiError? Error { get; private set; }
        public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        { var reply = await inner.SendAsync(request, ct); Error = reply.Error; return reply; }
    }
    private static async Task<PlanningDraft> Seed(string root)
    {
        Directory.CreateDirectory(root); var plans = new SqlitePlanningStore(root);
        var existing = (await plans.ListAsync()).FirstOrDefault(d => d.Input.Request == "P4合成集合课程"); if (existing is not null) return existing;
        var library = Path.Combine(root, "p4-library"); Directory.CreateDirectory(library);
        var file = Path.Combine(root, "p4-synthetic.md");
        await File.WriteAllTextAsync(file, "# 集合基础（原创合成测试资料）\n\n集合是确定对象的全体。元素是集合中的对象。集合A={1,2}有两个元素，记作|A|=2。1属于A，3不属于A。重复列举元素不改变集合，例如{1,1,2}与{1,2}相同。空集没有元素。\n\n集合可以用列举法表示。判断元素是否属于集合需要清晰的判定条件；例如所有小于3的正整数形成{1,2}。元素的顺序不影响集合。\n");
        var knowledge = new KnowledgeStore(root);
        var chosen = (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(library)))).Value!;
        await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFilePicker(file)), chosen.Id);
        var reference = (await knowledge.SearchAsync(chosen.Id, "", [])).Fragments[0].Reference;
        var input = new PlanningInput("P4合成集合课程", "零基础", "能判断集合成员、重复与元素个数", 120, "Math", [reference]);
        var draft = (await plans.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
        var candidate = new PlanCandidate("P4合成数学", input.Background!, input.Goal!, 120, "Math", [], [], [], ["s1"],
            [new("A", "集合与成员", "theory", ["判断集合成员与元素个数"], [], 20, null),
             new("B", "列举集合", "theory", ["解释集合列举法"], [], 20, null),
             new("C", "重复元素", "theory", ["解释重复不改变集合"], ["A"], 20, null),
             new("D", "综合练习", "theory", ["综合判断集合"], ["A", "B"], 20, null)]);
        var document = PlanRules.Validate(draft.Id, input, candidate, [new("s1", reference, true)], null).Value!;
        var proposed = (await plans.SaveProposalAsync(new(Guid.NewGuid(), 0, document), draft.Id)).Value!;
        var confirmed = (await plans.ConfirmAsync(new(Guid.NewGuid(), proposed.Revision, new(draft.Id, document.Hash, true)))).Value!;
        await File.WriteAllTextAsync(Path.Combine(root, "p4-synthetic.marker"), "P4 synthetic fixture only"); return confirmed;
    }
}
