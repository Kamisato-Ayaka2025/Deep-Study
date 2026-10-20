using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;
namespace StudyHarness.Tests.Runner;
// Every P1 Runner test is in this collection, and the collection is not parallelised: the
// tests share machine-wide resources (AppContainer profiles, named pipes, the machine's
// toolchains) and the cleanup checks count processes, which only means anything when no other
// test is starting one.
[CollectionDefinition("runner", DisableParallelization = true)]
public sealed class RunnerCollection;
// Compiling and running a normal program inside the container. This is the positive half of
// the P1 gate: a toolchain that reads headers and libraries outside the directory, and a
// program that writes inside it, must both work, or isolation would be indistinguishable from
// breakage.
[Collection("runner")]
public sealed class RunnerSmokeTests : IDisposable
{
    private readonly string dataRoot = RunnerTestEnvironment.EnsureDataRoot("smoke");
    public void Dispose() => RunnerTestEnvironment.DeleteDataRoot(dataRoot);
    [Theory]
    [InlineData("msvc")]
    [InlineData("mingw")]
    public async Task CompileAndRun_NormalProgram_SucceedsInsideTheContainer(string toolchain)
    {
        var host = new RunnerHost(RunnerTestEnvironment.RunnerPath, dataRoot);
        var opened = await host.OpenAsync();
        Assert.True(opened.Ok, opened.Error?.Code.ToString());
        await using var session = opened.Value!;
        // What the job holds before any run starts: the Runner, plus whatever the system
        // started for it. A run has left nothing behind when the count comes back to this.
        var baseline = session.ActiveProcessCount;
        var runId = Guid.NewGuid();
        var started = await session.StartAsync(RunnerStart.For(runId, toolchain, "hello", "smoke", RunnerPurpose.Run));
        Assert.True(started.Ok, started.Error?.Code.ToString());
        var replies = await ReadUntilFinished(session, runId);
        var compile = Assert.Single(replies, reply => reply.Kind == RunnerReplyKind.CompileResult);
        Assert.True(compile.Succeeded, $"compile failed with native {compile.NativeError}");
        var output = Assert.Single(replies, reply => reply.CaseId == "program-output");
        Assert.True(output.Succeeded, $"run failed with native {output.NativeError}");
        var finished = Assert.Single(replies, reply => reply.Kind == RunnerReplyKind.RunFinished);
        Assert.True(finished.Succeeded, finished.Error?.ToString());
        // The program's own stdout is the evidence the earlier phases could not produce: the
        // earlier defect where the child silently received the console instead of the host's
        // handles would leave this file empty while every native call still reported success.
        var work = Path.Combine(dataRoot, "runs", runId.ToString("N"), "work");
        Assert.Contains("study-harness-isolated-hello", await File.ReadAllTextAsync(Path.Combine(work, "run-stdout.txt")));
        Assert.True(File.Exists(Path.Combine(work, "program.exe")));
        // Back to the baseline: neither the compiler nor the program outlives the run.
        Assert.Equal(baseline, session.ActiveProcessCount);
    }
    // Read every reply up to and including RunFinished. A reply stream that stops without
    // RunFinished is reported as a failure, never treated as success.
    internal static async Task<List<RunnerReply>> ReadUntilFinished(IRunnerSession session, Guid runId)
    {
        var replies = new List<RunnerReply>();
        while (true)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var reply = await session.ReadAsync(deadline.Token);
            Assert.NotNull(reply);
            Assert.Equal(runId, reply!.RunId);
            replies.Add(reply);
            if (reply.Kind == RunnerReplyKind.RunFinished) return replies;
        }
    }
}
