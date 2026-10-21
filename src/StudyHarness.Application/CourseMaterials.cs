using StudyHarness.Contracts;

namespace StudyHarness.Application;

public static class CourseMaterials
{
    public static string WithQuality(IReadOnlyList<PlanCollection>? collections, PlanSource source, string body)
    {
        body = SourceText.ForModel(source.Reference.Locator.SourceLabel, body);
        var pendingOcr = collections?.Any(c => c.LibraryId == source.Reference.LibraryId && c.References.Contains(source.Reference) &&
            c.Manifest.Entries.Any(e => e.State == "ocrReady" && source.Reference.Locator.Block.StartsWith(e.Key + ":", StringComparison.Ordinal))) == true;
        return pendingOcr ? "[宿主来源质量：OCR待复核；图表标签、数字与位置关系未验证，不可据此编造事实或测验答案。]\n" + body : body;
    }
    public static async Task<IReadOnlyList<PlanSource>> SelectAsync(IKnowledgeStore knowledge, PlanDocument plan, PlannedNode node, CancellationToken ct)
    {
        async Task<IReadOnlyList<PlanSource>> SelectPlain(IReadOnlyList<PlanSource> available)
        {
            if (node.Lab is not null) return available.Where(s => node.Lab.SourceKeys.Contains(s.Key)).ToArray();
            if (plan.Candidate.SchemaVersion == 1) return available; // historical frozen V1 selection remains compatible
            var candidates = new List<(PlanSource Source, string Text)>();
            foreach (var source in available)
            {
                var resolved = await knowledge.ResolveAsync(source.Reference, ct);
                if (resolved.Text is null) throw new IOException("Frozen source unavailable");
                candidates.Add((source, resolved.Text));
            }
            var terms = LessonSourceSelector.Terms(node.Title + " " + string.Join(' ', node.Objectives));
            SourceEvidenceRules.CheckDefinitions(candidates.Where(c => LessonSourceSelector.Score(c.Text, terms) > 0).Select(c => (c.Source.Reference.Locator.SourceLabel, c.Text)));
            return LessonSourceSelector.Choose(node, candidates);
        }
        if (plan.Collections is not { Count: > 0 } || knowledge is not ICollectionKnowledge indexed)
            return await SelectPlain(plan.Sources);
        var result = new List<PlanSource>(); var used = 0; var fetchBudget = new SourceAcquisitionBudget();
        foreach (var (collection, index) in plan.Collections.Select((c, i) => (c, i)))
        {
            var entries = Entries(plan, node, collection, index);
            if (entries.Length == 0) continue;
            var query = entries.All(e => e.StartsWith("page-", StringComparison.Ordinal))
                ? string.Join('\n', node.Objectives.Prepend(node.Title))
                : node.Title + " " + string.Join(' ', node.Objectives) + " " + string.Join(' ', plan.Candidate.Nodes.Where(n => node.PrerequisiteKeys.Contains(n.Key)).Select(n => n.Title));
            var fragments = indexed is IBoundedCollectionKnowledge bounded
                ? await bounded.RetrieveFrozenAsync(collection, query, entries, 100000 - used, fetchBudget, ct)
                : await indexed.RetrieveFrozenAsync(collection, query, entries, 100000 - used, ct);
            foreach (var fragment in fragments)
            {
                var original = plan.Sources.FirstOrDefault(s => s.Reference == fragment.Reference);
                result.Add(original ?? new("c" + fragment.Reference.VersionId.ToString("N"), fragment.Reference, false)); used += fragment.Text.Length;
            }
        }
        // Keep explicitly selected lab material and non-collection sources within the same bounded request.
        var plain = plan.Sources.Where(s => !plan.Collections.Any(c => c.References.Contains(s.Reference)) ||
            result.Count == 0 && MissingBinding(plan, node)).ToArray();
        result.AddRange(await SelectPlain(plain));
        return result.Distinct().ToArray();
    }
    public static bool MissingBinding(PlanDocument plan, PlannedNode node) =>
        plan.Collections is { Count: > 0 } && !plan.Candidate.Coverage!.Any(e => e.State == "included" && e.NodeKeys.Contains(node.Key));
    public static string? MissingBindingError(PlanDocument plan)
    {
        var keys=plan.Candidate.Nodes.Where(n=>n.Type=="theory" && MissingBinding(plan,n)).Select(n=>n.Key).ToArray();
        return keys.Length==0 ? null : "PCOV007：以下理论节点缺少教材页绑定："+string.Join(", ",keys.Take(40))+"。在coverage中把这些key加入实际支持该课的included条目的nodeKeys，同一条目可支持多课；综合复习课同样需要绑定。保留全部节点、类型及已有绑定，不虚构目录条目。";
    }

