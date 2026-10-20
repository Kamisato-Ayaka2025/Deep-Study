using System.Security.Cryptography;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class OcrFlowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "StudyHarness-ocr", Guid.NewGuid().ToString("N"));
    private sealed class Capabilities : IModelCapabilitySource
    {
        public Task<ModelCapabilities> GetAsync(string model, CancellationToken ct = default) => Task.FromResult(new ModelCapabilities(model, 1048576, 16384, true, DateTimeOffset.UtcNow, true));
    }
    private sealed class Provider : IOcrProvider
    {
        public int Calls { get; private set; }
        public Func<Task>? During { get; set; }
        public async Task<Result<OcrPageResult>> RecognizeAsync(ApprovedOcrPage request, CancellationToken ct)
        {
            Calls++; Assert.InRange(request.Page.Png.Length, 1, 4194304);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(request.Page.Png)), request.Page.ImageHash);
            if (During is not null) await During();
            return Result<OcrPageResult>.Success(new("# 合成识别\n集合包含不同元素，图示标签需复核。", [], new(100, 20, 120), request.Model, OcrService.PromptVersion));
        }
    }
    private async Task<(KnowledgeStore Knowledge, Guid Library, Guid Collection, OcrService Service, Provider Provider)> Setup()
    {
        var library = Path.Combine(root, "library"); Directory.CreateDirectory(library);
        var knowledge = new KnowledgeStore(root); var selected = await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(library)));
        var path = Path.Combine(root, "scan.pdf"); var builder = new PdfDocumentBuilder(); builder.AddPage(300, 400); builder.AddPage(300, 400);
        await File.WriteAllBytesAsync(path, builder.Build());
        var imported = await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(path), selected.Value!.Id); Assert.True(imported.Ok);
        var provider = new Provider(); var service = new OcrService(knowledge, new(new SqliteSettingsStore(root)), new Capabilities(), provider);
        return (knowledge, selected.Value.Id, imported.Value!.Collection.Id, service, provider);
    }
    [Fact]
    public async Task NativeRenderConsentCacheAndQuality_AreBoundToSelectedPages()
    {
        var (knowledge, library, collection, service, provider) = await Setup();
        var prepared = await service.PrepareAsync(library, collection, [1]); Assert.True(prepared.Ok, prepared.Error?.Message);
        Assert.Contains("页序：1", prepared.Value!.Preview);
        Assert.False((await service.RunAsync(prepared.Value, false)).Ok); Assert.Equal(0, provider.Calls);
        Assert.True((await service.RunAsync(prepared.Value, true)).Ok); Assert.Equal(1, provider.Calls);
        var snapshot = Assert.Single(await knowledge.CollectionsAsync(library));
        Assert.Equal(1, snapshot.Collection.ReadyCount);
        Assert.Equal("ocrReady", snapshot.Manifest.Entries[0].State);
        Assert.Contains("unverified", snapshot.Manifest.Entries[0].QualityFlags);
        Assert.Contains("visualReviewRequired", snapshot.Manifest.Entries[0].QualityFlags);
        Assert.Equal("discovered", snapshot.Manifest.Entries[1].State);
        var reference = Assert.Single(snapshot.References); Assert.Contains("合成识别", (await knowledge.ResolveAsync(reference)).Text);
        Assert.True((await service.RunAsync(prepared.Value, true)).Ok); Assert.Equal(1, provider.Calls);
        var reindexed = await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(Path.Combine(root, "scan.pdf")), library);
        Assert.True(reindexed.Ok); Assert.NotNull(reindexed.Value!.Structure);
        Assert.Equal("ocrReady", reindexed.Value.Manifest.Entries[0].State);
        Assert.Equal("discovered", reindexed.Value.Manifest.Entries[1].State);
        Assert.True((await service.RunAsync(prepared.Value, true)).Ok); Assert.Equal(1, provider.Calls);
        Assert.Contains("合成识别", (await knowledge.ResolveAsync(reference)).Text);
        var next = await service.PrepareAsync(library, collection, [2]); Assert.True((await service.RunAsync(next.Value!, true)).Ok);
        Assert.Equal(2, (await knowledge.CollectionsAsync(library)).Single().Collection.ReadyCount);
        Assert.Contains("合成识别", (await knowledge.ResolveAsync(reference)).Text);
    }
    [Fact]
    public async Task CancelledReplyAndOversizedBatch_NeverPublishOrExpandScope()
    {
        var (knowledge, library, collection, service, provider) = await Setup();
        Assert.False((await service.PrepareAsync(library, collection, Enumerable.Range(1, 21).ToArray())).Ok);
        var review = (await service.PrepareAsync(library, collection, [1])).Value!;
        using var cancellation = new CancellationTokenSource(); provider.During = () => { cancellation.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(review, true, ct: cancellation.Token));
        Assert.Equal(0, (await knowledge.CollectionsAsync(library)).Single().Collection.ReadyCount);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
