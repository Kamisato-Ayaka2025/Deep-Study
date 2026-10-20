using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
namespace StudyHarness.Runner;
// PSEC supplies the execution boundary (ADR-005). The legacy per-run profile
// remains for host bookkeeping only; it is not the executed process identity.
internal sealed class SandboxProfile : IDisposable
{
    private SandboxProfile(string name, nint sid, SecurityIdentifier identity) => (Name, Sid, Identity) = (name, sid, identity);
    public string Name { get; }
    public nint Sid { get; }
    public SecurityIdentifier Identity { get; }
    public string OriginalLocalAppData
    {
        get
        {
            Marshal.ThrowExceptionForHR(Native.GetAppContainerFolderPath(Identity.Value, out var path));
            try { return Marshal.PtrToStringUni(path) ?? throw new IOException("Missing container folder"); }
            finally { Marshal.FreeCoTaskMem(path); }
        }
    }
    public static SandboxProfile Create(string name)
    {
        var result = Native.CreateAppContainerProfile(name, name, "Study Harness isolated run", 0, 0, out var sid);
        Marshal.ThrowExceptionForHR(result);
        var profile = new SandboxProfile(name, sid, new SecurityIdentifier(sid));
        try
        {
            profile.DenyOriginalProfileWrites();
            return profile;
        }
        catch { profile.Dispose(); throw; }
    }
    internal void DenyOriginalProfileWrites()
    {
        // Environment redirection is not authorization. The profile created above still has
        // OS-granted write access outside work. Revoke that access only for this fresh SID,
        // throughout its own new profile; never touch another profile or shared dependencies.
        var local = new DirectoryInfo(OriginalLocalAppData);
        var root = local.Parent ?? throw new IOException("Missing profile root");
        if (!local.Name.Equals("AC", StringComparison.OrdinalIgnoreCase) ||
            !root.Name.Equals(Name, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Unexpected container profile layout");
        var directories = root.EnumerateDirectories("*", SearchOption.AllDirectories).Prepend(root).ToArray();
        foreach (var directory in directories)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Unexpected link in new container profile");
            var acl = directory.GetAccessControl();
            // Windows also grants a profile-derived capability SID (S-1-15-3-*).
            // Remove all explicit package/capability grants on this private directory,
            // not just the app-container SID. Host user/system access stays intact.
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, false, typeof(SecurityIdentifier)))
                if (rule.IdentityReference.Value.StartsWith("S-1-15-", StringComparison.Ordinal))
                    acl.PurgeAccessRules(rule.IdentityReference);
            acl.AddAccessRule(new FileSystemAccessRule(Identity, FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(acl);
        }
    }
    // Grant the work directory: it is the only place the container may write, and the
    // container's own profile and temporary storage is redirected inside it (ContainerProfile).
    // The directory is also labelled low integrity, because an AppContainer token runs at low
    // integrity and the mandatory no-write-up policy would otherwise silently discard every
    // write it makes.
    public void AllowWorkRoot(string path)
    {
        var directory = new DirectoryInfo(path);
        var acl = directory.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(Identity, FileSystemRights.Modify,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
        if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;OICI;NW;;;LW)", 1, out var descriptor, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Low integrity label");
        try
        {
            if (!Native.SetFileSecurity(path, Native.LabelSecurityInformation, descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Low integrity label");
        }
        finally { Native.LocalFree(descriptor); }
    }
    public void Dispose()
    {
        Native.DeleteAppContainerProfile(Name);
        if (Sid != 0) Native.FreeSid(Sid);
    }
}
internal sealed record SandboxSpec(string WorkRoot, string Executable, string[] Arguments, string OutputPath, string ErrorPath, int TimeoutMilliseconds,
    Dictionary<string, string>? Environment = null, string[]? ReadRoots = null);
// Where an AppContainer's own profile paths have to land.
//
// The operating system rewrites these when it creates the process: the LOCALAPPDATA it was
// given becomes <it>\Packages\<lowercased profile name>\AC and TEMP/TMP become that directory's
// Temp subdirectory, whatever the environment block asked for. Nothing creates the tree. Left
// alone, the rewrite puts a compiler's intermediate files in the real user profile, outside the
// dedicated run directory. Handing the container a LOCALAPPDATA that is itself inside the work
// directory moves the whole chain inside it; the tree is created after the work root has been
// granted and low-integrity labelled, so the container inherits the right to write there.
internal static class ContainerProfile
{
    public static string LocalAppData(string work) => Path.Combine(work, "appdata");
    public static string Temp(string work, string profileName) =>
        Path.Combine(LocalAppData(work), "Packages", profileName.ToLowerInvariant(), "AC", "Temp");
    public static string Prepare(string work, string profileName)
    {
        var temp = Temp(work, profileName);
        Directory.CreateDirectory(temp);
        return temp;
    }
}
// A running sandboxed process plus the job that owns its whole tree. Disposing or killing
// this object destroys every descendant: the process is created suspended and assigned to
// the job before it is resumed, so it cannot spawn anything outside the job.
internal sealed class SandboxedProcess : IDisposable
{
    private readonly List<string> journal;
    private nint job, process, thread;
    private PsecEnvironment? securityEnvironment;
    private BoundedOutput? outputCapture;
    private bool exited;
    private readonly object gate = new();
    private SandboxedProcess(nint job, nint process, nint thread, List<string> journal) =>
        (this.job, this.process, this.thread, this.journal) = (job, process, thread, journal);
    public IReadOnlyList<string> Journal => journal;
    public uint ProcessId { get; private init; }
    public bool TimedOut { get; private set; }
    public int ExitCode { get; private set; }
    public static SandboxedProcess Start(SandboxProfile profile, SandboxSpec spec, List<string> journal)
    {
        var work = Path.GetFullPath(spec.WorkRoot);
        var executable = Path.GetFullPath(spec.Executable);
        Directory.CreateDirectory(work);
        nint attributes = 0, capabilities = 0, policy = 0, handleList = 0, inherit = 0, environment = 0;
        nint stdIn = 0, stdOut = 0, stdErr = 0, job = 0;
        var info = new Native.ProcessInformation();
        PsecEnvironment? psec = null;
        BoundedOutput? capture = null;
        try
        {
            inherit = Marshal.AllocHGlobal(Marshal.SizeOf<Native.SecurityAttributes>());
            Marshal.StructureToPtr(new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), InheritHandle = 1 }, inherit, false);
            stdIn = Open("NUL", false, inherit, 0x80000000);
            capture = new BoundedOutput(Path.GetFullPath(spec.OutputPath), Path.GetFullPath(spec.ErrorPath));
            stdOut = capture.OutputHandle;
            stdErr = capture.ErrorHandle;
            var handles = new[] { stdIn, stdOut, stdErr };
            const int attributeCount = 2;
            nuint size = 0;
            Native.InitializeProcThreadAttributeList(0, attributeCount, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Check(Native.InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref size), journal, "InitializeProcThreadAttributeList");
            psec = new PsecEnvironment(work, spec.ReadRoots ?? []);
            capabilities = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(capabilities, psec.Handle);
            Check(Native.UpdateProcThreadAttribute(attributes, 0, 0x20023, capabilities,
                (nuint)IntPtr.Size, 0, 0), journal, "PSEC");
            handleList = Marshal.AllocHGlobal(IntPtr.Size * handles.Length);
            Marshal.Copy(handles, 0, handleList, handles.Length);
            Check(Native.UpdateProcThreadAttribute(attributes, 0, Native.HandleListAttribute, handleList,
                (nuint)(IntPtr.Size * handles.Length), 0, 0), journal, "HandleList");
            var startup = new Native.StartupInfoEx
            {
                // STARTF_USESTDHANDLES is 0x100. With 0x1 the OS silently ignores all three
                // standard handles and hands the child the console, so every child write
                // disappears without an error and the toolchain fails with empty output.
                StartupInfo = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x00000100, StdIn = stdIn, StdOut = stdOut, StdErr = stdErr },
                Attributes = attributes
            };
            job = Native.CreateJobObject(0, null);
            Check(job != 0, journal, "CreateJobObject");
            var limits = new Native.ExtendedLimits
            {
                Basic = new Native.BasicLimits { LimitFlags = Native.JobObjectLimitKillOnJobClose | 0x8 | 0x100 | 0x200, ActiveProcessLimit = 16 },
                ProcessMemory = 1024u * 1024 * 1024,
                JobMemory = (nuint)2 * 1024 * 1024 * 1024
            };
            Check(Native.SetInformationJobObject(job, Native.JobObjectExtendedLimitInformation, ref limits,
                (uint)Marshal.SizeOf<Native.ExtendedLimits>()), journal, "SetInformationJobObject");
            var cpu = new Native.CpuLimits { Flags = 5, Rate = 2500 }; // enabled, hard cap, 25%
            Check(Native.SetInformationJobObject(job, 15, ref cpu, (uint)Marshal.SizeOf<Native.CpuLimits>()), journal, "CpuRateLimit");
            var command = new StringBuilder(Quote(executable));
            foreach (var argument in spec.Arguments) command.Append(' ').Append(Quote(argument));
            var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Ambient variables a toolchain may look for are copied first, so the discovered
            // toolchain can correct them; the containment-owned ones are written last, so a
            // caller-supplied dictionary cannot redirect the temporary or profile chain.
            foreach (var name in new[] { "USERPROFILE", "APPDATA", "ProgramData", "SystemDrive", "ComSpec", "ProgramFiles", "ProgramFiles(x86)", "ALLUSERSPROFILE", "USERNAME", "USERDOMAIN" })
            {
                var value = System.Environment.GetEnvironmentVariable(name);
                if (value is not null) variables[name] = value;
            }
            foreach (var pair in spec.Environment ?? []) variables[pair.Key] = pair.Value;
            var windows = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
            var temp = ContainerProfile.Prepare(work, profile.Name);
            variables["SystemRoot"] = windows;
            variables["WINDIR"] = windows;
            variables.TryAdd("PATH", System.Environment.GetFolderPath(System.Environment.SpecialFolder.System));
            variables["LOCALAPPDATA"] = ContainerProfile.LocalAppData(work);
            variables["TEMP"] = temp;
            variables["TMP"] = temp;
            var block = string.Join('\0', variables.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
            environment = Marshal.StringToHGlobalUni(block);
            journal.Add($"container-temp={temp}");
            Check(Native.CreateProcess(executable, command, 0, 0, true,
                Native.ExtendedStartupInfoPresent | Native.CreateSuspended | Native.CreateNoWindow | 0x00000400, environment, work, ref startup, out info),
                journal, "CreateProcess");
            Check(Native.AssignProcessToJobObject(job, info.Process), journal, "AssignProcessToJobObject");
            // PSEC chooses a fresh profile. Prepare its redirected temp before resuming.
            journal.Add("psec-temp=" + PsecEnvironment.PrepareTemp(info.Process, work));
            if (Native.ResumeThread(info.Thread) == uint.MaxValue) Check(false, journal, "ResumeThread");
            var child = new SandboxedProcess(job, info.Process, info.Thread, journal)
            {
                ProcessId = info.ProcessId, securityEnvironment = psec, outputCapture = capture
            };
            capture.Start(child.Kill);
            capture = null;
            psec = null;
            job = 0;
            info = default;
            journal.Add($"sandbox-start pid={child.ProcessId} profile={profile.Name}");
            return child;
        }
        finally
        {
            if (info.Process != 0) Native.TerminateProcess(info.Process, 1);
            if (job != 0) Native.CloseHandle(job);
            if (info.Thread != 0) Native.CloseHandle(info.Thread);
            if (info.Process != 0) Native.CloseHandle(info.Process);
            if (stdIn != 0) Native.CloseHandle(stdIn);
            capture?.Dispose();
            if (attributes != 0) { Native.DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (capabilities != 0) Marshal.FreeHGlobal(capabilities);
            if (policy != 0) Marshal.FreeHGlobal(policy);
            if (handleList != 0) Marshal.FreeHGlobal(handleList);
            if (inherit != 0) Marshal.FreeHGlobal(inherit);
            if (environment != 0) Marshal.FreeHGlobal(environment);
            psec?.Dispose();
        }
    }
    // True when the process exited on its own within the window. False means it is still
    // running, which is the expected outcome for a program that never terminates.
    public bool WaitForExit(int milliseconds)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            lock (gate)
            {
                if (process == 0) return false;
                var wait = Native.WaitForSingleObject(process, (uint)Math.Min(50, Math.Max(0, milliseconds - timer.ElapsedMilliseconds)));
                if (wait == 0)
                {
                    Check(Native.GetExitCodeProcess(process, out var code), journal, "GetExitCodeProcess");
                    ExitCode = unchecked((int)code);
                    exited = true;
                    return true;
                }
                if (wait != Native.WaitTimeout) throw new Win32Exception(Marshal.GetLastWin32Error(), "WaitForSingleObject");
            }
            Thread.Yield();
        } while (timer.ElapsedMilliseconds < milliseconds);
        TimedOut = true;
        return false;
    }
    // Stops the whole tree, not just the compiler: the job owns every descendant.
    public void Kill()
    {
        lock (gate)
        {
        if (job == 0) return;
        journal.Add("sandbox-kill");
        Native.TerminateJobObject(job, 1);
        if (!exited && process != 0) Native.WaitForSingleObject(process, 15000);
        }
    }
    public void Dispose()
    {
        BoundedOutput? capture;
        lock (gate)
        {
        // Closing the job handle is the second half of the guarantee: even an abnormal exit
        // of the Runner destroys every process still in the job.
        if (job != 0) { Native.CloseHandle(job); job = 0; }
        if (thread != 0) { Native.CloseHandle(thread); thread = 0; }
        if (process != 0) { Native.CloseHandle(process); process = 0; }
        securityEnvironment?.Dispose(); securityEnvironment = null;
        capture = outputCapture; outputCapture = null;
        }
        // The job is closed before draining, and no process lock is held while a
        // pump may call Kill. Drain/flush before the session reads an artifact.
        capture?.Dispose();
        if (capture?.Failed == true)
        {
            ExitCode = 1;
            journal.Add("sandbox-output-limit-or-io-failure");
        }
    }
    private static nint Open(string path, bool truncate, nint securityAttributes, uint access)
    {
        var handle = Native.CreateFile(path, truncate ? access | 2u : access, 1, securityAttributes,
            truncate ? 2u : 3u, 0x80, 0);
        if (handle == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "Open standard handle");
        return handle;
    }
    private static void Check(bool ok, List<string> journal, string operation)
    {
        if (ok) return;
        var error = new Win32Exception(Marshal.GetLastWin32Error(), operation);
        journal.Add($"{operation}-failed native={error.NativeErrorCode}");
        throw error;
    }
    // Standard Windows argv quoting: a run of backslashes before a quote (including the
    // closing one) is doubled. A naive '"' + value + '"' would escape its own closing quote
    // for any argument ending in a backslash, merging two arguments into one.
    private static string Quote(string value)
    {
        var builder = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            if (character == '"') builder.Append('\\', backslashes * 2 + 1).Append('"');
            else { if (backslashes > 0) builder.Append('\\', backslashes); builder.Append(character); }
            backslashes = 0;
        }
        return builder.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct CpuLimits { public uint Flags, Rate; }
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetInformationJobObject(nint job, nuint informationClass, ref CpuLimits limits, uint size);
    public const uint ExtendedStartupInfoPresent = 0x00080000;
    public const uint CreateSuspended = 0x00000004;
    public const uint CreateNoWindow = 0x08000000;
    public const uint WaitTimeout = 258;
    public const uint JobObjectLimitKillOnJobClose = 0x2000;
    public const nuint JobObjectExtendedLimitInformation = 9;
    public const nuint SecurityCapabilitiesAttribute = 0x00020009;
    public const nuint AllApplicationPackagesPolicyAttribute = 0x0002000F;
    public const nuint HandleListAttribute = 0x00020002;
    public const uint LabelSecurityInformation = 0x00000010;
    [StructLayout(LayoutKind.Sequential)] public struct SecurityAttributes { public uint Length; public nint SecurityDescriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential)] public struct SecurityCapabilities { public nint AppContainerSid, Capabilities; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential)] public struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public ushort ShowWindow, ReservedSize; public nint ReservedBytes, StdIn, StdOut, StdErr;
    }
    [StructLayout(LayoutKind.Sequential)] public struct StartupInfoEx { public StartupInfo StartupInfo; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] public struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] public struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] public struct ExtendedLimits
    {
        public BasicLimits Basic; public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] public static extern int GetAppContainerFolderPath(string sid, out nint path);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] public static extern int CreateAppContainerProfile(string name, string displayName, string description, nint capabilities, uint count, out nint sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] public static extern int DeleteAppContainerProfile(string name);
    // Recovers the SID of a profile that already exists. Every AppContainer name maps to exactly
    // one SID, so an adopting Runner gets the identity the profile was created with.
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] public static extern int DeriveAppContainerSidFromAppContainerName(string name, out nint sid);
    [DllImport("advapi32.dll")] public static extern nint FreeSid(nint sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out nint descriptor, out uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool SetFileSecurity(string file, uint information, nint descriptor);
    [DllImport("kernel32.dll")] public static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnSize);
    [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    public static extern bool CreateProcess(string app, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    public static extern nint CreateFile(string file, uint access, uint share, nint securityAttributes, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool SetInformationJobObject(nint job, nuint informationClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateJobObject(nint job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool TerminateProcess(nint process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(nint handle);
}
