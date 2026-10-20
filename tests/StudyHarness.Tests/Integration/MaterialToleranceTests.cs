using System.Text.Json;
using System.Text.Json.Nodes;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class MaterialToleranceTests
{
    private static readonly PlanSource[] Sources = [new("s1", new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('A', 64), new("fixture", null, null, "1")), false)];
    private static JsonObject Lesson() => JsonSerializer.SerializeToNode(new TheoryLesson(
        [new("b1", "paragraph", "二分每次缩小一半。"), new("b2", "example", "8个候选缩为4个。")],
        [new("q1", "8减半是多少？", [new("a", "4"), new("b", "8")], "a", "8除以2等于4。")], ["s1"]), Wire.Options)!.AsObject();

    [Fact]
    public void CompleteContentAllowsWrapperMetadataAndHostQuestionPlacement()
    {
        var node = Lesson(); node.Remove("schemaVersion"); node.Remove("sourceKeys"); node["title"] = "二分";
        var result = new TheoryLessonParser().Parse("```json\n" + node.ToJsonString() + "\n```", Sources);
        Assert.True(result.Ok, result.Error?.Message);
        Assert.Single(result.Value!.Questions);
        Assert.Equal("q1", result.Value.Blocks[^1].QuestionId);
        Assert.Equal("s1", Assert.Single(result.Value.SourceKeys));
    }

    [Fact]
    public void KnownNullAnnotations_DoNotChangeContent_OtherFieldsStillReject()
    {
        var parser = new TheoryLessonParser(); var node = Lesson();
        var baseline = parser.Parse(node.ToJsonString(), Sources).Value;
        node["blocks"]![1]!["kindnote"] = null;
        node["questions"]![0]!["questionId"] = null;
        var parsed = parser.Parse(node.ToJsonString(), Sources);
        Assert.True(parsed.Ok);
        Assert.Equal(JsonSerializer.Serialize(baseline, Wire.Options), JsonSerializer.Serialize(parsed.Value, Wire.Options));
        node["blocks"]![1]!["kindnote"] = "content must not disappear";
        Assert.False(parser.Parse(node.ToJsonString(), Sources).Ok);
        node["blocks"]![1]!.AsObject().Remove("kindnote");
        node["questions"]![0]!["questionId"] = "different";
        Assert.False(parser.Parse(node.ToJsonString(), Sources).Ok);
        node["questions"]![0]!.AsObject().Remove("questionId");
        node["blocks"]![1]!["script"] = null;
        Assert.False(parser.Parse(node.ToJsonString(), Sources).Ok);
    }

    [Theory]
    [InlineData("questions")]
    [InlineData("answer")]
    [InlineData("source")]
    [InlineData("body")]
    public void ToleranceDoesNotInventMissingLearningContent(string invalid)
    {
        var node = Lesson();
        if (invalid == "questions") node.Remove("questions");
        if (invalid == "answer") node["questions"]![0]!.AsObject().Remove("correctOptionId");
        if (invalid == "source") node["sourceKeys"] = new JsonArray("unknown");
        if (invalid == "body") node["blocks"] = new JsonArray();
        Assert.False(new TheoryLessonParser().Parse(node.ToJsonString(), Sources).Ok);
    }

    [Fact]
    public void EqualParameterComplexityChoices_RejectWithoutChangingAnswers()
    {
        var node = Lesson(); node["schemaVersion"] = 2;
        node["questions"]![0]!["prompt"] = "n=100000，q=100000，总复杂度可写成什么？";
        node["questions"]![0]!["options"] = new JsonArray(new JsonObject { ["id"] = "a", ["text"] = "O(n+q)" }, new JsonObject { ["id"] = "b", ["text"] = "O(q)" });
        var parser = new TheoryLessonParser();
        Assert.False(parser.Parse(node.ToJsonString(), Sources).Ok);
        node["questions"]![0]!["prompt"] = "n和q独立变化，预处理n项再查询q次的最紧渐近界？";
        Assert.True(parser.Parse(node.ToJsonString(), Sources).Ok);
        node["questions"]![0]!["options"]![1]!["text"] = "O(q+n)";
        Assert.False(parser.Parse(node.ToJsonString(), Sources).Ok);
        node["questions"]![0]!["options"]![0]!["text"] = "O(n+n)";
        node["questions"]![0]!["options"]![1]!["text"] = "O(n)";
        Assert.False(parser.Parse(node.ToJsonString(), Sources).Ok);
    }
    [Fact]
    public void ExerciseOptionalEmptyMetadataAndCommentsDoNotPreventExport()
    {
        var raw = CppFixture.RawMaterial;
        var text = raw.SourceFiles[0].Text.Replace("/* TODO:", "/* TODO: 不要使用 cin cout printf scanf;");
        var node = JsonSerializer.SerializeToNode(raw with { SourceFiles = [new("solution.cpp", text)] }, Wire.Options)!.AsObject();
        node.Remove("manualChecks"); node["title"] = "练习";
        var result = LabMaterialRules.Parse("```json\n" + node.ToJsonString() + "\n```", ["s1"]);
        Assert.True(result.Ok, result.Error?.Message);
        Assert.Contains("assert(out.str()", result.Value!.TestFiles.Single(f => f.Path == "test.cpp").Text);
        node["sourceFiles"]![0]!["text"] = text.Replace("void solve", "void bad() { std::cout << 1; }\nvoid solve");
        Assert.False(LabMaterialRules.Parse(node.ToJsonString(), ["s1"]).Ok);
    }
}
