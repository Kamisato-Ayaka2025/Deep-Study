using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P12DesktopCheck
{
    private sealed class PlansProvider : IModelProvider
    {
        public string Destination => "local:p12";
        public int Calls;
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            Calls++;
            var p = new PlanCandidate("集合与数学思维", "基础", "理解集合", 120, "Math", [], [], [], ["s1"],
                [new("n1","集合与元素","theory",["理解集合"],[],30,null),new("n2","集合的运算","theory",["理解交集与并集"],["n1"],30,null),
                 new("n3","用集合描述问题","theory",["描述简单问题"],["n1","n2"],30,null)]);
            return Task.FromResult(Result<ProviderReply>.Success(new(JsonSerializer.Serialize(p, Wire.Options), true, ReadSourceIds:["s1"], ToolCalls:1)));
        }
    }
    public static async Task RunAsync(string root, string evidence)
    {
        var checks = new List<string>(); Directory.CreateDirectory(evidence);
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Wait(Func<bool> condition)
        {
            for (var i = 0; i < 200; i++) { if (condition()) return; await Task.Delay(50); }
            throw new TimeoutException("Desktop condition timed out");
        }
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        try
        {
            if (!File.Exists(Path.Combine(root,"p12-synthetic.marker"))) throw new InvalidDataException("Synthetic root required");
            var knowledge = new KnowledgeStore(root);
            var library = (await knowledge.CurrentAsync())!;
            var store = new SqlitePlanningStore(root); var learning = new SqliteLearningStore(root,store);
            var settings = new SettingsService(new SqliteSettingsStore(root)); var provider = new PlansProvider();
            var service = new PlanningService(store,knowledge,settings,new(provider),new PlanParser(),explicitSourcesOnly:true);
            var planning = new PlanningViewModel(service,learning,sessionStore:new SqlitePlanningSessionStore(root));
            var workspace = new WorkspaceViewModel(new WorkspaceService(knowledge,settings,new(provider)));
            var theory = new TheoryFixtureProvider();
            var generation = new GenerationService(store,learning,learning,knowledge,settings,new(theory),new TheoryLessonParser());
            var shell = new ShellViewModel(settings,new(provider),new SafeLog(Path.Combine(root,"p12.log")));
            var old = System.Windows.Application.Current.MainWindow;
            var window = new MainWindow(shell,workspace,planning,generation,root,
                tutoringFactory:() => new TutoringSession(store,learning,learning,settings,new(provider)),
                files:new TutoringFiles(),subjectLifecycle:new SqliteSubjectLifecycle(root));
            window.Show(); System.Windows.Application.Current.MainWindow = window; old.Close();
            await window.Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await Wait(() => planning.RecoveryStatus.Length > 0 && !planning.Busy);
            await Task.Delay(200);
            var restart = (await store.ListAsync()).Any(d => d.Confirmed);
            Check(window.Title == "Deep Study","brand");
            Check(window.FindName("NavWorkspace") is null,"no-debug-navigation");
            if (!restart)
            {
                planning.Request = "学习集合"; planning.Background = "基础"; planning.Goal = "理解集合"; planning.Hours = "2"; planning.Scope = "Math";
                planning.WebTopic = "集合教材"; planning.WebSite = "example.org";
                var path = Path.Combine(root,"fixture.c"); await File.WriteAllTextAsync(path,"// 集合是确定对象的全体。\nint count = 2;");
                await workspace.ImportAsync(HostSelection.FromPlanFilePicker(path));
                Check(workspace.References.Count == 1,"single-code-import-auto-bound");
                window.ImportExpander.IsExpanded = true;
                window.UpdateLayout(); await Task.Delay(100);
                var remove = FindButtons(window).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "RemoveMaterial");
                await planning.FormSubmissionAsync(); Check(planning.CanSendPlan, "import-preview-before-removal");
                Click(remove);
                Check(workspace.References.Count == 0 && planning.Preview == "" && !planning.CanSendPlan, "remove-button-invalidates-submission");
                Check(new SqlitePlanningSessionStore(root).Load()!.References.Count == 0 && File.Exists(path), "remove-button-durable-source-preserved");
                await workspace.ImportAsync(HostSelection.FromPlanFilePicker(path));
                Check(workspace.Materials.Count == 1, "reimport-one-material-row");
                window.MainScroll.ScrollToVerticalOffset(580);
                await Screenshot(window,evidence,"import-removal");
                var scan = new UglyToad.PdfPig.Writer.PdfDocumentBuilder(); scan.AddPage(400, 500);
                var scanPath = Path.Combine(root, "scanned-book.pdf"); await File.WriteAllBytesAsync(scanPath, scan.Build());
                await workspace.ImportAsync(HostSelection.FromPlanFilePicker(scanPath));
                Check(workspace.Materials.Count == 2 && workspace.OcrPages == "1", "scanned-book-visible-auto-range");
                Click(FindButtons(window).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "PreparePlan"));
                await Wait(() => planning.Status.Contains("尚无可读正文"));
                Check(window.ImportExpander.IsExpanded && planning.Preview == "", "scan-submit-explains-next-step");
                window.UpdateLayout(); await Task.Delay(100);
                window.MainScroll.ScrollToVerticalOffset(650);
                Check(FindTexts(window).Any(t => t.Text.Contains("scanned-book.pdf")), "material-row-shows-book-name");
                await Screenshot(window, evidence, "scanned-import");
                Click(FindButtons(window).Single(b => b.DataContext is MaterialChoice { PendingId: not null } &&
                    System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "RemoveMaterial"));
                await Wait(() => !workspace.Busy && workspace.Materials.Count == 1);
                Check(workspace.Materials.Count == 1 && planning.PendingCollections.Count == 0, "remove-scanned-book-keeps-other-source");
                var journal = new SqlitePlanningSessionStore(root).Load()!;
                Check(journal.Request == planning.Request && journal.WebSite == "example.org" && journal.References.Count == 1,"form-durable-before-submit");
                Click(window.NavCoursesButton); await Task.Delay(150);
                Check(planning.Request == "学习集合","course-navigation-preserves-draft");
                Click(window.NavChatButton);
                await planning.FormSubmissionAsync(); Check(planning.CanSendPlan,"form-submission-saves-and-previews");
                await planning.ProposeAsync(false); Check(planning.Preview == "" && provider.Calls == 0,"deny-clears-no-dispatch");
                await planning.FormSubmissionAsync(); await planning.ProposeAsync(true);
                Check(planning.Summary.Contains("集合与数学思维"),"plan-through-same-send-chain");
                planning.AckFrozen = true;
                var confirm = FindButtons(window).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "ConfirmPlan");
                Click(confirm); await Wait(() => planning.Request == "" && !planning.Busy);
                Check(workspace.References.Count == 0,"confirm-clears-current-material-selection");
                Check((await store.ListAsync()).Count(d => d.Confirmed) == 1,"confirm-retains-course");
            }
            else
            {
                Check(planning.Request == "下次继续学习线性代数" && planning.Hours == "未填写完","restart-restores-incomplete-input");
                Check(planning.WebSite == "example.org" && planning.ClarificationAnswer == "保留这段尚未提交的回答","restart-restores-detail-and-answer");
                Check(provider.Calls == 0 && theory.Calls == 0,"restart-zero-provider-dispatch");
            }
            Click(window.NavCoursesButton); await Task.Delay(250);
            await window.RefreshCoursesAsync();
            var card = FindButtons(window.CourseList).Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b).StartsWith("Subject-"));
            Check(window.CourseMap.Visibility == Visibility.Collapsed,"courses-start-with-cards");
            await Screenshot(window,evidence,restart ? "restart-courses" : "courses");
            Click(card); await Task.Delay(100);
            Check(window.CourseMap.Visibility == Visibility.Visible,"card-opens-route");
            var nodes = window.CourseCanvas.Children.OfType<Button>().OrderBy(Canvas.GetTop).ToArray();
            Check(nodes.Length == 3,"all-nodes-visible-in-route");
            Check(System.Windows.Automation.AutomationProperties.GetAutomationId(nodes[0]) == "Course-n1","first-lesson-at-top");
            Check(nodes.Zip(nodes.Skip(1)).All(p => Canvas.GetTop(p.First) + p.First.Height < Canvas.GetTop(p.Second)),"no-node-overlap");
            Check(window.CourseCanvas.Children.OfType<System.Windows.Shapes.Line>().All(l => l.X1 == l.X2),"noncrossing-route");
            Click(nodes[2]); Check(window.CourseModel.Detail.Contains("n1、n2"),"full-multiple-prerequisite-details");
            Check(!System.Windows.Application.Current.Windows.OfType<LearningWindow>().Any(),"single-click-only-details");
            await Screenshot(window,evidence,restart ? "restart-route" : "route");
            nodes[0].RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
            var lesson = System.Windows.Application.Current.Windows.OfType<LearningWindow>().Single();
            await lesson.ReaderInitialized.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (!restart)
            {
                Check(lesson.Model.GenerateLabel == "点击自动生成课文","empty-lesson-label");
                Click((Button)lesson.FindName("GenerateArticleButton"));
                await Wait(() => lesson.Model.Reader is not null);
            }
            Check(lesson.Model.GenerateLabel == "重新生成课文","existing-lesson-label");
            var before = theory.Calls;
            lesson.ConfirmRegeneration = () => false;
            Click((Button)lesson.FindName("GenerateArticleButton")); await Task.Delay(200);
            Check(theory.Calls == before,"decline-regeneration-no-dispatch");
            await Screenshot(lesson,evidence,restart ? "restart-lesson" : "lesson");
            lesson.Model.Selected = lesson.Model.Nodes.Single(n => n.Key == "n2");
            await lesson.Model.OpenAsync(); await lesson.Model.RefreshAsync();
            Check(lesson.Model.GenerateLabel == "点击自动生成课文" && !lesson.Model.HasCurrentLesson && lesson.Model.Reader is null,"switch-ready-to-empty-no-stale-label");
            lesson.Close();
            Click(window.NavChatButton);
            planning.Request = "下次继续学习线性代数"; planning.Hours = "未填写完"; planning.WebSite = "example.org"; planning.ClarificationAnswer = "保留这段尚未提交的回答";
            await Screenshot(window,evidence,restart ? "restart-chat" : "chat");
            window.Width = 960; window.Height = 700;
            await Screenshot(window,evidence,restart ? "restart-compact" : "compact");
            Check(window.ActualWidth >= 960 && window.ChatPanel.ActualWidth > 600,"compact-layout-width");
            Click(window.NavSettingsButton); await Screenshot(window,evidence,restart ? "restart-settings" : "settings");
            Check(window.SettingsPanel.Visibility == Visibility.Visible,"settings-reachable");
            shell.Model = " bad model ";
            Check(!await shell.SaveAsync(null) && shell.Status.Contains("VALIDATION_ERROR"),"settings-invalid-model-rejected");
            shell.Model = "deepseek-chat";
            Check(await shell.SaveAsync(null),"settings-valid-save");
            await shell.LoadAsync(); Check(shell.Model == "deepseek-chat","settings-persisted");
            await File.WriteAllTextAsync(Path.Combine(evidence,restart ? "restart.json" : "ui.json"),JsonSerializer.Serialize(new { checks, failed=0, realNetwork=false, codeExecuted=false, manualPending=true },Wire.Options));
            if (!restart) { File.WriteAllText(Path.Combine(evidence,"crash-ready"),"Input committed; terminate without graceful shutdown."); await Task.Delay(Timeout.Infinite); }
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence,"failure.json"),JsonSerializer.Serialize(new { checks,error=ex.ToString() },Wire.Options));
            System.Windows.Application.Current.Shutdown(1);
        }
    }
    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        if (root is Button button) yield return button;
        for (var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach (var child in FindButtons(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
    private static IEnumerable<TextBlock> FindTexts(DependencyObject root)
    {
        if (root is TextBlock text) yield return text;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in FindTexts(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static async Task Screenshot(Window window,string folder,string name)
    {
        await window.Dispatcher.InvokeAsync(window.UpdateLayout,DispatcherPriority.ApplicationIdle);
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(folder,name+".png")); encoder.Save(stream);
    }
}
