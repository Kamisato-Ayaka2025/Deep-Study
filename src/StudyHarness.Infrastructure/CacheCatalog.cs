using System.Text.Json;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

// Read existing caches only: opening Settings must never import, migrate or call a model.
public sealed class CacheCatalog(string root) : ICacheCatalog
{
    private sealed record Entry(string Name, string Path, string Database, SourceCollection? Collection, IReadOnlyList<SourceRef>? WebReferences = null);
    private static SqliteConnection Open(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); return db;
    }
    public Task<CacheCatalogPage> ReadAsync(int page, CancellationToken ct = default) => Task.Run(() => Read(page, ct), ct);
    private CacheCatalogPage Read(int page, CancellationToken ct)
    {
        var entries = new List<Entry>(); var unavailable = 0;
        var sites = new List<(string Name, int Total, List<Entry> Pages)>();
        var registry = Path.Combine(root, "libraries.db");
        if (!File.Exists(registry)) return new([], 1, 0, null);
        using var registryDb = Open(registry);
        using var registryCommand = registryDb.CreateCommand();
        registryCommand.CommandText = "SELECT json FROM state WHERE id=1";
        var libraries = registryCommand.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<KnowledgeStore.Registry>(json, Wire.Options)!.Libraries : [];
        foreach (var library in libraries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Directory.Exists(library.Root)) throw new IOException("Library unavailable");
                var database = Path.Combine(library.Root, "collections.db");
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (File.Exists(database))
                {
                    using var db = Open(database); using var command = db.CreateCommand();
                    command.CommandText = "SELECT path,json FROM collections";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var collection = JsonSerializer.Deserialize<SourceCollection>(reader.GetString(1), Wire.Options)!;
                        paths.Add(reader.GetString(0));
                        if (collection.SourceKind != "site") entries.Add(new(collection.Label, reader.GetString(0), database, collection));
                        else
                        {
                            using var pagesCommand = db.CreateCommand();
                            pagesCommand.CommandText = "SELECT reference FROM chunks WHERE collection_id=$id AND version=$version ORDER BY rowid";
                            pagesCommand.Parameters.AddWithValue("$id", collection.Id.ToString()); pagesCommand.Parameters.AddWithValue("$version", collection.Version);
                            var references = new List<SourceRef>(); using var pageReader = pagesCommand.ExecuteReader();
                            while (pageReader.Read()) references.Add(JsonSerializer.Deserialize<SourceRef>(pageReader.GetString(0), Wire.Options)!);
                            var pages = references.GroupBy(r => r.Locator.SourceLabel).Select(g => new Entry(
                                (g.First().Locator.Heading ?? g.Key) + " · " + new Uri(g.Key).Host, g.Key, database, null, g.ToArray())).ToList();
                            entries.AddRange(pages); sites.Add((collection.Label, collection.EntryCount, pages));
                        }
                    }
                }
                var legacy = Path.Combine(library.Root, "knowledge.db");
                if (File.Exists(legacy))
                {
                    using var db = Open(legacy); using var command = db.CreateCommand();
                    command.CommandText = "SELECT json_extract(s.value,'$.path'),json_extract(s.value,'$.webSnapshots[#-1].title'),json_extract(s.value,'$.nodes') FROM state,json_each(state.json,'$.sources') s WHERE EXISTS (SELECT 1 FROM json_each(s.value,'$.nodes'))";
                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        var path = reader.GetString(0);
                        if (!paths.Add(path)) continue;
                        if (reader.IsDBNull(1)) entries.Add(new(Path.GetFileName(path), path, legacy, null));
                        else
                        {
                            var nodes = JsonSerializer.Deserialize<KnowledgeStore.Node[]>(reader.GetString(2), Wire.Options)!;
                            var refs = nodes.Where(n => !n.Missing && n.Versions.Count > 0).Select(n =>
                            { var v = n.Versions[^1]; return new SourceRef(library.Id, n.Id, v.Id, v.Hash, v.Locator); }).ToArray();
                            if (refs.Length > 0) entries.Add(new(reader.GetString(1) + " · " + new Uri(path).Host, path, legacy, null, refs));
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
            { unavailable++; }
        }
        var ordered = entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Database, StringComparer.OrdinalIgnoreCase).ToArray();
        page = Math.Clamp(page, 1, Math.Max(1, (ordered.Length + CacheCatalogPage.PageSize - 1) / CacheCatalogPage.PageSize));
        var covered = new HashSet<SourceRef>();
        var webCovered = new HashSet<SourceRef>();
        var cache = Path.Combine(root, "book-knowledge.db");
        if (File.Exists(cache))
        {
            using var db = Open(cache); using var command = db.CreateCommand();
            command.CommandText = """
                SELECT json_extract(json,'$.sources'),json_extract(json,'$.key'),json_extract(json,'$.nonTeachingIds'),
                (SELECT json_group_array(e.value) FROM json_each(batches.json,'$.points') p,json_each(p.value,'$.evidenceIds') e)
                FROM batches
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var sources = JsonSerializer.Deserialize<PlanSource[]>(reader.GetString(0), Wire.Options)!;
                var isWeb = reader.GetString(1).StartsWith("web-", StringComparison.Ordinal);
                var classified = isWeb ? (reader.IsDBNull(2) ? [] : JsonSerializer.Deserialize<string[]>(reader.GetString(2), Wire.Options)!)
                    .Concat(JsonSerializer.Deserialize<string[]>(reader.GetString(3), Wire.Options)!).ToHashSet(StringComparer.Ordinal) : null;
                foreach (var source in sources)
                {
                    if (isWeb && !classified!.Contains(source.Key)) continue;
                    covered.Add(source.Reference);
                    if (isWeb) webCovered.Add(source.Reference);
                }
            }
        }
        var items = new List<CachedMaterial>();
        foreach (var entry in ordered.Skip((page - 1) * CacheCatalogPage.PageSize).Take(CacheCatalogPage.PageSize))
        {
            ct.ThrowIfCancellationRequested();
            var complete = entry.WebReferences is { Count: > 0 } web && web.All(webCovered.Contains);
            if (entry.Collection is { } collection)
            {
                using var db = Open(entry.Database); using var command = db.CreateCommand();
                command.CommandText = "SELECT json FROM entries WHERE collection_id=$id AND version=$version";
                command.Parameters.AddWithValue("$id", collection.Id.ToString()); command.Parameters.AddWithValue("$version", collection.Version);
                var ready = true; var count = 0;
                using (var reader = command.ExecuteReader()) while (reader.Read())
                {
                    var value = JsonSerializer.Deserialize<CollectionEntry>(reader.GetString(0), Wire.Options)!;
                    count++; ready &= value.State is "textReady" or "ocrReady";
                }
                command.CommandText = "SELECT reference FROM chunks WHERE collection_id=$id AND version=$version";
                var references = 0;
                using (var reader = command.ExecuteReader()) while (reader.Read())
                { references++; ready &= covered.Contains(JsonSerializer.Deserialize<SourceRef>(reader.GetString(0), Wire.Options)!); }
                complete = ready && count > 0 && references > 0;
            }
            items.Add(new(entry.Name, entry.Path, complete));
        }
        var summaries = sites.Select(s => $"{s.Name}：目录{s.Total}项，已缓存正文{s.Pages.Count}篇，已整理{s.Pages.Count(p => p.WebReferences!.All(webCovered.Contains))}篇。").ToList();
        if (unavailable > 0) summaries.Add($"{unavailable} 个资料库暂时无法读取，列表可能不完整。");
        return new(items, page, ordered.Length, summaries.Count > 0 ? string.Join(" ", summaries) : null);
    }
}
