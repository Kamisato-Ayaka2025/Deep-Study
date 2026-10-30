using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P30DesktopCheck
{
    private sealed class Caps : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string model, CancellationToken ct = default) => Task.FromResult(new ModelCapabilities(model, 1048576, 32768, true, DateTimeOffset.UtcNow, true)); }
    private sealed class Knowledge : IKnowledgeStore, ICollectionKnowledge
    {
        public readonly Guid Library = Guid.Parse("30303030-3030-3030-3030-303030303030");
        public readonly Dictionary<SourceRef, string> Texts = [];
        public CollectionSnapshot Snapshot { get; }
        public Knowledge(bool presentation = false)
        {
            for (var i = 1; i <= 3; i++)
            {
                var text = $"# 合成教材第{i}节\n四位补码范围是-8到7。0111表示7，0001表示1，四位相加得到1000，表示-8。这是有符号溢出，不能把进位标志当成溢出标志。\n页尾机制{i}：时序电路需要满足建立时间和保持时间。";
                var identity = PlanRules.NodeId(Library, "page" + i);
                Texts.Add(new(Library, identity, identity, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))), new(presentation ? "P30合成课件.pptx" : "P30合成教材.pdf", i, "完整教学单元", $"page-{i}:0-{text.Length}")), text);
            }
            Snapshot = new(new(Library, presentation ? "presentation" : "pdf", "synthetic", DateTimeOffset.UnixEpoch, presentation ? "P30合成课件.pptx" : "P30合成教材.pdf", "v1", 3, 3),
                new(Library, "v1", "synthetic", true, Texts.Keys.Select(r => new CollectionEntry("page-" + r.Locator.Page, r.Locator.Page.ToString()!, "完整教学单元", "textReady", r.ContentHash, null, [])).ToArray()), Texts.Keys.ToArray());
        }
        public Task<ResolvedSource> ResolveAsync(SourceRef r, CancellationToken ct = default) => Task.FromResult(new ResolvedSource(r, Texts.GetValueOrDefault(r), null));
        public Task<IReadOnlyList<CollectionSnapshot>> CollectionsAsync(Guid id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CollectionSnapshot>>([Snapshot]);
        public Task<IReadOnlyList<KnowledgeFragment>> RetrieveAsync(Guid l, string q, IReadOnlyList<Guid> c, int b, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<KnowledgeFragment>>([]);
        public Task<IReadOnlyList<KnowledgeFragment>> RetrieveFrozenAsync(PlanCollection c, string q, IReadOnlyList<string> e, int b, CancellationToken ct = default) => throw new InvalidOperationException("Keyword fallback forbidden in this fixture");
        public Task<LibraryDto?> CurrentAsync(CancellationToken ct = default) => Task.FromResult<LibraryDto?>(null);
        public Task<Result<LibraryDto>> SwitchAsync(Command<HostSelection> c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<ImportReport>> ImportAsync(Command<HostSelection> c, Guid l, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<KnowledgeSearch> SearchAsync(Guid l, string q, IReadOnlyCollection<Guid> p, int limit = 20, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Result<CollectionSnapshot>> ImportCollectionAsync(HostSelection s, Guid l, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class Provider : IModelProvider
    {
        public int InventoryCalls;
        public bool Gap;
        public bool HoldInventory;
        public TaskCompletionSource InventoryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Destination => "local:deterministic-fake";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var prompt = request.Sources[0].Text; string text;
            if (request.Sources[0].Id == "book-knowledge")
            {
                InventoryCalls++;
                if (HoldInventory) { InventoryEntered.TrySetResult(); return AwaitCancellation(ct); }
                text = JsonSerializer.Serialize(new { points = request.Sources.Skip(1).Select(s => new { title = "进位、溢出与时序条件", detail = "区分进位和溢出，计算四位补码加法；说明建立时间和保持时间", evidenceIds = new[] { s.Id } }), nonTeachingIds = Array.Empty<string>() }, Wire.Options);
            }
            else if (request.Sources[0].Id == "knowledge-mapping")
            {
                var points = JsonSerializer.Deserialize<BookKnowledgePoint[]>(request.Sources.Single(s => s.Id == "points").Text, Wire.Options)!;
                text = JsonSerializer.Serialize(new { knowledgeCoverage = points.Select(p => new KnowledgeCoverage(p.Key, "included", ["n1"], null, [])) }, Wire.Options);
            }
            else if (request.Sources[0].Id == "planning-v2")
            {
                var keys = Regex.Matches(prompt, @"d1u\d+k\d+").Select(m => m.Value).Distinct().ToArray();
                var plan = new PlanCandidate("P30合成课程", "基础", "理解并计算", 120, "Adaptive", [], [], [], ["s1"],
                    [new("n1", "补码运算与时序条件", "theory", ["计算四位补码加法并解释溢出", "说明建立时间与保持时间"], [], 60, null)],
                    2, Strategy: new("计算机组成", ["theory"], "先定义再计算", [], [], []),
                    Coverage: Enumerable.Range(1, 3).Select(i => new PlanCoverage("d1/page-" + i, "included", ["n1"], null)).ToArray(),
                    KnowledgeCoverage: keys.Select(k => new KnowledgeCoverage(k, "included", ["n1"], null, [])).ToArray());
                text = JsonSerializer.Serialize(plan, Wire.Options);
            }
            else
            {
                var keys = Regex.Matches(prompt, @"d1u\d+k\d+|objective-[1-9]\d*").Select(m => m.Value).Distinct().ToArray();
                var sourceKeys = request.Sources.Skip(1).Select(s => s.Id).ToArray();
                var lesson = new TheoryLesson([new("h", "heading", "补码与时序"), new("p", "paragraph", "四位补码范围是-8到7。进位和有符号溢出含义不同；时序电路需满足建立时间和保持时间。"),
                    new("e", "example", "0111+0001=1000。7+1超出四位补码正数范围，产生有符号溢出。"), new("q", "question", null, "q1")],
                    [new("q1", "四位补码0111与0001相加，发生什么？", [new("a", "有符号溢出"), new("b", "结果为零"), new("c", "不发生溢出"), new("d", "结果为七")], "a", "7+1超出最大值7，结果1000解释为-8。")], sourceKeys, 3,
                    keys.Select(k => new LessonEvidenceCheck(k, !Gap, sourceKeys, ["p", "e"], Gap ? "缺少建立时间定义" : "原文给出了定义、条件和例子")).ToArray());
                text = JsonSerializer.Serialize(lesson, Wire.Options);
            }
            return Task.FromResult(Result<ProviderReply>.Success(new(text, false, Usage: new(10, 10, 20))));
        }
        private static async Task<Result<ProviderReply>> AwaitCancellation(CancellationToken ct)
        {
            await Task.Delay(10000, ct);
            return Result<ProviderReply>.Fail(ErrorCode.RUN_CANCELLED, "Synthetic cancellation fixture timed out");
        }
    }
    internal static async Task RunAsync(string root, string evidence)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            Check(File.Exists(Path.Combine(root, "p30-synthetic.marker")), "isolated-synthetic-root");
            var restart = File.Exists(Path.Combine(evidence, "ui.json"));
            var presentation = File.Exists(Path.Combine(root, "presentation-synthetic.marker"));
            var knowledge = new Knowledge(presentation); var provider = new Provider(); var caps = new Caps();
            var settings = new SettingsService(new SqliteSettingsStore(root)); var plans = new SqlitePlanningStore(root); var learning = new SqliteLearningStore(root, plans);
            var service = new PlanningService(plans, knowledge, settings, new(provider, caps), new PlanParser(), knowledgeCache: new BookKnowledgeCache(root));
            var planning = new PlanningViewModel(service, learning, sessionStore: new SqlitePlanningSessionStore(root));
            var realKnowledge = new KnowledgeStore(root);
            var workspace = new WorkspaceViewModel(new(realKnowledge, settings, new(provider)));
            var shell = new ShellViewModel(settings, new(provider), new SafeLog(Path.Combine(root, "p30.log")));
            var generation = new GenerationService(plans, learning, learning, knowledge, settings, new(provider, caps), new TheoryLessonParser());
            var old = System.Windows.Application.Current.MainWindow;
            var window = new MainWindow(shell, workspace, planning); window.Show(); System.Windows.Application.Current.MainWindow = window; old.Close(); await window.Ready.Task;
            if (!restart)
            {
                planning.Request = "深入学习计组"; planning.Background = "基础"; planning.Goal = "理解并计算"; planning.Minutes = "120";
                planning.SetReferences([knowledge.Snapshot.References[0]]); await planning.FormSubmissionAsync();
                Check(planning.CanSendPlan && planning.Preview.Contains("全书知识梳理"), "full-book-preview-visible");
                Check(provider.InventoryCalls == 0, "preview-does-not-dispatch");
                Check(planning.Preview.Contains("页尾机制3"), "tail-body-in-preview");
                await planning.ProposeAsync(false); Check(provider.InventoryCalls == 0, "reject-does-not-dispatch");
                await planning.FormSubmissionAsync(); provider.HoldInventory = true;
                var cancelling = planning.ProposeAsync(true); await provider.InventoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); planning.Cancel(); await cancelling;
                Check(!planning.Busy && (await plans.ListAsync()).All(d => d.Plan is null), "cancel-inventory-keeps-draft-unpublished");
                provider.HoldInventory = false; await planning.FormSubmissionAsync(); await planning.ProposeAsync(true);
                Check(planning.Summary.Contains("知识 d1u1k") && !planning.Summary.Contains("尚无候选"), "knowledge-mapping-visible");
                planning.AckFrozen = true; await planning.ConfirmAsync();
                Check(planning.ConfirmedSubject is not null, "mapped-plan-confirmed");
            }
            var draft = (await plans.ListAsync()).Single(d => d.Confirmed);
            Check(draft.Plan!.Collections!.Single().KnowledgeUnits!.Count == 1, "knowledge-units-persisted");
            if (presentation)
            {
                Check(draft.Plan.Collections!.Single().SourceKind == "presentation", "presentation-kind-persisted");
                Check(BookKnowledge.IsPresentation(draft.Plan.Collections!.Single()), "presentation-source-selection-active");
            }
            var preview = await generation.PrepareAsync(draft.Id, "n1");
            Check(preview.Ok && preview.Value!.Preview.Contains("页尾机制3"), "lesson-complete-context-preview");
            Check(preview.Value!.Reviews["n1"].Sources[0].Id == "theory-v3-context", "context-check-protocol");
            if (!restart)
            {
                provider.Gap = true;
                Check((await generation.GenerateAsync(draft.Id, "n1")).Ok, "generation-enqueued");
                await generation.TickAsync();
                Check((await learning.GetLessonAsync(draft.Id, "n1")).Ok, "advisory-gap-does-not-block-publication");
                var task = (await learning.TasksAsync()).Single();
                Check(task.Status == GenerationStatus.Ready, "supplement-task-ready");
                Check(await learning.ContextGapAsync(task.Id) is null, "no-false-context-error");
                Check(task.Attempts == 1, "supplement-published-without-retry");
            }
            Check((await learning.GetLessonAsync(draft.Id, "n1")).Ok, "complete-lesson-readable");
            var first = draft.Plan.Collections![0].KnowledgeUnits![0];
            Check((await new BookKnowledgeCache(root).ReadKnowledgeAsync(first.Fingerprint, default)) is not null, "cache-reopens");
            if (restart) Check(provider.InventoryCalls == 0, "restart-zero-inventory-dispatch");
            var reader = new LearningWindow(generation, draft.Id, "n1", root); reader.Show(); await reader.ReaderInitialized.Task;
            Check(reader.Model.HasCurrentLesson, "webview-lesson-loaded");
            var rendered = false;
            for (var attempt = 0; attempt < 40 && !rendered; attempt++)
            {
                rendered = await reader.Browser.ExecuteScriptAsync("document.body.innerText.includes('0111') && document.querySelectorAll('form').length===1") == "true";
                if (!rendered) await Task.Delay(100);
            }
            Check(rendered, "actual-webview-body-and-quiz-rendered");
            using (var browserImage = File.Create(Path.Combine(evidence, restart ? "reader-restart.png" : "reader.png")))
                await reader.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, browserImage);
            reader.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)reader.ActualWidth, (int)reader.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(reader);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(evidence, restart ? "restart.png" : "ui.png"))) encoder.Save(output);
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, realRemoteCalls = 0 }, Wire.Options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure-" + DateTime.UtcNow.ToString("HHmmss") + ".json"), JsonSerializer.Serialize(new { checks, error = ex.ToString() }, Wire.Options));
            System.Windows.Application.Current.Shutdown(1);
        }
    }
}
