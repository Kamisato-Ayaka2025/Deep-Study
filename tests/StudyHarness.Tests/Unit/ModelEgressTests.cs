using StudyHarness.Application;
using StudyHarness.Contracts;
using Xunit;

namespace StudyHarness.Tests.Unit;

public sealed class ModelEgressTests
{
    private sealed class PlanCapabilities : IModelCapabilitySource
    {
        public Task<ModelCapabilities> GetAsync(string model, CancellationToken ct) =>
            Task.FromResult(new ModelCapabilities(model, 1048576, 393216, false, DateTimeOffset.UtcNow, true));
    }
    [Fact]
    public async Task CompletePlan_ApprovedOutputBudgetReachesTransport()
    {
        var provider = new CaptureProvider(); var gate = new ModelEgress(provider, new PlanCapabilities());
        var review = await gate.PrepareAdaptiveAsync("deepseek-chat", [new("planning-v2", 0, ModelSourceKind.UserText, "学习算法竞赛")], true);
        Assert.Equal(32768, review.MaxOutputTokens);
        Assert.True((await gate.SendAsync(review, gate.Approve(review, ConsentMode.Once))).Ok);
        Assert.Equal(32768, Assert.Single(provider.Requests).MaxOutputTokens);
    }
    private sealed class CaptureProvider : IModelProvider
    {
        public string Destination { get; set; } = "test:capture-only";
        public List<ApprovedModelRequest> Requests { get; } = [];
        public bool Fail { get; set; }
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(Fail ? Result<ProviderReply>.Fail(ErrorCode.IO_FAILURE, "fixture") :
                Result<ProviderReply>.Success(new("capture", true)));
        }
    }
    private static ModelReview Review(ModelEgress gate, string text = "disclosed text", long revision = 1) =>
        gate.Prepare("deepseek-chat", [new("source-1", revision, ModelSourceKind.OriginalMaterial, text)]);

    [Fact]
    public async Task PlanTask_AtomicThreeDispatchCapSurvivesConcurrentCalls()
    {
        var provider = new CaptureProvider(); var gate = new ModelEgress(provider);
        var review = await gate.PrepareAdaptiveAsync("deepseek-chat", [new("planning-v2", 0, ModelSourceKind.UserText, "test")], true);
        var consent = gate.Approve(review, ConsentMode.Once);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => gate.SendAsync(review, consent))));
        Assert.Equal(3, provider.Requests.Count); Assert.Equal(3, results.Count(r => r.Ok));
        gate.Revoke(consent); Assert.False((await gate.SendAsync(review, consent)).Ok);
    }
    [Fact]
    public async Task MissingConsent_HasNoTransportSideEffect()
    {
        var capture = new CaptureProvider();
        var gate = new ModelEgress(capture);
        Assert.Equal(ErrorCode.CONSENT_REQUIRED, (await gate.SendAsync(Review(gate), null)).Error!.Code);
        Assert.Empty(capture.Requests);
    }
    [Theory]
    [InlineData(ModelSourceKind.RunOutput)]
    [InlineData(ModelSourceKind.RunModifiedFile)]
    [InlineData(ModelSourceKind.Credential)]
    public async Task UntrustedOrCredentialSource_CannotBeApprovedOrForwarded(ModelSourceKind kind)
    {
        var capture = new CaptureProvider();
        var gate = new ModelEgress(capture);
        var review = gate.Prepare("deepseek-chat", [new("synthetic-secret", 1, kind, "CANARY-DO-NOT-SEND")]);
        Assert.Throws<InvalidOperationException>(() => gate.Approve(review, ConsentMode.Once));
        Assert.Equal(ErrorCode.CONSENT_DENIED, (await gate.SendAsync(review, null)).Error!.Code);
        Assert.Empty(capture.Requests);
    }
    [Fact]
    public async Task EachConsent_IsConsumedAtomicallyEvenOnTransportFailure()
    {
        var capture = new CaptureProvider { Fail = true };
        var gate = new ModelEgress(capture);
        var review = Review(gate);
        var consent = gate.Approve(review, ConsentMode.Each);
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => gate.SendAsync(review, consent))));
        Assert.Single(capture.Requests);
        Assert.Equal(ErrorCode.CONSENT_DENIED, (await gate.SendAsync(review, consent)).Error!.Code);
    }
    [Fact]
    public async Task OnceConsent_ExactSnapshotOnly_AndRevocationStopsFurtherDispatch()
    {
        var capture = new CaptureProvider();
        var gate = new ModelEgress(capture);
        var review = Review(gate);
        var consent = gate.Approve(review, ConsentMode.Once);
        await gate.SendAsync(review, consent);
        await gate.SendAsync(review, consent);
        Assert.Equal(2, capture.Requests.Count);
        foreach (var changed in new[] { Review(gate, "changed"), Review(gate, revision: 2),
            gate.Prepare("different-model", review.Sources) })
            Assert.Equal(ErrorCode.CONSENT_DENIED, (await gate.SendAsync(changed, consent)).Error!.Code);
        gate.Revoke(consent);
        Assert.Equal(ErrorCode.CONSENT_DENIED, (await gate.SendAsync(review, consent)).Error!.Code);
        Assert.Equal(2, capture.Requests.Count);
    }
    [Fact]
    public async Task Review_FreezesContentAndProvenanceBeforeConsent()
    {
        var capture = new CaptureProvider();
        var gate = new ModelEgress(capture);
        var sources = new[] { new ModelSource("original", 7, ModelSourceKind.OriginalMaterial, "reviewed") };
        var review = gate.Prepare("deepseek-chat", sources);
        var consent = gate.Approve(review, ConsentMode.Each);
        sources[0] = new("replacement", 8, ModelSourceKind.RunModifiedFile, "SECRET");
        await gate.SendAsync(review, consent);
        var sent = Assert.Single(capture.Requests);
        Assert.Equal("reviewed", Assert.Single(sent.Sources).Text);
        Assert.Equal("original", sent.Sources[0].Id);
        Assert.Equal(7, sent.Sources[0].Revision);
        Assert.Throws<NotSupportedException>(() => ((IList<ModelSource>)sent.Sources)[0] = sources[0]);
    }
    [Fact]
    public async Task DestinationChange_CrossGateAndFixedProbe_CannotBypassConsent()
    {
        var capture = new CaptureProvider();
        var gate = new ModelEgress(capture);
        var review = Review(gate);
        var consent = gate.Approve(review, ConsentMode.Each);
        var other = new ModelEgress(capture);
        Assert.Equal(ErrorCode.CONSENT_DENIED, (await other.SendAsync(review, consent)).Error!.Code);
        Assert.Throws<InvalidOperationException>(() => other.Revoke(consent));
        capture.Destination = "test:changed";
        Assert.Equal(ErrorCode.CONSENT_DENIED, (await gate.SendAsync(review, consent)).Error!.Code);
        Assert.Equal(ErrorCode.CONSENT_DENIED, (await gate.ProbeAsync()).Error!.Code);
        Assert.Empty(capture.Requests);
    }
    [Fact]
    public async Task CancelledBeforeDispatch_LeavesTransportUntouched()
    {
        var capture = new CaptureProvider();
        var gate = new ModelEgress(capture);
        var review = Review(gate);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.SendAsync(review, gate.Approve(review, ConsentMode.Each), cancellation.Token));
        Assert.Empty(capture.Requests);
    }
    [Fact]
    public void MalformedOrOversizedSnapshots_AreRejected()
    {
        var gate = new ModelEgress(new CaptureProvider());
        Assert.Throws<ArgumentException>(() => gate.Prepare("deepseek-chat", []));
        Assert.Throws<ArgumentException>(() => Review(gate, new string('x', 65537)));
        Assert.Throws<ArgumentException>(() => Review(gate, revision: -1));
        Assert.Throws<ArgumentException>(() => gate.Prepare("deepseek-chat", [new("id", 0, (ModelSourceKind)99, "text")]));
        Assert.Throws<ArgumentException>(() => gate.Prepare("deepseek-chat", [new("id", 0, ModelSourceKind.UserText, "a"), new("id", 1, ModelSourceKind.UserText, "b")]));
    }
}
