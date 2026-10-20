using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class WebDesktopCheck
{
    public static async Task RunAsync(string root, string evidence)
    {
        Directory.CreateDirectory(evidence); var checks = new List<string>();
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            var knowledge = new KnowledgeStore(root); var plans = new SqlitePlanningStore(root);
            var settings = new SettingsService(new SqliteSettingsStore(root));
            var fake = new Fixture();
            var service = new WebLearningService(fake, knowledge, knowledge, Path.Combine(root, "knowledge"));
            var planning = new PlanningViewModel(new(plans, knowledge, settings, new(new CppFixture()), new PlanParser()), web: service);
            var main = (MainWindow)System.Windows.Application.Current.MainWindow;
            main.ChatPanel.DataContext = planning;
            planning.Request = "二分查找和前缀和"; planning.Scope = "CppAlgorithms";
            var initialLibrary = await knowledge.CurrentAsync();
            Check(initialLibrary is null || (await knowledge.SearchAsync(initialLibrary.Id,"",[])).Fragments.Count == 0, "fresh-empty-library");
            await planning.AcquireWebAsync();
            Check(planning.Status.Contains("网页正文已保存"), "target-only-web-entry");
            Check(planning.Preview.Contains("学习资料已准备好") && !planning.Preview.Contains("重定向"), "simple-status-without-fetch-internals");
            await planning.SaveAsync([]);
            var draft = (await plans.ListAsync()).Single();
            Check(draft.Input.PreferredReferences.Count == 1, "save-binds-web-snapshot");
            await planning.PrepareAsync(); Check(planning.Preview.Contains("按需读取范围"), "web-source-enters-plan-review");
            await planning.ProposeAsync(false); Check(planning.Status.Contains("已拒绝") && planning.Preview.Length == 0, "plan-consent-remains-independent");
            await planning.LoadAsync(); await planning.PrepareAsync();
            Check(planning.Preview.Contains("binary source is a search explanation."), "reload-reuses-body-without-web");
            fake.Fail = true; await planning.AcquireWebAsync();
            Check(planning.Status.Contains("未取得可用教学正文"), "failure-has-recoverable-message");
            fake.Fail = false; planning.WebSite = "oi-wiki.org"; await planning.AcquireWebAsync();
            Check(fake.Query.Contains("site:oi-wiki.org") && planning.Status.Contains("网页正文已保存"), "specified-site-recovery");
            fake.Wait = true; var pending = planning.AcquireWebAsync(); planning.Cancel(); await pending;
            Check(planning.Status.Contains("已取消") && !planning.Busy, "cancel-restores-controls");
            main.MainScroll.ScrollToTop(); await main.Dispatcher.InvokeAsync(main.UpdateLayout);
            var image = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(main);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(Path.Combine(evidence, "web-entry.png"))) encoder.Save(stream);
            main.MainScroll.ScrollToVerticalOffset(450); await main.Dispatcher.InvokeAsync(main.UpdateLayout);
            var detail = new RenderTargetBitmap((int)main.ActualWidth, (int)main.ActualHeight, 96, 96, PixelFormats.Pbgra32); detail.Render(main);
            var detailEncoder = new PngBitmapEncoder(); detailEncoder.Frames.Add(BitmapFrame.Create(detail));
            using (var stream = File.Create(Path.Combine(evidence, "web-controls.png"))) detailEncoder.Save(stream);
            await File.WriteAllTextAsync(Path.Combine(evidence, "web-ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, realNetwork = false, manualPending = true }, Wire.Options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "web-ui-failure.json"), JsonSerializer.Serialize(new { checks, error = e.ToString() }, Wire.Options));
            System.Windows.Application.Current.Shutdown(1);
        }
    }
    private sealed class Fixture : IWebSources
    {
        public bool Fail, Wait; public string Query = "";
        public async Task<IReadOnlyList<WebHit>> SearchAsync(string query, CancellationToken ct)
        {
            Query = query; if (Wait) await Task.Delay(Timeout.Infinite, ct);
            return [new("二分查找", "https://oi-wiki.org/basic/binary/")];
        }
        public Task<WebPage> FetchAsync(string url, CancellationToken ct)
        {
            if (!new Uri(url).AbsolutePath.Contains("binary", StringComparison.Ordinal)) throw new InvalidOperationException("fixture page unavailable");
            if (Fail) throw new InvalidOperationException("fixture failure");
            return Task.FromResult(new WebPage(url, "二分查找", "binary source is a search explanation.", DateTimeOffset.UtcNow));
        }
    }
}
