using System.Net;
using System.Text;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class DeepSeekTheoryTests
{
    [Fact]
    public async Task QuizRequest_UsesMixedQuestionSchemaWithoutRequiringTeachingBlocks()
    {
        var handler = new Handler(Response()); using var client = new HttpClient(handler);
        var egress = new ModelEgress(new DeepSeekTheoryProvider(new Key(), client));
        var review = egress.Prepare("deepseek-flash", [new("quiz-v1", 5, ModelSourceKind.UserText, QuizLesson.Prompt)], 512, true);
        Assert.True((await egress.SendAsync(review, egress.Approve(review, ConsentMode.Each))).Ok);
        using var body = JsonDocument.Parse(handler.Body);
        var props = body.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema").GetProperty("properties");
        Assert.Equal(5, props.GetProperty("schemaVersion").GetProperty("const").GetInt32());
        Assert.Equal(0, props.GetProperty("blocks").GetProperty("minItems").GetInt32());
        var question = props.GetProperty("questions").GetProperty("items").GetProperty("properties");
        Assert.Equal(0, question.GetProperty("options").GetProperty("minItems").GetInt32());
        Assert.True(question.TryGetProperty("referenceAnswer", out _));
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }
    [Theory]
    [InlineData("theory-v4", "high")]
    [InlineData("theory-v4-short", "none")]
    public async Task HybridRequests_AllowZeroCitations_KeepFourChoicesAndNoTools(string id, string effort)
    {
        var handler = new Handler(Response()); using var client = new HttpClient(handler);
        var egress = new ModelEgress(new DeepSeekTheoryProvider(new Key(), client));
        var review = egress.Prepare("deepseek-flash", [new(id, 4, ModelSourceKind.UserText, TheoryKnowledgePolicy.Guidance)], 512, true);
        Assert.True((await egress.SendAsync(review, egress.Approve(review, ConsentMode.Each))).Ok);
        using var body = JsonDocument.Parse(handler.Body);
        var properties = body.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema").GetProperty("properties");
        Assert.Equal(4, properties.GetProperty("schemaVersion").GetProperty("const").GetInt32());
        Assert.Equal(0, properties.GetProperty("sourceKeys").GetProperty("minItems").GetInt32());
        Assert.Equal(4, properties.GetProperty("questions").GetProperty("items").GetProperty("properties").GetProperty("options").GetProperty("minItems").GetInt32());
        Assert.Equal(effort, body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
        Assert.DoesNotContain("fixture-secret", handler.Body);
    }
    [Theory]
    [InlineData("theory-v3", "high")]
    [InlineData("theory-v3-short", "none")]
    public async Task FourChoiceRequests_SendNewSchemaAndSoftMathGuidance(string id, string effort)
    {
        var handler = new Handler(Response()); using var client = new HttpClient(handler);
        var egress = new ModelEgress(new DeepSeekTheoryProvider(new Key(), client));
        var prompt = id == "theory-v3" ? TheoryPrompt.AdaptiveText : TheoryPrompt.Text;
        var review = egress.Prepare("deepseek-flash", [new(id, 3, ModelSourceKind.UserText, prompt)], 512, true);
        Assert.True((await egress.SendAsync(review, egress.Approve(review, ConsentMode.Each))).Ok);
        using var body = JsonDocument.Parse(handler.Body);
        var properties = body.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema").GetProperty("properties");
        Assert.Equal(3, properties.GetProperty("schemaVersion").GetProperty("const").GetInt32());
        var options = properties.GetProperty("questions").GetProperty("items").GetProperty("properties").GetProperty("options");
        Assert.Equal(4, options.GetProperty("minItems").GetInt32());
        Assert.Equal(4, options.GetProperty("maxItems").GetInt32());
        Assert.Equal(effort, body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Contains("$x_1$", prompt); Assert.Contains(@"\begin{pmatrix}", prompt);
        Assert.Contains("不是额外拒绝或修复条件", prompt); Assert.Contains("不写“选A", prompt);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }
    private static string Response(string status = "completed", string type = "message", string text = "{}", int output = 2) =>
        JsonSerializer.Serialize(new { status, output = new[] { new { type, role = "assistant", content = new[] { new { type = "output_text", text } } } }, usage = new { input_tokens = 3, output_tokens = output, total_tokens = 3 + output } });
    [Fact]
    public async Task FixedDestination_SchemaAndApprovedSnapshot_UsagePreserved()
    {
        var handler = new Handler(Response()); using var client = new HttpClient(handler);
        var egress = new ModelEgress(new DeepSeekTheoryProvider(new Key(), client));
        var review = egress.Prepare("deepseek-flash", [new("theory-v1", 1, ModelSourceKind.UserText, "json instruction"), new("s1", 0, ModelSourceKind.OriginalMaterial, "approved text")], 512, true);
        var reply = await egress.SendAsync(review, egress.Approve(review, ConsentMode.Each));
        Assert.True(reply.Ok); Assert.Equal(5, reply.Value!.Usage!.TotalTokens);
        Assert.Equal("https://api.deepseek.com/responses", handler.Url);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("json_schema", body.RootElement.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
        Assert.True(body.RootElement.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean());
        var schema = body.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema");
        Assert.Equal("integer", schema.GetProperty("properties").GetProperty("schemaVersion").GetProperty("type").GetString());
        var block = schema.GetProperty("properties").GetProperty("blocks").GetProperty("items");
        Assert.Equal("string", block.GetProperty("properties").GetProperty("kind").GetProperty("type").GetString());
        Assert.Equal(4, block.GetProperty("required").GetArrayLength());
        Assert.False(block.TryGetProperty("if", out _));
        Assert.Equal("none", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(512, body.RootElement.GetProperty("max_output_tokens").GetInt32());
        Assert.Equal("approved text", body.RootElement.GetProperty("input")[1].GetProperty("content").GetString());
        Assert.DoesNotContain("fixture-secret", handler.Body);
        Assert.False(body.RootElement.TryGetProperty("tools", out _));
    }
    [Fact]
    public async Task AdaptiveTheory_UsesBoundedReasoning_WithoutExposingReasoningText()
    {
        var response = JsonSerializer.Serialize(new { status = "completed", output = new object[] {
            new { type = "reasoning", content = new[] { new { type = "reasoning_text", text = "internal reasoning" } } },
            new { type = "message", role = "assistant", content = new[] { new { type = "output_text", text = "{}" } } }
        }, usage = new { input_tokens = 3, output_tokens = 10, total_tokens = 13 } });
        var handler = new Handler(response); using var client = new HttpClient(handler);
        var egress = new ModelEgress(new DeepSeekTheoryProvider(new Key(), client));
        var review = egress.Prepare("deepseek-flash", [new("theory-v2", 2, ModelSourceKind.UserText, "json; reasoning=high")], 512, true);
        var reply = await egress.SendAsync(review, egress.Approve(review, ConsentMode.Each));
        Assert.True(reply.Ok); Assert.Equal("{}", reply.Value!.Text);
        Assert.Equal(10, reply.Value.Usage!.CompletionTokens);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("high", body.RootElement.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(512, body.RootElement.GetProperty("max_output_tokens").GetInt32());
    }
    [Theory]
    [InlineData("incomplete", "message", "{}", 2)]
    [InlineData("completed", "function_call", "{}", 2)]
    [InlineData("completed", "message", "fixture-secret", 2)]
    [InlineData("completed", "message", "", 2)]
    [InlineData("completed", "message", "{}", 513)]
    public async Task RejectsIncompleteToolsSecretsEmptyAndBudget(string status, string type, string text, int output)
    {
        using var client = new HttpClient(new Handler(Response(status, type, text, output)));
        var egress = new ModelEgress(new DeepSeekTheoryProvider(new Key(), client));
        var review = egress.Prepare("deepseek-flash", [new("theory-v1", 1, ModelSourceKind.UserText, "json")], 512, true);
        var reply = await egress.SendAsync(review, egress.Approve(review, ConsentMode.Each));
        Assert.Equal(ErrorCode.INVALID_MODEL_OUTPUT, reply.Error!.Code);
        Assert.DoesNotContain("fixture-secret", JsonSerializer.Serialize(reply));
    }
    [Fact]
    public void LargerOutput_IsLimitedToAdaptiveTheoryAndFreshCapability()
    {
        using var client = new HttpClient(new Handler(Response()));
        var egress = new ModelEgress(new DeepSeekTheoryProvider(new Key(), client));
        var caps = new ModelCapabilities("deepseek-flash", 1048576, 393216, true, DateTimeOffset.UtcNow, true);
        ModelSource[] sources = [new("theory-v2", 2, ModelSourceKind.UserText, "json")];
        Assert.Equal(32768, egress.Prepare("deepseek-flash", sources, 32768, true, adaptiveCapability: caps).MaxOutputTokens);
        Assert.Throws<ArgumentException>(() => egress.Prepare("deepseek-flash", sources, 32769, true, adaptiveCapability: caps));
        Assert.Throws<ArgumentException>(() => egress.Prepare("deepseek-flash", [sources[0] with { Id = "plan-v2" }], 32768, true, adaptiveCapability: caps));
        Assert.Throws<ArgumentException>(() => egress.Prepare("deepseek-flash", sources, 32768, true, adaptiveCapability: caps with { Verified = false }));
        Assert.Throws<ArgumentException>(() => egress.Prepare("deepseek-flash", sources, 32768, true, adaptiveCapability: caps with { MaxOutputTokens = 16000 }));
    }
    [Theory]
    [InlineData("max_output_tokens", true)]
    [InlineData("untrusted-provider-detail", false)]
    public async Task IncompleteResponse_ReportsOnlyAllowlistedReason(string reason, bool limited)
    {
        var response = JsonSerializer.Serialize(new { status = "incomplete", incomplete_details = new { reason }, output = Array.Empty<object>() });
        using var client = new HttpClient(new Handler(response));
        var egress = new ModelEgress(new DeepSeekTheoryProvider(new Key(), client));
        var review = egress.Prepare("deepseek-flash", [new("theory-v2", 2, ModelSourceKind.UserText, "json")], 512, true);
        var reply = await egress.SendAsync(review, egress.Approve(review, ConsentMode.Each));
        Assert.Equal(ErrorCode.INVALID_MODEL_OUTPUT, reply.Error!.Code);
        Assert.Equal(limited, reply.Error.Message.Contains("总输出上限"));
        Assert.DoesNotContain(reason, reply.Error.Message);
    }
    [Fact]
    public async Task NonTheoryScopeNeverSends()
    {
        var handler = new Handler(Response()); using var client = new HttpClient(handler);
        var egress = new ModelEgress(new DeepSeekTheoryProvider(new Key(), client));
        var review = egress.Prepare("deepseek-flash", [new("lab-v3", 1, ModelSourceKind.UserText, "json")], 512, true);
        var reply = await egress.SendAsync(review, egress.Approve(review, ConsentMode.Each));
        Assert.Equal(ErrorCode.CONSENT_DENIED, reply.Error!.Code); Assert.Equal(0, handler.Calls);
    }
    private sealed class Key : ICredentialReader { public Task<string?> ReadKeyAsync(CancellationToken ct) => Task.FromResult<string?>("fixture-secret"); }
    private sealed class Handler(string response) : HttpMessageHandler
    {
        public string Body = "", Url = ""; public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Url = request.RequestUri!.AbsoluteUri; Body = await request.Content!.ReadAsStringAsync(ct);
            return new(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }
}
