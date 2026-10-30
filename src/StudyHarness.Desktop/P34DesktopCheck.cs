using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P34DesktopCheck
{
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    internal static async Task RunAsync(string root, string evidence)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            Check(File.Exists(Path.Combine(root, "p34-synthetic.marker")), "isolated-synthetic-root");
            var restart = File.Exists(Path.Combine(evidence, "ui.json"));
            var window = (MainWindow)System.Windows.Application.Current.MainWindow;
            var shell = (ShellViewModel)window.DataContext;
            window.NavSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Check(!window.CacheExpander.IsExpanded, "collapsed-by-default");
            Check(window.CacheExpander.TranslatePoint(new Point(), window).Y >
                Descendants(window).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "MoveDataDirectory").TranslatePoint(new Point(), window).Y,
                "below-data-location");
            var store = new KnowledgeStore(root); var library = (await store.CurrentAsync())!;
            if (!restart)
            {
                await shell.LoadCacheAsync(); Check(shell.CachedMaterials.Count == 0 && shell.CacheStatus.Contains("尚无"), "empty-state");
                for (var i = 0; i < 18; i++)
                {
                    var path = Path.Combine(root, $"资料{i:00}—课程教材与知识点整理示例.md");
                    File.WriteAllText(path, "# 第一节\n定义与原理\n# 第二节\n例子与推导");
                    var imported = await store.ImportCollectionAsync(HostSelection.FromFilePicker(path), library.Id);
                    Check(imported.Ok, "import-" + i);
                    if (i == 0)
                    {
                        var sources = imported.Value!.References.Select((r, n) => new PlanSource("b" + n, r, false)).ToArray();
                        await new BookKnowledgeCache(root).SaveKnowledgeAsync(new("complete", "complete", sources, [], sources.Select(s => s.Key).ToArray()), default);
                    }
                }
            }
            window.CacheExpander.IsExpanded = true;
            while (!shell.CanRefreshCache) await Task.Delay(20);
            window.UpdateLayout();
            Check(shell.CachedMaterials.Count == 8 && window.CacheItems.Items.Count == 8, "eight-rendered-items");
            Check(shell.CachedMaterials[0].KnowledgeReady && !shell.CachedMaterials[1].KnowledgeReady, "both-statuses");
            Check(!shell.CanPreviousCache && shell.CanNextCache, "first-page-boundaries");
            async Task Click(string id)
            {
                Descendants(window).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == id).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (!shell.CanRefreshCache) await Task.Delay(20);
                window.UpdateLayout();
            }
            var first = shell.CachedMaterials.Select(x => x.Name).ToArray();
            await Click("CacheNext");
            Check(shell.CachedMaterials.Count == 8 && !shell.CachedMaterials.Any(x => first.Contains(x.Name)), "next-page-distinct");
            await Click("CacheNext");
            Check(shell.CachedMaterials.Count == 2 && !shell.CanNextCache && shell.CanPreviousCache, "last-page-boundaries");
            await Click("CachePrevious"); Check(shell.CachePageLabel.Contains("2 / 3"), "previous-page");
            await Click("CacheRefresh"); Check(shell.CachePageLabel.Contains("1 / 3"), "refresh-resets-first-page");
            window.CacheExpander.IsExpanded = false; window.UpdateLayout();
            Check(!window.CacheItems.IsVisible, "collapse-hides-list");
            window.CacheExpander.IsExpanded = true;
            while (!shell.CanRefreshCache) await Task.Delay(20);
            window.CacheExpander.BringIntoView(); window.UpdateLayout();
            Check(!Descendants(window.CacheItems).OfType<ScrollViewer>().Any(), "no-inner-scroll-list");
            var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            image.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
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
