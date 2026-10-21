using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P9DesktopCheck
{
    private sealed class Web : IWebSources
    {
        public int Calls;
        public Task<IReadOnlyList<WebHit>> SearchAsync(string query, CancellationToken ct)
        { Calls++; return Task.FromResult<IReadOnlyList<WebHit>>([new("二分查找", "https://oi.wiki/basic/binary/"), new("前缀和", "https://oi.wiki/basic/prefix-sum/")]); }
        public Task<WebPage> FetchAsync(string url, CancellationToken ct)
        { Calls++; return Task.FromResult(new WebPage(url, url.Contains("binary") ? "二分查找" : "前缀和", url.Contains("binary") ? "二分查找用于有序数组，维护半开区间不变量，复杂度O(log n)。" : "前缀和用于求区间和。下标从1开始，$p[0]=0$，$p[i]=p[i-1]+a[i]$；闭区间$[l,r]$的和为$p[r]-p[l-1]$。", DateTimeOffset.UnixEpoch)); }
    }
    private sealed class Capabilities : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string model, CancellationToken ct) => Task.FromResult(new ModelCapabilities(model, 1048576, 16384, true, DateTimeOffset.UtcNow, true)); }
    private sealed class Provider : IModelProvider
    {
        public string[] Questions { get; set; } = [];
        public bool Invalid { get; set; }
        public List<ApprovedModelRequest> Requests { get; } = [];
        public string Destination => "local:p9-fixture";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            if (Invalid) return Task.FromResult(Result<ProviderReply>.Success(new("{\"schemaVersion\":2}", true)));
            var candidate = new PlanCandidate("P9取材", "基础", "二分与前缀和", 120, "Adaptive", [], Questions, [], ["s1", "s2"],
                [new("n1", "二分查找", "theory", ["理解二分查找不变量"], [], 30, null), new("n2", "前缀和", "theory", ["理解前缀和区间和"], [], 30, null)],
                2, Strategy: new("算法概念", ["theory"], "本夹具只核对两课取材", [], [], []), Coverage: []);
            return Task.FromResult(Result<ProviderReply>.Success(new(JsonSerializer.Serialize(candidate, Wire.Options), true, Model: request.Model)));
        }
    }
    public static async Task RunAsync(string root, string evidence)
    {
        Directory.CreateDirectory(evidence); var checks = new List<string>();
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            if (!File.Exists(Path.Combine(root, "p9-synthetic.marker"))) throw new InvalidDataException("Synthetic marker required");
            Directory.CreateDirectory(Path.Combine(root, "library"));
            var web = new Web(); var knowledge = new KnowledgeStore(root); var plans = new SqlitePlanningStore(root); var learning = new SqliteLearningStore(root, plans);
            var settings = new SettingsService(new SqliteSettingsStore(root)); var provider = new Provider(); var capabilities = new Capabilities();
            var planningService = new PlanningService(plans, knowledge, settings, new(provider, capabilities), new PlanParser(), learning);
            var planning = new PlanningViewModel(planningService, learning, new WebLearningService(web, knowledge, knowledge, Path.Combine(root, "library")));
            var main = (MainWindow)System.Windows.Application.Current.MainWindow; main.ChatPanel.DataContext = planning;
            var existing = (await plans.ListAsync()).SingleOrDefault(); var restart = existing is not null;
            if (!restart)
            {
                planning.Request = "二分与前缀和"; planning.Background = "基础"; planning.Goal = "二分与前缀和"; planning.Minutes = "120";
                await planning.AcquireWebAsync(); Check(planning.Status.Contains("网页正文已保存"), "goal-only-acquisition: " + planning.Status);
                Check(planning.WebReport.Contains("阿里云") && planning.WebReport.Contains("待用户核验"), "publisher-and-mainland-evidence-separated");
                Check(planning.WebReport.Contains("正文资格：body"), "body-qualification-visible");
                Check(!planning.Preview.Contains("重定向"), "simple-main-status");
                await planning.SaveAsync([]); await planning.PrepareAsync(); await planning.ProposeAsync(true);
                var proposed = (await plans.ListAsync()).Single(); Check(proposed.Plan is not null, "real-planning-service-fixture: " + planning.Status);
                planning.AckFrozen = true; await planning.ConfirmAsync(); existing = (await plans.ListAsync()).Single();
                Check(existing.Confirmed, "explicit-plan-confirmation");
            }
            else
            {
                await planning.LoadAsync(); Check(existing!.Confirmed, "restart-plan-confirmation"); Check(web.Calls == 0, "restart-no-background-fetch");
            }
            var draft = existing!; var generation = new GenerationService(plans, learning, learning, knowledge, settings, new(provider, capabilities), new TheoryLessonParser());
            var first = await generation.PrepareAsync(draft.Id, "n1"); var second = await generation.PrepareAsync(draft.Id, "n2");
            Check(first.Ok && second.Ok, "both-lesson-previews-ready");
            Check(first.Value!.Preview.Contains("二分查找用于") && !first.Value.Preview.Contains("前缀和用于"), "binary-only-evidence-visible");
            Check(second.Value!.Preview.Contains("前缀和用于") && !second.Value.Preview.Contains("二分查找用于"), "prefix-only-evidence-visible");
            Check((await learning.SelectionAsync(draft.Id, "n1"))?.Sources.Count == 1, "selection-persisted");
            Check((await new SqlitePlanningStore(root).GetAsync(draft.Id))!.Plan!.Hash == draft.Plan!.Hash, "frozen-hash-preserved");
            main.WebSourcesExpander.IsExpanded = true; main.MainScroll.ScrollToVerticalOffset(400); await main.Dispatcher.InvokeAsync(main.UpdateLayout); Capture(main, Path.Combine(evidence, restart ? "restart.png" : "source-reasons.png"));
            var workspace = new WorkspaceViewModel(new(knowledge, settings, new(provider))); main.WorkspacePanel.DataContext = workspace;
            await workspace.LoadAsync();
            if (!restart)
            {
                var path = Path.Combine(root, "structure.md"); await File.WriteAllTextAsync(path, "# 定理与证明\n定理：零等于零。\n证明：逐项比较即可。\n# 例题\n例题：1+1。\n解答：2。\n");
                await workspace.ImportCollectionAsync(HostSelection.FromFilePicker(path));
            }
            Check(workspace.CollectionStructure.Contains("structure-v1") && workspace.CollectionStructure.Contains("proof"), "semantic-structure-and-relations-retained");
            main.ChatPanel.Visibility = Visibility.Visible; main.WorkspacePanel.Visibility = Visibility.Visible; main.ImportExpander.IsExpanded = true;
            main.MainScroll.ScrollToTop(); await main.Dispatcher.InvokeAsync(main.UpdateLayout); Capture(main, Path.Combine(evidence, restart ? "structure-restart.png" : "structure.png"));
            main.ImportExpander.IsExpanded = false; main.ChatPanel.Visibility = Visibility.Visible; main.DetailsExpander.IsExpanded = true; main.UpdateLayout();
            await planning.RefreshAfterDeletionAsync();
            await main.Dispatcher.InvokeAsync(main.UpdateLayout);
            Check(planning.Selected is null && planning.CanEdit && planning.Request == "" && planning.Summary == "尚无候选计划。", "delete-refresh-leaves-new-draft-even-with-frozen-course");
            var controls = Descendants(main.ChatPanel).OfType<System.Windows.Controls.TextBox>().ToArray();
            var requestBox = controls.Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == "PlanRequest");
            Check(requestBox.IsEnabled && requestBox.Text == "", "new-draft-request-control-editable");
            requestBox.Text = "我想学习算法竞赛";
            Check(planning.Request == requestBox.Text, "new-draft-real-textbox-binding-updates");
            Capture(main, Path.Combine(evidence, restart ? "new-draft-restart.png" : "new-draft.png"));
            var clarificationRoot = Path.Combine(root, "clarification-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(clarificationRoot, "library"));
            var clarificationKnowledge = new KnowledgeStore(clarificationRoot);
            var clarificationProvider = new Provider { Questions = ["是否接受已有基础默认值？", "是否有固定期限？"] };
            var clarificationSettings = new SettingsService(new SqliteSettingsStore(clarificationRoot));
            var clarificationVm = new PlanningViewModel(new(new SqlitePlanningStore(clarificationRoot), clarificationKnowledge,
                clarificationSettings, new(clarificationProvider, capabilities), new PlanParser()), web: new WebLearningService(web, clarificationKnowledge, clarificationKnowledge, Path.Combine(clarificationRoot, "library")));
            clarificationVm.Request = "二分与前缀和"; clarificationVm.Background = "基础"; clarificationVm.Goal = "二分与前缀和"; clarificationVm.Minutes = "120";
            await clarificationVm.AcquireWebAsync(); await clarificationVm.SaveAsync([]); await clarificationVm.PrepareAsync(); await clarificationVm.ProposeAsync(true);
            Check(clarificationVm.CanClarify, "clarification-window-entry-enabled");
            var clarificationWindow = new PlanClarificationWindow(clarificationVm) { Owner = main };
            clarificationWindow.Show(); await main.Dispatcher.InvokeAsync(clarificationWindow.UpdateLayout);
            T Control<T>(string id) where T : DependencyObject => Descendants(clarificationWindow).OfType<T>()
                .Single(c => System.Windows.Automation.AutomationProperties.GetAutomationId(c) == id);
            Control<System.Windows.Controls.TextBox>("ClarificationAnswer").Text = "接受默认值，没有固定期限。";
            Check(clarificationVm.ClarificationAnswer.Contains("没有固定期限"), "clarification-window-answer-binding");
            await Task.Delay(150);
            await main.Dispatcher.InvokeAsync(clarificationWindow.UpdateLayout, System.Windows.Threading.DispatcherPriority.Render);
            Check(Descendants(Control<System.Windows.Controls.ItemsControl>("ClarificationQuestions")).OfType<System.Windows.Controls.TextBlock>()
                .Count(t => t.Text.Contains("？")) == 2, "clarification-questions-rendered");
            Capture(clarificationWindow, Path.Combine(evidence, restart ? "clarification-restart.png" : "clarification.png"));
            Control<System.Windows.Controls.Button>("SaveClarification").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            for (var i = 0; i < 50 && clarificationVm.Busy; i++) await Task.Delay(100);
            Check(clarificationVm.CanSendPlan && clarificationVm.Preview.Contains("没有固定期限"), "clarification-window-save-previews-without-dispatch");
            clarificationProvider.Questions = [];
            clarificationProvider.Invalid = true;
            Control<System.Windows.Controls.Button>("RegeneratePlan").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            for (var i = 0; i < 50 && clarificationVm.Busy; i++) await Task.Delay(100);
            Check(clarificationProvider.Requests.Count == 3 && clarificationVm.CanRetryPlan && !clarificationVm.CanSendPlan && clarificationVm.Status.Contains("重复返回"), "repeated-invalid-plan-stops-early-and-enables-retry");
            var previousError=clarificationVm.Status;
            await clarificationVm.FormSubmissionAsync();
            Check(clarificationVm.Status.StartsWith(previousError,StringComparison.Ordinal) && clarificationVm.Status.Contains("请点击下方") && clarificationProvider.Requests.Count==3,"forming-again-explains-retry-and-preserves-error-without-dispatch");
            main.ChatPanel.DataContext=clarificationVm;
            await main.Dispatcher.InvokeAsync(main.UpdateLayout,System.Windows.Threading.DispatcherPriority.Render);
            var sendButton=Descendants(main.ChatPanel).OfType<System.Windows.Controls.Button>().Single(b=>System.Windows.Automation.AutomationProperties.GetAutomationId(b)=="ProposePlan");
            Check(sendButton.IsEnabled && sendButton.Content?.ToString()=="携带错误信息重试","main-send-button-explicitly-offers-repair");
            Check(clarificationVm.CanOpenClarification, "failed-plan-window-can-reopen");
            clarificationWindow.Close();
            clarificationWindow = new PlanClarificationWindow(clarificationVm); clarificationWindow.Show();
            await Task.Delay(150); clarificationWindow.UpdateLayout();
            Check(Control<System.Windows.Controls.Button>("RetryPlan").IsEnabled, "reopened-window-retry-button-enabled");
            Capture(clarificationWindow, Path.Combine(evidence, restart ? "retry-restart.png" : "retry.png"));
            clarificationProvider.Invalid = false;
            Control<System.Windows.Controls.Button>("RetryPlan").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            for (var i = 0; i < 50 && clarificationVm.Busy; i++) await Task.Delay(100);
            Check(clarificationProvider.Requests[^1].PlanRepair is { Candidate: "{\"schemaVersion\":2}" } feedback && feedback.Instructions.Contains("宿主错误"), "retry-click-includes-final-candidate-and-error");
            Check(!clarificationVm.HasClarifications && clarificationVm.Selected?.Draft.Plan is not null, "clarification-window-regenerate-resolves-questions");
            Check(!sendButton.IsEnabled && sendButton.Content?.ToString()=="发送并拟定计划","successful-plan-resets-main-send-button");
            clarificationVm.AckFrozen = true; Check(clarificationVm.CanConfirm, "clarification-result-can-be-explicitly-confirmed");
            clarificationProvider.Invalid = true;
            await clarificationVm.PrepareAsync(); await clarificationVm.ProposeAsync(true);
            Check(clarificationVm.CanRetryPlan, "subsequent-invalid-plan-retry-ready");
            clarificationVm.Request += " 修改目标";
            var calls = clarificationProvider.Requests.Count;
            await clarificationVm.RetryPlanAsync();
            Check(!clarificationVm.CanRetryPlan && clarificationProvider.Requests.Count == calls, "editing-input-invalidates-retry-without-dispatch");
            clarificationWindow.Close();
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, codeExecuted = false, realNetwork = false, manualPending = true }, Wire.Options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { checks, error = e.ToString() }, Wire.Options)); System.Windows.Application.Current.Shutdown(1);
        }
    }
    private static void Capture(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
