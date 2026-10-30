using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Desktop;
public sealed record CollectionChoice(CollectionSnapshot Snapshot)
{ public string Label => $"{Snapshot.Collection.Label} · 正文{Snapshot.Collection.ReadyCount}/{Snapshot.Collection.EntryCount}"; }
public sealed record MaterialChoice(string Label, IReadOnlyList<SourceRef> References, Guid? PendingId = null, Guid? CollectionId = null, bool IsExam = false)
{ public string RoleLabel => IsExam ? "考题参照" : "学习资料"; public bool NeedsScan => PendingId is not null; }
public sealed partial class WorkspaceViewModel(WorkspaceService service, OcrService? ocr = null, AutomaticPdfScanService? automaticScan = null) : INotifyPropertyChanged
{
    private OcrReview? ocrReview;
    private CollectionChoice? selectedCollection;
    private string ocrPages = "", ocrPreview = "选择扫描教材和页序，先预览本批外发范围。";
    public ObservableCollection<CollectionChoice> Collections { get; } = [];
    public ObservableCollection<Guid> ExamCollectionIds { get; } = [];
    public ObservableCollection<Guid> PendingCollections { get; } = [];
    private static bool Belongs(CollectionSnapshot snapshot, SourceRef reference) => snapshot.References.Any(r => r.LibraryId == reference.LibraryId && r.NodeId == reference.NodeId);
    public IReadOnlyList<MaterialChoice> Materials => References.GroupBy(r => (r.LibraryId, r.Locator.SourceLabel))
        .Where(g => !Collections.Any(c => PendingCollections.Contains(c.Snapshot.Collection.Id) && g.Any(r => Belongs(c.Snapshot,r))))
        .Select(g => new MaterialChoice(g.Key.SourceLabel, g.ToArray(), CollectionId: Collections.FirstOrDefault(c => g.Any(r => Belongs(c.Snapshot,r)))?.Snapshot.Collection.Id, IsExam: Collections.Any(c => ExamCollectionIds.Contains(c.Snapshot.Collection.Id) && g.Any(r => Belongs(c.Snapshot, r)))))
        .Concat(PendingCollections.Select(id => new MaterialChoice(
            (Collections.FirstOrDefault(c => c.Snapshot.Collection.Id == id)?.Label ?? "待识别教材") + " · 待完成扫描", References.Where(r => Collections.Any(c => c.Snapshot.Collection.Id == id && Belongs(c.Snapshot,r))).ToArray(), id, id, ExamCollectionIds.Contains(id)))).ToArray();
    private int scanCompleted, scanTotal;
    public int ScanCompleted => scanCompleted;
    public int ScanTotal => scanTotal;
    public bool ShowScanProgress => SelectedCollection is { } selected &&
        selected.Snapshot.Collection.SourceKind is ("pdf" or "presentation") &&
        selected.Snapshot.Collection.ReadyCount < selected.Snapshot.Collection.EntryCount;
    public double ScanPercent => scanTotal == 0 ? 0 : 100.0 * scanCompleted / scanTotal;
    public bool ScanIndeterminate => Busy && scanTotal == 0;
    public string ScanProgressText => $"已处理 {scanCompleted} / {scanTotal} 页（{ScanPercent:0.0}%）";
    private void SetScanProgress(int completed, int total)
    {
        scanCompleted = completed; scanTotal = total;
        Notify(nameof(ScanCompleted)); Notify(nameof(ScanTotal)); Notify(nameof(ScanPercent)); Notify(nameof(ScanProgressText));
        Notify(nameof(ScanIndeterminate));
    }
    public async Task DeleteMaterialAsync(MaterialChoice material)
    {
        if (operationCompletion is { } active) { Cancel(); await active.Task; }
        await Run(ct =>
        {
        var snapshot=Collections.FirstOrDefault(c=>c.Snapshot.Collection.Id==material.CollectionId)?.Snapshot;
        RemoveQueuedMaterial(snapshot?.Collection.Label ?? material.Label);
        if (material.CollectionId is { } removedId) ExamCollectionIds.Remove(removedId);
        if (snapshot?.Collection.SourceKind is ("pdf" or "presentation") && material.CollectionId is { } id)
        {
            var references=References.Where(r=>Belongs(snapshot,r)).Concat(material.References).Distinct().ToArray();
            var removed = references.ToHashSet();
            References.Replace(References.Where(r => !removed.Contains(r)));
            PendingCollections.Remove(id); ExamCollectionIds.Remove(id); SelectedCollection = null;
        }
        else
        {
            var removed = material.References.ToHashSet();
            References.Replace(References.Where(r => !removed.Contains(r)));
            if(material.PendingId is {} pending) PendingCollections.Remove(pending);
        }
        ocrReview = null; reviewedPrompt = null; reviewedReferences = []; importedReferences = [];
        Preview = "资料已移除，请重新预览外发范围。";
        RefreshMaterials();
        Status = "已从本次计划移除；原文件、扫描缓存和已有课程保留。重新添加同一文件可复用扫描结果。";
        return Task.CompletedTask;
        });
    }
    public Task ContinueScanAsync()
    {
        if (importQueue.TryPeek(out var queued) && (queued.IsDirectory ||
            string.Equals(System.IO.Path.GetFileName(queued.Path), SelectedCollection?.Snapshot.Collection.Label, StringComparison.OrdinalIgnoreCase)))
            return ResumeImportsAsync();
        return Run(ScanCoreAsync);
    }
    public Task ContinueMaterialScanAsync(MaterialChoice material)
    {
        if (Busy) return Task.CompletedTask;
        SelectedCollection = Collections.FirstOrDefault(c => c.Snapshot.Collection.Id == material.CollectionId);
        return ContinueScanAsync();
    }
    private async Task ScanCoreAsync(CancellationToken ct)
    {
        if (library is null || SelectedCollection is not { } selected || selected.Snapshot.Collection.SourceKind is not ("pdf" or "presentation") || automaticScan is null) { Status = "请选择PDF或PPT教材后继续扫描。"; return; }
        var id = selected.Snapshot.Collection.Id;
        var previousRefs = References.Where(r => Belongs(selected.Snapshot,r)).ToArray();
        if (selected.Snapshot.Collection.ReadyCount < selected.Snapshot.Collection.EntryCount && !PendingCollections.Contains(id)) PendingCollections.Add(id);
        Status = selected.Snapshot.Collection.SourceKind == "presentation"
            ? "PPT连续扫描中：最多8页并行；可取消并保留进度。"
            : "自动扫描中：待识别页全部在线处理，最多8页并行；可取消并保留进度。";
        string outcome;
        try
        {
            var progress = new Progress<PdfScanProgress>(p => { if (Busy && !ct.IsCancellationRequested) { SetScanProgress(p.Completed,p.Total); Status = p.Batches > 0 ? $"第{p.Batch}/{p.Batches}批 · {p.Message}" : p.Message; } });
            var result = await Task.Run(() => automaticScan.RunAsync(library.Id,id,true,progress,ct),ct);
            outcome = result.Ok ? "全书扫描完成，可形成发送内容。识别文字及图表仍需复核。" : "扫描已停止，进度已保存：" + result.Error!.Message;
        }
        catch (OperationCanceledException) { outcome = "扫描已暂停，已完成页已保存；点击继续扫描可从中断处继续。"; }
        finally
        {
            await LoadCollections(CancellationToken.None);
            SelectedCollection = Collections.FirstOrDefault(c => c.Snapshot.Collection.Id == id);
            var previousSet = previousRefs.ToHashSet();
            References.Replace(References.Where(r => !previousSet.Contains(r)));
            if (SelectedCollection is { } current)
            {
                if (current.Snapshot.References.Count > 0) UseCollection();
                if (current.Snapshot.Collection.ReadyCount == current.Snapshot.Collection.EntryCount) PendingCollections.Remove(id);
            }
            RefreshMaterials();
        }
        Status = outcome;
    }
    public void RefreshMaterials() => Notify(nameof(Materials));
    public void RemoveMaterial(MaterialChoice material)
    {
        if (Busy) return;
        var removed = material.References.ToHashSet();
        References.Replace(References.Where(r => !removed.Contains(r)));
        if (material.PendingId is { } id) PendingCollections.Remove(id);
        if (SelectedCollection is { } selected && (selected.Snapshot.Collection.Id == material.PendingId ||
            selected.Snapshot.References.Any(material.References.Contains))) SelectedCollection = null;
        ocrReview = null; RefreshMaterials();
        Status = "已从当前计划移除资料，原文件与已有课程保留。请重新形成发送内容。";
    }
    public void SuggestOcrPages()
    {
        OcrPages = string.Join(",", SelectedCollection?.Snapshot.Manifest.Entries
            .Where(e => e.QualityFlags.Contains("ocrRequired") && int.TryParse(e.Locator, out _))
            .Take(20).Select(e => e.Locator) ?? []);
    }
    public CollectionChoice? SelectedCollection { get => selectedCollection; set { selectedCollection = value; ocrReview = null; OcrPreview = "选择页范围后重新预览。"; SetScanProgress(value?.Snapshot.Collection.ReadyCount ?? 0,value?.Snapshot.Collection.EntryCount ?? 0); Notify(); Notify(nameof(CollectionStructure)); Notify(nameof(ShowScanProgress)); } }
    public string CollectionStructure => SelectedCollection?.Snapshot.Structure is { } index
        ? $"索引{index.IndexVersion} / 切分{index.ChunkerVersion}；源hash={index.SourceHash}\n" + string.Join("\n", index.Nodes.Select(n => $"{n.Title} · {n.Confidence} · {string.Join(",", n.Spans.Select(s => s.EntryKey + ":" + s.Start + "-" + s.End))}")) + "\n语义单元：\n" + string.Join("\n", index.Units.Select(u => $"{u.Kind} · {u.Id} · 父={u.ParentUnitId} · 续接={string.Join(",", u.ContinuationIds)}"))
        : "尚无结构索引；旧引用继续可用。重新导入同一教材可使用缓存重建，不重新OCR。";
    public void UseCollection()
    {
        var snapshot = SelectedCollection?.Snapshot;
        if (snapshot is null || snapshot.References.Count == 0) { Status = "此集合尚无正文，请先识别需要的页。"; return; }
        if (!References.Contains(snapshot.References[0])) References.Add(snapshot.References[0]);
        Status = "已选择教材；保存草稿并预览时绑定完整目录，正文按课检索。请核对目录缺口。";
    }
    public string OcrPages { get => ocrPages; set { ocrPages = value; ocrReview = null; Notify(); } }
    public string OcrPreview { get => ocrPreview; private set { ocrPreview = value; Notify(); } }
    private async Task LoadCollections(CancellationToken ct)
    {
        var prior = SelectedCollection?.Snapshot.Collection.Id;
        Collections.Clear();
        if (library is not null && service.Knowledge is ICollectionKnowledge collections)
        {
            var snapshots = await Task.Run(() => collections.CollectionsAsync(library.Id, ct), ct);
            if (service.Knowledge is IAutomaticPdfKnowledge pdf)
            {
                foreach (var item in snapshots.Where(s => s.Collection.SourceKind is ("pdf" or "presentation"))) await pdf.CommitStagedOcrAsync(library.Id,item.Collection.Id,ct);
                snapshots = await Task.Run(() => collections.CollectionsAsync(library.Id, ct), ct);
            }
            foreach (var item in snapshots) Collections.Add(new(item));
        }
        SelectedCollection = Collections.FirstOrDefault(c => c.Snapshot.Collection.Id == prior) ?? Collections.FirstOrDefault();
        RefreshMaterials();
    }
    public Task PrepareOcrAsync() => Run(async ct =>
    {
        if (library is null || SelectedCollection is null || ocr is null) { Status = "请先导入并选择扫描教材。"; return; }
        var pages = new List<int>();
        foreach (var part in OcrPages.Split(',', '，'))
        {
            var range = part.Trim().Split('-');
            if (range.Length > 2 || !int.TryParse(range[0], out var first) || first < 1 || first > 2000) { Status = "页序格式：11,51或1-5；每批最多20页。"; return; }
            var last = first;
            if (range.Length == 2 && (!int.TryParse(range[1], out last) || last < first || last > 2000)) { Status = "页范围无效。"; return; }
            if (pages.Count + last - first + 1 > 20) { Status = "每批最多20页，请缩小范围。"; return; }
            pages.AddRange(Enumerable.Range(first, last - first + 1));
        }
        Status = "正在准备本批页图，可取消。";
        var result = await ocr.PrepareAsync(library.Id, SelectedCollection.Snapshot.Collection.Id, pages, ct);
        ocrReview = result.Ok ? result.Value : null; OcrPreview = result.Ok ? result.Value!.Preview : result.Error!.Message;
        Status = result.Ok ? "请核对本批页范围、模型和费用说明后授权识别。" : result.Error!.Message;
    });
    public Task RunOcrAsync() => Run(async ct =>
    {
        if (ocrReview is null || ocr is null) { Status = "请先预览当前页范围。"; return; }
        var previous = SelectedCollection?.Snapshot;
        var selectedReferences = References.Where(r => previous?.References.Contains(r) == true).ToArray();
        var review = ocrReview; ocrReview = null;
        var result = await ocr.RunAsync(review, true, new Progress<string>(s => { if (Busy) Status = s; }), ct);
        await LoadCollections(ct); await SearchCore(ct);
        if (SelectedCollection is { } selected && (PendingCollections.Contains(selected.Snapshot.Collection.Id) || selectedReferences.Length > 0))
        {
            var previousSet = selectedReferences.ToHashSet();
            References.Replace(References.Where(r => !previousSet.Contains(r)));
            UseCollection();
            if (selected.Snapshot.References.Count > 0) PendingCollections.Remove(selected.Snapshot.Collection.Id);
        }
        SuggestOcrPages(); RefreshMaterials();
        Status = result.Ok ? $"本批{result.Value}页已缓存，识别文字待人工复核；没有自动识别其他页。" : result.Error!.Message;
    });
    public event PropertyChangedEventHandler? PropertyChanged;
    private LibraryDto? library;
    private string query = "", prompt = "请用一句话说明什么是变量。", preview = "请先预览外发范围。", reply = "", status = "资料与模型工作台；课程请从课程图打开。";
    private bool busy;
    private CancellationTokenSource? cancellation;
    private TaskCompletionSource? operationCompletion;
    private string? reviewedPrompt;
    private SourceRef[] reviewedReferences = [];
    private IReadOnlyList<SourceRef> importedReferences = [];
    public ObservableCollection<KnowledgeFragment> Fragments { get; } = [];
    public SnapshotCollection<SourceRef> References { get; } = [];
    public void RestoreReferences(IReadOnlyList<SourceRef> references)
    {
        References.Replace(references);
        RefreshMaterials();
    }
    public KnowledgeFragment? SelectedFragment { get; set; }
    public SourceRef? SelectedReference { get; set; }
    public string LibraryPath => library?.Root ?? "尚未选择资料库";
    public string Query { get => query; set { query = value; Notify(); } }
    public string Prompt { get => prompt; set { prompt = value; Notify(); } }
    public string Preview { get => preview; private set { preview = value; Notify(); } }
    public string Reply { get => reply; private set { reply = value; Notify(); } }
    public string Status { get => status; private set { status = value; Notify(); } }
    public bool Busy { get => busy; private set { busy = value; Notify(); Notify(nameof(CanOperate)); Notify(nameof(CanImport)); Notify(nameof(CanResumeImports)); Notify(nameof(ScanIndeterminate)); } }
    public bool CanOperate => !Busy;
    public Task LoadAsync() => Run(async ct => { library = await service.Knowledge.CurrentAsync(ct); Notify(nameof(LibraryPath)); await LoadCollections(ct); });
    public Task SwitchAsync(HostSelection selection) => Run(async ct =>
    {
        if (importQueue.Count > 0) { Status = "请先完成或清空待处理导入队列后切换资料库。"; return; }
        var result = await service.Knowledge.SwitchAsync(new(Guid.NewGuid(), library?.Revision ?? 0, selection), ct);
        if (!result.Ok) { Status = $"{result.Error!.Code}：{result.Error.Message}"; return; }
        library = result.Value; Fragments.Clear(); Notify(nameof(LibraryPath)); Status = "当前资料库已切换。旧引用保留在引用栏，原库没有迁移或删除。";
        ocrReview = null; await LoadCollections(ct);
    });
    private async Task<bool> ImportCoreAsync(HostSelection selection, CancellationToken ct)
    {
        if (library is null) { Status = "请先选择资料库。"; return false; }
        Status = "正在自动识别文件类型、整理目录并提取正文，请稍候。";
        SelectedCollection = null;
        OcrPages = "";
        SetScanProgress(0, 0);
        var preparing = true;
        var progress = new Progress<ImportProgress>(p =>
        {
            if (!preparing || ct.IsCancellationRequested) return;
            SetScanProgress(p.Completed, p.Total); Status = p.Message;
        });
        Result<ImportReport> result;
        try
        {
            result = service.Knowledge is IImportProgressKnowledge tracked
                ? await tracked.ImportWithProgressAsync(new(Guid.NewGuid(), 0, selection), library.Id, progress, ct)
                : await service.Knowledge.ImportAsync(new(Guid.NewGuid(), 0, selection), library.Id, ct);
        }
        finally { preparing = false; }
        importedReferences = result.Ok ? result.Value!.References ?? [] : [];
        if (selection.IsPlanImport)
        {
            References.Replace(References.Concat(importedReferences).Distinct());
            await LoadCollections(ct);
            SelectedCollection = Collections.LastOrDefault(c => result.Value?.Files.Any(f => f.Ok && f.SourceLabel == c.Snapshot.Collection.Label) == true);
            foreach (var item in Collections.Where(c => result.Value?.Files.Any(f => f.Ok && f.SourceLabel == c.Snapshot.Collection.Label) == true))
            {
                var collectionId = item.Snapshot.Collection.Id;
                if (selection.IsExamImport && !ExamCollectionIds.Contains(collectionId)) ExamCollectionIds.Add(collectionId);
                else if (!selection.IsExamImport) ExamCollectionIds.Remove(collectionId);
                if (item.Snapshot.References.Count == 0 && !PendingCollections.Contains(item.Snapshot.Collection.Id)) PendingCollections.Add(item.Snapshot.Collection.Id);
                else if (item.Snapshot.References.Count > 0) PendingCollections.Remove(item.Snapshot.Collection.Id);
            }
            SuggestOcrPages(); RefreshMaterials();
        }
        await SearchCore(ct);
        Status = result.Ok ? string.Join("\n", result.Value!.Files.Select(f => $"{f.SourceLabel}：{(f.Ok ? $"已导入，正文 {f.Added + f.Updated + f.Unchanged} 项。{f.Error}" : f.Error)}")) : $"{result.Error!.Code}：{result.Error.Message}";
        if (automaticScan is null && OcrPages.Length > 0) Status += "\n检测到扫描页，已自动建议下一批页序。请核对范围并授权识别；不会自动上传全书。";
        if (automaticScan is not null && result.Ok)
        {
            if (!selection.IsPlanImport) await LoadCollections(ct);
            var ids = Collections.Where(c => c.Snapshot.Collection.SourceKind is ("pdf" or "presentation") && result.Value!.Files.Any(f => f.Ok && f.SourceLabel == c.Snapshot.Collection.Label)).Select(c => c.Snapshot.Collection.Id).ToArray();
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested(); SelectedCollection = Collections.Single(c => c.Snapshot.Collection.Id == id);
                await ScanCoreAsync(ct);
                if (ct.IsCancellationRequested || SelectedCollection!.Snapshot.Collection.ReadyCount < SelectedCollection.Snapshot.Collection.EntryCount) break;
            }
        }
        return result.Ok && result.Value!.Files.All(f => f.Ok) && !ct.IsCancellationRequested &&
            !Collections.Any(c => result.Value.Files.Any(f => f.SourceLabel == c.Snapshot.Collection.Label) && c.Snapshot.Collection.ReadyCount < c.Snapshot.Collection.EntryCount && c.Snapshot.Collection.SourceKind is ("pdf" or "presentation"));
    }
    public Task ImportCollectionAsync(HostSelection selection) => Run(async ct =>
    {
        if (library is null || service.Knowledge is not ICollectionKnowledge collections) { Status = "请先选择资料库。"; return; }
        Status = "正在整理教材目录和正文，可取消。";
        var result = await collections.ImportCollectionAsync(selection, library.Id, ct);
        if (!result.Ok) { Status = result.Error!.Message; return; }
        importedReferences = result.Value!.References;
        await LoadCollections(ct);
        SelectedCollection = Collections.Single(c => c.Snapshot.Collection.Id == result.Value.Collection.Id);
        SuggestOcrPages();
        Status = $"教材已保存：共{result.Value.Collection.EntryCount}项，{result.Value.Collection.ReadyCount}项有可检索文字；其余需识别或复核。目录不代表正文已读。";
        await SearchCore(ct);
        if (automaticScan is not null && result.Value.Collection.SourceKind is ("pdf" or "presentation")) await ScanCoreAsync(ct);
    });
    public void UseImportedReferences()
    {
        if (importedReferences.Count is < 1 or > 99) { Status = "本次导入没有可选片段或超过99块；请缩小目录或手动选择。原引用保留。"; return; }
        References.Replace(importedReferences);
        Status = $"已用本次导入的{References.Count}个固定版本片段替换读取范围。请到计划页保存草稿并预览；尚未外发。";
    }
    public Task SearchAsync() => Run(async ct => { await SearchCore(ct); });
    private async Task SearchCore(CancellationToken ct)
    {
        if (library is null) { Status = "请先选择资料库。"; return; }
        var result = await service.Knowledge.SearchAsync(library.Id, Query, [], 100, ct);
        Fragments.Clear(); foreach (var fragment in result.Fragments) Fragments.Add(fragment);
        if (result.Insufficient) Status = result.Message;
    }
    public void AddReference()
    {
        if (SelectedFragment is not { } f) { Status = "请先选择一个资料块。"; return; }
        if (!References.Contains(f.Reference)) References.Add(f.Reference);
        Status = "已保留固定版本引用；切库后仍可查看。只有引用栏中的资料会进入外发预览。";
    }
    public void RemoveReference() { if (SelectedReference is { } r) References.Remove(r); }
    public Task ResolveAsync() => Run(async ct =>
    {
        if (SelectedReference is not { } r) { Status = "请选择引用。"; return; }
        var result = await service.Knowledge.ResolveAsync(r, ct);
        Preview = result.Text ?? $"{r.Locator.SourceLabel}\n{result.UnavailableReason}";
    });
    public Task PrepareAsync() => Run(async ct =>
    {
        if (string.IsNullOrWhiteSpace(Prompt)) { Status = "请输入请求。"; return; }
        var review = await service.PrepareAsync(Prompt, References.ToArray(), ct);
        reviewedPrompt = Prompt; reviewedReferences = References.ToArray();
        Preview = $"发送至 {review.Destination}\n模型：{review.Model}\n共 {review.Sources.Count} 项，UTF-8 {review.Sources.Sum(s => Encoding.UTF8.GetByteCount(s.Text))} 字节\n" +
            string.Join("\n\n", review.Sources.Select(s => $"[{s.Kind} / {s.Id}]\n{s.Text}"));
        Status = "请核对下方完整内容，再选择同意发送或拒绝。鉴权密钥不会作为学习内容发送。";
    });
    public Task SendAsync(bool allow) => Run(async ct =>
    {
        if (allow && (reviewedPrompt != Prompt || !reviewedReferences.SequenceEqual(References))) { Status = "CONSENT_REQUIRED：内容已变化，请重新预览。"; return; }
        var result = await service.SendAsync(allow, ct);
        if (!result.Ok) { Reply = ""; Status = $"{result.Error!.Code}：{result.Error.Message}"; return; }
        var value = result.Value!; Reply = value.Text;
        Status = value.Usage is { } u ? $"{value.Model} · 实际用量：输入 {u.PromptTokens}，输出 {u.CompletionTokens}，合计 {u.TotalTokens} tokens。余额未查询。" : "回复成功；服务未返回用量，余额未知。";
    });
    public void Cancel() => cancellation?.Cancel();
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (Busy) return; Busy = true; cancellation = new(); operationCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = "操作已取消。"; }
        catch (Exception) { Status = "IO_FAILURE：操作未完成，请检查资料库、输入长度或本地设置。"; }
        finally { cancellation.Dispose(); cancellation = null; Busy = false; var completed=operationCompletion; operationCompletion=null; completed?.TrySetResult(); if (!processingImports && !importsPaused && importQueue.Count > 0) _ = ResumeImportsAsync(); }
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
