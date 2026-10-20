using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using StudyHarness.Application;
using StudyHarness.Contracts;

namespace StudyHarness.Desktop;

public sealed record LabPreviewModel(string Title, string Objectives, string Plan)
{
    public static LabPreviewModel From(PlannedNode node, bool eligible) => new(node.Title, string.Join("；", node.Objectives),
        $"目标：{string.Join("；", node.Objectives)}\n模式：{(node.Lab?.Mode == "continuous" ? "连续项目：请自行衔接前课代码" : "独立实验")}；建议前课：{node.Lab?.PreviousLabKey ?? "无"}\n" +
        (eligible ? "可自由生成和学习；前置关系仅为建议。" : "确认计划后可生成材料。"));
}
public sealed class LabViewModel(LabService service, Guid subject, string key) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private CancellationTokenSource? cancellation;
    private LabPackage? package;
    private bool eligible, busy, acknowledged, completed;
    private string title = "实验材料", plan = "", status = "正在读取课程。";
    private LabFile? selectedFile;
    public string Title => title;
    public string Plan => plan;
    public string Notice => package?.Practice is not null ? "模型生成的练习或项目材料。请自行核对推导、操作与观察结果；软件不会替你执行操作，也不把完成声明视为机器验证通过。" : LabMaterialRules.Notice;
    public string Status => status;
    public bool Busy => busy;
    public bool HasMaterial => package is not null && !busy;
    public bool CanGenerate => eligible && !busy && package is null;
    public bool CanAttest => HasMaterial && eligible && !completed;
    public bool CanConfirm => CanAttest && acknowledged;
    public bool Acknowledged { get => acknowledged; set { acknowledged = value; Notify(); Notify(nameof(CanConfirm)); } }
    public ObservableCollection<LabFile> Files { get; } = [];
    public LabFile? SelectedFile { get => selectedFile; set { selectedFile = value; Notify(); } }
    public string Instructions => package is null ? "点击生成后在此阅读材料。" : (package.Practice is null ? LabMaterialRules.AssessmentOverview(package.Material) + "\n\n" : "") + package.Material.Instructions + "\n\n测评方案\n" + package.Material.Assessment + "\n\n安全注意\n" + package.Material.SafetyNotes + "\n\n实际来源\n" + string.Join("\n", package.Sources.Select(s => s.Key + " · " + s.Reference.Locator.SourceLabel)) + "\n\n材料摘要：" + package.Hash;
    public string Cases => package is null ? "尚无用例。" : string.Join("\n\n", package.Material.Cases.Select(c => $"{c.Id}\n输入：\n{c.Input}\n预期（模型建议）：\n{c.Expected}\n实际结果：由你在外部自行检查，软件未执行。"));
    public async Task LoadAsync()
    {
        var draft = await service.Plans.GetAsync(subject); var node = draft?.Plan?.Candidate.Nodes.FirstOrDefault(n => n.Key == key);
        eligible = draft?.Confirmed == true && node?.Type == "lab";
        if (node is not null) { var view = LabPreviewModel.From(node, eligible); title = view.Title; plan = view.Plan; }
        package = await service.Store.GetLabAsync(subject, key);
        completed = (await service.Store.GetLabAttestationAsync(subject, key)) is not null;
        Files.Clear(); if (package is not null) foreach (var file in package.Material.SourceFiles.Concat(package.Material.TestFiles)) Files.Add(file);
        SelectedFile = Files.FirstOrDefault();
        var job = await service.Store.GetLabJobAsync(subject, key);
        status = completed ? "用户确认完成，软件未验证。完成记录已保存。" : package is not null ? "材料已就绪，未经编译或测试。可导出后自行测评。" : !eligible ? "请先确认有效实验计划。" : job is null ? "可以自由生成实验材料。" : $"生成状态：{job.Status}；派发{job.Attempts}/3；{job.Failure}";
        if (job?.Usage is { } usage) status += $" 实际已报告用量：{usage.TotalTokens} tokens。";
        NotifyAll();
    }
    public async Task GenerateAsync()
    {
        if (!CanGenerate) return; busy = true; cancellation = new(); status = "正在生成材料，可取消；不会执行代码。"; NotifyAll();
        using var notice = new CancellationTokenSource(); var delayed = DelayNotice(notice.Token);
        try
        {
            var token = cancellation.Token;
            var result = await Task.Run(() => service.GenerateAsync(subject, key, token), token);
            await LoadAsync(); if (!result.Ok) status = result.Error!.Message;
        }
        finally { notice.Cancel(); await delayed; busy = false; cancellation.Dispose(); cancellation = null; NotifyAll(); }
    }
    private async Task DelayNotice(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(300), ct); status = "材料生成比预期久，仍在处理；可取消。"; Notify(nameof(Status)); }
        catch (OperationCanceledException) { }
    }
    public void Cancel() => cancellation?.Cancel();
    public async Task ExportAsync(string path)
    {
        var result = await service.ExportAsync(subject, key, path); status = result.Ok ? "已导出：" + result.Value + "。请自行解压、检查和测评。" : result.Error!.Message; Notify(nameof(Status));
    }
    public async Task ConfirmAsync()
    {
        if (!CanConfirm || package is null) return;
        var result = await service.ConfirmAsync(subject, key, package.Hash, acknowledged);
        await LoadAsync(); if (!result.Ok) { status = result.Error!.Message; Notify(nameof(Status)); }
    }
    public void ReportFailure() { status = "操作未完成，请检查数据位置后重试；未声明测试通过。"; Notify(nameof(Status)); }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    private void NotifyAll() { foreach (var p in new[] { nameof(Title), nameof(Plan), nameof(Notice), nameof(Status), nameof(Busy), nameof(HasMaterial), nameof(CanGenerate), nameof(CanAttest), nameof(CanConfirm), nameof(Instructions), nameof(Cases) }) Notify(p); }
}
public partial class LabPreviewWindow : Window
{
    private readonly LabViewModel model;
    public LabPreviewWindow(LabViewModel model, TutoringSession? tutoring = null, ITutoringFiles? files = null, Guid subject = default, string node = "")
    {
        InitializeComponent();
        if (tutoring is not null && files is not null) { Tutor.Configure(tutoring, files, subject, node); Tutor.Visibility = Visibility.Visible; }
        this.model = model; DataContext = model;
        Loaded += async (_, _) => await Safe(model.LoadAsync); Closed += (_, _) => { model.Cancel(); Tutor.CloseSession(); };
    }
    private async Task Safe(Func<Task> action) { try { await action(); } catch (Exception) { model.ReportFailure(); } }
    private async void GenerateClick(object sender, RoutedEventArgs e) => await Safe(model.GenerateAsync);
    private void CancelClick(object sender, RoutedEventArgs e) => model.Cancel();
    private async void ConfirmClick(object sender, RoutedEventArgs e) => await Safe(model.ConfirmAsync);
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "实验材料 ZIP|*.zip", FileName = "lab-materials-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(this) == true) await Safe(() => model.ExportAsync(dialog.FileName));
    }
}
