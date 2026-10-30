using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

// Theory has no tools: send the already approved fixed snapshot with its schema.
// No fallback to another destination and no silent JSON repair.
public sealed class DeepSeekTheoryProvider(ICredentialReader credentials, HttpClient client) : IModelProvider
{
    public string Destination => "https://api.deepseek.com/responses";
    public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
    {
        if (request.Destination != Destination || !request.JsonOutput || request.ReadableSources.Count != 0 ||
            request.Sources.FirstOrDefault()?.Id is not ("quiz-v1" or "theory-v1" or "theory-v2" or "theory-v3" or "theory-v3-context" or "theory-v3-short" or "theory-v4" or "theory-v4-short"))
            return Result<ProviderReply>.Fail(ErrorCode.CONSENT_DENIED, "结构化理论请求范围不匹配。");
        try
        {
            var key = await credentials.ReadKeyAsync(ct);
            if (string.IsNullOrWhiteSpace(key)) return Result<ProviderReply>.Fail(ErrorCode.AUTH_INVALID, "请在设置中保存有效API密钥。");
            using var schemaStream = typeof(DeepSeekTheoryProvider).Assembly.GetManifestResourceStream(request.Sources[0].Id == "quiz-v1" ? "quiz-lesson-v5.schema.json" : request.Sources[0].Id is "theory-v4" or "theory-v4-short" ? "theory-lesson-v4.schema.json" : request.Sources[0].Id is "theory-v3" or "theory-v3-context" or "theory-v3-short" ? "theory-lesson-v3.schema.json" : request.Sources[0].Id == "theory-v2" ? "theory-lesson-v2.schema.json" : "theory-lesson.schema.json")!;
            var schema = (await JsonNode.ParseAsync(schemaStream, cancellationToken: ct))!.AsObject();
            schema["properties"]!.AsObject().Remove("contextChecks");
            // Strict structured output requires every property to be required;
            // nullable block fields retain their local conditional validation.
            schema.Remove("$schema"); schema.Remove("title");
            var block = schema["properties"]!["blocks"]!["items"]!.AsObject();
            block.Remove("if"); block.Remove("then"); block.Remove("else");
            block["required"] = new JsonArray("id", "kind", "text", "questionId");
            schema["properties"]!["schemaVersion"]!["type"] = "integer";
            block["properties"]!["kind"]!["type"] = "string";
            using var message = new HttpRequestMessage(HttpMethod.Post, Destination);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var serialized = JsonSerializer.Serialize(new
            {
                model = request.Model, stream = false, temperature = 0, reasoning = new { effort = request.Sources[0].Id is "quiz-v1" or "theory-v2" or "theory-v3" or "theory-v3-context" or "theory-v4" ? "high" : "none" },
                max_output_tokens = request.MaxOutputTokens,
                input = request.Sources.Select(s => new { role = "user", content = s.Text }),
                text = new { format = new { type = "json_schema", name = "theory_lesson", strict = true, schema } }
                        }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (request.Capabilities is { } capability)
            {
                var admission = AdaptiveBudgetPolicy.Create(capability, request.Model, serialized, false, DateTimeOffset.UtcNow, request.Sources[0].Id is "quiz-v1" or "theory-v2" or "theory-v3" or "theory-v3-context" or "theory-v4", request.Sources[0].Id == "theory-v3-context", request.Sources[0].Id is "practice-v5" or "lab-cpp-v4" or "lab-v3");
                if (request.MaxOutputTokens > admission.OutputReserve) throw new ArgumentException("Output capability expired or reduced.");
            }
            message.Content = new StringContent(serialized, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return Result<ProviderReply>.Fail(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ErrorCode.AUTH_INVALID,
                    HttpStatusCode.PaymentRequired => ErrorCode.QUOTA_EXHAUSTED,
                    HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity => ErrorCode.MODEL_INVALID,
                    _ => ErrorCode.TRANSIENT_PROVIDER_ERROR
                }, $"DeepSeek结构化理论请求失败（HTTP {(int)response.StatusCode}）。");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(bytes, ct)) != 0)
            {
                if (buffer.Length + count > (request.Capabilities is null ? 1024 * 1024 : 4 * 1024 * 1024)) throw new InvalidDataException();
                buffer.Write(bytes, 0, count);
            }
            var raw = Encoding.UTF8.GetString(buffer.ToArray());
            if (raw.Contains(key, StringComparison.Ordinal)) throw new InvalidDataException();
            using var json = JsonDocument.Parse(raw); var root = json.RootElement;
            if (root.GetProperty("status").GetString() != "completed")
            {
                var limited = root.TryGetProperty("incomplete_details", out var details) && details.ValueKind == JsonValueKind.Object &&
                    details.TryGetProperty("reason", out var reason) && reason.GetString() == "max_output_tokens";
                return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, limited
                    ? "理论响应达到本次总输出上限，未完成，未发布。"
                    : "理论响应未完成，未发布；未记录上游正文或思考内容。");
            }
            var output = root.GetProperty("output"); var text = new StringBuilder();
            foreach (var item in output.EnumerateArray())
            {
                var type = item.GetProperty("type").GetString();
                if (type == "reasoning") continue;
                if (type != "message" || item.GetProperty("role").GetString() != "assistant") throw new InvalidDataException();
                foreach (var part in item.GetProperty("content").EnumerateArray())
                {
                    if (part.GetProperty("type").GetString() != "output_text") throw new InvalidDataException();
                    text.Append(part.GetProperty("text").GetString());
                }
            }
            if (string.IsNullOrWhiteSpace(text.ToString())) throw new InvalidDataException();
            var usage = root.GetProperty("usage"); var input = usage.GetProperty("input_tokens").GetInt32();
            var generated = usage.GetProperty("output_tokens").GetInt32(); var total = usage.GetProperty("total_tokens").GetInt32();
            if (input < 0 || input > (request.Capabilities is null ? 200000 : 393216) || generated < 0 || generated > request.MaxOutputTokens || (long)input + generated != total) throw new InvalidDataException();
            return Result<ProviderReply>.Success(new(text.ToString(), false, Usage: new(input, generated, total), Model: request.Model));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Result<ProviderReply>.Fail(ErrorCode.RUN_CANCELLED, "已取消结构化理论请求。"); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { return Result<ProviderReply>.Fail(ErrorCode.TRANSIENT_PROVIDER_ERROR, "结构化理论服务连接失败或超时。"); }
        catch (Exception e) when (e is ArgumentException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "结构化理论服务响应无效，未发布。"); }
    }
}
