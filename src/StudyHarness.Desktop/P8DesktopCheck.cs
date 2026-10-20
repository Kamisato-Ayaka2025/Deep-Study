using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P8DesktopCheck
{
    private sealed class OcrFixture : IOcrProvider
    {
        public int Calls;
        public Task<Result<OcrPageResult>> RecognizeAsync(ApprovedOcrPage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Result<OcrPageResult>.Success(new("# 合成识别\n集合由不同元素组成。图示需要复核。", [], new(100, 20, 120), request.Model, OcrService.PromptVersion)));
        }
    }
    private sealed class Capabilities : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string model, CancellationToken ct) => Task.FromResult(new ModelCapabilities(model, 1048576, 16384, true, DateTimeOffset.UtcNow, true)); }
    private static PlanCandidate Candidate => new("P8合成集合课程", "基础", "集合证明", 480, "Adaptive", [], [], [], ["s1"],
        [new("n1", "充分理解集合", "theory", ["理解集合与重复元素"], [], 240, null),
         new("n2", "纸笔证明", "lab", ["证明并集交换律"], ["n1"], 120, new("independent", null, "paper-exercise", ["s1"], "纸笔推导", "exercise"))],
        2, Strategy: new("数学集合", ["theory", "exercise"], "理论后纸笔推导", ["paper-exercise"], [], []), Coverage: []);
    private sealed class Provider : IModelProvider
    {
        public string Destination => "local:p8-desktop-fixture";
        public int PlanCalls { get; private set; }
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            object value = Candidate;
            if (request.Sources[0].Id == "planning-v2" && ++PlanCalls == 1) value = Candidate with { Coverage = [new("invalid", "included", ["n99"], null)] };
            if (request.Sources[0].Id.StartsWith("theory", StringComparison.Ordinal))
            {
                var baseLesson = TheoryFixtureProvider.NewLesson("s1");
                var blocks = Enumerable.Range(0, 10).Select(i => new LessonBlock("long" + i, "paragraph",
                    $"合成渲染压力段{i}。" + string.Concat(Enumerable.Repeat("集合由确定对象组成，重复列举不会改变集合。此段仅用于长文滚动测试。", 150)))).Concat(baseLesson.Blocks).ToArray();
                value = baseLesson with { Blocks = blocks };
            }
            else if (request.Sources[0].Id == "practice-v5") value = new GeneralPractice("exercise", "paper-exercise", "并集交换律", ["证明交换律"], [],
                [new("t1", "证明A∪B=B∪A", "任意集合A、B", "任意元素在两边归属相同", "任取x，x∈A或x∈B等价于x∈B或x∈A；双向包含成立。")], "核对任意元素法的两个方向。", ["s1"]);
            return Task.FromResult(Result<ProviderReply>.Success(new(JsonSerializer.Serialize(value, Wire.Options), true, Model: request.Model)));
        }
    }
    public static async Task RunAsync(string root, string evidence)
    {
        Directory.CreateDirectory(evidence); var checks = new List<string>(); LearningWindow? reader = null; LabPreviewWindow? lab = null;
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            if (!File.Exists(Path.Combine(root, "p8-synthetic.marker"))) throw new InvalidOperationException("Synthetic marker required");
            var knowledge = new KnowledgeStore(root); var libraryPath = Path.Combine(root, "library"); Directory.CreateDirectory(libraryPath);
            var library = await knowledge.CurrentAsync() ?? (await knowledge.SwitchAsync(new(Guid.NewGuid(), 0, HostSelection.FromFolderPicker(libraryPath)))).Value!;
            var file = Path.Combine(root, "synthetic.md"); await File.WriteAllTextAsync(file, "# 集合\n集合是确定对象的全体。重复列举不改变集合。并集满足交换律，按任意元素法证明。");
            await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFilePicker(file)), library.Id);
            var reference = (await knowledge.SearchAsync(library.Id, "", [])).Fragments[0].Reference;
            var plans = new SqlitePlanningStore(root); var learning = new SqliteLearningStore(root, plans); var settings = new SettingsService(new SqliteSettingsStore(root));
            var provider = new Provider(); var capabilities = new Capabilities();
            var planning = new PlanningViewModel(new(plans, knowledge, settings, new(provider, capabilities), new PlanParser(), learning), learning);
            var main = (MainWindow)System.Windows.Application.Current.MainWindow; main.ChatPanel.DataContext = planning;
            await planning.LoadAsync(); var restart = (await plans.ListAsync()).Any(p => p.Confirmed);
            if (!restart)
            {
                var scan = Path.Combine(root, "synthetic-scan.pdf");
                var pdf = new UglyToad.PdfPig.Writer.PdfDocumentBuilder(); pdf.AddPage(300, 400); pdf.AddPage(300, 400);
                await File.WriteAllBytesAsync(scan, pdf.Build());
                var ocrProvider = new OcrFixture();
                var workspace = new WorkspaceViewModel(new(knowledge, settings, new(provider)), new(knowledge, settings, capabilities, ocrProvider));
                main.WorkspacePanel.DataContext = workspace;
                main.ChatPanel.Visibility = Visibility.Visible; main.WorkspacePanel.Visibility = Visibility.Visible; main.ImportExpander.IsExpanded = true;
                await workspace.LoadAsync(); await workspace.ImportCollectionAsync(HostSelection.FromFilePicker(scan));
                main.OcrExpander.IsExpanded = true;
                main.UpdateLayout();
                var pages = Find(main, "OcrPages") as TextBox;
                Check(pages is not null, "ocr-page-control-present");
                for (DependencyObject? ancestor = pages; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
                    if (ancestor is Expander expander) expander.IsExpanded = true;
                pages!.Text = "1-21"; pages.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); await workspace.PrepareOcrAsync();
                Check(workspace.Status.Contains("最多20页") && ocrProvider.Calls == 0, "ocr-oversized-batch-no-dispatch");
                pages.Text = "1"; pages.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); await workspace.PrepareOcrAsync();
                Check(workspace.OcrPreview.Contains("页序：1") && ocrProvider.Calls == 0, "ocr-preview-no-dispatch");
                pages.Text = "2"; pages.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); await workspace.RunOcrAsync();
                Check(ocrProvider.Calls == 0 && workspace.Status.Contains("先预览"), "ocr-page-change-invalidates-consent");
                pages.Text = "1"; pages.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); await workspace.PrepareOcrAsync(); await workspace.RunOcrAsync();
                Check(ocrProvider.Calls == 1 && workspace.Status.Contains("待人工复核"), "ocr-explicit-run-quality-visible");
                await workspace.PrepareOcrAsync(); await workspace.RunOcrAsync();
                Check(ocrProvider.Calls == 1, "ocr-ui-cache-no-dispatch");
                main.UpdateLayout(); Capture(main, Path.Combine(evidence, "ocr-workspace.png"));
                main.ImportExpander.IsExpanded = false; main.ChatPanel.Visibility = Visibility.Visible; main.DetailsExpander.IsExpanded = true; main.UpdateLayout();
                planning.NewDraft(); planning.Request = "集合证明"; planning.Background = "基础"; planning.Goal = "集合证明";
                var hours = Find(main, "PlanHours") as TextBox; Check(hours is not null && Find(main, "PlanScope") is null, "hours-control-no-type-selector");
                hours!.Text = "0.01"; hours.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); await planning.SaveAsync([reference]);
                Check(planning.Status.Contains("VALIDATION_ERROR"), "lossy-hours-rejected-in-real-control");
                hours.Text = "8"; hours.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); await planning.SaveAsync([reference]);
                await planning.PrepareAsync(); Check(planning.Preview.Contains("16384") && planning.Preview.Contains("内容修复共用") && !planning.Preview.Contains("最多8轮"), "adaptive-preview-budget-and-no-legacy-tool-text");
                await planning.ProposeAsync(true); Check(planning.Summary.Contains("数学集合") && provider.PlanCalls == 2, "automatic-strategy-visible-after-bounded-repair");
                planning.AckFrozen = true; await planning.ConfirmAsync(); Check(planning.ConfirmedSubject is not null && !planning.CanEdit, "explicit-freeze-locks-input");
            }
            var draft = (await plans.ListAsync()).Single(p => p.Confirmed); Check(draft.Plan!.Candidate.WeeklyMinutes == 480, "eight-hours-persists-as-480-minutes");
            var generation = new GenerationService(plans, learning, learning, knowledge, settings, new(provider, capabilities), new TheoryLessonParser());
            if (!restart) { Check((await generation.GenerateAsync(draft.Id, "n1")).Ok, "request-long-course"); await generation.TickAsync(); }
            reader = new(generation, draft.Id, "n1", root); reader.Show(); await reader.ReaderInitialized.Task.WaitAsync(TimeSpan.FromSeconds(30));
            for (var i = 0; i < 80 && await reader.Browser.ExecuteScriptAsync("!!document.querySelector('#long9')") != "true"; i++) await Task.Delay(100);
            Check(await reader.Browser.ExecuteScriptAsync("!!document.querySelector('#long9') && document.body.textContent.length>40000") == "true", "long-schema2-body-renders-through-last-section");
            Check(await reader.Browser.ExecuteScriptAsync("document.querySelectorAll('.question-prompt').length===2") == "true", "long-course-retains-complete-quizzes");
            Check(await reader.Browser.ExecuteScriptAsync("""
                (()=>{
                 const text='| i | S_i |\n| --- | --- |\n| 0 | 0 |\n| 1 | $3$ |\n| 2 | <img src=x onerror=alert(1)> |';
                 const block=paragraph(text);block.id='table-check';document.getElementById('content').prepend(block);
                 return block.querySelectorAll('tbody tr').length===3&&block.querySelectorAll('th').length===2&&
                   block.querySelectorAll('img,script').length===0&&block.textContent.includes('<img')&&!!block.querySelector('.katex');
                })()
                """) == "true", "table-example-rendered-with-math-and-inert-html");
            Check(await reader.Browser.ExecuteScriptAsync("""
                (()=>{
                 const block=paragraph('```cpp\nprintf("\\n");\n```\n$\\nabla f$');
                 return block.querySelector('pre').textContent==='printf("\\n");'&&!!block.querySelector('.katex');
                })()
                """) == "true", "literal-code-newline-and-tex-command-preserved");
            await using (var image = File.Create(Path.Combine(evidence, "long-reader.png"))) await reader.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, image);
            if (!restart)
            {
                var lesson = (await learning.GetLessonAsync(draft.Id, "n1")).Value!;
                foreach (var q in lesson.Questions) foreach (var option in q.Options)
                    if ((await learning.SubmitAnswerAsync(new(Guid.NewGuid(), 0, new(lesson.Identity, q.Id, option.Id)))).Value?.Correct == true) break;
            }
            var labs = new LabService(plans, learning, learning, knowledge, settings, new(provider, capabilities), new LabZipExporter());
            var model = new LabViewModel(labs, draft.Id, "n2"); lab = new(model); lab.Show(); await model.LoadAsync();
            if (!restart)
            {
                Check(model.CanGenerate, "completed-theory-unlocks-paper-exercise"); await model.GenerateAsync();
                Check(model.HasMaterial && model.Files.Count == 0 && model.Instructions.Contains("双向包含"), "general-material-visible-without-arbitrary-files");
                await model.ExportAsync(Path.GetFullPath(Path.Combine(evidence, "paper.zip")));
                using (var zip = ZipFile.OpenRead(Path.Combine(evidence, "paper.zip"))) Check(zip.Entries.Count == 1 && zip.Entries[0].FullName == "README.md", "host-readme-only-export");
                Check(!(await learning.CompletedNodesAsync(draft.Id)).Contains("n2"), "export-does-not-complete");
                lab.SelfTestAck.IsChecked = true; await model.ConfirmAsync();
            }
            Check((await learning.GetLabAttestationAsync(draft.Id, "n2"))?.CompletionKind == "userAttested", "completion-never-machine-verified");
            await lab.Dispatcher.InvokeAsync(lab.UpdateLayout); Capture(lab, Path.Combine(evidence, "paper-material.png"));
            Check((await learning.GetLessonAsync(draft.Id, "n1")).Value!.Blocks.Sum(b => b.Text?.Length ?? 0) > 40000, "long-content-persisted");
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, manualPending = true, syntheticProgressOnly = true, codeExecuted = false }, Wire.Options));
            reader.Close(); lab.Close(); System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { checks, error = e.ToString() }, Wire.Options));
            reader?.Close(); lab?.Close(); System.Windows.Application.Current.Shutdown(1);
        }
    }
    private static FrameworkElement? Find(DependencyObject root, string id)
    {
        if (root is FrameworkElement element && AutomationProperties.GetAutomationId(element) == id) return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Find(VisualTreeHelper.GetChild(root, i), id) is { } found) return found;
        return null;
    }
    private static void Capture(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
