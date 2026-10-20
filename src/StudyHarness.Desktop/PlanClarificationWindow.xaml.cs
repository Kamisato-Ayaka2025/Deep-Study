using System.Windows;

namespace StudyHarness.Desktop;

public partial class PlanClarificationWindow : Window
{
    private readonly PlanningViewModel planning;
    public PlanClarificationWindow(PlanningViewModel planning)
    {
        InitializeComponent();
        this.planning = planning;
        DataContext = planning;
        Closing += (_, e) =>
        {
            if (planning.Busy) { planning.Cancel(); e.Cancel = true; }
        };
    }
    private async void SaveAnswer_Click(object sender, RoutedEventArgs e) => await planning.SaveClarificationAsync();
    private async void Regenerate_Click(object sender, RoutedEventArgs e) => await planning.ProposeAsync(true);
    private async void Retry_Click(object sender, RoutedEventArgs e) => await planning.RetryPlanAsync();
    private void Cancel_Click(object sender, RoutedEventArgs e) => planning.Cancel();
    private void Return_Click(object sender, RoutedEventArgs e) => Close();
}
