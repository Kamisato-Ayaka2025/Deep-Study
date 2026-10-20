using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

// Explicit synthetic acceptance input, never selected by the ordinary product composition root.
public sealed class LabFixtureProvider : IModelProvider
{
    public const string SourceText = "# 原创合成C教学资料\n\n标准C可用stdio.h读取整数并输出整数。求两个-100至100的整数之和，读取返回值必须等于2，先检查范围。1和2之和为3，-2和2之和为0。学生应在TODO处补全加法与输出，勿提供完整解答。后续课可扩展为三个整数求和，用户自行衔接前课代码。不得读写文件、网络或执行系统命令。\n";
    public static PlannedNode FirstNode => new("L1", "两个整数求和", "lab", ["补全TODO并自行测评"], [], 30, new("independent", null, "portable-c", ["s1"], "简单C标准输入输出"));
    public int Calls { get; private set; }
    public string Destination => "local:deterministic-fake";
    public static LabMaterial Material => new("# 求和实验\n补全 main.c 中 add 函数的 TODO。输入范围 -100 至 100。",
        [new("main.c", "#include \"solution.h\"\nint add(int a, int b) {\n    /* TODO: 返回 a 与 b 的和 */\n    return 0;\n}\n"),
         new("solution.h", "int add(int a, int b);\n")],
        [new("tests/cases.json", "[{\"id\":\"sum\",\"input\":\"1 2\\n\",\"expected\":\"3\\n\"}]"),
         new("test.c", "#include <assert.h>\n#include <stdio.h>\n#include \"solution.h\"\nint main(void) { assert(add(1, 2) == 3); puts(\"All tests passed\"); return 0; }\n")],
        [new("sum", "1 2\n", "3\n")],
        "## 自行测评\n在 files 目录用标准C编译器编译 main.c 和 test.c（如 cc main.c test.c -o test），不要定义 NDEBUG。自行运行 test；全部断言通过显示 All tests passed，否则断言失败退出。补全TODO后重新检查。",
        "检查代码后自行运行，软件没有验证结果。", ["s1"], 3, "exercise", "单个有界求和函数，可直接调用并断言结果。", []);
    public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(Result<ProviderReply>.Success(new(JsonSerializer.Serialize(Material, Wire.Options), true))); }
    public static async Task<PlanningDraft> SeedAsync(string root)
    {
        Directory.CreateDirectory(root); var plans = new SqlitePlanningStore(root);
        var existing = (await plans.ListAsync()).FirstOrDefault(d => d.Input.Request == "P5合成C材料"); if (existing is not null) return existing;
        var library = Path.Combine(root, "p5-library"); Directory.CreateDirectory(library);
        var file = Path.Combine(root, "p5-synthetic.md");
        await File.WriteAllTextAsync(file, SourceText);
        var knowledge = new KnowledgeStore(root);
        var current = await knowledge.CurrentAsync();
        var chosen = (await knowledge.SwitchAsync(new(Guid.NewGuid(), current?.Revision ?? 0, HostSelection.FromFolderPicker(library)))).Value!;
        await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFilePicker(file)), chosen.Id);
        var reference = (await knowledge.SearchAsync(chosen.Id, "", [])).Fragments[0].Reference;
        var input = new PlanningInput("P5合成C材料", "认识变量", "自行完成有界整数求和实验", 120, "SimpleC", [reference]);
        var draft = (await plans.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
        var candidate = new PlanCandidate("合成C材料课程", input.Background!, input.Goal!, 120, "SimpleC", [], [], [], ["s1"],
            [FirstNode,
             new("L2", "三个整数求和", "lab", ["自行衔接前课并扩展"], ["L1"], 30, new("continuous", "L1", "portable-c", ["s1"], "延续前课教学")),
             new("T3", "总结输入边界", "theory", ["解释边界"], ["L2"], 20, null)]);
        var validated = PlanRules.Validate(draft.Id, input, candidate, [new("s1", reference, true)], null);
        if (!validated.Ok) throw new InvalidDataException("Synthetic plan invalid");
        var proposed = (await plans.SaveProposalAsync(new(Guid.NewGuid(), 0, validated.Value!), draft.Id)).Value!;
        return (await plans.ConfirmAsync(new(Guid.NewGuid(), proposed.Revision, new(draft.Id, validated.Value!.Hash, true)))).Value!;
    }
}
