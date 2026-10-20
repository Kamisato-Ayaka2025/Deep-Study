using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using StudyHarness.Runner;
using Xunit;

namespace StudyHarness.Tests.Runner;

[Collection("runner")]
public sealed class RunnerOutputTests : IDisposable
{
    private readonly string dataRoot = RunnerTestEnvironment.EnsureDataRoot("output-limit");
    public void Dispose() => RunnerTestEnvironment.DeleteDataRoot(dataRoot);

    [Theory]
    [InlineData("msvc", "output-flood")]
    [InlineData("mingw", "output-flood")]
    [InlineData("msvc", "output-flood-tree")]
    [InlineData("mingw", "output-flood-tree")]
    public async Task CombinedOutputFlood_FailsDespiteSuccessMarker_AndReclaimsJob(string toolchain, string bundle)
    {
        var host = new RunnerHost(RunnerTestEnvironment.RunnerPath, dataRoot);
        var opened = await host.OpenAsync();
        Assert.True(opened.Ok, opened.Error?.Code.ToString());
        await using var session = opened.Value!;
        var baseline = session.ActiveProcessCount;
        var runId = Guid.NewGuid();
        var started = await session.StartAsync(RunnerStart.For(runId, toolchain, bundle, "smoke", RunnerPurpose.Run));
        Assert.True(started.Ok, started.Error?.Code.ToString());
        var replies = await RunnerSmokeTests.ReadUntilFinished(session, runId);
        Assert.True(Assert.Single(replies, reply => reply.Kind == RunnerReplyKind.CompileResult).Succeeded);
        Assert.False(Assert.Single(replies, reply => reply.Kind == RunnerReplyKind.RunFinished).Succeeded);
        var work = Path.Combine(dataRoot, "runs", runId.ToString("N"), "work");
        var stdout = Path.Combine(work, "run-stdout.txt");
        var stderr = Path.Combine(work, "run-stderr.txt");
        Assert.Contains("study-harness-isolated-hello", await File.ReadAllTextAsync(stdout));
        Assert.Equal(BoundedOutput.ByteLimit, new FileInfo(stdout).Length + new FileInfo(stderr).Length);
        Assert.Equal(baseline, session.ActiveProcessCount);
    }
}
