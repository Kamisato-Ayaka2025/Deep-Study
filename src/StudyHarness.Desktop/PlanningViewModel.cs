using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Desktop;

public sealed record DraftChoice(PlanningDraft Draft)
{
    public string Label => $"{(Draft.Confirmed ? "已确认" : "草稿")} · {Draft.Plan?.Candidate.Title ?? Draft.Input.Request} · r{Draft.Revision}";
}

public sealed class PlanningViewModel(PlanningService service, ILearningStore? learning = null, WebLearningService? web = null, IPlanningSessionStore? sessionStore = null) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private bool browserOnly;
    public PlanningViewModel CreateBrowser() => new(service, learning) { browserOnly = true };
    private bool restoring, recoveryFailed, referencesEdited;
    private IReadOnlyList<SourceRef> localReferences = [];
    public IReadOnlyList<SourceRef> LocalReferences => localReferences;
    public string SavedOcrPages { get; private set; } = "";
    public Guid? SavedOcrCollectionId { get; private set; }
    public IReadOnlyList<Guid> ExamCollectionIds { get; private set; } = [];
    public void SetExamCollections(IReadOnlyList<Guid> ids)
    {
        ExamCollectionIds = ids.Distinct().ToArray(); referencesEdited = true; review = null; Preview = ""; AckFrozen = false; Persist(); RefreshFlags();
    }
    public IReadOnlyList<Guid> PendingCollections { get; private set; } = [];
    public void SetPendingCollections(IReadOnlyList<Guid> ids)
    {
        PendingCollections = ids.ToArray(); referencesEdited = true; review = null; Preview = ""; AckFrozen = false; Persist(); RefreshFlags();
    }
    public void ReportImportStatus(string message) => Status = message;
    public void SetImportState(string pages, Guid? collection)
    {
        SavedOcrPages = pages; SavedOcrCollectionId = collection; Persist();
    }
    public string RecoveryStatus { get; private set; } = "";
    public void SetReferences(IReadOnlyList<SourceRef> references)
    {
        localReferences = references.ToArray(); referencesEdited = true; review = null; Preview = ""; AckFrozen = false;
        Persist(); RefreshFlags();
    }
    private void Persist()
    {
        if (sessionStore is null || restoring || current?.Confirmed == true) return;
        try
        {
            sessionStore.Save(new(current?.Id, Request, Background, Goal, Hours, WebTopic, WebSite,
                ClarificationAnswer, localReferences, webReferences, WebReport, SavedOcrPages, SavedOcrCollectionId, PendingCollections, ExamCollectionIds));
            recoveryFailed = false; RecoveryStatus = "已自动保存";
        }
        catch (Exception) { recoveryFailed = true; RecoveryStatus = "自动保存失败，请保留当前窗口并检查数据目录。"; }
        Notify(nameof(RecoveryStatus));
    }
    public async Task RestoreSessionAsync()
    {
        if (sessionStore is null) { await LoadAsync(); return; }
        restoring = true;
        try
        {
            var saved = sessionStore.Load();
            await LoadAsync();
            var draft = Drafts.FirstOrDefault(d => d.Draft.Id == saved?.DraftId);
            if (saved is null) { Selected = Drafts.FirstOrDefault(d => !d.Draft.Confirmed); if (Selected is null) NewDraft(); else localReferences = Selected.Draft.Input.PreferredReferences; }
            else if (draft?.Draft.Confirmed == true) NewDraft();
            else
            {
                if (draft is not null) Selected = draft; else NewDraft();
                request = saved.Request; background = saved.Background; goal = saved.Goal;
                Hours = saved.Hours; webTopic = saved.WebTopic; webSite = saved.WebSite;
                clarificationAnswer = saved.Clarification; localReferences = saved.References;
                SavedOcrPages = saved.OcrPages; SavedOcrCollectionId = saved.OcrCollectionId;
                PendingCollections = saved.PendingCollections ?? [];
                ExamCollectionIds = saved.ExamCollectionIds ?? draft?.Draft.Input.ExamCollectionIds ?? [];
                webReferences = saved.WebReferences; webReport = saved.WebReport; referencesEdited = current is not null && !localReferences.SequenceEqual(current.Input.PreferredReferences);
                review = null; Preview = ""; AckFrozen = false;
                NotifyAll(); Notify(nameof(WebTopic)); Notify(nameof(WebSite)); Notify(nameof(WebReport)); Notify(nameof(ClarificationAnswer));
            }
        }
        catch (Exception) { recoveryFailed = true; RecoveryStatus = "草稿恢复失败，未覆盖恢复记录；请检查数据目录。"; Notify(nameof(RecoveryStatus)); }
        finally { restoring = false; }
        if (!recoveryFailed) Persist();
    }
    public async Task FormSubmissionAsync()
    {
        if (Busy) return;
        Persist();
        if (recoveryFailed) { Status = RecoveryStatus; return; }
        if (Busy) { Status = "正在处理，请等待完成或取消。"; return; }
        if (PendingCollections.Count > 0) { Status = "教材扫描尚未完成，可能尚无可读正文。请点击继续扫描，完成后形成发送内容；也可点击×移出本次计划，已扫描结果会保留。"; return; }
        if (string.IsNullOrWhiteSpace(Request) && localReferences.Count > 0)
            Request = "学习这些资料：" + string.Join("、", localReferences.Select(r => r.Locator.SourceLabel).Distinct());
        if (!string.IsNullOrWhiteSpace(ClarificationAnswer) && HasClarifications) { await SaveClarificationAsync(); return; }
        if (CanRetryPlan)
        {
            const string hint = "资料预览已保留。请点击下方“携带错误信息重试”，将自动附上上次候选及校验错误；此处不会发送请求。";
            if (!Status.Contains(hint, StringComparison.Ordinal)) Status += "\n" + hint;
            return;
        }
        Status = "正在形成发送内容，请稍候。";
        if (Dirty || !localReferences.All(r => current!.Input.PreferredReferences.Contains(r))) await SaveAsync(localReferences);
        if (!Dirty) await PrepareAsync();
    }
    private PlanningDraft? current;
    private PlanningReview? review;
    private DraftChoice? selected;
    private string request = "", background = "", goal = "", minutes = "", scope = "Adaptive";
    private string status = "输入学习请求；已知信息直接填写，缺失项由模型提取或明确列为默认值。", preview = "", summary = "尚无候选计划。", detail = "选择课程节点查看目标。";
    private bool busy, ackFrozen;
    private string hours = "";
    private CancellationTokenSource? cancellation;
    private string clarificationAnswer = "";
    public string ClarificationAnswer { get => clarificationAnswer; set { clarificationAnswer = value; Notify(); AckFrozen = false; RefreshFlags(); Persist(); } }
    public string[] ClarificationQuestions => current?.Plan is { } p
        ? p.Candidate.Clarifications.Concat(p.Candidate.Strategy?.Clarifications ?? []).Distinct().ToArray() : [];
    public bool HasClarifications => ClarificationQuestions.Length > 0 && current?.Confirmed != true;
    public bool CanClarify => CanEdit && HasClarifications;
    public bool CanSendPlan => CanEdit && PendingCollections.Count == 0 && review is not null && !review.CanRetry && !Dirty;
    public bool CanRetryPlan => CanEdit && review?.CanRetry == true && !Dirty;
    public bool CanSubmitPlan => CanSendPlan || CanRetryPlan;
    public string SubmitPlanLabel => CanRetryPlan ? "携带错误信息重试" : "发送并拟定计划";
    public bool CanOpenClarification => CanClarify || CanRetryPlan;
    public Task RetryPlanAsync() => CanRetryPlan ? ProposeAsync(true) : Task.CompletedTask;
    public string ConfirmationHint => Busy ? "正在处理请求，请等待或取消。"
        : current?.Confirmed == true ? "此计划已确认冻结。"
        : !string.IsNullOrWhiteSpace(ClarificationAnswer) ? "已保留补充回答：请点击“形成发送内容”，核对后发送。"
        : Dirty ? "请先形成发送内容，核对后发送并拟定计划。"
        : CanRetryPlan ? "上次计划未通过校验。可携带错误信息重发尝试，通过后再核对完整计划。"
        : current?.Plan is null ? "尚无候选计划：请先预览外发并拟定计划。"
        : HasClarifications ? "请展开“补充计划信息”回答问题，再形成发送内容并重新拟定。"
        : review is not null ? "有待处理的外发预览：请完成或拒绝外发后核对计划。"
        : current.Plan.Candidate.Scope == "OutOfScope" ? "当前计划超出支持范围，请修正学习请求。"
        : !AckFrozen ? "请核对完整计划后勾选下方确认框。"
        : "条件已满足，可以确认并冻结计划。";
    public async Task SaveClarificationAsync()
    {
        if (!CanEdit || !HasClarifications) return;
        if (string.IsNullOrWhiteSpace(ClarificationAnswer)) { Status = "请填写补充回答；也可明确写出接受哪些默认值。"; return; }
        var references = current!.Input.PreferredReferences.Concat(localReferences).Concat(webReferences).Distinct().ToArray();
        var supplement = "\n\n补充澄清：\n" +
            string.Join("\n", ClarificationQuestions.Select((q, i) => $"{i + 1}. {q}")) + "\n我的回答：\n" +
            ClarificationAnswer.Trim();
        var updated = Request.EndsWith(supplement, StringComparison.Ordinal) ? Request : Request.Trim() + supplement;
        if (updated.Length > 8000) { Status = "补充后学习请求超过8000字符，请精简回答或原请求；回答尚未保存。"; return; }
        Request = updated;
        await SaveAsync(references);
        if (current?.Input.Request == updated && current.Plan is null)
            await PrepareAsync();
    }
    private string webTopic = "", webSite = "";
    public string WebTopic { get => webTopic; set { webTopic = value; Notify(); Persist(); } }
    public string WebSite { get => webSite; set { webSite = value; Notify(); Persist(); } }
    private string webReport = "尚未取得网页资料。";
    public string WebReport { get => webReport; private set { webReport = value; Notify(); } }
    private IReadOnlyList<SourceRef> webReferences = [];
    public Task AcquireWebAsync() => Run(async ct =>
    {
        if (web is null || current?.Confirmed == true) { Status = "联网获取不可用或计划已冻结。"; return; }
        var query = string.IsNullOrWhiteSpace(WebTopic) ? string.Join(" ", new[] { Request, Goal }.Where(s => !string.IsNullOrWhiteSpace(s))) : WebTopic;
        try
        {
            var result = await web.AcquireAsync(query, WebSite, true, new Progress<string>(s => { if (Busy && cancellation?.Token == ct) Status = s; }), ct);
            webReferences = webReferences.Concat(result.References).Distinct().ToArray(); review = null;
            AckFrozen = false; RefreshFlags();
            WebReport = result.Report;
            Preview = "学习资料已准备好；保存草稿后可查看计划外发范围与正文。";
            Status = "网页正文已保存并绑定当前草稿，可继续导入文件或形成发送内容。"; Persist();
        }
        catch (InvalidOperationException e) { Status = e.Message; }
    });
    public SnapshotCollection<DraftChoice> Drafts { get; } = [];
    public SnapshotCollection<CourseNodeView> Nodes { get; } = [];
    public LabPreviewModel? LabPreview(string key)
    {
        var node = current?.Confirmed == true ? current.Plan?.Candidate.Nodes.FirstOrDefault(n => n.Key == key && n.Type == "lab") : null;
        return node is null ? null : LabPreviewModel.From(node, Nodes.FirstOrDefault(n => n.Key == key)?.Eligible == true);
    }
    public string[] Scopes { get; } = ["Auto", "Math", "Concepts", "SimpleC", "CppAlgorithms", "OutOfScope"];
    public DraftChoice? Selected { get => selected; set { if (ReferenceEquals(selected, value)) return; selected = value; if (value is not null) Apply(value.Draft); Notify(); } }
    public string Request { get => request; set { if (request == value) return; request = value; if (sessionStore is null) ResetWeb(); Edited(); } }
    public string Background { get => background; set { if (background == value) return; background = value; Edited(); } }
    public string Goal { get => goal; set { if (goal == value) return; goal = value; if (sessionStore is null) ResetWeb(); Edited(); } }
    public string Minutes { get => minutes; set { if (minutes == value) return; minutes = value; Edited(); } }
        public string Hours
    {
        get => hours;
        set
        {
            if (value == hours) return;
            hours = value;
            minutes = string.IsNullOrWhiteSpace(value) ? "" :
                decimal.TryParse(value, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var h) &&
                StudyHarness.Domain.WeeklyStudyTime.TryMinutes(h, out var m) ? m.ToString() : "invalid-hours";
            Edited();
        }
    }
    public string Scope { get => scope; set { if (scope == value) return; scope = value; Edited(); } }
    public string Status { get => status; private set { status = value; Notify(); } }
    public string Preview { get => preview; private set { if (preview == value) return; preview = value; Notify(); } }
    public string Summary { get => summary; private set { if (summary == value) return; summary = value; Notify(); } }
    public string Detail { get => detail; private set { detail = value; Notify(); } }
    public bool Busy { get => busy; private set { busy = value; Notify(); RefreshFlags(); } }
    public bool CanOperate => !Busy;
    public bool CanEdit => !Busy && current?.Confirmed != true;
    public bool CanConfirm => CanEdit && PendingCollections.Count == 0 && current?.Plan is not null && review is null && !Dirty && AckFrozen &&
        current.Plan.Candidate.Clarifications.Count == 0 && (current.Plan.Candidate.Strategy?.Clarifications.Count ?? 0) == 0 && current.Plan.Candidate.Scope != "OutOfScope";
    public bool AckFrozen { get => ackFrozen; set { ackFrozen = value; Notify(); Notify(nameof(CanConfirm)); Notify(nameof(ConfirmationHint)); } }
    public string Identity => current is null ? "新学科草稿" : $"{current.Id} · 修订 {current.Revision} · {(current.Confirmed ? "已确认冻结" : "待确认")}";
    private bool Dirty => referencesEdited || !string.IsNullOrWhiteSpace(ClarificationAnswer) || webReferences.Count > 0 || current is null || Request != current.Input.Request || Background != (current.Input.Background ?? "") ||
        Goal != (current.Input.Goal ?? "") || Minutes != current.Input.WeeklyMinutes?.ToString() && !(Minutes == "" && current.Input.WeeklyMinutes is null) || Scope != current.Input.Scope;

    private void Edited([CallerMemberName] string? name = null)
    {
        Notify(name); review = null; Preview = ""; AckFrozen = false; RefreshFlags(); Persist();
    }
    public void NewDraft()
    {
        if (Busy) return;
        current = null; selected = null; request = background = goal = minutes = hours = ""; scope = "Adaptive";
        clarificationAnswer = ""; Notify(nameof(ClarificationAnswer));
        localReferences = []; referencesEdited = false; ResetWeb(); webSite = ""; Notify(nameof(WebSite));
        SavedOcrPages = ""; SavedOcrCollectionId = null; PendingCollections = []; ExamCollectionIds = [];
        review = null; Preview = ""; Summary = "尚无候选计划。"; Nodes.Clear(); AckFrozen = false;
        Status = "输入新的学习目标；资料只用于当前计划。";
        NotifyAll(); Persist();
    }
    public Task LoadAsync() => Run(async ct => await Reload(current?.Id, ct));
    public async Task RefreshAfterDeletionAsync()
    {
        await LoadAsync();
        NewDraft();
        Status = "学科已删除；可输入新的学习目标，或选择其他学科。";
    }
    private void ResetWeb()
    {
        webReferences = []; webTopic = ""; Notify(nameof(WebTopic));
        WebReport = "尚未取得当前目标的网页资料。";
    }
    private async Task Reload(Guid? id, CancellationToken ct)
    {
        var drafts = await Task.Run(() => service.ListAsync(ct), ct);
        var previous = Drafts.ToDictionary(d => d.Draft.Id);
        Drafts.Replace(drafts.Select(d => previous.TryGetValue(d.Id, out var old) && old.Draft.Revision == d.Revision &&
            old.Draft.Confirmed == d.Confirmed && old.Draft.Plan?.Hash == d.Plan?.Hash ? old : new DraftChoice(d)));
        var choice = Drafts.FirstOrDefault(d => d.Draft.Id == id) ?? Drafts.FirstOrDefault();
        // Only the read-only course browser may skip reapplying an unchanged selection.
        // An explicit editor reload must still discard unsaved form edits and reset confirmation.
        if (!browserOnly && choice is not null && ReferenceEquals(Selected, choice)) Apply(choice.Draft);
        else Selected = choice;
    }
    public Task SaveAsync(IReadOnlyList<SourceRef> references) => Run(async ct =>
    {
        int? weekly = null;
        if (!string.IsNullOrWhiteSpace(Minutes))
        {
            if (!int.TryParse(Minutes, out var value)) { Status = "VALIDATION_ERROR：每周小时须大于0且不超过168，并精确对应整数分钟（例如0.05小时=3分钟）。"; return; }
            weekly = value;
        }
        var input = new PlanningInput(Request.Trim(), string.IsNullOrWhiteSpace(Background) ? null : Background,
            string.IsNullOrWhiteSpace(Goal) ? null : Goal, weekly, Scope, references.Concat(webReferences).Distinct().ToArray(), ExamCollectionIds.Count == 0 ? null : ExamCollectionIds.ToArray());
        // Restored drafts keep their versioned sources unless the user explicitly selects replacements.
        if (sessionStore is null && !referencesEdited && input.PreferredReferences.Count == 0 && current is not null &&
            input.Request == current.Input.Request && input.Goal == current.Input.Goal)
            input = input with { PreferredReferences = current.Input.PreferredReferences };
        var result = current is null ? await service.StartAsync(new(Guid.NewGuid(), 0, input), ct) :
            await service.ReviseAsync(new(Guid.NewGuid(), current.Revision, new(current.Id, input)), ct);
        if (!result.Ok) { Show(result.Error!); return; }
        webReferences = []; localReferences = input.PreferredReferences; referencesEdited = false; review = null; Preview = "请形成发送内容后再发送。";
        await Reload(result.Value!.Id, ct); Status = "草稿已保存，旧候选已失效；可预览并拟定完整计划。";
    });
    public Task PrepareAsync() => Run(async ct =>
    {
        if (PendingCollections.Count > 0) { Status = "扫描教材尚无正文，请先识别或移除。"; return; }
        if (current is null || Dirty) { Status = "请先保存当前输入；未保存的内容不会发送。"; return; }
        var draft = current;
        var result = await Task.Run(() => service.PrepareAsync(draft.Id, draft.Revision, ct), ct);
        if (!result.Ok) { Show(result.Error!); return; }
        var prepared = result.Value!;
        var text = await Task.Run(() =>
        {
            var r = prepared.Review;
            var preview = $"发送至：{r.Destination}\n模型：{r.Model}\n计划模式：JSON输出，temperature=0，关闭思考模式以保留完整计划的输出预算\n最大输出：{r.MaxOutputTokens} tokens（实际用量以服务返回为准）\n" +
                $"共{r.Sources.Count}项 / UTF-8 {r.Sources.Sum(s => Encoding.UTF8.GetByteCount(s.Text))}字节\n\n" + string.Join("\n\n", r.Sources.Select(s => $"[{s.Id} / {s.Kind}]\n{s.Text}"));
            if (r.Budget is { } budget) preview += $"\n\n本次宿主已装配固定目录和原文；最多{budget.MaxDispatches}次派发（临时错误与内容修复共用）；不合规时会向同一模型发送上次候选及具体校验错误，修复后仍须完整校验。目标{budget.TargetSeconds}秒，可随时取消。" +
                (r.Capabilities?.Verified == true ? "" : "\n模型能力待刷新，已采用保守预算。 ");
            else preview += "\n\n按需读取范围：以下正文不会全部随首个请求发送，仅在模型调用时返回；最多8轮、12次工具调用、65536字符工具结果，总输出8192 tokens。目录是导入时的固定版本；重新导入/换范围需重新保存并预览。\n" +
                string.Join("\n\n", r.ReadableSources.Select(s => $"[{s.Id}] {s.Label}\n{s.Text}"));

            if (prepared.KnowledgeBatches.Count > 0)
            {
                preview += $"\n\n资料知识梳理：{prepared.KnowledgeBatches.Count}批。本地教材漏标原文保留供后续理解；网页广告/非教学内容排除，未分类段落不自动作为教学知识，缓存状态保留未完成；不为编号遗漏重发。格式或引用无效时最多一次修复，收到的回复与已完成批次均保留复用；失败/取消后重新预览可续做。首次读取以下全部已取得正文，随后按全部知识标题编排课程，再每40个知识点发送完整说明和课程结构逐项分配（每批最多一次修复）。总费用包含梳理、编排及分配；成功结果缓存复用。知识清单为模型提取，需人工核对遗漏。\n" +
                    string.Join("\n\n", prepared.KnowledgeBatches.Select(b => $"批次 {b.Key} / 最大输出{b.Review.MaxOutputTokens} tokens\n" + string.Join("\n", b.Review.Sources.Select(s => s.Text))));
            }
            return preview;
        }, ct);
        ct.ThrowIfCancellationRequested();
        review = prepared; Preview = text;
        Status = "核对完整外发范围后同意或拒绝；此次只拟定计划，不生成正文。"; RefreshFlags();
    });
    public Task ProposeAsync(bool allow) => Run(async ct =>
    {
        if (!allow) { review = null; Preview = ""; Status = "已拒绝发送；输入和资料已保留。"; return; }
        if (review is null || Dirty || PendingCollections.Count > 0) { Status = "CONSENT_REQUIRED：请先预览已保存草稿。"; return; }
        using var notice = new CancellationTokenSource();
        var delayed = DelayNotice(notice.Token);
        Result<PlanningDraft> result;
        var prepared = review;
        void OnProgress(string message) => Status = message;
        service.Progress += OnProgress;
        try { result = await Task.Run(() => service.ProposeAsync(prepared, allow, ct), ct); }
        finally { service.Progress -= OnProgress; notice.Cancel(); await delayed; }
        if (!result.Ok)
        {
            if (result.Error!.Code != ErrorCode.INVALID_MODEL_OUTPUT || !review.CanRetry) review = null;
            Show(result.Error!);
            if (review?.CanRetry == true) Status += "\n可点击“携带错误信息重试”：复用本次资料和回答，自动附上失败候选及校验错误；每次点击重新授权一轮，最多3次派发、900秒，可取消。";
            return;
        }
        review = null;
        await Reload(result.Value!.Id, ct);
        Status = "候选计划已保存。检查默认值、问题、周安排、来源与实验方案；有修改请保存后重新拟定。";
    });
    private async Task DelayNotice(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(600), ct); Status = "计划生成比预期久，仍在处理；可取消并保留草稿。"; }
        catch (OperationCanceledException) { }
    }
    public Task ConfirmAsync() => Run(async ct =>
    {
        if (current?.Plan is null || Dirty || !AckFrozen || PendingCollections.Count > 0) { Status = "请核对当前完整计划并勾选冻结提示。"; return; }
        var result = await service.ConfirmAsync(new(Guid.NewGuid(), current.Revision, new(current.Id, current.Plan.Hash, AckFrozen)), ct);
        if (!result.Ok) { Show(result.Error!); return; }
        await Reload(result.Value!.Id, ct); Status = "计划已确认冻结。可进入理论学习或生成实验材料；实验由你自行测评。";
    });
    public void SelectNode(CourseNodeView node)
    {
        Detail = $"{node.Title} · {node.Type} · 预计{node.Minutes}分钟\n目标：{node.Objectives}\n建议先学：{string.Join("、", node.Prerequisites)}\n{node.Status}\n确认学科后可打开理论学习窗口；实验可打开材料窗口，自行测评。";
    }
    public Guid? ConfirmedSubject => current?.Confirmed == true ? current.Id : null;
    public async Task RefreshProgressAsync()
    {
        if (learning is null || current?.Confirmed != true || current.Plan is null) return;
        var draft = current;
        var original = Nodes.ToArray();
        var updated = await Task.Run(async () =>
        {
            var completed = await learning.CompletedNodesAsync(draft.Id);
            var result = new List<CourseNodeView>();
            foreach (var node in original)
            {
                // Completed nodes need no material projection. Lab status never needs a theory reader.
                var ready = !completed.Contains(node.Key) && (node.Type == "lab"
                    ? learning is ILabStore labStore && await labStore.GetLabAsync(draft.Id, node.Key) is not null
                    : (await learning.GetLessonAsync(draft.Id, node.Key)).Ok);
                result.Add(node with { Eligible = true, Status = node.Type == "lab"
                    ? completed.Contains(node.Key) ? "用户确认完成 · 软件未验证" : ready ? "材料就绪 · 自行测评" : "可自由生成实验材料"
                    : completed.Contains(node.Key) ? "已完成" : ready ? "正文就绪" : "可自由生成理论课" });
            }
            return result;
        });
        if (!ReferenceEquals(current, draft) || !Nodes.SequenceEqual(original)) return;
        Nodes.Replace(updated);
    }
    private void Apply(PlanningDraft draft)
    {
        clarificationAnswer = ""; Notify(nameof(ClarificationAnswer));
        if (sessionStore is null && current?.Id != draft.Id) { ResetWeb(); webSite = ""; Notify(nameof(WebSite)); }
        webReferences = [];
        ExamCollectionIds = draft.Input.ExamCollectionIds ?? [];
        current = draft; request = draft.Input.Request; background = draft.Input.Background ?? ""; goal = draft.Input.Goal ?? "";
        minutes = draft.Input.WeeklyMinutes?.ToString() ?? "";
        hours = draft.Input.WeeklyMinutes is > 0 and <= 10080 ? draft.Input.WeeklyMinutes.Value % 3 == 0 ? (draft.Input.WeeklyMinutes.Value / 60m).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : StudyHarness.Domain.WeeklyStudyTime.Format(draft.Input.WeeklyMinutes.Value) : ""; scope = draft.Input.Scope; review = null; ackFrozen = false;
        Preview = "请预览当前草稿的完整请求和资料后再发送。";
        var nodes = new List<CourseNodeView>();
        if (draft.Plan is { } document)
        {
            var p = document.Candidate; var graph = PlanRules.Graph(draft.Id, p);
            Summary = $"{p.Title}\n目标：{p.Goal}\n基础：{p.Background}\n范围：{p.Scope}；每周{StudyHarness.Domain.WeeklyStudyTime.Format(p.WeeklyMinutes)}\n\n" +
                (p.Strategy is { } strategy ? $"主题：{strategy.Domain}\n策略：{string.Join("、", strategy.TeachingModes)}\n理由：{strategy.Reason}\n限制：{string.Join("；", strategy.Limitations)}\n待澄清：{string.Join("；", strategy.Clarifications)}\n\n" : "") + "默认值（可在确认前修正）：\n" + (p.Defaults.Count == 0 ? "无；已知信息不重复询问。" : string.Join("\n", p.Defaults.Select(d => $"{d.Field} = {d.Value}（{d.Reason}）"))) +
                "\n\n教学覆盖与节奏：\n" + PlanTeachingCoverage.Describe(document) +
                "\n\n待澄清：\n" + (p.Clarifications.Count == 0 ? "无" : string.Join("\n", p.Clarifications)) +
                "\n\n限制：\n" + string.Join("\n", p.Limitations) + "\n实验仅交付材料，由你自行编写与测评；软件不运行代码。\n\n周安排：\n" +
                string.Join("\n", document.Weeks.Select(w => $"第{w.Index}周：{string.Join("、", w.NodeKeys)}，预计{w.EstimatedMinutes}分钟")) +
                "\n\n完整目录与实验方案：\n" + string.Join("\n\n", p.Nodes.Select(n => $"{n.Key} · {n.Title} · {n.Type} · {n.EstimatedMinutes}分钟\n目标：{string.Join("；", n.Objectives)}\n建议先学：{string.Join("、", n.PrerequisiteKeys)}" +
                    (n.Lab is { } lab ? $"\n实验：{lab.Mode}；前课={lab.PreviousLabKey ?? "无"}；{(lab.Toolchain switch { "portable-cpp" => "C++算法题", "paper-exercise" => "纸笔练习", "manual-project" => "外部操作项目", _ => "C实践" })}材料（不依赖本机工具链）；素材={string.Join(",", lab.SourceKeys)}\n理由：{lab.Rationale}\n连续实验由你自行衔接前课代码；原始材料可重复导出。" : ""))) +
                "\n\n算法→理论课→实验题（确认前核对全集）：\n" + PlanRules.AlgorithmCoverage(p) + "\n\n固定来源：\n" + string.Join("\n", document.Sources.Select(s => $"{s.Key}: {s.Reference.Locator.SourceLabel} / {s.Reference.Locator.Heading} / {s.Reference.Locator.Page} / {s.Reference.Locator.Block}\n库={s.Reference.LibraryId} 版本={s.Reference.VersionId} hash={s.Reference.ContentHash}")) +
                "\n\n教材目录覆盖（未取得正文不表示已读）：\n" + CoverageSummary(document) +
                $"\n\n计划hash：{document.Hash}\n" + (document.Usage is { } usage ? $"实际用量：输入{usage.PromptTokens}，输出{usage.CompletionTokens}，合计{usage.TotalTokens} tokens。" : "服务未返回用量。") +
                $"\n按需读取：{document.ToolCalls}次工具调用；已读id：{string.Join(", ", document.ReadSourceIds ?? [])}\n" + "\n确认后目标、基础、时间、周安排、课程图及实验方案不可修改，请谨慎核对。可通过课程页删除整个学科。";
            foreach (var node in p.Nodes)
            {
                var availability = graph.GetAvailability(PlanRules.NodeId(draft.Id, node.Key), new HashSet<Guid>(), new(draft.Confirmed, true, false, false, false));
                nodes.Add(new(node.Key, node.Title, node.Type, string.Join("；", node.Objectives), node.EstimatedMinutes,
                    graph.Layers[PlanRules.NodeId(draft.Id, node.Key)], node.PrerequisiteKeys, availability.CanGenerate,
                    !draft.Confirmed ? "待确认 · 无正文" : node.Type == "lab" ? "实验材料 · 自行测评" : availability.CanGenerate ? "可自由生成理论课" : "尚无正文"));
            }
        }
        else Summary = "尚无候选计划，请预览外发内容并拟定。";
        Nodes.Replace(nodes);
        Detail = "选择图中的课程按钮查看标题和目标。"; NotifyAll();
    }
    public void Cancel() => cancellation?.Cancel();
    private static string CoverageSummary(PlanDocument document)
    {
        if (document.Collections is not { Count: > 0 }) return "本计划未使用教材集合目录。";
        var coverage = document.Candidate.Coverage!.ToDictionary(c => c.EntryKey);
        return string.Join("\n", document.Collections.SelectMany((c, i) => c.Manifest.Entries.Select(entry =>
        {
            var item = coverage[AdaptivePlanRules.DirectoryKey(i, entry.Key)];
            var state = item.State == "included" ? "纳入：" + string.Join("、", item.NodeKeys) : item.State == "excluded" ? "排除：" + item.Reason : "缺口：" + item.Reason;
            return $"{entry.Heading} · {state} · {(entry.State is "textReady" or "ocrReady" ? "已取得正文" : "正文未取得")}" + (entry.QualityFlags.Contains("visualReviewRequired") ? " · 图文待复核" : "");
        })));
    }
    private void Show(ApiError error) => Status = $"{error.Code}：{error.Message}";
    private async Task Run(Func<CancellationToken, Task> action)
    {
        if (Busy) return; Busy = true; cancellation = new();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { Status = "已取消；没有发布迟到计划。"; }
        catch (Exception) { Status = "IO_FAILURE：操作未完成，请检查输入、资料库及本地数据后重新载入。"; }
        finally { cancellation.Dispose(); cancellation = null; Busy = false; Persist(); }
    }
    private void NotifyAll()
    {
        foreach (var property in new[] { nameof(Request), nameof(Background), nameof(Goal), nameof(Minutes), nameof(Hours), nameof(Scope), nameof(Identity), nameof(Selected), nameof(AckFrozen) }) Notify(property);
        RefreshFlags();
    }
    private void RefreshFlags() { Notify(nameof(CanOperate)); Notify(nameof(CanEdit)); Notify(nameof(CanConfirm)); Notify(nameof(ConfirmationHint)); Notify(nameof(HasClarifications)); Notify(nameof(ClarificationQuestions)); Notify(nameof(CanClarify)); Notify(nameof(CanSendPlan)); Notify(nameof(CanRetryPlan)); Notify(nameof(CanSubmitPlan)); Notify(nameof(SubmitPlanLabel)); Notify(nameof(CanOpenClarification)); }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
