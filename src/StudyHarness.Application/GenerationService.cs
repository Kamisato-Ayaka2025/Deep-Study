using System.Text.Json;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

public sealed class TheoryReview
{
    internal TheoryReview(Guid subject, string planHash, string model, ConsentMode consentMode, Dictionary<string, ModelReview> reviews, PlanSource[] sources,
        Dictionary<string, IReadOnlyList<ModelSource>> materials)
        => (SubjectId, PlanHash, Model, ConsentMode, Reviews, Sources, Materials) = (subject, planHash, model, consentMode,
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, ModelReview>(reviews), Array.AsReadOnly(sources),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<ModelSource>>(materials));
    public Guid SubjectId { get; }
    public string PlanHash { get; }
    public string Model { get; }
    public ConsentMode ConsentMode { get; }
    public IReadOnlyDictionary<string, ModelReview> Reviews { get; }
    internal IReadOnlyList<PlanSource> Sources { get; }
    internal IReadOnlyDictionary<string, IReadOnlyList<ModelSource>> Materials { get; }
    internal Dictionary<string, IReadOnlyList<BookKnowledgeBatch>> WebBatches { get; } = [];
    public string Preview => "范围：本次列出的课程、固定资料片段与模型；课文至多3次派发，仅临时错误重试。取消不退还已消耗额度。授权不跨重启保留。\n" +
        (WebBatches.Count == 0 ? "" : $"网页知识整理：共{WebBatches.Values.Sum(b => b.Count)}批，复用已有结果；未缓存批次每批至多2次派发（含一次格式修复）。广告/非教学和未分类段落不作为课程知识；仅下列已冻结网页正文进入整理。\n") +
        string.Join("\n\n", Reviews.Select(pair => $"课程 {pair.Key} → {pair.Value.Destination} / {Model}\n按本课目标选择完整原文和必要前置；未选内容不外发。原文及指令共{pair.Value.Sources.Sum(s => s.Text.Length)}字符，完整请求预算由模型能力单独校验。\n" +
            string.Join("\n", pair.Value.Sources.Select(s => $"[{s.Id}] {s.Label}\n{s.Text}"))));
}

