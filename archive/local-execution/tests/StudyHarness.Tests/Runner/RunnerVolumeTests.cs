using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using StudyHarness.Runner;
using Xunit;
using Xunit.Abstractions;

namespace StudyHarness.Tests.Runner;

[Collection("runner")]
[Trait("Gate", "PSEC")]
public sealed class RunnerVolumeTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("msvc")]
    [InlineData("mingw")]
    public void FixedVolume_PsecCompilerAndDiskFull_DoNotEscapeToHost(string toolchainId)
    {
        var repository = FindRepository();
        var volumeRoot = Path.Combine(repository, ".tools", "p1-volume");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(volumeRoot, "manifest.json")));
        var mount = Path.Combine(volumeRoot, "mount") + Path.DirectorySeparatorChar;
        var volumeName = new StringBuilder(128);
        Assert.True(GetVolumeNameForVolumeMountPoint(mount, volumeName, 128), "Dedicated mount missing; refuse host fallback");
        Assert.Equal(manifest.RootElement.GetProperty("volumeUniqueId").GetString(), volumeName.ToString());
        Assert.True(GetDiskFreeSpaceEx(mount, out _, out var capacity, out var free));
        Assert.InRange(capacity, 1UL << 30, 2UL << 30);
        Assert.True(free > 512UL * 1024 * 1024, "Dedicated test volume needs 512MiB free; never expand it automatically");
        var evidence = Path.Combine(repository, "artifacts", "review-P1-volume", Guid.NewGuid().ToString("N"));
        var drive = DriveInfo.GetDrives().Single(candidate =>
        {
            var name = new StringBuilder(128);
            return GetVolumeNameForVolumeMountPoint(candidate.Name, name, 128) && name.ToString() == volumeName.ToString();
        });
        var work = Path.Combine(drive.Name, "probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        Directory.CreateDirectory(work);
        var sentinel = Path.Combine(evidence, "outside.txt");
        File.WriteAllText(sentinel, "synthetic-unrelated-secret");
        File.Copy(Path.Combine(repository, "probes", "PsecNative", "disk.c"), Path.Combine(work, "main.c"));
        using var profile = SandboxProfile.Create("studyharness.volume." + Guid.NewGuid().ToString("N"));
        profile.AllowWorkRoot(work);
        var toolchain = ToolchainDiscovery.Discover(toolchainId);
        Assert.NotNull(toolchain);
        var readRoots = toolchain.ReadRoots;
        var program = Path.Combine(work, "program.exe");
        Run(new SandboxSpec(work, toolchain.Compiler, toolchain.CompileArguments(Path.Combine(work, "main.c"), program, work),
            Path.Combine(evidence, "compile-out.txt"), Path.Combine(evidence, "compile-err.txt"), 240000, toolchain.Environment, readRoots));
        Run(new SandboxSpec(work, program, [sentinel], Path.Combine(evidence, "run-out.txt"),
            Path.Combine(evidence, "run-err.txt"), 240000, ReadRoots: readRoots));
        var report = File.ReadAllText(Path.Combine(evidence, "run-out.txt"));
        output.WriteLine(evidence);
        output.WriteLine(report);
        Assert.Contains("diskError=112 outsideReadError=5 bytes=", report);
        Assert.Equal("synthetic-unrelated-secret", File.ReadAllText(sentinel));
        Assert.False(File.Exists(Path.Combine(work, "capacity-probe.bin")));
        Assert.Equal(2147484160L, new FileInfo(Path.Combine(volumeRoot, "p1-work.vhd")).Length);

        void Run(SandboxSpec spec)
        {
            List<string> journal = [];
            using var child = SandboxedProcess.Start(profile, spec, journal);
            var exited = child.WaitForExit(spec.TimeoutMilliseconds);
            child.Dispose();
            File.WriteAllLines(spec.OutputPath + ".journal", journal);
            Assert.True(exited, "Bounded probe timed out: " + spec.OutputPath);
            Assert.True(child.ExitCode == 0, "Probe failed: " + spec.OutputPath + " code=" + child.ExitCode);
        }
    }
    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "StudyHarness.slnx"))) return directory.FullName;
        throw new IOException("Repository missing");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string path, StringBuilder name, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
}
