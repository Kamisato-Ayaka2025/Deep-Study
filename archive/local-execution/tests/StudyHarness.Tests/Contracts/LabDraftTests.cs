using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Desktop;
using Xunit;

namespace StudyHarness.Tests.Contracts;

public sealed class LabDraftTests
{
    private static readonly PlannedLab Plan = new("independent", null, "msvc", ["s1"], "synthetic");
    private static LabDraft Draft(int count = 1) => new("Print a number.",
        [new("main.c", "int main(void) { /* TODO: implement */ return 0; }")],
        [new("main.c", "/* PRIVATE_REFERENCE_SENTINEL */ int main(void) { return 0; }")],
        Enumerable.Range(1, count).Select(i => new LabCase("case" + i, "", "")).ToArray(), [], ["s1"]);
    [Theory]
    [InlineData(0, false)] [InlineData(1, true)] [InlineData(20, true)] [InlineData(21, false)]
    public void TestCount_IsNonemptyAndBounded(int count, bool valid) => Assert.Equal(valid, LabDraftRules.Validate(Draft(count), Plan, ["s1"]).Ok);

    [Theory]
    [InlineData("../main.c")] [InlineData("C:/main.c")] [InlineData("/main.c")]
    [InlineData("a\\main.c")] [InlineData("NUL.c")] [InlineData("dir/Com1.h")]
    [InlineData("main.c:stream")] [InlineData("a./main.c")] [InlineData("a//main.c")]
    [InlineData("main.cpp")] [InlineData("build.cmd")] [InlineData("a /main.c")]
    public void UnsafeOrUnsupportedNames_AreRejected(string path) => Assert.False(LabDraftRules.IsFileName(path));

    [Fact]
    public void Names_CannotAliasOrCollideAsDirectory()
    {
        var d = Draft();
        Assert.False(LabDraftRules.Validate(d with { StarterFiles = [d.StarterFiles[0], new("MAIN.c", "/* TODO */")] }, Plan, ["s1"]).Ok);
        Assert.False(LabDraftRules.Validate(d with { StarterFiles = [d.StarterFiles[0], new("main.c/child.h", "/* TODO */")] }, Plan, ["s1"]).Ok);
        Assert.True(LabDraftRules.IsFileName("src/hello_world.c"));
    }

    [Theory]
    [InlineData("/* TODO: finish */", true)] [InlineData("// TODO: finish\n", true)]
    [InlineData("char *s = \"/* TODO */\";", false)] [InlineData("/* incomplete TODO", false)]
    [InlineData("int TODO = 0;", false)]
    public void Todo_MustBeInAClosedComment(string code, bool expected) => Assert.Equal(expected, LabDraftRules.HasTodo(code));

    [Fact]
    public void InvalidSourcesNullsDuplicatesAndOversize_AreRejected()
    {
        var d = Draft();
        Assert.False(LabDraftRules.Validate(d, Plan, []).Ok);
        Assert.False(LabDraftRules.Validate(d with { SourceKeys = ["s1", "s1"] }, Plan, ["s1"]).Ok);
        Assert.False(LabDraftRules.Validate(d with { SourceKeys = ["s2"] }, Plan, ["s2"]).Ok);
        Assert.False(LabDraftRules.Validate(d with { Cases = [d.Cases[0], d.Cases[0]] }, Plan, ["s1"]).Ok);
        Assert.False(LabDraftRules.Validate(d with { Cases = [new("c", new string('a', 16385), "")] }, Plan, ["s1"]).Ok);
        Assert.False(LabDraftRules.Validate(d with { ReferenceFiles = null! }, Plan, ["s1"]).Ok);
        Assert.False(LabDraftRules.Validate(d with { StarterFiles = [new("main.c", new string('a', 65537))] }, Plan, ["s1"]).Ok);
        Assert.False(LabDraftRules.Validate(d with { SchemaVersion = 2 }, Plan, ["s1"]).Ok);
        Assert.False(LabDraftRules.Validate(null, Plan, ["s1"]).Ok);
    }

    [Fact]
    public void Preview_DoesNotSerializePrivateReference()
    {
        var json = JsonSerializer.Serialize(LabDraftRules.Preview(Draft()), Wire.Options);
        Assert.DoesNotContain("PRIVATE_REFERENCE_SENTINEL", json);
        Assert.DoesNotContain("referenceFiles", json);
        Assert.Contains("TODO", json);
    }

    [Theory] [InlineData(true)] [InlineData(false)]
    public void CompletionOrPrerequisites_CannotOpenExecution(bool eligible)
    {
        var status = LabExecutionBoundary.Availability(eligible);
        Assert.False(status.CanGenerate || status.CanValidateReference || status.CanStart || status.CanJudge || status.CanRestore);
        Assert.Equal(ErrorCode.ISOLATION_UNAVAILABLE, LabExecutionBoundary.Refuse<Guid>().Error!.Code);
        var model = LabPreviewModel.From(new("L1", "C lab", "lab", ["write C"], [], 30, Plan), eligible);
        Assert.Equal(status, model.Availability);
        Assert.Contains("msvc", model.Plan);
    }

    public sealed record Fixture(string Key, string Title, PlannedLab Plan, LabDraft Draft);
    [Theory] [InlineData("msvc")] [InlineData("mingw")]
    public void IndependentAndFourLessonFixtures_ValidateWithoutExecution(string toolchain)
    {
        var fixtures = JsonSerializer.Deserialize<Fixture[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures/p5/continuous-four.json")), Wire.Options)!;
        Assert.Equal(4, fixtures.Length);
        Assert.Equal("independent", fixtures[0].Plan.Mode);
        for (var i = 0; i < fixtures.Length; i++)
        {
            var f = fixtures[i];
            Assert.True(LabDraftRules.Validate(f.Draft, f.Plan with { Toolchain = toolchain }, ["s1"]).Ok);
            if (i > 0) Assert.Equal(fixtures[i - 1].Key, f.Plan.PreviousLabKey);
        }
        // Valid structure is explicitly not reference validation, Judge evidence or mastery.
        Assert.False(LabExecutionBoundary.Availability(true).CanStart);
    }

    [Fact]
    public void ContinuousPatches_CannotReplaceNewFilesOrRepeatAnchors()
    {
        var plan = Plan with { Mode = "continuous", PreviousLabKey = "L1" };
        var insertion = new LabTodoInsertion("previous.c", "/* anchor */", "/* TODO: extend */");
        var d = Draft() with { Insertions = [insertion] };
        Assert.True(LabDraftRules.Validate(d, plan, ["s1"]).Ok);
        Assert.False(LabDraftRules.Validate(d with { Insertions = [insertion, insertion] }, plan, ["s1"]).Ok);
        Assert.False(LabDraftRules.Validate(d with { Insertions = [insertion with { Path = "main.c" }] }, plan, ["s1"]).Ok);
        Assert.False(LabDraftRules.Validate(d, Plan, ["s1"]).Ok);
    }
}
