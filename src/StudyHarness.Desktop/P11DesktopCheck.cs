using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudyHarness.Contracts;
using StudyHarness.Application;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P11DesktopCheck
{
    public static async Task RunAsync(PlanningViewModel planning, MainWindow window, string root, string evidence, bool baseline)
    {
        planning = window.CourseModel; window.CourseMap.Visibility = Visibility.Visible;
        Directory.CreateDirectory(evidence);
        var samples = new List<object>();
        var interaction = new List<object>();
        var checks = new List<string>();
        try
        {
            if (!File.Exists(Path.Combine(root, "p11-synthetic.marker"))) throw new InvalidDataException("Synthetic marker required");
            await Task.Delay(1000); // Let the ordinary startup load finish.
            ((FrameworkElement)window.FindName("ChatPanel")).Visibility = Visibility.Collapsed;
            ((FrameworkElement)window.FindName("CoursesPanel")).Visibility = Visibility.Visible;
            foreach (var count in new[] { 10, 100, 300 })
            {
                // UI-only stress projection; 300 deliberately exceeds the product plan limit.
                // No unvalidated plan is saved or used for generation.
                var nodes = Enumerable.Range(0, count).Select(i => new PlannedNode("n" + i, "合成课程 " + i,
                    "theory", ["理解集合"], i >= 5 ? ["n" + (i - 5)] : [], 20, null)).ToArray();
                var plan = new PlanDocument(new("性能夹具", "基础", "集合", 120, "Math", [], [], [], [], nodes), [], [], new string('A', 64), null);
                var draft = new PlanningDraft(Guid.NewGuid(), 0, new("性能", "基础", "集合", 120, "Math", []), plan, false, null);
                for (var run = 0; run < 5; run++)
                {
                    var before = window.GraphRenderCount;
                    var watch = Stopwatch.StartNew();
                    planning.Selected = new(draft);
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    watch.Stop();
                    var renders = window.GraphRenderCount - before;
                    var buttons = ((Canvas)window.FindName("CourseCanvas")).Children.OfType<Button>().Count();
                    samples.Add(new { count, run, milliseconds = watch.Elapsed.TotalMilliseconds, renders, buttons });
                    if (buttons != count || (!baseline && renders > 1)) throw new InvalidOperationException("Graph completeness/batched render failure");
                }
            }
            if (!baseline)
            {
                void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
                // Persisted plans remain within the V1 limit. This is separate from the 300-node UI stress projection.
                var plans = new SqlitePlanningStore(root);
                var knowledge = new KnowledgeStore(root);
                var library = Path.Combine(root, "fixture-library"); Directory.CreateDirectory(library);
                var path = Path.Combine(root, "fixture.md");
                await File.WriteAllTextAsync(path, "# 集合\n\n集合是确定对象的全体。{1,2}有两个元素。重复列举不改变集合。\n");
                var selected = (await knowledge.SwitchAsync(new(Guid.NewGuid(), (await knowledge.CurrentAsync())?.Revision ?? 0, HostSelection.FromFolderPicker(library)))).Value!;
                await knowledge.ImportAsync(new(Guid.NewGuid(), 0, HostSelection.FromFilePicker(path)), selected.Id);
                var reference = (await knowledge.SearchAsync(selected.Id, "", [])).Fragments[0].Reference;
                var input = new PlanningInput("学习集合", "基础", "集合", 120, "Math", [reference]);
                var draft = (await plans.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
                var candidate = new PlanCandidate("性能课程", "基础", "集合", 120, "Math", [], [], [], ["s1"],
                    Enumerable.Range(0, 80).Select(i => new PlannedNode("n" + i, "集合 " + i, "theory", ["理解集合"], [], 20, null)).ToArray());
                var document = PlanRules.Validate(draft.Id, input, candidate, [new("s1", reference, true)], null);
                Check(document.Ok, "persisted-80-node-plan-valid");
                draft = (await plans.SaveProposalAsync(new(Guid.NewGuid(), 0, document.Value!), draft.Id)).Value!;
                draft = (await plans.ConfirmAsync(new(Guid.NewGuid(), draft.Revision, new(draft.Id, document.Value!.Hash, true)))).Value!;
                var delayed = new DelayedPlans(plans);
                var learning = new SqliteLearningStore(root, delayed);
                var provider = new CountingProvider();
                var generation = new GenerationService(delayed, learning, learning, knowledge,
                    new(new SqliteSettingsStore(root)), new(provider), new TheoryLessonParser());
                var readerWindow = new LearningWindow(generation, draft.Id, "n79", root) { ConfirmRegeneration = () => true };
                readerWindow.Show(); await readerWindow.ReaderInitialized.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Check(readerWindow.Model.CanGenerate, "last-node-can-generate");
                var generate = (Button)readerWindow.FindName("GenerateArticleButton");
                for (var run = 0; run < 5; run++)
                {
                    delayed.DelayMilliseconds = 350; // Controlled synchronous local IO; never model latency.
                    var gap = 0d; var last = Stopwatch.GetTimestamp();
                    var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(15) };
                    heartbeat.Tick += (_, _) =>
                    {
                        var now = Stopwatch.GetTimestamp();
                        gap = Math.Max(gap, Stopwatch.GetElapsedTime(last, now).TotalMilliseconds); last = now;
                    };
                    heartbeat.Start();
                    var watch = Stopwatch.StartNew();
                    generate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    var feedback = watch.Elapsed.TotalMilliseconds;
                    Check(readerWindow.Model.IsPreparing && !generate.IsEnabled, "click-immediate-busy-" + run);
                    await readerWindow.Model.GenerateAsync(); // A second click while preparing is ignored.
                    var timeout = Stopwatch.StartNew();
                    while (readerWindow.Model.IsPreparing)
                    {
                        if (timeout.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Preparation");
                        await Task.Delay(20);
                    }
                    await readerWindow.Model.RefreshAsync();
                    gap = Math.Max(gap, Stopwatch.GetElapsedTime(last).TotalMilliseconds);
                    heartbeat.Stop();
                    delayed.DelayMilliseconds = 0;
                    var tasks = await learning.TasksAsync();
                    Check(tasks.Count == run + 1, "duplicate-click-one-task-" + run);
                    Check(provider.Calls == run, "no-model-in-preparation-" + run);
                    Check(feedback <= 200 && gap <= 250, "local-response-budget-" + run);
                    interaction.Add(new { run, nodes = 80, feedbackMilliseconds = feedback, maxHeartbeatGapMilliseconds = gap,
                        localPreparationMilliseconds = watch.Elapsed.TotalMilliseconds, injectedLocalDelayPerReadMilliseconds = 350 });
                    Check(await Task.Run(() => generation.TickAsync()), "fixture-dispatch-" + run);
                    await readerWindow.Model.RefreshAsync();
                    Check(readerWindow.Model.Reader is not null, "reader-delivered-" + run);
                }
                Check((await plans.GetAsync(draft.Id))!.Plan!.Hash == document.Value!.Hash, "plan-hash-unchanged");
                Check((await learning.HistoryAsync(draft.Id, "n79")).Count == 4, "regeneration-history-preserved");
                Check((await learning.CompletedNodesAsync(draft.Id)).Count == 0, "generation-not-completion");
                delayed.DelayMilliseconds = 350;
                var cancelled = readerWindow.Model.GenerateAsync();
                await readerWindow.Model.CancelAsync(); await cancelled;
                delayed.DelayMilliseconds = 0;
                Check((await learning.TasksAsync()).Count == 5, "cancel-preparation-no-extra-task");
                Check(provider.Calls == 5, "only-five-offline-dispatches");
                await readerWindow.Model.RefreshAsync();
                delayed.DelayMilliseconds = 350;
                var closing = readerWindow.Model.GenerateAsync();
                readerWindow.Close();
                await closing;
                delayed.DelayMilliseconds = 0;
                Check((await learning.TasksAsync()).Count == 5, "close-during-preparation-no-extra-task");
                Check(!await Task.Run(() => generation.TickAsync()), "no-pending-work-after-close");
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "performance.json"), JsonSerializer.Serialize(new
            {
                baseline, samples, interaction, checks, failed = 0, manualPending = true, realNetwork = false,
                environment = new { runtime = Environment.Version.ToString(), processors = Environment.ProcessorCount, os = Environment.OSVersion.ToString() }
            }, Wire.Options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { samples, interaction, checks, error = e.ToString() }, Wire.Options));
            System.Windows.Application.Current.Shutdown(1);
        }
    }
    private sealed class CountingProvider : IModelProvider
    {
        public int Calls;
        private readonly TheoryFixtureProvider inner = new();
        public string Destination => inner.Destination;
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            // Distinct content exercises archival, not a no-op identical-material publication.
            return new TheoryFixtureProvider("性能重生成 " + Calls).SendAsync(request, cancellationToken);
        }
    }
    private sealed class DelayedPlans(IPlanningStore inner) : IPlanningStore
    {
        public int DelayMilliseconds;
        public Task<PlanningDraft?> GetAsync(Guid id, CancellationToken ct = default)
        {
            if (DelayMilliseconds > 0 && ct.WaitHandle.WaitOne(DelayMilliseconds)) ct.ThrowIfCancellationRequested();
            return inner.GetAsync(id, ct);
        }
        public Task<IReadOnlyList<PlanningDraft>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public Task<Result<PlanningDraft>> StartAsync(Command<PlanningInput> c, CancellationToken ct = default) => inner.StartAsync(c, ct);
        public Task<Result<PlanningDraft>> ReviseAsync(Command<RevisePlan> c, CancellationToken ct = default) => inner.ReviseAsync(c, ct);
        public Task<Result<PlanningDraft>> SaveProposalAsync(Command<PlanDocument> c, Guid id, CancellationToken ct = default) => inner.SaveProposalAsync(c, id, ct);
        public Task<Result<PlanningDraft>> ConfirmAsync(Command<ConfirmPlan> c, CancellationToken ct = default) => inner.ConfirmAsync(c, ct);
    }
}
