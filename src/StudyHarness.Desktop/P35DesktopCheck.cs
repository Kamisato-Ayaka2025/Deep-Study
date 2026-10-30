using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P35DesktopCheck
{
    private sealed class Caps : IModelCapabilitySource
    { public Task<ModelCapabilities> GetAsync(string m, CancellationToken ct = default) => Task.FromResult(new ModelCapabilities(m, 1048576, 32768, true, DateTimeOffset.UtcNow, true)); }
    private sealed class Web : IWebSources
    {
        public int Fetches;
        public Task<IReadOnlyList<WebHit>> SearchAsync(string query, CancellationToken ct) => Task.FromResult<IReadOnlyList<WebHit>>([new("集合教程", "https://example.org/tutorial")]);
        public Task<WebPage> FetchAsync(string url, CancellationToken ct)
        { Fetches++; return Task.FromResult(new WebPage(url, "集合教程", "# 集合定义\n集合是确定对象的全体。重复列举元素不会改变集合。\n广告：购买推广套餐\n", DateTimeOffset.UnixEpoch)); }
    }
    private sealed class Provider : IModelProvider
    {
        public int Inventories, Plans, Lessons;
        public string Destination => "local:p35-fixture";
        public Task<Result<ProviderReply>> SendAsync(ApprovedModelRequest r, CancellationToken ct)
        {
            string text;
            if (string.Join('\n', r.Sources.Select(s => s.Text)).Contains("购买推广套餐")) throw new InvalidOperationException("Advertisement leaked");
            if (r.Sources[0].Id == "web-knowledge")
            {
                Inventories++;
                text = JsonSerializer.Serialize(new { points = new[] { new { title = "集合定义与元素互异性", detail = "集合是确定对象的全体；重复列举不会改变集合。", evidenceIds = r.Sources.Skip(1).Select(s => s.Id).ToArray() } }, nonTeachingIds = Array.Empty<string>() });
            }
            else if (r.Sources[0].Id == "knowledge-mapping")
            {
                var points = JsonSerializer.Deserialize<BookKnowledgePoint[]>(r.Sources.Single(s => s.Id == "points").Text, Wire.Options)!;
                text = JsonSerializer.Serialize(new { knowledgeCoverage = points.Select(p => new KnowledgeCoverage(p.Key, "included", ["n2"], null, [])) }, Wire.Options);
            }
            else if (r.Sources[0].Id == "planning-v2")
            {
                Plans++;
                if (!r.Sources[0].Text.Contains("集合定义与元素互异性")) throw new InvalidOperationException("Missing web inventory in planning");
                text = JsonSerializer.Serialize(new PlanCandidate("集合入门", "零基础", "理解集合", 120, "Adaptive", [], [], [], ["s1"],
                    [new("n1", "集合导论", "theory", ["理解集合学习目标"], [], 15, null), new("n2", "集合定义与元素", "theory", ["理解集合定义与元素互异性"], [], 30, null), new("n3", "综合复习", "theory", ["复习集合与元素"], [], 15, null)], 2,
                    Strategy: new("数学", ["theory"], "依据网页正文学习集合", [], [], []),
                    Coverage: System.Text.RegularExpressions.Regex.Matches(r.Sources[0].Text, @"(?m)^(d\d+/[^\t\r\n]+)\t")
                        .Select(m => new PlanCoverage(m.Groups[1].Value, "included", ["n1", "n2", "n3"], null)).ToArray()), Wire.Options);
            }
            else
            {
                Lessons++;
                if (!r.Sources[0].Text.Contains("网页教学知识")) throw new InvalidOperationException("Missing inventory in lesson");
                text = JsonSerializer.Serialize(TheoryFixtureProvider.NewLesson(r.Sources[1].Id), Wire.Options);
            }
            return Task.FromResult(Result<ProviderReply>.Success(new(text, false, Usage: new(10, 10, 20))));
        }
    }
    internal static async Task RunAsync(string root, string evidence)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            Check(File.Exists(Path.Combine(root, "p35-synthetic.marker")), "isolated-root");
            var restart = File.Exists(Path.Combine(evidence, "ui.json"));
            var web = new Web(); var knowledge = new KnowledgeStore(root, web);
            var settings = new SettingsService(new SqliteSettingsStore(root)); var provider = new Provider(); var caps = new Caps();
            var plans = new SqlitePlanningStore(root); var learning = new SqliteLearningStore(root, plans); var cache = new BookKnowledgeCache(root);
            var service = new PlanningService(plans, knowledge, settings, new(provider, caps), new PlanParser(), explicitSourcesOnly: true, knowledgeCache: cache);
            var planning = new PlanningViewModel(service, learning, web: new WebLearningService(web, knowledge, knowledge, Path.Combine(root, "knowledge")), sessionStore: new SqlitePlanningSessionStore(root));
            var workspace = new WorkspaceViewModel(new(knowledge, settings, new(provider)));
            var shell = new ShellViewModel(settings, new(provider), new SafeLog(Path.Combine(root, "p35.log")), root, cacheCatalog: new CacheCatalog(root));
            var old = System.Windows.Application.Current.MainWindow;
            var window = new MainWindow(shell, workspace, planning); window.Show(); System.Windows.Application.Current.MainWindow = window; old.Close(); await window.Ready.Task;
            if (!restart)
            {
                planning.Request = "学习集合"; planning.Background = "零基础"; planning.Goal = "理解集合"; planning.Minutes = "120";
                planning.WebTopic = "集合"; planning.WebSite = "example.org/tutorial";
                await planning.AcquireWebAsync();
                Check(web.Fetches == 1 && planning.WebReport.Contains("已保存1页"), "web-acquisition-saved");
                await shell.LoadCacheAsync(); Check(shell.CachedMaterials.Count == 1 && !shell.CachedMaterials[0].KnowledgeReady, "imported-before-inventory");
                var previewPublications = 0;
                planning.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(planning.Preview) && planning.Preview.StartsWith("发送至：", StringComparison.Ordinal)) previewPublications++; };
                await planning.FormSubmissionAsync();
                Check(previewPublications == 1, "complete-preview-published-once");
                Check(planning.CanSendPlan && planning.Preview.Contains("web-knowledge-v1"), "web-inventory-preview");
                Check(provider.Inventories == 0 && provider.Plans == 0, "preview-no-model-dispatch");
                await planning.ProposeAsync(true);
                Check(provider.Inventories == 1 && planning.Summary.Contains("集合"), "inventory-before-course-plan");
                planning.AckFrozen = true; await planning.ConfirmAsync(); Check(planning.ConfirmedSubject is not null, "web-plan-confirmed");
                var library = (await knowledge.CurrentAsync())!;
                for (var i = 0; i < 10; i++)
                    await knowledge.SaveWebAsync(library.Id, [new($"https://example.org/extra/{i}", $"补充资料{i:00}", "集合定义与例子。", DateTimeOffset.UnixEpoch)], "集合", default);
            }
            var draft = (await plans.ListAsync()).Single(d => d.Confirmed);
            var frozenHash = draft.Plan!.Hash;
            Check(draft.Plan.Candidate.Nodes.Count == 3 && !draft.Plan.Candidate.KnowledgeCoverage!.Any(m => m.NodeKeys.Contains("n1") || m.NodeKeys.Contains("n3")), "unmapped-intro-and-review-preserved");
            var generation = new GenerationService(plans, learning, learning, knowledge, settings, new(provider, caps), new TheoryLessonParser(), knowledgeCache: cache);
            var preview = await generation.PrepareAsync(draft.Id, "n1");
            Check(preview.Ok && preview.Value!.Preview.Contains("网页知识整理"), "lesson-discloses-inventory-scope");
            var before = provider.Inventories;
            Check((await generation.GenerateAsync(draft.Id, "n1", restart)).Ok, "lesson-enqueued"); await generation.TickAsync();
            Check((await learning.GetLessonAsync(draft.Id, "n1")).Ok, "lesson-published");
            Check(provider.Inventories == before && provider.Lessons == 1, "lesson-reuses-planning-inventory");
            Check((await generation.GenerateAsync(draft.Id, "n3", restart)).Ok, "unmapped-review-enqueued"); await generation.TickAsync();
            Check((await learning.GetLessonAsync(draft.Id, "n3")).Ok && provider.Inventories == before, "unmapped-review-published-with-cache-reuse");
            Check((await plans.GetAsync(draft.Id))!.Plan!.Hash == frozenHash, "frozen-plan-unchanged");
            if (restart) Check(web.Fetches == 0 && provider.Inventories == 0, "restart-zero-fetch-zero-inventory");
            var reader = new LearningWindow(generation, draft.Id, "n1", root); reader.Show(); await reader.ReaderInitialized.Task.WaitAsync(TimeSpan.FromSeconds(30));
            for (var i = 0; i < 100 && await reader.Browser.ExecuteScriptAsync("document.body.innerText.includes('集合与元素')") != "true"; i++) await Task.Delay(100);
            Check(await reader.Browser.ExecuteScriptAsync("document.body.innerText.includes('集合与元素') && !document.body.innerText.includes('购买推广套餐')") == "true", "actual-webview-lesson"); reader.Close();
            window.NavSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.CacheExpander.IsExpanded = true;
            while (!shell.CanRefreshCache) await Task.Delay(20);
            Check(shell.CachedMaterials.Count == 8 && shell.CanNextCache, "web-catalog-pagination");
            Check(shell.CachedMaterials.All(x => x.Name.Contains("example.org")), "web-title-and-host");
            await shell.LoadCacheAsync(1); Check(shell.CachedMaterials.Count == 3 && !shell.CanNextCache, "web-catalog-last-page");
            Check(shell.CachedMaterials.Any(x => x.KnowledgeReady), "web-complete-status");
            window.CacheExpander.BringIntoView(); window.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(evidence, restart ? "restart.png" : "ui.png"))) encoder.Save(stream);
            File.WriteAllText(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { failed = 0, checks }, Wire.Options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { failed = 1, checks, error = e.ToString() }, Wire.Options));
            System.Windows.Application.Current.Shutdown(1);
        }
    }
}
