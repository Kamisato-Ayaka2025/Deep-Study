using System.Text;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

public static class PlanningDirectory
{
    // Compact only the planning view. Frozen manifests, page identities and lesson text stay intact.
    public static string Build(IReadOnlyList<PlanCollection> collections,
        IReadOnlyDictionary<(Guid, string), string> previews, IReadOnlySet<Guid> presentations, bool compactStatus = false)
    {
        var output = new StringBuilder();
        for (var i = 0; i < collections.Count; i++)
        {
            var manifest = collections[i].Manifest;
            if (!presentations.Contains(manifest.CollectionId))
            {
                var statuses = manifest.Entries.Select(e => (e.State, Flags: string.Join(',', e.QualityFlags))).Distinct()
                    .Select((x, n) => (x, Key: "Q" + (n + 1))).ToDictionary(x => x.x, x => x.Key);
                if (compactStatus)
                    foreach (var status in statuses)
                        output.AppendLine($"资料d{i + 1}状态{status.Value}={status.Key.State};质量={status.Key.Flags}（以下逐页引用，不省略质量标记）");
                foreach (var e in manifest.Entries)
                    output.AppendLine($"{AdaptivePlanRules.DirectoryKey(i, e.Key)}\t{e.Heading}\t{(compactStatus ? statuses[(e.State, string.Join(',', e.QualityFlags))] : e.State + "\t" + string.Join(',', e.QualityFlags))}\t索引摘录:{previews.GetValueOrDefault((manifest.CollectionId, e.Key), "未取得")}");
                continue;
            }
            output.AppendLine($"PPT目录d{i + 1}：T行定义标题/状态/质量标记；页行=完整目录键/T编号/索引摘录。T编号仅本集合有效，不是coverage键。相同标题不等于相同知识点；每页键均须覆盖。摘录仅供规划，完整文字、图示转写和备注在本地，按课取材。");
            var titles = new Dictionary<(string, string, string), int>();
            foreach (var e in manifest.Entries)
            {
                var identity = (e.Heading, e.State, string.Join(',', e.QualityFlags));
                if (!titles.TryGetValue(identity, out var title))
                {
                    title = titles.Count + 1;
                    titles.Add(identity, title);
                    output.AppendLine($"T{title}\t{e.Heading}\t{e.State}\t{identity.Item3}");
                }
                var excerpt = previews.GetValueOrDefault((manifest.CollectionId, e.Key), "未取得");
                // Remove only a literal duplicate prefix, never infer or summarize unseen content.
                if (e.Heading.Length > 0 && excerpt.StartsWith(e.Heading, StringComparison.Ordinal))
                    excerpt = excerpt[e.Heading.Length..].TrimStart();
                output.AppendLine($"{AdaptivePlanRules.DirectoryKey(i, e.Key)}\tT{title}\t{excerpt}");
            }
        }
        return output.ToString().TrimEnd();
    }
}
