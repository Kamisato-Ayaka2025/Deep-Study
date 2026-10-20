using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P6DesktopCheck
{
    private sealed class TutorProvider : IModelProvider
    {
        public int Calls;
        public ApprovedModelRequest? LastRequest;
        public string Destination => "local:deterministic-fake";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        { Calls++; LastRequest = request; return Task.FromResult(Result<ProviderReply>.Success(new("{\"hint\":\"先自己列出零和负数的边界用例，再解释预期。\",\"expression\":null}", true))); }
    }
    public static async Task RunAsync(string root, string evidence, bool restart)
    {
        Directory.CreateDirectory(evidence); var checks = new List<string>(); Window? window = null;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            var plans = new SqlitePlanningStore(root); var store = new SqliteLearningStore(root, plans);
            if (restart) Check((await plans.ListAsync()).Count == 0 && (await store.PendingEventsAsync()).Count == 0, "restart-deletion-persists");
            var draft = await LabFixtureProvider.SeedAsync(root);
            var settings = new SettingsService(new SqliteSettingsStore(root)); var provider = new TutorProvider();
            TutoringSession Session() => new(plans, store, store, settings, new(provider));
            var labs = new LabService(plans, store, store, new KnowledgeStore(root), settings, new(new LabFixtureProvider()), new LabZipExporter());
            var lab = new LabPreviewWindow(new(labs, draft.Id, "L1"), Session(), new TutoringFiles(), draft.Id, "L1");
            window = lab; lab.Show(); await lab.Dispatcher.InvokeAsync(lab.UpdateLayout);
            Check(lab.Tutor.IsVisible && lab.Tutor.Dialogue.Text == "" && provider.Calls == 0, "lab-sidebar-empty-without-request");
            lab.Tutor.Question.Text = "请直接写完整答案";
            lab.Tutor.Code.Text = "/* selected synthetic code */";
            await lab.Tutor.PrepareAsync();
            Check(lab.Tutor.Preview.Text.Contains("selected synthetic code") && lab.Tutor.SendButton.IsEnabled && provider.Calls == 0, "exact-preview-before-dispatch");
            lab.Tutor.Code.Text = "/* changed */";
            Check(lab.Tutor.SendButton.IsEnabled && lab.Tutor.Preview.Visibility == Visibility.Collapsed, "editing-code-clears-optional-preview-keeps-send-enabled");
            await lab.Tutor.SendAsync();
            Check(provider.Calls == 1 && provider.LastRequest!.Sources.Any(s => s.Id == "selected-code" && s.Text == "/* changed */") && lab.Tutor.Dialogue.Text.Contains("边界用例") && (await store.CompletedNodesAsync(draft.Id)).Count == 0, "tutor-response-never-completes-lesson");
            await lab.Dispatcher.InvokeAsync(lab.UpdateLayout); Capture(lab, Path.Combine(evidence, restart ? "restart-tutoring.png" : "lab-tutoring.png"));
            lab.Close(); Check(lab.Tutor.Dialogue.Text == "" && lab.Tutor.Code.Text == "", "close-discards-dialogue-and-code");
            var generation = new GenerationService(plans, store, store, new KnowledgeStore(root), settings, new(new TheoryFixtureProvider()), new TheoryLessonParser(), true);
            var theory = new LearningWindow(generation, draft.Id, "T3", root, Session(), new TutoringFiles());
            window = theory; theory.Show(); await theory.ReaderInitialized.Task.WaitAsync(TimeSpan.FromSeconds(40));
            Check(theory.Tutor.IsVisible && theory.Tutor.Dialogue.Text == "", "theory-sidebar-new-session");
            theory.Tutor.Question.Text = "如何理解边界？"; await theory.Tutor.SendAsync();
            Check(theory.Tutor.Dialogue.Text.Contains("边界用例") && provider.Calls == 2, "theory-direct-send-without-preview-works");
            await theory.Dispatcher.InvokeAsync(theory.UpdateLayout); Capture(theory, Path.Combine(evidence, restart ? "restart-theory.png" : "theory-tutoring.png"));
            theory.Tutor.Dialogue.ShowMessages([new("assistant", "## 头文件\n**声明**与 *定义*：使用 `stdio.h`。\n- 标准输入输出\n1. 引入声明\n> 教学示例，不执行\n```c\n#include <stdio.h>\nint main(void) { puts(\"**hello**\"); }\n```")]);
            var paragraphs = theory.Tutor.Dialogue.Document.Blocks.OfType<Paragraph>().ToArray();
            Check(paragraphs.Any(p => p.FontSize == 20) && paragraphs.Any(p => p.Inlines.OfType<Bold>().Any(b => new TextRange(b.ContentStart, b.ContentEnd).Text == "声明")) &&
                paragraphs.Any(p => new TextRange(p.ContentStart, p.ContentEnd).Text.StartsWith("• ")), "markdown-heading-emphasis-list-rendered");
            Check(paragraphs.Any(p => p.FontFamily.Source == "Consolas" && new TextRange(p.ContentStart, p.ContentEnd).Text.Contains("puts(\"**hello**\")")), "fenced-code-preserves-literal-content");
            theory.Tutor.Dialogue.ScrollToHome();
            await theory.Dispatcher.InvokeAsync(theory.UpdateLayout);
            Capture(theory, Path.Combine(evidence, restart ? "restart-markdown.png" : "tutor-markdown.png"));
            theory.Tutor.Dialogue.ShowMessages([new("assistant", "<script>alert(1)</script> ![image](https://invalid/image) [link](file:///C:/private)")]);
            var inert = new TextRange(theory.Tutor.Dialogue.Document.ContentStart, theory.Tutor.Dialogue.Document.ContentEnd).Text;
            Check(inert.Contains("<script>") && inert.Contains("file:///C:/private") && theory.Tutor.Dialogue.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).All(i => i is Run or Bold), "html-images-links-remain-inert-text");
            theory.Tutor.Dialogue.ShowMessages([new("assistant", "```c\n#include <stdio.h>\nunfinished")]);
            Check(new TextRange(theory.Tutor.Dialogue.Document.ContentStart, theory.Tutor.Dialogue.Document.ContentEnd).Text.Contains("unfinished"), "unclosed-fence-preserves-content");
            theory.Close(); generation.Stop();
            Check((await new SqliteSubjectLifecycle(root).DeleteAsync(draft.Id, draft.Revision, true)).Ok && (await plans.ListAsync()).Count == 0, "subject-delete-after-closing-reader");
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, manualPending = true }, Wire.Options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { checks, failed = 1, error = e.ToString() }, Wire.Options));
            window?.Close(); System.Windows.Application.Current.Shutdown(1);
        }
    }
    private static void Capture(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(path); encoder.Save(output);
    }
}
