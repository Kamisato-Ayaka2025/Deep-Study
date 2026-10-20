using System.Text.Json;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

public static class PlanningPrompt
{
    public static string Text { get; } = Load();
    public static string InputRules(PlanningInput input)
    {
        var known = new Dictionary<string, object>();
        if (!string.IsNullOrWhiteSpace(input.Background)) known["background"] = input.Background;
        if (!string.IsNullOrWhiteSpace(input.Goal)) known["goal"] = input.Goal;
        if (input.WeeklyMinutes is { } minutes) known["weeklyMinutes"] = minutes;
        if (input.Scope != "Auto") known["scope"] = input.Scope;
        var allowed = new[] { "background", "goal", "weeklyMinutes" }.Where(field => !known.ContainsKey(field)).ToArray();
        return "\n本次草稿的默认值约束（依据当前输入重新计算，优先于通用示例）：\n" +
            JsonSerializer.Serialize(new { knownFields = known, allowedDefaultFields = allowed }, Wire.Options) +
            "\nknownFields中的字段必须原样输出，且禁止出现在defaults，即使填写值恰好等于默认值（例如120）。" +
            "defaults只能从allowedDefaultFields中选择实际采用默认的项；从请求正文提取的已知信息也不能标为默认。" +
            "每个defaults.value必须与对应顶层字段完全一致（时间为整数转字符串），不要另写摘要。用户补填字段后删除旧默认标签，不继承上一版defaults。" +
            "显式scope与请求或资料冲突时，不得改写scope来绕过；保留已知字段，在clarifications说明冲突并请求调整，生成对应scope合法的待澄清目录，不得确认。Math不能包含实验。";
    }
    private static string Load()
    {
        using var stream = typeof(PlanningPrompt).Assembly.GetManifestResourceStream("planning-v1.txt")!;
        using var reader = new StreamReader(stream);
        using var schemaStream = typeof(PlanningPrompt).Assembly.GetManifestResourceStream("plan.schema.json")!;
        using var schemaReader = new StreamReader(schemaStream);
        return reader.ReadToEnd() + "\n以下是宿主实际校验的完整JSON Schema。仅输出符合它的计划对象，不要输出Schema本身：\n" + schemaReader.ReadToEnd();
    }
}
