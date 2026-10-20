using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

public sealed class TheoryLessonParser : ITheoryLessonParser
{
    private static readonly JsonSchema Schema = Load("theory-lesson.schema.json");
    private static readonly JsonSchema AdaptiveSchema = Load("theory-lesson-v2.schema.json");
    private static readonly JsonSchema FourChoiceSchema = Load("theory-lesson-v3.schema.json");
    private static readonly JsonSchema KnowledgeSchema = Load("theory-lesson-v4.schema.json");
    private static JsonSchema Load(string name)
    {
        using var stream = typeof(TheoryLessonParser).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
    public Result<TheoryLesson> Parse(string text, IReadOnlyList<PlanSource> sentSources)
    {
        try
        {
            if (text.Length > 4194304) throw new JsonException();
            var node = (JsonNode.Parse(ModelJson.Unwrap(text)) ?? throw new JsonException()).AsObject();
            if (node["schemaVersion"]?.GetValue<int>() is not (2 or 3 or 4) && text.Length > 262144) throw new JsonException();
            if (new[] { "completed", "status", "script", "apiKey", "path" }.Any(node.ContainsKey)) throw new JsonException();
            if (!node.ContainsKey("schemaVersion")) node["schemaVersion"] = 1;
            // Host provenance is the approved snapshot, not an invented external citation.
            if (!node.ContainsKey("sourceKeys")) node["sourceKeys"] = JsonSerializer.SerializeToNode(sentSources.Select(s => s.Key));
            foreach (var key in node.Select(p => p.Key).Where(k => k is not ("schemaVersion" or "blocks" or "questions" or "sourceKeys")).ToArray()) node.Remove(key);
            if (node["blocks"] is JsonArray blocks && node["questions"] is JsonArray questions)
            {
                // Empty known formatting annotations carry no lesson content. Other fields remain schema errors.
                foreach (var block in blocks.OfType<JsonObject>())
                    if (block.ContainsKey("kindnote") && block["kindnote"] is null) block.Remove("kindnote");
                foreach (var question in questions.OfType<JsonObject>())
                    if (question.ContainsKey("questionId") && question["questionId"] is null) question.Remove("questionId");
                if (node["schemaVersion"]?.GetValue<int>() is 2 or 3 or 4 && !blocks.OfType<JsonObject>().Any(b => b["kind"]?.GetValue<string>() == "example"))
                    for (var i = 0; i + 1 < blocks.Count; i++)
                        if (blocks[i] is JsonObject heading && heading["kind"]?.GetValue<string>() == "heading" &&
                            heading["text"]?.GetValue<string>() is { } title && new[] { "例子", "例题", "算例", "示例", "实例", "案例" }.Any(title.Contains) &&
                            blocks[i + 1] is JsonObject body && body["kind"]?.GetValue<string>() == "paragraph" && !string.IsNullOrWhiteSpace(body["text"]?.GetValue<string>()))
                        { body["kind"] = "example"; break; } // preserve the supplied text; never invent an example
                foreach (var question in questions.OfType<JsonObject>())
                {
                    var id = question["id"]?.GetValue<string>();
                    if (id is null || blocks.OfType<JsonObject>().Any(b => b["questionId"]?.GetValue<string>() == id)) continue;
                    var blockId = "host_question_" + blocks.Count;
                    while (blocks.OfType<JsonObject>().Any(b => b["id"]?.GetValue<string>() == blockId)) blockId += "_";
                    blocks.Add(new JsonObject { ["id"] = blockId, ["kind"] = "question", ["questionId"] = id });
                }
            }
            var schema = node["schemaVersion"]?.GetValue<int>() switch
            {
                4 => KnowledgeSchema,
                3 => FourChoiceSchema,
                2 => AdaptiveSchema,
                _ => Schema
            };
            if (!schema.Evaluate(node).IsValid) throw new JsonException();
            return LessonRules.Validate(node.Deserialize<TheoryLesson>(Wire.Options), sentSources);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        {
            // Never echo raw model output, field values, or parser exceptions.
            return Result<TheoryLesson>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "模型课件不符合JSON结构、大小或嵌套限制。");
        }
    }
}
