using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StudyHarness.Application;
using StudyHarness.Contracts;
namespace StudyHarness.Infrastructure;
// Launches the isolated Runner and owns its lifecycle. The host is the pipe server and the
// job owner: it creates the control channel, hands the Runner a per-session secret over
// inherited stdin, assigns the Runner to a kill-on-close job, and authenticates every frame.
//
// The job is what makes an abnormal host exit safe. It is not the file or network boundary --
// that is the AppContainer the Runner creates for each run -- but without it a host crash
// would leave a compiler and a program running with nothing left to stop them.
public sealed class RunnerHost(string runnerPath, string? dataRoot = null, TimeSpan? timeout = null) : IRunnerPort
{
    private readonly string runnerPath = Path.GetFullPath(runnerPath);
    // Absolute, because the child resolves the value against its own working directory while
    // this process resolves it against the caller's. A relative root would give the two
    // different directories, and the toolchain would then be handed a source path that exists
    // only in the host's imagination.
    private readonly string dataRoot = Path.GetFullPath(dataRoot
        ?? Environment.GetEnvironmentVariable("STUDYHARNESS_DATA_ROOT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudyHarness"));
    private readonly TimeSpan timeout = timeout ?? TimeSpan.FromSeconds(60);
    public async Task<Result<IRunnerSession>> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(runnerPath))
            return Result<IRunnerSession>.Fail(ErrorCode.ISOLATION_UNAVAILABLE, "未找到隔离运行组件，无法执行代码。");
        var pipeName = "StudyHarness.Runner." + Guid.NewGuid().ToString("N");
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(RunnerProtocol.HandshakeSecretBytes));
        NamedPipeServerStream? pipe = null;
        Job? job = null;
        Process? process = null;
        try
        {
            pipe = CreateControlPipe(pipeName);
            process = new Process
            {
                StartInfo = new ProcessStartInfo(runnerPath)
                {
                    // The only argument is the channel name. No task description, no path and
                    // no secret travels on a command line, where any process could read it.
                    Arguments = "--pipe " + pipeName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = AppContext.BaseDirectory
                }
            };
            // Both sides must agree on where runs live, and it must never be the source tree:
            // the child inherits the value from this process rather than reading a file.
            process.StartInfo.Environment["STUDYHARNESS_DATA_ROOT"] = dataRoot;
            if (!process.Start())
                return Result<IRunnerSession>.Fail(ErrorCode.ISOLATION_UNAVAILABLE, "无法启动隔离运行组件。");
            // The Runner is given no argument beyond the pipe name and nothing at all on the
            // command line that could be read back: the secret arrives on inherited stdin and
            // never lands in a file, an argument list or a directory the program can read.
            process.ErrorDataReceived += static (_, _) => { };
            process.BeginErrorReadLine();
            var started = process;
            job = Job.Create();
            if (!job.Assign(started))
                return Result<IRunnerSession>.Fail(ErrorCode.ISOLATION_UNAVAILABLE, "无法为隔离运行建立进程作业。");
            await started.StandardInput.WriteLineAsync(secret);
            await started.StandardInput.FlushAsync(cancellationToken);
            started.StandardInput.Close();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try
            {
                await pipe.WaitForConnectionAsync(deadline.Token);
            }
            catch (OperationCanceledException)
            {
                // No message text from the child ever reaches a caller: a launch failure is a
                // stable code plus a fixed sentence.
                return Result<IRunnerSession>.Fail(ErrorCode.ISOLATION_UNAVAILABLE, "隔离运行组件未能建立通信。");
            }
            var session = new RunnerSession(started, pipe, job, secret);
            process = null;
            pipe = null;
            job = null;
            return Result<IRunnerSession>.Success(session);
        }
        catch (Exception)
        {
            return Result<IRunnerSession>.Fail(ErrorCode.ISOLATION_UNAVAILABLE, "无法建立隔离运行环境。");
        }
        finally
        {
            if (process is not null)
            {
                try { if (!process.HasExited) { process.Kill(true); process.WaitForExit(10000); } }
                catch (InvalidOperationException) { }
            }
            process?.Dispose();
            pipe?.Dispose();
            job?.Dispose();
        }
    }
    // The control channel grants the current user only. An AppContainer token carries the user
    // SID as deny-only, so the program under study is refused even without an explicit deny
    // entry, and the handshake secret is a second lock on the same door.
    private static NamedPipeServerStream CreateControlPipe(string name)
    {
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
    }
}
internal sealed class RunnerSession : IRunnerSession
{
    // Stopping is expected to answer at once. The bound exists so a Runner that has gone
    // silent surfaces as a failure instead of a caller that never returns.
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private readonly Process process;
    private readonly Job job;
    private readonly NamedPipeServerStream pipe;
    private readonly string secret;
    private readonly Channel<RunnerReply> replies = Channel.CreateUnbounded<RunnerReply>();
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly Task pump;
    private bool disposed;
    internal RunnerSession(Process process, NamedPipeServerStream pipe, Job job, string secret)
    {
        (this.process, this.pipe, this.job, this.secret) = (process, pipe, job, secret);
        pump = Task.Run(Pump);
    }
    public int ActiveProcessCount => job.ActiveProcesses;
    public async Task<Result<RunnerAccepted>> StartAsync(RunnerStart run, CancellationToken cancellationToken = default)
    {
        if (run is null || run.RunId == Guid.Empty)
            return Result<RunnerAccepted>.Fail(ErrorCode.VALIDATION_ERROR, "运行标识无效。");
        // The host validates the same registered sets the Runner does, so an unregistered
        // identifier is refused here rather than travelling as a request.
        if (!RunnerResources.IsValidSandboxProfile(run.SandboxProfileId) || !RunnerResources.IsValidSourceBundle(run.SourceBundleId)
            || !RunnerResources.IsValidToolchain(run.ToolchainId) || !RunnerResources.IsValidTestSuite(run.TestSuiteId) || !Enum.IsDefined(run.Purpose))
            return Result<RunnerAccepted>.Fail(ErrorCode.VALIDATION_ERROR, "运行请求包含未登记的资源标识。");
        await Send(new RunnerRequest(run.RunId, RunnerRequestKind.StartRun, secret,
            new StartRun(run.SandboxProfileId, run.SourceBundleId, run.ToolchainId, run.TestSuiteId, run.Purpose)), cancellationToken);
        var first = await ReadAsync(cancellationToken);
        if (first is null)
            return Result<RunnerAccepted>.Fail(ErrorCode.ISOLATION_UNAVAILABLE, "隔离运行通信已中断。");
        return first.Kind == RunnerReplyKind.Accepted
            ? Result<RunnerAccepted>.Success(new RunnerAccepted(run.RunId))
            : Result<RunnerAccepted>.Fail(first.Error ?? ErrorCode.IO_FAILURE, "隔离运行拒绝了该请求。");
    }
    public async Task<Result<RunnerReply>> StopAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        if (runId == Guid.Empty)
            return Result<RunnerReply>.Fail(ErrorCode.VALIDATION_ERROR, "运行标识无效。");
        await Send(new RunnerRequest(runId, RunnerRequestKind.StopRun, secret, Stop: new StopRun()), cancellationToken);
        // StopRun's answer is the run's terminal RunFinished, so the caller learns the outcome
        // from the reply rather than from the disappearance of a process it cannot see.
        //
        // The wait is bounded even though the Runner is expected to answer immediately: a
        // Runner that goes silent must surface as a failure, never as a caller that never
        // returns. Stopping is the one call a user makes while something is wrong.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(StopTimeout);
        while (true)
        {
            var reply = await ReadAsync(deadline.Token);
            if (reply is null)
                return Result<RunnerReply>.Fail(ErrorCode.ISOLATION_UNAVAILABLE, "隔离运行通信已中断。");
            if (reply.RunId == runId && reply.Kind == RunnerReplyKind.RunFinished) return Result<RunnerReply>.Success(reply);
        }
    }
    public async Task<RunnerReply?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await replies.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException) { return null; }
        catch (OperationCanceledException) { return null; }
    }
    private async Task Send(RunnerRequest request, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Wire.Options) + "\n");
        if (payload.Length > RunnerProtocol.MaxFrameBytes)
            throw new InvalidOperationException("Runner request frame too large");
        await writing.WaitAsync(cancellationToken);
        try
        {
            await pipe.WriteAsync(payload, cancellationToken);
            await pipe.FlushAsync(cancellationToken);
        }
        finally { writing.Release(); }
    }
    private async Task Pump()
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line is null) break;
                if (line.Length == 0 || line.Length > RunnerProtocol.MaxFrameBytes) continue;
                RunnerReply? reply;
                try { reply = JsonSerializer.Deserialize<RunnerReply>(line, Wire.Options); }
                catch (JsonException) { break; }
                if (reply is null) break;
                await replies.Writer.WriteAsync(reply);
            }
        }
        catch (Exception) { }
        finally { replies.Writer.TryComplete(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        // Closing the pipe is the polite half: the Runner sees end-of-stream, destroys its
        // session and exits, taking every process in its own job with it.
        try { pipe.Dispose(); } catch (Exception) { }
        try { await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token); }
        catch (Exception) { }
        pump.Wait(TimeSpan.FromSeconds(5));
        // The job is the other half: whatever is still alive, including anything the Runner
        // could not reap, dies here. Nothing survives this session.
        job.Terminate();
        job.Dispose();
        process.Dispose();
        writing.Dispose();
    }
}
// A kill-on-close process job. The handle is held open for the life of the session, so the
// operating system closes it for us if this host dies without unwinding, and every process
// in the job is terminated at that moment.
internal sealed class Job : IDisposable
{
    private nint handle;
    private Job(nint handle) => this.handle = handle;
    public static Job Create()
    {
        var handle = Native.CreateJobObject(0, null);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject");
        var job = new Job(handle);
        try { job.SetKillOnClose(); }
        catch (Exception) { job.Dispose(); throw; }
        return job;
    }
    private void SetKillOnClose()
    {
        var limits = new Native.ExtendedLimits { Basic = new Native.BasicLimits { LimitFlags = Native.JobObjectLimitKillOnJobClose } };
        if (!Native.SetInformationJobObject(handle, Native.JobObjectExtendedLimitInformation, ref limits,
            (uint)Marshal.SizeOf<Native.ExtendedLimits>()))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject");
    }
    public bool Assign(Process process) => Native.AssignProcessToJobObject(handle, process.Handle);
    // Live process count for the job. A run has left nothing behind when this returns to the
    // count read after the channel opened: the compiler, the program under study and every
    // descendant of either are gone, which is the evidence the stop and cleanup checks are
    // built on. The baseline is neither zero nor one, because the job also owns the Runner and
    // the hidden console host Windows starts for a console-subsystem child.
    public int ActiveProcesses
    {
        get
        {
            if (handle == 0) return 0;
            var information = new Native.BasicAccountingInformation();
            return Native.QueryInformationJobObject(handle, Native.JobObjectBasicAccountingInformation,
                ref information, (uint)Marshal.SizeOf<Native.BasicAccountingInformation>(), 0)
                ? (int)information.ActiveProcesses
                : -1;
        }
    }
    public void Terminate()
    {
        if (handle == 0) return;
        Native.TerminateJobObject(handle, 1);
    }
    public void Dispose()
    {
        if (handle == 0) return;
        Native.CloseHandle(handle);
        handle = 0;
    }
}
internal static class Native
{
    public const uint JobObjectLimitKillOnJobClose = 0x2000;
    public const nuint JobObjectExtendedLimitInformation = 9;
    public const nuint JobObjectBasicAccountingInformation = 1;
    [StructLayout(LayoutKind.Sequential)] public struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] public struct ExtendedLimits
    {
        public BasicLimits Basic; public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential)] public struct BasicAccountingInformation
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetInformationJobObject(nint job, nuint informationClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool QueryInformationJobObject(nint job, nuint informationClass, ref BasicAccountingInformation information, uint size, nint returned);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateJobObject(nint job, uint exitCode);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint handle);
}
