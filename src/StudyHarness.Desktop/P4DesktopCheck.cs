using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Desktop;

internal static class P4DesktopCheck
{
    public static async Task RunAsync(GenerationService generation, string root, string evidence, bool restart)
    {
        Directory.CreateDirectory(evidence); var checks = new List<object>(); LearningWindow? window = null;
        void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        async Task Wait(Func<Task<bool>> predicate, string description)
        {
            var end = DateTime.UtcNow.AddSeconds(45);
            while (DateTime.UtcNow < end) { if (await predicate()) return; await Task.Delay(100); }
            throw new TimeoutException(description);
        }
        async Task Check(string id, Func<Task> action)
        {
            await action(); checks.Add(new { id, status = "pass" });
        }
        try
        {
            var plan = (await generation.Planning.ListAsync()).Single(d => d.Input.Request == "P4合成集合课程");
            window = new LearningWindow(generation, plan.Id, "A", root); window.Show();
            await window.ReaderInitialized.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Wait(() => Task.FromResult(window.Browser.ReaderReady), "reader ready");
            var vm = window.Model;
            if (restart)
            {
                await Check("p4-restart-mastery-position-pause", async () =>
                {
                    await vm.OpenAsync(); var reader = vm.Reader!;
                    Assert(reader.CompletedAt is not null && reader.Questions.All(q => q.EverCorrect), "persisted mastery");
                    Assert(reader.Position?.BlockId == "code", "persisted reading position");
                    Assert((await generation.Queue.ControlAsync()).AutoPaused, "pause survives restart");
                    Assert((await generation.Learning.AttemptsAsync(plan.Id, "A")).Count == 4, "all attempts restored");
                    Assert(!await generation.TickAsync(), "no automatic dispatch after restart");
                    await Wait(async () => await window.Browser.ExecuteScriptAsync("document.querySelectorAll('.answer').length") == "2", "restored answer UI");
                    await Wait(async () => await window.Browser.ExecuteScriptAsync("window.scrollY>0 && document.getElementById('code').getBoundingClientRect().top<window.innerHeight") == "true", "actual reading anchor restored in viewport");
                });
                await Check("p4-one-click-regenerate-history-and-completion", async () =>
                {
                    var before = vm.Reader!;
                    var button = (System.Windows.Controls.Button)window.FindName("GenerateArticleButton");
                    Assert(button.IsEnabled && button.Content.ToString() == "重新生成课文", "one regenerate button available");
                    window.ConfirmRegeneration = () => true; // Acceptance of the new confirmation is explicit in this fixture.
                    button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Wait(async () => (await generation.Learning.GetLessonAsync(plan.Id, "A")).Value!.Identity.LessonHash != before.Identity.LessonHash, "one-click replacement published");
                    await vm.RefreshAsync();
                    Assert(vm.Reader!.CompletedAt is not null && vm.Reader.Questions.All(q => !q.EverCorrect), "completion preserved with fresh question mastery");
                    Assert(vm.History.Count == 1, "old version retained");
                    vm.SelectedHistory = vm.History[0]; vm.OpenHistory();
                    Assert(!vm.Reader!.CanAnswer && vm.Reader.Identity == before.Identity, "history is read-only original");
                    await vm.OpenAsync();
                    Assert((await generation.Learning.AttemptsAsync(plan.Id, "A")).Count == 4, "old attempts not erased");
                });
            }
            else
            {
                await Check("p4-consent-deny-and-fake-generation", async () =>
                {
                    vm.Selected = vm.Nodes.Single(n => n.Key == "D");
                    await vm.RefreshAsync(); await vm.OpenAsync();
                    await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert((await generation.Learning.CompletedNodesAsync(plan.Id)).Count == 0 && vm.CanGenerate &&
                        window.GenerateArticleButton.IsEnabled, "dependent theory button enabled with no completed prerequisites");
                    vm.Selected = vm.Nodes.Single(n => n.Key == "A");
                    await vm.RefreshAsync(); await vm.OpenAsync();
                    await vm.PrepareAsync(false);
                    await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert(vm.HasPreview && window.EgressPreviewText.Visibility == Visibility.Visible, "egress preview shown");
                    window.HideEgressButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert(!vm.HasPreview && window.EgressPreviewText.Visibility == Visibility.Collapsed && window.HideEgressButton.Visibility == Visibility.Collapsed, "egress preview hidden without closing window");
                    await vm.PrepareAsync(false);
                    Assert(vm.HasPreview, "egress preview reopens");
                    vm.Authorize(false); await vm.RequestAsync(false);
                    Assert(!await generation.TickAsync(), "denial prevents dispatch");
                    Assert((await generation.Queue.TasksAsync()).Single().Attempts == 0, "no charged dispatch before approval");
                    await vm.RefreshAsync();
                    var button = (System.Windows.Controls.Button)window.FindName("GenerateArticleButton");
                    Assert(button.IsEnabled, "single generation button enabled without approval step");
                    button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Wait(async () => (await generation.Queue.TasksAsync()).Any(t => t.NodeKey == "A" && t.Status == GenerationStatus.Ready), "generated lesson");
                    await vm.OpenAsync();
                    await Wait(async () => await window.Browser.ExecuteScriptAsync("document.querySelectorAll('form').length") == "2", "rendered quizzes");
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('.answer').length") == "0", "no answer disclosure");
                });
                await Check("p4-local-math-code-diagram-and-injection", async () =>
                {
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('.katex').length") == "1", "local formula rendered");
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('.diagram span').length") == "3", "diagram rendered");
                    Assert(await window.Browser.ExecuteScriptAsync("typeof window.injected") == "\"undefined\"", "code not executed");
                    await using (var capture = File.Create(Path.Combine(evidence, "reader-top.png")))
                        await window.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, capture);
                    var original = vm.Reader!;
                    window.Browser.ShowLesson(original with { Blocks = original.Blocks.Select(b => b.Id == "definition" ? b with { Text = @"成员 $x \in A$；**个数 \(|A|=2\)**；`$literal$`；$\href{https://invalid.test}{blocked}$" } : b).ToArray() });
                    await Task.Delay(250);
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('#definition .katex').length>=2") == "true", "inline and emphasized formulas rendered locally");
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelector('#definition code').textContent==='$literal$'") == "true", "inline code remains literal");
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('#definition a').length") == "0", "inline formula cannot create trusted links");
                    window.Browser.ShowLesson(original with { Blocks = original.Blocks.Select(b => b.Id == "definition" ? b with { Kind = "math", Text = @"空集是每个集合的子集：对任意集合 $A$，都有 $\varnothing \subseteq A$。" } : b.Id == "code" ? b with { Kind = "paragraph", Text = "## 定义\n\n- **并集** $A$\n- 交集\n\n1. 第一步\n2. 第二步\n\n> 提示\n\n```html\n<script>window.injected=1</script>\n```" } : b).ToArray() });
                    await Task.Delay(250);
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('#definition .katex').length===2 && document.querySelectorAll('#definition .katex-error').length===0") == "true", "prose in math block falls back to inline formulas");
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('#code ul li').length===2 && document.querySelectorAll('#code ol li').length===2 && document.querySelectorAll('#code h2,#code blockquote,#code pre').length===3") == "true", "safe Markdown blocks render");
                    Assert(await window.Browser.ExecuteScriptAsync("typeof window.injected") == "\"undefined\"", "fenced markup stays literal");
                    window.Browser.ShowLesson(original with { Questions = original.Questions.Select(q => q with
                    {
                        Prompt = q.Id == "q1" ? "下列代码输出什么？\n\n```cpp\nif (a < b) {\n    value = 2;\n}\n```\n\n- 第一条 $x^2$\n- **保留强调**\n\n<img src='https://invalid.test/a' onerror='window.injected=1'><script>window.injected=1</script>" : "普通题干不应成为标题。\n\n第二段说明。"
                    }).ToArray() });
                    await Wait(async () => await window.Browser.ExecuteScriptAsync("document.querySelectorAll('.question-prompt').length") == "2", "Markdown question containers");
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('.question-prompt h2').length===0 && getComputedStyle(document.querySelector('.question-prompt p')).fontWeight==='400'") == "true", "plain question uses normal body type");
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelector('.question-prompt pre').textContent.includes('    value = 2;') && document.querySelector('.question-prompt pre').textContent.includes('a < b')") == "true", "question code indentation and angle brackets");
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('.question-prompt li').length===2 && document.querySelectorAll('.question-prompt .katex').length===1 && document.querySelectorAll('.question-prompt strong').length===1") == "true", "question list math and explicit emphasis");
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('.question-prompt img,.question-prompt script,.question-prompt a').length===0 && typeof window.injected==='undefined' && document.querySelectorAll('.answer').length===0") == "true", "question injection blocked and answers hidden");
                    await window.Browser.ExecuteScriptAsync("document.querySelector('.question').scrollIntoView()");
                    await Task.Delay(200);
                    await using (var quizImage = File.Create(Path.Combine(evidence, "quiz-markdown.png")))
                        await window.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, quizImage);
                    window.Browser.ShowLesson(original);
                    await Task.Delay(200);
                    await window.Browser.ExecuteScriptAsync("document.getElementById('definition').scrollIntoView()");
                    await Task.Delay(150);
                    await using (var formatted = File.Create(Path.Combine(evidence, "reader-markdown.png")))
                        await window.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, formatted);
                    window.Browser.ShowLesson(original with { Blocks = original.Blocks.Select(b => b.Id == "definition" ? b with { Text = "<img src='https://invalid.test/a' onerror='window.injected=1'><script>window.injected=1</script>" } : b).ToArray() });
                    await Task.Delay(250);
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('#definition img,#definition script').length") == "0", "paragraph HTML escaped");
                    Assert(!window.Browser.CoreWebView2.Settings.AreHostObjectsAllowed, "host objects disabled");
                    window.Browser.CoreWebView2.Navigate("https://invalid.test/"); await Task.Delay(200);
                    Assert(window.Browser.CoreWebView2.Source == LessonWebView.Origin + "/index.html", "external navigation blocked");
                    window.Browser.ShowLesson(original); await Task.Delay(200);
                });
                async Task Answer(string q, string option, int count)
                {
                    await window.Browser.ExecuteScriptAsync($"document.querySelector('form[data-question=\"{q}\"] input[value=\"{option}\"]').click();document.querySelector('form[data-question=\"{q}\"]').requestSubmit();");
                    await Wait(async () => (await generation.Learning.AttemptsAsync(plan.Id, "A")).Count == count, "answer persisted");
                    await Task.Delay(200);
                }
                await Check("p4-browser-answer-bridge-and-cumulative-completion", async () =>
                {
                    await Answer("q1", "b", 1);
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelectorAll('.answer').length") == "0", "wrong answer hides explanation");
                    await Answer("q1", "a", 2);
                    await Wait(async () => await window.Browser.ExecuteScriptAsync("document.querySelectorAll('.answer').length") == "1", "correct answer reveals explanation");
                    Assert(await window.Browser.ExecuteScriptAsync("document.querySelector('.answer').textContent.startsWith('正确选项：'+current.questions[0].options.find(o=>o.id==='a').displayLabel+' · 2个')") == "true", "answer displays option text instead of internal ID");
                    await Answer("q2", "b", 3); await Answer("q2", "a", 4);
                    Assert((await generation.Learning.GetLessonAsync(plan.Id, "A")).Value!.CompletedAt is not null, "completion retained");
                    var count = (await generation.Queue.DispatchesAsync((await generation.Queue.TasksAsync()).Single().Id)).Count;
                    await vm.OpenAsync(); Assert(count == 1, "reopen needs no model");
                });
                await Check("p4-branch-and-auto-pause", async () =>
                {
                    await vm.ModeAsync(GenerationMode.Auto);
                    var tasks = await generation.Queue.TasksAsync();
                    Assert(tasks.Any(t => t.NodeKey == "C") && tasks.Any(t => t.NodeKey == "D"), "both dependent branches available without B complete");
                    var c = tasks.Single(t => t.NodeKey == "C"); await generation.CancelAsync(c.Id);
                    await vm.ModeAsync(GenerationMode.Click); await vm.ModeAsync(GenerationMode.Auto);
                    Assert((await generation.Queue.ControlAsync()).AutoPaused, "switch does not resume");
                    await Task.Delay(500); // Wait for the last answer's render/anchor restoration to settle before user scrolling.
                    await window.Browser.ExecuteScriptAsync("document.getElementById('code').scrollIntoView()");
                    await Wait(async () => (await generation.Learning.GetLessonAsync(plan.Id, "A")).Value!.Position?.BlockId == "code", "browser scroll bridge persisted anchor");
                });
            }
            await Task.Delay(300);
            await using (var preview = File.Create(Path.Combine(evidence, restart ? "reader-restart.png" : "reader.png")))
                await window.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, preview);
            // A WPF capture complements the WebView screenshot with native controls and statuses.
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(evidence, restart ? "window-restart.png" : "window.png"))) encoder.Save(stream);
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, manualPending = true }, Wire.Options));
            window.Close(); System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { checks, failed = 1, error = e.Message, type = e.GetType().Name, manualPending = true }, Wire.Options));
            window?.Close(); System.Windows.Application.Current.Shutdown(1);
        }
    }
}
