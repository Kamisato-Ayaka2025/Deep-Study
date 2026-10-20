using System.Net;
using System.Text;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class AdaptiveCapabilityTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-capabilities", Guid.NewGuid().ToString("N"));
    private sealed class Credentials : ICredentialReader
    { public Task<string?> ReadKeyAsync(CancellationToken ct) => Task.FromResult<string?>("synthetic-key"); }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string Json { get; set; } = """{"data":[{"id":"test-model","context_window":1048576,"max_output_tokens":393216,"input_modalities":["text","image"]}]}""";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("https://api.deepseek.com/models", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Content); Assert.Equal("synthetic-key", request.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json) });
        }
    }
    [Fact]
    public async Task CapabilityCache_IsExactModelBoundAndSurvivesRestart()
    {
        var handler = new Handler(); using var client = new HttpClient(handler);
        var source = new DeepSeekCapabilities(root, new Credentials(), client);
        var value = await source.GetAsync("test-model"); Assert.True(value.Verified); Assert.True(value.SupportsImages);
        Assert.True((await new DeepSeekCapabilities(root, new Credentials(), client).GetAsync("test-model")).Verified);
        Assert.Equal(1, handler.Calls);
        Assert.False((await source.GetAsync("other-model")).Verified); Assert.Equal(2, handler.Calls);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":[]}")]
    [InlineData("{\"data\":[{\"id\":\"test-model\",\"context_window\":100,\"max_output_tokens\":1000}]}")]
    public void InvalidCapability_UsesConservativeNonVisualLimits(string json)
    {
        var result = DeepSeekCapabilities.Parse(Encoding.UTF8.GetBytes(json), "test-model", DateTimeOffset.UtcNow);
        Assert.False(result.Verified); Assert.False(result.SupportsImages); Assert.Equal(4096, result.MaxOutputTokens);
    }
    [Fact]
    public void LongLessonSchema_RequiresCompleteQuizAndEnforcesTotalSize()
    {
        var id = Guid.NewGuid(); PlanSource[] sources = [new("s1", new(id, id, id, new string('A', 64), new("text", null, null, "1")), false)];
        var lesson = new TheoryLesson([new("a", "example", new string('中', 16384)), new("q", "question", QuestionId: "q1")],
            [new("q1", "答案？", [new("a", "甲"), new("b", "乙")], "a", "依据定义。")], ["s1"], 2);
        var parser = new TheoryLessonParser();
        Assert.True(parser.Parse(JsonSerializer.Serialize(lesson, Wire.Options), sources).Ok);
        var escapedLong = lesson with { Blocks = Enumerable.Range(0, 4).Select(i => new LessonBlock("b" + i, "example", new string('中', 16000))).Append(new("q", "question", QuestionId: "q1")).ToArray() };
        var escapedJson = JsonSerializer.Serialize(escapedLong, Wire.Options);
        Assert.True(escapedJson.Length > 262144); Assert.True(parser.Parse(escapedJson, sources).Ok);
        Assert.False(parser.Parse(JsonSerializer.Serialize(escapedLong with { SchemaVersion = 1 }, Wire.Options), sources).Ok);
        var equivalentExample = lesson with { Blocks = [new("h", "heading", "完整具体例子"), new("p", "paragraph", "取集合A={1,2}，其中有两个不同元素。"), new("q", "question", QuestionId: "q1")] };
        Assert.True(parser.Parse(JsonSerializer.Serialize(equivalentExample, Wire.Options), sources).Ok);
        Assert.False(parser.Parse(JsonSerializer.Serialize(equivalentExample with { Blocks = equivalentExample.Blocks.Skip(1).ToArray() }, Wire.Options), sources).Ok);
        Assert.False(parser.Parse(JsonSerializer.Serialize(lesson with { Questions = [] }, Wire.Options), sources).Ok);
        var tooLong = lesson with { Blocks = Enumerable.Range(0, 5).Select(i => new LessonBlock("b" + i, "example", new string('x', 16384))).Append(new("q", "question", QuestionId: "q1")).ToArray() };
        Assert.False(LessonRules.Validate(tooLong, sources).Ok);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
