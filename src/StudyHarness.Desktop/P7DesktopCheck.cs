using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P7DesktopCheck
{
    public static async Task RunAsync(string root, string evidence)
    {
        Directory.CreateDirectory(evidence); var checks = new List<string>(); LabPreviewWindow? window = null;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            var draft = await CppFixture.SeedAsync(root); var plans = new SqlitePlanningStore(root); var store = new SqliteLearningStore(root, plans);
            var settings = new SettingsService(new SqliteSettingsStore(root)); var knowledge = new KnowledgeStore(root);
            var service = new LabService(plans, store, store, knowledge, settings, new(new CppFixture()), new LabZipExporter());
            var planning = new PlanningViewModel(new(plans, knowledge, settings, new(new CppFixture()), new PlanParser()), store);
            await planning.LoadAsync();
            Check(planning.Scopes.Contains("CppAlgorithms") && planning.Summary.Contains("a2 · 前缀和 → 理论：n1 → 实验题：n3"), "plan-shows-every-algorithm-mapping");
            if (System.Windows.Application.Current.MainWindow is MainWindow main)
            {
                main.ChatPanel.DataContext = planning;
                await main.Dispatcher.InvokeAsync(main.UpdateLayout);
                main.MainScroll.ScrollToEnd(); await main.Dispatcher.InvokeAsync(main.UpdateLayout);
                Capture(main, Path.Combine(evidence, "cpp-plan.png"));
            }
            var restart = await store.GetLabAsync(draft.Id, "n2") is not null;
            var model = new LabViewModel(service, draft.Id, "n2"); window = new(model); window.Show(); await model.LoadAsync();
            if (!restart)
            {
                Check(model.CanGenerate && !model.CanConfirm, "cpp-can-generate-before-theory");
                await model.LoadAsync();
                Check(model.CanGenerate && (await store.CompletedNodesAsync(draft.Id)).Count == 0, "cpp-generation-does-not-need-theory-completion");
                await model.GenerateAsync();
                Check(model.HasMaterial && model.Files.Count == 3 && model.Files.Any(f => f.Path == "solution.cpp") && model.Files.Any(f => f.Path == "test.cpp"), "cpp-source-and-harness-preview");
                Check(model.Instructions.Contains("仅编译 test.cpp") && !model.CanConfirm, "explicit-build-combination-no-auto-completion");
                await model.ExportAsync(Path.GetFullPath(Path.Combine(evidence, "cpp-desktop.zip")));
                Check(File.Exists(Path.Combine(evidence, "cpp-desktop.zip")) && !(await store.CompletedNodesAsync(draft.Id)).Contains("n2"), "cpp-export-does-not-complete");
                window.SelfTestAck.IsChecked = true;
                await window.Dispatcher.InvokeAsync(window.UpdateLayout);
                Check(window.ConfirmLab.IsEnabled, "cpp-explicit-attestation-enables-confirm");
                await model.ConfirmAsync();
                Check((await store.GetLabAttestationAsync(draft.Id, "n2"))?.CompletionKind == "userAttested", "cpp-completion-provenance");
            }
            else Check(model.HasMaterial && !model.CanGenerate && model.Status.Contains("用户确认完成"), "cpp-restart-keeps-material-and-attestation");
            Check(window.BoundaryNotice.Text.Contains("未经编译"), "cpp-honest-validation-boundary");
            await window.Dispatcher.InvokeAsync(window.UpdateLayout); Capture(window, Path.Combine(evidence, restart ? "cpp-restart.png" : "cpp-materials.png"));
            await File.WriteAllTextAsync(Path.Combine(evidence, restart ? "restart.json" : "ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, codeExecuted = false, manualPending = true }, Wire.Options));
            window.Close(); System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { checks, failed = 1, error = e.ToString() }, Wire.Options));
            window?.Close(); System.Windows.Application.Current.Shutdown(1);
        }
    }
    private static void Capture(Window window, string path)
    {
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
