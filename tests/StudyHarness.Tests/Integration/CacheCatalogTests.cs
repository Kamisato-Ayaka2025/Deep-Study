using System.Security.Cryptography;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class CacheCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-catalog", Guid.NewGuid().ToString("N"));
    private async Task<(KnowledgeStore Store, Guid Library)> Setup()
    {
        var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var store = new KnowledgeStore(root);
        var selected = await store.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(library)));
        Assert.True(selected.Ok); return (store, selected.Value!.Id);
    }
    [Fact]
    public async Task EmptyCatalog_DoesNotCreateDatabases()
    {
        var page = await new CacheCatalog(root).ReadAsync(9);
        Assert.Empty(page.Items); Assert.Equal(1, page.Page); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task ManyFiles_PagesAreStableBoundedAndReadOnly()
    {
        var (store, library) = await Setup();
        for (var i = 0; i < 19; i++)
        {
            var path = Path.Combine(root, $"资料{i:00}.md"); File.WriteAllText(path, "# 标题\n定义与例子");
            Assert.True((await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library)).Ok);
        }
        var db = Path.Combine(root, "library", "collections.db"); var hash = SHA256.HashData(File.ReadAllBytes(db));
        var catalog = new CacheCatalog(root);
        var first = await catalog.ReadAsync(1); var second = await catalog.ReadAsync(2); var last = await catalog.ReadAsync(999);
        Assert.Equal(19, first.Total); Assert.Equal(8, first.Items.Count); Assert.Equal(8, second.Items.Count);
        Assert.Equal(3, last.Items.Count); Assert.Equal(3, last.Page);
        Assert.Equal(19, first.Items.Concat(second.Items).Concat(last.Items).Select(x => x.Name).Distinct().Count());
        Assert.All(first.Items, x => Assert.False(x.KnowledgeReady));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(db)));
    }
    [Fact]
    public async Task PartialCompleteAndChangedSource_UseCurrentReferencesAcrossRestart()
    {
        var (store, library) = await Setup(); var path = Path.Combine(root, "教材.md");
        File.WriteAllText(path, "# 第一节\n定义一\n# 第二节\n定义二");
        var snapshot = (await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library)).Value!;
        Assert.True(snapshot.References.Count >= 2);
        var cache = new BookKnowledgeCache(root);
        async Task Save(IEnumerable<SourceRef> refs, string key) => await cache.SaveKnowledgeAsync(new(key, key,
            refs.Select((r, i) => new PlanSource("b" + i, r, false)).ToArray(), [], refs.Select((_, i) => "b" + i).ToArray()), default);
        await Save(snapshot.References.Take(1), "partial");
        Assert.False((await new CacheCatalog(root).ReadAsync(1)).Items.Single().KnowledgeReady);
        await Save(snapshot.References.Skip(1), "remaining");
        Assert.True((await new CacheCatalog(root).ReadAsync(1)).Items.Single().KnowledgeReady);
        File.WriteAllText(path, "# 第一节\n完全不同的新版本");
        Assert.True((await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library)).Ok);
        Assert.False((await new CacheCatalog(root).ReadAsync(1)).Items.Single().KnowledgeReady);
    }
    [Fact]
    public async Task MissingExternalLibrary_IsDisclosed()
    {
        await Setup(); Directory.Move(Path.Combine(root, "library"), Path.Combine(root, "offline"));
        var result = await new CacheCatalog(root).ReadAsync(1);
        Assert.NotNull(result.Warning); Assert.Empty(result.Items);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
