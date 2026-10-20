using StudyHarness.Contracts;
namespace StudyHarness.Application;
// Host side of the Runner channel (Plan.md section 5.5). A caller names registered resource
// identifiers and nothing else: no command string, no path, no environment variable. The
// implementation owns the Runner process and its job, so a lost channel, an exit or a
// termination destroys the whole process tree it started.
public sealed record RunnerStart(Guid RunId, string SandboxProfileId, string SourceBundleId, string ToolchainId, string TestSuiteId, RunnerPurpose Purpose)
{
    public static RunnerStart For(Guid runId, string toolchainId, string sourceBundleId, string testSuiteId, RunnerPurpose purpose) =>
        new(runId, RunnerResources.SandboxProfiles[0], sourceBundleId, toolchainId, testSuiteId, purpose);
}
public sealed record RunnerAccepted(Guid RunId);
public interface IRunnerPort
{
    // Launches a Runner, authenticates it and returns the open channel. Failure means the
    // boundary could not be established, which is reported as a failure rather than worked
    // around by running the program as an ordinary process.
    Task<Result<IRunnerSession>> OpenAsync(CancellationToken cancellationToken = default);
}
public interface IRunnerSession : IAsyncDisposable
{
    // Sends StartRun and consumes the first reply, which is either Accepted or the RunFinished
    // that refuses the request. Later replies are read with ReadAsync.
    Task<Result<RunnerAccepted>> StartAsync(RunnerStart run, CancellationToken cancellationToken = default);
    // Sends StopRun and returns the terminal RunFinished for that run. Success means the
    // Runner confirmed the run ended, not that the run itself passed.
    Task<Result<RunnerReply>> StopAsync(Guid runId, CancellationToken cancellationToken = default);
    // Null means the channel closed, which is not by itself evidence of success.
    Task<RunnerReply?> ReadAsync(CancellationToken cancellationToken = default);
    // Processes still owned by the session's job. A run is over when this returns to the value
    // it had before the run started: the compiler, the program under study and every
    // descendant of either are gone. The baseline is not zero or one because the job also owns
    // the Runner itself and, on Windows, the hidden console host the system starts for it, so
    // a caller compares against the count it read after OpenAsync rather than against a
    // constant.
    int ActiveProcessCount { get; }
}
