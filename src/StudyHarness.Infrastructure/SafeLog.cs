using System.Text.Json;
using StudyHarness.Contracts;
namespace StudyHarness.Infrastructure;
// An allowlist: callers cannot supply prompts, exception text, paths or model output.
public sealed class SafeLog(string path)
{
    public void Write(Guid operationId, ErrorCode? error, bool success)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow, operationId,
            status = success ? "success" : "failure", errorCategory = error?.ToString()
        }) + Environment.NewLine);
    }
}
