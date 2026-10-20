using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StudyHarness.Application;

namespace StudyHarness.Infrastructure;

// The desktop connects exclusively to DeepSeek. Remote DNS, redirects and
// target connections are performed by Fetch's security boundary, not this host.
public sealed class DeepSeekWebSources(ICredentialReader credentials, HttpClient client) : IWebSources
{
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(45) };
    public async Task<IReadOnlyList<WebHit>> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 128) throw new InvalidOperationException("搜索词超出限制。");
        using var json = await PostAsync("search", new { query, count = 5, need_content = false }, ct);
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array || results.GetArrayLength() > 5) throw new InvalidOperationException("搜索结果格式无效。");
        return results.EnumerateArray().Select(r => new WebHit(Text(r, "title"), Text(r, "url"))).ToArray();
    }
    public async Task<WebPage> FetchAsync(string url, CancellationToken ct)
    {
        var uri = WebAddress.Parse(url);
        using var json = await PostAsync("fetch", new { url = uri.AbsoluteUri }, ct);
        var title = Text(json.RootElement, "title");
        var content = Text(json.RootElement, "content");
        if (string.IsNullOrWhiteSpace(content) || content.Length > 60000 || title.Length > 500 || content.Contains('\0'))
            throw new InvalidOperationException("网页无可用正文或超出60000字符限制；未截断冒充完整正文。");
        return new(uri.AbsoluteUri, title, content, DateTimeOffset.UtcNow);
    }
    private static string Text(JsonElement value, string property)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(property, out var text) || text.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("联网服务响应字段格式无效。");
        return text.GetString()!;
    }
    private async Task<JsonDocument> PostAsync(string operation, object body, CancellationToken ct)
    {
        var key = await credentials.ReadKeyAsync(ct);
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("请在设置中保存DeepSeek API密钥。");
        var payload = JsonSerializer.Serialize(body);
        if (payload.Contains(key, StringComparison.Ordinal)) throw new InvalidOperationException("请求包含凭据，已阻止外发。");
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.deepseek.com/v1/tools/" + operation);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"DeepSeek {operation}失败（HTTP {(int)response.StatusCode}）；未自动重试，请检查配置、余额或稍后重试。");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(); var bytes = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(bytes, ct)) != 0)
            {
                if (buffer.Length + count > 512 * 1024) throw new InvalidOperationException("联网响应超出512KiB限制。");
                buffer.Write(bytes, 0, count);
            }
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            if (text.Contains(key, StringComparison.Ordinal)) throw new InvalidOperationException("服务响应包含凭据，已阻止保存及显示。");
            return JsonDocument.Parse(text);
        }
        catch (HttpRequestException) { throw new InvalidOperationException("联网失败，请检查网络后重试；未自动重试。"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new InvalidOperationException("联网请求超时，请稍后重试；未自动重试。"); }
        catch (JsonException) { throw new InvalidOperationException("联网服务响应格式无效。"); }
    }
}
