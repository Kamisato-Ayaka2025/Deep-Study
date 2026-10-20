using StudyHarness.Runner;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Xunit;

namespace StudyHarness.Tests.Runner;

public sealed class SafeRunFilesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "studyharness-safe-read-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public void ReadText_RegularArtifact_IsBounded()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "artifact.txt");
        File.WriteAllText(path, "safe-output");
        Assert.Equal("safe-output", SafeRunFiles.ReadText(root, path));
        Assert.Throws<IOException>(() => SafeRunFiles.ReadText(root, path, 4));
    }
    [Fact]
    public void ReadText_JunctionToOutside_RejectsBeforeReading()
    {
        var work = Path.Combine(root, "work");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(outside);
        var file = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(file, "synthetic-secret");
        var link = Path.Combine(work, "link");
        Junction.Create(link, outside);
        Assert.Throws<IOException>(() => SafeRunFiles.ReadText(work, Path.Combine(link, "sentinel.txt")));
        Assert.Throws<IOException>(() => SafeRunFiles.ReadText(work, file));
    }
    [Fact]
    public void ReadText_HardLinkToOutside_RejectsDespiteInBoundaryFinalPath()
    {
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        var outside = Path.Combine(root, "sentinel.txt");
        var alias = Path.Combine(work, "alias.txt");
        File.WriteAllText(outside, "synthetic-secret");
        if (!CreateHardLink(alias, outside, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Assert.Equal("synthetic-secret", File.ReadAllText(alias));
        Assert.Throws<IOException>(() => SafeRunFiles.ReadText(work, alias));
        Assert.Equal("synthetic-secret", File.ReadAllText(outside));
    }
    [Fact]
    public void OutputCapture_PreplantedHardLink_RejectsWithoutOverwritingTarget()
    {
        Directory.CreateDirectory(root);
        var outside = Path.Combine(root, "sentinel.txt");
        var alias = Path.Combine(root, "stdout.txt");
        File.WriteAllText(outside, "synthetic-secret");
        if (!CreateHardLink(alias, outside, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Assert.Throws<IOException>(() => new BoundedOutput(alias, Path.Combine(root, "stderr.txt")));
        Assert.Equal("synthetic-secret", File.ReadAllText(outside));
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string name, string existing, nint attributes);
    [Fact]
    public async Task PinnedAncestors_RejectConcurrentRenameUntilReleased()
    {
        var parent = Path.Combine(root, "parent");
        var work = Path.Combine(parent, "work");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "artifact.txt"), "safe");
        using (SafeRunFiles.PinDirectory(work))
        {
            await Task.Run(() =>
            {
                Assert.Throws<IOException>(() => Directory.Move(work, work + "-moved"));
                Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-moved"));
            });
            Assert.Equal("safe", SafeRunFiles.ReadText(work, Path.Combine(work, "artifact.txt")));
        }
        Directory.Move(parent, parent + "-moved");
        Directory.Move(parent + "-moved", parent);
    }
    [Theory]
    [InlineData("artifact.txt:secret")]
    [InlineData("..\\sentinel.txt")]
    [InlineData("artifact.txt.")]
    [InlineData("artifact.txt ")]
    [InlineData("NUL")]
    [InlineData("CON.txt")]
    [InlineData("COM¹")]
    [InlineData("CONIN$")]
    public void AmbiguousNames_AreRejectedForReadsAndHostWrites(string name)
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, name);
        Assert.Throws<IOException>(() => SafeRunFiles.ReadText(root, path));
        Assert.Throws<IOException>(() => SafeRunFiles.CreateNew(root, path));
    }
    [Fact]
    public void JunctionAsRoot_RejectsReadAndCreateWithoutTouchingTarget()
    {
        var outside = Path.Combine(root, "outside");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(outside);
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(outside, "sentinel.txt"), "secret");
        var link = Path.Combine(work, "link");
        Junction.Create(link, outside);
        Assert.Throws<IOException>(() => SafeRunFiles.ReadText(link, Path.Combine(link, "sentinel.txt")));
        Assert.Throws<IOException>(() => SafeRunFiles.CreateNew(link, Path.Combine(link, "new.txt")));
        Assert.False(File.Exists(Path.Combine(outside, "new.txt")));
        Assert.Equal("secret", File.ReadAllText(Path.Combine(outside, "sentinel.txt")));
    }
    [Fact]
    public async Task PinnedDirectory_RejectsInPlaceReparseMutation()
    {
        var work = Path.Combine(root, "work");
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(outside);
        using (SafeRunFiles.PinDirectory(work))
            await Task.Run(() => Assert.Throws<Win32Exception>(() => Junction.Create(work, outside)));
        Assert.False((File.GetAttributes(work) & FileAttributes.ReparsePoint) != 0);
    }
    [Fact]
    public void HostCreate_DoesNotOverwriteAndReadRejectsDirectory()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "artifact.txt");
        using (var file = SafeRunFiles.CreateNew(root, path)) file.Write("safe"u8);
        Assert.Equal("safe", SafeRunFiles.ReadText(root.ToUpperInvariant(), path.ToUpperInvariant()));
        Assert.Throws<IOException>(() => SafeRunFiles.CreateNew(root, path));
        Assert.Equal("safe", File.ReadAllText(path));
        var directory = Path.Combine(root, "directory.txt");
        Directory.CreateDirectory(directory);
        Assert.ThrowsAny<Exception>(() => SafeRunFiles.ReadText(root, directory));
        Assert.Throws<ArgumentOutOfRangeException>(() => SafeRunFiles.ReadText(root, path, 0));
    }
    public void Dispose()
    {
        // Remove the link itself while its synthetic target still exists. Do not
        // recurse through a link or depend on directory enumeration order.
        var link = Path.Combine(root, "work", "link");
        if (Directory.Exists(link)) Directory.Delete(link, false);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
