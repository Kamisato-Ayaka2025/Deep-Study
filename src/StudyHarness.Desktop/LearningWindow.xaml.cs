using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Desktop;

public partial class LearningWindow : Window
{
    public LearningViewModel Model { get; }
    internal Func<bool>? ConfirmRegeneration { get; set; }
    public LessonWebView Browser => Reader;
    public TaskCompletionSource ReaderInitialized { get; } = new();
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromSeconds(2) };
    public LearningWindow(GenerationService service, Guid subject, string? node, string root, TutoringSession? tutoring = null, ITutoringFiles? files = null)
    {
        InitializeComponent();
        if (tutoring is not null && files is not null && node is not null) { Tutor.Configure(tutoring, files, subject, node); Tutor.Visibility = Visibility.Visible; }
        Model = new(service, subject); DataContext = Model;
        Model.LessonChanged += lesson => { if (Reader.ReaderReady) Reader.ShowLesson(lesson); };
        Model.LessonCleared += () => { if (Reader.ReaderReady) Reader.ClearLesson(); };
        Model.Nodes.CollectionChanged += (_, _) => RefreshOutline();
        Model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LearningViewModel.Selected) or nameof(LearningViewModel.Title)) RefreshOutline();
        };
        Reader.ReaderMessage += async message => await Model.SafeAsync(async () =>
        {
            var action = message.GetProperty("action").GetString();
            if (action == "ready") { if (Model.Reader is { } lesson) Reader.ShowLesson(lesson); return; }
            var hash = message.GetProperty("lessonHash").GetString()!;
            if (!Guid.TryParse(message.GetProperty("requestId").GetString(), out var id) || id == Guid.Empty) return;
            if (action == "answer") await Model.SubmitAsync(hash, message.GetProperty("questionId").GetString()!, message.GetProperty("optionId").GetString()!, id);
            if (action == "position") await Model.PositionAsync(hash, message.GetProperty("blockId").GetString()!, message.GetProperty("offset").GetInt32(), id);
        });
        Loaded += async (_, _) =>
        {
            try
            {
                await Reader.InitializeAsync(root); await Model.RefreshAsync();
                Model.Selected = Model.Nodes.FirstOrDefault(n => n.Key == node) ?? Model.Selected;
                await Model.OpenAsync(); RefreshOutline(); refresh.Start(); ReaderInitialized.TrySetResult();
            }
            catch (Exception) { ReaderInitialized.TrySetException(new InvalidOperationException("Reader initialization failed")); MessageBox.Show("阅读器初始化失败，请检查WebView2 Runtime安装。", "阅读器"); }
        };
        refresh.Tick += async (_, _) => await Model.SafeAsync(Model.RefreshAsync);
        Closed += (_, _) => { refresh.Stop(); Model.Close(); Tutor.CloseSession(); Reader.Dispose(); };
    }
    private readonly Dictionary<string, (string Signature, Button Button)> outlineCache = new();
    private void RefreshOutline()
    {
        if (OutlinePanel is null) return;
        var selectedKey = Model.Selected?.Key;
        var index = 0;
        var keys = Model.Nodes.Select(n => n.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in outlineCache.Keys.Where(k => !keys.Contains(k)).ToArray()) outlineCache.Remove(key);
        foreach (var node in Model.Nodes)
        {
            var selected = node.Key == selectedKey;
            var signature = node.Detail + "\0" + node.Label + "\0" + selected;
            if (outlineCache.TryGetValue(node.Key, out var cached) && cached.Signature == signature)
            {
                Place(cached.Button); continue;
            }
            var title = node.Detail.Split('\n')[0];
            var state = node.Label.Split(" · ").LastOrDefault() ?? "";
            var color = OutlineColor(state);
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = color, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 10, 0) };
            content.Children.Add(dot);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = title, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = selected ? (Brush)FindResource("DsPrimaryBrush") : (Brush)FindResource("DsBodyBrush")
            });
            text.Children.Add(new TextBlock { Text = ShortState(state), FontSize = 10.5, Foreground = selected ? color : (Brush)FindResource("DsFaintBrush"), TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(text, 1); content.Children.Add(text);
            var button = new Button
            {
                Content = content, Style = (Style)FindResource("OutlineItemButton"),
                Tag = selected ? "selected" : null, HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(button, "Outline-" + node.Key);
            button.Click += (_, _) => { if (Model.Selected?.Key != node.Key) Model.Selected = Model.Nodes.FirstOrDefault(n => n.Key == node.Key); RefreshOutline(); };
            outlineCache[node.Key] = (signature, button);
            Place(button);
        }
        while (OutlinePanel.Children.Count > index) OutlinePanel.Children.RemoveAt(index);
        void Place(Button button)
        {
            if (index < OutlinePanel.Children.Count && ReferenceEquals(OutlinePanel.Children[index], button)) { index++; return; }
            OutlinePanel.Children.Remove(button);
            OutlinePanel.Children.Insert(index++, button);
        }
    }
    private static string ShortState(string state) => state.Length > 22 ? state[..22] + "…" : state;
    private Brush OutlineColor(string state)
    {
        if (state.Contains("已完成", StringComparison.Ordinal)) return (Brush)FindResource("DsSuccessBrush");
        if (state.Contains("就绪", StringComparison.Ordinal)) return (Brush)FindResource("DsPrimaryBrush");
        return (Brush)FindResource("DsFaintBrush");
    }
    private void Back_Click(object sender, RoutedEventArgs e) => Close();
    private async void Open_Click(object sender, RoutedEventArgs e) => await Model.SafeAsync(Model.OpenAsync);
    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (Model.HasCurrentLesson && !(ConfirmRegeneration?.Invoke() ?? ConfirmDialog.Ask(this,
            "重新生成课文",
            "你真的要重新生成课文吗？这意味着清空你当前的课文。\n\n新课文成功生成后会替换当前课文；失败或取消会保留原文，旧版本仍可在历史中阅读。",
            "重新生成", subtitle: "Deep Study · 生成会按服务商计费"))) return;
        await Model.SafeAsync(Model.GenerateAsync);
    }
    private void History_Click(object sender, RoutedEventArgs e) => Model.OpenHistory();
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await Model.SafeAsync(Model.CancelAsync);
    private void HidePreview_Click(object sender, RoutedEventArgs e) => Model.HidePreview();
    private async void Prepare_Click(object sender, RoutedEventArgs e) => await Model.SafeAsync(() => Model.PrepareAsync(false));
}
