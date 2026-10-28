using StudyHarness.Application;
using StudyHarness.Contracts;
using Xunit;

namespace StudyHarness.Tests.Unit;

public sealed class PlanningEvidenceTests
{
    private sealed class Knowledge : IKnowledgeStore, ICollectionKnowledge
    {
        public Dictionary<SourceRef, string> Texts { get; } = [];
        public List<Guid[]> Queries { get; } = [];
        public Task<ResolvedSource> ResolveAsync(SourceRef r, CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(new ResolvedSource(r, Texts[r], null)); }
        public Task<IReadOnlyList<KnowledgeFragment>> RetrieveAsync(Guid l, string q, IReadOnlyList<Guid> c, int budget, CancellationToken ct = default) { Queries.Add(c.ToArray()); return Task.FromResult<IReadOnlyList<KnowledgeFragment>>([]); }
        public Task<LibraryDto?> CurrentAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<LibraryDto>> SwitchAsync(Command<HostSelection> c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ImportReport>> ImportAsync(Command<HostSelection> c, Guid l, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnowledgeSearch> SearchAsync(Guid l, string q, IReadOnlyCollection<Guid> p, int limit = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<CollectionSnapshot>> ImportCollectionAsync(HostSelection s, Guid l, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<CollectionSnapshot>> CollectionsAsync(Guid l, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KnowledgeFragment>> RetrieveFrozenAsync(PlanCollection c, string q, IReadOnlyList<string> e, int b, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private static PlanCollection Collection(Knowledge k, int number, int pages, int length)
    {
        var library = Guid.NewGuid(); var id = Guid.NewGuid();
        var refs = Enumerable.Range(1, pages).Select(p => new SourceRef(library, Guid.NewGuid(), Guid.NewGuid(), new string('A',64), new($"chapter-{number}", p, "topic", $"page-{p}:0-{length}"))).ToArray();
        foreach (var r in refs) k.Texts.Add(r, $"unique-{number}-{r.Locator.Page}:" + new string('中',length));
        return new(library, new(id,"version","hash",true,refs.Select(r => new CollectionEntry("page-"+r.Locator.Page,r.Locator.Page.ToString()!,"topic","ocrReady","hash",null,[])).ToArray()), refs);
    }
    [Fact]
    public async Task EightShortDecks_EveryThirdReceivesWholeEvidenceRegardlessOfCollectionOrder()
    {
        var k = new Knowledge(); var collections = Enumerable.Range(1,8).Select(i => Collection(k,i,90,6500)).ToArray();
        var selected = await PlanningEvidence.SelectAsync(k,collections,new Dictionary<(Guid,string),string>(),"network",[],default);
        foreach (var c in collections)
        {
            var pages = selected.Fragments.Where(f => f.SourceId == c.Manifest.CollectionId).Select(f => f.Reference.Locator.Page!.Value).ToArray();
            Assert.Contains(pages,p => p <= 30); Assert.Contains(pages,p => p > 30 && p <= 60); Assert.Contains(pages,p => p > 60);
        }
        Assert.All(selected.Fragments,f => Assert.Equal(k.Texts[f.Reference],f.Text));
        Assert.All(k.Queries,q => Assert.Single(q));
        var reversed = await PlanningEvidence.SelectAsync(k,collections.Reverse().ToArray(),new Dictionary<(Guid,string),string>(),"network",[],default);
        Assert.True(selected.Fragments.Select(f=>f.Reference).ToHashSet().SetEquals(reversed.Fragments.Select(f=>f.Reference)));
        Assert.Contains("chapter-8", selected.Report);
    }
    [Fact]
    public async Task LongPdf_LargeEarlyPagesCannotConsumeMiddleAndTailAllowance()
    {
        var k = new Knowledge(); var book = Collection(k,1,505,15000);
        var result = await PlanningEvidence.SelectAsync(k,[book],new Dictionary<(Guid,string),string>(),"network",[],default);
        Assert.Contains(result.Fragments,f=>f.Reference.Locator.Page <= 168);
        Assert.Contains(result.Fragments,f=>f.Reference.Locator.Page > 168 && f.Reference.Locator.Page <= 336);
        Assert.Contains(result.Fragments,f=>f.Reference.Locator.Page > 336);
        Assert.All(result.Fragments,f=>Assert.Equal(k.Texts[f.Reference],f.Text));
        Assert.True(result.Fragments.Sum(f=>f.Text.Length)<=144000);
    }
    [Fact]
    public async Task ExistingReferencesRemainUniqueAndCancellationPropagates()
    {
        var k = new Knowledge(); var book = Collection(k,1,12,500);
        var result = await PlanningEvidence.SelectAsync(k,[book],new Dictionary<(Guid,string),string>(),"network",[book.References[0]],default);
        Assert.DoesNotContain(result.Fragments,f=>f.Reference==book.References[0]);
        Assert.Equal(result.Fragments.Count,result.Fragments.Select(f=>f.Reference).Distinct().Count());
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PlanningEvidence.SelectAsync(k,[book],new Dictionary<(Guid,string),string>(),"network",[],stop.Token));
    }
}
