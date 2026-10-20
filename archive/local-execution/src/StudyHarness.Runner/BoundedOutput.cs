using System.IO.Pipes;

namespace StudyHarness.Runner;

// Only pipe handles enter the sandbox. The host owns the disk handles and a
// shared stdout/stderr budget; exceeding either stream's remaining budget kills
// the whole job, even if the program has already printed a success marker.
internal sealed class BoundedOutput : IDisposable
{
    internal const int ByteLimit = 65536;
    private readonly AnonymousPipeServerStream stdout = new(PipeDirection.In, HandleInheritability.Inheritable);
    private readonly AnonymousPipeServerStream stderr = new(PipeDirection.In, HandleInheritability.Inheritable);
    private readonly FileStream output;
    private readonly FileStream error;
    private readonly object gate = new();
    private Task[] pumps = [];
    private int remaining = ByteLimit;
    private bool failed;
    public bool Failed { get { lock (gate) return failed; } }
    public nint OutputHandle => stdout.ClientSafePipeHandle.DangerousGetHandle();
    public nint ErrorHandle => stderr.ClientSafePipeHandle.DangerousGetHandle();

    public BoundedOutput(string outputPath, string errorPath)
    {
        // A previous compiler may have planted these names. Never overwrite or
        // follow an existing file/link on behalf of the sandbox.
        try
        {
            output = SafeRunFiles.CreateNew(Path.GetDirectoryName(outputPath)!, outputPath);
            try { error = SafeRunFiles.CreateNew(Path.GetDirectoryName(errorPath)!, errorPath); }
            catch { output.Dispose(); throw; }
        }
        catch { stdout.Dispose(); stderr.Dispose(); throw; }
    }
    public void Start(Action stopJob)
    {
        stdout.DisposeLocalCopyOfClientHandle();
        stderr.DisposeLocalCopyOfClientHandle();
        pumps = [Task.Run(() => Pump(stdout, output, stopJob)), Task.Run(() => Pump(stderr, error, stopJob))];
    }
    private void Pump(Stream input, Stream destination, Action stopJob)
    {
        try
        {
            var buffer = new byte[4096];
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                bool exceeded;
                lock (gate)
                {
                    if (failed) return;
                    var accepted = Math.Min(count, remaining);
                    destination.Write(buffer, 0, accepted);
                    remaining -= accepted;
                    exceeded = count > accepted;
                    if (exceeded) failed = true;
                }
                if (exceeded) { stopJob(); return; }
            }
        }
        catch (Exception)
        {
            lock (gate) failed = true;
            stopJob();
        }
    }
    // Caller first terminates/closes the job: descendants may retain the pipe
    // even when the root process exits. Never wait for EOF while they are alive.
    public void Dispose()
    {
        Task.WaitAll(pumps);
        stdout.Dispose(); stderr.Dispose(); output.Dispose(); error.Dispose();
    }
}
