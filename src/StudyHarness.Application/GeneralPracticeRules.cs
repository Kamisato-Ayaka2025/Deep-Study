using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

// Independent schema5 rules: never relax the existing C/C++ file allowlist.
public static class GeneralPracticeRules
{
    // Host-owned read-only presentation for the existing material viewer/tutor.
    // This is never accepted by the C/C++ validators or exported as source files.
    public static LabMaterial Presentation(GeneralPractice material) => new(
        Markdown(material, material.Sources), [], [], [], material.Assessment,
        "仅提供学习材料，由你自行完成和核对；软件不执行操作。", material.Sources, 5, material.Kind,
        "按已确认计划提供通用实践材料。");
    private static readonly JsonSerializerOptions Strict = new(Wire.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    private static bool Text(string? value, int limit) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit && !value.Contains('\0');
    private static bool Texts(IReadOnlyList<string>? values, int minimum, int maximum) =>
        values is not null && values.Count >= minimum && values.Count <= maximum && values.All(v => Text(v, 2000));
    private static Result<GeneralPractice> Bad() => Result<GeneralPractice>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "通用实践材料缺少操作、输入、预期、评判依据或有效来源；未发布。");

    public static Result<GeneralPractice> Validate(GeneralPractice? material, IReadOnlyList<string> sourceKeys)
    {
        if (material is null || material.SchemaVersion != 5 ||
            !(material.Kind == "exercise" && material.Capability == "paper-exercise" || material.Kind == "project" && material.Capability == "manual-project") ||
            !Text(material.Title, 200) || !Texts(material.Objectives, 1, 20) || !Texts(material.Prerequisites, 0, 20) ||
            !Text(material.Assessment, 16000) || material.Tasks is null || material.Tasks.Count is < 1 or > 20 ||
            material.Tasks.Any(t => t is null || !Text(t.Id, 80) || !Text(t.Action, 8000) || !Text(t.Input, 8000) ||
                !Text(t.ExpectedObservation, 8000) || !Text(t.Acceptance, 8000)) ||
            material.Tasks.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != material.Tasks.Count ||
            material.Sources is null || material.Sources.Count is < 1 or > 99 ||
            material.Sources.Distinct().Count() != material.Sources.Count || material.Sources.Any(s => !Text(s, 128) || !sourceKeys.Contains(s))) return Bad();
        if (JsonSerializer.SerializeToUtf8Bytes(material, Wire.Options).Length > 262144) return Bad();
        return Result<GeneralPractice>.Success(material);
    }

    public static Result<GeneralPractice> Parse(string text, IReadOnlyList<string> sourceKeys)
    {
        try
        {
            if (text.Length > 262144) return Bad();
            text = ModelJson.Unwrap(text);
            using var json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 16 });
            if (json.RootElement.ValueKind != JsonValueKind.Object || !Unique(json.RootElement) ||
                new[] { "schemaVersion", "kind", "capability", "title", "objectives", "prerequisites", "tasks", "assessment", "sources" }
                    .Any(p => !json.RootElement.TryGetProperty(p, out _))) return Bad();
            var node = JsonNode.Parse(text)!;
            if (node["tasks"] is JsonArray tasks)
                foreach (var task in tasks.OfType<JsonObject>())
                    foreach (var field in new[] { "action", "input", "expectedObservation", "acceptance" })
                        if (task[field] is JsonValue && task[field + "补充"] is JsonValue extra &&
                            extra.TryGetValue<string>(out var value) && string.IsNullOrWhiteSpace(value)) task.Remove(field + "补充");
            return Validate(node.Deserialize<GeneralPractice>(Strict), sourceKeys);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return Bad(); }
    }

    private static bool Unique(JsonElement item) => item.ValueKind switch
    {
        JsonValueKind.Object => item.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == item.EnumerateObject().Count() && item.EnumerateObject().All(p => Unique(p.Value)),
        JsonValueKind.Array => item.EnumerateArray().All(Unique),
        _ => true
    };

    public static string Markdown(GeneralPractice material, IReadOnlyList<string> sourceKeys)
    {
        if (!Validate(material, sourceKeys).Ok) throw new ArgumentException("Invalid general practice material", nameof(material));
        var body = new StringBuilder("# ").AppendLine(material.Title)
            .AppendLine("\n模型生成的实践材料；由你在外部完成并自行核对。软件不执行操作，也不将你的确认视为机器验证通过。")
            .AppendLine("\n## 学习目标");
        foreach (var objective in material.Objectives) body.Append("- ").AppendLine(objective);
        body.AppendLine("\n## 前置条件");
        if (material.Prerequisites.Count == 0) body.AppendLine("无额外前置条件。");
        foreach (var item in material.Prerequisites) body.Append("- ").AppendLine(item);
        foreach (var task in material.Tasks)
            body.Append("\n## ").AppendLine(task.Id).AppendLine("\n### 操作").AppendLine(task.Action)
                .AppendLine("\n### 输入").AppendLine(task.Input).AppendLine("\n### 预期观察/结果").AppendLine(task.ExpectedObservation)
                .AppendLine("\n### 评判依据").AppendLine(task.Acceptance);
        body.AppendLine("\n## 自行测评").AppendLine(material.Assessment).AppendLine("\n## 固定来源");
        foreach (var source in material.Sources) body.Append("- ").AppendLine(source);
        return body.ToString();
    }
}
