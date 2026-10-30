using System.Text.RegularExpressions;

namespace StudyHarness.Application;

/// <summary>Advisory only: transcribed edge lists cannot establish which image is correct.</summary>
public static class ExamGraphConflicts
{
    public static string Describe(IEnumerable<ModelSource> sources)
    {
        var graphs = sources.Where(s => s.Text.Contains("[宿主资料用途：考题参照", StringComparison.Ordinal))
            .Select(s => new { Source = s, Edges = Edges(s.Text) }).Where(g => g.Edges.Count >= 3).ToArray();
        var warnings = new List<string>();
        for (var i = 0; i < graphs.Length; i++)
        for (var j = i + 1; j < graphs.Length; j++)
        {
            var a = graphs[i]; var b = graphs[j];
            var shared = a.Edges.Keys.Intersect(b.Edges.Keys).ToArray();
            if (shared.Length < 3) continue;
            var differentWeights = shared.Where(k => a.Edges[k] != b.Edges[k]).ToArray();
            var onlyA = a.Edges.Keys.Except(b.Edges.Keys).ToArray();
            var onlyB = b.Edges.Keys.Except(a.Edges.Keys).ToArray();
            if (differentWeights.Length == 0 && (onlyA.Length == 0 || onlyB.Length == 0)) continue;
            string Detail(Dictionary<string, string> edges, IEnumerable<string> keys) => string.Join("、", keys.Select(k => k + "=" + edges[k]));
            warnings.Add($"{a.Source.Id} 与 {b.Source.Id} 存在疑似图示转录冲突：前者 {Detail(a.Edges, differentWeights.Concat(onlyA))}；后者 {Detail(b.Edges, differentWeights.Concat(onlyB))}。页码见各资料的来源定位。");
        }
        return warnings.Count == 0 ? "" : "\n宿主转录差异提示（不是原图判定）：\n" + string.Join('\n', warnings) +
            "\n先确认是否同一题；不同题须分开。同题冲突不能凭模型记忆、语言或派生摘要选定原图。给出公共算法与明确条件下的分支结果，局部注明待核对，继续生成其余内容。\n";
    }

    private static Dictionary<string, string> Edges(string text)
    {
        var edges = new Dictionary<string, string>();
        text = Regex.Replace(text, @"([A-Za-z])\s*与\s*([A-Za-z])\s*相连[，,]\s*链路代价标注为\s*(\d+)", "$1-$2 $3", RegexOptions.CultureInvariant);
        foreach (Match m in Regex.Matches(text, @"(?<![A-Za-z0-9])([A-Za-z])\s*[-–—]\s*([A-Za-z])\s*(?:权重|代价|成本|[:=：])?\s*(\d+(?:\.\d+)?)(?![A-Za-z0-9])", RegexOptions.CultureInvariant))
        {
            var a = m.Groups[1].Value; var b = m.Groups[2].Value;
            if (a == b) continue;
            var key = string.CompareOrdinal(a, b) < 0 ? a + "-" + b : b + "-" + a;
            edges.TryAdd(key, m.Groups[3].Value);
        }
        return edges;
    }
}
