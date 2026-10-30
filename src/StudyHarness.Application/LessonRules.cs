using StudyHarness.Contracts;
using System.Text.RegularExpressions;

namespace StudyHarness.Application;

public static class LessonRules
{
    private static bool Text(string? value, int max = 16000) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;
    private static bool Id(string? value) => Text(value, 80) && value!.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static Result<TheoryLesson> Validate(TheoryLesson? lesson, IReadOnlyList<PlanSource> sentSources)
    {
        Result<TheoryLesson> Bad() => Result<TheoryLesson>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "课件结构、题目或来源无效。");
        if (lesson is null || lesson.SchemaVersion is not (1 or 2 or 3 or 4 or 5) || lesson.Blocks is null || lesson.Questions is null ||
            lesson.SourceKeys is null || lesson.SourceKeys.Count > 99 || (lesson.SchemaVersion < 3 && lesson.SourceKeys.Count == 0) || lesson.SourceKeys.Any(s => !Id(s)) ||
            sentSources is null || sentSources.Count > CourseMaterials.MaximumContextReferences || sentSources.Any(s => s is null || !Id(s.Key) || s.Reference is null ||
                s.Reference.SchemaVersion != 1 || s.Reference.LibraryId == Guid.Empty || s.Reference.NodeId == Guid.Empty ||
                s.Reference.VersionId == Guid.Empty || s.Reference.ContentHash is null || s.Reference.ContentHash.Length != 64 ||
                !s.Reference.ContentHash.All(Uri.IsHexDigit) || s.Reference.Locator is null) ||
            lesson.Blocks.Count < (lesson.SchemaVersion == 5 ? 1 : 2) || lesson.Blocks.Count > (lesson.SchemaVersion >= 2 ? 128 : 200) || lesson.Questions.Count < 1 || lesson.Questions.Count > (lesson.SchemaVersion == 5 ? 100 : lesson.SchemaVersion >= 2 ? 20 : 10) ||
            lesson.Blocks.Any(b => b is null || !Id(b.Id)) || lesson.Questions.Any(q => q is null || !Id(q.Id)) ||
            lesson.Blocks.Select(b => b.Id).Distinct().Count() != lesson.Blocks.Count ||
            lesson.Questions.Select(q => q.Id).Distinct().Count() != lesson.Questions.Count ||
            lesson.SchemaVersion != 5 && !lesson.Blocks.Any(b => b.Kind == "example")) return Bad();
        foreach (var q in lesson.Questions)
        {
            if (lesson.SchemaVersion == 5 && q.Kind == "written")
            {
                if (!Text(q.Prompt, 16384) || !Text(q.ReferenceAnswer, 32768) || q.Options is not { Count: 0 }) return Bad();
                continue;
            }
            if (!Text(q.Prompt, lesson.SchemaVersion >= 2 ? 16384 : 16000) || !Text(q.Explanation, lesson.SchemaVersion >= 2 ? 16384 : 16000) || q.Options is null || q.Options.Count is < 2 or > 10 || (lesson.SchemaVersion >= 3 && q.Options.Count != 4) ||
                q.Options.Any(o => o is null || !Id(o.Id) || !Text(o.Text)) ||
                q.Options.Select(o => o.Id).Distinct().Count() != q.Options.Count ||
                q.Options.Select(o => o.Text.Trim()).Distinct(StringComparer.Ordinal).Count() != q.Options.Count ||
                q.Options.Count(o => o.Id == q.CorrectOptionId) != 1) return Bad();
            if (lesson.SchemaVersion >= 2 && EquivalentLinearChoices(q))
                return Result<TheoryLesson>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "复杂度单选题含等价选项；变量应独立变化，并使用唯一最紧渐近界。");
        }
        foreach (var b in lesson.Blocks)
        {
            if (b.Kind == "question")
            {
                if (b.Text is not null || !lesson.Questions.Any(q => q.Id == b.QuestionId)) return Bad();
            }
            else if (b.Kind is not ("paragraph" or "heading" or "code" or "math" or "diagram" or "example") ||
                !Text(b.Text, lesson.SchemaVersion >= 2 ? 16384 : 16000) || b.QuestionId is not null) return Bad();
        }
        if (lesson.Questions.Any(q => lesson.Blocks.Count(b => b.QuestionId == q.Id) != 1) ||
            lesson.SourceKeys.Distinct().Count() != lesson.SourceKeys.Count ||
            sentSources.Select(s => s.Key).Distinct().Count() != sentSources.Count ||
            lesson.SourceKeys.Any(key => !sentSources.Any(s => s.Key == key))) return Bad();
        if (lesson.SchemaVersion >= 2 && lesson.Blocks.Sum(b => (long)(b.Text?.Length ?? 0)) > 65536) return Bad();
        // Content is data, never HTML or executable code. The future renderer must escape
        // text and use allowlisted local renderers; validation is not an XSS sanitizer.
        return Result<TheoryLesson>.Success(lesson);
    }
    private static bool EquivalentLinearChoices(LessonQuestion question)
    {
        // A deliberately small symbolic check: sums of single-letter parameters only.
        // It rejects demonstrably equivalent choices, not a claim of general quiz correctness.
        var assignments = Regex.Matches(question.Prompt, @"\b([a-z])\s*=\s*(\d+)\b")
            .Cast<Match>().GroupBy(m => m.Groups[1].Value)
            .Where(g => g.Select(m => m.Groups[2].Value).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Groups[2].Value);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var option in question.Options)
        {
            var match = Regex.Match(option.Text.Trim(), @"^O\s*\(\s*([a-z](?:\s*\+\s*[a-z])*)\s*\)$");
            if (!match.Success) continue;
            var terms = match.Groups[1].Value.Split('+').Select(s => s.Trim()).Select(s =>
                assignments.TryGetValue(s, out var value) ? "assigned:" + value : "variable:" + s);
            var key = string.Join("+", terms.Distinct().Order(StringComparer.Ordinal));
            if (!seen.Add(key)) return true;
        }
        return false;
    }
}
