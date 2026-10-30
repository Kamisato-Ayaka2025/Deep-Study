using StudyHarness.Application;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class ExamGraphConflictsTests
{
    private static ModelSource Source(string id, string edges, bool exam = true) => new(id, 0,
        ModelSourceKind.OriginalMaterial, (exam ? "[宿主资料用途：考题参照]\n" : "") + edges);

    [Fact]
    public void ConflictingExamGraphs_DescribeBothWithoutChoosingAuthority()
    {
        var text = ExamGraphConflicts.Describe([
            Source("page13", "x-y权重6，x-v权重3，v-w权重4，y-z权重8，z-t权重12"),
            Source("page14", "y 与 x 相连，链路代价标注为 6\nv 与 x 相连，链路代价标注为 3\nw 与 v 相连，链路代价标注为 4\nz 与 y 相连，链路代价标注为 12\nz 与 x 相连，链路代价标注为 8")]);
        Assert.Contains("page13 与 page14", text);
        Assert.Contains("y-z=8", text); Assert.Contains("y-z=12", text);
        Assert.Contains("x-z=8", text); Assert.Contains("t-z=12", text);
        Assert.Contains("不能凭模型记忆", text);
    }

    [Fact]
    public void SameGraphSubsetAndTextbook_DoNotTriggerConflict()
    {
        var a = Source("a", "x-y 6，x-v 3，v-w 4，y-z 8");
        Assert.Empty(ExamGraphConflicts.Describe([a, Source("b", "y-x 6，v-x 3，w-v 4")]));
        Assert.Empty(ExamGraphConflicts.Describe([a, Source("book", "x-y 9，x-v 9，v-w 9，y-z 9", false)]));
    }
}
