using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

// Fixed HTTPS destination, no redirects and no automatic retries. Credential
// access belongs to transport; it never becomes part of learning content.
public sealed class DeepSeekProvider(ICredentialReader credentials, HttpClient client) : IModelProvider
{
    public string Destination => "https://api.deepseek.com/chat/completions";
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(600) };
    private static object Tool(string name, string description, string field) => new { type = "function", function = new {
        name, description, parameters = new { type = "object", properties = new Dictionary<string, object> { [field] = new { type = "string" } }, required = new[] { field }, additionalProperties = false } } };
    public async Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
    {
        if (request.Destination != Destination) return Result<ProviderReply>.Fail(ErrorCode.CONSENT_DENIED, "外发目标不匹配。");
        try
        {
            var key = await credentials.ReadKeyAsync(ct);
            if (string.IsNullOrWhiteSpace(key)) return Result<ProviderReply>.Fail(ErrorCode.AUTH_INVALID, "请在设置中保存有效 API 密钥。");
            var retrieval = request.ReadableSources.Count == 0 ? null : new MaterialRetrieval(request.ReadableSources);
            var messages = request.Sources.Where(s => request.PlanRepair is null || s.Id != "plan-repair")
                .Select(s => (object)new { role = "user", content = s.Text }).ToList();
            if (request.PlanRepair is { } repair)
            {
                if (request.Sources.FirstOrDefault()?.Id != "planning-v2" || request.ReadableSources.Count != 0)
                    return Result<ProviderReply>.Fail(ErrorCode.CONSENT_DENIED, "修复仅允许固定来源的V2计划。");
                if (repair.Candidate.Length > 0) messages.Add(new { role = "assistant", content = repair.Candidate });
                messages.Add(new { role = "user", content = repair.Instructions });
            }
            if (retrieval is not null) messages.Add(new { role = "user", content = "授权资料目录（正文需调用read_material；只可引用实际读取的id）：\n" + retrieval.Catalog });
            int promptTokens = 0, completionTokens = 0; bool completeUsage = true;
            for (var round = 0; round < 8; round++)
            {
            ct.ThrowIfCancellationRequested();
            using var message = new HttpRequestMessage(HttpMethod.Post, Destination);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            // All variable prompt material is precisely the reviewed snapshot.
            var body = new Dictionary<string, object> { ["model"] = request.Model, ["stream"] = false, ["max_tokens"] = request.MaxOutputTokens,
                ["messages"] = messages };
            if (request.JsonOutput)
            {
                // Structured planning uses its reviewed budget for the final JSON, not hidden reasoning.
                body["thinking"] = new { type = "disabled" };
                body["temperature"] = 0;
                body["response_format"] = new { type = "json_object" };
            }
            body["max_tokens"] = request.MaxOutputTokens - completionTokens;
            if (retrieval is not null)
            {
                body["thinking"] = new { type = "disabled" };
                body["tools"] = new[] { Tool("read_material", "按目录id读取一个授权资料块；无路径参数。", "id"), Tool("search_materials", "按关键词搜索授权资料快照，返回id，不返回正文。", "query") };
                body["tool_choice"] = retrieval.ReadIds.Count == 0 ? "required" : "auto";
            }
                        var serialized = JsonSerializer.Serialize(body, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            if (request.Capabilities is { } capability)
                _ = AdaptiveBudgetPolicy.Create(capability, request.Model, serialized, true, DateTimeOffset.UtcNow);
            message.Content = new StringContent(serialized, Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                var code = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ErrorCode.AUTH_INVALID,
                    HttpStatusCode.PaymentRequired => ErrorCode.QUOTA_EXHAUSTED,
                    HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity => ErrorCode.MODEL_INVALID,
                    _ => ErrorCode.TRANSIENT_PROVIDER_ERROR
                };
                // Never copy provider error bodies: they may echo secrets or input.
                return Result<ProviderReply>.Fail(code, $"DeepSeek 请求失败（HTTP {(int)response.StatusCode}）；未自动重试。");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int read;
            while ((read = await stream.ReadAsync(bytes, ct)) > 0)
            {
                if (buffer.Length + read > (request.Capabilities is null ? 1024 * 1024 : 4 * 1024 * 1024)) return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "模型响应超出限制。");
                buffer.Write(bytes, 0, read);
            }
            using var json = JsonDocument.Parse(buffer.ToArray()); var root = json.RootElement;
            var choice = root.GetProperty("choices")[0];
            var assistant = choice.GetProperty("message");
            var text = assistant.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null;
            if (root.TryGetProperty("usage", out var roundUsage) && roundUsage.ValueKind == JsonValueKind.Object)
            {
                var p = roundUsage.GetProperty("prompt_tokens").GetInt32(); var c = roundUsage.GetProperty("completion_tokens").GetInt32();
                if (p < 0 || c < 0 || roundUsage.GetProperty("total_tokens").GetInt64() != (long)p + c ||
                    (long)promptTokens + p > (request.Capabilities is null ? 200000 : request.Sources.FirstOrDefault()?.Id == "planning-v2" ? 786432 : 393216) || (long)completionTokens + c > request.MaxOutputTokens)
                    return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "模型用量无效或超出本次总预算。");
                promptTokens += p; completionTokens += c;
            }
            else completeUsage = false;
            var finish = choice.GetProperty("finish_reason").GetString();
            // Do not echo an untrusted finish reason, reply or reasoning content into errors.
            if (text?.Contains(key, StringComparison.Ordinal) == true)
                return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "回复包含当前凭据，已阻止展示；请更换密钥。");
            if (finish == "tool_calls" && retrieval is not null)
            {
                if (!completeUsage || completionTokens >= request.MaxOutputTokens)
                    return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "工具请求缺少用量或已耗尽预算；未继续外发。");
                var calls = assistant.GetProperty("tool_calls");
                if (calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() is < 1 or > 12 || retrieval.Calls + calls.GetArrayLength() > MaterialRetrieval.MaxCalls)
                    throw new InvalidDataException("Tool limit");
                var ids = new HashSet<string>(StringComparer.Ordinal);
                var outgoing = new List<object>(); var responses = new List<object>();
                foreach (var call in calls.EnumerateArray())
                {
                    var id = call.GetProperty("id").GetString();
                    if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || !ids.Add(id) || call.GetProperty("type").GetString() != "function") throw new InvalidDataException("Invalid tool call");
                    var function = call.GetProperty("function"); var name = function.GetProperty("name").GetString()!;
                    var arguments = function.GetProperty("arguments").GetString()!;
                    if (arguments.Contains(key, StringComparison.Ordinal)) throw new InvalidDataException("Credential echoed");
                    var result = retrieval.Invoke(name, arguments, ct);
                    outgoing.Add(new { id, type = "function", function = new { name, arguments } });
                    responses.Add(new { role = "tool", tool_call_id = id, content = result });
                }
                messages.Add(new { role = "assistant", content = text, tool_calls = outgoing }); messages.AddRange(responses);
                messages.Add(new { role = "user", content = "宿主读取记录：当前已读资料id=" + string.Join(",", retrieval.ReadIds) +
                    "。最终sourceKeys及实验sourceKeys只能引用上述id；目录或搜索结果不等于已读正文。如需引用其他资料，先调用read_material。" });
                continue;
            }
            if (finish == "length")
                return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, $"输出被截断（finish_reason=length，本次上限{request.MaxOutputTokens} tokens）；请减少课程数量后重新预览。未自动重试。");
            if (finish == "content_filter")
                return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "服务返回内容过滤标记（finish_reason=content_filter），未生成计划；无法据此确定具体原因。");
            if (finish != "stop")
                return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "服务未正常结束回复，未生成计划；未自动重试。");
            if (string.IsNullOrWhiteSpace(text))
                return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "服务正常结束但最终回复为空，未生成计划；未自动重试。");
            if (retrieval is not null && retrieval.ReadIds.Count == 0) return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "模型未读取资料，计划未保存。");
            TokenUsage? usage = completeUsage ? new(promptTokens, completionTokens, promptTokens + completionTokens) : null;
            // Metadata is provider-reported, not a verified identity. Never expose a credential echoed here.
            var reportedModel = root.TryGetProperty("model", out var modelField) && modelField.ValueKind == JsonValueKind.String ? modelField.GetString() : null;
            if (reportedModel is not null && (reportedModel.Contains(key, StringComparison.Ordinal) ||
                !System.Text.RegularExpressions.Regex.IsMatch(reportedModel, @"\A[A-Za-z0-9][A-Za-z0-9._:/-]{0,127}\z"))) reportedModel = null;
            return Result<ProviderReply>.Success(new(text, false, Usage: usage, Model: request.Model, ReadSourceIds: retrieval?.ReadIds.ToArray(), ToolCalls: retrieval?.Calls ?? 0, ReportedModel: reportedModel));
            }
            return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "按需读取超过8轮；已停止，未自动重试。请缩小资料范围。");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Result<ProviderReply>.Fail(ErrorCode.RUN_CANCELLED, "请求已取消；已发送部分可能仍计费。"); }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { return Result<ProviderReply>.Fail(ErrorCode.TRANSIENT_PROVIDER_ERROR, "服务连接失败或超时，未自动重试。"); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or NullReferenceException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException)
        { return Result<ProviderReply>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "模型响应格式不符合协议。"); }
    }
}
