using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Infrastructure;

internal sealed partial class CollectionStore
{
    private static bool SameOiPage(string first, string second) => first.TrimEnd('/').Equals(second.TrimEnd('/'), StringComparison.Ordinal);
    public CollectionSnapshot SaveSite(SiteDirectory directory, IReadOnlyList<WebPage> pages, CancellationToken ct)
    {
        if (directory.Entries.Count is < 1 or > 10000 || directory.Entries.Select(e => e.Url).Distinct().Count() != directory.Entries.Count ||
            directory.Entries.Any(e => WebAddress.Parse(e.Url).Host is not ("oi-wiki.org" or "oi.wiki")) ||
            directory.Entries.Select(e => new Uri(e.Url).Host).Distinct().Count() != 1) throw new InvalidDataException("Invalid OI directory");
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = File.Exists(Database) ? SafeKnowledgeFiles.PinExistingFile(Database) : null;
        using var db = Open();
        var origin = "https://" + new Uri(directory.Entries[0].Url).Host + "/";
        var id = Identity(origin);
        var version = Hash(directory.Fingerprint + Json(pages.Select(p => new { p.Url, Hash = Hash(p.Content) }).OrderBy(p => p.Url)));
        var entries = directory.Entries.Select((e, i) => new CollectionEntry("url-" + (i + 1), e.Url, e.Title,
            pages.Any(p => SameOiPage(p.Url, e.Url)) ? "textReady" : "discovered", pages.FirstOrDefault(p => SameOiPage(p.Url, e.Url)) is { } page ? Hash(page.Content) : null, null, ["fetchOnDemand"])).ToArray();
        var manifest = new CoverageManifest(id, version, directory.Fingerprint, true, entries);
        var collection = new SourceCollection(id, "site", directory.Fingerprint, directory.RetrievedAt, "OI Wiki 官方目录", version, entries.Length, entries.Count(e => e.State == "textReady"));
        using (var tx = db.BeginTransaction())
        {
            Execute(db, tx, "INSERT INTO collections VALUES($p0,$p1,$p2) ON CONFLICT(id) DO UPDATE SET json=excluded.json", id.ToString(), origin, Json(collection));
            Execute(db, tx, "INSERT OR IGNORE INTO collection_versions VALUES($p0,$p1,$p2)", id.ToString(), version, Json(manifest));
            foreach (var entry in entries) Execute(db, tx, "INSERT OR IGNORE INTO entries VALUES($p0,$p1,$p2,$p3)", id.ToString(), version, entry.Key, Json(entry));
            Execute(db, tx, "INSERT OR REPLACE INTO extraction_jobs VALUES($p0,$p1,$p2)", "directory-" + version, id.ToString(), Json(new { directory.RetrievedAt, directory.SitemapCount, directory.NavigationCount, state = "directoryReady" }));
            foreach (var page in pages) Execute(db, tx, "INSERT OR REPLACE INTO extraction_jobs VALUES($p0,$p1,$p2)", "fetch-" + version + Hash(page.Url), id.ToString(),
                Json(new { page.Url, page.Title, page.RetrievedAt, matchedDirectory = entries.Any(e => SameOiPage(e.Locator, page.Url)), contentHash = Hash(page.Content), finalUrl = (string?)null }));
            ct.ThrowIfCancellationRequested(); tx.Commit();
        }
        var binding = new PlanCollection(libraryId, manifest, []);
        foreach (var page in pages)
        {
            var entry = entries.FirstOrDefault(e => SameOiPage(e.Locator, page.Url));
            if (entry is not null) SaveSiteBody(binding, entry, page, ct);
        }
        return Snapshot(db, collection);
    }
    public bool HasSiteBody(PlanCollection collection, string key)
    {
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT count(*) FROM chunks WHERE collection_id=$id AND version=$version AND entry_key=$key";
        command.Parameters.AddWithValue("$id", collection.Manifest.CollectionId.ToString()); command.Parameters.AddWithValue("$version", collection.Manifest.Version); command.Parameters.AddWithValue("$key", key);
        return (long)command.ExecuteScalar()! > 0;
    }
    public void SaveSiteBody(PlanCollection collection, CollectionEntry entry, WebPage page, CancellationToken ct)
    {
        if (!SameOiPage(entry.Locator, page.Url) || WebAddress.Parse(page.Url).Host is not ("oi-wiki.org" or "oi.wiki") || string.IsNullOrWhiteSpace(page.Content) || page.Content.Length > 60000)
            throw new InvalidDataException("Unexpected site body");
        var assessment = page.Assessment ?? TeachingPages.Assess(page, entry.Heading);
        if (!assessment.UsableForTeaching) throw new InvalidDataException("Site page is not teaching body");
        page = TeachingPages.Clean(page, entry.Heading);
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        if (new FileInfo(Database).Length > 2L * 1024 * 1024 * 1024 - 128L * 1024 * 1024) throw new IOException("Collection cache budget");
        using var tx = db.BeginTransaction(); using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = "SELECT manifest FROM collection_versions WHERE collection_id=$id AND version=$version";
        command.Parameters.AddWithValue("$id", collection.Manifest.CollectionId.ToString()); command.Parameters.AddWithValue("$version", collection.Manifest.Version);
        if (command.ExecuteScalar() is not string json || json != Json(collection.Manifest) || !collection.Manifest.Entries.Contains(entry)) throw new InvalidDataException("Unbound site body");
        command.CommandText = "SELECT count(*) FROM chunks WHERE collection_id=$id AND version=$version AND entry_key=$key"; command.Parameters.AddWithValue("$key", entry.Key);
        if ((long)command.ExecuteScalar()! > 0) { tx.Commit(); return; } // first successful receipt is immutable within this directory version
        foreach (var slice in SemanticChunker.Split(page.Content))
        {
            var start = slice.Start; var text = page.Content[slice.Start..slice.End]; var hash = Hash(text);
            var node = Identity($"{collection.Manifest.CollectionId}/{entry.Key}/{start}"); var version = Identity($"{collection.Manifest.Version}/{entry.Key}/{start}/{hash}");
            var reference = new SourceRef(libraryId, node, version, hash, new(entry.Locator, null, page.Title, $"{entry.Key}:{start}-{start + text.Length}"));
            Execute(db, tx, "INSERT INTO chunks VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7,$p8,$p9)", node.ToString(), version.ToString(), collection.Manifest.CollectionId.ToString(), collection.Manifest.Version, entry.Key, hash, start, start + text.Length, text, Json(reference));
            Execute(db, tx, "INSERT INTO chunk_fts VALUES($p0,$p1,$p2)", node.ToString(), version.ToString(), Tokens(text + " " + page.Title));
        }
        SaveSummary(db, tx, collection.Manifest.Version, entry.Key, SemanticChunker.Split(page.Content).Select(s => Hash(page.Content[s.Start..s.End])), page.Content);
        Execute(db, tx, "INSERT OR REPLACE INTO extraction_jobs VALUES($p0,$p1,$p2)", collection.Manifest.Version + "/" + entry.Key, collection.Manifest.CollectionId.ToString(), Json(new { state = "textReady", page.Url, page.RetrievedAt, contentHash = Hash(page.Content), finalUrl = (string?)null, page.Assessment, page.OriginalContent }));
        ct.ThrowIfCancellationRequested(); tx.Commit();
    }
    public void SaveSiteFailure(PlanCollection collection, string key, string failure)
    {
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        Execute(db, null, "INSERT OR REPLACE INTO extraction_jobs VALUES($p0,$p1,$p2)", collection.Manifest.Version + "/" + key, collection.Manifest.CollectionId.ToString(), Json(new { state = "failed", failure }));
    }

