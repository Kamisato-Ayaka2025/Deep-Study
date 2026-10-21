using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

public sealed class PlanParser : IPlanParser
{
    private static readonly JsonSchema Schema = Load("plan.schema.json");
    private static readonly JsonSchema AdaptiveSchema = Load("plan-v2.schema.json");
    private static JsonSchema Load(string name)
    {
        using var stream = typeof(PlanParser).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream); return JsonSchema.FromText(reader.ReadToEnd());
    }
    public Result<PlanCandidate> Parse(string text) => ParseCore(text, true);
    public Result<PlanCandidate> ParseRaw(string text) => ParseCore(text, false);
    private static Result<PlanCandidate> ParseCore(string text, bool derive)
    {
        try
        {
            if (text.Length > 524288) throw new JsonException();
            var node = JsonNode.Parse(text);
            var schema = node?["schemaVersion"]?.GetValue<int>() == 2 ? AdaptiveSchema : Schema;
            if (schema == AdaptiveSchema && node?["coverage"] is JsonArray coverage)
            {
                var expanded = new JsonArray();
                foreach (var item in coverage)
                {
                    var key = item?["entryKey"]?.GetValue<string>() ?? "";
                    var range = System.Text.RegularExpressions.Regex.Match(key, @"^(d[1-9][0-9]?/(?:page|url)-)([1-9][0-9]{0,4})\.\.(?:\1)?([1-9][0-9]{0,4})$");
                    if (range.Success)
                    {
                        var start = int.Parse(range.Groups[2].Value); var end = int.Parse(range.Groups[3].Value);
                        if (end < start || end > 10000 || expanded.Count + end - start + 1 > 10000) throw new JsonException();
                        for (var i = start; i <= end; i++)
                        {
                            var copy = item!.DeepClone(); copy["entryKey"] = range.Groups[1].Value + i; expanded.Add(copy);
                        }
                    }
                    else expanded.Add(item?.DeepClone());
                    if (expanded.Count > 10000) throw new JsonException();
                }
                node!["coverage"] = expanded;
                text = node.ToJsonString(Wire.Options);
            }
            if (schema == AdaptiveSchema && node is JsonObject adaptive && adaptive["algorithms"] is JsonArray { Count: 0 })
            {
                // Empty optional catalog carries no algorithm declarations; preserve all actual mappings.
                adaptive.Remove("algorithms"); text = adaptive.ToJsonString(Wire.Options);
            }
            if (schema == Schema && text.Length > 131072) throw new JsonException();
            if (derive && schema == AdaptiveSchema && node is JsonObject candidate)
            {
                DeriveSummaries(candidate); text = candidate.ToJsonString(Wire.Options);
            }
            var evaluation = schema.Evaluate(node, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
            if (!evaluation.IsValid)
            {
                var issues = Problems(evaluation).Distinct().Take(5).DefaultIfEmpty("$ [结构约束]");
                return Result<PlanCandidate>.Fail(ErrorCode.INVALID_MODEL_OUTPUT,
                    "模型已返回JSON，但计划结构不符合Schema：" + string.Join("；", issues) + "。未保存候选计划；请重新拟定。");
            }
            var parsed = JsonSerializer.Deserialize<PlanCandidate>(text, Wire.Options)!;
            if (derive && schema == AdaptiveSchema && parsed.Coverage is not null)
            {
                var merged = new List<PlanCoverage>();
                foreach (var group in parsed.Coverage.GroupBy(e => e.EntryKey, StringComparer.Ordinal))
                {
                    var first = group.First();
                    // Merge only facts already declared by the model. Conflicting states remain invalid.
                    if (group.All(e => e.State == "included" && e.NodeKeys.Count > 0 && (e.Reason is null || !string.IsNullOrWhiteSpace(e.Reason))))
                    {
                        var reasons = group.Select(e => e.Reason).Where(r => r is not null).Distinct().ToArray();
                        var reason = reasons.Length == 0 ? null : string.Join("；", reasons);
                        if (reason?.Length > 2000) { merged.AddRange(group); continue; }
                        merged.Add(first with { NodeKeys = group.SelectMany(e => e.NodeKeys).Distinct().ToArray(), Reason = reason });
                    }
                    else if (group.All(e => e.State == first.State && e.Reason == first.Reason && e.NodeKeys.SequenceEqual(first.NodeKeys)))
                        merged.Add(first);
                    else merged.AddRange(group);
                }
                parsed = parsed with { Coverage = merged };
            }
            return Result<PlanCandidate>.Success(parsed);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        { return Result<PlanCandidate>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "模型回复不是可读取的计划JSON，或超过大小/嵌套限制；未保存候选计划。"); }
    }

    // Only redundant summaries are derived. Never invent a node, capability, kind, source or algorithm.
    private static void DeriveSummaries(JsonObject candidate)
    {
        if (candidate["strategy"] is not JsonObject strategy || candidate["nodes"] is not JsonArray nodes || nodes.Count == 0) return;
        string[] allowedModes = ["theory", "exercise", "project"];
        string[] allowedCapabilities = ["paper-exercise", "manual-project", "portable-c", "portable-cpp"];
        static bool Known(JsonNode? value, string[] allowed) => value is null || value is JsonArray a && a.All(v => v is JsonValue j && j.TryGetValue<string>(out var s) && allowed.Contains(s));
        // Unknown declared values must still fail Schema; do not erase unsupported capabilities.
        if (!Known(strategy["teachingModes"], allowedModes) || !Known(strategy["materialCapabilities"], allowedCapabilities)) return;
        var modes = new List<string>(); var capabilities = new List<string>();
        foreach (var node in nodes)
        {
            if (node?["type"]?.GetValue<string>() == "theory" && node["lab"] is null) modes.Add("theory");
            else if (node?["type"]?.GetValue<string>() == "lab" && node["lab"] is JsonObject lab &&
                lab["kind"]?.GetValue<string>() is string kind && allowedModes.Contains(kind) && kind != "theory" &&
                lab["toolchain"]?.GetValue<string>() is string capability && allowedCapabilities.Contains(capability))
            { modes.Add(kind); capabilities.Add(capability); }
            else return;
        }
        strategy["teachingModes"] = new JsonArray(modes.Distinct().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        strategy["materialCapabilities"] = new JsonArray(capabilities.Distinct().Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
    }

    private static IEnumerable<string> Problems(EvaluationResults result)
    {
        // A successful oneOf can contain a failed alternative; never report that alternative.
        if (result.IsValid) yield break;
        if (result.EvaluationPath.ToString().EndsWith("/additionalProperties", StringComparison.Ordinal))
            yield return SafeLocation(result.InstanceLocation.ToString()) + " [additionalProperties]";
        // Never echo schema error messages: they can include arbitrary model values/property names.
        if (result.HasErrors)
            foreach (var keyword in result.Errors!.Keys)
                if (keyword is "type" or "required" or "additionalProperties" or "enum" or "const" or
                    "pattern" or "minLength" or "maxLength" or "minimum" or "maximum" or "minItems" or "maxItems" or "uniqueItems")
                    yield return SafeLocation(result.InstanceLocation.ToString()) + " [" + keyword + "]";
        if (result.HasDetails)
            foreach (var child in result.Details)
                foreach (var issue in Problems(child)) yield return issue;
    }

    private static string SafeLocation(string pointer)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal) { "schemaVersion", "title", "background", "goal", "weeklyMinutes", "scope",
            "defaults", "field", "value", "reason", "clarifications", "limitations", "sourceKeys", "nodes", "key", "type", "objectives",
            "prerequisiteKeys", "estimatedMinutes", "lab", "mode", "previousLabKey", "toolchain", "rationale", "algorithms", "algorithmKeys", "name" };
        return "$" + string.Concat(pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(12)
            .Select(part => fields.Contains(part) ? "." + part : part.Length <= 5 && part.All(char.IsAsciiDigit) ? "[" + part + "]" : ".<未知字段>"));
    }
}
