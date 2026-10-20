using System.Diagnostics;
using StudyHarness.Application;
using StudyHarness.Contracts;
using StudyHarness.Infrastructure;
namespace StudyHarness.HostProbe;
// The harness a person operates while doing P1 manual acceptance (docs/acceptance/P1.md).
//
// It drives the production host -- the same RunnerHost, the same job, the same protocol the
// desktop application will use -- so what the operator watches is the product's own behaviour
// rather than a test double's. Its additions are the Chinese prompts, a keypress to stop, and
// an after-the-fact process count.
//
// The stop is a keypress rather than a button because P1 delivers the Runner prototype: the
// learner-facing surface that would own such a button belongs to a later phase, so this gate
// is operated through the host instead. Whether that satisfies the manual step is the
// reviewer's call, and docs/acceptance/P1.md records it as a question rather than a pass.
internal static class ManualHarness
{
    private static readonly string[] Bundles = ["hello", "escape", "loop"];
    public static int Run(string[] args)
    {
        var runner = Value(args, "--runner");
        var dataRoot = Value(args, "--data-root");
        var toolchain = Value(args, "--toolchain") ?? "msvc";
        var bundle = Value(args, "--bundle") ?? "loop";
        // Stops at the observation window instead of waiting for a keypress. It exists so the
        // harness's own stop path can be exercised without a person, leaving the keypress
        // itself as the only part manual acceptance has to supply.
        var autoStop = Array.IndexOf(args, "--auto-stop") >= 0;
        if (runner is null || dataRoot is null)
        {
            Console.Error.WriteLine("USAGE_INVALID");
            return 64;
        }
        if (Array.IndexOf(Bundles, bundle) < 0)
        {
            Console.Error.WriteLine("BUNDLE_INVALID");
            return 64;
        }
        return RunAsync(runner, dataRoot, toolchain, bundle, autoStop).GetAwaiter().GetResult();
    }
    private static async Task<int> RunAsync(string runner, string dataRoot, string toolchain, string bundle, bool autoStop)
    {
        // The console's own output encoding is left alone on purpose. Switching it to UTF-8
        // would re-code-page the shared console, and the PowerShell process that launched this
        // one keeps writing in the console's original codepage -- so the operator would see one
        // half of the screen correct and the other half unreadable. The Chinese prompts render
        // as long as both sides agree, which they do when neither changes it.
        Console.WriteLine($"隔离运行人工验证：工具链 {toolchain}，夹具 {bundle}");
        Console.WriteLine($"数据目录：{dataRoot}");
        var opened = await new RunnerHost(runner, dataRoot).OpenAsync();
        if (!opened.Ok)
        {
            Console.WriteLine($"无法建立隔离会话：{opened.Error?.Code}。隔离不可用时不会以普通进程执行。");
            return 65;
        }
        var session = opened.Value!;
        var exit = 0;
        try
        {
            var baseline = session.ActiveProcessCount;
            // A baseline rather than a constant: the job also owns the Runner, and Windows may
            // start a console host for it, so the starting count varies between machines and
            // between runs. What must hold is that it comes back to this number.
            Console.WriteLine($"会话已建立。作业内进程数基线为 {baseline}（运行结束后应回到该值）。");
            var runId = Guid.NewGuid();
            var started = await session.StartAsync(RunnerStart.For(runId, toolchain, bundle, Suite(bundle), RunnerPurpose.Run));
            if (!started.Ok)
            {
                Console.WriteLine($"运行请求被拒：{started.Error?.Code}");
                return 66;
            }
            Console.WriteLine("运行已受理，以下为逐条回复（caseId / 是否通过 / 原始错误号）：");
            while (true)
            {
                var reply = await Read(session, TimeSpan.FromMinutes(6));
                if (reply is null)
                {
                    Console.WriteLine("通道被关闭，本次运行没有给出终态回复。");
                    return 67;
                }
                Console.WriteLine("  " + Describe(reply));
                if (reply.Kind == RunnerReplyKind.RunFinished)
                {
                    if (!reply.Succeeded) exit = 68;
                    break;
                }
                if (reply.CaseId != "observation-window-exceeded") continue;
                // The program is still running, which is this fixture's whole point: the
                // observation window is not a timeout, so nothing stops it on its own.
                Console.WriteLine();
                if (autoStop)
                {
                    Console.WriteLine("程序仍在运行（这是预期结果）。--auto-stop：直接停止，不等按键。");
                }
                else
                {
                    // The stop is the operator's, so the harness has to be attached to a
                    // console: with redirected input there is nobody to press the key, and
                    // reporting a stop nobody performed would be worse than refusing.
                    if (Console.IsInputRedirected)
                    {
                        Console.WriteLine("程序仍在运行，但输入被重定向，无法接受停止按键。请在交互式控制台中运行。");
                        exit = 70;
                        break;
                    }
                    Console.WriteLine("程序仍在运行（这是预期结果）。按 S 停止本次运行，按 Q 结束会话但不停止。");
                    var key = Console.ReadKey(true).Key;
                    Console.WriteLine(key.ToString());
                    if (key != ConsoleKey.S) break;
                }
                var stopped = await session.StopAsync(runId);
                Console.WriteLine(stopped.Ok ? "停止已确认：" + Describe(stopped.Value!) : $"停止未确认：{stopped.Error?.Code}");
                if (!stopped.Ok) exit = 69;
                // Back to the baseline means the whole tree went: the compiler, the program and
                // the descendant the program spawned itself, not merely the process started.
                var after = await WaitForBaseline(session, baseline, TimeSpan.FromSeconds(30));
                Console.WriteLine($"停止后作业内进程数：{after}（基线 {baseline}）");
                if (after != baseline)
                {
                    Console.WriteLine("停止后进程数没有回到基线：整树未完全退出。");
                    exit = 71;
                }
                break;
            }
        }
        finally
        {
            await session.DisposeAsync();
        }
        // After the session is gone, what the machine still has running. The desktop
        // application is expected to leave the same zero.
        Console.WriteLine($"会话已关闭。遗留 program 进程：{Count("program")}，遗留 StudyHarness.Runner 进程：{Count("StudyHarness.Runner")}。");
        return exit;
    }
    private static string Suite(string bundle) => bundle switch
    {
        "hello" => "smoke",
        "escape" => "isolation",
        _ => "stop"
    };
    private static async Task<RunnerReply?> Read(IRunnerSession session, TimeSpan budget)
    {
        using var deadline = new CancellationTokenSource(budget);
        return await session.ReadAsync(deadline.Token);
    }
    // Terminating a job is not instantaneous, so the count is waited for rather than sampled
    // once, and the value actually reached is what gets printed.
    private static async Task<int> WaitForBaseline(IRunnerSession session, int baseline, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && session.ActiveProcessCount != baseline) await Task.Delay(250);
        return session.ActiveProcessCount;
    }
    private static int Count(string name)
    {
        try { return Process.GetProcessesByName(name).Length; }
        catch (Exception) { return -1; }
    }
    private static string Describe(RunnerReply reply)
    {
        var text = $"{reply.Kind} seq={reply.Sequence} ok={reply.Succeeded}";
        if (reply.CaseId is not null) text += $" case={reply.CaseId}";
        if (reply.NativeError is not null) text += $" native={reply.NativeError}";
        if (reply.Error is not null) text += $" error={reply.Error}";
        return text;
    }
    private static string? Value(string[] args, string name)
    {
        for (var index = 0; index + 1 < args.Length; index++)
            if (args[index] == name && args[index + 1].Length > 0) return args[index + 1];
        return null;
    }
}
