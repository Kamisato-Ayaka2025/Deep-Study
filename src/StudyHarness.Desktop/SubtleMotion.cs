using System.Windows;
using System.Windows.Media.Animation;

namespace StudyHarness.Desktop;

public static class SubtleMotion
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SubtleMotion), new PropertyMetadata(false, (d, e) =>
        {
            if (d is not FrameworkElement element) return;
            if ((bool)e.NewValue) element.IsVisibleChanged += Visible;
            else element.IsVisibleChanged -= Visible;
        }));
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    private static void Visible(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && SystemParameters.ClientAreaAnimation)
            ((FrameworkElement)sender).BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(.88, 1, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop });
    }
}
