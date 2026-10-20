using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
namespace StudyHarness.Desktop;
public sealed class ShellViewModel(SettingsService service, ModelEgress provider, SafeLog log, string? dataRoot = null) : INotifyPropertyChanged
{
    private string model = "deepseek-chat", status = "学习数据保存在本机。";
    private GenerationMode generationMode;
    private ConsentMode consentMode;
    private bool hasApiKey, busy, hasPendingApiKey;
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Model { get => model; set { model = value; Notify(); } }
    public GenerationMode GenerationMode { get => generationMode; set { generationMode = value; Notify(); } }
    public ConsentMode ConsentMode { get => consentMode; set { consentMode = value; Notify(); } }
    public bool HasApiKey { get => hasApiKey; private set { hasApiKey = value; Notify(); Notify(nameof(CredentialStatus)); } }
    public bool Busy { get => busy; private set { busy = value; Notify(); Notify(nameof(CanSave)); } }
    public bool CanSave => !Busy;
    public string CredentialStatus => hasPendingApiKey ? "新密钥尚未保存 · 请点击保存设置；输入不会立即替换已保存凭据" : HasApiKey ? "已保存加密密钥 · 自动使用，无需重复输入（有效性以真实连接结果为准）" : "尚未保存密钥";
    public void SetPendingApiKey(bool pending) { hasPendingApiKey = pending; Notify(nameof(CredentialStatus)); }
    public void DiscardPendingApiKey() { SetPendingApiKey(false); Status = "已放弃未保存输入；仍使用上次成功保存的设置。"; }
    public bool CanLeaveSettings()
    {
        if (!hasPendingApiKey) return true;
        Status = "新密钥尚未保存，已保留输入。请先保存设置，或明确点击“放弃未保存输入并重新载入”，再切页或退出。";
        return false;
    }
    public string DataLocation => dataRoot is null ? "" : $"当前数据目录：{dataRoot}";
    public string Status { get => status; private set { status = value; Notify(); } }
    public long Revision { get; private set; }
    public async Task LoadAsync()
    {
        try { Apply(await service.GetAsync()); }
        catch (Exception) { ShowFailure(ErrorCode.IO_FAILURE); }
    }
    public async Task<bool> SaveAsync(string? newKey)
    {
        if (Busy) return false;
        Busy = true;
        try
        {
            var request = new Command<SaveSettings>(Guid.NewGuid(), Revision,
                new(Model.Trim(), GenerationMode, ConsentMode, newKey));
            var result = await service.SaveAsync(request);
            if (result.Ok) { Apply(result.Value!); Status = "设置已保存；可检测连接或开始学习。"; }
            else Status = $"{result.Error!.Code}：{result.Error.Message}";
            WriteLog(request.RequestId, result.Error?.Code, result.Ok);
            return result.Ok;
        }
        catch (Exception) { ShowFailure(ErrorCode.IO_FAILURE); return false; }
        finally { Busy = false; }
    }
    public async Task ProbeAsync()
    {
        try
        {
            var result = await provider.ProbeAsync(CancellationToken.None);
            Status = result.Ok ? result.Value!.Text : $"{result.Error!.Code}：{result.Error.Message}";
        }
        catch (Exception) { ShowFailure(ErrorCode.IO_FAILURE); }
    }
    public async Task CheckConnectionAsync()
    {
        if (Busy || !CanLeaveSettings()) return;
        Busy = true; Status = "正在询问 DeepSeek：你是什么模型？";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var config = await service.GetAsync(timeout.Token);
            var review = provider.Prepare(config.Model, [new("connection-check", 0, ModelSourceKind.UserText, "你是什么模型？")], 512);
            var result = await provider.SendAsync(review, provider.Approve(review, ConsentMode.Each), timeout.Token);
            if (!result.Ok) { Status = $"{result.Error!.Code}：{result.Error.Message}"; return; }
            var reply = result.Value!;
            var inconsistentIdentity = reply.Text.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
                reply.Text.Contains("OpenAI", StringComparison.OrdinalIgnoreCase);
            Status = $"连接成功（接口已返回回复）\n请求地址：{review.Destination}\n请求模型：{review.Model}\n" +
                (inconsistentIdentity ? "注意：回复提及 ChatGPT/OpenAI，与本次请求的 DeepSeek 服务不一致。模型自述不能作为身份认证。\n" :
                    "模型自述仅供参考，不作为服务商或底层版本的身份认证。\n") +
                "\n模型原始回复：\n" + reply.Text +
                (reply.Usage is { } usage ? $"\n本次用量：{usage.TotalTokens} tokens。" : "\n服务未返回用量。");
        }
        catch (OperationCanceledException) { Status = "连接超时；请求可能已经计费，请稍后手动重试。"; }
        catch (Exception) { ShowFailure(ErrorCode.IO_FAILURE); }
        finally { Busy = false; }
    }
    private void Apply(SettingsDto value)
    {
        Model = value.Model; GenerationMode = value.GenerationMode; ConsentMode = value.ConsentMode;
        HasApiKey = value.HasApiKey; Revision = value.Revision;
    }
    private void ShowFailure(ErrorCode code)
    {
        Status = $"{code}：操作未完成，请检查本地数据后重试。";
        WriteLog(Guid.NewGuid(), code, false);
    }
    private void WriteLog(Guid id, ErrorCode? code, bool success)
    {
        try { log.Write(id, code, success); }
        catch (IOException) { Status += " 日志写入失败。"; }
        catch (UnauthorizedAccessException) { Status += " 日志目录不可写。"; }
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
