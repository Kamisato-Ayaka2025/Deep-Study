using System.Text.Json;
using System.Text.RegularExpressions;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

public static partial class LabMaterialRules
{
    public const string Notice = "模型生成，未经编译或运行验证。请先检查源文件、测试文件及测评方案；你自行选择环境、编写和测试并承担外部执行风险。规格检查不保证代码绝对安全或正确。软件不运行代码，也不自动上传你的外部代码或测试结果。";
    public static string AssessmentOverview(LabMaterial m) => m.SchemaVersion == 2
        ? "旧版材料：未分类，不含新版断言测试承诺。"
        : $"## 实验类型：{(m.ExperimentKind == "exercise" ? "独立题目（断言测试）" : "工程实践（操作验收）")}\n{m.ClassificationReason}\n" +
          string.Join("\n\n", (m.ManualChecks ?? []).Select(c => $"### {c.Id}\n操作/输入：{c.Input}\n\n预期现象：{c.Expected}"));
    private static bool Text(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Length <= max && !s.Contains('\0');
    public static bool FileName(string? path, bool source, bool cpp = false)
    {
        if (!Text(path, 180) || path!.Contains('\\') || path.Contains(':') || path.StartsWith('/')) return false;
        var parts = path.Split('/');
        if (parts.Length > 8 || parts.Any(p => p.Length is 0 or > 80 || p is "." or ".." || p.EndsWith('.') ||
            !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') || Device().IsMatch(p.Split('.')[0]))) return false;
        var ext = path[(path.LastIndexOf('.') + 1)..];
        return cpp ? (source ? ext == "cpp" : ext is "cpp" or "json") : (source ? ext is "c" or "h" : ext is "c" or "h" or "txt" or "md" or "json" or "csv");
    }
    public static bool HasTodo(string code) => Tokens().Matches(code).Any(m =>
        (m.Value.StartsWith("/*", StringComparison.Ordinal) || m.Value.StartsWith("//", StringComparison.Ordinal)) && m.Value.Contains("TODO", StringComparison.Ordinal));
    public static string WithoutComments(string code) => Tokens().Replace(code, m =>
        m.Value.StartsWith("/*", StringComparison.Ordinal) || m.Value.StartsWith("//", StringComparison.Ordinal)
            ? new string(m.Value.Select(c => c is '\r' or '\n' ? c : ' ').ToArray()) : m.Value);

    // Conservative lexical filters. This is not a C parser or proof of runtime safety.
    public static bool CodeAllowed(string code, bool cpp = false)
    {
        if (Danger().IsMatch(code) || code.Contains("##", StringComparison.Ordinal) || code.Contains("\\\n", StringComparison.Ordinal) || code.Contains("\\\r", StringComparison.Ordinal) || code.Contains("??", StringComparison.Ordinal)) return false;
        foreach (Match line in Directive().Matches(code))
        {
            var directive = line.Groups[1].Value.Trim();
            if (directive.StartsWith("include", StringComparison.Ordinal))
            {
                if (!(cpp ? CppHeader().IsMatch(directive) : Header().IsMatch(directive))) return false;
            }
            else if (!Regex.IsMatch(directive, @"^(?:ifndef|ifdef|endif|else|elif|if|define)\b", RegexOptions.CultureInvariant)) return false;
        }
        return !code.Contains("%:", StringComparison.Ordinal);
    }
    public static Result<LabMaterial> Validate(LabMaterial? m, IReadOnlyList<string> sourceKeys)
    {
        Result<LabMaterial> Bad() => Result<LabMaterial>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "实验材料不符合文件、用例、来源或安全规格；未发布。请重试或调整资料。");
        if (m is null || m.SchemaVersion is not (2 or 3 or 4) || !Text(m.Instructions, 32000) || !Text(m.Assessment, 32000) || !Text(m.SafetyNotes, 16000) ||
            m.SourceFiles is null || m.TestFiles is null || m.SourceFiles.Count is < 1 or > 32 || m.TestFiles.Count is < 1 or > 32 ||
            m.SourceFiles.Any(f => f is null || !FileName(f.Path, true, m.SchemaVersion == 4) || !Text(f.Text, 65536)) ||
            m.TestFiles.Any(f => f is null || !FileName(f.Path, false, m.SchemaVersion == 4) || !Text(f.Text, 65536)) ||
            !m.SourceFiles.Any(f => f.Path.EndsWith(m.SchemaVersion == 4 ? ".cpp" : ".c", StringComparison.Ordinal) && HasTodo(f.Text)) ||
            m.Cases is null || m.Cases.Count is < 1 or > 20 ||
            m.Cases.Any(c => c is null || !Text(c.Id, 80) || c.Input is null || c.Expected is null || c.Input.Length > 16384 || c.Expected.Length > 16384 || c.Input.Contains('\0') || c.Expected.Contains('\0')) ||
            m.Cases.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != m.Cases.Count ||
            m.SourceKeys is null || m.SourceKeys.Count is < 1 or > 99 || m.SourceKeys.Distinct().Count() != m.SourceKeys.Count || m.SourceKeys.Any(s => !sourceKeys.Contains(s))) return Bad();
        if (m.SchemaVersion is 3 or 4)
        {
            if (m.ExperimentKind is not ("exercise" or "project") || !Text(m.ClassificationReason, 2000) ||
                m.ManualChecks is null || m.ManualChecks.Count > 20 ||
                m.TestFiles.Any(f => f.Path.EndsWith(".txt", StringComparison.Ordinal)) ||
                m.ManualChecks.Any(c => c is null || !Text(c.Id, 80) || !Text(c.Input, 16384) || !Text(c.Expected, 16384)) ||
                m.ManualChecks.Select(c => c.Id).Distinct().Count() != m.ManualChecks.Count) return Bad();
            if (m.SchemaVersion == 3 && m.ExperimentKind == "exercise" && !m.TestFiles.Any(f => f.Path.EndsWith(".c", StringComparison.Ordinal) &&
                Regex.IsMatch(Tokens().Replace(f.Text, " "), @"\bassert\s*\(") &&
                Regex.IsMatch(Tokens().Replace(f.Text, " "), @"\bmain\s*\("))) return Bad();
            if (m.ExperimentKind == "exercise" && m.SourceFiles.Concat(m.TestFiles).Any(f =>
                (f.Path.EndsWith(".c", StringComparison.Ordinal) || f.Path.EndsWith(".h", StringComparison.Ordinal)) &&
                Regex.IsMatch(Tokens().Replace(f.Text, " "), @"(?m)^\s*#\s*define\s+(?:NDEBUG|assert)\b"))) return Bad();
            if (m.ExperimentKind == "project" && m.ManualChecks.Count == 0) return Bad();
        }
        var files = m.SourceFiles.Concat(m.TestFiles).ToArray();
        var names = files.Select(f => f.Path).ToArray();
        if ((m.ClassificationReason?.Length ?? 0) + (m.ManualChecks?.Sum(c => (long)c.Id.Length + c.Input.Length + c.Expected.Length) ?? 0) + files.Sum(f => (long)f.Text.Length) + m.Instructions.Length + m.Assessment.Length + m.SafetyNotes.Length + m.Cases.Sum(c => (long)c.Input.Length + c.Expected.Length) > 262144 ||
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length || names.Any(a => names.Any(b => b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase))) ||
            files.Any(f => (f.Path.EndsWith(".c", StringComparison.Ordinal) || f.Path.EndsWith(".h", StringComparison.Ordinal)) && !CodeAllowed(f.Text))) return Bad();
        if (m.SchemaVersion == 4 && !CppLabRules.Valid(m)) return Bad();
        // One canonical data file binds the user-visible cases to exported tests.
        var casesFile = m.TestFiles.SingleOrDefault(f => f.Path == "tests/cases.json");
        if (casesFile is null) return Bad();
        try
        {
            var exportedCases = JsonSerializer.Deserialize<LabCase[]>(casesFile.Text, Wire.Options);
            if (exportedCases is null || !exportedCases.SequenceEqual(m.Cases)) return Bad();
        }
        catch (JsonException) { return Bad(); }
        foreach (var file in files.Where(f => f.Path.EndsWith(".c", StringComparison.Ordinal) || f.Path.EndsWith(".h", StringComparison.Ordinal)))
            foreach (Match include in LocalHeader().Matches(file.Text))
            {
                var name = include.Groups[1].Value;
                var folder = file.Path.Contains('/') ? file.Path[..(file.Path.LastIndexOf('/') + 1)] : "";
                if (!FileName(name, true) || !names.Contains(folder + name, StringComparer.Ordinal)) return Bad();
            }
        return Result<LabMaterial>.Success(m);
    }
    public static Result<LabMaterial> Parse(string text, IReadOnlyList<string> sources)
    {
        try
        {
            if (text.Length > 600000) throw new JsonException();
            text = ModelJson.Unwrap(text);
            var options = new JsonSerializerOptions(Wire.Options);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                new[] { "referenceFiles", "completed", "status", "script", "apiKey" }.Any(k => doc.RootElement.TryGetProperty(k, out _))) throw new JsonException();
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !UniqueProperties(doc.RootElement) ||
                new[] { "schemaVersion", "instructions", "sourceFiles", "testFiles", "cases", "assessment", "safetyNotes", "sourceKeys" }.Any(k => !doc.RootElement.TryGetProperty(k, out _))) throw new JsonException();
            if (!doc.RootElement.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v is not (3 or 4) ||
                new[] { "experimentKind", "classificationReason" }.Any(k => !doc.RootElement.TryGetProperty(k, out _))) throw new JsonException();
            var material = JsonSerializer.Deserialize<LabMaterial>(text, options)!;
            if (!doc.RootElement.TryGetProperty("manualChecks", out _) && material.ExperimentKind == "exercise") material = material with { ManualChecks = [] };
            return v == 4 ? CppLabRules.Prepare(material, sources) : Validate(material, sources);
        }
        catch (JsonException) { return Result<LabMaterial>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "模型材料格式无效，未发布。"); }
    }
    private static bool UniqueProperties(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == element.EnumerateObject().Count() && element.EnumerateObject().All(p => UniqueProperties(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().All(UniqueProperties),
        _ => true
    };
    [GeneratedRegex(@"^include\s*<(?:iostream|istream|ostream|sstream|vector|array|string|algorithm|utility|limits|cstdint|cstddef|cassert|cmath|numeric|queue|stack|deque|map|set|unordered_map|unordered_set|tuple|functional|bitset|iomanip|climits|cstdlib|cctype)>\s*(?://[^\r\n]*)?$", RegexOptions.CultureInvariant)] private static partial Regex CppHeader();
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Device();
    [GeneratedRegex("\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|/\\*[\\s\\S]*?\\*/|//[^\\r\\n]*", RegexOptions.CultureInvariant)] private static partial Regex Tokens();
    [GeneratedRegex(@"\b(system|popen|_popen|exec\w*|spawn\w*|fork|socket|connect|fopen|freopen|tmpfile|tmpnam|tempnam|_tempnam|open|remove|rename|unlink|Create\w*|ShellExecute\w*|WinExec|LoadLibrary\w*|GetProcAddress|VirtualAlloc\w*|Reg\w*|asm|__asm|gets)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Danger();
    [GeneratedRegex(@"(?m)^\s*#([^\r\n]*)", RegexOptions.CultureInvariant)] private static partial Regex Directive();
    [GeneratedRegex("^include\\s*(?:<(?:stdio|stdlib|stdint|stdbool|stddef|limits|float|math|string|ctype|assert)\\.h>|\"[A-Za-z0-9_./-]+\\.h\")\\s*(?://[^\\r\\n]*)?$", RegexOptions.CultureInvariant)] private static partial Regex Header();
    [GeneratedRegex("(?m)^\\s*#\\s*include\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant)] private static partial Regex LocalHeader();
}
