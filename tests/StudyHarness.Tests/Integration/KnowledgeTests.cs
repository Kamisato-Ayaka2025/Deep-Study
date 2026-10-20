using System.Security.Cryptography;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Core;
using Xunit;

namespace StudyHarness.Tests.Integration;
public sealed class KnowledgeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-knowledge", Guid.NewGuid().ToString());
    private string Dir(string name) { var path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private KnowledgeStore Store() => new(Dir("app"));
    private static Command<HostSelection> Select(string path, long revision = 0, bool folder = true) => new(Guid.NewGuid(), revision, folder ? HostSelection.FromFolderPicker(path) : HostSelection.FromFilePicker(path));
    private async Task<(KnowledgeStore Store, Guid Id, string Source)> Setup()
    {
        var store = Store(); var library = await store.SwitchAsync(Select(Dir("A")));
        Assert.True(library.Ok); return (store, library.Value!.Id, Dir("source"));
    }
    [Theory]
    [InlineData("c")]
    [InlineData("cpp")]
    [InlineData("hpp")]
    public async Task ProjectImport_ExplicitSourceKinds_RelativePaths_FrozenVersions_ExcludedBuildTree(string extension)
    {
        var (store, id, source) = await Setup();
        Directory.CreateDirectory(Path.Combine(source, "src")); Directory.CreateDirectory(Path.Combine(source, "bin"));
        var path = Path.Combine(source, "src", "main." + extension);
        var code = "#include <stdio.h>\nint main(void) { return 0; }";
        await File.WriteAllTextAsync(path, code);
        await File.WriteAllTextAsync(Path.Combine(source, "bin", "excluded.c"), "excluded");
        await File.WriteAllTextAsync(Path.Combine(source, ".env"), "unrelated-secret");
        var ordinary = await store.ImportAsync(Select(source), id);
        Assert.Empty(ordinary.Value!.References!);
        var imported = await store.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromProjectPicker(source)), id);
        var reference = Assert.Single(imported.Value!.References!);
        Assert.Equal("source/src/main." + extension, reference.Locator.SourceLabel);
        Assert.Equal("代码行 1-2", reference.Locator.Heading);
        Assert.Equal(code, (await store.ResolveAsync(reference)).Text);
        Assert.Equal(code, await File.ReadAllTextAsync(path));
        await File.WriteAllTextAsync(path, "int main(void) { return 1; }");
        await store.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromProjectPicker(source)), id);
        Assert.Equal(code, (await store.ResolveAsync(reference)).Text);
        Assert.DoesNotContain((await store.SearchAsync(id, "", [])).Fragments, f => f.Text.Contains("excluded") || f.Text.Contains("unrelated-secret"));
    }
    [Fact]
    public async Task Import_ReorderInsertChangeDelete_RetainsStableVersionsAndSourceBytes()
    {
        var (store, id, source) = await Setup(); var path = Path.Combine(source, "guide.md");
        await File.WriteAllTextAsync(path, "# Alpha\n\nfirst\n\nsecond\n\n# Beta\n\nold");
        var before = SHA256.HashData(File.ReadAllBytes(path));
        var command = Select(source); var first = await store.ImportAsync(command, id);
        Assert.True(first.Ok); Assert.Equal(3, first.Value!.Files.Single().Added);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(path)));
        Assert.Equal(first.Value.JobId, (await store.ImportAsync(command, id)).Value!.JobId);
        Assert.Equal(ErrorCode.IDEMPOTENCY_CONFLICT, (await store.ImportAsync(command with { Payload = HostSelection.FromFilePicker(path) }, id)).Error!.Code);
        var initial = (await store.SearchAsync(id, "", [])).Fragments;
        var old = initial.Single(f => f.Text == "old").Reference;
        var second = initial.Single(f => f.Text == "second").Reference;
        var repeat = await store.ImportAsync(Select(source), id); Assert.Equal(3, repeat.Value!.Files.Single().Unchanged);
        await File.WriteAllTextAsync(path, "# Alpha\n\nsecond\n\ninserted\n\nfirst\n\n# Beta\n\nnew");
        var change = await store.ImportAsync(Select(source), id);
        Assert.Equal(1, change.Value!.Files.Single().Added); Assert.Equal(1, change.Value.Files.Single().Updated);
        var current = (await store.SearchAsync(id, "", [])).Fragments;
        Assert.Equal(second, current.Single(f => f.Text == "second").Reference);
        Assert.Equal(old.NodeId, current.Single(f => f.Text == "new").Reference.NodeId);
        Assert.NotEqual(old.VersionId, current.Single(f => f.Text == "new").Reference.VersionId);
        Assert.Equal("old", (await store.ResolveAsync(old)).Text);
        await File.WriteAllTextAsync(path, "# Alpha\n\nfirst"); await store.ImportAsync(Select(source), id);
        Assert.True((await store.SearchAsync(id, "second", [])).Fragments.Single().SourceMissing);
        File.Delete(path); await store.ImportAsync(Select(source), id);
        Assert.All((await store.SearchAsync(id, "", [])).Fragments, f => Assert.True(f.SourceMissing));
        Assert.Equal("old", (await Store().ResolveAsync(old)).Text);
    }
    [Fact]
    public async Task SwitchLibrary_RestartOfflineAndTamperedReference_NeverSubstitutesCurrentLibrary()
    {
        var (store, id, source) = await Setup(); await File.WriteAllTextAsync(Path.Combine(source, "x.md"), "# X\n\ncontent");
        await store.ImportAsync(Select(source), id); var reference = (await store.SearchAsync(id, "", [])).Fragments.Single().Reference;
        var oldHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "A", "knowledge.db")));
        var switched = await store.SwitchAsync(Select(Dir("B"), 1)); Assert.True(switched.Ok);
        Assert.Empty((await store.SearchAsync(switched.Value!.Id, "", [])).Fragments);
        Assert.Equal("content", (await Store().ResolveAsync(reference)).Text);
        Assert.Equal(oldHash, SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "A", "knowledge.db"))));
        Assert.Null((await store.ResolveAsync(reference with { ContentHash = new string('0', 64) })).Text);
        Directory.Move(Path.Combine(root, "A"), Path.Combine(root, "A-offline"));
        var offline = await store.ResolveAsync(reference); Assert.Null(offline.Text); Assert.Equal(reference, offline.Reference); Assert.NotNull(offline.UnavailableReason);
        Assert.False(Directory.Exists(Path.Combine(root, "A")));
        Directory.Move(Path.Combine(root, "A-offline"), Path.Combine(root, "A"));
        Assert.Equal(id, (await store.SwitchAsync(Select(Path.Combine(root, "A"), 2))).Value!.Id);
    }
    [Fact]
    public async Task MixedImport_PdfMarkdownUnsupportedCorruptAndEmpty_IndependentResultsAndLocators()
    {
        var (store, id, source) = await Setup();
        var builder = new PdfDocumentBuilder(); var page = builder.AddPage(300, 400);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica); page.AddText("PDF sample knowledge", 12, new PdfPoint(20, 300), font);
        await File.WriteAllBytesAsync(Path.Combine(source, "sample.pdf"), builder.Build());
        var empty = new PdfDocumentBuilder(); empty.AddPage(300, 400); await File.WriteAllBytesAsync(Path.Combine(source, "scan.pdf"), empty.Build());
        await File.WriteAllTextAsync(Path.Combine(source, "bad.pdf"), "broken");
        await File.WriteAllTextAsync(Path.Combine(source, "unsupported.docx"), "ignored");
        await File.WriteAllTextAsync(Path.Combine(source, "guide.md"), "# Heading\n\nMarkdown sample knowledge");
        var before = Directory.GetFiles(source).ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
        var report = (await store.ImportAsync(Select(source), id)).Value!;
        Assert.Equal(2, report.Files.Count(f => f.Ok)); Assert.Equal(3, report.Files.Count(f => !f.Ok));
        foreach (var pair in before) Assert.Equal(pair.Value, SHA256.HashData(File.ReadAllBytes(pair.Key)));
        var fragments = (await store.SearchAsync(id, "knowledge", [])).Fragments;
        Assert.Equal(2, fragments.Count); Assert.Equal(1, fragments.Single(f => f.Reference.Locator.SourceLabel == "sample.pdf").Reference.Locator.Page);
        Assert.Equal("Heading", fragments.Single(f => f.Reference.Locator.SourceLabel == "guide.md").Reference.Locator.Heading);
        Assert.True((await store.SearchAsync(id, "not present", [])).Insufficient);
    }
    [Fact]
    public async Task SameContentDifferentSources_KeepsProvenanceAndPreferredSourceOrder()
    {
        var (store, id, source) = await Setup();
        await File.WriteAllTextAsync(Path.Combine(source, "a.md"), "# Same\n\nbody");
        await File.WriteAllTextAsync(Path.Combine(source, "b.md"), "# Same\n\nbody");
        await store.ImportAsync(Select(source), id);
        var all = (await store.SearchAsync(id, "", [])).Fragments; Assert.Equal(2, all.Count);
        Assert.NotEqual(all[0].Reference.NodeId, all[1].Reference.NodeId);
        Assert.Equal(all[0].Reference.ContentHash, all[1].Reference.ContentHash);
        var preferred = (await store.SearchAsync(id, "", [all[1].SourceId])).Fragments;
        Assert.Equal(all[1].SourceId, preferred[0].SourceId);
    }
    [Fact]
    public async Task CancelAndRevisionConflict_DoNotCommitChanges()
    {
        var (store, id, source) = await Setup();
        Assert.Equal(ErrorCode.REVISION_CONFLICT, (await store.SwitchAsync(Select(Dir("B"), 0))).Error!.Code);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ImportAsync(Select(source), id, cancelled.Token));
        Assert.Equal(id, (await store.CurrentAsync())!.Id);
    }
    [Fact]
    public async Task JunctionInSource_IsRejectedWithoutReadingOutsideSelection()
    {
        var (store, id, source) = await Setup(); var outside = Dir("outside");
        await File.WriteAllTextAsync(Path.Combine(outside, "secret.md"), "outside-canary");
        var link = Path.Combine(source, "linked");
        Junction.Create(link, outside);
        try
        {
            var result = await store.ImportAsync(Select(source), id);
            Assert.True(result.Ok); Assert.Contains(result.Value!.Files, f => !f.Ok);
            Assert.Empty((await store.SearchAsync(id, "", [])).Fragments);
            Assert.False((await store.ImportAsync(Select(Path.Combine(link, "secret.md"), folder: false), id)).Ok);
        }
        finally { Directory.Delete(link); }
    }
    [Fact]
    public async Task AmbiguousChangedParagraphs_AreAddedWithoutOverwritingHistoricalNodes()
    {
        var (store, id, source) = await Setup(); var path = Path.Combine(source, "ambiguous.md");
        await File.WriteAllTextAsync(path, "# Same\n\na\n\nb"); await store.ImportAsync(Select(source), id);
        var old = (await store.SearchAsync(id, "", [])).Fragments;
        await File.WriteAllTextAsync(path, "# Same\n\nc\n\nd"); var report = await store.ImportAsync(Select(source), id);
        Assert.Equal(2, report.Value!.Files.Single().Added); Assert.Equal(0, report.Value.Files.Single().Updated);
        foreach (var f in old) Assert.Equal(f.Text, (await store.ResolveAsync(f.Reference)).Text);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
