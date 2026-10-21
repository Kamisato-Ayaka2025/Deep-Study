using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace StudyHarness.Desktop;

/// <summary>Visibility from a bool; pass "invert" to flip. Null/false collapse.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase)) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the string is empty; pass "invert" for visible when non-empty.</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = string.IsNullOrWhiteSpace(value as string);
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase)) empty = !empty;
        return empty ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Course progress colour: red below 10%, amber below 100%, green when complete.</summary>
public sealed class ProgressBrushConverter : IValueConverter
{
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xF0, 0x44, 0x38));
    private static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(0xF7, 0x90, 0x09));
    private static readonly Brush Success = new SolidColorBrush(Color.FromRgb(0x12, 0xB7, 0x6A));
    static ProgressBrushConverter()
    {
        foreach (var brush in new[] { Danger, Warning, Success }) brush.Freeze();
    }
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = value is double d ? d : 0;
        return ratio >= 1 ? Success : ratio < 0.1 ? Danger : Warning;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PercentTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ratio = value is double d ? d : 0;
        return $"{Math.Round(Math.Clamp(ratio, 0, 1) * 100)}%";
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Zero count is visible (used for empty states); pass "invert" for non-zero.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var zero = value is int i && i == 0;
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase)) zero = !zero;
        return zero ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}