public sealed class GenerationService(IPlanningStore planning, ILearningStore learning, IGenerationStore queue,
    IKnowledgeStore knowledge, SettingsService settings, ModelEgress egress, ITheoryLessonParser parser, bool legacyAutoRegression = false, IBookKnowledgeCache? knowledgeCache = null)
{
    private readonly SemaphoreSlim worker = new(1, 1);
    private readonly SemaphoreSlim userRequest = new(1, 1);
    private sealed class Grant(TheoryReview preview, ModelConsent consent)
    {
        public TheoryReview Preview { get; } = preview;
        public ModelConsent Consent { get; } = consent;
        public Guid? TaskId { get; set; }
        public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    }
    private readonly Dictionary<(Guid, string), Grant> grants = [];
    private readonly object grantGate = new();
    private readonly object runningGate = new();
    private Guid? runningId;
    private CancellationTokenSource? running;
    public IGenerationStore Queue => queue;
    public ILearningStore Learning => learning;
    public IPlanningStore Planning => planning;
    public event Action? Changed;
    public async Task<bool> HasApprovalAsync(GenerationTask task)
    {
        var config = await settings.GetAsync();
        lock (grantGate) return grants.TryGetValue((task.SubjectId, task.NodeKey), out var grant) && grant.Preview.Model == config.Model &&
            grant.Preview.ConsentMode == config.ConsentMode && (grant.TaskId is null || grant.TaskId == task.Id);
    }

    // The explicit Generate button approves only this course's bounded request and retry budget.
    public async Task<Result<GenerationTask>> GenerateAsync(Guid subjectId, string nodeKey, bool regenerate = false, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        using var preparationDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        preparationDeadline.CancelAfter(TimeSpan.FromSeconds(600)); ct = preparationDeadline.Token;
        await userRequest.WaitAsync(ct);
        try
        {
            var active = (await queue.TasksAsync(ct)).LastOrDefault(t => t.SubjectId == subjectId && t.NodeKey == nodeKey && t.Status is GenerationStatus.Queued or GenerationStatus.Running);
            if (active?.Status == GenerationStatus.Running) return Result<GenerationTask>.Success(active);
            var preview = await PrepareAsync(subjectId, nodeKey, ct);
            if (!preview.Ok) return new(false, null, preview.Error);
            var request = active is not null ? Result<GenerationTask>.Success(active) : await queue.RequestAsync(subjectId, new(Guid.NewGuid(), 0, new(nodeKey, regenerate ? GenerationTrigger.Regenerate : GenerationTrigger.ManualRetry)), ct);
            if (!request.Ok) return request;
            Authorize(preview.Value!, true);
            lock (grantGate) { grants[(subjectId, nodeKey)].TaskId = request.Value!.Id; grants[(subjectId, nodeKey)].StartedAt = started; }
            Changed?.Invoke(); return request;
        }
        finally { userRequest.Release(); }
    }

    public async Task<Result<TheoryReview>> PrepareAsync(Guid subjectId, string? onlyNode, CancellationToken ct = default)
    {
        var draft = await planning.GetAsync(subjectId, ct);
        if (draft?.Confirmed != true || draft.Plan is null) return Result<TheoryReview>.Fail(ErrorCode.PLAN_NOT_CONFIRMED, "先确认学习计划。");
        var config = await settings.GetAsync(ct);
        if (onlyNode is null && config.ConsentMode == ConsentMode.Each)
            return Result<TheoryReview>.Fail(ErrorCode.CONSENT_REQUIRED, "当前为逐任务授权，请预览本课。若要批量自动生成，请在设置中选择Once，再明确预览并同意全学科范围。");
        var adaptive = draft.Plan.Candidate.SchemaVersion == 2;
        var allSources = new List<PlanSource>();
        var reviews = new Dictionary<string, ModelReview>();
        var originalMaterials = new Dictionary<string, IReadOnlyList<ModelSource>>();
        var webBatches = new Dictionary<string, IReadOnlyList<BookKnowledgeBatch>>();
        foreach (var node in draft.Plan.Candidate.Nodes.Where(n => n.Type is ("theory" or "quiz") && (onlyNode is null || n.Key == onlyNode)))
        {
            var materials = new List<ModelSource>(); var sources = new List<PlanSource>(); var budget = adaptive ? CourseMaterials.MaximumLessonCharacters : 12000;
            IReadOnlyList<PlanSource> selected;
            try { selected = await CourseMaterials.SelectAsync(knowledge, draft.Plan, node, ct); }
            catch (SourcePreparationException e)
            { return Result<TheoryReview>.Fail(ErrorCode.SOURCE_UNAVAILABLE, e.Message); }
            catch (Exception e) when (e is IOException or ArgumentException)
            { return Result<TheoryReview>.Fail(ErrorCode.SOURCE_UNAVAILABLE, "冻结章节不可访问或超出预算；未替换或截断来源。"); }
            if (knowledgeCache is not null && knowledge is IWebPageKnowledge web)
            {
                var pageReferences = new HashSet<SourceRef>();
                foreach (var source in selected.Where(s => s.Reference.Locator.SourceLabel.Contains("https://", StringComparison.OrdinalIgnoreCase))
                    .DistinctBy(s => s.Reference.Locator.SourceLabel))
                    pageReferences.UnionWith(await web.WebPageReferencesAsync(source.Reference, ct));
                if (pageReferences.Count > 0)
                {
                    pageReferences.RemoveWhere(r => !CourseMaterials.Allowed(draft.Plan, node, new("c" + r.VersionId.ToString("N"), r, false)) &&
                        !selected.Any(s => s.Reference == r));
                    selected = selected.Concat(pageReferences.Select(r => new PlanSource("c" + r.VersionId.ToString("N"), r, false)))
                        .DistinctBy(s => s.Reference).ToArray();
                    webBatches[node.Key] = await WebKnowledge.PrepareAsync(knowledge, pageReferences, egress, config.Model, 0, ct);
                }
            }
            var fullWebContext = webBatches.ContainsKey(node.Key);
            if (fullWebContext) budget = CourseMaterials.MaximumLessonCharacters;
            foreach (var source in selected.OrderByDescending(s => s.UserPreferred))
            {
                if (!adaptive && !fullWebContext && (budget == 0 || materials.Count == 30)) break;
                var resolved = await knowledge.ResolveAsync(source.Reference, ct);
                if (resolved.Text is null) return Result<TheoryReview>.Fail(ErrorCode.SOURCE_UNAVAILABLE, "固定版本资料库不可访问，请恢复资料库后重试。");
                var body = adaptive || fullWebContext ? resolved.Text : resolved.Text[..Math.Min(resolved.Text.Length, Math.Min(4000, budget))]; budget -= body.Length;
                if (budget < 0) return Result<TheoryReview>.Fail(ErrorCode.VALIDATION_ERROR, "本课固定原文超出预算，需缩小明确范围；未截断来源。");
                materials.Add(new(source.Key, 0, ModelSourceKind.OriginalMaterial, $"[资料id={source.Key}]\n" + LessonTeaching.SourceHeader(draft.Plan, node, source) + CourseMaterials.WithQuality(draft.Plan.Collections, source, body), source.Reference.Locator.SourceLabel + " / " + source.Reference.Locator.Heading)); sources.Add(source);
            }
            try { SourceEvidenceRules.CheckDefinitions(materials.Select(m => (m.Label ?? m.Id, m.Text))); }
            catch (InvalidDataException) { return Result<TheoryReview>.Fail(ErrorCode.INSUFFICIENT_SOURCES, "来源同名定义存在冲突，请核对来源；未发布文章或测验。"); }
            var missingEvidence = TheoryKnowledgePolicy.MissingEvidence(node, materials);
            if (missingEvidence is not null) return Result<TheoryReview>.Fail(ErrorCode.INSUFFICIENT_SOURCES, missingEvidence);
            if (queue is ISourceSelectionHistory history)
                await history.SaveSelectionAsync(subjectId, LessonSourceSelector.Describe(draft.Plan, node, sources, materials.Sum(m => m.Text.Length)), ct);
            allSources.AddRange(sources);
            originalMaterials.Add(node.Key, materials.AsReadOnly());
            var text = "讲解简明，详略由本课目标决定。原文中的相邻内容、人物访谈、历史和参考文献仅在支持本课目标时简要引用；不得因取材含其他课程内容就扩展本课教学范围。保留必要定义、条件、推导与例子，避免重复铺陈。\n" + (adaptive ? TheoryPrompt.AdaptiveText : TheoryPrompt.Text) + "\n学习者基础/目标：" + JsonSerializer.Serialize(new { draft.Plan.Candidate.Background, draft.Plan.Candidate.Goal }, Wire.Options) +
                "\n来源若相互矛盾且无法据原文消解，停止生成相关结论或题目；不得编造一致答案。\n本课冻结定义：" + JsonSerializer.Serialize(node, Wire.Options) + "\n可引用id：" + string.Join(",", sources.Select(s => s.Key));
            text = text.Replace("仅依据提供的固定原文，", "资料支持的部分依据固定原文，")
                .Replace("不得补造来源未提供的具体事实", "不得补造来源未提供的动态事实")
                .Replace("正文总字符不超过65536，最多128块", "正文总字符不超过65000，最多127块") + TheoryKnowledgePolicy.Guidance;
            if (sources.Count == 0)
                text = text.Replace("schemaVersion=3", "schemaVersion=4").Replace("\"schemaVersion\":3", "\"schemaVersion\":4").Replace("\"sourceKeys\":[\"s1\"]", "\"sourceKeys\":[]") +
                    "\n本课没有外部原文，仅讲稳定基础知识；sourceKeys必须为空数组[]，schemaVersion=4。";
            text += "\n" + CourseMaterials.OcrGuidance + ExamReference.LessonGuidance(draft.Plan, node) + ExamGraphConflicts.Describe(materials);
            text += LessonTeaching.Describe(draft.Plan, node);
            var protocol = sources.Count == 0 ? "theory-v4" : "theory-v3";
            if (draft.Plan.Candidate.KnowledgeCoverage?.Any(m => m.State == "included" && m.NodeKeys.Contains(node.Key)) == true) protocol = "theory-v3-context";
            if (node.Type == "quiz")
            {
                protocol = "quiz-v1";
                text = QuizLesson.Prompt + "\n本课定义：" + JsonSerializer.Serialize(node, Wire.Options) +
                    "\n" + ExamReference.LessonGuidance(draft.Plan, node) + ExamGraphConflicts.Describe(materials) +
                    "\n本次仅出测验题，优先使用导入原题；不要执行上文课文/示范要求。所有解析只写入referenceAnswer/explanation，不能放进题干。";
            }
            var snapshot = new[] { new ModelSource(node.Type == "quiz" || adaptive ? protocol : protocol + "-short", sources.Count == 0 ? 4 : 3, ModelSourceKind.UserText, text) }.Concat(CourseMaterials.PackContext(materials));
            try { reviews.Add(node.Key, adaptive || fullWebContext ? await egress.PrepareAdaptiveAsync(config.Model, snapshot, false, ct) : egress.Prepare(config.Model, snapshot, 8192, true)); }
            catch (ContextBudgetExceededException e) { return Result<TheoryReview>.Fail(ErrorCode.VALIDATION_ERROR, e.UserMessage(config.Model)); }
            catch (ArgumentException) { return Result<TheoryReview>.Fail(ErrorCode.VALIDATION_ERROR, "本课请求的资料数量或单段长度超过限制；未发送，请检查来源切分。"); }
        }
        if (reviews.Count == 0) return Result<TheoryReview>.Fail(ErrorCode.VALIDATION_ERROR, "没有可授权的理论课程。");
        var prepared = new TheoryReview(subjectId, draft.Plan.Hash, config.Model, config.ConsentMode, reviews, allSources.Distinct().ToArray(), originalMaterials);
        foreach (var pair in webBatches) prepared.WebBatches.Add(pair.Key, pair.Value);
        return Result<TheoryReview>.Success(prepared);
    }
    public void Authorize(TheoryReview preview, bool allow)
    {
        lock (grantGate)
            foreach (var pair in preview.Reviews)
            {
                if (grants.Remove((preview.SubjectId, pair.Key), out var old)) egress.Revoke(old.Consent);
                // User explicitly approves the disclosed task retry scope, not just one HTTP attempt.
                if (allow) grants[(preview.SubjectId, pair.Key)] = new(preview, egress.Approve(pair.Value, ConsentMode.Once));
            }
        Changed?.Invoke();
    }
    public async Task ScanAsync(bool resumeCancelled = false, CancellationToken ct = default)
    {
        // Retained solely to test migration/old cancellation protections. Product workflow is demand-only.
        if (!legacyAutoRegression) return;
        var control = await queue.ControlAsync(ct); if (control.Mode != GenerationMode.Auto || control.AutoPaused) return;
        foreach (var draft in (await planning.ListAsync(ct)).Where(d => d.Confirmed && d.Plan is not null).OrderBy(d => d.Id))
        {
            var graph = PlanRules.Graph(draft.Id, draft.Plan!.Candidate);
            var tasks = await queue.TasksAsync(ct);
            foreach (var node in draft.Plan.Candidate.Nodes.Select((n, order) => (n, order))
                .OrderBy(x => graph.Layers[PlanRules.NodeId(draft.Id, x.n.Key)]).ThenBy(x => x.order))
            {
                if (node.n.Type is not ("theory" or "quiz")) continue;
                if ((await learning.GetLessonAsync(draft.Id, node.n.Key, ct)).Ok) continue;
                var previous = tasks.LastOrDefault(t => t.SubjectId == draft.Id && t.NodeKey == node.n.Key);
                if (previous is not null && !(previous.Failure == "MODE_CHANGED" || resumeCancelled && previous.Status == GenerationStatus.Cancelled)) continue;
                await queue.RequestAsync(draft.Id, new(Guid.NewGuid(), 0, new(node.n.Key, resumeCancelled ? GenerationTrigger.Resume : GenerationTrigger.Auto)), ct);
            }
        }
        // State scanning is the idempotent consumer. A crash before acknowledgement only
        // repeats this scan; unique active tasks and immutable lessons prevent duplicate work.
        foreach (var entry in await learning.PendingEventsAsync(ct)) await learning.AcknowledgeEventAsync(entry.EventId, ct);
    }
    public async Task ResumeAsync(CancellationToken ct = default)
    { await queue.ResumeAsync(ct); await ScanAsync(true, ct); Changed?.Invoke(); }
    public async Task SetModeAsync(GenerationMode mode, CancellationToken ct = default)
    {
        var config = await settings.GetAsync(ct);
        if (config.GenerationMode != mode)
        {
            var saved = await settings.SaveAsync(new(Guid.NewGuid(), config.Revision, new(config.Model, mode, config.ConsentMode, null)), ct);
            if (!saved.Ok) throw new InvalidOperationException("Settings revision changed");
        }
        await queue.SetModeAsync(mode, ct); await ScanAsync(false, ct); Changed?.Invoke();
    }
    public async Task CancelAsync(Guid id)
    {
        await queue.CancelAsync(id); // Commit cancellation before interrupting the transport.
        lock (runningGate) { if (runningId == id) running?.Cancel(); }
        Changed?.Invoke();
    }
    public async Task<bool> TickAsync(CancellationToken ct = default)
    {
        if (!await worker.WaitAsync(0, ct)) return false;
        try
        {
            await ScanAsync(false, ct);
            var config = await settings.GetAsync(ct); var control = await queue.ControlAsync(ct);
            var candidates = (await queue.TasksAsync(ct)).Where(t => (legacyAutoRegression || t.Trigger is not (GenerationTrigger.Auto or GenerationTrigger.Resume)) && t.Status == GenerationStatus.Queued && (t.NotBefore is null || t.NotBefore <= DateTimeOffset.UtcNow) &&
                (t.Trigger is not (GenerationTrigger.Auto or GenerationTrigger.Resume) || control.Mode == GenerationMode.Auto && !control.AutoPaused)).ToArray();
            if (candidates.Length == 0) return false;
            GenerationTask? task = null; Grant? permission = null;
            var ordering = new Dictionary<(Guid, string), (int Layer, int Order, Guid Id)>();
            foreach (var draft in legacyAutoRegression
                ? (await planning.ListAsync(ct)).Where(d => d.Confirmed && d.Plan is not null)
                : Enumerable.Empty<PlanningDraft>())
            {
                var graph = PlanRules.Graph(draft.Id, draft.Plan!.Candidate);
                foreach (var node in draft.Plan.Candidate.Nodes.Select((n, i) => (n, i)))
                    ordering[(draft.Id, node.n.Key)] = (graph.Layers[PlanRules.NodeId(draft.Id, node.n.Key)], node.i, PlanRules.NodeId(draft.Id, node.n.Key));
            }
            foreach (var candidate in legacyAutoRegression ? candidates.OrderBy(t => ordering.GetValueOrDefault((t.SubjectId, t.NodeKey))) : candidates.OrderBy(t => t.CreatedAt))
            {
                lock (grantGate)
                    if (grants.TryGetValue((candidate.SubjectId, candidate.NodeKey), out var found) && found.Preview.Model == config.Model && found.Preview.PlanHash == candidate.PlanHash &&
                        found.Preview.ConsentMode == config.ConsentMode && (legacyAutoRegression && config.ConsentMode == ConsentMode.Once || found.TaskId is null || found.TaskId == candidate.Id))
                    { task = candidate; permission = found; break; }
            }
            if (task is null || permission is null) return false;
            var review = permission.Preview.Reviews[task.NodeKey];
            var remaining = TimeSpan.FromSeconds(600) - (DateTimeOffset.UtcNow - permission.StartedAt);
            if (review.Budget is not null && remaining <= TimeSpan.Zero) { await queue.CancelAsync(task.Id, ct); return true; }
            var dispatched = await queue.DispatchAsync(task.Id, task.Revision, config.Model, ct);
            if (!dispatched.Ok) return false;
            permission.TaskId = task.Id;
            task = dispatched.Value!;
            lock (runningGate) { runningId = task.Id; running = CancellationTokenSource.CreateLinkedTokenSource(ct); }
            Changed?.Invoke();
            if (review.Budget is { } taskBudget)
            {
                running.CancelAfter(remaining);
                if (queue is ITaskTelemetry telemetry) await telemetry.SaveBudgetAsync(new(task.SubjectId, task.Id, config.Model, taskBudget, permission.StartedAt), ct);
            }
            var dispatchedAt = DateTimeOffset.UtcNow;
            try
            {
                var original = permission.Preview.Materials[task.NodeKey];
                ModelConsent? webConsent = null;
                if (knowledgeCache is not null && permission.Preview.WebBatches.TryGetValue(task.NodeKey, out var batches))
                {
                    var usages = new List<TokenUsage>();
                    IReadOnlyList<BookKnowledgeUnit> units;
                    try { units = await WebKnowledge.CompleteAsync(batches, knowledgeCache, egress, config.Model, usages.Add, running.Token); }
                    finally
                    {
                        if (queue is ITaskTelemetry webTiming)
                            for (var i = 0; i < usages.Count; i++)
                                await webTiming.RecordAsync(new(task.Id, "web-knowledge", i + 1, dispatchedAt, DateTimeOffset.UtcNow, "received", usages[i]));
                    }
                    var webRefs = units.SelectMany(u => u.Sources).Select(s => s.Reference).ToHashSet();
                    var teaching = units.SelectMany(WebKnowledge.TeachingReferences).ToHashSet();
                    var excludedIds = permission.Preview.Sources.Where(s => webRefs.Contains(s.Reference) && !teaching.Contains(s.Reference)).Select(s => s.Key).ToHashSet();
                    original = original.Where(m => !excludedIds.Contains(m.Id)).ToArray();
                    if (original.Count == 0) throw new SourcePreparationException("网页未提取到可用教学知识，请补充正文资料。");
                    var configNow = await settings.GetAsync(running.Token);
                    if (configNow.Model != config.Model || configNow.ConsentMode != config.ConsentMode) throw new OperationCanceledException();
                    var guidance = "\n网页教学知识（模型提取，仅围绕本课目标使用；非教学和未分类内容不进入课程）：\n" +
                        string.Join('\n', units.SelectMany(u => u.Points).Select(p => p.Title + "：" + p.Detail)) +
                        "\n混合段落中的推广/导航/评论不可用于解释或出题。实际允许引用id：" + string.Join(',', original.Select(m => m.Id));
                    review = await egress.PrepareAdaptiveAsync(config.Model,
                        new[] { review.Sources[0] with { Text = review.Sources[0].Text + guidance } }.Concat(CourseMaterials.PackContext(original)), false, running.Token);
                    webConsent = egress.Approve(review, ConsentMode.Once);
                }
                Result<ProviderReply> reply;
                try { reply = await egress.SendAsync(review, webConsent ?? permission.Consent, running.Token); }
                finally { if (webConsent is not null) egress.Revoke(webConsent); }
                if (review.Budget is not null && queue is ITaskTelemetry timing)
                    await timing.RecordAsync(new(task.Id, "model", task.Attempts, dispatchedAt, DateTimeOffset.UtcNow, reply.Ok ? "received" : reply.Error!.Code.ToString(), reply.Value?.Usage));
                if (running.IsCancellationRequested)
                {
                    await queue.CancelAsync(task.Id);
                    await queue.FailAsync(task.Id, task.Revision, ErrorCode.RUN_CANCELLED, reply.Value?.Usage);
                    return true;
                }
                if (!reply.Ok) await queue.FailAsync(task.Id, task.Revision, reply.Error!.Code, null);
                else
                {
                    var sent = permission.Preview.Sources.Where(s => original.Any(m => m.Id == s.Key)).ToArray();
                    var isQuiz = review.Sources[0].Id == "quiz-v1";
                    var parsed = isQuiz ? QuizLesson.Parse(reply.Value!.Text, sent) : parser.Parse(reply.Value!.Text, sent);
                    if (parsed.Ok && parsed.Value!.SchemaVersion != (isQuiz ? 5 : sent.Length == 0 ? 4 : 3))
                        parsed = Result<TheoryLesson>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "新生成课程版本必须匹配请求，每题恰好四个不同选项。");
                    if (parsed.Ok)
                    {
                        var frozenPlan = (await planning.GetAsync(task.SubjectId))?.Plan;
                        var definition = frozenPlan?.Candidate.Nodes.SingleOrDefault(n => n.Key == task.NodeKey);
                        var cited = original.Where(s => parsed.Value!.SourceKeys.Contains(s.Id)).ToArray();
                        if (definition is null || TheoryKnowledgePolicy.MissingEvidence(definition, cited) is not null)
                            parsed = Result<TheoryLesson>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "正文引用未覆盖需要资料的课程目标，未发布。");

                    }
                    if (parsed.Ok) parsed = LessonRules.Validate(isQuiz ? parsed.Value! : TheoryKnowledgePolicy.Label(parsed.Value!), sent);
                    if (!parsed.Ok) await queue.FailAsync(task.Id, task.Revision, parsed.Error!.Code, reply.Value.Usage);
                    else await queue.CompleteAsync(task.Id, task.Revision, parsed.Value!, sent, reply.Value.Usage);
                }
            }
            catch (OperationCanceledException) { await queue.FailAsync(task.Id, task.Revision, ErrorCode.RUN_CANCELLED, null); }
            catch (Exception) { await queue.FailAsync(task.Id, task.Revision, ErrorCode.IO_FAILURE, null); }
            finally
            {
                lock (runningGate) { running.Dispose(); running = null; runningId = null; }
                if (review.Budget is not null && queue is ITaskTelemetry finalTiming)
                {
                    var completed = (await queue.TasksAsync()).FirstOrDefault(t => t.Id == task.Id);
                    if (completed is not null && completed.Status is GenerationStatus.Ready or GenerationStatus.Failed or GenerationStatus.Cancelled)
                        await finalTiming.RecordAsync(new(task.Id, "generation", 0, permission.StartedAt, DateTimeOffset.UtcNow, completed.Status.ToString(), completed.Usage));
                }
                Changed?.Invoke();
            }
            return true;
        }
        finally { worker.Release(); }
    }
    public void Stop()
    {
        // The SQLite port commits synchronously before returning its completed Task.
        // Preserve automatic pause on a graceful desktop close, then interrupt transport.
        Guid? id;
        lock (runningGate) { id = runningId; if (id is null) running?.Cancel(); }
        if (id is { } taskId) _ = CancelAsync(taskId);
    }
}
