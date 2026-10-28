using StudyHarness.Contracts;

namespace StudyHarness.Application;

public sealed record PlanningEvidenceSelection(IReadOnlyList<KnowledgeFragment> Fragments, string Report);

public static class PlanningEvidence
{
    public static async Task<PlanningEvidenceSelection> SelectAsync(IKnowledgeStore knowledge,
        IReadOnlyList<PlanCollection> collections, IReadOnlyDictionary<(Guid, string), string> previews,
        string query, IReadOnlyList<SourceRef> existing, CancellationToken ct)
    {
        var result = new List<KnowledgeFragment>();
        var selected = existing.ToHashSet();
        var capacity = Math.Max(0, 98 - selected.Count);
        var cache = new Dictionary<SourceRef, string?>();
        async Task<string?> Body(SourceRef reference)
        {
            if (!cache.TryGetValue(reference, out var body))
            {
                body = (await knowledge.ResolveAsync(reference, ct)).Text;
                cache.Add(reference, body);
            }
            return body;
        }
        var paged = collections.Where(c => c.References.Any(r => r.Locator.Page is not null)).ToArray();
        var bins = new List<(PlanCollection Collection, int Region, Queue<SourceRef> Candidates, int Limit)>();
        foreach (var c in paged)
        {
            var pages = c.References.Where(r => r.Locator.Page is not null).GroupBy(r => r.Locator.Page).OrderBy(g => g.Key).ToArray();
            // Stratify every deck/book, including short PPTs; never spend another collection's share.
            for (var region = 0; region < Math.Min(3, pages.Length); region++)
            {
                var count = Math.Min(3, pages.Length);
                var section = pages.Skip(region * pages.Length / count).Take((region + 1) * pages.Length / count - region * pages.Length / count).ToArray();
                var anchors = Enumerable.Range(0, Math.Min(4, section.Length))
                    .Select(i => i * (section.Length - 1) / Math.Max(1, Math.Min(4, section.Length) - 1));
                var ordered = anchors.Concat(Enumerable.Range(0, section.Length)).Distinct()
                    .SelectMany(i => section[i]).ToArray();
                bins.Add((c, region, new Queue<SourceRef>(ordered), 144000 / Math.Max(1, paged.Length) / count));
            }
        }
        var used = new int[bins.Count]; var counts = new int[bins.Count];
        // One complete chunk per region per round: first/middle/end of all books precede extra samples.
        // Reserve at least one extra slot per collection for contents/query evidence.
        // Equal complete rounds avoid spending the final slots on the first few books.
        var rounds = Math.Min(4, Math.Max(1, (capacity - collections.Count) / Math.Max(1, bins.Count)));
        for (var round = 0; round < rounds; round++)
        for (var i = 0; i < bins.Count && result.Count < capacity; i++)
        {
            var bin = bins[i];
            while (bin.Candidates.TryDequeue(out var reference))
            {
                ct.ThrowIfCancellationRequested();
                if (selected.Contains(reference)) continue;
                var body = await Body(reference);
                if (string.IsNullOrWhiteSpace(body)) continue;
                // Always admit the first whole chunk in a region. Oversized chunks must
                // reach the final context check, not silently erase a book's later regions.
                if (counts[i] > 0 && used[i] + body.Length > bin.Limit) continue;
                selected.Add(reference); used[i] += body.Length; counts[i]++;
                result.Add(new(reference, bin.Collection.Manifest.CollectionId, body, false)); break;
            }
        }
        // Contents and query evidence get separate per-collection quotas and round-robin slots.
        var extras = new List<Queue<KnowledgeFragment>>();
        foreach (var c in collections)
        {
            var candidates = new List<KnowledgeFragment>(); var contents = 0;
            foreach (var entry in c.Manifest.Entries.Where(e => int.TryParse(e.Locator, out var page) && page <= 40))
            {
                var excerpt = previews.GetValueOrDefault((c.Manifest.CollectionId, entry.Key), "");
                if (!System.Text.RegularExpressions.Regex.IsMatch(excerpt, @"目录|contents|(?:…{2,}|\.{4,})\s*\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                foreach (var reference in c.References.Where(r => r.Locator.Page?.ToString() == entry.Locator))
                {
                    if (selected.Contains(reference)) continue;
                    var body = await Body(reference);
                    if (body is null || contents + body.Length > 12000 / Math.Max(1, collections.Count)) continue;
                    contents += body.Length; candidates.Add(new(reference, c.Manifest.CollectionId, body, false));
                }
            }
            if (knowledge is ICollectionKnowledge indexed)
                candidates.AddRange(await indexed.RetrieveAsync(c.LibraryId, query, [c.Manifest.CollectionId], 24000 / Math.Max(1, collections.Count), ct));
            extras.Add(new(candidates.DistinctBy(f => f.Reference)));
        }
        bool added;
        do
        {
            added = false;
            foreach (var queue in extras)
                while (result.Count < capacity && queue.TryDequeue(out var fragment))
                    if (selected.Add(fragment.Reference)) { result.Add(fragment); added = true; break; }
        } while (added && result.Count < capacity);
        var report = string.Join("\n", collections.Select((c, index) =>
        {
            var all = c.References.Where(selected.Contains).ToArray();
            var distribution = bins.Select((b, i) => (b, i)).Where(x => x.b.Collection == c)
                .Select(x => $"{new[] { "前", "中", "后" }[x.b.Region]}段新增{counts[x.i]}块");
            return $"d{index + 1} {c.References.FirstOrDefault()?.Locator.SourceLabel}：目录{c.Manifest.Entries.Count}页/项；就绪{c.Manifest.Entries.Count(e => e.State is "textReady" or "ocrReady")}；本次正文{all.Length}块；{string.Join('、', distribution)}。";
        }));
        return new(result, report + "\n正文是代表性完整片段，不是全书；某段零新增可能因已有选材、未取得正文或预算不足。完整目录均保留，不能把未抽样主题当成不存在或压缩后续章节；每课仍取绑定范围的完整相关证据。");
    }
}
