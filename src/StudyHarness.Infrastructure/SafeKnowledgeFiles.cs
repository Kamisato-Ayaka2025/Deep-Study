using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StudyHarness.Infrastructure;

// Open once, validate the opened object, then read that same handle. A pathname
// pre-check followed by File.ReadAllText would permit a link replacement race.
internal static class SafeKnowledgeFiles
{
    internal sealed class ProtectedSource(IDisposable directories, FileStream stream) : IDisposable
    {
        public FileStream Stream { get; } = stream;
        public void Dispose() { Stream.Dispose(); directories.Dispose(); }
    }
    internal static ProtectedSource OpenRead(string path, long maximumBytes)
    {
        var full = Path.GetFullPath(path);
        var directories = PinDirectory(Path.GetDirectoryName(full)!);
        try
        {
            var handle = Open(full, 0x80000000, 1, 0, 3, 0x00200000, 0);
            try
            {
                Validate(handle, false);
                if (!FinalPath(handle).Equals(@"\\?\" + full, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe source alias");
                var stream = new FileStream(handle, FileAccess.Read);
                if (stream.Length > maximumBytes) { stream.Dispose(); throw new IOException("Source exceeds size budget"); }
                return new(directories, stream);
            }
            catch { handle.Dispose(); throw; }
        }
        catch { directories.Dispose(); throw; }
    }
    public static IDisposable PinExistingFile(string path)
    {
        var file = Open(path, 0x80000000, 3, 0, 3, 0x00200000, 0);
        try
        {
            Validate(file, false);
            if (!FinalPath(file).Equals(@"\\?\" + Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) throw new IOException("Unsafe library file");
            return file;
        }
        catch { file.Dispose(); throw; }
    }
    public static byte[] ReadBytes(string path)
    {
        var full = Path.GetFullPath(path);
        using var directories = PinDirectory(Path.GetDirectoryName(full)!);
        using var file = Open(full, 0x80000000, 1, 0, 3, 0x00200000, 0);
        Validate(file, false);
        if (!FinalPath(file).Equals(@"\\?\" + full, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Source alias is not permitted");
        using var stream = new FileStream(file, FileAccess.Read);
        if (stream.Length > 32 * 1024 * 1024) throw new IOException("Source exceeds 32 MiB limit");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }
    // Keep every directory component open without FILE_SHARE_DELETE. Reject
    // junctions at every level, not just the final file, and retain the handles
    // through the operation so an ancestor cannot be renamed after validation.
    internal static IDisposable PinDirectory(string root)
    {
        var full = ValidatePath(root);
        var pins = new DirectoryPins();
        try
        {
            var current = Path.GetPathRoot(full)!;
            Pin(current);
            foreach (var component in full[current.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                Pin(current);
            }
            return pins;
            void Pin(string path)
            {
                // FILE_LIST_DIRECTORY participates in sharing checks. Access=0
                // is only a metadata query and did not block rename in tests.
                var handle = Open(path, 1, 1, 0, 3, 0x02200000, 0);
                pins.Handles.Add(handle);
                Validate(handle, true);
                if (!FinalPath(handle).TrimEnd('\\').Equals((@"\\?\" + path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Directory alias is not permitted");
            }
        }
        catch { pins.Dispose(); throw; }
    }
    internal static FileStream CreateNew(string root, string path)
    {
        using var directories = PinDirectory(root);
        ValidateLeaf(root, path);
        // CREATE_NEW refuses existing hard links, symlinks and ordinary files.
        // The returned file handle denies deletion and survives path changes.
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }
    private static string ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length < 3 || !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' || path[2] != '\\' || path[2..].Contains(':') || path.Contains('/') || path.Contains('~'))
            throw new IOException("Only canonical local disk paths are permitted");
        foreach (var part in path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new IOException("Ambiguous path component");
        return Path.GetFullPath(path).TrimEnd('\\') + (path.Length == 3 ? "\\" : "");
    }
    private static void ValidateLeaf(string root, string path)
    {
        var full = ValidatePath(path);
        if (!string.Equals(Path.GetDirectoryName(full), ValidatePath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Only direct run artifacts are permitted");
        var name = Path.GetFileName(full);
        if (name.Length > 128 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-'))
            throw new IOException("Unsupported artifact name");
        var stem = Path.GetFileNameWithoutExtension(full).Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3]))
            throw new IOException("Device names are not permitted");
    }
    private sealed class DirectoryPins : IDisposable
    {
        public List<SafeFileHandle> Handles { get; } = [];
        public void Dispose() { for (var i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
    }
    private static void Validate(SafeFileHandle handle, bool directory)
    {
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & 0x400) != 0 || directory != ((info.Attributes & 0x10) != 0) || (!directory && info.Links != 1))
            throw new IOException("Linked or invalid run artifact");
    }
    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot resolve run artifact handle");
        return buffer.ToString();
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle Open(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
}
