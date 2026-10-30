using System.IO;
using System.Text.Json;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P23DesktopCheck
{
    private sealed class Capabilities : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string m, CancellationToken ct = default) => Task.FromResult(new ModelCapabilities(m, 1048576, 16384, true, DateTimeOffset.UtcNow, true)); }
    private sealed class Ocr : IOcrProvider
    {
        public int Calls;
        public bool Fail;
        public Task<Result<OcrPageResult>> RecognizeAsync(ApprovedOcrPage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) return Task.FromResult(Result<OcrPageResult>.Fail(ErrorCode.INVALID_MODEL_OUTPUT, "synthetic scan failure"));
            return Task.FromResult(Result<OcrPageResult>.Success(new("Diagram: the arrow points from sender to receiver. Page " + request.Page.Page,
                [], new(10, 10, 20), request.Model, OcrService.PromptVersion)));
        }
    }
    internal static async Task RunAsync(string root, string evidence)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            if (!File.Exists(Path.Combine(root, "p23-synthetic.marker"))) throw new IOException("Synthetic root required");
            var knowledge = new KnowledgeStore(root); var settings = new SettingsService(new SqliteSettingsStore(root));
            var provider = new FakeProvider(); var ocr = new Ocr(); var cap = new Capabilities();
            var workspace = new WorkspaceViewModel(new(knowledge, settings, new(provider)), new(knowledge, settings, cap, ocr), new(knowledge, settings, cap, ocr));
            var planning = new PlanningViewModel(new(new SqlitePlanningStore(root), knowledge, settings, new(provider, cap), new PlanParser(), explicitSourcesOnly: true), sessionStore: new SqlitePlanningSessionStore(root));
            var shell = new ShellViewModel(settings, new(provider), new SafeLog(Path.Combine(root, "check.log")));
            var old = System.Windows.Application.Current.MainWindow; var window = new MainWindow(shell, workspace, planning);
            window.Show(); System.Windows.Application.Current.MainWindow = window; old.Close(); await window.Ready.Task;
            window.ImportExpander.IsExpanded = true;
            var library = (await knowledge.CurrentAsync())!;
            if (File.Exists(Path.Combine(root, "scan-queue.marker")))
            {
                var pdfPath = Path.Combine(root, "queued.pdf");
                var pdf = new UglyToad.PdfPig.Writer.PdfDocumentBuilder(); pdf.AddPage(100, 100);
                await File.WriteAllBytesAsync(pdfPath, pdf.Build());
                var nextPath = Path.Combine(root, "next.md");
                await File.WriteAllTextAsync(nextPath, "# Next material\nLocal text after the scanned book.");
                ocr.Fail = true;
                await workspace.ImportManyAsync([HostSelection.FromPlanFilePicker(pdfPath), HostSelection.FromPlanFilePicker(nextPath)]);
                Check(workspace.ImportQueueCount == 2 && workspace.CanResumeImports, "scan-failure-keeps-queue-paused");
                window.UpdateLayout();
                Check(workspace.ShowScanProgress && window.ScanProgressPanel.IsVisible, "incomplete-scan-progress-visible");
                ocr.Fail = false;
                await workspace.ContinueScanAsync();
                await System.Windows.Threading.Dispatcher.Yield(); window.UpdateLayout();
                Check(workspace.ImportQueueCount == 0 && !workspace.CanResumeImports && workspace.ImportQueueText.Length == 0, "continue-scan-drains-completed-and-next-items");
                Check(!workspace.ShowScanProgress && !window.ScanProgressPanel.IsVisible, "completed-scan-panel-hidden");
                Check(workspace.Materials.Count == 2 && workspace.PendingCollections.Count == 0, "both-materials-retained-and-ready");
                var calls = ocr.Calls;
                await workspace.ImportAsync(HostSelection.FromPlanFilePicker(pdfPath));
                window.UpdateLayout();
                Check(workspace.ImportQueueCount == 0 && !workspace.ShowScanProgress && !window.ScanProgressPanel.IsVisible, "cached-import-drains-and-stays-hidden");
                Check(ocr.Calls == calls, "completed-import-does-not-repeat-recognition");
                await P27QueueCheck.Run(root, Check);
                await File.WriteAllTextAsync(Path.Combine(evidence, "scan-queue.json"), JsonSerializer.Serialize(new { checks, failed = 0, realRemoteCalls = 0 }, Wire.Options));
                System.Windows.Application.Current.Shutdown(0); return;
            }
            if (File.Exists(Path.Combine(root, "import-only.marker")))
            {
                var local = new WorkspaceViewModel(new(knowledge, settings, new(provider)));
                window.WorkspacePanel.DataContext = local;
                await local.LoadAsync();
                var prior = Path.Combine(root, "previous.md");
                await File.WriteAllTextAsync(prior, "# Previous book\nPreviously imported local text.");
                await local.ImportAsync(HostSelection.FromPlanFilePicker(prior));
                Check(local.SelectedCollection is not null && local.ScanCompleted > 0, "old-book-selected-before-failure");
                var references = local.References.ToArray();
                var invalid = Path.Combine(root, "invalid.pdf");
                await File.WriteAllTextAsync(invalid, "Not a PDF");
                await local.ImportAsync(HostSelection.FromPlanFilePicker(invalid));
                Check(local.SelectedCollection is null, "failed-import-does-not-select-old-book");
                Check(local.ScanTotal == 0 && local.ScanCompleted == 0 && local.OcrPages.Length == 0, "failed-import-clears-stale-scan");
                Check(local.References.SequenceEqual(references), "failed-import-preserves-existing-material");
                Check(local.CanResumeImports && local.ImportQueueCount == 1, "failed-file-retained-for-retry");
                Check((await knowledge.CollectionsAsync(library.Id)).Count == 1, "failed-import-does-not-publish-book");
                Check(ocr.Calls == 0, "no-ocr-or-planning-dispatch");
                await File.WriteAllTextAsync(Path.Combine(evidence, "import-ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, realRemoteCalls = 0 }, Wire.Options));
                System.Windows.Application.Current.Shutdown(0); return;
            }
            var restart = File.Exists(Path.Combine(evidence, "ui.json"));
            var path = Path.Combine(root, "synthetic.pptx");
            if (restart) Check(workspace.Materials.Count == 1, "presentation-binding-restored");
            var defaults = new AutomaticPdfScanService(knowledge, settings, cap, ocr);
            Check(defaults.PresentationConcurrency == 8 && defaults.PdfConcurrency == 8, "all-scanning-default-eight");
            Check(window.FindName("PresentationConcurrencySelector") is null, "concurrency-selector-removed");
            var importing = workspace.ImportAsync(HostSelection.FromPlanFilePicker(path));
            if (!restart)
            {
                await Task.Delay(100);
                Check(workspace.Busy, "scan-remains-busy-until-complete");
                Check(window.ImportFileButton.IsEnabled, "import-picker-enabled-during-scan");
            }
            await importing;
            Check(!workspace.Busy, "presentation-import-finishes");
            var snapshot = (await knowledge.CollectionsAsync(library.Id)).Single();
            Check(snapshot.Collection.SourceKind == "presentation", "presentation-kind-persisted");
            Check(snapshot.Collection.EntryCount == 5 && snapshot.Collection.ReadyCount == 5, "five-slides-ready");
            Check(ocr.Calls == (restart ? 0 : 2), "only-two-diagram-slides-remote-and-restart-zero");
            Check(workspace.PendingCollections.Count == 0 && workspace.Materials.Count == 1, "ready-material-bound-to-plan");
            Check(workspace.ScanTotal == 5 && workspace.ScanCompleted == 5, "slide-progress-visible");
            var native = new List<string>();
            foreach (var reference in snapshot.References) native.Add((await knowledge.ResolveAsync(reference)).Text!);
            Check(native.Any(t => t.Contains("Teacher note")), "notes-survive-visual-merge");
            Check(native.Any(t => t.Contains("arrow points")), "visual-description-retrievable");
            var before = snapshot.Collection.Version;
            await workspace.ImportAsync(HostSelection.FromPlanFilePicker(path));
            Check(ocr.Calls == (restart ? 0 : 2), "repeat-import-zero-additional-remote-calls");
            Check((await knowledge.CollectionsAsync(library.Id)).Single().Collection.Version == before, "repeat-keeps-version");
            planning.Request = "按全部幻灯片规划计算机网络课程";
            planning.SetReferences([snapshot.References.First()]);
            await planning.FormSubmissionAsync();
            Check(planning.CanSendPlan, "presentation-plan-preview-ready");
            Check(planning.Preview.Contains("PPT目录d1") && planning.Preview.Contains("d1/page-5"), "compact-directory-last-page-visible");
            Check(planning.Preview.Contains("计划正文取样分布") && planning.Preview.Contains("后段新增"), "balanced-evidence-distribution-visible");
            await P27QueueCheck.Run(root, Check);
            window.UpdateLayout();
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(window); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(evidence, restart ? "restart.png" : "ui.png"))) encoder.Save(output);
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, realRemoteCalls = 0 }, Wire.Options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { checks, error = ex.ToString() }, Wire.Options));
            System.Windows.Application.Current.Shutdown(1);
        }
    }
}
