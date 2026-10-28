using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using StudyHarness.Application;
using StudyHarness.Contracts;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace StudyHarness.Infrastructure;

public sealed partial class KnowledgeStore : IKnowledgeStore, IWebKnowledge, IBoundedCollectionKnowledge, IOcrKnowledge, ISiteKnowledge, IImportProgressKnowledge
{
    public async Task<Result<CollectionSnapshot>> ImportCollectionAsync(HostSelection selection, Guid library, CancellationToken ct = default)
    {
        await mutex.WaitAsync(ct);
        try
        {
            if (selection.IsDirectory || Path.GetExtension(selection.Path).ToLowerInvariant() is not (".pdf" or ".ppt" or ".pptx" or ".md" or ".markdown"))
                return Result<CollectionSnapshot>.Fail(ErrorCode.VALIDATION_ERROR, "请选择PDF、PPT、PPTX或Markdown教材文件。");
            var location = Locate(library);
            return Result<CollectionSnapshot>.Success(await Task.Run(() => new CollectionStore(location.Root, library).Import(selection.Path, ct), ct));
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        { return Result<CollectionSnapshot>.Fail(ErrorCode.SOURCE_UNAVAILABLE, "教材无法读取、格式损坏或超过资源限额；未完成导入。"); }
        finally { mutex.Release(); }
    }
    public async Task<IReadOnlyList<CollectionSnapshot>> CollectionsAsync(Guid library, CancellationToken ct = default)
    {
        await mutex.WaitAsync(ct);
        try { return new CollectionStore(Locate(library).Root, library).List(); }
        finally { mutex.Release(); }
    }
    public async Task<IReadOnlyList<KnowledgeFragment>> RetrieveAsync(Guid library, string query, IReadOnlyList<Guid> collections, int maxCharacters, CancellationToken ct = default)
    {
        if (maxCharacters is < 1 or > 131072) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        await mutex.WaitAsync(ct);
        try { return new CollectionStore(Locate(library).Root, library).Search(query, 99, collections, maxCharacters); }
        finally { mutex.Release(); }
    }
    public Task<IReadOnlyList<KnowledgeFragment>> RetrieveFrozenAsync(PlanCollection collection, string query, IReadOnlyList<string> entryKeys, int maxCharacters, CancellationToken ct = default)
        => RetrieveFrozenAsync(collection, query, entryKeys, maxCharacters, new SourceAcquisitionBudget(), ct);
    public async Task<IReadOnlyList<KnowledgeFragment>> RetrieveFrozenAsync(PlanCollection collection, string query, IReadOnlyList<string> entryKeys, int maxCharacters, SourceAcquisitionBudget budget, CancellationToken ct)
    {
        if (maxCharacters is < 1 or > 131072) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        await mutex.WaitAsync(ct);
        try
        {
            var store = new CollectionStore(Locate(collection.LibraryId).Root, collection.LibraryId);
            var navigation = new HashSet<string>();
            foreach (var entry in collection.Manifest.Entries.Where(e => entryKeys.Contains(e.Key) && e.QualityFlags.Contains("fetchOnDemand")))
            {
                if (store.HasSiteBody(collection, entry.Key)) continue;
                if (store.ReadNavigation(collection, entry) is not null) { navigation.Add(entry.Key); continue; }
                if (siteSources is null) throw new IOException("On-demand fetch unavailable");
                budget.ReserveFetch();
                using var fetchDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (budget.Remaining <= TimeSpan.Zero) throw new IOException("Source acquisition deadline");
                fetchDeadline.CancelAfter(budget.Remaining);
                try
                {
                    var page = await siteSources.FetchAsync(entry.Locator, fetchDeadline.Token);
                    if (SiteNavigation.IsReviewedOverview(page))
                    {
                        store.SaveNavigation(collection, entry, page); navigation.Add(entry.Key);
                    }
                    else if (!(page.Assessment ?? TeachingPages.Assess(page, entry.Heading)).UsableForTeaching)
                    {
                        store.SaveSiteFailure(collection, entry.Key, "NOT_TEACHING_BODY");
                        throw new SourcePreparationException($"资料“{entry.Heading}”没有合格教学正文；请调整课程来源，不能将简介或目录当作正文。");
                    }
                    else store.SaveSiteBody(collection, entry, page, fetchDeadline.Token);
                }
                catch (SourcePreparationException) { throw; }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
                {
                    store.SaveSiteFailure(collection, entry.Key, "FETCH_FAILED");
                    throw new SourcePreparationException($"资料“{entry.Heading}”获取失败；请检查网络和DeepSeek设置后重试。已缓存正文保留，未开始文章生成。");
                }
            }
            var bodyKeys = entryKeys.Where(k => !navigation.Contains(k)).ToArray();
            foreach (var key in navigation)
            {
                var parent = collection.Manifest.Entries.Single(e => e.Key == key);
                if (!collection.Manifest.Entries.Any(child => bodyKeys.Contains(child.Key) &&
                    SiteNavigation.IsChild(parent, child) && store.HasSiteBody(collection, child.Key)))
                    throw new SourcePreparationException($"资料“{parent.Heading}”仅为章节导览；本课没有已绑定的子章节正文，请调整课程来源。");
            }
            return store.SearchFrozen(collection, query, bodyKeys, maxCharacters, ct);
        }
        finally { mutex.Release(); }
    }
    private readonly string registryPath;
    private readonly IWebSources? siteSources;
    private readonly SemaphoreSlim mutex = new(1);
    public KnowledgeStore(string dataRoot, IWebSources? siteSources = null)
    {
        this.siteSources = siteSources;
        Directory.CreateDirectory(dataRoot);
        registryPath = Path.Combine(dataRoot, "libraries.db");
        using var db = Open(registryPath, true);
        Initialize(db);
    }
    private static SqliteConnection Open(string path, bool create = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite }.ToString());
        db.Open();
        return db;
    }
    private static void Initialize(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        var version = Convert.ToInt32(cmd.ExecuteScalar());
        if (version > 2) throw new InvalidDataException("Unsupported knowledge version");
        if (version == 0)
        {
            cmd.CommandText = "CREATE TABLE state(id INTEGER PRIMARY KEY CHECK(id=1), json TEXT NOT NULL); PRAGMA user_version=1;";
            cmd.ExecuteNonQuery();
        }
    }
    private static T Read<T>(SqliteConnection db, SqliteTransaction? tx = null) where T : new()
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "SELECT json FROM state WHERE id=1";
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<T>(json, Wire.Options)! : new();
    }
    private static void Write<T>(SqliteConnection db, SqliteTransaction tx, T value)
    {
        using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO state VALUES(1,$json) ON CONFLICT(id) DO UPDATE SET json=excluded.json";
        cmd.Parameters.AddWithValue("$json", JsonSerializer.Serialize(value, Wire.Options)); cmd.ExecuteNonQuery();
    }
    public async Task<LibraryDto?> CurrentAsync(CancellationToken ct = default)
    {
        await mutex.WaitAsync(ct);
        try { using var db = Open(registryPath); var r = Read<Registry>(db); return r.Libraries.FirstOrDefault(l => l.Id == r.Current) is { } l ? l with { Revision = r.Revision } : null; }
        finally { mutex.Release(); }
    }
    public async Task<Result<LibraryDto>> SwitchAsync(Command<HostSelection> command, CancellationToken ct = default)
    {
        await mutex.WaitAsync(ct);
        try
        {
            if (!Valid(command) || !command.Payload.IsDirectory) return Result<LibraryDto>.Fail(ErrorCode.VALIDATION_ERROR, "请选择资料库文件夹。");
            using var pin = SafeKnowledgeFiles.PinDirectory(command.Payload.Path);
            using var db = Open(registryPath); using var tx = db.BeginTransaction(); var registry = Read<Registry>(db, tx);
            var signature = Hash(JsonSerializer.Serialize(command, Wire.Options));
            if (registry.Requests.TryGetValue(command.RequestId, out var prior))
                return prior.Hash == signature ? JsonSerializer.Deserialize<Result<LibraryDto>>(prior.Json, Wire.Options)! : Result<LibraryDto>.Fail(ErrorCode.IDEMPOTENCY_CONFLICT, "请求标识冲突。");
            if (command.ExpectedRevision != registry.Revision) return Result<LibraryDto>.Fail(ErrorCode.REVISION_CONFLICT, "资料库已切换，请刷新。");
            var existing = registry.Libraries.FirstOrDefault(l => l.Root.Equals(command.Payload.Path, StringComparison.OrdinalIgnoreCase));
            var path = Path.Combine(command.Payload.Path, "knowledge.db");
            using var filePin = File.Exists(path) ? SafeKnowledgeFiles.PinExistingFile(path) : null;
            using var library = Open(path, existing is null); Initialize(library);
            using var libTx = library.BeginTransaction(); var state = Read<LibraryState>(library, libTx);
            if (existing is not null && state.Id != existing.Id) throw new InvalidDataException("Library identity changed");
            Write(library, libTx, state); libTx.Commit();
            var value = new LibraryDto(state.Id, command.Payload.Path, ++registry.Revision);
            if (existing is null)
            {
                if (registry.Libraries.Any(l => l.Id == state.Id)) throw new InvalidDataException("Library identity is registered elsewhere");
                registry.Libraries.Add(value);
            }
            registry.Current = value.Id;
            var result = Result<LibraryDto>.Success(value);
            registry.Requests.Add(command.RequestId, new(signature, JsonSerializer.Serialize(result, Wire.Options)));
            Write(db, tx, registry); ct.ThrowIfCancellationRequested(); tx.Commit(); return result;
        }
        catch (Exception e) when (IsIo(e)) { return Result<LibraryDto>.Fail(ErrorCode.IO_FAILURE, "资料库不可写、路径不安全或版本不兼容；原库未迁移。"); }
        finally { mutex.Release(); }
    }
    private LibraryDto Locate(Guid id)
    {
        using var db = Open(registryPath);
        return Read<Registry>(db).Libraries.SingleOrDefault(l => l.Id == id) ?? throw new IOException("Library not registered");
    }
    public async Task<IReadOnlyList<SourceRef>> SaveWebAsync(Guid libraryId, IReadOnlyList<WebPage> pages, string query, CancellationToken ct)
    {
        if (pages.Count is < 1 or > 3) throw new InvalidOperationException("网页数量超出限制。");
        await mutex.WaitAsync(ct);
        try
        {
            var library = Locate(libraryId);
            using var pin = SafeKnowledgeFiles.PinDirectory(library.Root);
            using var filePin = SafeKnowledgeFiles.PinExistingFile(Path.Combine(library.Root, "knowledge.db"));
            using var db = Open(Path.Combine(library.Root, "knowledge.db")); Initialize(db);
            using var tx = db.BeginTransaction(); var state = Read<LibraryState>(db, tx);
            if (state.Id != libraryId) throw new InvalidDataException("Library identity mismatch");
            var refs = new List<SourceRef>();
            foreach (var page in pages)
            {
                var url = WebAddress.Parse(page.Url).AbsoluteUri;
                if (string.IsNullOrWhiteSpace(page.Content) || page.Content.Length > 60000) throw new InvalidOperationException("网页正文无效。");
                var blocks = new List<Block>();
                foreach (var slice in SemanticChunker.Split(page.Content))
                    blocks.Add(new($"web-{SemanticChunker.Version}:{slice.Start}-{slice.End}", null, page.Title, page.Content[slice.Start..slice.End]));
                Merge(state, url, blocks, $"{page.Title} · {url} · {page.RetrievedAt:O}");
                var source = state.Sources.Single(s => s.Path.Equals(url, StringComparison.Ordinal));
                source.WebSnapshots.Add(new(url, null, page.Title, page.RetrievedAt, Hash(page.Content), query, page.Assessment, page.OriginalContent, TrustedSources.Version));
                refs.AddRange(source.Nodes.Where(n => !n.Missing).Select(n =>
                {
                    var v = n.Versions[^1]; return new SourceRef(libraryId, n.Id, v.Id, v.Hash, v.Locator);
                }));
            }
            Write(db, tx, state);
            using var migration = db.CreateCommand(); migration.Transaction = tx;
            migration.CommandText = "PRAGMA user_version=2"; migration.ExecuteNonQuery();
            ct.ThrowIfCancellationRequested(); tx.Commit(); return refs;
        }
        finally { mutex.Release(); }
    }
    public Task<Result<ImportReport>> ImportAsync(Command<HostSelection> command, Guid libraryId, CancellationToken ct = default)
        => Task.Run(() => ImportCoreAsync(command, libraryId, ct), ct);
    public Task<Result<ImportReport>> ImportWithProgressAsync(Command<HostSelection> command, Guid libraryId, IProgress<ImportProgress> progress, CancellationToken ct = default)
        => Task.Run(() => ImportCoreAsync(command, libraryId, ct, progress), ct);
    private async Task<Result<ImportReport>> ImportCoreAsync(Command<HostSelection> command, Guid libraryId, CancellationToken ct, IProgress<ImportProgress>? progress = null)
    {
        await mutex.WaitAsync(ct);
        try
        {
            if (!Valid(command)) return Result<ImportReport>.Fail(ErrorCode.VALIDATION_ERROR, "无效导入请求。");
            var library = Locate(libraryId);
            using var libraryPin = SafeKnowledgeFiles.PinDirectory(library.Root);
            using var filePin = SafeKnowledgeFiles.PinExistingFile(Path.Combine(library.Root, "knowledge.db"));
            using var db = Open(Path.Combine(library.Root, "knowledge.db")); Initialize(db);
            using var tx = db.BeginTransaction(); var state = Read<LibraryState>(db, tx);
            if (state.Id != libraryId) throw new InvalidDataException("Library identity mismatch");
            var signature = Hash(JsonSerializer.Serialize(command, Wire.Options));
            if (state.Requests.TryGetValue(command.RequestId, out var previous))
                return previous.Hash == signature ? Result<ImportReport>.Success(JsonSerializer.Deserialize<ImportReport>(previous.Json, Wire.Options)!) : Result<ImportReport>.Fail(ErrorCode.IDEMPOTENCY_CONFLICT, "请求标识冲突。");
            var reports = new List<ImportFileResult>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var imported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var collectionReferences = new List<SourceRef>();
            var selection = command.Payload;
            // Pin the selected root throughout enumeration; descendants are pinned
            // before enumeration/read and never follow reparse points.
            using var selectionPin = SafeKnowledgeFiles.PinDirectory(selection.IsDirectory ? selection.Path : Path.GetDirectoryName(selection.Path)!);
            var files = selection.IsDirectory ? Enumerate(selection.Path, reports, ct).ToArray() : [selection.Path];
            foreach (var path in files)
            {
                ct.ThrowIfCancellationRequested(); seen.Add(path);
                try
                {
                    var extension = Path.GetExtension(path).ToLowerInvariant();
                    if (selection.IsPlanImport || extension is ".ppt" or ".pptx")
                    {
                        if (extension is not (".pdf" or ".ppt" or ".pptx" or ".md" or ".markdown" or ".c" or ".cpp")) { reports.Add(new(Path.GetFileName(path), false, 0, 0, 0, "已跳过：仅支持PDF/PPT/PPTX/Markdown/.c/.cpp")); continue; }
                        if (extension is ".pdf" or ".ppt" or ".pptx" or ".md" or ".markdown")
                        {
                            var collection = new CollectionStore(library.Root, libraryId).Import(path, ct, progress);
                            collectionReferences.AddRange(collection.References.Take(1));
                            reports.Add(new(Path.GetFileName(path), true, collection.Collection.ReadyCount, 0, 0,
                                collection.Collection.ReadyCount < collection.Collection.EntryCount ? "部分页待识别，请在扫描页识别中处理" : null));
                            continue;
                        }
                    }
                    if (extension is not ".md" and not ".markdown" and not ".pdf" && !(selection.IsProject && extension is ".c" or ".h" or ".cpp" or ".hpp")) { reports.Add(new(Path.GetFileName(path), false, 0, 0, 0, "不支持的格式")); continue; }
                    var bytes = SafeKnowledgeFiles.ReadBytes(path);
                    var blocks = Extract(bytes, extension, ct);
                    if (blocks.Count == 0) { reports.Add(new(Path.GetFileName(path), false, 0, 0, 0, "没有可提取文字；扫描 PDF 请提供文本资料（不含 OCR）")); continue; }
                    var label = selection.IsProject ? Path.GetFileName(selection.Path) + "/" + Path.GetRelativePath(selection.Path, path).Replace('\\', '/') : Path.GetFileName(path);
                    reports.Add(Merge(state, path, blocks, label)); imported.Add(path);
                }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
                { reports.Add(new(Path.GetFileName(path), false, 0, 0, 0, "文件无法读取、格式损坏或超出导入限制")); }
            }
            // Only a complete successful enumeration can establish source absence.
            if (!selection.IsPlanImport && selection.IsDirectory && !reports.Any(r => r.Error == "文件夹无法枚举或为链接"))
                foreach (var source in state.Sources.Where(s => s.Path.StartsWith(selection.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && !seen.Contains(s.Path)))
                    foreach (var node in source.Nodes) node.Missing = true;
            var references = state.Sources.Where(s => imported.Contains(s.Path)).SelectMany(s => s.Nodes.Where(n => !n.Missing).Select(n =>
            {
                var v = n.Versions[^1]; return new SourceRef(libraryId, n.Id, v.Id, v.Hash, v.Locator);
            })).Concat(collectionReferences).ToArray();
            var report = new ImportReport(Guid.NewGuid(), reports, References: references);
            state.Requests.Add(command.RequestId, new(signature, JsonSerializer.Serialize(report, Wire.Options)));
            Write(db, tx, state); ct.ThrowIfCancellationRequested(); tx.Commit(); return Result<ImportReport>.Success(report);
        }
        catch (Exception e) when (IsIo(e)) { return Result<ImportReport>.Fail(ErrorCode.SOURCE_UNAVAILABLE, "来源或资料库不可访问；未提交导入。"); }
        finally { mutex.Release(); }
    }
    private static IEnumerable<string> Enumerate(string root, List<ImportFileResult> reports, CancellationToken ct)
    {
        var queue = new Queue<string>(); queue.Enqueue(root); var count = 0;
        while (queue.TryDequeue(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            string[] entries;
            try { using var pin = SafeKnowledgeFiles.PinDirectory(directory); entries = Directory.GetFileSystemEntries(directory); }
            catch (Exception e) when (IsIo(e)) { reports.Add(new(Path.GetFileName(directory), false, 0, 0, 0, "文件夹无法枚举或为链接")); continue; }
            foreach (var entry in entries.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (++count > 10000) throw new IOException("Too many source entries");
                var attrs = File.GetAttributes(entry);
                if ((attrs & FileAttributes.ReparsePoint) != 0) { reports.Add(new(Path.GetFileName(entry), false, 0, 0, 0, "文件夹无法枚举或为链接")); continue; }
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if (!new[] { ".git", ".tools", ".vs", "bin", "obj", "node_modules" }.Contains(Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase)) queue.Enqueue(entry);
                }
                else yield return entry;
            }
        }
    }
    private static List<Block> Extract(byte[] bytes, string extension, CancellationToken ct)
    {
        var result = new List<Block>();
        if (extension is ".c" or ".h" or ".cpp" or ".hpp")
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n");
            if (text.Contains('\0')) throw new InvalidDataException("Binary source");
            var lines = text.Split('\n');
            for (var start = 0; start < lines.Length; start += 80)
            {
                ct.ThrowIfCancellationRequested();
                var end = Math.Min(start + 80, lines.Length);
                var body = string.Join("\n", lines[start..end]);
                if (body.Length > 16000) throw new InvalidDataException("Code block too long");
                if (!string.IsNullOrWhiteSpace(body)) result.Add(new($"lines:{start + 1}-{end}", null, $"代码行 {start + 1}-{end}", body));
            }
        }
        else if (extension == ".pdf")
        {
            using var pdf = PdfDocument.Open(bytes);
            if (pdf.NumberOfPages > 1000) throw new InvalidDataException("Too many pages");
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var text = ContentOrderTextExtractor.GetText(page).Trim();
                if (text.Length > 0) result.Add(new($"page:{page.Number}", page.Number, null, text));
            }
        }
        else
        {
            var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n", "\n");
            var headings = new List<string>(); var buffer = new StringBuilder(); bool fenced = false;
            void Flush()
            {
                var body = buffer.ToString().Trim(); buffer.Clear();
                if (body.Length > 0) { var heading = string.Join(" / ", headings); result.Add(new(heading, null, heading, body)); }
            }
            foreach (var line in text.Split('\n'))
            {
                ct.ThrowIfCancellationRequested();
                if (line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~")) fenced = !fenced;
                var match = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
                if (!fenced && match.Success)
                {
                    Flush(); var level = match.Groups[1].Length;
                    while (headings.Count >= level) headings.RemoveAt(headings.Count - 1);
                    while (headings.Count < level - 1) headings.Add("");
                    headings.Add(match.Groups[2].Value.Trim());
                }
                else if (!fenced && string.IsNullOrWhiteSpace(line)) Flush();
                else buffer.AppendLine(line);
            }
            Flush();
        }
        if (result.Count > 10000 || result.Sum(b => (long)b.Text.Length) > 4 * 1024 * 1024) throw new InvalidDataException("Extraction limit");
        return result;
    }
    private static ImportFileResult Merge(LibraryState state, string path, List<Block> blocks, string label)
    {
        var comparison = path.StartsWith("https://", StringComparison.Ordinal) ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var source = state.Sources.FirstOrDefault(s => s.Path.Equals(path, comparison));
        if (source is null) { source = new() { Path = path }; state.Sources.Add(source); }
        var available = source.Nodes.ToList(); var matched = new Dictionary<int, Node>();
        // Match unchanged blocks before considering positions, so insertion and
        // reordering cannot turn an old citation into a different paragraph.
        for (var i = 0; i < blocks.Count; i++)
        {
            var hash = Hash(blocks[i].Text);
            var exact = available.FirstOrDefault(n => n.Versions[^1].Hash == hash && n.Key == blocks[i].Key)
                ?? available.FirstOrDefault(n => n.Versions[^1].Hash == hash);
            if (exact is not null) { matched[i] = exact; available.Remove(exact); }
        }
        int added = 0, updated = 0, unchanged = 0;
        var ordered = new List<Node>();
        foreach (var node in source.Nodes) node.Missing = true;
        for (var i = 0; i < blocks.Count; i++)
        {
            var b = blocks[i]; var hash = Hash(b.Text);
            if (!matched.TryGetValue(i, out var node))
            {
                var candidates = available.Where(n => n.Key == b.Key).ToArray();
                var unmatchedInSection = blocks.Where((x, j) => x.Key == b.Key && !matched.ContainsKey(j)).Count();
                if (candidates.Length == 1 && unmatchedInSection == 1) { node = candidates[0]; available.Remove(node); updated++; }
                else { node = new(); source.Nodes.Add(node); added++; }
            }
            else unchanged++;
            node.Key = b.Key; node.Missing = false;
            ordered.Add(node);
            if (node.Versions.Count == 0 || node.Versions[^1].Hash != hash)
            {
                state.Blobs.TryAdd(hash, b.Text);
                node.Versions.Add(new(Guid.NewGuid(), hash, new(label, b.Page, b.Heading, node.Id.ToString())));
            }
        }
        // Identity matching preserves old citations, but must not preserve the old
        // presentation order when an index rebuild inserts/replaces semantic units.
        source.Nodes = ordered.Concat(source.Nodes.Where(n => n.Missing)).ToList();
        return new(Path.GetFileName(path), true, added, updated, unchanged, null);
    }
    public async Task<KnowledgeSearch> SearchAsync(Guid libraryId, string topic, IReadOnlyCollection<Guid> preferredSources, int limit = 20, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        await mutex.WaitAsync(ct);
        try
        {
            var library = Locate(libraryId); using var pin = SafeKnowledgeFiles.PinDirectory(library.Root);
            using var filePin = SafeKnowledgeFiles.PinExistingFile(Path.Combine(library.Root, "knowledge.db"));
            using var db = Open(Path.Combine(library.Root, "knowledge.db")); Initialize(db); var state = Read<LibraryState>(db);
            if (state.Id != libraryId) throw new InvalidDataException("Library identity mismatch");
            var fragments = state.Sources.SelectMany(s => s.Nodes.Select(n =>
            {
                var v = n.Versions[^1]; return new KnowledgeFragment(new(libraryId, n.Id, v.Id, v.Hash, v.Locator), s.Id, state.Blobs[v.Hash], n.Missing);
            })).Where(f => string.IsNullOrWhiteSpace(topic) || f.Text.Contains(topic, StringComparison.OrdinalIgnoreCase) || (f.Reference.Locator.Heading?.Contains(topic, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(f => preferredSources.Contains(f.SourceId)).ThenBy(f => f.Reference.Locator.SourceLabel).Take(limit).ToArray();
            fragments = fragments.Concat(new CollectionStore(library.Root, libraryId).Search(topic, limit)).Take(limit).ToArray();
            return new(fragments, fragments.Length == 0, fragments.Length == 0 ? "本地资料不足，请补充 PDF 或 Markdown；未进行联网搜索。" : "本地匹配结果；是否足以支持课程须按目标核对。");
        }
        catch (Exception e) when (IsIo(e)) { return new([], true, "资料库不可访问或版本不兼容，请检查原位置。"); }
        finally { mutex.Release(); }
    }
    public async Task<ResolvedSource> ResolveAsync(SourceRef reference, CancellationToken ct = default)
    {
        await mutex.WaitAsync(ct);
        try
        {
            var library = Locate(reference.LibraryId); using var pin = SafeKnowledgeFiles.PinDirectory(library.Root);
            using var filePin = SafeKnowledgeFiles.PinExistingFile(Path.Combine(library.Root, "knowledge.db"));
            using var db = Open(Path.Combine(library.Root, "knowledge.db")); Initialize(db); var state = Read<LibraryState>(db);
            if (state.Id != reference.LibraryId || reference.SchemaVersion != 1) throw new InvalidDataException("Identity mismatch");
            var node = state.Sources.SelectMany(s => s.Nodes).SingleOrDefault(n => n.Id == reference.NodeId);
            if (node is null && new CollectionStore(library.Root, library.Id).Resolve(reference) is { } collectionSource) return collectionSource;
            var version = node?.Versions.SingleOrDefault(v => v.Id == reference.VersionId && v.Hash == reference.ContentHash && v.Locator == reference.Locator);
            if (version is null || !state.Blobs.TryGetValue(version.Hash, out var text) || Hash(text) != reference.ContentHash) throw new InvalidDataException("Citation mismatch");
            return new(reference, text, null);
        }
        catch (Exception e) when (IsIo(e)) { return new(reference, null, "原资料库离线、引用不存在或内容校验失败；保留原引用，不替换内容。"); }
        finally { mutex.Release(); }
    }
    private static bool Valid(Command<HostSelection> c) => c.SchemaVersion == 1 && c.RequestId != Guid.Empty && c.ExpectedRevision >= 0;
    private static bool IsIo(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or System.ComponentModel.Win32Exception or JsonException;
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private sealed record Block(string Key, int? Page, string? Heading, string Text);
    public sealed record SavedRequest(string Hash, string Json);
    public sealed class Registry { public Guid? Current { get; set; } public long Revision { get; set; } public List<LibraryDto> Libraries { get; set; } = []; public Dictionary<Guid, SavedRequest> Requests { get; set; } = []; }
    public sealed class LibraryState { public Guid Id { get; set; } = Guid.NewGuid(); public List<Source> Sources { get; set; } = []; public Dictionary<string, string> Blobs { get; set; } = []; public Dictionary<Guid, SavedRequest> Requests { get; set; } = []; }
    public sealed record WebSnapshot(string OriginalUrl, string? FinalUrl, string Title, DateTimeOffset RetrievedAt, string ContentHash, string Query, PageAssessment? Assessment = null, string? OriginalContent = null, string? RegistryVersion = null);
    public sealed class Source { public Guid Id { get; set; } = Guid.NewGuid(); public string Path { get; set; } = ""; public List<Node> Nodes { get; set; } = []; public List<WebSnapshot> WebSnapshots { get; set; } = []; }
    public sealed class Node { public Guid Id { get; set; } = Guid.NewGuid(); public string Key { get; set; } = ""; public bool Missing { get; set; } public List<Version> Versions { get; set; } = []; }
    public sealed record Version(Guid Id, string Hash, SourceLocator Locator);
}
