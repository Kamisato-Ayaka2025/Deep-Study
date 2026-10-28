using System.Windows;
using Forms = System.Windows.Forms;

namespace StudyHarness.Desktop;

/// <summary>Owns the notification icon for the application, independently of lesson windows.</summary>
internal sealed class TrayLifetime : IDisposable
{
    private readonly Window mainWindow;
    private readonly Action exit;
    private readonly System.Drawing.Icon icon;
    private readonly Forms.NotifyIcon notification;
    private readonly Forms.ContextMenuStrip menu;
    private bool exiting;
    private WindowState lastState = WindowState.Normal;

    public TrayLifetime(Window mainWindow, Action exit)
    {
        this.mainWindow = mainWindow;
        this.exit = exit;
        if (mainWindow.WindowState != WindowState.Minimized) lastState = mainWindow.WindowState;
        using var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/deep-study.ico")).Stream;
        icon = new System.Drawing.Icon(resource);
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => Restore());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Exit());
        notification = new Forms.NotifyIcon { Icon = icon, Text = "Deep Study", ContextMenuStrip = menu, Visible = true };
        notification.DoubleClick += (_, _) => Restore();
        mainWindow.StateChanged += OnStateChanged;
    }

    internal bool IsVisible => notification.Visible;
    internal void InvokeMenu(int index) => menu.Items[index].PerformClick();
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (mainWindow.WindowState != WindowState.Minimized) lastState = mainWindow.WindowState;
    }

    public bool TryHide(Window window)
    {
        if (exiting || !ReferenceEquals(window, mainWindow) ||
            !ReferenceEquals(System.Windows.Application.Current.MainWindow, mainWindow)) return false;
        mainWindow.Hide();
        return true;
    }

    public void Restore()
    {
        if (exiting) return;
        mainWindow.Show();
        if (mainWindow.WindowState == WindowState.Minimized) mainWindow.WindowState = lastState;
        mainWindow.Activate();
    }

    public void Exit()
    {
        if (exiting) return;
        if (mainWindow is MainWindow shell && !shell.CanExit()) { Restore(); return; }
        exiting = true;
        exit();
    }

    public void Dispose()
    {
        mainWindow.StateChanged -= OnStateChanged;
        notification.Visible = false;
        notification.Dispose();
        menu.Dispose();
        icon.Dispose();
    }
}
