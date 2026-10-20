using System.Text.RegularExpressions;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

// Pure validation only. No filesystem, provider, compiler or completion side effects.
public static partial class LabDraftRules
{
    private static bool Text(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Contains('\0');
    private static bool Id(string? id) => Text(id, 80) && id!.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    // Canonical portable relative names; deliberately reject Windows aliases, ADS and device names.
    public static bool IsFileName(string? path)
    {
        if (!Text(path, 180) || path!.Contains('\\') || path.StartsWith('/') || path.Contains(':')) return false;
        var parts = path.Split('/');
        return parts.Length <= 8 && parts.All(p => p.Length > 0 && p.Length <= 80 && p is not ("." or "..") &&
            !p.EndsWith('.') && p.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_') &&
            !DeviceName().IsMatch(p.Split('.')[0])) &&
            (path.EndsWith(".c", StringComparison.Ordinal) || path.EndsWith(".h", StringComparison.Ordinal));
    }

    private static bool Files(IReadOnlyList<LabFile>? files, bool allowEmpty)
    {
        if (files is null || files.Count > 32 || (!allowEmpty && files.Count == 0) ||
            files.Any(f => f is null || !IsFileName(f.Path) || !Text(f.Text, 65536))) return false;
        var names = files.Select(f => f.Path).ToArray();
        return files.Sum(f => (long)f.Text.Length) <= 262144 &&
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length &&
            !names.Any(a => names.Any(b => b.StartsWith(a + "/", StringComparison.OrdinalIgnoreCase)));
    }

    public static Result<LabDraft> Validate(LabDraft? draft, PlannedLab plan, IReadOnlyList<string> sentSourceKeys)
    {
        Result<LabDraft> Bad() => Result<LabDraft>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "实验材料、TODO、测试集或来源无效；未发布或执行。");
        if (draft is null || draft.SchemaVersion != 1 || !Text(draft.Instructions, 32000) || plan is null ||
            plan.Toolchain is not ("msvc" or "mingw") || plan.Mode is not ("independent" or "continuous") ||
            (plan.Mode == "continuous" ? !Id(plan.PreviousLabKey) : plan.PreviousLabKey is not null) ||
            !Files(draft.StarterFiles, plan.Mode == "continuous") || !Files(draft.ReferenceFiles, false) ||
            !draft.ReferenceFiles.Any(f => f.Path.EndsWith(".c", StringComparison.Ordinal)) ||
            draft.Cases is null || draft.Cases.Count is < 1 or > 20 ||
            draft.Cases.Any(c => c is null || !Id(c.Id) || c.Input is null || c.Expected is null ||
                c.Input.Length > 16384 || c.Expected.Length > 16384 || c.Input.Contains('\0') || c.Expected.Contains('\0')) ||
            draft.Cases.Select(c => c.Id).Distinct().Count() != draft.Cases.Count ||
            draft.Insertions is null || draft.Insertions.Count > 32 ||
            draft.SourceKeys is null || draft.SourceKeys.Count is < 1 or > 99 ||
            draft.SourceKeys.Any(k => !Id(k)) || draft.SourceKeys.Distinct().Count() != draft.SourceKeys.Count ||
            sentSourceKeys is null || plan.SourceKeys is null ||
            draft.SourceKeys.Any(k => !sentSourceKeys.Contains(k) || !plan.SourceKeys.Contains(k))) return Bad();
        if (draft.Insertions.Any(i => i is null || !IsFileName(i.Path) || !Text(i.Anchor, 256) ||
            !Text(i.Text, 65536) || !HasTodo(i.Text) || draft.StarterFiles.Any(f => string.Equals(f.Path, i.Path, StringComparison.OrdinalIgnoreCase))) ||
            draft.Insertions.Select(i => (i.Path.ToUpperInvariant(), i.Anchor)).Distinct().Count() != draft.Insertions.Count ||
            draft.Insertions.Sum(i => (long)i.Text.Length) > 262144) return Bad();
        if (plan.Mode == "independent" && (draft.Insertions.Count != 0 || !draft.StarterFiles.Any(f => f.Path.EndsWith(".c", StringComparison.Ordinal)))) return Bad();
        if (!draft.StarterFiles.Any(f => HasTodo(f.Text)) && !draft.Insertions.Any(i => HasTodo(i.Text))) return Bad();
        return Result<LabDraft>.Success(draft);
    }

    // A lexical comment check, not a C compiler. Compilation remains a mandatory later gate.
    public static bool HasTodo(string code) => CToken().Matches(code).Any(m =>
        (m.Value.StartsWith("/*", StringComparison.Ordinal) || m.Value.StartsWith("//", StringComparison.Ordinal)) && m.Value.Contains("TODO", StringComparison.Ordinal));

    public static LabDraftPreview Preview(LabDraft draft) => new(draft.Instructions,
        draft.StarterFiles.ToArray(), draft.Cases.ToArray(), draft.Insertions.ToArray());

    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceName();
    [GeneratedRegex("\"(?:\\\\.|[^\"\\\\])*\"|'(?:\\\\.|[^'\\\\])*'|/\\*[\\s\\S]*?\\*/|//[^\\r\\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex CToken();
}
