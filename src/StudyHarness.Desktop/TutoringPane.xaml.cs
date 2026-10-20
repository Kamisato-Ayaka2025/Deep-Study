using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StudyHarness.Application;

namespace StudyHarness.Desktop;

public partial class TutoringPane : UserControl
{
    private TutoringSession? session;
    private ITutoringFiles? files;
    private Guid subject;
    private string node = "";
    private bool busy;
    private int revision;
    public TutoringPane() { InitializeComponent(); }
    public void Configure(TutoringSession value, ITutoringFiles reader, Guid subjectId, string nodeKey)
    { session = value; files = reader; subject = subjectId; node = nodeKey; }
    public void CloseSession() { session?.Dispose(); Clear(); }
    private void Clear()
    {
        revision++; Dialogue.Clear(); Question.Clear(); Code.Clear(); Preview.Clear();
        Preview.Visibility = Visibility.Collapsed; SendButton.IsEnabled = false;
    }
    private void InputChanged(object sender, TextChangedEventArgs e)
    { revision++; if (SendButton is not null) SendButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(Question.Text); if (Preview is not null) { Preview.Clear(); Preview.Visibility = Visibility.Collapsed; } }
    private async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception) { Status.Text = "操作未完成，请检查课程、文本长度或文件权限后重试。请重新点击发送。"; }
    }
    public async Task PrepareAsync()
    {
        if (session is null || busy) return;
        if (Preview.Visibility == Visibility.Visible) { Preview.Clear(); Preview.Visibility = Visibility.Collapsed; return; }
        var current = revision;
        var review = await session.PrepareAsync(subject, node, Question.Text, Code.Text);
        if (current != revision || busy) return;
        Preview.Text = $"目标：{review.Destination}\n模型：{review.Model}\n本次固定外发内容：\n" + string.Join("\n\n", review.Sources.Select(s => s.Id + "\n" + s.Text));
        Preview.Visibility = Visibility.Visible;
        Status.Text = "再次点击可收起。无需查看详情也可直接发送；发送时使用当前输入。";
    }
    public async Task SendAsync()
    {
        if (session is null || busy || string.IsNullOrWhiteSpace(Question.Text)) return;
        var current = revision;
        busy = true; SendButton.IsEnabled = false; PrepareButton.IsEnabled = false;
        Question.IsReadOnly = Code.IsReadOnly = true;
        Status.Text = "正在答疑；取消不能撤回已发送数据或用量。";
        try
        {
            await session.PrepareAsync(subject, node, Question.Text, Code.Text);
            if (current != revision) return;
            var result = await session.SendAsync(true);
            if (current != revision) return;
            Dialogue.ShowMessages(session.Messages);
            Status.Text = result.Ok ? "已回复，仅供学习参考；未修改文件或学习进度。" : result.Error!.Message;
            if (session.LastUsage is { } usage) Status.Text += $" 本次已报告用量：{usage.TotalTokens} tokens。";
            if (result.Ok) Question.Clear();
        }
        finally { busy = false; SendButton.IsEnabled = !string.IsNullOrWhiteSpace(Question.Text); PrepareButton.IsEnabled = true; Question.IsReadOnly = Code.IsReadOnly = false; Preview.Clear(); Preview.Visibility = Visibility.Collapsed; }
    }
    private async void PrepareClick(object sender, RoutedEventArgs e) => await Safe(PrepareAsync);
    private async void SendClick(object sender, RoutedEventArgs e) => await Safe(SendAsync);
    private void ResetClick(object sender, RoutedEventArgs e) { session?.Reset(); Clear(); Status.Text = "临时会话已清空。"; }
    private void ClearCode(object sender, RoutedEventArgs e) { if (!busy) Code.Clear(); }
    private void SelectCode(object sender, RoutedEventArgs e)
    {
        if (busy || files is null) return;
        var dialog = new OpenFileDialog { Filter = "代码和文本|*.c;*.h;*.cpp;*.hpp;*.md;*.txt", Multiselect = false };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { Code.Text = files.ReadSelected(HostSelection.FromFilePicker(dialog.FileName)); Status.Text = "仅已选文件内容已读入；点击发送时将附带这些代码。"; }
        catch (Exception) { Status.Text = "无法读取：仅支持16000字符以内UTF-8文本，拒绝链接、别名和二进制。"; }
    }
}
