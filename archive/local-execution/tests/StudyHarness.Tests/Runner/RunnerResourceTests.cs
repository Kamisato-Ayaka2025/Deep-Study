using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;
using Xunit.Abstractions;
using System.Text.RegularExpressions;

namespace StudyHarness.Tests.Runner;

[Collection("runner")]
public sealed class RunnerResourceTests(ITestOutputHelper output) : IDisposable
{
    private readonly string dataRoot = RunnerTestEnvironment.EnsureDataRoot("resource-limits");
    public void Dispose() => RunnerTestEnvironment.DeleteDataRoot(dataRoot);
    [Theory]
    [InlineData("msvc")]
    [InlineData("mingw")]
    public async Task MemoryAndProcessLimits_RejectExcessAndReclaimChildren(string toolchain)
    {
        var host = new RunnerHost(RunnerTestEnvironment.RunnerPath, dataRoot);
        var opened = await host.OpenAsync();
        Assert.True(opened.Ok, opened.Error?.Code.ToString());
        await using var session = opened.Value!;
        var baseline = session.ActiveProcessCount;
        var runId = Guid.NewGuid();
        var start = await session.StartAsync(RunnerStart.For(runId, toolchain, "resource-limits", "smoke", RunnerPurpose.Run));
        Assert.True(start.Ok, start.Error?.Code.ToString());
        var replies = await RunnerSmokeTests.ReadUntilFinished(session, runId);
        Assert.True(Assert.Single(replies, reply => reply.Kind == RunnerReplyKind.CompileResult).Succeeded);
        var report = await File.ReadAllTextAsync(Path.Combine(dataRoot, "runs", runId.ToString("N"), "work", "run-stdout.txt"));
        output.WriteLine(report);
        Assert.Contains("memoryError=1455 aggregate0=0 aggregate1=0 aggregate2=1455 spawned=15 spawnError=1816 cpuFlags=5 cpuRate=2500", report);
        Assert.True(Assert.Single(replies, reply => reply.Kind == RunnerReplyKind.RunFinished).Succeeded);
        Assert.Equal(baseline, session.ActiveProcessCount);
    }
    [Theory]
    [InlineData("msvc")]
    [InlineData("mingw")]
    public async Task CpuHardCap_BoundsMeasuredBusyWork(string toolchain)
    {
        var host = new RunnerHost(RunnerTestEnvironment.RunnerPath, dataRoot);
        var opened = await host.OpenAsync();
        Assert.True(opened.Ok, opened.Error?.Code.ToString());
        await using var session = opened.Value!;
        var baseline = session.ActiveProcessCount;
        var runId = Guid.NewGuid();
        var start = await session.StartAsync(RunnerStart.For(runId, toolchain, "cpu-limit", "smoke", RunnerPurpose.Run));
        Assert.True(start.Ok, start.Error?.Code.ToString());
        var replies = await RunnerSmokeTests.ReadUntilFinished(session, runId);
        Assert.True(Assert.Single(replies, reply => reply.Kind == RunnerReplyKind.CompileResult).Succeeded);
        var report = await File.ReadAllTextAsync(Path.Combine(dataRoot, "runs", runId.ToString("N"), "work", "run-stdout.txt"));
        output.WriteLine(report);
        var match = Regex.Match(report, @"cpuUsagePermille=(\d+) elapsedMs=(\d+) workers=(\d+) processors=(\d+)");
        Assert.True(match.Success, report);
        Assert.InRange(int.Parse(match.Groups[1].Value), 100, 350);
        Assert.InRange(long.Parse(match.Groups[2].Value), 8000, 19000);
        Assert.Equal(match.Groups[3].Value, match.Groups[4].Value);
        Assert.True(Assert.Single(replies, reply => reply.Kind == RunnerReplyKind.RunFinished).Succeeded);
        Assert.Equal(baseline, session.ActiveProcessCount);
    }
}
