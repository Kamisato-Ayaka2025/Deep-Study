using System.Text.RegularExpressions;

namespace StudyHarness.Application;

public sealed record SemanticSlice(int Start, int End, string Kind, string? Parent, bool Continuation);

// Lossless local structure extraction. Offsets always address the supplied text.
public static class SemanticChunker
{
    public const string Version = "semantic-v3";
    public const string IndexVersion = "structure-v1";
    public const int Target = 6000;
    public const int Maximum = 16000;
    public static string Kind(string text)
    {
        var first = Regex.Replace(SourceText.Markers(text.TrimStart(' ', '\r', '\n')), @"^#{1,6}\s+", "").TrimStart('*');
        if (Regex.IsMatch(first, @"^\?{3}\+?\s+example\b", RegexOptions.IgnoreCase)) return "example";
        if (Regex.IsMatch(first, @"^\?{3}\+?\s+(note|tip|warning)\b", RegexOptions.IgnoreCase)) return "paragraph";
        if (Regex.IsMatch(first, @"^(证明|Proof\b)", RegexOptions.IgnoreCase)) return "proof";
        if (Regex.IsMatch(first, @"^(定理|引理|Theorem\b|Lemma\b)", RegexOptions.IgnoreCase)) return "theorem";
        if (Regex.IsMatch(first, @"^(定义|Definition\b)", RegexOptions.IgnoreCase)) return "definition";
        if (Regex.IsMatch(first, @"^(例题|例\s*\d|Example\b)", RegexOptions.IgnoreCase)) return "example";
        if (Regex.IsMatch(first, @"^(解答|解[：:]|Solution\b)", RegexOptions.IgnoreCase)) return "solution";
        if (first.StartsWith("```", StringComparison.Ordinal) || first.StartsWith("~~~", StringComparison.Ordinal)) return "code";
        if (first.StartsWith("$$", StringComparison.Ordinal) || first.StartsWith("\\[", StringComparison.Ordinal)) return "formula";
        return first.StartsWith('|') ? "table" : "paragraph";
    }
    public static IReadOnlyList<SemanticSlice> Split(string text)
    {
        if (text.Length == 0) return [];
        var boundaries = new List<int> { 0 }; var fence = ""; var formulaEnd = "";
        var previousBlank = false; var closedBoundary = false; var currentKind = "paragraph";
        foreach (Match line in Regex.Matches(text, @"(?m)^.*(?:\n|$)"))
        {
            if (line.Length == 0) continue;
            if (boundaries.Count > 50000) throw new InvalidDataException("Semantic structure budget exceeded");
            var trimmed = SourceText.Markers(line.Value.Trim());
            var isFence = Regex.Match(trimmed, @"^(`{3,}|~{3,})");
            if (fence.Length > 0)
            { if (trimmed.StartsWith(fence, StringComparison.Ordinal)) { fence = ""; closedBoundary = true; } continue; }
            if (formulaEnd.Length > 0) { if (trimmed == formulaEnd) { formulaEnd = ""; closedBoundary = true; } continue; }
            var kind = Kind(trimmed);
            var separator = Regex.IsMatch(trimmed, @"^(?:-{3,}|\*{3,}|_{3,})$");
            var heading = Regex.IsMatch(trimmed, @"^#{1,6}\s+|^\?{3}\+?\s+\w+|^第[一二三四五六七八九十0-9]+[章节]\s*");
            var explicitStart = kind is "definition" or "theorem" or "example" or "proof" or "solution" or "code" or "formula";
            if (line.Index == 0) currentKind = kind;
            var ordinaryBoundary = (previousBlank || line.Index > 0 && text[line.Index - 1] == '\n') && currentKind == "paragraph" && line.Index - boundaries[^1] >= Target;
            if (line.Index > boundaries[^1] && (heading || separator || explicitStart || ordinaryBoundary || trimmed.Length > 0 && (closedBoundary || kind == "table" && currentKind != "table" || currentKind == "table" && kind != "table")))
            { boundaries.Add(line.Index); currentKind = kind; }
            if (trimmed.Length > 0) closedBoundary = false;
            if (isFence.Success) fence = isFence.Value;
            if (trimmed is "$$" or "\\[") formulaEnd = trimmed == "$$" ? "$$" : "\\]";
            previousBlank = trimmed.Length == 0;
        }
        boundaries.Add(text.Length);
        var result = new List<SemanticSlice>(); string? parent = null;
        for (var i = 0; i < boundaries.Count - 1; i++)
        {
            var start = boundaries[i]; var end = boundaries[i + 1]; var kind = Kind(text[start..end]);
            if (kind is "theorem" or "example") parent = "offset-" + start;
            var related = kind is "proof" or "solution" ? parent : null;
            // Large units split only on line/step boundaries; all children are marked as continuations.
            var position = start; var child = 0;
            while (end - position > Maximum)
            {
                var cut = text.LastIndexOf('\n', position + Maximum - 1, Maximum);
                if (cut < position) break; // indivisible long line retained; selector must report insufficient budget
                cut++;
                result.Add(new(position, cut, kind, related ?? "offset-" + start, child++ > 0)); position = cut;
            }
            if (position < end) result.Add(new(position, end, kind, related, child > 0));
        }
        return result;
    }
}
