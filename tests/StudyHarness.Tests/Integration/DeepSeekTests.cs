using System.Net;
using System.Text;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;
namespace StudyHarness.Tests.Integration;
public sealed class DeepSeekTests
{
    private sealed class Key : ICredentialReader { public Task<string?> ReadKeyAsync(CancellationToken ct) => Task.FromResult<string?>("synthetic-test-secret"); }
    private sealed class Handler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls; public string? Payload; public Uri? Destination; public string? Authorization;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Payload = await request.Content!.ReadAsStringAsync(ct); Destination = request.RequestUri; Authorization = request.Headers.Authorization?.Parameter;
            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
    private const string Success = """{"choices":[{"message":{"content":"A variable stores a value."},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":6,"total_tokens":16}}""";
    [Fact]
    public async Task ConnectionIdentityIsBasedOnRequestAndDoesNotRewriteChatGptReply()
    {
        var root = Path.Combine(Path.GetTempPath(), "connection-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string answer = "我是 ChatGPT，由 OpenAI 开发。";
            var response = Success.Replace("A variable stores a value.", answer);
            using var handler = new Handler(HttpStatusCode.OK, response);
            using var client = new HttpClient(handler);
            var settings = new SettingsService(new SqliteSettingsStore(root));
            var vm = new StudyHarness.Desktop.ShellViewModel(settings,
                new(new DeepSeekProvider(new Key(), client)), new SafeLog(Path.Combine(root, "log.jsonl")));
            await vm.LoadAsync();
            var configured = vm.Model;
            await vm.CheckConnectionAsync();
            Assert.Equal(1, handler.Calls);
            Assert.Equal("https://api.deepseek.com/chat/completions", handler.Destination!.ToString());
            using var body = JsonDocument.Parse(handler.Payload!);
            Assert.Equal(configured, body.RootElement.GetProperty("model").GetString());
            var message = Assert.Single(body.RootElement.GetProperty("messages").EnumerateArray());
            Assert.Equal("user", message.GetProperty("role").GetString());
            Assert.Equal("你是什么模型？", message.GetProperty("content").GetString());
            Assert.Contains("请求地址：https://api.deepseek.com/chat/completions", vm.Status);
            Assert.Contains("请求模型：" + configured, vm.Status);
            Assert.Contains("不能作为身份认证", vm.Status);
            Assert.Contains("模型原始回复：\n" + answer, vm.Status);
            Assert.DoesNotContain("我是 DeepSeek", vm.Status);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Consent_DenialEachOnceAndChangedPayload_ActualHttpBytesMatchDisclosure()
    {
        using var handler = new Handler(HttpStatusCode.OK, Success); using var client = new HttpClient(handler);
        var egress = new ModelEgress(new DeepSeekProvider(new Key(), client));
        var review = egress.Prepare("configured-model", [new("a", 0, ModelSourceKind.OriginalMaterial, "reviewed text")]);
        Assert.Equal(ErrorCode.CONSENT_REQUIRED, (await egress.SendAsync(review, null)).Error!.Code); Assert.Equal(0, handler.Calls);
        var denied = egress.Approve(review, ConsentMode.Each); egress.Revoke(denied);
        Assert.False((await egress.SendAsync(review, denied)).Ok); Assert.Equal(0, handler.Calls);
        var each = egress.Approve(review, ConsentMode.Each);
        var reply = await egress.SendAsync(review, each); Assert.True(reply.Ok); Assert.False(reply.Value!.IsFake); Assert.Equal(new TokenUsage(10, 6, 16), reply.Value.Usage);
        Assert.Equal("https://api.deepseek.com/chat/completions", handler.Destination!.ToString()); Assert.Equal("synthetic-test-secret", handler.Authorization);
        using var body = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("configured-model", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("reviewed text", body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.DoesNotContain("synthetic-test-secret", handler.Payload!);
        Assert.False((await egress.SendAsync(review, each)).Ok); Assert.Equal(1, handler.Calls);
        var once = egress.Approve(review, ConsentMode.Once); Assert.True((await egress.SendAsync(review, once)).Ok); Assert.True((await egress.SendAsync(review, once)).Ok);
        var changed = egress.Prepare("configured-model", [new("a", 1, ModelSourceKind.OriginalMaterial, "changed text")]);
        Assert.False((await egress.SendAsync(changed, once)).Ok); Assert.Equal(3, handler.Calls);
    }
    [Theory]
    [InlineData(401, ErrorCode.AUTH_INVALID)] [InlineData(402, ErrorCode.QUOTA_EXHAUSTED)]
    [InlineData(400, ErrorCode.MODEL_INVALID)] [InlineData(404, ErrorCode.MODEL_INVALID)]
    [InlineData(429, ErrorCode.TRANSIENT_PROVIDER_ERROR)] [InlineData(503, ErrorCode.TRANSIENT_PROVIDER_ERROR)]
    public async Task Errors_AreSanitizedAndNeverRetried(int status, ErrorCode expected)
    {
        using var handler = new Handler((HttpStatusCode)status, "synthetic-test-secret private-content"); using var client = new HttpClient(handler);
        var gate = new ModelEgress(new DeepSeekProvider(new Key(), client)); var review = gate.Prepare("test", [new("a", 0, ModelSourceKind.UserText, "hello")]);
        var result = await gate.SendAsync(review, gate.Approve(review, ConsentMode.Each));
        Assert.Equal(expected, result.Error!.Code); Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain("synthetic-test-secret", JsonSerializer.Serialize(result)); Assert.DoesNotContain("private-content", JsonSerializer.Serialize(result));
    }
    [Theory]
    [InlineData("{}")] [InlineData("not-json")]
    [InlineData("{\"choices\":[]}")]
    [InlineData("{\"choices\":[{\"message\":{\"content\":\"synthetic-test-secret\"},\"finish_reason\":\"stop\"}]}")]
    public async Task InvalidOutput_NotReportedAsSuccess(string body)
    {
        using var handler = new Handler(HttpStatusCode.OK, body); using var client = new HttpClient(handler);
        var gate = new ModelEgress(new DeepSeekProvider(new Key(), client)); var review = gate.Prepare("test", [new("a", 0, ModelSourceKind.UserText, "hello")]);
        Assert.Equal(ErrorCode.INVALID_MODEL_OUTPUT, (await gate.SendAsync(review, gate.Approve(review, ConsentMode.Each))).Error!.Code);
    }
    [Fact]
    public async Task PlanningJsonMode_IsBoundToConsent_AndDisablesThinkingWithinSameBudget()
    {
        using var handler = new Handler(HttpStatusCode.OK, Success); using var client = new HttpClient(handler);
        var gate = new ModelEgress(new DeepSeekProvider(new Key(), client));
        var sources = new[] { new ModelSource("plan", 0, ModelSourceKind.UserText, "Return JSON") };
        var textReview = gate.Prepare("deepseek-flash", sources, 8192);
        var jsonReview = gate.Prepare("deepseek-flash", sources, 8192, jsonOutput: true);
        Assert.False((await gate.SendAsync(jsonReview, gate.Approve(textReview, ConsentMode.Once))).Ok);
        Assert.Equal(0, handler.Calls);
        Assert.True((await gate.SendAsync(jsonReview, gate.Approve(jsonReview, ConsentMode.Each))).Ok);
        using var body = JsonDocument.Parse(handler.Payload!);
        Assert.Equal("disabled", body.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal(0, body.RootElement.GetProperty("temperature").GetInt32());
        Assert.Equal(8192, body.RootElement.GetProperty("max_tokens").GetInt32());
    }

    [Theory]
    [InlineData("length", "partial-json", "finish_reason=length")]
    [InlineData("content_filter", null, "finish_reason=content_filter")]
    [InlineData("stop", "", "最终回复为空")]
    [InlineData("stop", "synthetic-test-secret", "回复包含当前凭据")]
    [InlineData("private-finish-marker", "private-response", "未正常结束")]
    public async Task InvalidCompletion_ExplainsReason_WithoutLeakingResponseOrRetry(string finish, string? content, string expected)
    {
        var response = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content, reasoning_content = "private-reasoning" }, finish_reason = finish } } });
        using var handler = new Handler(HttpStatusCode.OK, response); using var client = new HttpClient(handler);
        var gate = new ModelEgress(new DeepSeekProvider(new Key(), client));
        var review = gate.Prepare("deepseek-flash", [new("a", 0, ModelSourceKind.UserText, "JSON")], 8192, true);
        var result = await gate.SendAsync(review, gate.Approve(review, ConsentMode.Each));
        Assert.Equal(ErrorCode.INVALID_MODEL_OUTPUT, result.Error!.Code);
        Assert.Contains(expected, result.Error.Message);
        foreach (var secret in new[] { "private-finish-marker", "private-response", "private-reasoning", "synthetic-test-secret", "partial-json" })
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(result));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task MissingUsage_StaysUnknownInsteadOfInventedZero()
    {
        using var handler = new Handler(HttpStatusCode.OK, """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}"""); using var client = new HttpClient(handler);
        var gate = new ModelEgress(new DeepSeekProvider(new Key(), client)); var review = gate.Prepare("test", [new("a", 0, ModelSourceKind.UserText, "hello")]);
        var result = await gate.SendAsync(review, gate.Approve(review, ConsentMode.Each)); Assert.True(result.Ok); Assert.Null(result.Value!.Usage);
    }
}
