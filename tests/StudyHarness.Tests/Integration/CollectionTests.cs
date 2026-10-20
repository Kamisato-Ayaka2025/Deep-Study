using System.Security.Cryptography;
using System.Text;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class CollectionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-collections", Guid.NewGuid().ToString("N"));
    private async Task<(KnowledgeStore Store, Guid Library)> Setup()
    {
        var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var store = new KnowledgeStore(root); var selected = await store.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(library)));
        Assert.True(selected.Ok); return (store, selected.Value!.Id);
    }
    [Fact]
    public async Task LongText_IndexFindsFrontMiddleEndAndPreservesEveryCharacterAndOldVersion()
    {
        var (store, library) = await Setup(); var source = Path.Combine(root, "textbook.md");
        var body = new StringBuilder();
        foreach (var chapter in new[] { "集合基础", "网络协议", "图论算法" })
        { body.AppendLine("# " + chapter); for (var i = 0; i < 3000; i++) body.AppendLine(chapter + "定义与推导，例子" + i); }
        await File.WriteAllTextAsync(source, body.ToString()); var before = SHA256.HashData(await File.ReadAllBytesAsync(source));
        var imported = await store.ImportCollectionAsync(HostSelection.FromFilePicker(source), library); Assert.True(imported.Ok, imported.Error?.Message);
        var snapshot = imported.Value!; Assert.True(snapshot.Manifest.DirectoryComplete); Assert.True(snapshot.References.Count > 3);
        Assert.NotNull(snapshot.Structure);
        Assert.Equal(3, snapshot.Manifest.Entries.Count); // chunks must not become invented directory chapters
        var total = 0;
        foreach (var reference in snapshot.References) { var resolved = await store.ResolveAsync(reference); Assert.NotNull(resolved.Text); total += resolved.Text!.Length; }
        Assert.Equal(body.Length, total); Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(source)));
        foreach (var query in new[] { "集合基础", "网络协议", "图论算法" })
        {
            var found = await store.RetrieveAsync(library, query, [snapshot.Collection.Id], 12000);
            Assert.NotEmpty(found); Assert.Contains(found, f => f.Text.Contains(query)); Assert.True(found.Sum(f => f.Text.Length) <= 12000);
        }
        var repeat = await store.ImportCollectionAsync(HostSelection.FromFilePicker(source), library);
        Assert.Equal(snapshot.Collection.Version, repeat.Value!.Collection.Version);
        var old = snapshot.References[0]; var oldText = (await store.ResolveAsync(old)).Text;
        await File.WriteAllTextAsync(source, "# changed\n新版本内容。");
        Assert.True((await store.ImportCollectionAsync(HostSelection.FromFilePicker(source), library)).Ok);
        Assert.Equal(oldText, (await new KnowledgeStore(root).ResolveAsync(old)).Text);
        Assert.Null((await store.ResolveAsync(old with { ContentHash = new string('0', 64) })).Text);
    }
    [Fact]
    public async Task FrozenRetrieval_ExpandsNeighboursAndSurvivesReimportWithoutSubstitution()
    {
        var (store, library) = await Setup(); var path = Path.Combine(root, "chapters.md");
        await File.WriteAllTextAsync(path, "# 前章\n" + new string('甲', 5000) + "\n# 中章\n" + new string('乙', 5000) + "\n# 后章\n" + new string('丙', 5000));
        var snapshot = (await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library)).Value!;
        var binding = new PlanCollection(library, snapshot.Manifest, snapshot.References);
        foreach (var entry in snapshot.Manifest.Entries)
        {
            var selected = await store.RetrieveFrozenAsync(binding, entry.Heading, [entry.Key], 12000);
            Assert.Single(selected); // complete 5000-character paragraph is no longer cut at 4000 Assert.All(selected, f => Assert.StartsWith(entry.Key + ":", f.Reference.Locator.Block));
        }
        var before = await store.RetrieveFrozenAsync(binding, "中章", ["section-2"], 12000);
        await File.WriteAllTextAsync(path, "# 替换\n改变的内容"); await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library);
        var after = await new KnowledgeStore(root).RetrieveFrozenAsync(binding, "中章", ["section-2"], 12000);
        Assert.Equal(before, after);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RetrieveFrozenAsync(binding, "", ["section-99"], 12000));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.RetrieveFrozenAsync(binding, "", ["section-1", "section-2", "section-3"], 100));
    }
    [Fact]
    public async Task ScannedPages_AreExplicitlyUnreadyAndNeverCountedAsRead()
    {
        var (store, library) = await Setup(); var path = Path.Combine(root, "scan.pdf");
        var builder = new PdfDocumentBuilder(); builder.AddPage(300, 400); builder.AddPage(300, 400);
        await File.WriteAllBytesAsync(path, builder.Build());
        var result = await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library); Assert.True(result.Ok, result.Error?.Message);
        Assert.Equal(2, result.Value!.Collection.EntryCount); Assert.Equal(0, result.Value.Collection.ReadyCount); Assert.Empty(result.Value.References);
        Assert.All(result.Value.Manifest.Entries, e => { Assert.Equal("discovered", e.State); Assert.Contains("ocrRequired", e.QualityFlags); });
        Assert.Single(await store.CollectionsAsync(library));
    }
    [Fact]
    public async Task InvalidFormatAndCancelledImport_DoNotPublishCollection()
    {
        var (store, library) = await Setup(); var path = Path.Combine(root, "private.db"); await File.WriteAllTextAsync(path, "unrelated");
        Assert.False((await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library)).Ok);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library, cancellation.Token));
        Assert.Empty(await store.CollectionsAsync(library));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
