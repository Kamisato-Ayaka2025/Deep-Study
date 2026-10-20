using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using StudyHarness.Contracts;
namespace StudyHarness.Tests.Runner;
// Shared setup for the P1 Runner tests. Everything runs against a real Runner process, a real
// AppContainer and a real toolchain: nothing here is a stub, because a stubbed boundary would
// prove nothing about the boundary.
internal static class RunnerTestEnvironment
{
    public static string RepositoryRoot { get; } = FindRepositoryRoot();
    public static string Configuration { get; } = FindConfiguration();
    public static string RunnerPath { get; } = Path.Combine(RepositoryRoot, "src", "StudyHarness.Runner", "bin", Configuration, "net10.0-windows", "StudyHarness.Runner.exe");
    // The throwaway host the abnormal-exit check kills. It is a separate process on purpose:
    // a host cannot observe what the system does when it dies.
    public static string HostProbePath { get; } = Path.Combine(RepositoryRoot, "tests", "StudyHarness.HostProbe", "bin", Configuration, "net10.0-windows", "StudyHarness.HostProbe.exe");
    public static string EnsureDataRoot(string name)
    {
        // Runtime data never goes into the source tree, not even for a test.
        var path = Path.Combine(Path.GetTempPath(), "studyharness-p1", name, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    public static void DeleteDataRoot(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "StudyHarness.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository root not found");
    }
    private static string FindConfiguration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        while (directory.Parent is not null && !string.Equals(directory.Parent.Name, "bin", StringComparison.OrdinalIgnoreCase))
            directory = directory.Parent;
        return directory.Name;
    }
}
// A deliberately raw host: it speaks the wire protocol by hand, without the production host's
// validation, so a test can present a bad secret, a stale protocol version and an
// unregistered identifier. A client built on the production types could not make those
// mistakes, and those mistakes are exactly what must not be able to produce a pass.
internal sealed class RawRunnerHost : IDisposable
{
    // Longer than the Runner's own compile budget, so a slow first compile of the other
    // toolchain cannot be mistaken for a silent Runner.
    private const int ReadTimeoutMilliseconds = 300000;
    private readonly Process process;
    private readonly NamedPipeServerStream pipe;
    private readonly StreamReader reader;
    public RawRunnerHost(string pipeName, string secret, string dataRoot)
    {
        pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var start = new ProcessStartInfo(RunnerTestEnvironment.RunnerPath)
        {
            Arguments = "--pipe " + pipeName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        start.Environment["STUDYHARNESS_DATA_ROOT"] = dataRoot;
        process = new Process { StartInfo = start };
        process.Start();
        process.BeginErrorReadLine();
        process.StandardInput.WriteLine(secret);
        process.StandardInput.Flush();
        process.StandardInput.Close();
        if (!pipe.WaitForConnectionAsync().Wait(TimeSpan.FromSeconds(60))) throw new TimeoutException("Runner did not connect");
        reader = new StreamReader(pipe, Encoding.UTF8);
    }
    // Returns the next reply as the Runner wrote it, so assertions see the wire shape and not
    // a type the test itself chose.
    public string Send(string frame)
    {
        Write(frame);
        return Read();
    }
    // Writing and reading separately is required while a run is active: the run's own frames
    // interleave with the answer to the frame just sent, so "next frame is my answer" is only
    // true when nothing is running.
    public void Write(string frame)
    {
        var payload = Encoding.UTF8.GetBytes(frame + "\n");
        pipe.WriteAsync(payload).AsTask().Wait(TimeSpan.FromSeconds(30));
        pipe.FlushAsync().Wait(TimeSpan.FromSeconds(30));
    }
    public string Read()
    {
        var pending = reader.ReadLineAsync();
        // A silent Runner is a failure, never a wait: an unimplemented reply must not look
        // like a slow machine.
        if (!pending.Wait(TimeSpan.FromMilliseconds(ReadTimeoutMilliseconds))) throw new TimeoutException("Runner did not reply");
        return pending.Result ?? throw new InvalidOperationException("Runner closed the channel");
    }
    public RunnerReply Next() => JsonSerializer.Deserialize<RunnerReply>(Read(), Wire.Options)!;
    public RunnerReply Reply(string frame) => JsonSerializer.Deserialize<RunnerReply>(Send(frame), Wire.Options)!;
    public void Dispose()
    {
        try { pipe.Dispose(); } catch (Exception) { }
        try { if (!process.WaitForExit(20000)) process.Kill(true); } catch (Exception) { }
        process.Dispose();
    }
    public static StartRun Start(string toolchain, string bundle, string suite, RunnerPurpose purpose = RunnerPurpose.Run) =>
        new(RunnerResources.SandboxProfiles[0], bundle, toolchain, suite, purpose);
    public static string Frame(Guid runId, RunnerRequestKind kind, string secret, StartRun? start = null,
        int protocolVersion = RunnerProtocol.Version, int schemaVersion = 1) =>
        JsonSerializer.Serialize(new RunnerRequest(runId, kind, secret, start,
            kind == RunnerRequestKind.StopRun ? new StopRun() : null, protocolVersion, schemaVersion), Wire.Options);
}
