using System.Text.RegularExpressions;

namespace StudyHarness.Application;

public static class SourceEvidenceRules
{
    // Conservative conflict signal for explicit named definitions. This is not a truth checker.
    public static void CheckDefinitions(IEnumerable<(string Source, string Text)> sources)
    {
        var definitions = new Dictionary<string, (string Source, string Body)>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
            foreach (Match match in Regex.Matches(source.Text, @"(?m)^\s*(?:定义|Definition)\s+([^：:\r\n]{1,60})[：:]\s*([^\r\n]+)", RegexOptions.IgnoreCase))
            {
                var name = match.Groups[1].Value.Trim(); var body = Regex.Replace(match.Groups[2].Value, @"\s+", "").TrimEnd('。', '.');
                if (definitions.TryGetValue(name, out var prior) && prior.Source != source.Source && prior.Body != body)
                    throw new InvalidDataException("来源中的同名定义冲突；请核对并缩小来源后重新确认，未生成答案。");
                definitions[name] = (source.Source, body);
            }
    }
}
