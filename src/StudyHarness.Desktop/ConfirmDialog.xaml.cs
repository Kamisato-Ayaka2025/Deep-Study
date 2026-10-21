using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace StudyHarness.Desktop;

/// <summary>Small themed confirmation dialog; replaces MessageBox for destructive or costly actions.</summary>
public partial class ConfirmDialog : Window
{
    private ConfirmDialog() => InitializeComponent();

    public static bool Ask(Window? owner, string title, string message, string okText = "确认", bool danger = false, string cancelText = "取消", string? subtitle = null)
    {
        var dialog = new ConfirmDialog();
        if (owner is not null && owner.IsVisible) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.TitleText.Text = title;
        dialog.SubtitleText.Text = subtitle ?? "Deep Study · 请确认";
        dialog.MessageText.Text = message;
        dialog.OkButton.Content = okText;
        dialog.CancelButton.Content = cancelText;
        dialog.OkButton.IsDefault = true;
        if (danger)
        {
            dialog.GlyphTile.Background = (Brush)dialog.FindResource("DsDangerSoftBrush");
            dialog.Glyph.Background = Brushes.Transparent;
            dialog.Glyph.Foreground = (Brush)dialog.FindResource("DsDangerBrush");
            dialog.Glyph.Text = "\uE7BA";
            dialog.OkButton.Style = (Style)dialog.FindResource("DangerButton");
        }
        return dialog.ShowDialog() == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }
}
