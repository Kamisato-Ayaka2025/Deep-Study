using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace StudyHarness.Runner;

// PSEC 1.1 wire layout: Microsoft MXC ProcessSecurityEnvironment.fbs,
// commit 6f6e43683877d68a43fdd1d6c201808c914c5685. Only fixed policy is encoded.
// Offsets are forward FlatBuffers offsets; omitted slots retain schema defaults.
internal sealed class PsecEnvironment : IDisposable
{
    public nint Handle { get; private set; }
    public PsecEnvironment(string work, string[] readRoots)
    {
        Marshal.ThrowExceptionForHR(IsProcessSecurityEnvironmentVersionSupported(1, out var supported, out var minor));
        if (supported == 0 || minor < 1) throw new PlatformNotSupportedException("PSEC 1.1 required");
        var bytes = Encode(work, readRoots);
        Marshal.ThrowExceptionForHR(CreateProcessSecurityEnvironment(bytes, (uint)bytes.Length, 0, out var handle));
        if (handle == 0) throw new IOException("PSEC returned no handle");
        Handle = handle;
    }

    internal static byte[] Encode(string work, string[] readRoots)
    {
        var wire = new WireBuilder();
        var root = wire.Table(9);
        wire.Set(0, root);
        wire.Set(root + 4, 0x00010001); // inline SchemaVersion { major=1, minor=1 }
        wire.Reference(root, 1, wire.String("registryRead,lpacCom"));
        wire.Reference(root, 4, wire.Strings([Path.GetFullPath(work)]));
        wire.Reference(root, 5, wire.Strings(readRoots.Prepend(Environment.GetFolderPath(Environment.SpecialFolder.System))
            .Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        wire.Reference(root, 6, wire.Strings(DriveInfo.GetDrives().Select(drive => drive.Name).ToArray()));
        var network = wire.Table(4);
        wire.Reference(root, 7, network);
        var egress = wire.Table(3); // default deny; no network capability, allow, proxy or peer
        wire.Reference(network, 1, egress);
        return wire.Bytes();
    }

    public static string PrepareTemp(nint process, string work)
    {
        if (!OpenProcessToken(process, 8, out var token)) throw new Win32Exception();
        nint info = 0;
        try
        {
            GetTokenInformation(token, 31, 0, 0, out var size);
            if (size == 0) throw new Win32Exception();
            info = Marshal.AllocHGlobal((int)size);
            if (!GetTokenInformation(token, 31, info, size, out _)) throw new Win32Exception();
            var sid = new SecurityIdentifier(Marshal.ReadIntPtr(info));
            Marshal.ThrowExceptionForHR(Native.GetAppContainerFolderPath(sid.Value, out var folder));
            try
            {
                var path = Marshal.PtrToStringUni(folder) ?? throw new IOException("Missing PSEC profile");
                var parent = Directory.GetParent(path) ?? throw new IOException("Missing PSEC profile parent");
                if (!Path.GetFileName(path).Equals("AC", StringComparison.OrdinalIgnoreCase) ||
                    !parent.Name.StartsWith("sandbox.", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Unexpected PSEC profile layout");
                return ContainerProfile.Prepare(work, parent.Name);
            }
            finally { Marshal.FreeCoTaskMem(folder); }
        }
        finally { if (info != 0) Marshal.FreeHGlobal(info); Native.CloseHandle(token); }
    }

    public void Dispose() { if (Handle != 0) { CloseProcessSecurityEnvironment(Handle); Handle = 0; } }

    private sealed class WireBuilder
    {
        private readonly MemoryStream stream = new();
        private readonly BinaryWriter writer;
        public WireBuilder() { writer = new BinaryWriter(stream, Encoding.UTF8, true); writer.Write(0); writer.Write("PSEC"u8); }
        private int Position => checked((int)stream.Position);
        private void Align() { while ((Position & 3) != 0) writer.Write((byte)0); }
        public int Table(int slots)
        {
            Align();
            var vtable = Position;
            writer.Write(checked((ushort)(4 + 2 * slots)));
            writer.Write(checked((ushort)(4 + 4 * slots)));
            for (var i = 0; i < slots; i++) writer.Write((ushort)0);
            Align();
            var table = Position;
            writer.Write(table - vtable);
            for (var i = 0; i < slots; i++) writer.Write(0);
            // Required version struct exists only on the root table.
            if (slots == 9) Slot(table, 0);
            return table;
        }
        public void Set(int offset, int value) { var end = stream.Position; stream.Position = offset; writer.Write(value); stream.Position = end; }
        private void Slot(int table, int slot)
        {
            var bytes = stream.GetBuffer();
            var vtable = table - BitConverter.ToInt32(bytes, table);
            var end = stream.Position;
            stream.Position = vtable + 4 + 2 * slot;
            writer.Write(checked((ushort)(4 + 4 * slot)));
            stream.Position = end;
        }
        public void Reference(int table, int slot, int target) { Slot(table, slot); var cell = table + 4 + 4 * slot; Set(cell, target - cell); }
        public int String(string value) { Align(); var offset = Position; var bytes = Encoding.UTF8.GetBytes(value); writer.Write(bytes.Length); writer.Write(bytes); writer.Write((byte)0); return offset; }
        public int Strings(string[] values)
        {
            Align(); var vector = Position; writer.Write(values.Length);
            foreach (var _ in values) writer.Write(0);
            for (var i = 0; i < values.Length; i++) { var target = String(values[i]); var cell = vector + 4 + i * 4; Set(cell, target - cell); }
            return vector;
        }
        public byte[] Bytes() => stream.ToArray();
    }

    [DllImport("processmodel.dll", ExactSpelling = true)] private static extern int IsProcessSecurityEnvironmentVersionSupported(uint major, out byte supported, out uint minor);
    [DllImport("processmodel.dll", ExactSpelling = true)] private static extern int CreateProcessSecurityEnvironment(byte[] spec, uint size, uint flags, out nint handle);
    [DllImport("processmodel.dll", ExactSpelling = true)] private static extern void CloseProcessSecurityEnvironment(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int kind, nint info, uint size, out uint needed);
}
