using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P16DesktopCheck
{
    private sealed class CountingPlans(IPlanningStore inner) : IPlanningStore
    {
        public int Reads;
        public Task<PlanningDraft?> GetAsync(Guid id, CancellationToken ct = default) { Interlocked.Increment(ref Reads); return inner.GetAsync(id, ct); }
        public Task<IReadOnlyList<PlanningDraft>> ListAsync(CancellationToken ct = default) => inner.ListAsync(ct);
        public Task<Result<PlanningDraft>> StartAsync(Command<PlanningInput> c, CancellationToken ct = default) => inner.StartAsync(c, ct);
        public Task<Result<PlanningDraft>> ReviseAsync(Command<RevisePlan> c, CancellationToken ct = default) => inner.ReviseAsync(c, ct);
        public Task<Result<PlanningDraft>> SaveProposalAsync(Command<PlanDocument> c, Guid id, CancellationToken ct = default) => inner.SaveProposalAsync(c, id, ct);
        public Task<Result<PlanningDraft>> ConfirmAsync(Command<ConfirmPlan> c, CancellationToken ct = default) => inner.ConfirmAsync(c, ct);
    }
    public static async Task RunAsync(string root, string evidence, bool baseline)
    {
        var checks = new List<string>(); var samples = new List<object>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            if (!File.Exists(Path.Combine(root, "p16-synthetic.marker"))) throw new IOException("Fresh synthetic marker required");
            Directory.CreateDirectory(evidence);
            var seed = await LabFixtureProvider.SeedAsync(root);
            var plans = new CountingPlans(new SqlitePlanningStore(root));
            for (var i = 0; i < 12; i++)
            {
                var input = seed.Input with { Request = "性能课程" + i, Scope = "Math" };
                var draft = (await plans.StartAsync(new(Guid.NewGuid(), 0, input))).Value!;
                var candidate = new PlanCandidate("性能课程" + i, input.Background!, input.Goal!, 120, "Math", [], [], [], ["s1"],
                    Enumerable.Range(0, 80).Select(n => new PlannedNode("n" + n, "集合" + n, "theory", ["理解集合"], [], 20, null)).ToArray());
                var doc = PlanRules.Validate(draft.Id, input, candidate, seed.Plan!.Sources, null);
                Check(doc.Ok, "valid-plan-" + i);
                var saved = (await plans.SaveProposalAsync(new(Guid.NewGuid(), 0, doc.Value!), draft.Id)).Value!;
                Check((await plans.ConfirmAsync(new(Guid.NewGuid(), saved.Revision, new(draft.Id, doc.Value!.Hash, true)))).Ok, "confirmed-" + i);
            }
            var knowledge = new KnowledgeStore(root); var settings = new SettingsService(new SqliteSettingsStore(root));
            var learning = new SqliteLearningStore(root, plans); var provider = new FakeProvider();
            var journal = new SqlitePlanningSessionStore(root);
            var workspace = new WorkspaceViewModel(new(knowledge, settings, new(provider)));
            var planning = new PlanningViewModel(new(plans, knowledge, settings, new(provider), new PlanParser()), learning, sessionStore: journal);
            var generation = new GenerationService(plans, learning, learning, knowledge, settings, new(provider), new TheoryLessonParser());
            var window = new MainWindow(new(settings, new(provider), new SafeLog(Path.Combine(root, "p16.log"))), workspace, planning, generation, root);
            var old = System.Windows.Application.Current.MainWindow;
            System.Windows.Application.Current.MainWindow = window; window.Show(); old.Close(); await window.Ready.Task;
            window.ChatPanel.Visibility = Visibility.Collapsed; window.CoursesPanel.Visibility = Visibility.Visible;
            await window.RefreshCoursesAsync();
            for (var run = 0; run < 5; run++)
            {
                var oldCards = window.CourseList.Children.Cast<UIElement>().ToArray();
                var allocated = GC.GetTotalAllocatedBytes(true); var watch = Stopwatch.StartNew();
                await window.RefreshCoursesAsync();
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                var reused = window.CourseList.Children.Cast<UIElement>().Count(oldCards.Contains);
                samples.Add(new { kind = "course-refresh", run, milliseconds = watch.Elapsed.TotalMilliseconds, allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated, reused, count = window.CourseList.Children.Count });
                Check(window.CourseList.Children.Count == 13, "all-cards-" + run);
                if (!baseline) Check(reused == 13, "unchanged-cards-reused-" + run);
            }
            window.CourseModel.Selected = window.CourseModel.Drafts.First(d => d.Draft.Plan?.Candidate.Nodes.Count == 80);
            window.NavChatButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(1000); var beforeReads = plans.Reads;
            await Task.Delay(4500); var hiddenReads = plans.Reads - beforeReads;
            samples.Add(new { kind = "hidden-course-idle", milliseconds = 4500, planReads = hiddenReads });
            if (!baseline) Check(hiddenReads == 0, "hidden-course-no-progress-reads");
            planning.NewDraft();
            var references = Enumerable.Range(0, 500).Select(i => seed.Plan!.Sources[0].Reference with { NodeId = Guid.NewGuid(), Locator = seed.Plan.Sources[0].Reference.Locator with { SourceLabel = "资料" + i } }).ToArray();
            var events = 0; workspace.References.CollectionChanged += (_, _) => events++;
            var refWatch = Stopwatch.StartNew(); workspace.RestoreReferences(references); refWatch.Stop();
            samples.Add(new { kind = "restore-500-references", milliseconds = refWatch.Elapsed.TotalMilliseconds, events });
            Check(journal.Load()!.References.Count == 500, "all-references-durable");
            if (!baseline) Check(events == 1, "references-one-notification");
            var editTimes = new List<double>();
            for (var i = 0; i < 25; i++)
            {
                var watch = Stopwatch.StartNew(); planning.Request = "性能输入" + i; watch.Stop(); editTimes.Add(watch.Elapsed.TotalMilliseconds);
                Check(new SqlitePlanningSessionStore(root).Load()!.Request == "性能输入" + i, "each-edit-durable-" + i);
            }
            samples.Add(new { kind = "real-input", milliseconds = editTimes });
            var session = journal.Load()!; var saveWatch = Stopwatch.StartNew();
            for (var i = 0; i < 25; i++) journal.Save(session);
            samples.Add(new { kind = "unchanged-journal", iterations = 25, milliseconds = saveWatch.Elapsed.TotalMilliseconds });
            Check(journal.Load()!.Request == session.Request, "journal-reload-equivalent");
            if (!baseline)
            {
                Button Card(Guid id) => window.CourseList.Children.OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Subject-" + id);
                var seedCard = Card(seed.Id);
                var other = window.CourseModel.Drafts.First(d => d.Draft.Plan?.Candidate.Nodes.Count == 80);
                var otherCard = Card(other.Draft.Id);
                var job = (await learning.BeginLabAsync(seed.Id, "L1")).Value!;
                var lab = (await learning.PublishLabAsync(job, LabFixtureProvider.Material, seed.Plan!.Hash, seed.Plan.Sources)).Value!;
                Check((await learning.ConfirmLabAsync(seed.Id, new(Guid.NewGuid(), 0, new("L1", lab.Hash, true)))).Ok, "synthetic-lab-attestation");
                await window.RefreshCoursesAsync();
                Check(!ReferenceEquals(seedCard, Card(seed.Id)), "changed-progress-invalidates-card");
                Check(ReferenceEquals(otherCard, Card(other.Draft.Id)), "other-cards-remain-reused");
                otherCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.ContextIdle);
                Check(window.CourseCanvas.Children.OfType<Button>().Count() == 80, "cached-card-opens-complete-route");
                var firstRefresh = window.RefreshCoursesAsync();
                Check((await new SqliteSubjectLifecycle(root).DeleteAsync(seed.Id, seed.Revision, true)).Ok, "delete-synthetic-subject");
                var secondRefresh = window.RefreshCoursesAsync();
                await Task.WhenAll(firstRefresh, secondRefresh);
                Check(window.CourseList.Children.Count == 12 && !window.CourseModel.Drafts.Any(d => d.Draft.Id == seed.Id), "overlapping-refresh-does-not-resurrect-deleted-course");
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "performance.json"), JsonSerializer.Serialize(new { baseline, samples, checks, failed = 0, realNetwork = false, manualPending = true }, Wire.Options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { checks, samples, error = ex.ToString() }, Wire.Options));
            System.Windows.Application.Current.Shutdown(1);
        }
    }
}