    public WebPage? ReadNavigation(PlanCollection collection, CollectionEntry entry)
    {
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT json FROM extraction_jobs WHERE id=$id";
        command.Parameters.AddWithValue("$id", "navigation-" + collection.Manifest.Version + "/" + entry.Key);
        if (command.ExecuteScalar() is not string json) return null;
        var page = JsonSerializer.Deserialize<WebPage>(json, Wire.Options);
        if (page is null || !SameOiPage(page.Url, entry.Locator) || !SiteNavigation.IsReviewedOverview(page))
            throw new SourcePreparationException("已缓存的目录导航记录无效；请恢复资料缓存后重试。");
        return page;
    }

    public void SaveNavigation(PlanCollection collection, CollectionEntry entry, WebPage page)
    {
        if (!SameOiPage(page.Url, entry.Locator) || !SiteNavigation.IsReviewedOverview(page))
            throw new InvalidDataException("Not reviewed navigation");
        using var pin = SafeKnowledgeFiles.PinDirectory(root); using var filePin = SafeKnowledgeFiles.PinExistingFile(Database); using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT manifest FROM collection_versions WHERE collection_id=$id AND version=$version";
        command.Parameters.AddWithValue("$id", collection.Manifest.CollectionId.ToString()); command.Parameters.AddWithValue("$version", collection.Manifest.Version);
        if (command.ExecuteScalar() is not string json || json != Json(collection.Manifest) || !collection.Manifest.Entries.Contains(entry))
            throw new InvalidDataException("Unbound navigation receipt");
        // A separate immutable receipt: keep the original failure and never create teaching chunks.
        Execute(db, null, "INSERT OR IGNORE INTO extraction_jobs VALUES($p0,$p1,$p2)",
            "navigation-" + collection.Manifest.Version + "/" + entry.Key, collection.Manifest.CollectionId.ToString(), Json(page));
    }
}

public sealed partial class KnowledgeStore
{
    public async Task<CollectionSnapshot> SaveSiteAsync(Guid library, SiteDirectory directory, IReadOnlyList<WebPage> pages, CancellationToken ct)
    {
        await mutex.WaitAsync(ct);
        try { return new CollectionStore(Locate(library).Root, library).SaveSite(directory, pages, ct); }
        finally { mutex.Release(); }
    }
}
