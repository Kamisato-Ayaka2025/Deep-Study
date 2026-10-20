using System.Text.Json;
using System.Windows;
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
                await Model.OpenAsync(); refresh.Start(); ReaderInitialized.TrySetResult();
            }
            catch (Exception e) { ReaderInitialized.TrySetException(e); MessageBox.Show("阅读器初始化失败，请检查WebView2 Runtime安装。", "阅读器"); }
        };
        refresh.Tick += async (_, _) => await Model.SafeAsync(Model.RefreshAsync);
        Closed += (_, _) => { refresh.Stop(); Model.Close(); Tutor.CloseSession(); Reader.Dispose(); };
    }
    private void Back_Click(object sender, RoutedEventArgs e) => Close();
    private async void Open_Click(object sender, RoutedEventArgs e) => await Model.SafeAsync(Model.OpenAsync);
    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (Model.HasCurrentLesson && !(ConfirmRegeneration?.Invoke() ?? (MessageBox.Show(this,
            "你真的要重新生成课文吗？这意味着清空你当前的课文。\n新课文成功生成后替换当前课文；失败或取消保留原文，旧版本可在历史中阅读。",
            "重新生成课文", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes))) return;
        await Model.SafeAsync(Model.GenerateAsync);
    }
    private void History_Click(object sender, RoutedEventArgs e) => Model.OpenHistory();
    private async void Retry_Click(object sender, RoutedEventArgs e) => await Model.SafeAsync(() => Model.RequestAsync(true));
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await Model.SafeAsync(Model.CancelAsync);
    private void HidePreview_Click(object sender, RoutedEventArgs e) => Model.HidePreview();
    private async void Prepare_Click(object sender, RoutedEventArgs e) => await Model.SafeAsync(() => Model.PrepareAsync(false));
    private async void PrepareAll_Click(object sender, RoutedEventArgs e) => await Model.SafeAsync(() => Model.PrepareAsync(true));
    private void Approve_Click(object sender, RoutedEventArgs e) => Model.Authorize(true);
    private void Deny_Click(object sender, RoutedEventArgs e) => Model.Authorize(false);
    private async void ClickMode_Click(object sender, RoutedEventArgs e) => await Model.SafeAsync(() => Model.ModeAsync(GenerationMode.Click));
    private async void AutoMode_Click(object sender, RoutedEventArgs e) => await Model.SafeAsync(() => Model.ModeAsync(GenerationMode.Auto));
    private async void Resume_Click(object sender, RoutedEventArgs e) => await Model.SafeAsync(Model.ResumeAsync);
}
