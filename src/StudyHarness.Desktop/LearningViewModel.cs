using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Desktop;

public sealed record LearningNode(string Key, string Label, string Detail, string Type = "theory");
public sealed class LearningViewModel(GenerationService service, Guid subjectId) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<LessonReader>? LessonChanged;
    public event Action? LessonCleared;
    private bool refreshing;
    private Task? refreshTask;
    private CancellationTokenSource? preparation;
    private Task<Result<GenerationTask>>? preparationTask;
    private string? preparationKey;
    private bool closed;
    private readonly SemaphoreSlim readerWrites = new(1, 1);
    private int previewRequest;
    private int viewRevision;
    public SnapshotCollection<LearningNode> Nodes { get; } = [];
    private string status = "选择理论课程；实验材料请从课程图打开。", preview = "", detail = "", control = "", tasks = "";
    private LearningNode? selected;
    private TheoryReview? review;
    private LessonReader? reader;
    private bool viewingHistory;
    private (Guid, long)? observedTask;
    public ObservableCollection<LessonHistory> History { get; } = [];
    public LessonHistory? SelectedHistory { get; set; }
    public bool IsGenerating { get; private set; }
    internal bool IsPreparing => preparation is not null;
    public bool CanGenerate { get; private set; }
    public string GenerateLabel { get; private set; } = "点击自动生成课文";
    public string Title => Selected?.Detail.Split('\n')[0] ?? "课程";
    public string LessonTypeLabel => Selected?.Type == "quiz" ? "测验课" : Selected?.Type == "lab" ? "实验课" : "理论课";
    public string ContentLabel => Selected?.Type == "quiz" ? "测验" : "课文";
    public bool HasCurrentLesson { get; private set; }
    public LessonReader? Reader => reader;
    public LearningNode? Selected
    {
        get => selected;
        set
        {
            var changed = selected?.Key != value?.Key; selected = value; Notify(); Notify(nameof(Title)); Notify(nameof(LessonTypeLabel)); Notify(nameof(ContentLabel)); Detail = value?.Detail ?? "";
            if (changed) { HasCurrentLesson = false; GenerateLabel = "点击自动生成" + ContentLabel; CanGenerate = false; Notify(nameof(GenerateLabel)); Notify(nameof(CanGenerate)); }
            if (changed && !refreshing) { HidePreview(); viewingHistory = false; reader = null; LessonCleared?.Invoke(); if (value is not null) _ = SafeAsync(OpenAsync); }
        }
    }
    public string Status { get => status; private set { status = value; Notify(); } }
    public bool HasPreview => !string.IsNullOrEmpty(preview);
    public void HidePreview() { previewRequest++; Preview = ""; review = null; }
    public string Preview { get => preview; private set { preview = value; Notify(); Notify(nameof(HasPreview)); } }
    public string Detail { get => detail; private set { detail = value; Notify(); } }
    public string Control { get => control; private set { control = value; Notify(); } }
    public string Tasks { get => tasks; private set { tasks = value; Notify(); } }
    public async Task RefreshAsync()
    {
        if (refreshTask is { IsCompleted: false } pending) { await pending; return; }
        var operation = RefreshCoreAsync();
        refreshTask = operation;
        try { await operation; }
        finally { if (ReferenceEquals(refreshTask, operation)) refreshTask = null; }
    }
    private async Task RefreshCoreAsync()
    {
        try
        {
            var selectedKey = selected?.Key;
            var revision = viewRevision;
            var snapshot = await Task.Run(async () =>
            {
                var plan = await service.Planning.GetAsync(subjectId);
                var done = await service.Learning.CompletedNodesAsync(subjectId);
                var queue = await service.Queue.TasksAsync();
                var control = await service.Queue.ControlAsync();
                var key = selectedKey ?? plan?.Plan?.Candidate.Nodes.FirstOrDefault()?.Key;
                var lesson = key is null ? null : await service.Learning.GetLessonAsync(subjectId, key);
                var versions = key is null ? Array.Empty<LessonHistory>() : await service.Learning.HistoryAsync(subjectId, key);
                var latest = queue.LastOrDefault(t => t.SubjectId == subjectId && t.NodeKey == key);
                var needsClick = latest?.Status == GenerationStatus.Queued && !await service.HasApprovalAsync(latest);
                var contextGap = latest?.Status == GenerationStatus.Failed && service.Queue is IContextDiagnostics diagnostics ? await diagnostics.ContextGapAsync(latest.Id) : null;
                return (plan, done, queue, control, lesson, versions, needsClick, contextGap);
            });
            if (closed || selected?.Key != selectedKey || revision != viewRevision) return;
            var (draft, completed, all, policy, loaded, versions, needsClick, contextGap) = snapshot;
            if (draft?.Plan is null) return;
            Control = $"模式：{policy.Mode} · 自动状态：{(policy.AutoPaused ? "已暂停，需主动继续" : "未暂停")} · 授权不跨重启保留";
            Tasks = string.Join("\n", all.Where(t => t.SubjectId == subjectId).TakeLast(15).Select(t =>
                $"{t.NodeKey} · {t.Status} · 派发 {t.Attempts}/3 · {FailureText(t.Failure)}" + (t.Usage is { } u ? $" · 服务已报告{u.TotalTokens} tokens（未报告部分未知）" : " · 用量未知/未返回") +
                (t.Status == GenerationStatus.Queued ? " · 等待授权/退避/调度" : "")));
            var key = selected?.Key; refreshing = true;
            var nodes = new List<LearningNode>();
            var latestTasks = all.Where(t => t.SubjectId == subjectId).GroupBy(t => t.NodeKey).ToDictionary(g => g.Key, g => g.Last());
            foreach (var n in draft.Plan.Candidate.Nodes)
            {
                var task = latestTasks.GetValueOrDefault(n.Key);
                var state = completed.Contains(n.Key) ? "已完成" : task?.Status == GenerationStatus.Ready ? "正文就绪" : task?.Status.ToString() ?? "未生成";
                nodes.Add(new(n.Key, $"{n.Key} · {n.Title} · {state}", $"{n.Title}\n目标：{string.Join("；", n.Objectives)}\n建议先学：{string.Join("、", n.PrerequisiteKeys)}\n{(n.Type == "lab" ? "实验材料：请从课程图打开，自行测评" : state)}", n.Type));
            }
            Nodes.Replace(nodes);
            Selected = Nodes.FirstOrDefault(n => n.Key == key) ?? Nodes.FirstOrDefault();
            refreshing = false;
            var selectedNode = draft.Plan.Candidate.Nodes.FirstOrDefault(n => n.Key == Selected?.Key);
            var current = loaded?.Value;
            if (loaded?.Error?.Code == ErrorCode.IO_FAILURE)
            {
                reader = null; viewingHistory = false; LessonCleared?.Invoke();
                CanGenerate = false; Status = loaded.Error.Message; Notify(nameof(CanGenerate)); return;
            }
            IsGenerating = preparation is not null || all.Any(t => t.SubjectId == subjectId && t.NodeKey == Selected?.Key && t.Status is GenerationStatus.Queued or GenerationStatus.Running);
            var latest = all.LastOrDefault(t => t.SubjectId == subjectId && t.NodeKey == Selected?.Key);
            if (latest is not null && observedTask != (latest.Id, latest.Revision))
            {
                observedTask = (latest.Id, latest.Revision);
                Status = latest.Status switch {
                    GenerationStatus.Failed => contextGap is not null ? contextGap + "；请补充资料或重新规划本课，重复生成不能补齐原文。" : "生成失败：" + FailureText(latest.Failure) + (current is null ? "；点击按钮可重试。" : "；原文章和学习记录已保留。"),
                    GenerationStatus.Cancelled => "生成已取消，已发送的请求可能计费；原文章和学习记录保留。",
                    GenerationStatus.Ready => "文章已就绪，可直接阅读和作答。",
                    _ => "正在生成文章，请稍候；成功后自动显示。"
                };
            }
            if (latest?.Status == GenerationStatus.Running && DateTimeOffset.UtcNow - latest.CreatedAt > TimeSpan.FromSeconds(300))
                Status = "文章生成比预期久，仍在处理；可取消，原文章会保留。";
            CanGenerate = preparation is null && (!IsGenerating || needsClick) && selectedNode?.Type is ("theory" or "quiz") && draft.Confirmed;
            HasCurrentLesson = current is not null;
            GenerateLabel = current is not null ? "重新生成" + ContentLabel : "点击自动生成" + ContentLabel;
            if (needsClick) Status = "任务尚未发送。点击继续生成即可使用当前配置完成本课。";
            Notify(nameof(IsGenerating)); Notify(nameof(CanGenerate)); Notify(nameof(GenerateLabel));
            if (!viewingHistory && current is not null && reader?.Identity.LessonHash != current.Identity.LessonHash)
            { reader = current; LessonChanged?.Invoke(current); Status = "文章已就绪，可直接阅读和作答。"; }
            if (Selected is not null)
            {
                if (!History.Select(h => h.LessonHash).SequenceEqual(versions.Select(h => h.LessonHash)))
                { History.Clear(); foreach (var version in versions) History.Add(version); SelectedHistory = History.FirstOrDefault(); Notify(nameof(SelectedHistory)); }
            }
        }
        finally { refreshing = false; }
    }
    public async Task OpenAsync()
    {
        var revision = ++viewRevision;
        viewingHistory = false;
        if (Selected is null) return;
        var key = Selected.Key;
        var plan = await Task.Run(() => service.Planning.GetAsync(subjectId));
        if (closed || Selected?.Key != key || revision != viewRevision) return;
        if (plan?.Plan?.Candidate.Nodes.FirstOrDefault(n => n.Key == key)?.Type == "lab")
        {
            reader = null; LessonCleared?.Invoke();
            Status = "请从课程图打开实验材料窗口。实验由你自行测评，软件不编译或运行代码。";
            return;
        }
        var result = await Task.Run(() => service.Learning.GetLessonAsync(subjectId, key));
        if (closed || Selected?.Key != key || revision != viewRevision) return;
        reader = result.Value;
        HasCurrentLesson = result.Ok;
        GenerateLabel = HasCurrentLesson ? "重新生成" + ContentLabel : "点击自动生成" + ContentLabel; Notify(nameof(GenerateLabel));
        if (reader is not null) { LessonChanged?.Invoke(reader); Status = reader.CanAnswer ? "正文已打开。跳过阅读仍需每题答对一次。" : "历史版本仅供阅读。"; }
        else { LessonCleared?.Invoke(); Status = result.Error?.Message ?? "本课尚无课文。点击自动生成课文即可。"; }
        await RefreshAsync();
    }
    public async Task GenerateAsync()
    {
        if (closed || Selected is null || !CanGenerate || preparation is not null) return;
        var key = Selected.Key;
        using var cancellation = new CancellationTokenSource();
        preparation = cancellation;
        preparationKey = key;
        CanGenerate = false; IsGenerating = true;
        Status = "正在准备本课资料，请稍候；可取消。";
        Notify(nameof(CanGenerate)); Notify(nameof(IsGenerating));
        try
        {
            preparationTask = Task.Run(async () =>
            {
                var loaded = await service.Learning.GetLessonAsync(subjectId, key, cancellation.Token);
                if (loaded.Error?.Code == ErrorCode.IO_FAILURE) return new Result<GenerationTask>(false, null, loaded.Error);
                return await service.GenerateAsync(subjectId, key, loaded.Ok, cancellation.Token);
            });
            var result = await preparationTask;
            if (closed || Selected?.Key != key) return;
            Status = result.Ok ? "正在生成文章，请稍候。成功后自动显示；失败或取消不会替换原文章。" : $"{result.Error!.Code}：{result.Error.Message}";
            viewingHistory = false;
        }
        catch (OperationCanceledException) { if (!closed && Selected?.Key == key) Status = "操作已取消。"; }
        finally
        {
            preparation = null; preparationTask = null; preparationKey = null;
            if (!closed) { await RefreshAsync(); }
        }
    }
    public void OpenHistory()
    {
        if (SelectedHistory is null) return;
        viewRevision++;
        viewingHistory = true; reader = SelectedHistory.Reader; LessonChanged?.Invoke(reader);
        Status = "正在查看历史版本（只读）；已获得的完成状态保留。";
    }
    public async Task PrepareAsync(bool all)
    {
        var request = ++previewRequest;
        var key = all ? null : Selected?.Key;
        var result = await Task.Run(() => service.PrepareAsync(subjectId, key));
        if (closed || request != previewRequest) return;
        review = result.Value; Preview = review?.Preview ?? "";
        Status = result.Ok ? "请核对外发范围。单课或全学科授权均覆盖所列固定内容及每任务至多3次派发；新来源/模型必须重新授权。" : result.Error!.Message;
    }
    public void Authorize(bool allow)
    {
        if (review is null) { Status = "请先预览外发范围。"; return; }
        service.Authorize(review, allow); Status = allow ? "已授权。点击生成，或切换自动模式。" : "已拒绝外发。"; review = null;
    }
    public async Task RequestAsync(bool retry)
    {
        if (Selected is null) return;
        var result = await service.Queue.RequestAsync(subjectId, new(Guid.NewGuid(), 0, new(Selected.Key, retry ? GenerationTrigger.ManualRetry : GenerationTrigger.Click)));
        Status = result.Ok ? "课程已入队；未授权时不会发送，请先预览并同意。" : $"{result.Error!.Code}：{result.Error.Message}";
        if (result.Error?.Code == ErrorCode.ALREADY_READY) await OpenAsync();
        await RefreshAsync();
    }
    public async Task CancelAsync()
    {
        var key = preparationKey ?? Selected?.Key;
        preparation?.Cancel();
        if (preparationTask is { } pending)
            try { await pending; } catch (OperationCanceledException) { }
        await Task.Run(async () =>
        {
            var task = (await service.Queue.TasksAsync()).LastOrDefault(t => t.SubjectId == subjectId && t.NodeKey == key && t.Status is GenerationStatus.Queued or GenerationStatus.Running);
            if (task is not null) await service.CancelAsync(task.Id);
        });
        Status = "生成已取消；原文章保留，已发送请求可能计费。"; await RefreshAsync();
    }
    public void Close() { closed = true; viewRevision++; preparation?.Cancel(); HidePreview(); }
    public async Task ModeAsync(GenerationMode mode) { await service.SetModeAsync(mode); await RefreshAsync(); }
    public async Task ResumeAsync() { await service.ResumeAsync(); Status = "已明确继续自动生成；仍需有效授权。"; await RefreshAsync(); }
    public async Task SubmitAsync(string hash, string question, string option, Guid requestId, string? responseText = null)
    {
        if (reader is null || reader.Identity.LessonHash != hash || Selected?.Key != reader.Identity.NodeKey) return;
        var identity = reader.Identity;
        await readerWrites.WaitAsync();
        Result<AnswerResult> result;
        try { result = await Task.Run(() => service.Learning.SubmitAnswerAsync(new(requestId, 0, new(identity, question, option, responseText)))); }
        finally { readerWrites.Release(); }
        if (closed || reader?.Identity != identity || Selected?.Key != identity.NodeKey) return;
        if (!result.Ok) { Status = result.Error!.Message; return; }
        var value = result.Value!;
        await OpenAsync();
        Status = value.Kind == "written" ? option == "reveal" ? "参考答案已展示，请自行核对；软件未判分。" : value.Completed ? "已记录自评，本课已完成（含自行核对）。" : "已记录自评，可继续练习。" : value.Correct ? value.Completed ? "回答正确，本课已完成。" : "回答正确，解析已解锁。" : "本次回答错误，请继续尝试；已获完成记录不会撤回。";
        await service.ScanAsync(); await RefreshAsync();
    }
    public async Task PositionAsync(string hash, string block, int offset, Guid requestId)
    {
        if (reader is null || reader.Identity.LessonHash != hash || Selected?.Key != reader.Identity.NodeKey) return;
        var identity = reader.Identity;
        await readerWrites.WaitAsync();
        try { await Task.Run(() => service.Learning.SavePositionAsync(new(requestId, 0, new(identity, block, offset)))); }
        finally { readerWrites.Release(); }
    }
    public async Task SafeAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { Status = "操作已取消。"; }
        catch (Exception) { Status = "IO_FAILURE：操作未完成，请保留数据并重试；没有把失败记录为完成。"; }
    }
    private static string FailureText(string? code) => code switch
    {
        "AUTH_INVALID" => "密钥鉴权失败；请在设置中更新密钥后手动重试",
        "MODEL_INVALID" => "模型不可用；请检查模型设置后再次点击生成",
        "QUOTA_EXHAUSTED" => "额度不足；补充额度后手动重试",
        "TRANSIENT_PROVIDER_ERROR" => "临时服务错误；达到3次上限后需手动重试",
        "INVALID_MODEL_OUTPUT" => "课件格式或来源无效，未发布；可手动重试",
        "INTERRUPTED_MAY_BE_BILLED" => "上次运行中断，可能已计费；请手动重试",
        "RUN_CANCELLED" => "已取消，迟到内容不会发布",
        "MODE_CHANGED" => "切换点击模式后取消了待派发自动任务",
        _ => code ?? ""
    };
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
