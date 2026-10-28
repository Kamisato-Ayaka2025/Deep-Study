using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;

namespace StudyHarness.Desktop;

internal static class P27QueueCheck
{
    private sealed class Knowledge : IKnowledgeStore
    {
        public readonly List<string> Started = [];
        public bool Fail;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly LibraryDto library = new(Guid.NewGuid(), "synthetic", 0);
        public Task<LibraryDto?> CurrentAsync(CancellationToken ct = default) => Task.FromResult<LibraryDto?>(library);
        public async Task<Result<ImportReport>> ImportAsync(Command<HostSelection> c, Guid l, CancellationToken ct = default)
        {
            Started.Add(System.IO.Path.GetFileName(c.Payload.Path)); Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            if (Fail) return Result<ImportReport>.Fail(ErrorCode.IO_FAILURE, "synthetic failure");
            return Result<ImportReport>.Success(new(Guid.NewGuid(), [new(Started[^1], true, 1, 0, 0, null)], References: []));
        }
        public Task<KnowledgeSearch> SearchAsync(Guid l, string q, IReadOnlyCollection<Guid> p, int limit = 20, CancellationToken ct = default) => Task.FromResult(new KnowledgeSearch([], false, ""));
        public Task<Result<LibraryDto>> SwitchAsync(Command<HostSelection> c, CancellationToken ct = default) => throw new InvalidOperationException("Cannot switch queued library");
        public Task<ResolvedSource> ResolveAsync(SourceRef r, CancellationToken ct = default) => throw new NotSupportedException();
    }
    internal static async Task Run(string root, Action<bool,string> check)
    {
        var k = new Knowledge(); var settings = new SettingsService(new SqliteSettingsStore(root));
        var vm = new WorkspaceViewModel(new(k, settings, new(new FakeProvider())));
        await vm.LoadAsync();
        var a = HostSelection.FromPlanFilePicker(System.IO.Path.Combine(root,"a.pptx"));
        var b = HostSelection.FromPlanFilePicker(System.IO.Path.Combine(root,"b.pptx"));
        var c = HostSelection.FromPlanFilePicker(System.IO.Path.Combine(root,"c.pptx"));
        var first = vm.ImportAsync(a); await k.Entered.Task;
        await vm.ImportManyAsync([b,c,a]);
        check(vm.Busy && vm.CanImport && vm.ImportQueueCount == 3 && k.Started.Count == 1, "queue-accepts-during-active-and-deduplicates");
        vm.Cancel(); await first;
        check(!vm.Busy && vm.CanResumeImports && vm.ImportQueueCount == 3, "queue-cancel-retains-active-and-waiting");
        await vm.SwitchAsync(HostSelection.FromFolderPicker(root));
        check(vm.LibraryPath == "synthetic", "queued-library-switch-rejected");
        k.Release.SetResult(); await vm.ResumeImportsAsync();
        check(k.Started.SequenceEqual(new[]{"a.pptx","a.pptx","b.pptx","c.pptx"}) && vm.ImportQueueCount == 0, "queue-resume-fifo-and-drains");
        var failed = new Knowledge { Fail = true }; failed.Release.SetResult();
        var retry = new WorkspaceViewModel(new(failed, settings, new(new FakeProvider()))); await retry.LoadAsync();
        await retry.ImportManyAsync([a,b]);
        check(retry.CanResumeImports && retry.ImportQueueCount == 2 && failed.Started.Count == 1, "queue-failure-stops-before-next-file");
        failed.Fail = false; await retry.ResumeImportsAsync();
        check(retry.ImportQueueCount == 0 && failed.Started.SequenceEqual(new[]{"a.pptx","a.pptx","b.pptx"}), "queue-failure-resumes-in-order");

    }
}
