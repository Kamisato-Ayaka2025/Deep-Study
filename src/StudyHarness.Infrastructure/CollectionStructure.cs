using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

internal sealed partial class CollectionStore
{
    private static StructureIndex? ReadIndex(SqliteConnection db, string version)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM structure_indexes WHERE version=$version";
        command.Parameters.AddWithValue("$version", version);
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<StructureIndex>(json, Wire.Options) : null;
    }
    private static StructureIndex BuildIndex(SourceCollection collection, IReadOnlyList<CollectionChunk> chunks)
    {
        var nodes = new List<StructureNode>(); var units = new List<SemanticUnit>();
        var stack = new List<StructureNode>(); SemanticUnit? previous = null; int? previousPage = null;
        foreach (var group in chunks.GroupBy(c => c.EntryKey))
        {
            var ordered = group.OrderBy(c => c.Start).ToArray();
            var text = string.Concat(ordered.Select(c => c.Text)); var page = ordered[0].Reference.Locator.Page;
            var spans = ordered.Select(c => new SourceSpan(c.EntryKey, c.Reference.Locator.Page, c.Start, c.End)).ToArray();
            var heading = Regex.Match(text, @"(?m)^((?:\\?#){1,6})\s+([^\r\n]+)");
            var level = heading.Success ? heading.Groups[1].Value.Count(c => c == '#') : 1;
            var title = heading.Success ? heading.Groups[2].Value : page is null ? ordered[0].Reference.Locator.Heading : "物理页 " + page;
            while (stack.Count > 0 && stack[^1].Level >= level) stack.RemoveAt(stack.Count - 1);
            var node = new StructureNode(group.Key, stack.LastOrDefault()?.Id, title ?? "无标题正文", level, heading.Success ? "explicitHeading" : page is null ? "inferred" : "pageFallback", spans);
            nodes.Add(node); if (heading.Success) stack.Add(node);
            string? anchor = null; var chunkCursor = 0;
            foreach (var slice in SemanticChunker.Split(text))
            {
                while (chunkCursor < ordered.Length && ordered[chunkCursor].End <= slice.Start) chunkCursor++;
                var refs = ordered.Skip(chunkCursor).TakeWhile(c => c.Start < slice.End).Select(c => c.Reference).ToArray();
                var id = group.Key + ":" + slice.Start; var parent = slice.Parent is null ? null : group.Key + ":" + slice.Parent[7..];
                if (slice.Kind is "theorem" or "example" or "definition") anchor = id;
                if (slice.Kind is "proof" or "solution" && parent is null) parent = anchor;
                if (parent is null && previous is not null &&
                    (slice.Kind == "proof" && previous.Kind is "theorem" or "proof" || slice.Kind == "solution" && previous.Kind is "example" or "solution") &&
                    (collection.SourceKind == "text" || previous.Spans.Any(s => s.EntryKey == group.Key || page is not null && s.Page is not null && (s.Page == page || s.Page == page - 1)))) parent = previous.Id;
                var kind = slice.Kind;
                if (slice.Start == 0 && !heading.Success && page is not null && previousPage == page - 1 && previous?.Kind is "theorem" or "example" or "proof" or "solution" or "code" or "table" or "formula")
                {
                    parent = previous.Id;
                    if (kind == "paragraph") kind = previous.Kind;
                }
                if (slice.Continuation && parent is null) parent = previous?.Id;
                if (parent == id) parent = null;
                var unit = new SemanticUnit(id, kind, parent, [], [], Hash(text[slice.Start..slice.End]), [new(group.Key, page, slice.Start, slice.End)], refs);
                if (units.Count >= 50000) throw new InvalidDataException("Semantic unit budget exceeded");
                units.Add(unit); previous = unit;
            }
            previousPage = page;
        }
        var children = units.Where(u => u.ParentUnitId is not null).ToLookup(u => u.ParentUnitId!);
        var complete = units.Select(u => u with { ContinuationIds = children[u.Id].Select(c => c.Id).ToArray() }).ToArray();
        return new(collection.SourceFingerprint, SemanticChunker.IndexVersion, SemanticChunker.Version, nodes, complete);
    }
    private static void SaveIndex(SqliteConnection db, SqliteTransaction tx, SourceCollection collection, IReadOnlyList<CollectionChunk> chunks)
    {
        var index = BuildIndex(collection, chunks);
        Execute(db, tx, "INSERT OR IGNORE INTO structure_indexes VALUES($p0,$p1,$p2)", collection.Version, collection.Id.ToString(), Json(index));
    }
    private CollectionSnapshot Reindex(SqliteConnection db, SourceCollection previous, CancellationToken ct)
    {
        if (ReadIndex(db, previous.Version) is { } existingIndex && existingIndex.ChunkerVersion == SemanticChunker.Version && existingIndex.IndexVersion == SemanticChunker.IndexVersion) return Snapshot(db, previous);
        var old = Snapshot(db, previous);
        var version = Hash(previous.Version + "/" + SemanticChunker.Version);
        var collection = previous with { Version = version };
        using var command = db.CreateCommand(); command.CommandText = "SELECT entry_key,start,end,text,reference FROM chunks WHERE collection_id=$id AND version=$version ORDER BY rowid";
        command.Parameters.AddWithValue("$id", previous.Id.ToString()); command.Parameters.AddWithValue("$version", previous.Version);
        var originals = new List<CollectionChunk>();
        using (var reader = command.ExecuteReader()) while (reader.Read())
        {
            var reference = JsonSerializer.Deserialize<SourceRef>(reader.GetString(4), Wire.Options)!;
            var text = reader.GetString(3); if (Hash(text) != reference.ContentHash) throw new InvalidDataException("Reindex source hash mismatch");
            originals.Add(new(reference.NodeId, reference.VersionId, reader.GetString(0), reference.ContentHash, reader.GetInt32(1), reader.GetInt32(2), text, reference));
        }
        var chunks = new List<CollectionChunk>();
        foreach (var group in originals.GroupBy(c => c.EntryKey))
        {
            ct.ThrowIfCancellationRequested(); var text = string.Concat(group.OrderBy(c => c.Start).Select(c => c.Text));
            foreach (var slice in SemanticChunker.Split(text))
            {
                var body = text[slice.Start..slice.End]; var hash = Hash(body); var node = Identity($"{collection.Id}/{group.Key}/{slice.Start}");
                var identity = Identity($"{version}/{group.Key}/{slice.Start}/{hash}");
                var reference = group.First().Reference with { NodeId = node, VersionId = identity, ContentHash = hash,
                    Locator = group.First().Reference.Locator with { Block = $"{group.Key}:{slice.Start}-{slice.End}" } };
                chunks.Add(new(node, identity, group.Key, hash, slice.Start, slice.End, body, reference));
            }
        }
        var manifest = old.Manifest with { Version = version };
        using var tx = db.BeginTransaction();
        Execute(db, tx, "INSERT INTO collection_versions VALUES($p0,$p1,$p2)", collection.Id.ToString(), version, Json(manifest));
        foreach (var entry in manifest.Entries) Execute(db, tx, "INSERT INTO entries VALUES($p0,$p1,$p2,$p3)", collection.Id.ToString(), version, entry.Key, Json(entry));
        foreach (var c in chunks)
        {
            Execute(db, tx, "INSERT INTO chunks VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9)", c.Id.ToString(), c.VersionId.ToString(), collection.Id.ToString(), version, c.EntryKey, c.ContentHash, c.Start, c.End, c.Text, Json(c.Reference));
            Execute(db, tx, "INSERT INTO chunk_fts VALUES($p0,$p1,$p2)", c.Id.ToString(), c.VersionId.ToString(), Tokens(c.Text));
        }
        SaveIndex(db, tx, collection, chunks);
        Execute(db, tx, "UPDATE collections SET json=$p0 WHERE id=$p1", Json(collection), collection.Id.ToString());
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Snapshot(db, collection);
    }
}
