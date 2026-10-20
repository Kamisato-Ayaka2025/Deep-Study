using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using StudyHarness.Contracts;
namespace StudyHarness.Runner;
// One run: compile a registered source bundle inside the container, then execute the produced
// program inside it. Every path the program may touch is chosen here, never by the caller; the
// caller only ever names registered resource ids. Compiled once, the compiler also runs inside
// the same container as the program it produces.
internal sealed class RunSession : IDisposable
{
    private const int CompileTimeoutMilliseconds = 240000;
    private const int ObservationWindowMilliseconds = 20000;
    private const int WaitTimeoutCompleted = 258;
    private readonly SandboxProfile profile;
    private readonly Toolchain toolchain;
    private readonly ReplyChannel replies;
    private readonly string controlPipe;
    private readonly string work;
    private readonly string outside;
    private readonly Guid runId;
    private readonly object gate = new();
    private SandboxedProcess? active;
    private bool cancelled;
    private bool finished;
    private bool failedCase;
    private bool disposed;
    public Guid RunId => runId;
    public bool IsFinished { get { lock (gate) return finished; } }
    private RunSession(Guid runId, SandboxProfile profile, Toolchain toolchain, string runRoot, string controlPipe, ReplyChannel replies)
    {
        (this.runId, this.profile, this.toolchain, this.controlPipe, this.replies) = (runId, profile, toolchain, controlPipe, replies);
        work = Path.Combine(runRoot, "work");
        outside = Path.Combine(runRoot, "outside");
    }
    public static RunSession? Create(Guid runId, SandboxProfile profile, Toolchain toolchain, string runsRoot, string controlPipe, ReplyChannel replies)
    {
        // The directory name comes from the run id, so a caller cannot influence the layout.
        var runRoot = Path.Combine(runsRoot, runId.ToString("N"));
        try
        {
            // Atomically claim a fresh directory. Reusing an old run id could
            // otherwise make host copies follow files/links planted by that run.
            if (!CreateDirectory(runRoot, 0)) return null;
            Directory.CreateDirectory(Path.Combine(runRoot, "work"));
            Directory.CreateDirectory(Path.Combine(runRoot, "outside"));
            File.WriteAllText(Path.Combine(runRoot, "outside", "sentinel.txt"), "Study Harness synthetic sentinel; not user data.\n");
        }
        catch (Exception) { return null; }
        return new RunSession(runId, profile, toolchain, runRoot, controlPipe, replies);
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectory(string path, nint attributes);
    public void Execute(StartRun start)
    {
        try
        {
            var bundle = SourceBundles.Resolve(start.SourceBundleId);
            if (bundle is null) { Finish(false, ErrorCode.SOURCE_UNAVAILABLE); return; }
            foreach (var file in Directory.GetFiles(bundle))
            {
                // Registered C fixtures are text. Never copy through a link or
                // overwrite a target planted by an earlier process.
                var text = SafeRunFiles.ReadText(bundle, file);
                using var target = SafeRunFiles.CreateNew(work, Path.Combine(work, Path.GetFileName(file)));
                using var writer = new StreamWriter(target, new System.Text.UTF8Encoding(false));
                writer.Write(text);
            }
            var program = Path.Combine(work, "program.exe");
            if (!Compile(program)) return;
            if (start.Purpose == RunnerPurpose.Compile) { Finish(true, null); return; }
            if (start.TestSuiteId == "isolation") RunIsolation(program);
            else RunProgram(program);
        }
        catch (Exception)
        {
            // No exception text travels anywhere: replies carry stable codes only.
            Finish(false, ErrorCode.IO_FAILURE);
        }
    }
    private bool Compile(string program)
    {
        // Compiler variables enter an explicit Unicode block, never the Runner's global environment.
        var output = Path.Combine(work, "compile-stdout.txt");
        var error = Path.Combine(work, "compile-stderr.txt");
        var child = Start(new SandboxSpec(work, toolchain.Compiler,
            toolchain.CompileArguments(Path.Combine(work, "main.c"), program, work), output, error, CompileTimeoutMilliseconds, toolchain.Environment));
        if (child is null) return false;
        var exited = Wait(child, CompileTimeoutMilliseconds);
        var succeeded = exited && child.ExitCode == 0 && File.Exists(program);
        Send(RunnerReplyKind.CompileResult, succeeded, "compile", exited ? child.ExitCode : WaitTimeoutCompleted);
        if (succeeded) return true;
        Finish(false, ErrorCode.IO_FAILURE);
        return false;
    }
    private void RunProgram(string program)
    {
        var child = Start(new SandboxSpec(work, program, [],
            Path.Combine(work, "run-stdout.txt"), Path.Combine(work, "run-stderr.txt"), ObservationWindowMilliseconds));
        if (child is null) return;
        if (!Wait(child, ObservationWindowMilliseconds))
        {
            // Still running past the window: the intended outcome for a program that never
            // terminates. The process is left running so the caller can stop it by hand, and
            // RunFinished is withheld until it is stopped.
            Send(RunnerReplyKind.CaseResult, true, "observation-window-exceeded", WaitTimeoutCompleted);
            return;
        }
        var marker = ReadText(Path.Combine(work, "run-stdout.txt")).Contains("study-harness-isolated-hello", StringComparison.Ordinal);
        Send(RunnerReplyKind.CaseResult, marker && child.ExitCode == 0, "program-output", child.ExitCode);
        Finish(marker && child.ExitCode == 0, null);
    }
    private void RunIsolation(string program)
    {
        var junction = Path.Combine(work, "escape");
        var traversal = Path.Combine(work, "..", "outside", "traversal-write.txt");
        using var listeners = new ProbeListeners();
        using var probePipe = new ProbePipe();
        // Both package classes must be denied; LPAC still honors ARAP grants.
        var unrelatedSentinel = Path.Combine(outside, "sentinel.txt");
        var ambientSentinel = Path.Combine(outside, "aap-readable-sentinel.txt");
        File.WriteAllText(ambientSentinel, "Synthetic unrelated user file, not a compiler dependency.");
        var ambientFile = new FileInfo(ambientSentinel);
        var ambientAcl = ambientFile.GetAccessControl();
        ambientAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-1"),
            FileSystemRights.Read, AccessControlType.Allow));
        ambientFile.SetAccessControl(ambientAcl);
        var restrictedAmbientSentinel = Path.Combine(outside, "arap-readable-sentinel.txt");
        File.WriteAllText(restrictedAmbientSentinel, "Synthetic unrelated user file, not a compiler dependency.");
        var restrictedAmbientFile = new FileInfo(restrictedAmbientSentinel);
        var restrictedAmbientAcl = restrictedAmbientFile.GetAccessControl();
        restrictedAmbientAcl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-2"),
            FileSystemRights.Read, AccessControlType.Allow));
        restrictedAmbientFile.SetAccessControl(restrictedAmbientAcl);
        // A link the host created inside the run directory and pointing outside it is one of the
        // shapes Plan.md section 6.4 requires the mechanism to survive, and the boundary has to
        // hold on the final resolved path rather than on the path as written. If the volume
        // refuses to create it at all, that case is reported as failed rather than quietly
        // dropped: a boundary nobody could test is not a boundary that held.
        var junctionReady = false;
        try
        {
            // A pre-existing name is a failed fixture setup, never a reason
            // for the host to delete an object left by sandboxed code.
            if (Directory.Exists(junction) || File.Exists(junction)) throw new IOException("Existing probe path");
            Junction.Create(junction, outside);
            junctionReady = true;
        }
        catch (Exception) { }
        // The controls run before the program starts, so what they consume cannot be confused
        // with anything the program did.
        var hostTargets = listeners.SelfTest();
        var hostResolves = ProbeListeners.ResolveControl(ProbeListeners.ExternalDnsName);
        var hostCanReadSentinel = ReadError(unrelatedSentinel);
        var arguments = new[]
        {
            unrelatedSentinel, Path.Combine(outside, "escape-write.txt"),
            traversal, Path.Combine(junction, "escape-write.txt"),
            // The fixture is pointed at the real control channel as well as at a decoy with the
            // same ACL shape: a program that could open the control channel could speak the
            // host protocol, so that attempt is the one worth measuring.
            controlPipe, probePipe.Name,
            listeners.TcpPort.ToString(CultureInfo.InvariantCulture), listeners.Address,
            listeners.UdpPort.ToString(CultureInfo.InvariantCulture), ProbeListeners.ExternalDnsName,
            program, Path.Combine(work, "child.json"), toolchain.Header, Path.Combine(work, "escape.json"),
            ambientSentinel, "0", Dns.GetHostName(), Path.Combine(profile.OriginalLocalAppData, "Temp", "outside-profile-probe.txt"),
            restrictedAmbientSentinel
        };
        var child = Start(new SandboxSpec(work, program, arguments,
            Path.Combine(work, "escape-stdout.txt"), Path.Combine(work, "escape-stderr.txt"), CompileTimeoutMilliseconds));
        if (child is null) return;
        if (!Wait(child, CompileTimeoutMilliseconds) || child.ExitCode != 0)
        {
            Finish(false, ErrorCode.IO_FAILURE);
            return;
        }
        var observed = ReadReport(Path.Combine(work, "escape.json"));
        if (observed is null) { Finish(false, ErrorCode.IO_FAILURE); return; }
        // Raw native numbers are reported so the caller can re-check the policy itself rather
        // than trust this summary; the listener side is evidence of the same kind, because a
        // blocked program must leave the controlled targets untouched. The positive cases come
        // first: a container that broke the toolchain, or that blocked every socket it has,
        // would otherwise pass the whole negative list by doing nothing.
        Equal(observed, "isAppContainer", 1);
        Equal(observed, "insideWriteError", 0);
        Equal(observed, "headerReadError", 0);
        Equal(observed, "containerLoopbackError", 10013);
        Equal(observed, "tempWithinWork", 1);
        Equal(observed, "tempWriteError", 0);
        Equal(observed, "tempFileWithinWork", 1);
        Equal(observed, "childSpawnError", 0);
        Equal(observed, "childIsAppContainer", 1);
        // Each refusal is paired with the control that makes it evidence: the target is live and
        // the file is readable from this process, so the container's failure is isolation.
        Send(RunnerReplyKind.CaseResult, hostTargets == 0, "host-target-reachable", hostTargets);
        Send(RunnerReplyKind.CaseResult, hostResolves == 0, "host-dns-resolves", hostResolves);
        Send(RunnerReplyKind.CaseResult, hostCanReadSentinel == 0, "unrelated-file-readable-on-host", hostCanReadSentinel);
        Denied(observed, "unrelatedReadError", "unrelated-file-read-denied");
        Denied(observed, "outsideWriteError", "outside-write-denied");
        Denied(observed, "traversalWriteError", "traversal-escape-denied");
        if (junctionReady) Denied(observed, "junctionWriteError", "junction-escape-denied");
        else Send(RunnerReplyKind.CaseResult, false, "junction-escape-denied", 5);
        Denied(observed, "controlPipeConnectError", "runner-pipe-denied");
        Denied(observed, "decoyPipeConnectError", "decoy-pipe-denied");
        Denied(observed, "tcpV4Error", "tcp-egress-denied");
        // UDP is judged at the receiver, not at the sender: a blocked datagram is accepted by
        // sendto and then dropped, so the send call returning success is not evidence either
        // way. The raw sendto result still travels as this case's native number so a reviewer
        // can see that it was 0 and that the denial came from the network policy.
        Send(RunnerReplyKind.CaseResult, listeners.UdpUntouched, "udp-egress-denied",
            observed.TryGetValue("udpError", out var udpError) ? udpError : null);
        Denied(observed, "dnsError", "dns-egress-denied");
        Denied(observed, "childConnectError", "child-egress-denied");
        var untouched = listeners.Untouched && !probePipe.Connected;
        Send(RunnerReplyKind.CaseResult, untouched, "listeners-untouched", untouched ? 0 : 1);
        Denied(observed, "originalProfileWriteError", "original-profile-write-denied");
        // Local resolution is diagnostic; unrelated file access remains a hard boundary.
        // the acceptance record states what each means. See ADR-002 measurement G.
        Denied(observed, "ambientAapReadError", "ambient-aap-read-denied");
        Denied(observed, "ambientArapReadError", "ambient-arap-read-denied");
        var hostPackageReads = ReadError(ambientSentinel) | ReadError(restrictedAmbientSentinel);
        Send(RunnerReplyKind.CaseResult, hostPackageReads == 0, "ambient-files-readable-on-host", hostPackageReads);
        Send(RunnerReplyKind.CaseResult, true, "local-name-resolution-measured",
            observed.TryGetValue("dnsHostNameError", out var localName) ? localName : null);
        Finish(true, null);
    }
    // Whether this process -- the user the experiment belongs to -- can read the file the
    // container is denied. Read-only, and the file is a synthetic one the Runner itself wrote.
    private static int ReadError(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            return file.ReadByte() >= 0 ? 0 : 1;
        }
        catch (IOException) { return 5; }
        catch (UnauthorizedAccessException) { return 5; }
    }
    // Reports a raw probe number and, separately, whether the mechanism held.
    private void Denied(Dictionary<string, int> observed, string field, string caseId)
    {
        if (!observed.TryGetValue(field, out var value)) { Send(RunnerReplyKind.CaseResult, false, caseId, null); return; }
        var denied = caseId.Contains("egress", StringComparison.Ordinal)
            ? value is 10013 or 10060 or 10061 or 11001
            : value == 5;
        Send(RunnerReplyKind.CaseResult, denied, caseId, value);
    }
    private void Equal(Dictionary<string, int> observed, string field, int expected)
    {
        var present = observed.TryGetValue(field, out var value);
        Send(RunnerReplyKind.CaseResult, present && value == expected, field, present ? value : null);
    }
    private Dictionary<string, int>? ReadReport(string path)
    {
        var report = ReadText(path);
        if (report.Length == 0) return null;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(report);
            if (parsed is null) return null;
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in parsed)
                if (pair.Value.TryGetInt32(out var number)) values[pair.Key] = number;
            return values;
        }
        catch (JsonException) { return null; }
    }
    private string ReadText(string path)
    {
        try { return SafeRunFiles.ReadText(work, path); }
        catch (Exception) { return string.Empty; }
    }
    private SandboxedProcess? Start(SandboxSpec spec)
    {
        spec = spec with { ReadRoots = toolchain.ReadRoots };
        List<string> journal = [];
        try
        {
            lock (gate)
            {
                if (cancelled || finished) return null;
                profile.AllowWorkRoot(work);
                journal = [];
                var child = SandboxedProcess.Start(profile, spec, journal);
                active = child;
                return child;
            }
        }
        catch (Exception error)
        {
            // Fail closed. Failure to establish the container is reported as
            // ISOLATION_UNAVAILABLE and the program is never run outside it: neither a
            // working-directory setting nor a process-tree kill is a substitute boundary.
            // Diagnostics must not follow a name planted in sandbox-writable work.
            File.WriteAllLines(Path.Combine(outside, "sandbox-failure-" + Guid.NewGuid().ToString("N") + ".txt"),
                journal.Append($"type={error.GetType().Name} hresult={error.HResult}"));
            Finish(false, ErrorCode.ISOLATION_UNAVAILABLE);
            return null;
        }
    }
    // Leaves a still-running process alive and owned by this session, so a later Stop can
    // destroy its whole tree. Disposing it here would kill the tree at the window boundary.
    private bool Wait(SandboxedProcess child, int milliseconds)
    {
        if (!child.WaitForExit(milliseconds)) return false;
        lock (gate)
        {
            if (ReferenceEquals(active, child)) { active = null; child.Dispose(); }
        }
        return true;
    }
    // Stops the whole tree: the job owns every descendant, so this ends the compiler, the
    // program and anything either spawned, not merely the process that was started.
    //
    // Returns whether this call wrote the run's terminal reply. A stop for a run that has
    // already finished has nothing to kill, and answering with silence would leave the caller
    // waiting for a terminal frame that is never coming; the caller answers instead.
    public bool Stop()
    {
        SandboxedProcess? child;
        lock (gate)
        {
            cancelled = true;
            if (finished) return false;
            child = active;
            active = null;
        }
        child?.Kill();
        child?.Dispose();
        Finish(false, ErrorCode.RUN_CANCELLED);
        return true;
    }
    private void Finish(bool succeeded, ErrorCode? error)
    {
        lock (gate)
        {
            if (finished) return;
            finished = true;
            active?.Kill();
            active?.Dispose();
            active = null;
            replies.Send(runId, RunnerReplyKind.RunFinished, succeeded && !failedCase && !cancelled,
                error: cancelled ? ErrorCode.RUN_CANCELLED : error ?? (failedCase ? ErrorCode.IO_FAILURE : null));
        }
        // RunFinished is written even though the run is over: it is the one reply that must
        // still be delivered, and it must be the last one.
    }
    private void Send(RunnerReplyKind kind, bool succeeded, string? caseId, int? nativeError, ErrorCode? error = null)
    {
        lock (gate)
        {
            // A finished or stopped run reports nothing further: a late frame would break the
            // host's assumption that RunFinished is the last reply on the channel.
            if (cancelled || finished) return;
            if (!succeeded) failedCase = true;
            replies.Send(runId, kind, succeeded, caseId, error, nativeError);
        }
    }
    public void Dispose()
    {
        SandboxedProcess? child;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            cancelled = true;
            child = active;
            active = null;
        }
        child?.Kill();
        child?.Dispose();
        profile.Dispose();
    }
}
// Controlled targets for the isolation suite. Nothing outside this run may be contacted, so
// the program's egress attempts are aimed at sockets the Runner itself owns and can observe.
internal sealed class ProbeListeners : IDisposable
{
    // A name that resolves on any machine with a working resolver. The DNS case is only
    // evidence if the name is resolvable somewhere: a name that cannot resolve anywhere would
    // fail inside the container for a reason that has nothing to do with the boundary.
    public const string ExternalDnsName = "example.com";
    private readonly TcpListener tcp;
    private readonly UdpClient udp;
    private readonly IPAddress address;
    public ProbeListeners()
    {
        address = LocalAddress();
        tcp = new TcpListener(address, 0);
        tcp.Start();
        TcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
        udp = new UdpClient(new IPEndPoint(address, 0));
        UdpPort = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }
    public int TcpPort { get; }
    public int UdpPort { get; }
    public string Address => address.ToString();
    // The two halves are reported separately because they answer differently: a refused TCP
    // connection is visible to the caller, while a dropped UDP datagram is visible only here.
    public bool TcpUntouched => !tcp.Pending();
    public bool UdpUntouched => udp.Available == 0;
    public bool Untouched => TcpUntouched && UdpUntouched;
    // The host's half of the same policy: each target the contained program is aimed at is
    // reachable from this process, so a refusal inside the container is isolation and not a port
    // nobody was listening on. Every control consumes what it sent and then restores the target
    // exactly as it was: the TCP connections are accepted and closed, and the datagram is
    // drained again. A target that has been touched can therefore still be checked afterwards,
    // and anything found there later can only have come from the container.
    public int SelfTest()
    {
        var client = new TcpClient();
        try
        {
            client.Connect(address, TcpPort);
            using var accepted = tcp.AcceptTcpClient();
        }
        catch (SocketException error) { return (int)error.SocketErrorCode; }
        finally { client.Dispose(); }
        var sender = new UdpClient(new IPEndPoint(address, 0));
        try
        {
            var payload = "study-harness-host-control"u8.ToArray();
            sender.Send(payload, payload.Length, new IPEndPoint(address, UdpPort));
            udp.Client.ReceiveTimeout = 5000;
            var endpoint = new IPEndPoint(IPAddress.Any, 0);
            var received = udp.Receive(ref endpoint);
            return received.Length == payload.Length ? 0 : 1;
        }
        catch (SocketException error) { return (int)error.SocketErrorCode; }
        finally { sender.Dispose(); }
    }
    // Whether this machine's resolver answers for the name the contained program is given.
    public static int ResolveControl(string name)
    {
        try { return Dns.GetHostAddresses(name).Length > 0 ? 0 : 1; }
        catch (SocketException error) { return (int)error.SocketErrorCode; }
    }
    private static IPAddress LocalAddress()
    {
        try
        {
            var preferred = Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(candidate => candidate.AddressFamily == AddressFamily.InterNetwork);
            if (preferred is not null) return preferred;
        }
        catch (SocketException) { }
        return IPAddress.Loopback;
    }
    public void Dispose() { tcp.Stop(); udp.Dispose(); }
}
// A dedicated pipe the contained program is invited to attack. If it ever connects, the
// isolation suite fails on its own evidence rather than on an assumption. This is never the
// control channel, so the handshake pipe stays secret.
internal sealed class ProbePipe : IDisposable
{
    private readonly System.IO.Pipes.NamedPipeServerStream pipe;
    public ProbePipe()
    {
        var bare = "StudyHarness.Probe." + Guid.NewGuid().ToString("N");
        Name = @"\\.\pipe\" + bare;
        pipe = new System.IO.Pipes.NamedPipeServerStream(bare, System.IO.Pipes.PipeDirection.InOut, 1);
    }
    public string Name { get; }
    public bool Connected => pipe.IsConnected;
    public void Dispose() => pipe.Dispose();
}
internal static class SourceBundles
{
    // Bundle ids resolve against a fixed root, so a caller-supplied path is never read. The
    // packaged layout puts bundles beside the executable; a development tree is found by
    // walking up to the repository root.
    public static string? Resolve(string id)
    {
        if (id.Length == 0 || id.Contains('.') || id.Contains('/') || id.Contains('\\')) return null;
        var local = Path.Combine(AppContext.BaseDirectory, "bundles", id);
        if (Directory.Exists(local)) return local;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "fixtures", "runner", "bundles", id);
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