    // Compatibility selection is derived at request time; never rewrite the confirmed plan/hash.
    public static string[] Entries(PlanDocument plan, PlannedNode node, PlanCollection collection, int index)
    {
        var prefix = $"d{index + 1}/";
        var coverage = plan.Candidate.Coverage ?? [];
        var bound = coverage.Where(e => e.State == "included" && e.NodeKeys.Contains(node.Key) && e.EntryKey.StartsWith(prefix, StringComparison.Ordinal))
            .Select(e => e.EntryKey[prefix.Length..]).ToArray();
        if (bound.Length > 0 || node.Type != "theory" || !MissingBinding(plan, node)) return bound;
        var eligible = collection.Manifest.Entries.Where(e => coverage.Any(c => c.State == "included" && c.EntryKey == prefix + e.Key)).ToArray();
        return node.Objectives.Prepend(node.Title).SelectMany(objective =>
        {
            var terms = LessonSourceSelector.Terms(objective);
            return eligible.Select(e => (Entry: e, Score: LessonSourceSelector.Score(e.Heading + " " + e.Locator, terms)))
                .Where(e => e.Score > 0).OrderByDescending(e => e.Score).Take(2).Select(e => e.Entry.Key);
        }).Distinct().Take(8).ToArray();
    }
    public static bool Allowed(PlanDocument plan, PlannedNode node, PlanSource source) =>
        plan.Sources.Contains(source) && (node.Lab is null || node.Lab.SourceKeys.Contains(source.Key)) ||
        plan.Collections?.Select((c, i) => (c, i)).Any(pair => (pair.c.References.Contains(source.Reference) || DynamicSiteReference(pair.c, source.Reference)) &&
            Entries(plan, node, pair.c, pair.i).Any(entry => source.Reference.Locator.Block.StartsWith(entry + ":", StringComparison.Ordinal) || Related(pair.c, source.Reference, entry))) == true;
    private static bool Related(PlanCollection collection, SourceRef reference, string entry)
    {
        if (collection.Structure is not { } index) return false;
        var byId = index.Units.ToDictionary(u => u.Id); var children = index.Units.Where(u => u.ParentUnitId is not null).ToLookup(u => u.ParentUnitId!);
        var queue = new Queue<SemanticUnit>(index.Units.Where(u => u.References.Contains(reference))); var visited = new HashSet<string>();
        while (queue.TryDequeue(out var unit))
        {
            if (!visited.Add(unit.Id)) continue;
            if (unit.Spans.Any(s => s.EntryKey == entry)) return true;
            if (unit.ParentUnitId is { } parent && byId.TryGetValue(parent, out var ancestor)) queue.Enqueue(ancestor);
            foreach (var linked in children[unit.Id]) queue.Enqueue(linked);
        }
        return false;
    }
    private static bool DynamicSiteReference(PlanCollection collection, SourceRef reference)
    {
        if (reference.LibraryId != collection.LibraryId) return false;
        var entry = collection.Manifest.Entries.FirstOrDefault(e => e.QualityFlags.Contains("fetchOnDemand") && reference.Locator.Block.StartsWith(e.Key + ":", StringComparison.Ordinal));
        if (entry is null || reference.Locator.SourceLabel != entry.Locator) return false;
        var position = reference.Locator.Block[(entry.Key.Length + 1)..].Split('-')[0];
        if (!int.TryParse(position, out var start) || start < 0) return false;
        static Guid Id(string value) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))[..16]);
        return reference.NodeId == Id($"{collection.Manifest.CollectionId}/{entry.Key}/{start}") &&
            reference.VersionId == Id($"{collection.Manifest.Version}/{entry.Key}/{start}/{reference.ContentHash}");
    }
}
