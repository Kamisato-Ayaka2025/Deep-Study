using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
using Xunit;
using Xunit.Abstractions;
namespace StudyHarness.Tests.Runner;
// The negative half of the P1 gate. The escape fixture is compiled and run inside the same
// container as a normal program, and every boundary it attacks must hold: reads and writes
// outside the dedicated directory, a host-created link out of it, a path-traversal form of
// the same, the host's own control channel, IPv4 and IPv6 TCP, UDP, DNS, and a child process
// that tries the same things. Each negative is paired with a control that keeps it honest --
// the target is reachable and the file is readable from the host, the container can write
// inside its own directory, its redirected temporary directory is inside that directory, it
// can still use a socket of its own -- so a container that simply broke, or that blocked every
// socket it has, cannot pass by doing nothing. Cases the Runner reports are asserted
// individually, so a mechanism that keeps file isolation but loses network isolation cannot
// pass as a whole.
[Collection("runner")]
public sealed class RunnerIsolationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string dataRoot = RunnerTestEnvironment.EnsureDataRoot("isolation");
    public void Dispose() => RunnerTestEnvironment.DeleteDataRoot(dataRoot);
    [Theory]
    [InlineData("msvc")]
    [InlineData("mingw")]
    public async Task EscapeFixture_IsDeniedEveryBoundary(string toolchain)
    {
        var host = new RunnerHost(RunnerTestEnvironment.RunnerPath, dataRoot);
        var opened = await host.OpenAsync();
        Assert.True(opened.Ok, opened.Error?.Code.ToString());
        await using var session = opened.Value!;
        var baseline = session.ActiveProcessCount;
        var runId = Guid.NewGuid();
        var started = await session.StartAsync(RunnerStart.For(runId, toolchain, "escape", "isolation", RunnerPurpose.Run));
        Assert.True(started.Ok, started.Error?.Code.ToString());
        var replies = await RunnerSmokeTests.ReadUntilFinished(session, runId);
        var compile = Assert.Single(replies, reply => reply.Kind == RunnerReplyKind.CompileResult);
        Assert.True(compile.Succeeded, $"compile failed with native {compile.NativeError}");
        var cases = replies.Where(reply => reply.Kind == RunnerReplyKind.CaseResult).ToList();
        // Every refused attempt must have been refused with a real native refusal rather than
        // with a zero, and the positive controls must have genuinely succeeded: a container
        // that broke the toolchain, that blocked every socket it owns, or that was aimed at
        // targets nobody was listening on would otherwise look like a container that blocks
        // everything. Ambient package grants do not exempt unrelated files from REQ-034.
        foreach (var caseId in new[]
                 {
                     "isAppContainer", "insideWriteError", "headerReadError", "containerLoopbackError",
                     "tempWithinWork", "tempWriteError", "tempFileWithinWork", "childSpawnError", "childIsAppContainer",
                     "host-target-reachable", "host-dns-resolves", "unrelated-file-readable-on-host",
                     "unrelated-file-read-denied", "outside-write-denied", "traversal-escape-denied", "junction-escape-denied",
                     "runner-pipe-denied", "decoy-pipe-denied", "tcp-egress-denied",
                     "udp-egress-denied", "dns-egress-denied", "child-egress-denied", "listeners-untouched",
                     "ambient-aap-read-denied", "ambient-arap-read-denied", "ambient-files-readable-on-host",
                     "original-profile-write-denied", "local-name-resolution-measured"
                 })
            Assert.True(cases.Any(candidate => candidate.CaseId == caseId), $"missing case {caseId}");
        // The raw numbers are printed, not merely asserted on: a reviewer re-checks the policy
        // against this machine's own refusals instead of taking a boolean on trust.
        foreach (var candidate in cases) output.WriteLine($"{candidate.CaseId}: native={candidate.NativeError} succeeded={candidate.Succeeded}");
        var report = Path.Combine(dataRoot, "runs", runId.ToString("N"), "work", "escape.json");
        if (File.Exists(report)) output.WriteLine("escape.json: " + await File.ReadAllTextAsync(report));
        var finished = Assert.Single(replies, reply => reply.Kind == RunnerReplyKind.RunFinished);
        Assert.Equal(cases.All(candidate => candidate.Succeeded), finished.Succeeded);
        foreach (var candidate in cases) Assert.True(candidate.Succeeded, $"case {candidate.CaseId} failed with native {candidate.NativeError}");
        Assert.True(finished.Succeeded, finished.Error?.ToString());
        // The sentinel outside the run directory must be untouched: a denied write that had
        // actually landed would leave a file behind even if the call reported failure.
        Assert.False(File.Exists(Path.Combine(dataRoot, "runs", runId.ToString("N"), "outside", "escape-write.txt")));
        Assert.False(File.Exists(Path.Combine(dataRoot, "runs", runId.ToString("N"), "outside", "traversal-write.txt")));
        Assert.True(File.Exists(Path.Combine(dataRoot, "runs", runId.ToString("N"), "work", "allowed.txt")));
        // Back to the baseline: the fixture and everything it spawned are gone.
        Assert.Equal(baseline, session.ActiveProcessCount);
    }
}
