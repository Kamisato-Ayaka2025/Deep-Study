using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace StudyHarness.Tests.Integration;
// A directory junction, used to plant a link from inside the run directory to a directory
// outside it.
//
// A junction rather than Directory.CreateSymbolicLink because a symbolic link needs
// SeCreateSymbolicLinkPrivilege or Developer Mode, which a stock machine does not grant, while
// a junction needs nothing. That also makes the junction the shape an unprivileged process
// would actually plant, so it is the one worth measuring. Both are reparse points and the
// boundary resolves the final path either way.
internal static class Junction
{
    private const uint FsctlSetReparsePoint = 0x000900A4;
    private const uint IoReparseTagMountPoint = 0xA0000003;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private static readonly nint InvalidHandle = -1;
    public static void Create(string link, string target)
    {
        var substitute = @"\??\" + Path.GetFullPath(target);
        var print = Path.GetFullPath(target);
        var substituteLength = (ushort)(substitute.Length * 2);
        var printLength = (ushort)(print.Length * 2);
        // The header is the tag and two lengths; the payload is the four name offsets and
        // lengths followed by both NUL-terminated names.
        var pathBufferLength = substituteLength + 2 + printLength + 2;
        var reparseDataLength = 8 + pathBufferLength;
        var buffer = new byte[reparseDataLength + 8];
        BitConverter.GetBytes(IoReparseTagMountPoint).CopyTo(buffer, 0);
        BitConverter.GetBytes((ushort)reparseDataLength).CopyTo(buffer, 4);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 6);
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 8);
        BitConverter.GetBytes(substituteLength).CopyTo(buffer, 10);
        BitConverter.GetBytes((ushort)(substituteLength + 2)).CopyTo(buffer, 12);
        BitConverter.GetBytes(printLength).CopyTo(buffer, 14);
        Encoding.Unicode.GetBytes(substitute).CopyTo(buffer, 16);
        Encoding.Unicode.GetBytes(print).CopyTo(buffer, 16 + substituteLength + 2);
        Directory.CreateDirectory(link);
        // The empty directory is opened without following reparse points, which is what lets the
        // reparse point be written onto it rather than onto whatever it already points at.
        var handle = CreateFile(link, GenericWrite, ShareAll, 0, OpenExisting, OpenReparsePoint | BackupSemantics, 0);
        if (handle == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error(), "Open junction directory");
        try
        {
            if (!DeviceIoControl(handle, FsctlSetReparsePoint, buffer, buffer.Length, 0, 0, out _, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Set reparse point");
        }
        finally { CloseHandle(handle); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern nint CreateFile(string file, uint access, uint share, nint attributes, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(nint device, uint control, byte[] input, int inputSize, nint output, int outputSize, out int returned, nint overlapped);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
