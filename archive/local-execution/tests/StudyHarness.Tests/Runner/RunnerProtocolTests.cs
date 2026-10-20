using StudyHarness.Contracts;
using Xunit;
namespace StudyHarness.Tests.Runner;
// The host and the Runner authenticate each other with a per-session secret, and the Runner
// accepts only registered resource identifiers. These tests speak the wire protocol by hand,
// as an incorrect or hostile host would, because a client built on the production types could
// not present a bad secret, a stale protocol version or an unregistered identifier -- and each
// of those must be unable to produce a pass.
[Collection("runner")]
public sealed class RunnerProtocolTests : IDisposable
{
    private const string Secret = "the-real-session-secret";
    private readonly string dataRoot = RunnerTestEnvironment.EnsureDataRoot("protocol");
    public void Dispose() => RunnerTestEnvironment.DeleteDataRoot(dataRoot);
    [Fact]
    public void WrongHandshakeSecret_IsRefusedWithAuthInvalid()
    {
        using var host = new RawRunnerHost(NewPipeName(), Secret, dataRoot);
        var runId = Guid.NewGuid();
        var reply = host.Reply(Frame(runId, RunnerRequestKind.StartRun, "not-the-secret", RawRunnerHost.Start("msvc", "hello", "smoke")));
        Assert.Equal(RunnerReplyKind.RunFinished, reply.Kind);
        Assert.Equal(ErrorCode.AUTH_INVALID, reply.Error);
        Assert.False(reply.Succeeded);
        Assert.Equal(runId, reply.RunId);
    }
    [Fact]
    public void UnknownProtocolVersion_IsRefusedRatherThanGuessed()
    {
        using var host = new RawRunnerHost(NewPipeName(), Secret, dataRoot);
        var reply = host.Reply(Frame(Guid.NewGuid(), RunnerRequestKind.StartRun, Secret,
            RawRunnerHost.Start("msvc", "hello", "smoke"), protocolVersion: RunnerProtocol.Version + 1));
        Assert.Equal(ErrorCode.VALIDATION_ERROR, reply.Error);
        Assert.False(reply.Succeeded);
    }
    [Fact]
    public void UnregisteredResourceIdentifiers_AreRefused()
    {
        using var host = new RawRunnerHost(NewPipeName(), Secret, dataRoot);
        // A path, a command line and an environment variable assignment are refused for the
        // same reason: none of them is a registered identifier, so nothing downstream -- no
        // process launch, no filesystem lookup -- ever sees them.
        string[] refused =
        [
            Frame(Guid.NewGuid(), RunnerRequestKind.StartRun, Secret, RawRunnerHost.Start("msvc", @"C:\Windows\System32\cmd.exe", "smoke")),
            Frame(Guid.NewGuid(), RunnerRequestKind.StartRun, Secret, RawRunnerHost.Start("cmd.exe /c whoami", "hello", "smoke")),
            Frame(Guid.NewGuid(), RunnerRequestKind.StartRun, Secret, RawRunnerHost.Start("PATH=.", "hello", "smoke")),
            Frame(Guid.NewGuid(), RunnerRequestKind.StartRun, Secret, RawRunnerHost.Start("msvc", "hello", "smoke", (RunnerPurpose)9))
        ];
        foreach (var frame in refused)
        {
            var reply = host.Reply(frame);
            Assert.Equal(ErrorCode.VALIDATION_ERROR, reply.Error);
            Assert.False(reply.Succeeded);
        }
    }
    [Fact]
    public void SecondRunWhileOneIsActive_IsRefused()
    {
        using var host = new RawRunnerHost(NewPipeName(), Secret, dataRoot);
        var first = host.Reply(Frame(Guid.NewGuid(), RunnerRequestKind.StartRun, Secret, RawRunnerHost.Start("msvc", "loop", "stop")));
        Assert.Equal(RunnerReplyKind.Accepted, first.Kind);
        host.Write(Frame(Guid.NewGuid(), RunnerRequestKind.StartRun, Secret, RawRunnerHost.Start("msvc", "hello", "smoke")));
        // The active run's own frames interleave with the refusal, so the refusal is found by
        // its content rather than by arriving next.
        var refusal = ReadUntilFinished(host);
        Assert.Equal(ErrorCode.TASK_ACTIVE, refusal.Error);
        Assert.False(refusal.Succeeded);
    }
    [Fact]
    public void StopWithoutAnActiveRun_IsRefused()
    {
        using var host = new RawRunnerHost(NewPipeName(), Secret, dataRoot);
        var reply = host.Reply(Frame(Guid.NewGuid(), RunnerRequestKind.StopRun, Secret));
        Assert.Equal(RunnerReplyKind.RunFinished, reply.Kind);
        Assert.Equal(ErrorCode.RUN_CANCELLED, reply.Error);
        Assert.False(reply.Succeeded);
    }
    [Fact]
    public void Replies_CarryTheRunIdAndAnIncreasingSequence()
    {
        using var host = new RawRunnerHost(NewPipeName(), Secret, dataRoot);
        var runId = Guid.NewGuid();
        var accepted = host.Reply(Frame(runId, RunnerRequestKind.StartRun, Secret, RawRunnerHost.Start("msvc", "hello", "smoke")));
        Assert.Equal(RunnerReplyKind.Accepted, accepted.Kind);
        Assert.Equal(runId, accepted.RunId);
        var previous = accepted.Sequence;
        var kinds = new List<RunnerReplyKind>();
        while (true)
        {
            var reply = host.Next();
            Assert.Equal(runId, reply.RunId);
            Assert.True(reply.Sequence > previous, $"sequence went backwards at {reply.Sequence}");
            previous = reply.Sequence;
            kinds.Add(reply.Kind);
            if (reply.Kind == RunnerReplyKind.RunFinished) break;
            Assert.True(kinds.Count < 100, "the run never finished");
        }
        // Accepted, the compile result, the case result and the terminal finish, in that order.
        var expected = new[] { RunnerReplyKind.Accepted, RunnerReplyKind.CompileResult, RunnerReplyKind.CaseResult, RunnerReplyKind.RunFinished };
        Assert.Equal(expected, kinds.Prepend(accepted.Kind));
    }
    private static RunnerReply ReadUntilFinished(RawRunnerHost host)
    {
        while (true)
        {
            var reply = host.Next();
            if (reply.Kind == RunnerReplyKind.RunFinished) return reply;
        }
    }
    [Fact]
    public void ExistingRunDirectory_IsRefusedBeforeHostWrites()
    {
        var runId = Guid.NewGuid();
        var outside = Path.Combine(dataRoot, "runs", runId.ToString("N"), "outside");
        Directory.CreateDirectory(outside);
        var sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "must-not-be-overwritten");
        using var host = new RawRunnerHost(NewPipeName(), Secret, dataRoot);
        var reply = host.Reply(Frame(runId, RunnerRequestKind.StartRun, Secret, RawRunnerHost.Start("msvc", "hello", "smoke")));
        Assert.Equal(RunnerReplyKind.RunFinished, reply.Kind);
        Assert.Equal(ErrorCode.IO_FAILURE, reply.Error);
        Assert.False(reply.Succeeded);
        Assert.Equal("must-not-be-overwritten", File.ReadAllText(sentinel));
        Assert.False(Directory.Exists(Path.Combine(dataRoot, "runs", runId.ToString("N"), "work")));
    }
    [Fact]
    public void UnknownKindAndConflictingPayload_AreRefused()
    {
        using var host = new RawRunnerHost(NewPipeName(), Secret, dataRoot);
        var start = RawRunnerHost.Start("msvc", "hello", "smoke");
        Assert.Equal(ErrorCode.VALIDATION_ERROR,
            host.Reply(Frame(Guid.NewGuid(), (RunnerRequestKind)9, Secret, start)).Error);
        Assert.Equal(ErrorCode.VALIDATION_ERROR,
            host.Reply(Frame(Guid.NewGuid(), RunnerRequestKind.StopRun, Secret, start)).Error);
    }
    [Fact]
    public void CompileOnly_DoesNotExecuteLoop_AndSessionAcceptsNextRun()
    {
        using var host = new RawRunnerHost(NewPipeName(), Secret, dataRoot);
        for (var index = 0; index < 2; index++)
        {
            var run = Guid.NewGuid();
            Assert.Equal(RunnerReplyKind.Accepted, host.Reply(Frame(run, RunnerRequestKind.StartRun, Secret,
                RawRunnerHost.Start("msvc", "loop", "stop", RunnerPurpose.Compile))).Kind);
            var finished = ReadUntilFinished(host);
            Assert.True(finished.Succeeded, finished.Error?.ToString());
            Assert.False(File.Exists(Path.Combine(dataRoot, "runs", run.ToString("N"), "work", "loop-started.txt")));
        }
    }
    [Fact]
    public void StopAnotherRun_IsRejected_ThenImmediateStopHasNoLateFramesOrLaunch()
    {
        using var host = new RawRunnerHost(NewPipeName(), Secret, dataRoot);
        var run = Guid.NewGuid();
        Assert.Equal(RunnerReplyKind.Accepted, host.Reply(Frame(run, RunnerRequestKind.StartRun, Secret,
            RawRunnerHost.Start("msvc", "loop", "stop"))).Kind);
        var other = Guid.NewGuid();
        host.Write(Frame(other, RunnerRequestKind.StopRun, Secret));
        var refused = ReadUntilFinished(host);
        Assert.Equal(other, refused.RunId);
        Assert.Equal(ErrorCode.VALIDATION_ERROR, refused.Error);
        host.Write(Frame(run, RunnerRequestKind.StopRun, Secret));
        Assert.Equal(ErrorCode.RUN_CANCELLED, ReadUntilFinished(host).Error);
        // A fresh request acts as a protocol barrier: no old run frame may arrive after its terminal frame.
        var barrier = Guid.NewGuid();
        var reply = host.Reply(Frame(barrier, RunnerRequestKind.StartRun, "invalid"));
        Assert.Equal(barrier, reply.RunId);
        Thread.Sleep(1000);
        Assert.False(File.Exists(Path.Combine(dataRoot, "runs", run.ToString("N"), "work", "loop-started.txt")));
    }
    private static string Frame(Guid runId, RunnerRequestKind kind, string secret, StartRun? start = null,
        int protocolVersion = RunnerProtocol.Version, int schemaVersion = 1) =>
        RawRunnerHost.Frame(runId, kind, secret, start, protocolVersion, schemaVersion);
    private static string NewPipeName() => "StudyHarness.Tests." + Guid.NewGuid().ToString("N");
}
