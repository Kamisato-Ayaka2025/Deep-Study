using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P5DesktopCheck
{
    public static async Task RunAsync(string root, string evidence)
    {
        Directory.CreateDirectory(evidence); var checks = new List<string>(); LabPreviewWindow? window = null;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            var draft = await LabFixtureProvider.SeedAsync(root); var plans = new SqlitePlanningStore(root); var store = new SqliteLearningStore(root, plans);
            var provider = new LabFixtureProvider();
            var service = new LabService(plans, store, store, new KnowledgeStore(root), new SettingsService(new SqliteSettingsStore(root)), new(provider), new LabZipExporter());
            var restart = await store.GetLabAsync(draft.Id, "L1") is not null;
            var model = new LabViewModel(service, draft.Id, "L1"); window = new(model); window.Show(); await model.LoadAsync();
            if (!restart)
            {
                var locked = new LabViewModel(service, draft.Id, "L2"); await locked.LoadAsync();
                Check(locked.CanGenerate && !locked.CanConfirm, "dependent-lab-can-generate-but-no-material-no-confirm");
                await model.GenerateAsync();
                Check(model.HasMaterial && provider.Calls == 1 && model.Files.Count == 4 && model.Instructions.Contains("独立题目") && model.Files.Any(f => f.Path == "test.c"), "generate-preview-source-test-files");
                Check(!model.CanConfirm && (await store.CompletedNodesAsync(draft.Id)).Count == 0, "generation-not-completion");
                await model.ExportAsync(Path.GetFullPath(Path.Combine(evidence, "desktop-materials.zip")));
                Check(File.Exists(Path.Combine(evidence, "desktop-materials.zip")) && (await store.CompletedNodesAsync(draft.Id)).Count == 0, "export-not-completion");
                window.SelfTestAck.IsChecked = true;
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Check(window.ConfirmLab.IsEnabled, "explicit-attestation-enables-confirm");
                window.ConfirmLab.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                for (var i = 0; i < 100 && (await store.GetLabAttestationAsync(draft.Id, "L1")) is null; i++) await Task.Delay(30);
                await model.LoadAsync(); await locked.LoadAsync();
                Check(model.Status.Contains("用户确认完成") && locked.CanGenerate, "self-attestation-persists-without-gating-other-lessons");
            }
            else Check(model.Status.Contains("用户确认完成") && provider.Calls == 0 && model.HasMaterial, "restart-preserves-material-and-attestation-no-request");
            await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Check(window.FindName("JudgeLab") is null && window.FindName("ValidateReference") is null && window.BoundaryNotice.Text.Contains("未经编译"), "no-local-evaluation-controls-and-honest-notice");
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(Path.Combine(evidence, restart ? "restart.png" : "materials.png"))) png.Save(stream);
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, codeExecuted = false, manualPending = true }, Wire.Options));
            window.Close(); System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { checks, failed = 1, error = e.ToString() }, Wire.Options));
            window?.Close(); System.Windows.Application.Current.Shutdown(1);
        }
    }
}
