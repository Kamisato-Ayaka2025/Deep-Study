using System.Windows;
using Microsoft.Win32;
using StudyHarness.Application;
using StudyHarness.Contracts;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
namespace StudyHarness.Desktop;
public partial class MainWindow : Window
{
    private readonly ShellViewModel vm;
    private readonly WorkspaceViewModel workspace;
    private readonly PlanningViewModel planning;
    internal PlanningViewModel CourseModel { get; }
    private readonly GenerationService? generation;
    private readonly string? root;
    private readonly LabService? labs;
    private readonly Func<TutoringSession>? tutoring;
    private readonly ITutoringFiles? tutorFiles;
    private readonly ISubjectLifecycle? lifecycle;
    private Window? lessonWindow;
    public TaskCompletionSource Ready { get; } = new();
    internal int GraphRenderCount { get; private set; }
    private bool schedulerBusy, progressBusy, closed;
    private DateTimeOffset nextProgressRefresh;
    private readonly CancellationTokenSource schedulerLifetime = new();
    private readonly System.Windows.Threading.DispatcherTimer scheduler = new() { Interval = TimeSpan.FromMilliseconds(500) };
    public MainWindow(ShellViewModel viewModel, WorkspaceViewModel workspaceViewModel, PlanningViewModel planningViewModel, GenerationService? generationService = null, string? dataRoot = null, LabService? labService = null, Func<TutoringSession>? tutoringFactory = null, ITutoringFiles? files = null, ISubjectLifecycle? subjectLifecycle = null)
    {
        vm = viewModel;
        InitializeComponent();
        DataContext = vm;
        workspace = workspaceViewModel;
        WorkspacePanel.DataContext = workspace;
        planning = planningViewModel; CourseModel = planning.CreateBrowser(); ChatPanel.DataContext = planning; CoursesPanel.DataContext = CourseModel;
        generation = generationService; root = dataRoot; labs = labService;
        tutoring = tutoringFactory; tutorFiles = files; lifecycle = subjectLifecycle;
        CourseModel.Nodes.CollectionChanged += (_, _) => DrawGraph();
        workspace.References.CollectionChanged += (_, _) => { if (!syncReferences) planning.SetReferences(workspace.References.ToArray()); };
        workspace.PropertyChanged += (_, e) =>
        {
            if (!syncReferences && e.PropertyName is nameof(WorkspaceViewModel.OcrPages) or nameof(WorkspaceViewModel.SelectedCollection))
                planning.SetImportState(workspace.OcrPages, workspace.SelectedCollection?.Snapshot.Collection.Id);
        };
        ChatPanel.IsEnabled = false;
        Loaded += async (_, _) =>
        {
            try
            {
                syncReferences = true; await vm.LoadAsync(); await workspace.LoadAsync();
                await planning.RestoreSessionAsync(); SyncReferences(); ChatPanel.IsEnabled = true;
                await RefreshCoursesAsync(); Ready.TrySetResult();
            }
            catch (Exception ex) { Ready.TrySetException(ex); }
        };
        scheduler.Tick += (_, _) =>
        {
            if (!schedulerBusy && generation is not null) _ = RunSchedulerAsync();
            if (!progressBusy && DateTimeOffset.UtcNow >= nextProgressRefresh) _ = RefreshProgressAsync();
        };
        scheduler.Start();
        Closing += (_, e) => { if (!vm.CanLeaveSettings()) e.Cancel = true; };
        Closed += (_, _) => { closed = true; scheduler.Stop(); schedulerLifetime.Cancel(); generation?.Stop(); workspace.Cancel(); planning.Cancel(); };
    }
    private async Task RunSchedulerAsync()
    {
        if (closed || generation is null) return;
        schedulerBusy = true;
        try { await Task.Run(() => generation.TickAsync(schedulerLifetime.Token)); }
        catch (Exception) { /* Durable task state remains authoritative. */ }
        finally { schedulerBusy = false; }
    }
    private async Task RefreshProgressAsync()
    {
        if (closed) return;
        progressBusy = true;
        try { await CourseModel.RefreshProgressAsync(); }
        catch (Exception) { /* Retry the bounded refresh on the next interval. */ }
        finally { progressBusy = false; nextProgressRefresh = DateTimeOffset.UtcNow.AddSeconds(2); }
    }
    private void Select(string page)
    {
        if (!vm.CanLeaveSettings()) return;
        ChatPanel.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        CoursesPanel.Visibility = page == "courses" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        NavChatButton.FontWeight = page == "chat" ? FontWeights.Bold : FontWeights.Normal;
        ApiKey.Clear();
        MainScroll.ScrollToTop();
    }
    private void Chat_Click(object sender, RoutedEventArgs e) => Select("chat");
    private async void Courses_Click(object sender, RoutedEventArgs e) { Select("courses"); await RefreshCoursesAsync(); ShowCourseList(); }
    private void Settings_Click(object sender, RoutedEventArgs e) => Select("settings");
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKey.Password.Length == 0 ? null : ApiKey.Password;
        if (await vm.SaveAsync(key)) ApiKey.Clear();
    }
    private async void Reload_Click(object sender, RoutedEventArgs e) { ApiKey.Clear(); vm.DiscardPendingApiKey(); await vm.LoadAsync(); }
    private void ApiKey_PasswordChanged(object sender, RoutedEventArgs e) => vm.SetPendingApiKey(ApiKey.Password.Length != 0);
    private async void Probe_Click(object sender, RoutedEventArgs e) => await vm.CheckConnectionAsync();
    private void Workspace_Click(object sender, RoutedEventArgs e) => Select("workspace");
    private async void Library_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择资料库保存目录（不会迁移旧资料）" };
        if (dialog.ShowDialog(this) == true) await workspace.SwitchAsync(HostSelection.FromFolderPicker(dialog.FolderName));
    }
    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择只读导入的资料目录" };
        if (dialog.ShowDialog(this) == true) await workspace.ImportAsync(HostSelection.FromPlanFolderPicker(dialog.FolderName));
    }
    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "学习资料|*.pdf;*.c;*.cpp", Multiselect = true };
        if (dialog.ShowDialog(this) == true) foreach (var file in dialog.FileNames) await workspace.ImportAsync(HostSelection.FromPlanFilePicker(file));
    }
    private async void ImportProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 C/C++ 项目目录（只读导入 .c/.h/.cpp/.hpp 和 PDF/Markdown，不编译执行）" };
        if (dialog.ShowDialog(this) == true) await workspace.ImportAsync(HostSelection.FromProjectPicker(dialog.FolderName));
    }
    private async void ImportCollection_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "教材 PDF / Markdown|*.pdf;*.md;*.markdown", Title = "选择只读教材文件" };
        if (dialog.ShowDialog(this) == true) await workspace.ImportCollectionAsync(HostSelection.FromFilePicker(dialog.FileName));
    }
    private void UseImported_Click(object sender, RoutedEventArgs e) => workspace.UseImportedReferences();
    private async void PrepareOcr_Click(object sender, RoutedEventArgs e) => await workspace.PrepareOcrAsync();
    private async void RunOcr_Click(object sender, RoutedEventArgs e) => await workspace.RunOcrAsync();
    private async void Search_Click(object sender, RoutedEventArgs e) => await workspace.SearchAsync();
    private void AddRef_Click(object sender, RoutedEventArgs e) => workspace.AddReference();
    private void RemoveRef_Click(object sender, RoutedEventArgs e) => workspace.RemoveReference();
    private async void Resolve_Click(object sender, RoutedEventArgs e) => await workspace.ResolveAsync();
    private async void Prepare_Click(object sender, RoutedEventArgs e) => await workspace.PrepareAsync();
    private async void Allow_Click(object sender, RoutedEventArgs e) => await workspace.SendAsync(true);
    private async void Deny_Click(object sender, RoutedEventArgs e) => await workspace.SendAsync(false);
    private void Cancel_Click(object sender, RoutedEventArgs e) => workspace.Cancel();
    private void NewPlan_Click(object sender, RoutedEventArgs e) => planning.NewDraft();
    private async void ReloadPlans_Click(object sender, RoutedEventArgs e) => await planning.LoadAsync();
    private void UseCollection_Click(object sender, RoutedEventArgs e) => workspace.UseCollection();
    private async void SavePlan_Click(object sender, RoutedEventArgs e) => await planning.SaveAsync(workspace.References.ToArray());
    private void OpenClarification_Click(object sender, RoutedEventArgs e)
    {
        if (planning.CanOpenClarification) new PlanClarificationWindow(planning) { Owner = this }.ShowDialog();
    }
    private async void PreparePlan_Click(object sender, RoutedEventArgs e)
    {
        if (workspace.Busy) return;
        await planning.FormSubmissionAsync(); SyncReferences();
    }
    private async void AcquireWeb_Click(object sender, RoutedEventArgs e) => await planning.AcquireWebAsync();
    private async void ProposePlan_Click(object sender, RoutedEventArgs e) { if (!workspace.Busy) await planning.ProposeAsync(true); }
    private async void DenyPlan_Click(object sender, RoutedEventArgs e) => await planning.ProposeAsync(false);
    private void CancelPlan_Click(object sender, RoutedEventArgs e) => planning.Cancel();
    private async void ConfirmPlan_Click(object sender, RoutedEventArgs e)
    {
        if (workspace.Busy) return;
        await planning.ConfirmAsync();
        if (planning.ConfirmedSubject is not null)
        {
            planning.NewDraft(); SyncReferences(); await RefreshCoursesAsync(); Select("courses"); ShowCourseList();
        }
    }
    private bool syncReferences;
    private void SyncReferences()
    {
        syncReferences = true;
        try
        {
            workspace.RestoreReferences(planning.LocalReferences);
            workspace.OcrPages = planning.SavedOcrPages;
            workspace.SelectedCollection = workspace.Collections.FirstOrDefault(c => c.Snapshot.Collection.Id == planning.SavedOcrCollectionId);
        }
        finally { syncReferences = false; }
    }
    private void ShowCourseList()
    {
        CourseMap.Visibility = Visibility.Collapsed; CourseList.Visibility = Visibility.Visible;
        NoSubjects.Visibility = CourseList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void CourseBack_Click(object sender, RoutedEventArgs e) { await RefreshCoursesAsync(); ShowCourseList(); }
    internal async Task RefreshCoursesAsync()
    {
        await CourseModel.LoadAsync();
        CourseList.Children.Clear();
        foreach (var choice in CourseModel.Drafts.Where(d => d.Draft.Confirmed))
        {
            var draft = choice.Draft; var plan = draft.Plan!;
            var done = generation is null ? 0 : (await generation.Learning.CompletedNodesAsync(draft.Id)).Count(k => plan.Candidate.Nodes.Any(n => n.Key == k));
            var total = plan.Candidate.Nodes.Count;
            var ratio = total == 0 ? 0 : (double)done / total;
            var color = (Brush)new BrushConverter().ConvertFromString(ratio >= 1 ? "#16835B" : ratio < .1 ? "#DC4B59" : "#CB791E")!;
            var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = plan.Candidate.Title, FontSize = 21, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = $"{done} / {total} 课已完成", Foreground = color, Margin = new Thickness(0,10,0,8) });
            text.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(total,1), Value = done, Height = 5, Foreground = color, Background = Brushes.AliceBlue, BorderThickness = new Thickness(0) });
            var open = new Button { Content = text, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(22), Background = Brushes.White, Margin = new Thickness(0,0,10,14) };
            System.Windows.Automation.AutomationProperties.SetAutomationId(open, "Subject-" + draft.Id);
            open.Click += (_, _) => { CourseModel.Selected = choice; CourseTitle.Text = plan.Candidate.Title; CourseList.Visibility = NoSubjects.Visibility = Visibility.Collapsed; CourseMap.Visibility = Visibility.Visible; DrawGraph(); };
            var delete = new Button { Content = "\uE74D", FontFamily = new FontFamily("Segoe MDL2 Assets"), Foreground = Brushes.Crimson, Background = Brushes.Transparent, ToolTip = "删除课程", VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetAutomationId(delete, "Delete-" + draft.Id);
            delete.Click += async (_, _) => await DeleteCourseAsync(choice);
            Grid.SetColumn(delete, 1); grid.Children.Add(open); grid.Children.Add(delete); CourseList.Children.Add(grid);
        }
        NoSubjects.Visibility = CourseList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task DeleteCourseAsync(DraftChoice choice)
    {
        if (lifecycle is null || planning.Busy || CourseModel.Busy) return;
        if (MessageBox.Show(this, $"你真的想要删除这个课程吗？\n{choice.Draft.Plan?.Candidate.Title}\n课程内容及学习记录将被删除，此操作不可撤销。", "删除课程", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        lessonWindow?.Close();
        try
        {
            var result = await lifecycle.DeleteAsync(choice.Draft.Id, choice.Draft.Revision, true);
            if (!result.Ok) { MessageBox.Show(this, result.Error!.Message); return; }
            await RefreshCoursesAsync(); ShowCourseList();
        }
        catch (Exception) { MessageBox.Show(this, "删除未完成，请保留数据后重试。", "删除失败"); }
    }
    private void DrawGraph()
    {
        GraphRenderCount++;
        CourseCanvas.Children.Clear();
        var nodes = CourseModel.Nodes.OrderBy(n => n.Layer).ToArray();
        CourseCanvas.Width = 650; CourseCanvas.Height = Math.Max(60, nodes.Length * 112 + 20);
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i]; var y = 16 + i * 112;
            if (i > 0)
            {
                CourseCanvas.Children.Add(new Line { X1 = 295, X2 = 295, Y1 = y - 23, Y2 = y - 4, Stroke = Brushes.LightSteelBlue, StrokeThickness = 2 });
                CourseCanvas.Children.Add(new Polygon { Points = new PointCollection { new(290,y-9), new(300,y-9), new(295,y-3) }, Fill = Brushes.LightSteelBlue });
            }
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = $"{i + 1:00}   {node.Title}", FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            text.Children.Add(new TextBlock { Text = $"{(node.Type == "lab" ? "实践" : "理论")} · {node.Minutes} 分钟 · {node.Status}", FontSize = 12, Foreground = Brushes.SlateGray, Margin = new Thickness(0,6,0,0) });
            var button = new Button { Width = 550, Height = 88, Margin = new Thickness(0), Padding = new Thickness(18), Background = Brushes.AliceBlue, Content = text, ToolTip = node.Objectives };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "Course-" + node.Key);
            button.Click += (_, _) => CourseModel.SelectNode(node);
            button.MouseDoubleClick += (_, e) => { e.Handled = true; OpenLesson(node); };
            button.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; OpenLesson(node); } };
            Canvas.SetLeft(button,20); Canvas.SetTop(button,y); CourseCanvas.Children.Add(button);
        }
    }
    private void OpenLesson(CourseNodeView node)
    {
        lessonWindow?.Close(); CourseModel.SelectNode(node);
        if (node.Type == "lab")
        {
            if (labs is not null && CourseModel.ConfirmedSubject is { } labSubject)
            { lessonWindow = new LabPreviewWindow(new LabViewModel(labs, labSubject, node.Key), tutoring?.Invoke(), tutorFiles, labSubject, node.Key) { Owner = this }; lessonWindow.Show(); }
        }
        else if (generation is not null && root is not null && CourseModel.ConfirmedSubject is { } subject)
        { lessonWindow = new LearningWindow(generation, subject, node.Key, root, tutoring?.Invoke(), tutorFiles) { Owner = this }; lessonWindow.Show(); }
    }
}
