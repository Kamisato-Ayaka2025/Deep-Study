using System.Net;
using System.Text;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class RetrievalTests
{
    private sealed class Key : ICredentialReader { public Task<string?> ReadKeyAsync(CancellationToken ct) => Task.FromResult<string?>("synthetic-secret"); }
    private sealed class Handler(Func<int, string> response) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.OK) { Content = new StringContent(response(Requests.Count), Encoding.UTF8, "application/json") };
        }
    }
    private static string Call(string name = "read_material", string arguments = "{\"id\":\"s1\"}") => JsonSerializer.Serialize(new {
        choices = new[] { new { message = new { content = (string?)null, tool_calls = new[] { new { id = "call1", type = "function", function = new { name, arguments } } } }, finish_reason = "tool_calls" } },
        usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 } });
    private const string Final = """{"choices":[{"message":{"content":"{}"},"finish_reason":"stop"}],"usage":{"prompt_tokens":20,"completion_tokens":6,"total_tokens":26}}""";
    private static ModelReview Review(ModelEgress gate) => gate.Prepare("deepseek-flash", [new("prompt", 0, ModelSourceKind.UserText, "Return JSON")], 8192, true,
        [new("s1", 0, ModelSourceKind.OriginalMaterial, "requested-body-sentinel", "project/main.c"), new("s2", 0, ModelSourceKind.OriginalMaterial, "unread-body-sentinel", "other.md")]);
    [Fact]
    public async Task ToolLoop_OnlyRequestedBodyIsSent_ConsentCoversFrozenScope_UsageAccumulates()
    {
        using var handler = new Handler(n => n == 1 ? Call() : Final); using var client = new HttpClient(handler);
        var gate = new ModelEgress(new DeepSeekProvider(new Key(), client)); var review = Review(gate);
        var consent = gate.Approve(review, ConsentMode.Each);
        Assert.False((await gate.SendAsync(Review(gate), consent)).Ok); Assert.Empty(handler.Requests);
        var reply = await gate.SendAsync(review, consent);
        Assert.True(reply.Ok, reply.Error?.Message); Assert.Equal(new TokenUsage(30, 11, 41), reply.Value!.Usage);
        Assert.Equal(["s1"], reply.Value.ReadSourceIds); Assert.Equal(1, reply.Value.ToolCalls);
        Assert.DoesNotContain("requested-body-sentinel", handler.Requests[0]);
        Assert.Contains("requested-body-sentinel", handler.Requests[1]);
        Assert.All(handler.Requests, r => { Assert.DoesNotContain("unread-body-sentinel", r); Assert.DoesNotContain("synthetic-secret", r); });
        using var next = JsonDocument.Parse(handler.Requests[1]);
        var reminder = next.RootElement.GetProperty("messages").EnumerateArray().Last().GetProperty("content").GetString();
        Assert.Contains("当前已读资料id=s1。", reminder);
        Assert.Equal(8187, next.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False((await gate.SendAsync(review, consent)).Ok); Assert.Equal(2, handler.Requests.Count);
    }
    [Theory]
    [InlineData("read_material", "{\"id\":\"../../secret\"}")]
    [InlineData("read_material", "{\"id\":\"s99\"}")]
    [InlineData("read_material", "{\"id\":\"s1\",\"path\":\"C:/secret\"}")]
    [InlineData("read_material", "{\"id\":\"s1\",\"id\":\"s2\"}")]
    [InlineData("execute", "{}")]
    [InlineData("search_materials", "{\"query\":\"\"}")]
    public async Task InvalidTool_CannotExpandScope_AndNeverRetries(string name, string arguments)
    {
        using var handler = new Handler(_ => Call(name, arguments)); using var client = new HttpClient(handler);
        var gate = new ModelEgress(new DeepSeekProvider(new Key(), client)); var review = Review(gate);
        var result = await gate.SendAsync(review, gate.Approve(review, ConsentMode.Each));
        Assert.Equal(ErrorCode.INVALID_MODEL_OUTPUT, result.Error!.Code); Assert.Single(handler.Requests);
    }
    [Fact]
    public async Task RepeatedCalls_StopAtRoundBudget_AndUnreadyFinalIsRejected()
    {
        using var handler = new Handler(_ => Call()); using var client = new HttpClient(handler);
        var gate = new ModelEgress(new DeepSeekProvider(new Key(), client)); var review = Review(gate);
        Assert.False((await gate.SendAsync(review, gate.Approve(review, ConsentMode.Each))).Ok);
        Assert.Equal(8, handler.Requests.Count);
        using var early = new Handler(_ => Final); using var earlyClient = new HttpClient(early);
        var earlyGate = new ModelEgress(new DeepSeekProvider(new Key(), earlyClient)); var earlyReview = Review(earlyGate);
        Assert.False((await earlyGate.SendAsync(earlyReview, earlyGate.Approve(earlyReview, ConsentMode.Each))).Ok);
    }
    [Fact]
    public void ChineseToolBodyStaysReadableWithoutChangingContent()
    {
        const string body = "二分查找与前缀和：\"引号\"\n<script>只是资料文本</script>";
        var session = new MaterialRetrieval([new("s1", 0, ModelSourceKind.OriginalMaterial, body, "中文资料")]);
        var result = session.Invoke("read_material", "{\"id\":\"s1\"}", default);
        Assert.Contains("二分查找", result);
        using var parsed = JsonDocument.Parse(result);
        Assert.Equal(body, parsed.RootElement.GetProperty("text").GetString());
    }
    [Fact]
    public void SearchDoesNotReadBodies_CancellationAndCharacterBudgetAreEnforced()
    {
        var session = new MaterialRetrieval([new("s1", 0, ModelSourceKind.OriginalMaterial, new string('a', 16000), "main.c")]);
        Assert.Contains("s1", session.Invoke("search_materials", "{\"query\":\"main\"}", default));
        Assert.Empty(session.ReadIds);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => session.Invoke("read_material", "{\"id\":\"s1\"}", cts.Token));
        for (var i = 0; i < 4; i++) session.Invoke("read_material", "{\"id\":\"s1\"}", default);
        Assert.Throws<InvalidDataException>(() => session.Invoke("read_material", "{\"id\":\"s1\"}", default));
    }
}
