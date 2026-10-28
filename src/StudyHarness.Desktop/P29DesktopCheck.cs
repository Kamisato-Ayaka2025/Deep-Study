using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P29DesktopCheck
{
    internal static async Task RunAsync(string root, string evidence, DataDirectorySettings locations)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            Check(File.Exists(Path.Combine(root, "p29-synthetic.marker")), "synthetic-only");
            var window = (MainWindow)System.Windows.Application.Current.MainWindow;
            var shell = (ShellViewModel)window.DataContext;
            window.NavSettingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            var move = Descendants(window).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "MoveDataDirectory");
            move.BringIntoView(); window.UpdateLayout();
            Check(move.IsVisible && move.IsEnabled, "settings-move-button-visible-enabled");
            Check(shell.DataLocation.Contains(root), "current-directory-shown");
            var target = Path.Combine(evidence, "moved-data");
            var restart = File.Exists(Path.Combine(evidence, "ui.json"));
            var plans = new SqlitePlanningStore(root); var learning = new SqliteLearningStore(root, plans);
            if (!restart)
            {
                var seed = await LabFixtureProvider.SeedAsync(root);
                var job = (await learning.BeginLabAsync(seed.Id, "L1")).Value!;
                var lab = (await learning.PublishLabAsync(job, LabFixtureProvider.Material, seed.Plan!.Hash, seed.Plan.Sources)).Value!;
                Check((await learning.ConfirmLabAsync(seed.Id, new(Guid.NewGuid(), 0, new("L1", lab.Hash, true)))).Ok, "synthetic-progress-saved");
                var service = new SettingsService(new SqliteSettingsStore(root)); var settings = await service.GetAsync();
                Check((await service.SaveAsync(new(Guid.NewGuid(), settings.Revision, new("deepseek-chat", GenerationMode.Click, ConsentMode.Each, "fake-p29-key")))).Ok, "synthetic-key-saved");
                File.WriteAllText(Path.Combine(evidence, "plan.json"), JsonSerializer.Serialize(seed, Wire.Options));
                Check(await shell.ChangeDataDirectoryAsync(target, true), "settings-schedules-copy");
                Check(locations.Read()!.CopyPending && !File.Exists(Path.Combine(target, "app.db")), "no-live-copy");
                Check(shell.DataLocation.Contains(root) && shell.DataDirectoryStatus.Contains(target), "pending-and-current-paths-distinct");
            }
            else
            {
                Check(root == target && !locations.Read()!.CopyPending, "startup-applies-directory-preference");
                var expected = JsonSerializer.Deserialize<PlanningDraft>(File.ReadAllText(Path.Combine(evidence, "plan.json")), Wire.Options)!;
                var actual = (await plans.GetAsync(expected.Id))!;
                Check(actual.Plan!.Hash == expected.Plan!.Hash, "frozen-course-preserved");
                Check((await learning.CompletedNodesAsync(expected.Id)).Contains("L1"), "learning-progress-preserved");
                Check(await new SqliteSettingsStore(root).ReadKeyAsync(default) == "fake-p29-key", "credential-decrypts-after-move");
                var knowledge = new KnowledgeStore(root);
                Check((await knowledge.CurrentAsync())!.Root.StartsWith(target, StringComparison.OrdinalIgnoreCase), "internal-library-rebased");
                Check((await knowledge.ResolveAsync(actual.Plan.Sources[0].Reference)).Text is not null, "frozen-reference-readable");
                Check(window.CourseList.Children.Count > 0, "course-cards-restored");
            }
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window); var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using (var output = File.Create(Path.Combine(evidence, restart ? "restart.png" : "ui.png"))) encoder.Save(output);
            File.WriteAllText(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, realRemoteCalls = 0 }, Wire.Options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { checks, error = ex.ToString() }, Wire.Options));
            System.Windows.Application.Current.Shutdown(1);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var item in Descendants(child)) yield return item; }
    }
}
