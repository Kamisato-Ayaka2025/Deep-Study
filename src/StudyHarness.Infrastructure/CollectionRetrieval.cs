using System.Text.Json;
using StudyHarness.Contracts;
using StudyHarness.Application;

namespace StudyHarness.Infrastructure;

internal sealed partial class CollectionStore
{
    public IReadOnlyList<KnowledgeFragment> SearchFrozen(PlanCollection binding, string query, IReadOnlyList<string> entries, int budget, CancellationToken ct)
    {
        if (!File.Exists(Database)) throw new IOException("Frozen collection unavailable");
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT manifest FROM collection_versions WHERE collection_id=$id AND version=$version";
        command.Parameters.AddWithValue("$id", binding.Manifest.CollectionId.ToString()); command.Parameters.AddWithValue("$version", binding.Manifest.Version);
        if (command.ExecuteScalar() is not string json || Json(binding.Manifest) != json) throw new InvalidDataException("Frozen directory changed");
        if (entries.Any(key => !binding.Manifest.Entries.Any(e => e.Key == key))) throw new InvalidDataException("Unknown entry");
        var terms = Tokens(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Take(40).ToHashSet();
        var candidates = new List<(string Entry, int Start, string Text, SourceRef Reference, int Score)>();
        command.CommandText = "SELECT entry_key,start,text,reference FROM chunks WHERE collection_id=$id AND version=$version ORDER BY rowid";
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested(); var key = reader.GetString(0);
                if (entries.Count > 0 && !entries.Contains(key)) continue;
                var text = reader.GetString(2); var reference = JsonSerializer.Deserialize<SourceRef>(reader.GetString(3), Wire.Options)!;
                if (Hash(text) != reference.ContentHash) throw new InvalidDataException("Chunk hash mismatch");
                var score = Tokens(text + " " + reference.Locator.Heading).Split(' ').Count(terms.Contains);
                candidates.Add((key, reader.GetInt32(1), text, reference, score));
            }
        // PDF page bindings describe the eligible chapter, not a requirement to send every page.
        // Select evidence independently for each lesson objective, then retain semantic dependencies.
        var paged = entries.Count > 1 && entries.All(key => key.StartsWith("page-", StringComparison.Ordinal));
        var seedsList = new List<(string Entry, int Start, string Text, SourceRef Reference, int Score)>();
        if (paged)
        {
            foreach (var objective in query.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var objectiveTerms = LessonSourceSelector.Terms(objective);
                var best = candidates.Where(c => LessonSourceSelector.HasBody(c.Text))
                    .Select(c => (Candidate: c, Relevance: LessonSourceSelector.Score(c.Text, objectiveTerms)))
                    .Where(c => c.Relevance > 0).OrderByDescending(c => c.Relevance).FirstOrDefault();
                if (best.Relevance > 0) seedsList.Add(best.Candidate);
            }
            if (seedsList.Count == 0) throw new InvalidDataException("No relevant textbook evidence for lesson objectives");
        }
        foreach (var group in candidates.Where(_ => !paged).GroupBy(c => c.Entry))
        {
            var ranked = group.Where(c => LessonSourceSelector.HasBody(c.Text)).OrderByDescending(c => c.Score).ThenBy(c => c.Start).ToArray();
            var uncovered = terms.Where(t => ranked.Any(c => (c.Text + " " + c.Reference.Locator.Heading).Contains(t, StringComparison.OrdinalIgnoreCase))).ToHashSet();
            if (ranked.Length > 0) seedsList.Add(ranked[0]);
            foreach (var c in ranked)
            {
                if (!uncovered.Any(t => (c.Text + " " + c.Reference.Locator.Heading).Contains(t, StringComparison.OrdinalIgnoreCase))) continue;
                seedsList.Add(c); uncovered.RemoveWhere(t => (c.Text + " " + c.Reference.Locator.Heading).Contains(t, StringComparison.OrdinalIgnoreCase));
                if (uncovered.Count == 0) break;
            }
        }
        if (binding.Manifest.Entries.Any(e => e.QualityFlags.Contains(PresentationReader.Version)))
        {
            // Slides are sparse. Keep a contiguous same-title explanation together, without
            // pulling in the next topic or treating a chapter-wide binding as required context.
            var manifestEntries = binding.Manifest.Entries;
            foreach (var seed in seedsList.ToArray())
            {
                var position = manifestEntries.ToList().FindIndex(e => e.Key == seed.Entry);
                if (position < 0) continue;
                var title = manifestEntries[position].Heading;
                var first = position; var last = position;
                while (first > 0 && manifestEntries[first - 1].Heading == title && entries.Contains(manifestEntries[first - 1].Key)) first--;
                while (last + 1 < manifestEntries.Count && manifestEntries[last + 1].Heading == title && entries.Contains(manifestEntries[last + 1].Key)) last++;
                var keys = manifestEntries.Skip(first).Take(last - first + 1).Select(e => e.Key).ToHashSet();
                seedsList.AddRange(candidates.Where(c => keys.Contains(c.Entry)));
            }
        }
        var seeds = seedsList.DistinctBy(c => c.Reference).ToArray();
        var ordered = seeds.Concat(seeds.SelectMany(seed => candidates.Where(c => c.Entry == seed.Entry && Math.Abs(c.Start - seed.Start) <= 4000)))
            .Concat(candidates.OrderByDescending(c => c.Score)).DistinctBy(c => c.Reference).ToArray();
        var result = new List<KnowledgeFragment>(); var used = 0;
        var index = ReadIndex(db, binding.Manifest.Version);
        if (index is null && binding.Manifest.Entries.Any(e => e.QualityFlags.Contains("fetchOnDemand")))
        {
            var chunks = candidates.Select(c => new CollectionChunk(c.Reference.NodeId, c.Reference.VersionId, c.Entry, c.Reference.ContentHash, c.Start, c.Start + c.Text.Length, c.Text, c.Reference)).ToArray();
            index = BuildIndex(new(binding.Manifest.CollectionId, "site", binding.Manifest.DirectoryHash, DateTimeOffset.UnixEpoch, "site", binding.Manifest.Version, 0, 0), chunks);
        }
        if (binding.Structure is not null && Json(binding.Structure) != Json(index)) throw new InvalidDataException("Frozen semantic index changed");
        if (index is not null)
        {
            var byId = index.Units.ToDictionary(u => u.Id); var children = index.Units.Where(u => u.ParentUnitId is not null).ToLookup(u => u.ParentUnitId!);
            var byReference = index.Units.SelectMany(u => u.References.Select(r => (Reference: r, Unit: u))).ToLookup(p => p.Reference, p => p.Unit);
            var selected = new HashSet<SourceRef>();
            foreach (var seed in seeds)
            {
                var pending = new Queue<SemanticUnit>(byReference[seed.Reference]);
                var visited = new HashSet<string>();
                while (pending.TryDequeue(out var unit))
                {
                    if (!visited.Add(unit.Id)) continue;
                    foreach (var reference in unit.References) selected.Add(reference);
                    if (unit.ParentUnitId is { } parent && byId.TryGetValue(parent, out var ancestor)) pending.Enqueue(ancestor);
                    foreach (var related in children[unit.Id]) pending.Enqueue(related);
                    foreach (var key in unit.PrerequisiteUnitIds) if (byId.TryGetValue(key, out var dependency)) pending.Enqueue(dependency);
                }
                // A confirmed section is a scope constraint, not permission to fill all remaining context.
            }
            // Dependencies can cross physical pages. Read them only from the same frozen version.
            command.CommandText = "SELECT entry_key,start,text,reference FROM chunks WHERE collection_id=$id AND version=$version ORDER BY rowid";
            using var dependencyReader = command.ExecuteReader();
            while (dependencyReader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var reference = JsonSerializer.Deserialize<SourceRef>(dependencyReader.GetString(3), Wire.Options)!;
                if (!selected.Contains(reference)) continue;
                var text = dependencyReader.GetString(2);
                if (Hash(text) != reference.ContentHash || used + text.Length > budget || text.Length > SemanticChunker.Maximum)
                    throw new InvalidDataException("Complete semantic evidence exceeds budget; revise lesson scope");
                used += text.Length; result.Add(new(reference, binding.Manifest.CollectionId, text, false));
            }
            if (!paged && entries.Any(key => !result.Any(r => r.Reference.Locator.Block.StartsWith(key + ":", StringComparison.Ordinal))))
                throw new InvalidDataException("Requested section has no complete evidence");
            return result;
        }
        foreach (var item in paged ? seeds : ordered)
        {
            if (used + item.Text.Length > budget || result.Count >= 90) continue;
            result.Add(new(item.Reference, binding.Manifest.CollectionId, item.Text, false)); used += item.Text.Length;
        }
        if (paged && result.Count != seeds.Length || !paged && entries.Any(key => !result.Any(r => r.Reference.Locator.Block.StartsWith(key + ":", StringComparison.Ordinal))))
            throw new InvalidDataException("Requested sections exceed context budget or have no body");
        return result;
    }
}
