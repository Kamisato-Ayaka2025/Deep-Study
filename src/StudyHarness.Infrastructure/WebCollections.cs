using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

public sealed partial class KnowledgeStore : IWebPageKnowledge
{
    // Project already-saved web snapshots as collections without rewriting their references or old plans.
    private IReadOnlyList<CollectionSnapshot> WebCollections(Guid library)
    {
        using var db = Open(Path.Combine(Locate(library).Root, "knowledge.db"));
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT json_extract(s.value,'$.id'), json_extract(s.value,'$.path'), json_extract(s.value,'$.nodes'),
                   json_extract(s.value,'$.webSnapshots[#-1].title'), json_extract(s.value,'$.webSnapshots[#-1].contentHash'),
                   json_extract(s.value,'$.webSnapshots[#-1].retrievedAt')
            FROM state,json_each(state.json,'$.sources') s WHERE json_array_length(s.value,'$.webSnapshots')>0
            """;
        var result = new List<CollectionSnapshot>(); using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = Guid.Parse(reader.GetString(0)); var path = reader.GetString(1);
            var nodes = System.Text.Json.JsonSerializer.Deserialize<Node[]>(reader.GetString(2), Wire.Options)!;
            var title = reader.GetString(3);
            var references = nodes.Where(n => !n.Missing && n.Versions.Count > 0).Select(n =>
            { var v = n.Versions[^1]; return new SourceRef(library, n.Id, v.Id, v.Hash, v.Locator); }).ToArray();
            var version = PlanRules.Hash(references);
            var entries = references.GroupBy(r => r.Locator.Block.Split(':')[0]).Select(g =>
                new CollectionEntry(g.Key, path, title, "textReady", PlanRules.Hash(g.ToArray()), null, [])).ToArray();
            result.Add(new(new(id, "web", reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5), System.Globalization.CultureInfo.InvariantCulture),
                title + " · " + new Uri(path).Host, version, entries.Length, entries.Length),
                new(id, version, version, true, entries), references));
        }
        return result;
    }
    public async Task<IReadOnlyList<SourceRef>> WebPageReferencesAsync(SourceRef reference, CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try
        {
            var library = Locate(reference.LibraryId);
            // Dynamic site pages may belong to an older frozen directory, so query by the selected reference.
            var site = new CollectionStore(library.Root, library.Id).WebPageReferences(reference);
            if (site.Count > 0) return site;
            var web = WebCollections(library.Id).FirstOrDefault(c => c.References.Contains(reference));
            if (web is not null) return web.References;
            using var db = Open(Path.Combine(library.Root, "knowledge.db"));
            var state = Read<LibraryState>(db);
            var source = state.Sources.FirstOrDefault(s => s.WebSnapshots.Count > 0 && s.Nodes.Any(n => n.Id == reference.NodeId && n.Versions.Any(v => v.Id == reference.VersionId)));
            if (source is null) return [];
            return source.Nodes.SelectMany(n => n.Versions.Where(v => v.Locator.SourceLabel == reference.Locator.SourceLabel)
                .Select(v => new SourceRef(library.Id, n.Id, v.Id, v.Hash, v.Locator))).Distinct().ToArray();
        }
        finally { mutex.Release(); }
    }
}

internal sealed partial class CollectionStore
{
    public IReadOnlyList<SourceRef> WebPageReferences(SourceRef reference)
    {
        if (!File.Exists(Database)) return [];
        using var db = Open(); using var command = db.CreateCommand();
        command.CommandText = """
            SELECT c.reference FROM chunks c JOIN chunks selected
            ON c.collection_id=selected.collection_id AND c.version=selected.version AND c.entry_key=selected.entry_key
            JOIN collections s ON s.id=c.collection_id
            WHERE selected.id=$id AND selected.version_id=$version AND json_extract(s.json,'$.sourceKind')='site'
            ORDER BY c.start
            """;
        command.Parameters.AddWithValue("$id", reference.NodeId.ToString()); command.Parameters.AddWithValue("$version", reference.VersionId.ToString());
        var result = new List<SourceRef>(); using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(System.Text.Json.JsonSerializer.Deserialize<SourceRef>(reader.GetString(0), Wire.Options)!);
        return result.Contains(reference) ? result : [];
    }
}
