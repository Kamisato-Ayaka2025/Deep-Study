using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StudyHarness.Desktop;

/// <summary>One frame-paced wheel motion per explicit pixel-scrolling viewport.</summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty ControllerProperty = DependencyProperty.RegisterAttached(
        "Controller", typeof(Controller), typeof(SmoothScroll));
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer viewer) return;
        if (viewer.GetValue(ControllerProperty) is Controller old) old.Detach();
        viewer.SetValue(ControllerProperty, (bool)e.NewValue ? new Controller(viewer) : null);
    }

    private sealed class Controller
    {
        private readonly ScrollViewer viewer;
        private bool active;
        private double position, target, requested;
        private long lastFrame;
        public Controller(ScrollViewer viewer)
        {
            this.viewer = viewer;
            viewer.PreviewMouseWheel += Wheel;
            viewer.PreviewMouseDown += Stop;
            viewer.PreviewKeyDown += Stop;
            viewer.Unloaded += Stop;
            viewer.IsVisibleChanged += VisibilityChanged;
            viewer.ScrollChanged += Scrolled;
        }
        public void Detach()
        {
            Cancel();
            viewer.PreviewMouseWheel -= Wheel;
            viewer.PreviewMouseDown -= Stop;
            viewer.PreviewKeyDown -= Stop;
            viewer.Unloaded -= Stop;
            viewer.IsVisibleChanged -= VisibilityChanged;
            viewer.ScrollChanged -= Scrolled;
        }
        private void Stop(object sender, RoutedEventArgs e) => Cancel();
        private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Cancel(); }
        private void Cancel()
        {
            if (!active) return;
            active = false;
            CompositionTarget.Rendering -= Frame;
        }
        private void Scrolled(object sender, ScrollChangedEventArgs e)
        {
            if (!active || !ReferenceEquals(e.OriginalSource, viewer)) return;
            // Compare with the last command actually sent, not a concurrently advancing animation clock.
            if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0 ||
                (e.VerticalChange != 0 && Math.Abs(viewer.VerticalOffset - requested) > 1)) Cancel();
        }
        private static bool CanMove(ScrollViewer scroll, int delta) =>
            delta < 0 ? scroll.VerticalOffset < scroll.ScrollableHeight - .1 : scroll.VerticalOffset > .1;
        private void Wheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || viewer.CanContentScroll || !SystemParameters.ClientAreaAnimation ||
                Keyboard.Modifiers != ModifierKeys.None || SystemParameters.WheelScrollLines == 0) return;
            // Preview tunnels through outer viewports first: let the innermost movable one own the input.
            for (var child = e.OriginalSource as DependencyObject; child is not null && child != viewer;
                child = child is Visual ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child))
                if (child is ScrollViewer nested && CanMove(nested, e.Delta)) return;
            if (!active)
            {
                position = requested = target = viewer.VerticalOffset;
                lastFrame = Stopwatch.GetTimestamp();
            }
            var distance = SystemParameters.WheelScrollLines < 0 ? viewer.ViewportHeight : SystemParameters.WheelScrollLines * 16d;
            var delta = -e.Delta / 120d * distance;
            // A reversal must respond now rather than first finishing accumulated travel in the old direction.
            if (delta * (target - position) < 0) target = position;
            target = Math.Clamp(target + delta, 0, viewer.ScrollableHeight);
            if (Math.Abs(target - position) < .1) return;
            if (!active) { active = true; CompositionTarget.Rendering += Frame; }
            e.Handled = true;
        }
        private void Frame(object? sender, EventArgs e)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(lastFrame, now).TotalSeconds;
            if (elapsed < .001) return; // Rendering can repeat at the same presentation time after layout.
            lastFrame = now;
            if (!viewer.IsVisible || !SystemParameters.ClientAreaAnimation) { Cancel(); return; }
            // Follow a changing target without restarting a steep ease-out curve for every wheel notch.
            position += (target - position) * (1 - Math.Exp(-Math.Min(elapsed, .05) / .065));
            var finished = Math.Abs(target - position) < .3;
            requested = finished ? target : position;
            viewer.ScrollToVerticalOffset(requested);
            if (finished) Cancel();
        }
    }
}
