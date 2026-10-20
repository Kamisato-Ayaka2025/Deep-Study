using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StudyHarness.Contracts;

namespace StudyHarness.Desktop;

internal static class P5PreviewCheck
{
    public static async Task RunAsync(string evidence)
    {
        Directory.CreateDirectory(evidence);
        var checks = new List<string>();
        LabPreviewWindow? window = null;
        try
        {
            foreach (var eligible in new[] { false, true })
            {
                var node = new PlannedNode("L2", "累计器：扩展当前代码", "lab", ["填写 TODO 并理解标准输入输出"], ["L1"], 30,
                    new("continuous", "L1", "msvc", ["s1"], "在上一课当前代码上继续；这是合成 UI 检查。"));
                window = new(LabPreviewModel.From(node, eligible)); window.Show();
                await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                foreach (var name in new[] { "GenerateLab", "ValidateReference", "StartLab", "JudgeLab", "RestoreLab" })
                    if (((Button)window.FindName(name)).IsEnabled) throw new InvalidOperationException("Execution control unexpectedly enabled: " + name);
                if (!window.BlockedReason.Text.Contains("PSEC") || !window.BlockedReason.Text.Contains(eligible ? "已满足" : "尚未完成"))
                    throw new InvalidOperationException("Missing reason or prerequisite state");
                checks.Add(eligible ? "unlocked-lab-still-refuses-execution" : "locked-lab-refuses-execution");
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(evidence, eligible ? "unlocked.png" : "locked.png"))) encoder.Save(stream);
                window.Close(); window = null;
            }
            await File.WriteAllTextAsync(Path.Combine(evidence, "preview-ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, realCodeEnabled = false, manualPending = true }, Wire.Options));
            System.Windows.Application.Current.Shutdown(0);
        }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "preview-ui.json"), JsonSerializer.Serialize(new { checks, failed = 1, error = e.GetType().Name, realCodeEnabled = false }, Wire.Options));
            window?.Close(); System.Windows.Application.Current.Shutdown(1);
        }
    }
}
