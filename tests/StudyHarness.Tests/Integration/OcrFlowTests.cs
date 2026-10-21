using System.Security.Cryptography;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace StudyHarness.Tests.Integration;

public sealed class OcrFlowTests : IDisposable
{
    [Fact]
    public async Task NoisyColorScan_ResizesWithinByteLimitWithoutSendingOrChangingSource()
    {
        var libraryPath = Path.Combine(root, "library"); Directory.CreateDirectory(libraryPath);
        var knowledge = new KnowledgeStore(root);
        var library = (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(libraryPath)))).Value!.Id;
        using var png = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, png);
        var pixels = new byte[2000 * 2000 * 4]; new Random(74).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Rgba8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, 2000, 2000, 96, 96, pixels);
        await encoder.FlushAsync(); png.Seek(0);
        using var stream = png.AsStreamForRead(); var image = new byte[(int)png.Size]; await stream.ReadExactlyAsync(image);
        var pdf = new PdfDocumentBuilder(); pdf.AddPage(600, 600).AddPng(image, new(0, 0, 600, 600));
        var bytes = pdf.Build(); var path = Path.Combine(root, "noisy.pdf"); await File.WriteAllBytesAsync(path, bytes);
        var snapshot = (await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(path), library)).Value!;
        var rendered = Assert.Single(await knowledge.RenderPagesAsync(library, snapshot.Collection.Id, [1], default));
        Assert.EndsWith("-v2", rendered.RenderVersion); Assert.InRange(rendered.Png.Length, 1, 4194304);
        using var bounded = new MemoryStream(rendered.Png);
        var decoded = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(bounded.AsRandomAccessStream());
        Assert.True((long)decoded.PixelWidth * decoded.PixelHeight <= 4000000);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))));
        Assert.Equal(0, Assert.Single(await knowledge.CollectionsAsync(library)).Collection.ReadyCount);
    }
    [Fact]
    public async Task ChangedPdf_ReturnsActionablePreparationErrorWithoutDispatch()
    {
        var (_, library, collection, service, provider) = await Setup();
        await File.AppendAllTextAsync(Path.Combine(root, "scan.pdf"), "changed");
        var result = await service.PrepareAsync(library, collection, [1]);
        Assert.False(result.Ok); Assert.Contains("PDF_SOURCE_INVALID", result.Error!.Message);
        Assert.Equal(0, provider.Calls);
    }
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
    [Fact]
    public async Task ImportedScanAutomaticallyBindsRecognizedTextAndAdvancesSuggestion()
    {
        var (knowledge, library, collection, service, provider) = await Setup();
        var workspace = new StudyHarness.Desktop.WorkspaceViewModel(new(knowledge, new(new SqliteSettingsStore(root)), new(new FakeProvider())), service);
        await workspace.LoadAsync();
        await workspace.ImportAsync(HostSelection.FromPlanFilePicker(Path.Combine(root, "scan.pdf")));
        Assert.Single(workspace.PendingCollections); Assert.Empty(workspace.References);
        workspace.OcrPages = "1";
        await workspace.PrepareOcrAsync(); Assert.Equal(0, provider.Calls);
        await workspace.RunOcrAsync();
        Assert.Equal(1, provider.Calls); Assert.Empty(workspace.PendingCollections); Assert.Single(workspace.References);
        var old = workspace.References[0]; Assert.Equal("2", workspace.OcrPages);
        await workspace.RunOcrAsync(); Assert.Equal(1, provider.Calls);
        await workspace.PrepareOcrAsync(); await workspace.RunOcrAsync();
        Assert.Equal(2, provider.Calls); Assert.Single(workspace.References); Assert.Empty(workspace.OcrPages);
        Assert.NotEqual(old.VersionId, workspace.References[0].VersionId);
        Assert.Contains(workspace.References[0], (await knowledge.CollectionsAsync(library)).Single().References);
        Assert.NotNull((await knowledge.ResolveAsync(old)).Text);
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    [Fact]
    public async Task AutomaticScan_CancelRestartBatchesAndDeleteRetainSource()
    {
        var (knowledge, library, _, _, provider) = await Setup();
        var path = Path.Combine(root,"long-scan.pdf"); var pdf = new PdfDocumentBuilder();
        for (var i = 0; i < 23; i++) pdf.AddPage(150,200);
        var bytes = pdf.Build(); await File.WriteAllBytesAsync(path,bytes);
        var imported = (await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(path),library)).Value!;
        var id = imported.Collection.Id;
        var settings = new SettingsService(new SqliteSettingsStore(root));
        var scanner = new AutomaticPdfScanService(knowledge,settings,new Capabilities(),provider);
        using var stop = new CancellationTokenSource();
        provider.During = () => { if (provider.Calls == 2) stop.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.RunAsync(library,id,true,ct:stop.Token));
        Assert.Single((await knowledge.ScanStateAsync(library,id,default)).ReadyPages);
        provider.During = null;
        var restarted = new KnowledgeStore(root);
        scanner = new(restarted,settings,new Capabilities(),provider);
        var progress = new List<PdfScanProgress>();
        Assert.True((await scanner.RunAsync(library,id,true,new InlineProgress<PdfScanProgress>(progress.Add))).Ok);
        Assert.Equal(24,provider.Calls); // The interrupted page may be charged; only it is retried.
        Assert.Equal(23,progress[^1].Completed); Assert.Contains(progress,p => p.Batch == 2);
        Assert.True((await scanner.RunAsync(library,id,true)).Ok); Assert.Equal(24,provider.Calls);
        var ready = (await restarted.CollectionsAsync(library)).Single(c => c.Collection.Id == id);
        var reference = ready.References[0];
        await restarted.DeletePdfAsync(library,id,default);
        Assert.Null((await restarted.ResolveAsync(reference)).Text);
        var again = (await restarted.ImportCollectionAsync(HostSelection.FromFilePicker(path),library)).Value!;
        Assert.Equal(0,again.Collection.ReadyCount); Assert.Equal(bytes,await File.ReadAllBytesAsync(path));
    }
    [Fact]
    public async Task StagedPageSurvivesUncommittedRestartAndDeleteCannotResurrectIt()
    {
        var (knowledge,library,collection,_,_) = await Setup();
        var page=(await knowledge.RenderPagesAsync(library,collection,[1],default)).Single();
        var result=new OcrPageResult("# Durable page\nA successfully recognized network page.",["unverified","visualReviewRequired"],new(10,10,20),"model",OcrService.PromptVersion);
        await knowledge.StageOcrAsync(library,page,result,default);
        Assert.Empty((await knowledge.ScanStateAsync(library,collection,default)).ReadyPages);
        var restarted=new KnowledgeStore(root);
        await restarted.CommitStagedOcrAsync(library,collection,default);
        Assert.Single((await restarted.ScanStateAsync(library,collection,default)).ReadyPages);
        await restarted.CommitStagedOcrAsync(library,collection,default);
        Assert.Single((await restarted.ScanStateAsync(library,collection,default)).ReadyPages);
        await restarted.StageOcrAsync(library,page,result,default);
        await restarted.DeletePdfAsync(library,collection,default);
        await restarted.CommitStagedOcrAsync(library,collection,default);
        Assert.Empty(await restarted.CollectionsAsync(library));
        await Assert.ThrowsAsync<IOException>(()=>restarted.StageOcrAsync(library,page,result,default));
    }
    [Fact]
    public async Task LongBookPlanIncludesTailEvidenceWithinBoundedContext()
    {
        var (knowledge,library,_,_,_) = await Setup();
        var pdf=new PdfDocumentBuilder(); var font=pdf.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        for(var i=1;i<=120;i++) pdf.AddPage(300,400).AddText(i==16 ? "Contents: Networks .... 1. Security .... 110." : $"Network chapter page {i}. Protocols connect hosts. Tail evidence {i}.",10,new UglyToad.PdfPig.Core.PdfPoint(20,200),font);
        var path=Path.Combine(root,"long-text.pdf"); await File.WriteAllBytesAsync(path,pdf.Build());
        var book=(await knowledge.ImportCollectionAsync(HostSelection.FromFilePicker(path),library)).Value!;
        var service=new PlanningService(new SqlitePlanningStore(root),knowledge,new(new SqliteSettingsStore(root)),new(new FakeProvider(),new Capabilities()),new PlanParser(),explicitSourcesOnly:true);
        var draft=(await service.StartAsync(new(Guid.NewGuid(),0,new("Study networks",null,null,null,"Adaptive",[book.References[0]])))).Value!;
        var prepared=await service.PrepareAsync(draft.Id,draft.Revision);
        Assert.True(prepared.Ok,prepared.Error?.Message);
        var sources=prepared.Value!.Review.Sources;
        Assert.Contains(sources,s=>s.Kind==ModelSourceKind.OriginalMaterial && s.Text.Contains("Tail evidence 120"));
        Assert.Contains(sources,s=>s.Kind==ModelSourceKind.OriginalMaterial && s.Text.Contains("Contents: Networks"));
        Assert.Contains("长教材处理",sources[0].Text);
        Assert.InRange(sources.Where(s=>s.Kind==ModelSourceKind.OriginalMaterial).Sum(s=>s.Text.Length),1,60000);
        Assert.NotNull(prepared.Value.Review.Budget);
    }
    [Fact]
    public async Task DeleteDuringScanWaitsForCancellationAndRejectsLateReply()
    {
        var (knowledge,_,_,_,provider)=await Setup();
        var settings=new SettingsService(new SqliteSettingsStore(root));
        var workspace=new StudyHarness.Desktop.WorkspaceViewModel(new(knowledge,settings,new(new FakeProvider())),automaticScan:new(knowledge,settings,new Capabilities(),provider));
        await workspace.LoadAsync();
        var arrived=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.During=async()=>{ if(provider.Calls==2) { arrived.TrySetResult(); await release.Task; } };
        var importing=workspace.ImportAsync(HostSelection.FromPlanFilePicker(Path.Combine(root,"scan.pdf")));
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var deleting=workspace.DeleteMaterialAsync(Assert.Single(workspace.Materials));
        Assert.False(deleting.IsCompleted);
        release.TrySetResult(); await Task.WhenAll(importing,deleting);
        Assert.Empty(workspace.Collections); Assert.Empty(workspace.Materials); Assert.Empty(workspace.References);
        Assert.Contains("扫描缓存已删除",workspace.Status); Assert.True(File.Exists(Path.Combine(root,"scan.pdf")));
    }
}
