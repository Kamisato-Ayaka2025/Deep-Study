using System.Text.Json;
using StudyHarness.Contracts;

namespace StudyHarness.Application;

public sealed class LabService(IPlanningStore plans, ILearningStore learning, ILabStore store,
    IKnowledgeStore knowledge, SettingsService settings, ModelEgress egress, ILabExporter exporter)
{
    private readonly SemaphoreSlim worker = new(1, 1);
    public ILabStore Store => store;
    public IPlanningStore Plans => plans;
    public ILearningStore Learning => learning;
    public async Task<Result<LabPackage>> GenerateAsync(Guid subject, string key, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(600)); ct = deadline.Token;
        if (!await worker.WaitAsync(0, ct)) return Result<LabPackage>.Fail(ErrorCode.TASK_ACTIVE, "已有实验正在生成。");
        LabJob? job = null; ModelConsent? consent = null;
        try
        {
            var draft = await plans.GetAsync(subject, ct);
            var node = draft?.Plan?.Candidate.Nodes.SingleOrDefault(n => n.Key == key);
            if (draft?.Confirmed != true || node?.Type != "lab" || node.Lab is null) return Result<LabPackage>.Fail(ErrorCode.PLAN_NOT_CONFIRMED, "先确认实验计划。");
            var existing = await store.GetLabAsync(subject, key, ct);
            if (existing is not null) return Result<LabPackage>.Success(existing);
            var config = await settings.GetAsync(ct);
            var adaptive = draft.Plan!.Candidate.SchemaVersion == 2;
            var materials = new List<ModelSource>(); var sources = new List<PlanSource>(); var budget = adaptive ? CourseMaterials.MaximumLessonCharacters : 32000;
            foreach (var source in await CourseMaterials.SelectAsync(knowledge, draft.Plan!, node, ct))
            {
                if (!adaptive && budget == 0) break;
                var resolved = await knowledge.ResolveAsync(source.Reference, ct);
                if (resolved.Text is null) return Result<LabPackage>.Fail(ErrorCode.SOURCE_UNAVAILABLE, "固定资料不可访问。");
                var text = adaptive ? resolved.Text : resolved.Text[..Math.Min(resolved.Text.Length, Math.Min(8000, budget))]; budget -= text.Length;
                if (budget < 0) return Result<LabPackage>.Fail(ErrorCode.VALIDATION_ERROR, "实践资料超出预算；未截断来源。");
                materials.Add(new(source.Key, 0, ModelSourceKind.OriginalMaterial, "[资料id=" + source.Key + "]\n" + CourseMaterials.WithQuality(draft.Plan.Collections, source, text), source.Reference.Locator.SourceLabel)); sources.Add(source);
            }
            SourceEvidenceRules.CheckDefinitions(materials.Select(m => (m.Label ?? m.Id, m.Text)));
            if (sources.Count == 0) return Result<LabPackage>.Fail(ErrorCode.INSUFFICIENT_SOURCES, "没有可用的实验资料。");
            if (store is ISourceSelectionHistory history)
                await history.SaveSelectionAsync(subject, LessonSourceSelector.Describe(draft.Plan, node, sources, materials.Sum(m => m.Text.Length)), ct);
            var cpp = node.Lab.Toolchain == "portable-cpp";
            var general = node.Lab.Toolchain is "paper-exercise" or "manual-project";
            var prompt = ExamReference.LessonGuidance(draft.Plan, node) + ExamGraphConflicts.Describe(materials) + (general ? GeneralPracticePrompt.Text : cpp ? CppLabPrompt.Text : LabPrompt.Text) + "\n来源若相互矛盾且无法据原文消解，停止相关题目，不得编造一致答案。\n本课冻结定义：" + JsonSerializer.Serialize(node, Wire.Options) + "\n可引用资料id：" + string.Join(',', sources.Select(s => s.Key));
            if (cpp) prompt += "\n本课算法定义：" + JsonSerializer.Serialize(draft.Plan.Candidate.Algorithms?.Where(a => node.AlgorithmKeys!.Contains(a.Key)), Wire.Options);
            var snapshot = new[] { new ModelSource(general ? "practice-v5" : cpp ? "lab-cpp-v4" : "lab-v3", general ? 5 : cpp ? 4 : 3, ModelSourceKind.UserText, prompt) }.Concat(CourseMaterials.PackContext(materials));
            var review = adaptive ? await egress.PrepareAdaptiveAsync(config.Model, snapshot, false, ct) : egress.Prepare(config.Model, snapshot, 8192, true);
            var begun = await store.BeginLabAsync(subject, key, ct);
            if (!begun.Ok) return new(false, null, begun.Error);
            job = begun.Value!;
            if (review.Budget is { } taskBudget && store is ITaskTelemetry telemetry)
            {
                await telemetry.SaveBudgetAsync(new(subject, job.Id, config.Model, taskBudget, started), ct);
                await telemetry.RecordAsync(new(job.Id, "prepare", 0, started, DateTimeOffset.UtcNow, "ready", null), ct);
            }
            // Called only by an explicit Generate action; UI discloses the bounded course/source scope.
            consent = egress.Approve(review, ConsentMode.Once);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                ct.ThrowIfCancellationRequested(); job = job with { Attempts = attempt };
                if (!await store.UpdateLabJobAsync(job, ct)) return Result<LabPackage>.Fail(ErrorCode.RUN_CANCELLED, "生成已取消。");
                var dispatchedAt = DateTimeOffset.UtcNow;
                var reply = await egress.SendAsync(review, consent, ct);
                if (review.Budget is not null && store is ITaskTelemetry timing)
                    await timing.RecordAsync(new(job.Id, "model", attempt, dispatchedAt, DateTimeOffset.UtcNow, reply.Ok ? "received" : reply.Error!.Code.ToString(), reply.Value?.Usage));
                if (reply.Ok)
                {
                    job = job with { Usage = Add(job.Usage, reply.Value!.Usage) };
                    if (general)
                    {
                        var practice = GeneralPracticeRules.Parse(reply.Value.Text, sources.Select(s => s.Key).ToArray());
                        ct.ThrowIfCancellationRequested();
                        var result = practice.Ok ? await store.PublishPracticeAsync(job, practice.Value!, draft.Plan.Hash, sources, ct)
                            : new Result<LabPackage>(false, null, practice.Error);
                        if (!result.Ok) await store.UpdateLabJobAsync(job with { Status = "failed", Failure = result.Error!.Code.ToString() });
                        return result;
                    }
                    var parsed = LabMaterialRules.Parse(reply.Value.Text, sources.Select(s => s.Key).ToArray());
                    if (parsed.Ok && (cpp ? parsed.Value!.SchemaVersion != 4 : parsed.Value!.SchemaVersion == 4))
                        parsed = Result<LabMaterial>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "材料语言与冻结计划不一致。");
                    ct.ThrowIfCancellationRequested();
                    if (parsed.Ok)
                    {
                        var published = await store.PublishLabAsync(job, parsed.Value!, draft.Plan.Hash, sources, ct);
                        if (!published.Ok) await store.UpdateLabJobAsync(job with { Status = "failed", Failure = published.Error!.Code.ToString() });
                        return published;
                    }
                    await store.UpdateLabJobAsync(job with { Status = "failed", Failure = parsed.Error!.Code.ToString() }); return new(false, null, parsed.Error);
                }
                if (reply.Error!.Code != ErrorCode.TRANSIENT_PROVIDER_ERROR || attempt == 3)
                { await store.UpdateLabJobAsync(job with { Status = "failed", Failure = reply.Error.Code.ToString() }); return new(false, null, reply.Error); }
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
            throw new InvalidOperationException();
        }
        catch (OperationCanceledException)
        { if (job is not null) await store.UpdateLabJobAsync(job with { Status = "cancelled", Failure = "RUN_CANCELLED" }); return Result<LabPackage>.Fail(ErrorCode.RUN_CANCELLED, "已取消，已发送请求可能计费；材料未发布。"); }
        catch (Exception)
        { if (job is not null) await store.UpdateLabJobAsync(job with { Status = "failed", Failure = "IO_FAILURE" }); return Result<LabPackage>.Fail(ErrorCode.IO_FAILURE, "生成未完成，请检查本地数据或配置后重试。"); }
        finally
        {
            if (consent is not null) egress.Revoke(consent);
            try
            {
                if (job is not null && store is ITaskTelemetry timing)
                {
                    var final = await store.GetLabJobAsync(subject, key);
                    if (final is not null) await timing.RecordAsync(new(job.Id, "generation", 0, started, DateTimeOffset.UtcNow, final.Status, final.Usage));
                }
            }
            finally { worker.Release(); }
        }
    }
    private static TokenUsage? Add(TokenUsage? a, TokenUsage? b) => b is null ? a : a is null ? b : new(a.PromptTokens + b.PromptTokens, a.CompletionTokens + b.CompletionTokens, a.TotalTokens + b.TotalTokens);
    public async Task<Result<string>> ExportAsync(Guid subject, string node, string path, CancellationToken ct = default)
    {
        var package = await store.GetLabAsync(subject, node, ct);
        return package is null ? Result<string>.Fail(ErrorCode.VALIDATION_ERROR, "尚无材料。") : await exporter.ExportAsync(package, path, ct);
    }
    public Task<Result<LabAttestation>> ConfirmAsync(Guid subject, string node, string hash, bool acknowledged, CancellationToken ct = default) =>
        store.ConfirmLabAsync(subject, new(Guid.NewGuid(), 0, new(node, hash, acknowledged)), ct);
}
