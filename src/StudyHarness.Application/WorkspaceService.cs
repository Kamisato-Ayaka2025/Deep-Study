using StudyHarness.Contracts;
namespace StudyHarness.Application;

// P2 composition of knowledge and model capabilities. No Runner port exists here.
public sealed class WorkspaceService(IKnowledgeStore knowledge, SettingsService settings, ModelEgress egress)
{
    public IKnowledgeStore Knowledge { get; } = knowledge;
    private ModelReview? review;
    private ModelConsent? consent;
    private ConsentMode? approvedMode;
    public ModelReview? Review => review;
    public async Task<ModelReview> PrepareAsync(string text, IReadOnlyList<SourceRef> references, CancellationToken ct = default)
    {
        var config = await settings.GetAsync(ct);
        var sources = new List<ModelSource> { new("user-text", 0, ModelSourceKind.UserText, text) };
        foreach (var reference in references.Distinct())
        {
            var resolved = await Knowledge.ResolveAsync(reference, ct);
            if (resolved.Text is null) throw new IOException(resolved.UnavailableReason);
            sources.Add(new(reference.VersionId.ToString(), 0, ModelSourceKind.OriginalMaterial, resolved.Text));
        }
        var candidate = egress.Prepare(config.Model, sources);
        if (review is not null && review.Model == candidate.Model && review.Sources.SequenceEqual(candidate.Sources)) return review;
        if (consent is not null) egress.Revoke(consent);
        consent = null; review = candidate; return review;
    }
    public async Task<Result<ProviderReply>> SendAsync(bool allow, CancellationToken ct = default)
    {
        if (review is null) return Result<ProviderReply>.Fail(ErrorCode.CONSENT_REQUIRED, "请先预览外发内容。");
        if (!allow)
        {
            if (consent is not null) egress.Revoke(consent);
            consent = null;
            return Result<ProviderReply>.Fail(ErrorCode.CONSENT_DENIED, "已拒绝外发，资料仍保存在本地。");
        }
        var config = await settings.GetAsync(ct);
        if (config.Model != review.Model) return Result<ProviderReply>.Fail(ErrorCode.CONSENT_REQUIRED, "模型已变化，请重新预览。");
        if (approvedMode != config.ConsentMode && consent is not null) { egress.Revoke(consent); consent = null; }
        consent ??= egress.Approve(review, config.ConsentMode);
        approvedMode = config.ConsentMode;
        var result = await egress.SendAsync(review, consent, ct);
        if (config.ConsentMode == ConsentMode.Each) consent = null;
        return result;
    }
}
