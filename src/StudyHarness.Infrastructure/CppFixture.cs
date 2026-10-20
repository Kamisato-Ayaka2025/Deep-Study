using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

// Synthetic regression data only, never the user's learning record.
public sealed class CppFixture : IModelProvider
{
    public const string SourceText = "# C++算法竞赛合成教材\n\n本课程只教授两个算法：二分查找与一维前缀和。已有C++基础。二分查找在升序数组中找到第一个不小于x的位置，不存在返回n；维护半开区间[l,r)，中点l+(r-l)/2，每轮区间缩小。输入n x后接n个整数，输出位置及换行。0<=n<=100，元素和x在[-1000,1000]。示例3 2及1 2 4输出1；空数组输出0；重复值取最左位置。前缀和计算p[0]=0，p[i+1]=p[i]+a[i]，区间[l,r)之和为p[r]-p[l]。输入n l r后接n个整数，输出区间和及换行，0<=l<=r<=n<=100，元素[-1000,1000]，使用long long。示例3 0 2及1 2 4输出3；空区间输出0。每算法一节理论及一道单文件C++实验题，实验依赖理论；题目保留TODO，无完整答案。使用solve(std::istream& in,std::ostream& out)，标准输入输出，全部测试在外部。";
    public static PlanCandidate Candidate(PlanningInput input) => new("C++算法竞赛", input.Background!, input.Goal!, 120, "CppAlgorithms", [], [], [], ["s1"],
        [new("n1", "二分查找与前缀和", "theory", ["解释二分查找与前缀和"], [], 30, null, ["a1", "a2"]),
         new("n2", "二分查找题", "lab", ["实现二分查找的标准输入输出"], ["n1"], 30, new("independent", null, "portable-cpp", ["s1"], "有界算法题"), ["a1"]),
         new("n3", "前缀和题", "lab", ["实现区间和的标准输入输出"], ["n1"], 30, new("independent", null, "portable-cpp", ["s1"], "有界算法题"), ["a2"])],
        Algorithms: [new("a1", "二分查找"), new("a2", "前缀和")]);
    public static LabMaterial RawMaterial
    {
        get
        {
            LabCase[] cases = [new("normal", "3 2\n1 2 4\n", "1\n"), new("empty", "0 2\n", "0\n"), new("duplicate", "3 2\n2 2 4\n", "0\n")];
            return new("# 二分查找\n输入n x及升序数组，输出第一个不小于x的位置，不存在输出n。0<=n<=100，值在[-1000,1000]。示例：3 2 / 1 2 4，输出1。完成TODO。",
                [new("solution.cpp", "#include <iostream>\nvoid solve(std::istream& in, std::ostream& out) {\n    /* TODO: read bounded input and implement binary search */\n}\n" + CppLabRules.MainWrapper + "\n")],
                [new("tests/cases.json", JsonSerializer.Serialize(cases, Wire.Options))], cases, "## 测评\n先检查正常、空数组和重复值。", "未经运行验证。", ["s1"], 4, "exercise", "单文件算法题，标准输入输出不构成工程。", []);
        }
    }
    public string Destination => "local:deterministic-fake";
    public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct) => Task.FromResult(Result<ProviderReply>.Success(new(JsonSerializer.Serialize(RawMaterial, Wire.Options), true)));

    public static async Task<PlanningDraft> StartAsync(string root)
    {
        Directory.CreateDirectory(root);
        var library = Path.Combine(root, "cpp-library"); Directory.CreateDirectory(library);
        var file = Path.Combine(root, "cpp-synthetic.md"); await File.WriteAllTextAsync(file, SourceText);
        var knowledge = new KnowledgeStore(root); var current = await knowledge.CurrentAsync();
        var chosen = (await knowledge.SwitchAsync(new(Guid.NewGuid(), current?.Revision ?? 0, HostSelection.FromFolderPicker(library)))).Value!;
        var imported = await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFilePicker(file)), chosen.Id);
        if (!imported.Ok) throw new InvalidDataException("Synthetic source import failed");
        var reference = (await knowledge.SearchAsync(chosen.Id, "", [])).Fragments[0].Reference;
        return (await new SqlitePlanningStore(root).StartAsync(new(Guid.NewGuid(), 0,
            new("学习C++下的算法竞赛，仅二分查找和一维前缀和，每个算法理论及一道题", "已掌握C++基础", "掌握二分查找和一维前缀和并完成对应算法题", 120, "CppAlgorithms", [reference])))).Value!;
    }
    public static async Task<PlanningDraft> SeedAsync(string root)
    {
        var plans = new SqlitePlanningStore(root); var existing = (await plans.ListAsync()).FirstOrDefault(d => d.Input.Scope == "CppAlgorithms");
        if (existing is not null) return existing;
        var draft = await StartAsync(root);
        var doc = PlanRules.Validate(draft.Id, draft.Input, Candidate(draft.Input), [new("s1", draft.Input.PreferredReferences[0], true)], null);
        if (!doc.Ok) throw new InvalidDataException(doc.Error!.Message);
        var proposed = (await plans.SaveProposalAsync(new(Guid.NewGuid(), draft.Revision, doc.Value!), draft.Id)).Value!;
        return (await plans.ConfirmAsync(new(Guid.NewGuid(), proposed.Revision, new(draft.Id, doc.Value!.Hash, true)))).Value!;
    }

    public static async Task CompleteSyntheticTheoryAsync(PlanningDraft draft, SqliteLearningStore store)
    {
        var lesson = new TheoryLesson([new("intro", "paragraph", "二分查找缩小有序区间，前缀和用两个前缀之差回答区间和。"), new("example", "example", "数组1,2,4中第一个不小于2的下标为1。"), new("last", "question", QuestionId: "q")],
            [new("q", "二分查找每轮如何推进？", [new("a", "缩小区间"), new("b", "无限扩大区间")], "a", "区间缩小保证结束。")], ["s1"]);
        var read = await store.GetLessonAsync(draft.Id, "n1");
        var identity = read.Ok ? read.Value!.Identity : (await store.PublishAsync(new(Guid.NewGuid(), 0, lesson), draft.Id, "n1", draft.Plan!.Hash, draft.Plan.Sources)).Value!;
        var answer = await store.SubmitAnswerAsync(new(Guid.NewGuid(), 0, new(identity, "q", "a")));
        if (!answer.Ok || !answer.Value!.Completed) throw new InvalidDataException("Synthetic theory progression failed");
    }
}
