using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

internal sealed partial class CollectionStore
{
    public void RecordOcrAttempt(RenderedPage page, string model, int attempt, DateTimeOffset started, string outcome, TokenUsage? usage)
    {
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        Execute(db, null, "INSERT INTO extraction_jobs VALUES($p0,$p1,$p2)", Guid.NewGuid().ToString(), page.CollectionId.ToString(),
            Json(new { page.SourceFingerprint, page.Page, page.ImageHash, page.RenderVersion, model, promptVersion = OcrService.PromptVersion, attempt, started, ended = DateTimeOffset.UtcNow, outcome, usage }));
    }
    private static string OcrKey(RenderedPage page, string model, string promptVersion) => Hash($"{page.SourceFingerprint}/{page.Page}/{page.RenderVersion}/{page.ImageHash}/{model}/{promptVersion}");
    public async Task<IReadOnlyList<RenderedPage>> RenderAsync(Guid collection, IReadOnlyList<int> pages, CancellationToken ct)
    {
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT path,json FROM collections WHERE id=$id"; command.Parameters.AddWithValue("$id", collection.ToString());
        string path; SourceCollection item;
        using (var reader = command.ExecuteReader())
        { if (!reader.Read()) throw new InvalidDataException("Unknown collection"); path = reader.GetString(0); item = JsonSerializer.Deserialize<SourceCollection>(reader.GetString(1), Wire.Options)!; }
        if (item.SourceKind != "pdf") throw new InvalidDataException("PDF required");
        var snapshot = Snapshot(db, item);
        var needed = pages.Where(p => snapshot.Manifest.Entries.Any(e => e.Key == "page-" + p && e.State != "textReady")).ToArray();
        return await WindowsPdfRenderer.RenderAsync(path, collection, item.SourceFingerprint, needed, ct);
    }
    public OcrPageResult? Cached(RenderedPage page, string model, string promptVersion)
    {
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM extraction_pages WHERE cache_key=$key"; command.Parameters.AddWithValue("$key", OcrKey(page, model, promptVersion));
        return command.ExecuteScalar() is string json ? JsonSerializer.Deserialize<OcrPageResult>(json, Wire.Options) : null;
    }
    public void SaveOcr(RenderedPage page, OcrPageResult result, CancellationToken ct)
        => SaveOcrBatch([(page, result)], ct);
    private void SaveOcrBatch(IReadOnlyList<(RenderedPage Page, OcrPageResult Result)> batch, CancellationToken ct)
    {
        if (batch.Count is < 1 or > 20 || batch.Any(p => string.IsNullOrWhiteSpace(p.Result.Text) || p.Result.Text.Length > 65536 || !p.Result.QualityFlags.Contains("unverified") || !p.Result.QualityFlags.Contains("visualReviewRequired")))
            throw new InvalidDataException("OCR quality contract");
        var page = batch[0].Page;
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        if (new FileInfo(Database).Length > 2L * 1024 * 1024 * 1024 - 128L * 1024 * 1024) throw new IOException("Collection cache budget");
        using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT json FROM collections WHERE id=$id"; command.Parameters.AddWithValue("$id", page.CollectionId.ToString());
        var collection = JsonSerializer.Deserialize<SourceCollection>((string)command.ExecuteScalar()!, Wire.Options)!;
        if (batch.Any(p => collection.SourceFingerprint != p.Page.SourceFingerprint || p.Page.CollectionId != collection.Id)) throw new InvalidDataException("Collection changed during OCR");
        command.CommandText = "SELECT manifest FROM collection_versions WHERE collection_id=$id AND version=$version"; command.Parameters.AddWithValue("$version", collection.Version);
        var original = JsonSerializer.Deserialize<CoverageManifest>((string)command.ExecuteScalar()!, Wire.Options)!;
        var active = new List<(RenderedPage Page, OcrPageResult Result)>();
        foreach (var item in batch)
        {
            if (!original.Entries.Any(e => e.Key == "page-" + item.Page.Page)) throw new InvalidDataException("Unknown page");
            using var cached = db.CreateCommand(); cached.Transaction = tx;
            cached.CommandText = "SELECT count(*) FROM extraction_pages WHERE cache_key=$key";
            cached.Parameters.AddWithValue("$key", OcrKey(item.Page, item.Result.Model, item.Result.PromptVersion));
            if ((long)cached.ExecuteScalar()! == 0) active.Add(item);
        }
        if (active.Count == 0) { tx.Commit(); return; }
        var byKey = active.ToDictionary(p => "page-" + p.Page.Page);
        var version = Hash(collection.Version + string.Join("/", active.Select(p => OcrKey(p.Page,p.Result.Model,p.Result.PromptVersion) + Hash(p.Result.Text))));
        var entries = original.Entries.Select(e => byKey.TryGetValue(e.Key, out var item) ? e with { State = "ocrReady", ContentHash = Hash(item.Result.Text), Failure = null, QualityFlags = item.Result.QualityFlags } : e).ToArray();
        var manifest = original with { Version = version, Entries = entries };
        var chunks = new List<(string Entry, int Start, int End, string Text, SourceRef Reference)>();
        command.CommandText = "SELECT entry_key,start,end,text,reference FROM chunks WHERE collection_id=$id AND version=$version";
        using (var reader = command.ExecuteReader()) while (reader.Read()) if (!byKey.ContainsKey(reader.GetString(0))) chunks.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), JsonSerializer.Deserialize<SourceRef>(reader.GetString(4), Wire.Options)!));
        foreach (var item in active)
        foreach (var slice in SemanticChunker.Split(item.Result.Text))
        {
            var key = "page-" + item.Page.Page;
            var start = slice.Start; var body = item.Result.Text[slice.Start..slice.End];
            chunks.Add((key, start, start + body.Length, body, new(libraryId, Identity($"{collection.Id}/{key}/{start}"), Guid.Empty, Hash(body),
                new(collection.Label, item.Page.Page, "第" + item.Page.Page + "页（OCR，待复核）", $"{key}:{start}-{start + body.Length}"))));
        }
        Execute(db, tx, "INSERT INTO collection_versions VALUES($p0,$p1,$p2)", collection.Id.ToString(), version, Json(manifest));
        foreach (var entry in entries) Execute(db, tx, "INSERT INTO entries VALUES($p0,$p1,$p2,$p3)", collection.Id.ToString(), version, entry.Key, Json(entry));
        foreach (var chunk in chunks)
        {
            var reference = chunk.Reference with { VersionId = Identity(version + chunk.Reference.NodeId + chunk.Reference.ContentHash) };
            Execute(db, tx, "INSERT INTO chunks VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9)", reference.NodeId.ToString(), reference.VersionId.ToString(), collection.Id.ToString(), version, chunk.Entry, reference.ContentHash, chunk.Start, chunk.End, chunk.Text, Json(reference));
            Execute(db, tx, "INSERT INTO chunk_fts VALUES($p0,$p1,$p2)", reference.NodeId.ToString(), reference.VersionId.ToString(), Tokens(chunk.Text));
        }
        foreach (var group in chunks.GroupBy(c => c.Entry)) SaveSummary(db, tx, version, group.Key, group.Select(c => c.Reference.ContentHash), group.First().Text);
        foreach (var item in active)
        {
            var cacheKey = OcrKey(item.Page, item.Result.Model, item.Result.PromptVersion);
            Execute(db, tx, "INSERT INTO extraction_pages VALUES($p0,$p1,$p2,$p3)", cacheKey, collection.Id.ToString(), item.Page.Page, Json(item.Result));
            Execute(db, tx, "INSERT OR REPLACE INTO extraction_jobs VALUES($p0,$p1,$p2)", cacheKey, collection.Id.ToString(), Json(new { page = item.Page.Page, state = "ocrReady", item.Result.Usage, item.Result.QualityFlags }));
        }
        Execute(db, tx, "UPDATE collections SET json=$p1 WHERE id=$p0", collection.Id.ToString(), Json(collection with { Version = version, ReadyCount = entries.Count(e => e.State is "ocrReady" or "textReady") }));
        SaveIndex(db, tx, collection with { Version = version }, chunks.Select(c =>
        {
            var r = c.Reference with { VersionId = Identity(version + c.Reference.NodeId + c.Reference.ContentHash) };
            return new CollectionChunk(r.NodeId, r.VersionId, c.Entry, r.ContentHash, c.Start, c.End, c.Text, r);
        }).ToArray());
        ct.ThrowIfCancellationRequested(); tx.Commit();
    }
}

public sealed partial class KnowledgeStore
{
    public async Task RecordOcrAttemptAsync(Guid library, RenderedPage page, string model, int attempt, DateTimeOffset started, string outcome, TokenUsage? usage)
    {
        await mutex.WaitAsync();
        try { new CollectionStore(Locate(library).Root, library).RecordOcrAttempt(page, model, attempt, started, outcome, usage); }
        finally { mutex.Release(); }
    }
    public async Task<IReadOnlyList<RenderedPage>> RenderPagesAsync(Guid library, Guid collection, IReadOnlyList<int> pages, CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try { return await new CollectionStore(Locate(library).Root, library).RenderAsync(collection, pages, ct); }
        finally { mutex.Release(); }
    }
    public async Task<OcrPageResult?> CachedOcrAsync(Guid library, RenderedPage page, string model, string promptVersion, CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try { return new CollectionStore(Locate(library).Root, library).Cached(page, model, promptVersion); }
        finally { mutex.Release(); }
    }
    public async Task SaveOcrAsync(Guid library, RenderedPage page, OcrPageResult result, CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try { new CollectionStore(Locate(library).Root, library).SaveOcr(page, result, ct); }
        finally { mutex.Release(); }
    }
}
