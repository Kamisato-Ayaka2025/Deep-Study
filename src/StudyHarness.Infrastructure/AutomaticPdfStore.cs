using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

internal sealed partial class CollectionStore
{
    private sealed record StagedOcr(RenderedPage Page, OcrPageResult Result);
    public PdfScanState ScanState(Guid id)
    {
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var file = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT json FROM collections WHERE id=$id"; command.Parameters.AddWithValue("$id",id.ToString());
        var collection = JsonSerializer.Deserialize<SourceCollection>(command.ExecuteScalar() as string ?? throw new IOException("教材已删除。"),Wire.Options)!;
        if (collection.SourceKind is not ("pdf" or "presentation")) throw new InvalidDataException("PDF required");
        command.CommandText = "SELECT manifest FROM collection_versions WHERE collection_id=$id AND version=$version"; command.Parameters.AddWithValue("$version",collection.Version);
        var manifest = JsonSerializer.Deserialize<CoverageManifest>((string)command.ExecuteScalar()!,Wire.Options)!;
        var ready = manifest.Entries.Where(e => e.State is "textReady" or "ocrReady").Select(e => int.Parse(e.Locator)).ToArray();
        return new(id,collection.SourceFingerprint,collection.EntryCount,ready,manifest.Entries.Select(e => int.Parse(e.Locator)).Except(ready).Order().ToArray(), collection.SourceKind);
    }
    public void StageOcr(RenderedPage page, OcrPageResult result, CancellationToken ct)
    {
        var state = ScanState(page.CollectionId);
        if (state.SourceFingerprint != page.SourceFingerprint || page.Page < 1 || page.Page > state.TotalPages ||
            string.IsNullOrWhiteSpace(result.Text) || result.Text.Length > 65536 || !result.QualityFlags.Contains("unverified")) throw new InvalidDataException("Staged OCR identity");
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var file = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        ct.ThrowIfCancellationRequested();
        Execute(db,null,"INSERT OR REPLACE INTO staged_ocr VALUES($p0,$p1,$p2)",page.CollectionId.ToString(),page.Page,Json(new StagedOcr(page with { Png = [] },result)));
    }
    public void CommitStaged(Guid id, CancellationToken ct)
    {
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var file = SafeKnowledgeFiles.PinExistingFile(Database);
        List<StagedOcr> staged = [];
        using (var db = Open())
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT json FROM staged_ocr WHERE collection_id=$id ORDER BY page LIMIT 20"; command.Parameters.AddWithValue("$id",id.ToString());
            using var reader = command.ExecuteReader(); while (reader.Read()) staged.Add(JsonSerializer.Deserialize<StagedOcr>(reader.GetString(0),Wire.Options)!);
        }
        if (staged.Count == 0) return;
        SaveOcrBatch(staged.Select(s => (s.Page,s.Result)).ToArray(),ct);
        using var cleanup = Open(); using var tx = cleanup.BeginTransaction();
        foreach (var item in staged) Execute(cleanup,tx,"DELETE FROM staged_ocr WHERE collection_id=$p0 AND page=$p1",id.ToString(),item.Page.Page);
        tx.Commit();
    }
    public void DeletePdf(Guid id, CancellationToken ct)
    {
        _ = ScanState(id);
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var file = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        Execute(db,null,"PRAGMA secure_delete=ON"); using var tx = db.BeginTransaction();
        var key = id.ToString();
        Execute(db,tx,"DELETE FROM chunk_fts WHERE id IN (SELECT id FROM chunks WHERE collection_id=$p0)",key);
        Execute(db,tx,"DELETE FROM summaries WHERE EXISTS (SELECT 1 FROM collection_versions v WHERE v.collection_id=$p0 AND substr(summaries.id,1,length(v.version)+1)=v.version||'/' AND NOT EXISTS (SELECT 1 FROM collection_versions other WHERE other.version=v.version AND other.collection_id<>$p0))",key);
        foreach (var table in new[] { "chunks", "entries", "extraction_jobs", "extraction_pages", "staged_ocr", "structure_indexes", "collection_versions" })
            Execute(db,tx,$"DELETE FROM {table} WHERE collection_id=$p0",key);
        Execute(db,tx,"DELETE FROM collections WHERE id=$p0",key);
        ct.ThrowIfCancellationRequested(); tx.Commit();
    }
}
public sealed partial class KnowledgeStore : IAutomaticPdfKnowledge
{
    public async Task<PdfScanState> ScanStateAsync(Guid library, Guid collection, CancellationToken ct)
    { await mutex.WaitAsync(ct); try { return new CollectionStore(Locate(library).Root,library).ScanState(collection); } finally { mutex.Release(); } }
    public async Task StageOcrAsync(Guid library, RenderedPage page, OcrPageResult result, CancellationToken ct)
    { await mutex.WaitAsync(ct); try { new CollectionStore(Locate(library).Root,library).StageOcr(page,result,ct); } finally { mutex.Release(); } }
    public async Task CommitStagedOcrAsync(Guid library, Guid collection, CancellationToken ct)
    { await mutex.WaitAsync(ct); try { await Task.Run(() => new CollectionStore(Locate(library).Root,library).CommitStaged(collection,ct),ct); } finally { mutex.Release(); } }
    public async Task DeletePdfAsync(Guid library, Guid collection, CancellationToken ct)
    { await mutex.WaitAsync(ct); try { await Task.Run(() => new CollectionStore(Locate(library).Root,library).DeletePdf(collection,ct),ct); } finally { mutex.Release(); } }
}
