using System.Text;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

public sealed class ContextBudgetExceededException(int input, int output, int margin, int context, bool verified, int inputLimit = 393216) : ArgumentException("Context budget exceeded")
{
    public string UserMessage(string model) => $"模型 {model}：输入保守估算 {input:N0}（按UTF-8字节计，非实际Token），输出预留 {output:N0} Token，安全余量 {margin:N0} Token，上下文 {context:N0} Token，本地输入上限 {inputLimit:N0} 字节。" +
        (verified ? "当前输入超过额度，请缩小范围或分阶段规划。" : "当前模型能力未取得，正使用保守额度；请在设置中核对模型和API密钥后重试。");
}

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
        string serializedTextRequest, bool plan, DateTimeOffset now, bool reasoningLesson = false, bool contextLesson = false, bool practiceLesson = false)
    {
        ArgumentNullException.ThrowIfNull(serializedTextRequest);
        var verified = IsFresh(capability, model, now);
        var effective = verified ? capability : Conservative(model, now);
        // User-approved lesson fallback only. Planning/OCR and verified model limits stay unchanged.
        if (!verified && reasoningLesson) effective = effective with { ContextWindow = 65536 };
        var input = Encoding.UTF8.GetByteCount(serializedTextRequest);
        var reserve = Math.Min(contextLesson ? 65536 : plan || reasoningLesson ? 32768 : 16384, effective.MaxOutputTokens);
        const int margin = 8192;
        var inputLimit = plan || contextLesson || practiceLesson ? 786432 : 393216;
        if (input > inputLimit || (long)input + reserve + margin > effective.ContextWindow)
            throw new ContextBudgetExceededException(input, reserve, margin, effective.ContextWindow, IsFresh(capability, model, now), inputLimit);
        return new(input, reserve, margin, 4 * 1024 * 1024, 3, 2, plan ? 600 : 300, plan ? 900 : 600);
    }
}
