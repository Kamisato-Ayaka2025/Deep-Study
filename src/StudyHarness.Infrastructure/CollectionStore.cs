using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using StudyHarness.Contracts;
using StudyHarness.Application;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace StudyHarness.Infrastructure;

// One normalized database per library. Historical versions and original text remain immutable.
internal sealed partial class CollectionStore(string root, Guid libraryId)
{
    private string Database => Path.Combine(root, "collections.db");
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static Guid Identity(string value) => new(SHA256.HashData(Encoding.UTF8.GetBytes(value))[..16]);
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); db.Open();
        try
        {
            using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
            var databaseVersion = Convert.ToInt32(command.ExecuteScalar());
            if (databaseVersion > 2) { db.Dispose(); throw new InvalidDataException("Unsupported collections version"); }
            if (databaseVersion == 1) DatabaseMigrationBackup.Create(db, Database, 1);
            using var migration = db.BeginTransaction(); command.Transaction = migration;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS collections(id TEXT PRIMARY KEY,path TEXT NOT NULL,json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS collection_versions(collection_id TEXT NOT NULL,version TEXT NOT NULL,manifest TEXT NOT NULL,PRIMARY KEY(collection_id,version));
                CREATE TABLE IF NOT EXISTS entries(collection_id TEXT NOT NULL,version TEXT NOT NULL,key TEXT NOT NULL,json TEXT NOT NULL,PRIMARY KEY(collection_id,version,key));
                CREATE TABLE IF NOT EXISTS chunks(id TEXT NOT NULL,version_id TEXT NOT NULL,collection_id TEXT NOT NULL,version TEXT NOT NULL,entry_key TEXT NOT NULL,hash TEXT NOT NULL,start INTEGER NOT NULL,end INTEGER NOT NULL,text TEXT NOT NULL,reference TEXT NOT NULL,PRIMARY KEY(id,version_id));
                CREATE TABLE IF NOT EXISTS summaries(id TEXT PRIMARY KEY,parent_hashes TEXT NOT NULL,text TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS extraction_jobs(id TEXT PRIMARY KEY,collection_id TEXT NOT NULL,json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS extraction_pages(cache_key TEXT PRIMARY KEY,collection_id TEXT NOT NULL,page INTEGER NOT NULL,json TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS staged_ocr(collection_id TEXT NOT NULL,page INTEGER NOT NULL,json TEXT NOT NULL,PRIMARY KEY(collection_id,page));
                CREATE VIRTUAL TABLE IF NOT EXISTS chunk_fts USING fts5(id UNINDEXED,version_id UNINDEXED,tokens);
                CREATE TABLE IF NOT EXISTS structure_indexes(version TEXT PRIMARY KEY,collection_id TEXT NOT NULL,json TEXT NOT NULL);
                PRAGMA user_version=2;
                """; command.ExecuteNonQuery();
            command.CommandText = "SELECT version,collection_id,json FROM structure_indexes LIMIT 0";
            using (command.ExecuteReader()) { }
            migration.Commit(); return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params object[] values)
    {
        using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]); command.ExecuteNonQuery();
    }
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Wire.Options);
    internal static string Tokens(string text)
    {
        var tokens = new List<string>();
        foreach (Match word in Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9_]+|[\p{IsCJKUnifiedIdeographs}]+"))
        {
            if (word.Value[0] <= 127) tokens.Add(word.Value);
            else if (word.Length == 1) tokens.Add(word.Value);
            else for (var i = 0; i < word.Length - 1; i++) tokens.Add(word.Value.Substring(i, 2));
        }
        return string.Join(' ', tokens);
    }
    public CollectionSnapshot Import(string path, CancellationToken ct)
    {
        using var pin = SafeKnowledgeFiles.PinDirectory(root);
        using var dbPin = File.Exists(Database) ? SafeKnowledgeFiles.PinExistingFile(Database) : null;
        using var source = SafeKnowledgeFiles.OpenRead(path, Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase) ? 512L * 1024 * 1024 : 32L * 1024 * 1024);
        if (File.Exists(Database) && new FileInfo(Database).Length > 2L * 1024 * 1024 * 1024 - 128L * 1024 * 1024) throw new IOException("Collection cache budget");
        var fingerprint = Convert.ToHexString(SHA256.HashData(source.Stream)); source.Stream.Position = 0;
        var pdf = Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        var snapshotVersion = Hash(fingerprint + "/" + SemanticChunker.Version);
        var id = Identity(Path.GetFullPath(path).ToUpperInvariant());
        using var db = Open();
        using (var existing = db.CreateCommand())
        {
            existing.CommandText = "SELECT json FROM collections WHERE id=$id"; existing.Parameters.AddWithValue("$id", id.ToString());
            if (existing.ExecuteScalar() is string json && JsonSerializer.Deserialize<SourceCollection>(json, Wire.Options) is { } previous && previous.SourceFingerprint == fingerprint)
                return Reindex(db, previous, ct);
        }
        var entries = new List<CollectionEntry>(); var chunks = new List<CollectionChunk>(); long characters = 0;
        void Add(string key, string locator, string heading, string text, int? page)
        {
            characters += text.Length;
            if (characters > 32L * 1024 * 1024 || entries.Count >= 10000) throw new InvalidDataException("Collection extraction budget");
            entries.Add(new(key, locator, heading, text.Length == 0 ? "discovered" : "textReady", text.Length == 0 ? null : Hash(text), null,
                text.Length == 0 ? ["ocrRequired", "visualReviewRequired"] : []));
            foreach (var slice in SemanticChunker.Split(text))
            {
                var start = slice.Start; var body = text[slice.Start..slice.End]; var hash = Hash(body);
                var node = Identity($"{id}/{key}/{start}"); var version = Identity($"{snapshotVersion}/{key}/{start}/{hash}");
                var reference = new SourceRef(libraryId, node, version, hash, new(Path.GetFileName(path), page, heading, $"{key}:{start}-{start + body.Length}"));
                if (chunks.Count >= 50000) throw new InvalidDataException("Semantic chunk budget exceeded");
                chunks.Add(new(node, version, key, hash, start, start + body.Length, body, reference));
            }
        }
        if (pdf)
        {
            using var document = PdfDocument.Open(source.Stream);
            if (document.NumberOfPages > 2000) throw new InvalidDataException("PDF page budget");
            foreach (var page in document.GetPages()) { ct.ThrowIfCancellationRequested(); Add("page-" + page.Number, page.Number.ToString(), "第" + page.Number + "页", ContentOrderTextExtractor.GetText(page).Trim(), page.Number); }
        }
        else
        {
            using var reader = new StreamReader(source.Stream, new UTF8Encoding(false, true), true, 8192, true);
            var body = new StringBuilder(); var heading = Path.GetFileName(path); var index = 1; string? line;
            void Flush() { if (body.Length > 0) { Add("section-" + index++, heading, heading, body.ToString(), null); body.Clear(); } }
            while ((line = reader.ReadLine()) is not null)
            {
                ct.ThrowIfCancellationRequested();
                if (Regex.IsMatch(line, @"^(?:\\?#){1,6}\s+")) { Flush(); heading = Regex.Replace(line, @"^(?:\\?#){1,6}\s+", ""); }
                body.AppendLine(line);
                if (body.Length + characters > 32L * 1024 * 1024) throw new InvalidDataException("Collection extraction budget");
            }
            Flush();
        }
        var directoryHash = Hash(Json(entries.Select(e => new { e.Key, e.Locator, e.Heading })));
        var manifest = new CoverageManifest(id, snapshotVersion, directoryHash, true, entries);
        var collection = new SourceCollection(id, pdf ? "pdf" : "text", fingerprint, DateTimeOffset.UtcNow, Path.GetFileName(path), snapshotVersion, entries.Count, entries.Count(e => e.State == "textReady"));
        using var tx = db.BeginTransaction();
        Execute(db, tx, "INSERT INTO collections VALUES($p0,$p1,$p2) ON CONFLICT(id) DO UPDATE SET json=excluded.json,path=excluded.path", id.ToString(), Path.GetFullPath(path), Json(collection));
        Execute(db, tx, "INSERT OR IGNORE INTO collection_versions VALUES($p0,$p1,$p2)", id.ToString(), snapshotVersion, Json(manifest));
        foreach (var entry in entries) Execute(db, tx, "INSERT OR IGNORE INTO entries VALUES($p0,$p1,$p2,$p3)", id.ToString(), snapshotVersion, entry.Key, Json(entry));
        foreach (var chunk in chunks)
        {
            Execute(db, tx, "INSERT OR IGNORE INTO chunks VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9)", chunk.Id.ToString(), chunk.VersionId.ToString(), id.ToString(), snapshotVersion, chunk.EntryKey, chunk.ContentHash, chunk.Start, chunk.End, chunk.Text, Json(chunk.Reference));
            Execute(db, tx, "DELETE FROM chunk_fts WHERE id=$p0 AND version_id=$p1", chunk.Id.ToString(), chunk.VersionId.ToString());
            Execute(db, tx, "INSERT INTO chunk_fts VALUES($p0,$p1,$p2)", chunk.Id.ToString(), chunk.VersionId.ToString(), Tokens(chunk.Text + " " + chunk.Reference.Locator.Heading));
        }
        foreach (var group in chunks.GroupBy(c => c.EntryKey)) SaveSummary(db, tx, snapshotVersion, group.Key, group.Select(c => c.ContentHash), group.First().Text);
        SaveIndex(db, tx, collection, chunks);
        ct.ThrowIfCancellationRequested(); tx.Commit(); return Snapshot(db, collection);
    }
    private static void SaveSummary(SqliteConnection db, SqliteTransaction tx, string version, string entry, IEnumerable<string> hashes, string text)
    {
        // Deterministic extractive index preview, never a replacement for the immutable original.
        var excerpt = text[..Math.Min(160, text.Length)];
        Execute(db, tx, "INSERT OR IGNORE INTO summaries VALUES($p0,$p1,$p2)", version + "/" + entry, Json(hashes.ToArray()), excerpt);
    }
    private static CollectionSnapshot Snapshot(SqliteConnection db, SourceCollection collection)
    {
        using var command = db.CreateCommand(); command.CommandText = "SELECT manifest FROM collection_versions WHERE collection_id=$id AND version=$version";
        command.Parameters.AddWithValue("$id", collection.Id.ToString()); command.Parameters.AddWithValue("$version", collection.Version);
        var manifest = JsonSerializer.Deserialize<CoverageManifest>((string)command.ExecuteScalar()!, Wire.Options)!;
        command.CommandText = "SELECT reference FROM chunks WHERE collection_id=$id AND version=$version ORDER BY rowid";
        var refs = new List<SourceRef>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) refs.Add(JsonSerializer.Deserialize<SourceRef>(reader.GetString(0), Wire.Options)!);
        command.CommandText = "SELECT id,parent_hashes,text FROM summaries WHERE id LIKE $prefix"; command.Parameters.AddWithValue("$prefix", collection.Version + "/%");
        var summaries = new List<CollectionSummary>();
        using (var reader = command.ExecuteReader()) while (reader.Read()) summaries.Add(new(reader.GetString(0)[(collection.Version.Length + 1)..], JsonSerializer.Deserialize<string[]>(reader.GetString(1), Wire.Options)!, reader.GetString(2)));
        return new(collection with { ReadyCount = refs.Select(r => r.Locator.Block.Split(':')[0]).Distinct().Count() }, manifest, refs, summaries, ReadIndex(db, collection.Version));
    }
    public IReadOnlyList<CollectionSnapshot> List()
    {
        if (!File.Exists(Database)) return [];
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM collections ORDER BY rowid";
        var items = new List<SourceCollection>(); using (var reader = command.ExecuteReader()) while (reader.Read()) items.Add(JsonSerializer.Deserialize<SourceCollection>(reader.GetString(0), Wire.Options)!);
        return items.Select(c => Snapshot(db, c)).ToArray();
    }
    public ResolvedSource? Resolve(SourceRef reference)
    {
        if (!File.Exists(Database)) return null;
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT text,reference FROM chunks WHERE id=$id AND version_id=$version";
        command.Parameters.AddWithValue("$id", reference.NodeId.ToString()); command.Parameters.AddWithValue("$version", reference.VersionId.ToString());
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var text = reader.GetString(0); var stored = JsonSerializer.Deserialize<SourceRef>(reader.GetString(1), Wire.Options);
        return stored == reference && Hash(text) == reference.ContentHash ? new(reference, text, null) : new(reference, null, "资料引用校验失败。");
    }
    public IReadOnlyList<KnowledgeFragment> Search(string query, int limit, IReadOnlyList<Guid>? selected = null, int maxCharacters = 131072)
    {
        if (!File.Exists(Database)) return [];
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        using var command = db.CreateCommand();
        var terms = Tokens(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().Take(40).ToArray();
        command.CommandText = "SELECT c.collection_id,c.text,c.reference FROM chunks c JOIN collections s ON c.collection_id=s.id AND c.version=json_extract(s.json,'$.version') " +
            (terms.Length == 0 ? "" : "JOIN chunk_fts f ON f.id=c.id AND f.version_id=c.version_id ") +
            "WHERE ($selected='[]' OR c.collection_id IN (SELECT value FROM json_each($selected))) " +
            (terms.Length == 0 ? "ORDER BY c.rowid LIMIT $limit" : "AND chunk_fts MATCH $query ORDER BY rank LIMIT $limit");
        command.Parameters.AddWithValue("$selected", Json(selected ?? []));
        command.Parameters.AddWithValue("$limit", limit);
        if (terms.Length > 0) command.Parameters.AddWithValue("$query", string.Join(" OR ", terms.Select(t => "\"" + t.Replace("\"", "\"\"") + "\"")));
        using var reader = command.ExecuteReader(); var result = new List<KnowledgeFragment>(); var used = 0;
        while (reader.Read())
        {
            var collection = Guid.Parse(reader.GetString(0)); if (selected is { Count: > 0 } && !selected.Contains(collection)) continue;
            var text = reader.GetString(1); if (used + text.Length > maxCharacters) continue; used += text.Length;
            result.Add(new(JsonSerializer.Deserialize<SourceRef>(reader.GetString(2), Wire.Options)!, collection, text, false));
        }
        return result;
    }
}
