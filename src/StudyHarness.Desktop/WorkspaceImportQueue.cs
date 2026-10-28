using System.IO;
using StudyHarness.Application;

namespace StudyHarness.Desktop;

public sealed partial class WorkspaceViewModel
{
    private readonly Queue<HostSelection> importQueue = new();
    private bool processingImports, importsPaused;
    public bool CanImport => library is not null;
    public bool CanResumeImports => !Busy && importQueue.Count > 0;
    public int ImportQueueCount => importQueue.Count;
    public string ImportQueueText => importQueue.Count == 0 ? "" :
        (importsPaused ? "导入队列已暂停" : "按顺序处理，可继续添加") + "（本次运行）\n" +
        string.Join("\n", importQueue.Select((s, i) => $"{i + 1}. {Path.GetFileName(s.Path)}"));
    private void QueueChanged()
    {
        Notify(nameof(ImportQueueText)); Notify(nameof(ImportQueueCount));
        Notify(nameof(CanResumeImports)); Notify(nameof(CanImport));
    }
    public Task ImportAsync(HostSelection selection) => ImportManyAsync([selection]);
    public Task ImportManyAsync(IEnumerable<HostSelection> selections)
    {
        if (library is null) { Status = "请先选择资料库。"; return Task.CompletedTask; }
        var requested = selections.ToArray();
        var resumesCurrent = importQueue.TryPeek(out var head) && requested.Any(s => string.Equals(s.Path, head.Path, StringComparison.OrdinalIgnoreCase));
        foreach (var selection in requested)
            if (!importQueue.Any(s => string.Equals(s.Path, selection.Path, StringComparison.OrdinalIgnoreCase))) importQueue.Enqueue(selection);
        QueueChanged();
        return Busy || importsPaused && !resumesCurrent ? Task.CompletedTask : ResumeImportsAsync();
    }
    public async Task ResumeImportsAsync()
    {
        if (Busy || processingImports || importQueue.Count == 0) return;
        processingImports = true; importsPaused = false; QueueChanged();
        try
        {
            await Run(async ct =>
            {
                while (importQueue.TryPeek(out var selection))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!await ImportCoreAsync(selection, ct)) break;
                    importQueue.Dequeue(); QueueChanged();
                }
            });
        }
        finally
        {
            processingImports = false; importsPaused = importQueue.Count > 0; QueueChanged();
        }
    }
    private void RemoveQueuedMaterial(string label)
    {
        var keep = importQueue.Where(s => !string.Equals(Path.GetFileName(s.Path), label, StringComparison.OrdinalIgnoreCase)).ToArray();
        importQueue.Clear(); foreach (var item in keep) importQueue.Enqueue(item);
        if (importQueue.Count == 0) importsPaused = false;
        QueueChanged();
    }
    public void ClearImportQueue()
    {
        if (Busy) return;
        importQueue.Clear(); importsPaused = false; QueueChanged();
        Status = "已清空待处理队列；原文件和已保存扫描结果保留。";
    }
}
