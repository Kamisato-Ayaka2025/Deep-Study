using System.Diagnostics;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;
namespace StudyHarness.Tests.Runner;
// Stopping and cleanup. A program that never terminates must stay running past the
// observation window, and stopping it must end its whole tree -- the program and the
// descendant it spawned -- rather than only the process that was started. The window is an
// observation window, not the product's timeout policy.
//
// Every check here disposes its session even when an assertion fails. A session left open by a
// failed test keeps a Runner alive, and a live Runner keeps the AppContainer profile alive
// with it, which makes every later check in the process fail for a reason that has nothing to
// do with what it was testing.
[Collection("runner")]
public sealed class RunnerStopTests : IDisposable
{
    private readonly string dataRoot = RunnerTestEnvironment.EnsureDataRoot("stop");
    public void Dispose() => RunnerTestEnvironment.DeleteDataRoot(dataRoot);
    [Fact]
    public async Task Stop_EndsTheWholeTreeAndLeavesNoProcessBehind()
    {
        var host = new RunnerHost(RunnerTestEnvironment.RunnerPath, dataRoot);
        var opened = await host.OpenAsync();
        Assert.True(opened.Ok, opened.Error?.Code.ToString());
        await using var session = opened.Value!;
        var baseline = session.ActiveProcessCount;
        var runId = Guid.NewGuid();
        // The loop bundle writes a marker and spawns a descendant before it starts looping,
        // so a stop that misses descendants is observable rather than assumed.
        var started = await session.StartAsync(RunnerStart.For(runId, "msvc", "loop", "stop", RunnerPurpose.Run));
        Assert.True(started.Ok, started.Error?.Code.ToString());
        var compile = await session.ReadAsync(TestTimeout());
        Assert.NotNull(compile);
        Assert.Equal(RunnerReplyKind.CompileResult, compile!.Kind);
        Assert.True(compile.Succeeded, $"compile failed with native {compile.NativeError}");
        var work = Path.Combine(dataRoot, "runs", runId.ToString("N"), "work");
        // Still running past the window: the program did not stop on its own and the Runner
        // says so as a case result rather than by finishing the run.
        var window = await session.ReadAsync(TestTimeout());
        Assert.NotNull(window);
        Assert.Equal("observation-window-exceeded", window!.CaseId);
        Assert.True(window.Succeeded, "observation window behaviour is part of the gate");
        Assert.True(File.Exists(Path.Combine(work, "loop-started.txt")), "the program never started");
        // Runner, program and the program's own descendant are all alive and all owned by the
        // session's job. Fewer than two would mean the descendant was never created.
        var beforeStop = session.ActiveProcessCount;
        Assert.True(beforeStop >= 2, $"expected the program tree to be alive, saw {beforeStop} processes");
        var stopped = await session.StopAsync(runId);
        Assert.True(stopped.Ok, stopped.Error?.Code.ToString());
        Assert.Equal(RunnerReplyKind.RunFinished, stopped.Value!.Kind);
        Assert.Equal(ErrorCode.RUN_CANCELLED, stopped.Value.Error);
        // Back to the baseline: the compiler, the program and the descendant are gone, which is
        // the whole-tree guarantee and not a single-process kill.
        await WaitForCount(session, baseline, TimeSpan.FromSeconds(30));
        Assert.Equal(baseline, session.ActiveProcessCount);
        Assert.Empty(Process.GetProcessesByName("program"));
    }
    // Closing the channel is the polite half of a host going away: the Runner sees
    // end-of-stream, destroys its session and exits. It must leave nothing running even
    // without a StopRun first.
    [Fact]
    public async Task Disconnect_EndsTheWholeTreeWithoutAStopRequest()
    {
        var host = new RunnerHost(RunnerTestEnvironment.RunnerPath, dataRoot);
        var opened = await host.OpenAsync();
        Assert.True(opened.Ok, opened.Error?.Code.ToString());
        var session = opened.Value!;
        try
        {
            var runId = Guid.NewGuid();
            var started = await session.StartAsync(RunnerStart.For(runId, "msvc", "loop", "stop", RunnerPurpose.Run));
            Assert.True(started.Ok, started.Error?.Code.ToString());
            var work = Path.Combine(dataRoot, "runs", runId.ToString("N"), "work");
            Assert.True(await WaitForFile(Path.Combine(work, "loop-started.txt"), TimeSpan.FromMinutes(3)),
                "the program never started");
            await session.DisposeAsync();
            Assert.True(await WaitForNoProcess("program", TimeSpan.FromSeconds(30)), "the program outlived the channel");
            Assert.True(await WaitForNoProcess("StudyHarness.Runner", TimeSpan.FromSeconds(30)), "the Runner outlived the channel");
        }
        finally
        {
            await session.DisposeAsync();
        }
    }
    // The impolite half of the same event: the host process dies where the operating system
    // cannot be asked to unwind it, so nothing closes the control channel politely and nothing
    // calls Stop. The host's job handle is closed by the kernel as part of the process dying,
    // and every process in the job -- the Runner, the toolchain, the program and the program's
    // own descendant -- is destroyed at that moment.
    //
    // The host is a separate process because a process cannot observe its own abnormal death.
    // It is killed with entireProcessTree: false, so only the host dies: anything that survives
    // it did so because the job did not hold it.
    [Fact]
    public async Task AbnormalHostExit_LeavesNeitherRunnerNorProgramBehind()
    {
        Assert.True(File.Exists(RunnerTestEnvironment.HostProbePath), $"host probe not built: {RunnerTestEnvironment.HostProbePath}");
        var probeRoot = RunnerTestEnvironment.EnsureDataRoot("abnormal");
        var marker = Path.Combine(probeRoot, "started.txt");
        try
        {
            using var probe = Process.Start(new ProcessStartInfo(RunnerTestEnvironment.HostProbePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "--runner", RunnerTestEnvironment.RunnerPath,
                    "--data-root", Path.Combine(probeRoot, "data"),
                    "--marker", marker,
                    "--toolchain", "msvc"
                }
            })!;
            Assert.True(await WaitForFile(marker, TimeSpan.FromMinutes(3)),
                $"the host probe never reported a running program (exit {SafeExitCode(probe)})");
            // The program and its descendant are running under a live host. Kill the host the
            // way a crash does: no unwinding, no disposal, no chance to stop anything.
            probe.Kill(false);
            Assert.True(probe.WaitForExit(30000), "the host probe did not die");
            Assert.True(await WaitForNoProcess("program", TimeSpan.FromSeconds(30)),
                "the program survived the death of the host that owned its job");
            Assert.True(await WaitForNoProcess("StudyHarness.Runner", TimeSpan.FromSeconds(30)),
                "the Runner survived the death of the host that owned its job");
        }
        finally { RunnerTestEnvironment.DeleteDataRoot(probeRoot); }
    }
    private static int SafeExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : -1; }
        catch (InvalidOperationException) { return -1; }
    }
    private static async Task<bool> WaitForFile(string path, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path)) return true;
            await Task.Delay(250);
        }
        return File.Exists(path);
    }
    private static async Task<bool> WaitForNoProcess(string name, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName(name).Length == 0) return true;
            await Task.Delay(250);
        }
        return Process.GetProcessesByName(name).Length == 0;
    }
    // Terminating a job is not instantaneous, so "the tree is gone" is waited for rather than
    // sampled once. The wait is bounded and the caller still asserts the final count.
    private static async Task WaitForCount(IRunnerSession session, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && session.ActiveProcessCount != expected) await Task.Delay(250);
    }
    private static CancellationToken TestTimeout() => new CancellationTokenSource(TimeSpan.FromMinutes(5)).Token;
}
