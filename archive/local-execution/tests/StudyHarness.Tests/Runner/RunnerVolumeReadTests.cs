using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using StudyHarness.Runner;
using Xunit;
using Xunit.Abstractions;

namespace StudyHarness.Tests.Runner;

[Collection("runner")]
[Trait("Gate", "PSEC")]
public sealed class RunnerVolumeReadTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("msvc")]
    [InlineData("mingw")]
    public void GrantedMountedFile_CanOpenBeforeFinalPathConversion(string toolchainId)
    {
        var repository = RunnerTestEnvironment.RepositoryRoot;
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(repository, ".tools/p1-volume/manifest.json")));
        var id = manifest.RootElement.GetProperty("volumeUniqueId").GetString();
        var drive = DriveInfo.GetDrives().Single(d => {
            var name = new StringBuilder(128);
            return GetVolumeNameForVolumeMountPoint(d.Name, name, 128) && name.ToString() == id;
        });
        Assert.True(drive.IsReady);
        Assert.InRange(drive.TotalSize, 1L << 30, 2L << 30);
        var evidence = Path.Combine(repository, "artifacts/psec-20261017-012", toolchainId + "-" + Guid.NewGuid().ToString("N"));
        var work = Path.Combine(evidence, "work");
        var volumeWork = Path.Combine(drive.Name, "read-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work); Directory.CreateDirectory(volumeWork);
        var own = Path.Combine(work, "own.txt"); var volumeFile = Path.Combine(volumeWork, "own.txt");
        var outside = Path.Combine(evidence, "outside.txt");
        File.WriteAllText(own, "synthetic"); File.WriteAllText(volumeFile, "synthetic"); File.WriteAllText(outside, "outside");
        Assert.Equal("synthetic", File.ReadAllText(volumeFile)); // ordinary host positive control
        var guidFile = id + Path.GetFileName(volumeWork) + "\\own.txt";
        Assert.Equal("synthetic", File.ReadAllText(guidFile));
        File.Copy(Path.Combine(repository, "probes/PsecNative/volume-read.c"), Path.Combine(work, "main.c"));
        var toolchain = ToolchainDiscovery.Discover(toolchainId); Assert.NotNull(toolchain);
        var program = Path.Combine(work, "program.exe");
        using var profile = SandboxProfile.Create("studyharness.volumeread." + Guid.NewGuid().ToString("N"));
        profile.AllowWorkRoot(work); profile.AllowWorkRoot(volumeWork);
        Run(new(work, toolchain.Compiler, toolchain.CompileArguments(Path.Combine(work, "main.c"), program, work),
            Path.Combine(evidence, "compile.txt"), Path.Combine(evidence, "compile-err.txt"), 240000, toolchain.Environment, toolchain.ReadRoots));
        var reports = new List<string>();
        foreach (var volumeIsWork in new[] { false, true })
        {
            var label = volumeIsWork ? "volume-work" : "ordinary-work";
            var runWork = volumeIsWork ? volumeWork : work;
            var readRoots = toolchain.ReadRoots.Concat(volumeIsWork ? [work] : new[] { volumeWork }).ToArray();
            var spec = new SandboxSpec(runWork, program, [own, volumeFile, guidFile, outside],
                Path.Combine(evidence, label + ".txt"), Path.Combine(evidence, label + "-err.txt"), 30000, ReadRoots: readRoots);
            Run(spec); var report = File.ReadAllText(spec.OutputPath); reports.Add(report);
            output.WriteLine(label + "\n" + report);
        }
        output.WriteLine(evidence);
        Assert.Equal("outside", File.ReadAllText(outside));
        // Keep each boundary as an assertion: diagnostics never turn denial into acceptance.
        foreach (var report in reports)
        {
            Assert.Contains("path1 open=0 read=0 bytes=1", report);
            Assert.Contains("path4 open=5", report);
            Assert.Contains("path2 open=0 read=0 bytes=1", report);
            Assert.Contains("path3 open=0 read=0 bytes=1", report);
        }
        void Run(SandboxSpec spec)
        {
            List<string> journal = [];
            using var child = SandboxedProcess.Start(profile, spec, journal);
            var ended = child.WaitForExit(spec.TimeoutMilliseconds); child.Dispose();
            File.WriteAllLines(spec.OutputPath + ".journal", journal);
            Assert.True(ended, "bounded read probe timed out");
            Assert.True(child.ExitCode == 0, "probe launch/compile failed: " + spec.OutputPath + " exit=" + child.ExitCode);
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string path, StringBuilder name, uint size);
}
