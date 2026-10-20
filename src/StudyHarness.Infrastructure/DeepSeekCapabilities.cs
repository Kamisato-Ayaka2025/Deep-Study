using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

public sealed class DeepSeekCapabilities(string root, ICredentialReader credentials, HttpClient client) : IModelCapabilitySource
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    public async Task<ModelCapabilities> GetAsync(string model, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(root);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "model-capabilities.db"), Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS capabilities(model TEXT PRIMARY KEY,json TEXT NOT NULL)"; command.ExecuteNonQuery();
            command.CommandText = "SELECT json FROM capabilities WHERE model=$model"; command.Parameters.AddWithValue("$model", model);
            var now = DateTimeOffset.UtcNow;
            if (command.ExecuteScalar() is string cached)
            {
                var value = JsonSerializer.Deserialize<ModelCapabilities>(cached, Wire.Options);
                if (value is not null && AdaptiveBudgetPolicy.IsFresh(value, model, now)) return value;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/models");
            var key = await credentials.ReadKeyAsync(ct);
            if (string.IsNullOrWhiteSpace(key)) return AdaptiveBudgetPolicy.Conservative(model, now);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return AdaptiveBudgetPolicy.Conservative(model, now);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(); var bytes = new byte[4096]; int read;
            while ((read = await stream.ReadAsync(bytes, ct)) != 0)
            {
                if (buffer.Length + read > 65536) return AdaptiveBudgetPolicy.Conservative(model, now);
                buffer.Write(bytes, 0, read);
            }
            var valueParsed = Parse(buffer.ToArray(), model, now);
            if (valueParsed.Verified)
            {
                command.CommandText = "INSERT INTO capabilities VALUES($model,$json) ON CONFLICT(model) DO UPDATE SET json=excluded.json";
                command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(valueParsed, Wire.Options)); command.ExecuteNonQuery();
            }
            return valueParsed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or IOException or SqliteException or UnauthorizedAccessException)
        { return AdaptiveBudgetPolicy.Conservative(model, DateTimeOffset.UtcNow); }
        finally { gate.Release(); }
    }
    public static ModelCapabilities Parse(byte[] json, string model, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var item = document.RootElement.GetProperty("data").EnumerateArray().SingleOrDefault(m => m.GetProperty("id").GetString() == model);
            if (item.ValueKind != JsonValueKind.Object) return AdaptiveBudgetPolicy.Conservative(model, now);
            var context = item.GetProperty("context_window").GetInt32(); var output = item.GetProperty("max_output_tokens").GetInt32();
            var images = item.TryGetProperty("input_modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Array && modalities.EnumerateArray().Any(x => x.GetString() == "image");
            var value = new ModelCapabilities(model, context, output, images, now, true);
            return AdaptiveBudgetPolicy.IsFresh(value, model, now) ? value : AdaptiveBudgetPolicy.Conservative(model, now);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return AdaptiveBudgetPolicy.Conservative(model, now); }
    }
}
