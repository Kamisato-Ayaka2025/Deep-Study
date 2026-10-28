using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P17DesktopCheck
{
    public static async Task RunAsync(string root, string evidence)
    {
        var checks = new List<string>();
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        var app = (App)System.Windows.Application.Current;
        try
        {
            if (!File.Exists(Path.Combine(root, "p17-synthetic.marker"))) throw new IOException("Fresh synthetic root required");
            var seed = await LabFixtureProvider.SeedAsync(root);
            var main = (MainWindow)app.MainWindow;
            await main.CourseModel.LoadAsync();
            main.CourseModel.Selected = main.CourseModel.Drafts.Single(d => d.Draft.Id == seed.Id);
            Check(app.ShutdownMode == ShutdownMode.OnExplicitShutdown && app.Tray.IsVisible, "explicit-lifetime-and-tray-visible");
            var open = typeof(MainWindow).GetMethod("OpenLesson", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Window? lastLesson = null;
            foreach (var type in new[] { "theory", "lab" })
            {
                var node = main.CourseModel.Nodes.First(n => n.Type == type);
                open.Invoke(main, [node]);
                var lesson = app.Windows.Cast<Window>().Single(w => w is LearningWindow or LabPreviewWindow);
                lastLesson = lesson;
                if (lesson is LearningWindow reader) await reader.ReaderInitialized.Task.WaitAsync(TimeSpan.FromSeconds(30));
                await Task.Delay(300);
                Check(lesson.Owner is null && lesson.ShowInTaskbar && !lesson.Topmost, type + "-independent-taskbar-window");
                var state = lesson.WindowState;
                main.WindowState = WindowState.Minimized;
                await Task.Delay(250);
                Check(lesson.IsVisible && lesson.WindowState == state, type + "-main-minimize-preserves-lesson");
                app.Tray.InvokeMenu(0);
                await Task.Delay(250);
                Check(main.IsVisible && main.WindowState != WindowState.Minimized && main.IsActive, type + "-main-can-activate-above-lesson");
                main.Close();
                await Task.Delay(200);
                Check(!main.IsVisible && lesson.IsVisible && lesson.WindowState == state, type + "-main-close-only-hides");
                Check(app.Windows.Cast<Window>().Contains(main) && app.Tray.IsVisible, type + "-hidden-main-still-alive");
                app.Tray.InvokeMenu(0);
                Check(main.IsVisible && ReferenceEquals(main, app.MainWindow), type + "-menu-restores-same-main");
                lesson.WindowState = WindowState.Minimized;
                Check(main.WindowState != WindowState.Minimized, type + "-lesson-minimize-does-not-minimize-main");
                lesson.WindowState = state;
                if (type == "theory") { lesson.Close(); Check(main.IsVisible, "lesson-close-keeps-main"); }
            }
            for (var i = 0; i < 3; i++) { main.Close(); app.Tray.InvokeMenu(0); }
            Check(app.Windows.Cast<Window>().Count(w => w is MainWindow) == 1, "repeated-restore-no-duplicate-main");
            var shell = (ShellViewModel)main.DataContext;
            shell.SetPendingApiKey(true);
            main.Close();
            Check(!main.IsVisible, "pending-settings-can-hide-with-input-retained");
            app.Tray.InvokeMenu(2);
            Check(main.IsVisible && shell.CredentialStatus.Contains("尚未保存"), "exit-preserves-unsaved-credential-guard");
            shell.DiscardPendingApiKey();
            main.WindowState = WindowState.Maximized;
            main.WindowState = WindowState.Minimized;
            main.Close();
            app.Tray.InvokeMenu(0);
            Check(main.WindowState == WindowState.Maximized, "restore-preserves-maximized-state");
            main.Close();
            lastLesson!.Close();
            Check(app.Windows.Cast<Window>().All(w => !w.IsVisible) && app.Tray.IsVisible, "last-visible-window-close-keeps-tray-running");
            app.Tray.InvokeMenu(0);
            Check(main.IsVisible, "tray-recovers-with-no-visible-windows");
            open.Invoke(main, [main.CourseModel.Nodes.First(n => n.Type == "lab")]);
            lastLesson = app.Windows.Cast<Window>().Single(w => w is LabPreviewWindow);
            main.Close();
            var closed = new List<string>();
            main.Closed += (_, _) => closed.Add("main");
            lastLesson!.Closed += (_, _) => closed.Add("lesson");
            app.Exit += (_, _) => File.WriteAllText(Path.Combine(evidence, "exit.json"), JsonSerializer.Serialize(new { closed, trayVisible = app.Tray.IsVisible }, Wire.Options));
            File.WriteAllText(Path.Combine(evidence, "ui.json"), JsonSerializer.Serialize(new { checks, failed = 0, manualPending = true }, Wire.Options));
            app.Tray.InvokeMenu(2);
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(evidence, "failure.json"), JsonSerializer.Serialize(new { checks, error = ex.ToString() }, Wire.Options));
            app.Shutdown(1);
        }
    }
}
