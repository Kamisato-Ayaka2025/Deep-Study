using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudyHarness.Contracts;
namespace StudyHarness.Runner;
// The Runner accepts execution tasks only over its restricted, authenticated pipe, and only
// for registered resource ids. It owns the container and the process job for every run, so a
// disconnect, an exit or a termination destroys the whole process tree.
internal static class Program
{
    private static int Main(string[] args)
    {
        // A discovery report for the acceptance gate. It names the toolchain ids this machine
        // can actually reach, so a machine that cannot run the checks is recorded as blocked
        // rather than as a failure of the checks. It reads and launches nothing beyond what
        // discovery itself needs.
        if (Array.IndexOf(args, "--toolchains") >= 0)
        {
            var found = RunnerResources.Toolchains.Where(id => ToolchainDiscovery.Discover(id) is not null).ToArray();
            var missing = RunnerResources.Toolchains.Where(id => Array.IndexOf(found, id) < 0).ToArray();
            Console.Out.WriteLine(JsonSerializer.Serialize(new { toolchains = found, missing }, Wire.Options));
            return missing.Length == 0 ? 0 : 3;
        }
        var pipeName = Value(args, "--pipe");
        if (pipeName is null)
        {
            Console.Error.WriteLine("USAGE_INVALID");
            return 64;
        }
        // The handshake secret arrives on inherited stdin, never on the command line and never
        // through a file: no directory the program under study can read ever holds it.
        var secret = Console.In.ReadLine();
        if (string.IsNullOrEmpty(secret))
        {
            Console.Error.WriteLine("HANDSHAKE_MISSING");
            return 65;
        }
        try
        {
            // Asynchronous, not synchronous: the serving thread blocks in ReadLine while a run
            // reports from a background thread, and the I/O manager serialises operations on a
            // synchronous handle. With a synchronous handle the first frame the serving thread
            // sends (Accepted) gets through and every later frame blocks forever, which looks
            // exactly like a run that never finishes.
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName,
                System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            pipe.Connect(30000);
            using var server = new RunnerServer(secret, pipe, @"\\.\pipe\" + pipeName);
            server.Serve();
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("PIPE_UNAVAILABLE");
            return 66;
        }
    }
    private static string? Value(string[] args, string name)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (args[index] == name && args[index + 1].Length > 0) return args[index + 1];
        return null;
    }
}
internal sealed class RunnerServer : IDisposable
{
    private readonly string secret;
    private readonly Stream stream;
    private readonly string controlPipe;
    private readonly ReplyChannel replies;
    private readonly object writeGate = new();
    private readonly string dataRoot;
    private RunSession? session;
    public RunnerServer(string secret, Stream stream, string controlPipe)
    {
        this.secret = secret;
        this.stream = stream;
        this.controlPipe = controlPipe;
        replies = new ReplyChannel(Send);
        dataRoot = Path.Combine(
            Environment.GetEnvironmentVariable("STUDYHARNESS_DATA_ROOT")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudyHarness"),
            "runs");
        Directory.CreateDirectory(dataRoot);
    }
    public void Serve()
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = reader.ReadLine();
            if (line is null) break;
            if (line.Length > RunnerProtocol.MaxFrameBytes) break;
            RunnerRequest? request;
            try { request = JsonSerializer.Deserialize<RunnerRequest>(line, Wire.Options); }
            catch (JsonException) { break; }
            if (request is null) break;
            if (!FixedTimeEquals(request.HandshakeSecret, secret)) { Reject(request.RunId, ErrorCode.AUTH_INVALID); continue; }
            if (!Dispatch(request)) break;
        }
        // The host is gone. Tearing the session down here destroys every process in its job,
        // so an abnormal host exit cannot leave a compiler or a program behind.
        session?.Dispose();
        session = null;
    }
    private bool Dispatch(RunnerRequest request)
    {
        if (request.ProtocolVersion != RunnerProtocol.Version || request.SchemaVersion != 1 || request.RunId == Guid.Empty
            || !Enum.IsDefined(request.Kind)
            || (request.Kind == RunnerRequestKind.StartRun && (request.Start is null || request.Stop is not null))
            || (request.Kind == RunnerRequestKind.StopRun && (request.Stop is null || request.Start is not null)))
        {
            Reject(request.RunId, ErrorCode.VALIDATION_ERROR);
            return true;
        }
        if (request.Kind == RunnerRequestKind.StopRun)
        {
            if (session is not null && session.RunId != request.RunId)
            {
                Reject(request.RunId, ErrorCode.VALIDATION_ERROR);
                return true;
            }
            var stopping = session;
            session = null;
            // A run that already reached its terminal reply has nothing left to stop, but the
            // request still has to be answered: the caller is waiting for a terminal frame.
            if (stopping is null || !stopping.Stop()) Reject(request.RunId, ErrorCode.RUN_CANCELLED);
            stopping?.Dispose();
            return true;
        }
        if (request.Start is not { } start || !RunnerResources.IsValidSandboxProfile(start.SandboxProfileId)
            || !RunnerResources.IsValidSourceBundle(start.SourceBundleId) || !RunnerResources.IsValidToolchain(start.ToolchainId)
            || !RunnerResources.IsValidTestSuite(start.TestSuiteId) || !Enum.IsDefined(start.Purpose))
        {
            // Only registered ids reach the filesystem or a process launch, so a request can
            // never name a command, a path, an environment variable or an unknown resource.
            Reject(request.RunId, ErrorCode.VALIDATION_ERROR);
            return true;
        }
        if (session is not null && !session.IsFinished) { Reject(request.RunId, ErrorCode.TASK_ACTIVE); return true; }
        session?.Dispose();
        session = null;
        var toolchain = ToolchainDiscovery.Discover(start.ToolchainId);
        if (toolchain is null) { Reject(request.RunId, ErrorCode.TOOLCHAIN_MISSING); return true; }
        SandboxProfile profile;
        try
        {
            profile = SandboxProfile.Create(start.SandboxProfileId + "." + Guid.NewGuid().ToString("N"));
        }
        catch (Exception)
        {
            Reject(request.RunId, ErrorCode.ISOLATION_UNAVAILABLE);
            return true;
        }
        var created = RunSession.Create(request.RunId, profile, toolchain, dataRoot, controlPipe, replies);
        if (created is null) { profile.Dispose(); Reject(request.RunId, ErrorCode.IO_FAILURE); return true; }
        session = created;
        replies.Send(request.RunId, RunnerReplyKind.Accepted, true);
        _ = Task.Run(() =>
        {
            try { created.Execute(start); }
            catch (Exception) { replies.Send(request.RunId, RunnerReplyKind.RunFinished, false, error: ErrorCode.IO_FAILURE); }
        });
        return true;
    }
    private void Reject(Guid runId, ErrorCode code) =>
        replies.Send(runId, RunnerReplyKind.RunFinished, false, error: code);
    private void Send(RunnerReply reply)
    {
        var payload = JsonSerializer.Serialize(reply, Wire.Options);
        // The lock keeps frames whole when a run thread and the serving thread both report.
        lock (writeGate)
        {
            if (payload.Length > RunnerProtocol.MaxFrameBytes) return;
            try
            {
                stream.Write(Encoding.UTF8.GetBytes(payload + "\n"));
                stream.Flush();
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }
    private static bool FixedTimeEquals(string? presented, string expected)
    {
        if (presented is null) return false;
        var left = Encoding.UTF8.GetBytes(presented);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
    public void Dispose()
    {
        session?.Dispose();
    }
}
// The channel's single reply counter. It lives here rather than in the session so that every
// frame on the channel -- the acceptance, the per-case results and the terminal finish --
// shares one increasing sequence, whichever thread writes it. Two counters would let a frame
// arrive with a number its predecessor already used, which is how a reordered or replayed
// reply could pass for a fresh one.
internal sealed class ReplyChannel(Action<RunnerReply> send)
{
    private long sequence;
    private readonly object gate = new();
    public void Send(Guid runId, RunnerReplyKind kind, bool succeeded, string? caseId = null, ErrorCode? error = null, int? nativeError = null)
    {
        lock (gate) send(new RunnerReply(runId, ++sequence, kind, succeeded, caseId, error, nativeError));
    }
}
