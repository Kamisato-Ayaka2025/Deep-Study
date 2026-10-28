using System.Text;
using StudyHarness.Application;
using StudyHarness.Contracts;
using Xunit;

namespace StudyHarness.Tests.Unit;

public sealed class PresentationPlanningTests
{
    private sealed class Caps : IModelCapabilitySource
    {
        public Task<ModelCapabilities> GetAsync(string m, CancellationToken ct) => Task.FromResult(new ModelCapabilities(m, 1048576, 32768, true, DateTimeOffset.UtcNow, true));
    }
    private sealed class NoSend : IModelProvider
    {
        public string Destination => "test:offline";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct) => throw new InvalidOperationException("No dispatch permitted");
    }
    [Fact]
    public async Task LargePlanBlock_AcceptedWithinVerifiedContext_LessonAndUnknownRemainBounded()
    {
        var gate = new ModelEgress(new NoSend(), new Caps());
        var text = new string('中', 150000);
        var review = await gate.PrepareAdaptiveAsync("deepseek-chat", [new("planning-v2", 0, ModelSourceKind.UserText, text)], true);
        Assert.True(review.Budget!.InputUpperBound > 393216);
        Assert.Equal(text, Assert.Single(review.Sources).Text);
        await Assert.ThrowsAsync<ContextBudgetExceededException>(() => gate.PrepareAdaptiveAsync("deepseek-chat", [new("theory-v3", 0, ModelSourceKind.UserText, text)], false));
        await Assert.ThrowsAsync<ContextBudgetExceededException>(() => new ModelEgress(new NoSend()).PrepareAdaptiveAsync("deepseek-chat", [new("planning-v2", 0, ModelSourceKind.UserText, text)], true));
        var caps = await new Caps().GetAsync("deepseek-chat", default);
        Assert.Throws<ContextBudgetExceededException>(() => AdaptiveBudgetPolicy.Create(caps, caps.Model, new string('x', 786433), true, DateTimeOffset.UtcNow));
        Assert.Throws<ContextBudgetExceededException>(() => AdaptiveBudgetPolicy.Create(caps with { ContextWindow = 131072 }, caps.Model, text, true, DateTimeOffset.UtcNow));
    }
    [Fact]
    public async Task FullSlideDirectory_CompactsRepetitionWithoutLosingPageCoverageOrWarnings()
    {
        var collections = new List<PlanCollection>(); var previews = new Dictionary<(Guid, string), string>(); var ppt = new HashSet<Guid>();
        for (var d = 0; d < 8; d++)
        {
            var id = Guid.NewGuid(); ppt.Add(id);
            var entries = Enumerable.Range(1, 120).Select(p => new CollectionEntry("page-" + p, p.ToString(), "Chapter " + d + " — Reliable transport and sequence numbers", p == 120 ? "unavailable" : "ocrReady", null, null, p == 119 ? ["uncertain-formula"] : Array.Empty<string>())).ToArray();
            collections.Add(new(Guid.NewGuid(), new(id, "v1", "hash", true, entries), []));
            foreach (var e in entries) previews[(id, e.Key)] = e.Heading + " unique mechanism " + e.Key;
        }
        var legacy = PlanningDirectory.Build(collections, previews, new HashSet<Guid>());
        var compact = PlanningDirectory.Build(collections, previews, ppt);
        Assert.True(Encoding.UTF8.GetByteCount(compact) < Encoding.UTF8.GetByteCount(legacy) * 0.65);
        for (var i = 0; i < collections.Count; i++)
            foreach (var e in collections[i].Manifest.Entries)
            {
                Assert.Single(compact.Split('\n'), l => l.StartsWith($"d{i + 1}/{e.Key}\t", StringComparison.Ordinal));
                Assert.Contains("unique mechanism " + e.Key, compact);
            }
        Assert.Contains("unavailable", compact); Assert.Contains("uncertain-formula", compact);
        Assert.Equal("ocrReady", collections[0].Manifest.Entries[0].State);
        var review = await new ModelEgress(new NoSend(), new Caps()).PrepareAdaptiveAsync("deepseek-chat", [new("planning-v2", 0, ModelSourceKind.UserText, AdaptivePlanningPrompt.Text + compact)], true);
        Assert.NotNull(review.Budget);
    }
}
