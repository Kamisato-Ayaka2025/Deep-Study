using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
namespace StudyHarness.HostProbe;
// A throwaway host process used only by the P1 acceptance check for "an abnormal host exit
// leaves nothing running".
//
// It has to be a separate process: the guarantee under test is what the operating system does
// when a host dies without unwinding, and the host that is supposed to die cannot observe its
// own death. This process opens a runner session, starts a program that never terminates on
// its own, announces that the program is running, and then waits to be killed.
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Contains("--credential-status")) return CredentialStatus.Run(Value(args, "--data-root"));
        if (args.Contains("--p4-seed") || args.Contains("--p4-real")) return P4Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p3-seed") || args.Contains("--p3-real")) return P3Harness.Run(args).GetAwaiter().GetResult();
        if (args.Contains("--p2-seed") || args.Contains("--p2-real")) return P2Harness.Run(args).GetAwaiter().GetResult();
        // Two modes in one project: the throwaway host the cleanup check kills, and the harness
        // a person drives during manual acceptance. They share the launch plumbing that has to
        // match the production host, and neither is part of the shipped application.
        if (Array.IndexOf(args, "--interactive") >= 0) return ManualHarness.Run(args);
        var runner = Value(args, "--runner");
        var dataRoot = Value(args, "--data-root");
        var marker = Value(args, "--marker");
        var toolchain = Value(args, "--toolchain") ?? "msvc";
        if (runner is null || dataRoot is null || marker is null)
        {
            Console.Error.WriteLine("USAGE_INVALID");
            return 64;
        }
        return RunAsync(runner, dataRoot, marker, toolchain).GetAwaiter().GetResult();
    }
    private static async Task<int> RunAsync(string runner, string dataRoot, string marker, string toolchain)
    {
        var opened = await new RunnerHost(runner, dataRoot).OpenAsync();
        if (!opened.Ok)
        {
            Console.Error.WriteLine(opened.Error?.Code.ToString() ?? "OPEN_FAILED");
            return 65;
        }
        await using var session = opened.Value!;
        var runId = Guid.NewGuid();
        var started = await session.StartAsync(RunnerStart.For(runId, toolchain, "loop", "stop", RunnerPurpose.Run));
        if (!started.Ok)
        {
            Console.Error.WriteLine(started.Error?.Code.ToString() ?? "START_FAILED");
            return 66;
        }
        var work = Path.Combine(dataRoot, "runs", runId.ToString("N"), "work");
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (!File.Exists(Path.Combine(work, "loop-started.txt")) && DateTime.UtcNow < deadline)
            await Task.Delay(250);
        if (!File.Exists(Path.Combine(work, "loop-started.txt")))
        {
            Console.Error.WriteLine("PROGRAM_NOT_STARTED");
            return 67;
        }
        await File.WriteAllTextAsync(marker, runId.ToString("N"));
        // The program and its descendant are running. From here the process is killed without
        // unwinding, which is what a crash or a "end task" in Task Manager looks like.
        await Task.Delay(Timeout.Infinite);
        return 0;
    }
    private static string? Value(string[] args, string name)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (args[index] == name && args[index + 1].Length > 0) return args[index + 1];
        return null;
    }
}
