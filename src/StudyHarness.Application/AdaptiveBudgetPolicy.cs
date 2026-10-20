using System.Text;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

public static class AdaptiveBudgetPolicy
{
    public static ModelCapabilities Conservative(string model, DateTimeOffset now) => new(model, 32768, 4096, false, now, false);

    public static bool IsFresh(ModelCapabilities capability, string model, DateTimeOffset now) =>
        capability.SchemaVersion == 1 && capability.Verified && capability.Model == model &&
        capability.ContextWindow is > 0 and <= 16777216 && capability.MaxOutputTokens > 0 &&
        capability.MaxOutputTokens <= capability.ContextWindow && capability.ObservedAt <= now &&
        now - capability.ObservedAt <= TimeSpan.FromHours(24);

    // Count the entire serialized text request, including tools/history. UTF-8 bytes
    // are a conservative admission estimate, not an assertion of exact model tokens.
    public static ContextBudget Create(ModelCapabilities capability, string model,
        string serializedTextRequest, bool plan, DateTimeOffset now, bool reasoningLesson = false)
    {
        ArgumentNullException.ThrowIfNull(serializedTextRequest);
        var effective = IsFresh(capability, model, now) ? capability : Conservative(model, now);
        var input = Encoding.UTF8.GetByteCount(serializedTextRequest);
        var reserve = Math.Min(plan || reasoningLesson ? 32768 : 16384, effective.MaxOutputTokens);
        const int margin = 8192;
        if (input > 393216 || (long)input + reserve + margin > effective.ContextWindow)
            throw new ArgumentException("Context budget exceeded; retrieve a smaller explicit scope before dispatch.", nameof(serializedTextRequest));
        return new(input, reserve, margin, 4 * 1024 * 1024, 3, 2, plan ? 600 : 300, plan ? 900 : 600);
    }
}
