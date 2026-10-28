using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Desktop;

internal static class P28WheelCheck
{
    public static async Task RunAsync(MainWindow main, GenerationService generation, string root, string evidence)
    {
        var results = new List<object>();
        var checks = new List<string>();
        void Check(bool valid, string name) { if (!valid) throw new InvalidOperationException(name); checks.Add(name); }
        Check(SystemParameters.ClientAreaAnimation && SystemParameters.WheelScrollLines > 0, "wheel-animation-enabled");
        void Wheel(UIElement source, int delta) => source.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent });
        async Task Measure(string name, ScrollViewer viewer)
        {
            Check(viewer.ScrollableHeight > 150, name + "-scrollable");
            viewer.ScrollToTop(); await Task.Delay(180);
            var source = DescendantButton((DependencyObject)viewer.Content) ?? (UIElement)viewer.Content;
            var frames = new List<(double Time, double Offset)>();
            var watch = Stopwatch.StartNew();
            void Frame(object? sender, EventArgs args) => frames.Add((watch.Elapsed.TotalMilliseconds, viewer.VerticalOffset));
            CompositionTarget.Rendering += Frame;
            try
            {
                for (var i = 0; i < 8; i++) { Wheel(source, -120); await Task.Delay(55); }
                await Task.Delay(500);
            }
            finally { CompositionTarget.Rendering -= Frame; }
            var expected = Math.Min(viewer.ScrollableHeight, 8 * SystemParameters.WheelScrollLines * 16d);
            var changes = frames.Zip(frames.Skip(1), (a, b) => b.Offset - a.Offset).ToArray();
            Check(Math.Abs(viewer.VerticalOffset - expected) < 1, name + "-all-wheel-distance-delivered");
            Check(changes.All(d => d >= -.1), name + "-no-backwards-jump");
            Check(changes.Count(d => d > .1) >= 12, name + "-multiple-visible-frames");
            results.Add(new { name, expected, actual = viewer.VerticalOffset,
                movingFrames = changes.Count(d => d > .1), maxStep = changes.Max(),
                frames = frames.Select(f => new { milliseconds = f.Time, offset = f.Offset }) });
            Wheel(source, -120); await Task.Delay(35);
            viewer.ScrollToVerticalOffset(100); await Task.Delay(350);
            Check(Math.Abs(viewer.VerticalOffset - 100) < 1, name + "-external-scroll-interrupts-in-flight");
            Wheel(source, -120); await Task.Delay(35); Wheel(source, 120); await Task.Delay(400);
            Check(viewer.VerticalOffset < 148, name + "-reverse-wheel-takes-effect");
            viewer.ScrollToTop(); await Task.Delay(100);
            for (var i = 0; i < 8; i++) { Wheel(source, -15); await Task.Delay(8); }
            await Task.Delay(500);
            Check(Math.Abs(viewer.VerticalOffset - SystemParameters.WheelScrollLines * 16d) < 1, name + "-high-resolution-wheel-preserves-distance");
            Wheel(source, -120); await Task.Delay(35);
            viewer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = Mouse.PreviewMouseDownEvent });
            await Task.Delay(40); var stopped = viewer.VerticalOffset;
            await Task.Delay(200);
            Check(Math.Abs(viewer.VerticalOffset - stopped) < 1, name + "-mouse-down-stops-tail");
        }
        LearningWindow? lesson = null;
        try
        {
            main.NavCoursesButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await main.RefreshCoursesAsync(); await Task.Delay(300); main.UpdateLayout();
            await Measure("main", main.MainScroll);
            var course = main.CourseModel.Drafts.First(d => d.Draft.Plan?.Candidate.Nodes.Count == 80);
            var card = main.CourseList.Children.OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == "Subject-" + course.Draft.Id);
            card.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(300); main.UpdateLayout();
            var outerOffset = main.MainScroll.VerticalOffset;
            await Measure("route", main.RouteViewport);
            Check(Math.Abs(main.MainScroll.VerticalOffset - outerOffset) < 1, "route-wheel-does-not-scroll-outer-page");
            lesson = new LearningWindow(generation, course.Draft.Id, "n0", root);
            lesson.Show(); await lesson.ReaderInitialized.Task.WaitAsync(TimeSpan.FromSeconds(30)); lesson.UpdateLayout();
            var outline = FindScroll(lesson.OutlinePanel);
            await Measure("outline", outline);
            await File.WriteAllTextAsync(Path.Combine(evidence, "wheel.json"), JsonSerializer.Serialize(new { checks, results, failed = 0 }, Wire.Options));
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "wheel-failure.json"), JsonSerializer.Serialize(new { checks, results, error = ex.ToString() }, Wire.Options));
            throw;
        }
        finally { lesson?.Close(); }
    }
    private static ScrollViewer FindScroll(DependencyObject child)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            if (parent is ScrollViewer viewer) return viewer;
        throw new InvalidOperationException("Outline scroll viewport missing");
    }
    private static UIElement? DescendantButton(DependencyObject parent)
    {
        if (parent is Button button) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (DescendantButton(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
}
