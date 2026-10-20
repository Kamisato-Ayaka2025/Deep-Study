using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Desktop;
public sealed record CollectionChoice(CollectionSnapshot Snapshot)
{ public string Label => $"{Snapshot.Collection.Label} · 正文{Snapshot.Collection.ReadyCount}/{Snapshot.Collection.EntryCount}"; }
public sealed class WorkspaceViewModel(WorkspaceService service, OcrService? ocr = null) : INotifyPropertyChanged
{
    private OcrReview? ocrReview;
    private CollectionChoice? selectedCollection;
    private string ocrPages = "", ocrPreview = "选择扫描教材和页序，先预览本批外发范围。";
    public ObservableCollection<CollectionChoice> Collections { get; } = [];
    public CollectionChoice? SelectedCollection { get => selectedCollection; set { selectedCollection = value; ocrReview = null; OcrPreview = "选择页范围后重新预览。"; Notify(); Notify(nameof(CollectionStructure)); } }
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
            foreach (var item in await collections.CollectionsAsync(library.Id, ct)) Collections.Add(new(item));
        SelectedCollection = Collections.FirstOrDefault(c => c.Snapshot.Collection.Id == prior) ?? Collections.FirstOrDefault();
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
        var review = ocrReview; ocrReview = null;
        var result = await ocr.RunAsync(review, true, new Progress<string>(s => { if (Busy) Status = s; }), ct);
        await LoadCollections(ct); await SearchCore(ct);
        Status = result.Ok ? $"本批{result.Value}页已缓存，识别文字待人工复核；没有自动识别其他页。" : result.Error!.Message;
    });
    public event PropertyChangedEventHandler? PropertyChanged;
    private LibraryDto? library;
    private string query = "", prompt = "请用一句话说明什么是变量。", preview = "请先预览外发范围。", reply = "", status = "资料与模型工作台；课程请从课程图打开。";
    private bool busy;
    private CancellationTokenSource? cancellation;
    private string? reviewedPrompt;
    private SourceRef[] reviewedReferences = [];
    private IReadOnlyList<SourceRef> importedReferences = [];
    public ObservableCollection<KnowledgeFragment> Fragments { get; } = [];
    public ObservableCollection<SourceRef> References { get; } = [];
    public void RestoreReferences(IReadOnlyList<SourceRef> references)
    {
        References.Clear(); foreach (var reference in references) References.Add(reference);
    }
    public KnowledgeFragment? SelectedFragment { get; set; }
    public SourceRef? SelectedReference { get; set; }
    public string LibraryPath => library?.Root ?? "尚未选择资料库";
    public string Query { get => query; set { query = value; Notify(); } }
    public string Prompt { get => prompt; set { prompt = value; Notify(); } }
    public string Preview { get => preview; private set { preview = value; Notify(); } }
    public string Reply { get => reply; private set { reply = value; Notify(); } }
    public string Status { get => status; private set { status = value; Notify(); } }
    public bool Busy { get => busy; private set { busy = value; Notify(); Notify(nameof(CanOperate)); } }
    public bool CanOperate => !Busy;
    public Task LoadAsync() => Run(async ct => { library = await service.Knowledge.CurrentAsync(ct); Notify(nameof(LibraryPath)); await LoadCollections(ct); });
    public Task SwitchAsync(HostSelection selection) => Run(async ct =>
    {
        var result = await service.Knowledge.SwitchAsync(new(Guid.NewGuid(), library?.Revision ?? 0, selection), ct);
        if (!result.Ok) { Status = $"{result.Error!.Code}：{result.Error.Message}"; return; }
        library = result.Value; Fragments.Clear(); Notify(nameof(LibraryPath)); Status = "当前资料库已切换。旧引用保留在引用栏，原库没有迁移或删除。";
        ocrReview = null; await LoadCollections(ct);
    });
    public Task ImportAsync(HostSelection selection) => Run(async ct =>
    {
        if (library is null) { Status = "请先选择资料库。"; return; }
        var result = await service.Knowledge.ImportAsync(new(Guid.NewGuid(), 0, selection), library.Id, ct);
        importedReferences = result.Ok ? result.Value!.References ?? [] : [];
        if (selection.IsPlanImport)
        {
            foreach (var reference in importedReferences)
                if (!References.Contains(reference)) References.Add(reference);
            await LoadCollections(ct);
            SelectedCollection = Collections.LastOrDefault(c => result.Value?.Files.Any(f => f.SourceLabel == c.Snapshot.Collection.Label) == true) ?? SelectedCollection;
        }
        Status = result.Ok ? string.Join("\n", result.Value!.Files.Select(f => $"{f.SourceLabel}：{(f.Ok ? $"新增 {f.Added} / 更新 {f.Updated} / 未变 {f.Unchanged}" : f.Error)}")) : $"{result.Error!.Code}：{result.Error.Message}";
        await SearchCore(ct);
    });
    public Task ImportCollectionAsync(HostSelection selection) => Run(async ct =>
    {
        if (library is null || service.Knowledge is not ICollectionKnowledge collections) { Status = "请先选择资料库。"; return; }
        Status = "正在整理教材目录和正文，可取消。";
        var result = await collections.ImportCollectionAsync(selection, library.Id, ct);
        if (!result.Ok) { Status = result.Error!.Message; return; }
        importedReferences = result.Value!.References;
        await LoadCollections(ct);
        Status = $"教材已保存：共{result.Value.Collection.EntryCount}项，{result.Value.Collection.ReadyCount}项有可检索文字；其余需识别或复核。目录不代表正文已读。";
        await SearchCore(ct);
    });
    public void UseImportedReferences()
    {
        if (importedReferences.Count is < 1 or > 99) { Status = "本次导入没有可选片段或超过99块；请缩小目录或手动选择。原引用保留。"; return; }
        References.Clear(); foreach (var reference in importedReferences) References.Add(reference);
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
        if (Busy) return; Busy = true; cancellation = new();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = "操作已取消。"; }
        catch (Exception) { Status = "IO_FAILURE：操作未完成，请检查资料库、输入长度或本地设置。"; }
        finally { cancellation.Dispose(); cancellation = null; Busy = false; }
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
