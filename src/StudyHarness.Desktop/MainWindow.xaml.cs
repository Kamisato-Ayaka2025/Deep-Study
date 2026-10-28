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
    internal bool CanExit() => vm.CanLeaveSettings();
    private CourseNodeView? selectedCourseNode;
    private double lastRouteWidth;
    private bool graphDirty = true, courseRefreshAgain;
    private Task? courseRefreshTask;
    private readonly Dictionary<Guid, (long Revision, string Hash, int Done, Button Card)> courseCards = [];
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
        VersionText.Text = "Deep Study v" + (typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "1.5.0") + " · 本地学习助手";
        workspace = workspaceViewModel;
        WorkspacePanel.DataContext = workspace;
        planning = planningViewModel; CourseModel = planning.CreateBrowser(); ChatPanel.DataContext = planning; CoursesPanel.DataContext = CourseModel;
        generation = generationService; root = dataRoot; labs = labService;
        tutoring = tutoringFactory; tutorFiles = files; lifecycle = subjectLifecycle;
        CourseModel.Nodes.CollectionChanged += (_, _) => { graphDirty = true; if (CourseMap.IsVisible) DrawGraph(); };
        workspace.References.CollectionChanged += (_, _) => { workspace.RefreshMaterials(); if (!syncReferences) planning.SetReferences(workspace.References.ToArray()); };
        workspace.PendingCollections.CollectionChanged += (_, _) => { workspace.RefreshMaterials(); if (!syncReferences) planning.SetPendingCollections(workspace.PendingCollections.ToArray()); };
        workspace.PropertyChanged += (_, e) =>
        {
            if (!syncReferences && e.PropertyName is nameof(WorkspaceViewModel.OcrPages) or nameof(WorkspaceViewModel.SelectedCollection))
                planning.SetImportState(workspace.OcrPages, workspace.SelectedCollection?.Snapshot.Collection.Id);
        };
        RouteViewport.SizeChanged += (_, e) =>
        {
            if (closed || CourseMap.Visibility != Visibility.Visible || e.NewSize.Width <= 0) return;
            if (Math.Abs(e.NewSize.Width - lastRouteWidth) < 24) return;
            // Layout-driven redraws keep the route readable after a window resize but are
            // not graph data renders; the batched-render check counts data changes only.
            DrawGraphCore(countRender: false);
        };
        ChatPanel.IsEnabled = false;
        Select("chat");
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
            if (CourseMap.IsVisible && !progressBusy && DateTimeOffset.UtcNow >= nextProgressRefresh) _ = RefreshProgressAsync();
        };
        scheduler.Start();
        Closing += (_, e) =>
        {
            if (System.Windows.Application.Current is App app && app.TryHideToTray(this)) { e.Cancel = true; return; }
            if (!vm.CanLeaveSettings()) e.Cancel = true;
        };
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
        NavChatButton.Tag = page == "chat" ? "active" : null;
        NavCoursesButton.Tag = page == "courses" ? "active" : null;
        NavSettingsButton.Tag = page == "settings" ? "active" : null;
        ApiKey.Clear();
        MainScroll.ScrollToTop();
    }
    private void Chat_Click(object sender, RoutedEventArgs e) => Select("chat");
    private void GoChat_Click(object sender, RoutedEventArgs e) => Select("chat");
    private async void Courses_Click(object sender, RoutedEventArgs e) { Select("courses"); await RefreshCoursesAsync(); ShowCourseList(); }
    private void Settings_Click(object sender, RoutedEventArgs e) => Select("settings");
    private void Suggest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string suggestion } && planning.CanEdit) planning.Request = suggestion;
    }
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKey.Password.Length == 0 ? null : ApiKey.Password;
        if (await vm.SaveAsync(key)) ApiKey.Clear();
    }
    private async void Reload_Click(object sender, RoutedEventArgs e) { ApiKey.Clear(); vm.DiscardPendingApiKey(); await vm.LoadAsync(); }
    private void ApiKey_PasswordChanged(object sender, RoutedEventArgs e) => vm.SetPendingApiKey(ApiKey.Password.Length != 0);
    private async void Probe_Click(object sender, RoutedEventArgs e) => await vm.CheckConnectionAsync();
    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择只读导入的资料目录" };
        if (dialog.ShowDialog(this) == true) { await workspace.ImportAsync(HostSelection.FromPlanFolderPicker(dialog.FolderName)); ShowImportResult(); }
    }
    private async void ImportFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "学习资料|*.pdf;*.ppt;*.pptx;*.md;*.markdown;*.c;*.cpp", Multiselect = true };
        if (dialog.ShowDialog(this) == true) { await workspace.ImportManyAsync(dialog.FileNames.Select(HostSelection.FromPlanFilePicker)); ShowImportResult(); }
    }
    private async void ResumeImports_Click(object sender, RoutedEventArgs e) => await workspace.ResumeImportsAsync();
    private void ClearImports_Click(object sender, RoutedEventArgs e) => workspace.ClearImportQueue();
    private void ShowImportResult()
    {
        planning.ReportImportStatus(workspace.Status);
    }
    private async void PreparePlan_Click(object sender, RoutedEventArgs e)
    {
        if (workspace.Busy || workspace.ImportQueueCount > 0) { planning.ReportImportStatus("导入队列尚未处理完，请继续队列或清空待处理项后再形成计划。" ); SubmissionFeedback.BringIntoView(); return; }
        if (planning.PendingCollections.Count > 0)
        {
            ImportExpander.IsExpanded = true;
            workspace.SelectedCollection = workspace.Collections.FirstOrDefault(c => planning.PendingCollections.Contains(c.Snapshot.Collection.Id));
            workspace.SuggestOcrPages();
        }
        await planning.FormSubmissionAsync(); SyncReferences(); SubmissionFeedback.BringIntoView();
    }
    private async void RemoveMaterial_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: MaterialChoice material }) await workspace.DeleteMaterialAsync(material);
    }
    private async void ContinueScan_Click(object sender, RoutedEventArgs e) => await workspace.ContinueScanAsync();
    private async void ContinueMaterialScan_Click(object sender, RoutedEventArgs e)
    { if (sender is Button { DataContext: MaterialChoice material }) await workspace.ContinueMaterialScanAsync(material); }
    private void CancelImport_Click(object sender, RoutedEventArgs e) => workspace.Cancel();
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
            workspace.PendingCollections.Clear();
            foreach (var id in planning.PendingCollections) workspace.PendingCollections.Add(id);
            workspace.OcrPages = planning.SavedOcrPages;
            workspace.SelectedCollection = workspace.Collections.FirstOrDefault(c => c.Snapshot.Collection.Id == planning.SavedOcrCollectionId);
        }
        finally { syncReferences = false; }
    }
    private void ShowCourseList()
    {
        CourseMap.Visibility = Visibility.Collapsed; CourseList.Visibility = Visibility.Visible;
        CoursesHeader.Visibility = Visibility.Visible;
        NoSubjects.Visibility = CourseList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void CourseBack_Click(object sender, RoutedEventArgs e) { await RefreshCoursesAsync(); ShowCourseList(); }
    internal Task RefreshCoursesAsync()
    {
        if (courseRefreshTask is { IsCompleted: false } pending) { courseRefreshAgain = true; return pending; }
        courseRefreshTask = RefreshCoursesLoopAsync();
        return courseRefreshTask;
    }
    private async Task RefreshCoursesLoopAsync()
    {
        try
        {
            do { courseRefreshAgain = false; await RefreshCoursesCoreAsync(); } while (courseRefreshAgain && !closed);
        }
        catch (OperationCanceledException) when (closed) { }
    }
    private async Task RefreshCoursesCoreAsync()
    {
        if (closed) return;
        await CourseModel.LoadAsync();
        var choices = CourseModel.Drafts.Where(d => d.Draft.Confirmed).ToArray();
        // SQLite ports may complete synchronously. Never project all course progress on the dispatcher.
        var completed = await Task.Run(async () =>
        {
            var counts = new Dictionary<Guid, int>();
            foreach (var choice in choices)
            {
                schedulerLifetime.Token.ThrowIfCancellationRequested();
                var keys = choice.Draft.Plan!.Candidate.Nodes.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
                counts[choice.Draft.Id] = generation is null ? 0 :
                    (await generation.Learning.CompletedNodesAsync(choice.Draft.Id, schedulerLifetime.Token)).Count(keys.Contains);
            }
            return counts;
        }, schedulerLifetime.Token);
        if (closed || courseRefreshAgain) return;
        var cards = new List<Button>();
        foreach (var choice in choices)
        {
            var draft = choice.Draft; var plan = draft.Plan!;
            var done = completed[draft.Id];
            if (courseCards.TryGetValue(draft.Id, out var cached) && cached.Revision == draft.Revision && cached.Hash == plan.Hash && cached.Done == done)
            { cards.Add(cached.Card); continue; }
            var total = plan.Candidate.Nodes.Count;
            var ratio = total == 0 ? 0 : (double)done / total;
            var color = ProgressColor(ratio);
            var status = ratio >= 1 ? "已完成" : ratio <= 0 ? "尚未开始" : "学习中";

            var header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var tile = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(13), Background = (Brush)FindResource("DsPrimarySoftBrush"), VerticalAlignment = VerticalAlignment.Top };
            tile.Child = new TextBlock { Text = "\uE8F1", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 17, Foreground = (Brush)FindResource("DsPrimaryBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            header.Children.Add(tile);

            var text = new StackPanel { Margin = new Thickness(12, 0, 8, 0) };
            text.Children.Add(new TextBlock { Text = plan.Candidate.Title, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("DsInkBrush"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
            text.Children.Add(new TextBlock { Text = $"{done} / {total} 课已完成", FontSize = 12, Foreground = color, Margin = new Thickness(0, 5, 0, 0) });
            Grid.SetColumn(text, 1); header.Children.Add(text);

            var delete = new Button
            {
                Content = new TextBlock { Text = "\uE74D", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = (Brush)FindResource("DsFaintBrush") },
                Style = (Style)FindResource("DangerIconButton"),
                Width = 32, Height = 32, Padding = new Thickness(0), ToolTip = "删除课程", VerticalAlignment = VerticalAlignment.Top
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(delete, "Delete-" + draft.Id);
            delete.Click += async (_, _) => await DeleteCourseAsync(choice);
            Grid.SetColumn(delete, 2); header.Children.Add(delete);

            var progress = new ProgressBar { Minimum = 0, Maximum = Math.Max(total, 1), Value = done, Height = 6, Margin = new Thickness(0, 16, 0, 0), Background = (Brush)FindResource("DsSurfaceMutedBrush"), Foreground = color };

            var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 13, 0, 0) };
            chips.Children.Add(Chip($"{Math.Round(ratio * 100)}%", color, true));
            chips.Children.Add(Chip(status, color, false));
            chips.Children.Add(Chip($"共 {total} 课", (Brush)FindResource("DsMutedBrush"), false));

            var body = new StackPanel();
            body.Children.Add(header);
            body.Children.Add(progress);
            body.Children.Add(chips);

            var open = new Button
            {
                Content = body,
                Style = (Style)FindResource("CourseCardButton"),
                Width = 366,
                Margin = new Thickness(0, 0, 16, 16),
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(open, "Subject-" + draft.Id);
            open.Click += (_, _) => OpenCourse(choice);
            cards.Add(open);
            courseCards[draft.Id] = (draft.Revision, plan.Hash, done, open);
        }
        var live = choices.Select(c => c.Draft.Id).ToHashSet();
        foreach (var id in courseCards.Keys.Where(id => !live.Contains(id)).ToArray()) courseCards.Remove(id);
        if (!CourseList.Children.Cast<UIElement>().SequenceEqual(cards))
        {
            CourseList.Children.Clear();
            foreach (var card in cards) CourseList.Children.Add(card);
        }
        NoSubjects.Visibility = CourseList.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private static Border Chip(string text, Brush foreground, bool soft)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(9, 3, 9, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Background = soft ? new SolidColorBrush(Color.FromArgb(28, 77, 107, 254)) : new SolidColorBrush(Color.FromRgb(0xF1, 0xF4, 0xFA))
        };
        border.Child = new TextBlock { Text = text, FontSize = 11, Foreground = foreground, TextWrapping = TextWrapping.NoWrap };
        return border;
    }
    private static Brush ProgressColor(double ratio) => ratio >= 1
        ? new SolidColorBrush(Color.FromRgb(0x12, 0xB7, 0x6A))
        : ratio < 0.1 ? new SolidColorBrush(Color.FromRgb(0xF0, 0x44, 0x38)) : new SolidColorBrush(Color.FromRgb(0xF7, 0x90, 0x09));
    private void OpenCourse(DraftChoice choice)
    {
        CourseModel.Selected = choice;
        CourseTitle.Text = choice.Draft.Plan?.Candidate.Title ?? "课程";
        CoursesHeader.Visibility = Visibility.Collapsed;
        CourseList.Visibility = NoSubjects.Visibility = Visibility.Collapsed;
        CourseMap.Visibility = Visibility.Visible;
        if (graphDirty) DrawGraph();
        nextProgressRefresh = DateTimeOffset.MinValue;
        var first = CourseModel.Nodes.OrderBy(n => n.Layer).FirstOrDefault();
        if (first is not null) SelectNode(first);
        UpdateCourseMeta();
    }
    private static bool IsStatusLine(string line) =>
        line.StartsWith("可自由", StringComparison.Ordinal) || line.StartsWith("已完成", StringComparison.Ordinal) ||
        line.StartsWith("正文就绪", StringComparison.Ordinal) || line.StartsWith("材料就绪", StringComparison.Ordinal) ||
        line.StartsWith("用户确认完成", StringComparison.Ordinal) || line.StartsWith("未生成", StringComparison.Ordinal);
    private void UpdateCourseMeta()
    {
        var nodes = CourseModel.Nodes.ToArray();
        var done = nodes.Count(n => n.Status.StartsWith("已完成", StringComparison.Ordinal) || n.Status.StartsWith("用户确认完成", StringComparison.Ordinal));
        CourseProgressChip.Text = $"{done} / {nodes.Length} 课已完成";
    }
    private void SelectNode(CourseNodeView node)
    {
        selectedCourseNode = node;
        CourseModel.SelectNode(node);
        CourseDetailTitle.Text = node.Title;
        CourseDetailMeta.Text = $"{(node.Type == "lab" ? "实践课" : "理论课")} · 预计 {node.Minutes} 分钟";
        var lines = CourseModel.Detail.Replace("\r\n", "\n").Split('\n');
        CourseDetailStatus.Text = lines.FirstOrDefault(IsStatusLine) ?? "";
        CourseDetailText.Text = string.Join("\n", lines.Skip(1).Where(l => l.StartsWith("目标：", StringComparison.Ordinal) || l.StartsWith("建议先学：", StringComparison.Ordinal)));
        foreach (var button in CourseCanvas.Children.OfType<Button>())
            button.Tag = System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "Course-" + node.Key ? "selected" : null;
        UpdateCourseMeta();
    }
    private async Task DeleteCourseAsync(DraftChoice choice)
    {
        if (lifecycle is null || planning.Busy || CourseModel.Busy) return;
        var title = choice.Draft.Plan?.Candidate.Title ?? "该课程";
        if (!ConfirmDialog.Ask(this, "删除课程", $"你真的想要删除这个课程吗？\n\n{title}\n\n课程正文、作答记录与学习进度都会被删除，此操作不可撤销。", "删除课程", danger: true, subtitle: "Deep Study · 删除后无法恢复")) return;
        lessonWindow?.Close();
        try
        {
            var result = await lifecycle.DeleteAsync(choice.Draft.Id, choice.Draft.Revision, true);
            if (!result.Ok) { ConfirmDialog.Ask(this, "删除未完成", result.Error!.Message, "我知道了", subtitle: "Deep Study · 请保留数据后重试"); return; }
            await RefreshCoursesAsync(); ShowCourseList();
        }
        catch (Exception) { ConfirmDialog.Ask(this, "删除失败", "删除未完成，请保留数据后重试。", "我知道了", subtitle: "Deep Study · 操作未完成"); }
    }
    private void DrawGraph() => DrawGraphCore(countRender: true);
    private void DrawGraphCore(bool countRender)
    {
        graphDirty = false;
        if (countRender) GraphRenderCount++;
        CourseCanvas.Children.Clear();
        var nodes = CourseModel.Nodes.OrderBy(n => n.Layer).ToArray();
        var width = Math.Max(460, RouteViewport.ActualWidth > 0 ? RouteViewport.ActualWidth - 14 : 560);
        lastRouteWidth = RouteViewport.ActualWidth;
        const double nodeHeight = 94, gap = 24;
        var nodeWidth = width - 56;
        CourseCanvas.Width = width; CourseCanvas.Height = Math.Max(80, nodes.Length * (nodeHeight + gap) + 24);
        var center = width / 2;
        for (var i = 0; i < nodes.Length; i++)
        {
            var node = nodes[i]; var y = 12 + i * (nodeHeight + gap);
            if (i > 0)
            {
                CourseCanvas.Children.Add(new Line
                {
                    X1 = center, X2 = center, Y1 = y - gap + 4, Y2 = y - 10,
                    Stroke = (Brush)FindResource("DsLineGradient"), StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                });
                CourseCanvas.Children.Add(new Polygon
                {
                    Points = new PointCollection { new(center - 4, y - 15), new(center + 4, y - 15), new(center, y - 8) },
                    Fill = new SolidColorBrush(Color.FromRgb(0xAE, 0xC0, 0xF5))
                });
            }
            var button = new Button
            {
                Width = nodeWidth, Height = nodeHeight, Padding = new Thickness(0),
                Style = (Style)FindResource("CourseNodeButton"),
                Tag = selectedCourseNode?.Key == node.Key ? "selected" : null,
                Content = NodeContent(node, i + 1)
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "Course-" + node.Key);
            button.Click += (_, _) => SelectNode(node);
            button.MouseDoubleClick += (_, e) => { e.Handled = true; OpenLesson(node); };
            button.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; OpenLesson(node); } };
            Canvas.SetLeft(button, 28); Canvas.SetTop(button, y); CourseCanvas.Children.Add(button);
        }
        UpdateCourseMeta();
    }
    private object NodeContent(CourseNodeView node, int index)
    {
        var lab = node.Type == "lab";
        var (soft, strong) = NodeColors(node.Status);
        var grid = new Grid { Margin = new Thickness(18, 0, 18, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var badge = new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(13), Background = soft, VerticalAlignment = VerticalAlignment.Center };
        badge.Child = new TextBlock { Text = index.ToString("00"), FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = strong, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(badge);

        var text = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = node.Title, FontSize = 14.5, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("DsInkBrush"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 0) };
        meta.Children.Add(new TextBlock { Text = lab ? "实践" : "理论", FontSize = 11.5, Foreground = (Brush)FindResource("DsMutedBrush") });
        meta.Children.Add(new TextBlock { Text = "  ·  ", FontSize = 11.5, Foreground = (Brush)FindResource("DsFaintBrush") });
        meta.Children.Add(new TextBlock { Text = $"{node.Minutes} 分钟", FontSize = 11.5, Foreground = (Brush)FindResource("DsMutedBrush") });
        meta.Children.Add(new TextBlock { Text = "  ·  ", FontSize = 11.5, Foreground = (Brush)FindResource("DsFaintBrush") });
        meta.Children.Add(new TextBlock { Text = node.Status, FontSize = 11.5, Foreground = strong });
        text.Children.Add(meta);
        Grid.SetColumn(text, 1); grid.Children.Add(text);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (lab)
        {
            var labChip = new Border { CornerRadius = new CornerRadius(20), Background = (Brush)FindResource("DsLabSoftBrush"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 10, 0) };
            labChip.Child = new TextBlock { Text = "实验", FontSize = 10.5, Foreground = (Brush)FindResource("DsLabBrush") };
            right.Children.Add(labChip);
        }
        var chevron = new TextBlock { Text = "\uE76C", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = (Brush)FindResource("DsFaintBrush"), VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(chevron);
        Grid.SetColumn(right, 2); grid.Children.Add(right);
        return grid;
    }
    private (Brush soft, Brush strong) NodeColors(string status)
    {
        if (status.StartsWith("已完成", StringComparison.Ordinal) || status.StartsWith("用户确认完成", StringComparison.Ordinal))
            return ((Brush)FindResource("DsSuccessSoftBrush"), (Brush)FindResource("DsSuccessBrush"));
        if (status.Contains("就绪", StringComparison.Ordinal))
            return ((Brush)FindResource("DsPrimarySoftBrush"), (Brush)FindResource("DsPrimaryBrush"));
        return ((Brush)FindResource("DsSurfaceMutedBrush"), (Brush)FindResource("DsMutedBrush"));
    }
    private void OpenSelectedNode_Click(object sender, RoutedEventArgs e)
    {
        var node = selectedCourseNode ?? CourseModel.Nodes.OrderBy(n => n.Layer).FirstOrDefault();
        if (node is not null) OpenLesson(node);
    }
    private void OpenLesson(CourseNodeView node)
    {
        lessonWindow?.Close(); SelectNode(node);
        if (node.Type == "lab")
        {
            if (labs is not null && CourseModel.ConfirmedSubject is { } labSubject)
            { lessonWindow = new LabPreviewWindow(new LabViewModel(labs, labSubject, node.Key), tutoring?.Invoke(), tutorFiles, labSubject, node.Key); lessonWindow.Show(); }
        }
        else if (generation is not null && root is not null && CourseModel.ConfirmedSubject is { } subject)
        { lessonWindow = new LearningWindow(generation, subject, node.Key, root, tutoring?.Invoke(), tutorFiles); lessonWindow.Show(); }
    }
}
