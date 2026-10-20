using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace StudyHarness.Runner;

// Feasibility harness only. It is deliberately not the production Runner: it takes a
// whole command line and an arbitrary grant list so that mechanism variants can be
// compared. The production Runner accepts registered resource ids instead.
internal sealed record SandboxSpec(
    string ProfileName,
    bool LowPrivilege,
    string Root,
    string[] ReadRoots,
    string WorkRoot,
    string SandboxExecutable,
    string[] Arguments,
    string? StandardInputPath,
    string StandardOutputPath,
    string StandardErrorPath,
    int TimeoutMilliseconds,
    string[]? ExtraReadSids = null,
    Dictionary<string, string>? Environment = null,
    bool UseHandleList = true,
    string[]? Capabilities = null,
    bool RestrictedPrimaryToken = false);

internal static class Sandbox
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint WaitTimeout = 258;
    private const uint WaitObject0 = 0;
    private const nuint JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const nuint SecurityCapabilitiesAttribute = 0x00020009;
    private const nuint AllApplicationPackagesPolicyAttribute = 0x0002000F;
    private const nuint HandleListAttribute = 0x00020002;
    // STARTF_USESTDHANDLES is 0x100, not 0x1 (that is STARTF_USESHOWWINDOW). Passing the
    // wrong bit makes the OS silently ignore hStdInput/hStdOutput/hStdError and hand the
    // child the console instead, so every child write disappears without an error.
    private const uint UseStdHandles = 0x00000100;
    private const nuint LowIntegrityLabel = 0x10;
    private const uint LabelSecurityInformation = 0x00000010;

    public static int Run(SandboxSpec spec, out SandboxResult result)
    {
        var root = Path.GetFullPath(spec.Root);
        var work = Path.GetFullPath(spec.WorkRoot);
        var executable = Path.GetFullPath(spec.SandboxExecutable);
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(work);
        var journal = new List<string>();
        nint sid = 0, attributes = 0, capabilities = 0, policy = 0, handleList = 0, inheritHandle = 0, environment = 0;
        var info = new ProcessInformation();
        nint job = 0, stdIn = 0, stdOut = 0, stdErr = 0;
        nint primaryToken = 0, restrictedToken = 0, restrictingSid = 0, restrictingSidBytes = 0;
        var profileCreated = false;
        var exitCode = 0;
        var capabilitySids = new List<nint>();
        nint capabilityEntries = 0;
        var readAcls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var name in spec.Capabilities ?? [])
            {
                Check(DeriveCapabilitySidsFromName(name, out var groups, out var groupCount, out var sids, out var sidCount), journal, "DeriveCapabilitySidsFromName");
                for (var i = 0; i < groupCount; i++) LocalFree(Marshal.ReadIntPtr(groups, i * IntPtr.Size));
                LocalFree(groups);
                for (var i = 0; i < sidCount; i++) capabilitySids.Add(Marshal.ReadIntPtr(sids, i * IntPtr.Size));
                LocalFree(sids);
            }
            // An empty capability set must present a null pointer, not a zero-byte allocation:
            // SECURITY_CAPABILITIES with CapabilityCount 0 and a non-null Capabilities pointer
            // makes CreateProcess fail with ERROR_INVALID_PARAMETER (87), which looks exactly
            // like a mechanism that cannot start a container at all.
            capabilityEntries = capabilitySids.Count == 0
                ? 0
                : Marshal.AllocHGlobal(capabilitySids.Count * Marshal.SizeOf<SidAndAttributes>());
            for (var i = 0; i < capabilitySids.Count; i++)
                Marshal.StructureToPtr(new SidAndAttributes { Sid = capabilitySids[i], Attributes = 4 },
                    capabilityEntries + i * Marshal.SizeOf<SidAndAttributes>(), false);
            Marshal.ThrowExceptionForHR(CreateAppContainerProfile(spec.ProfileName, spec.ProfileName,
                "Study Harness P1 feasibility probe", 0, 0, out sid));
            profileCreated = true;
            var identity = new SecurityIdentifier(sid);
            journal.Add($"profile={spec.ProfileName} sid={identity.Value} lowPrivilege={spec.LowPrivilege}");
            Grant(work, identity, FileSystemRights.Modify, journal);
            SetLowIntegrity(work);
            foreach (var readRoot in spec.ReadRoots)
            {
                var path = Path.GetFullPath(readRoot);
                readAcls.TryAdd(path, new DirectoryInfo(path).GetAccessControl()
                    .GetSecurityDescriptorSddlForm(AccessControlSections.Access));
                Grant(path, identity, FileSystemRights.ReadAndExecute, journal);
            }
            foreach (var extra in spec.ExtraReadSids ?? [])
            {
                var wellKnown = new SecurityIdentifier(extra);
                foreach (var readRoot in spec.ReadRoots)
                    Grant(Path.GetFullPath(readRoot), wellKnown, FileSystemRights.ReadAndExecute, journal);
            }

            inheritHandle = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityAttributes>());
            Marshal.StructureToPtr(new SecurityAttributes { Length = (uint)Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 },
                inheritHandle, false);
            var handles = new List<nint>();
            stdIn = OpenInheritable(spec.StandardInputPath is null ? null : Path.GetFullPath(spec.StandardInputPath), false, inheritHandle);
            stdOut = OpenInheritable(Path.GetFullPath(spec.StandardOutputPath), true, inheritHandle);
            stdErr = OpenInheritable(Path.GetFullPath(spec.StandardErrorPath), true, inheritHandle);
            handles.Add(stdIn);
            handles.Add(stdOut);
            handles.Add(stdErr);

            // The declared count must equal the number of attributes actually set.
            var attributeCount = spec.UseHandleList ? 3 : 2;
            var size = (nuint)0;
            InitializeProcThreadAttributeList(0, attributeCount, 0, ref size);
            attributes = Marshal.AllocHGlobal(checked((int)size));
            Check(InitializeProcThreadAttributeList(attributes, attributeCount, 0, ref size), journal, "InitializeProcThreadAttributeList");
            capabilities = Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());
            Marshal.StructureToPtr(new SecurityCapabilities { AppContainerSid = sid, Capabilities = capabilityEntries, Count = (uint)capabilitySids.Count }, capabilities, false);
            Check(UpdateProcThreadAttribute(attributes, 0, SecurityCapabilitiesAttribute, capabilities,
                (nuint)Marshal.SizeOf<SecurityCapabilities>(), 0, 0), journal, "SecurityCapabilities");
            policy = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(policy, spec.LowPrivilege ? 1 : 0);
            Check(UpdateProcThreadAttribute(attributes, 0, AllApplicationPackagesPolicyAttribute, policy, 4, 0, 0),
                journal, "AllApplicationPackagesPolicy");
            if (spec.UseHandleList)
            {
                handleList = Marshal.AllocHGlobal(IntPtr.Size * handles.Count);
                Marshal.Copy(handles.ToArray(), 0, handleList, handles.Count);
                Check(UpdateProcThreadAttribute(attributes, 0, HandleListAttribute, handleList,
                    (nuint)(IntPtr.Size * handles.Count), 0, 0), journal, "HandleList");
            }
            journal.Add($"startup-size={Marshal.SizeOf<StartupInfoEx>()} info-size={Marshal.SizeOf<StartupInfo>()} "
                + $"flags-offset={Marshal.OffsetOf<StartupInfo>("Flags")} stdout-offset={Marshal.OffsetOf<StartupInfo>("StdOut")} "
                + $"handles={stdIn:X},{stdOut:X},{stdErr:X}");

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = UseStdHandles,
                    StdIn = stdIn,
                    StdOut = stdOut,
                    StdErr = stdErr
                },
                Attributes = attributes
            };
            job = CreateJobObject(0, null);
            Check(job != 0, journal, "CreateJobObject");
            var limits = new ExtendedLimits { Basic = new BasicLimits { LimitFlags = JobObjectLimitKillOnJobClose } };
            Check(SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits,
                (uint)Marshal.SizeOf<ExtendedLimits>()), journal, "SetInformationJobObject");

            if (spec.Environment is not null) environment = BuildEnvironment(spec.Environment, journal);
            var command = new StringBuilder(Quote(executable));
            foreach (var argument in spec.Arguments) command.Append(' ').Append(Quote(argument));
            // Suspended start closes the spawn-before-job-assignment race.
            var flags = ExtendedStartupInfoPresent | CreateSuspended | CreateNoWindow | 0x00000400;
            if (spec.RestrictedPrimaryToken)
            {
                Check(OpenProcessToken(GetCurrentProcess(), 0x000B, out primaryToken), journal, "OpenProcessToken");
                GetTokenInformation(primaryToken, 29, out var hostContainer, 4, out _);
                GetTokenInformation(primaryToken, 11, 0, 0, out var restrictedSize);
                journal.Add($"host-appcontainer={hostContainer} host-restricted-buffer={restrictedSize}");
                var restriction = new SecurityIdentifier("S-1-5-21-" + Random.Shared.Next(1, int.MaxValue) + "-" +
                    Random.Shared.Next(1, int.MaxValue) + "-" + Random.Shared.Next(1, int.MaxValue) + "-1001");
                Grant(work, restriction, FileSystemRights.Modify, journal);
                foreach (var readRoot in spec.ReadRoots) Grant(Path.GetFullPath(readRoot), restriction, FileSystemRights.ReadAndExecute, journal);
                var bytes = new byte[restriction.BinaryLength];
                restriction.GetBinaryForm(bytes, 0);
                restrictingSidBytes = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, restrictingSidBytes, bytes.Length);
                restrictingSid = Marshal.AllocHGlobal(Marshal.SizeOf<SidAndAttributes>());
                Marshal.StructureToPtr(new SidAndAttributes { Sid = restrictingSidBytes }, restrictingSid, false);
                Check(CreateRestrictedToken(primaryToken, 1, 0, 0, 0, 0, 1, restrictingSid, out restrictedToken),
                    journal, "CreateRestrictedToken");
                Check(CreateProcessAsUser(restrictedToken, executable, command, 0, 0, true,
                    flags, environment, work, ref startup, out info), journal, "CreateProcessAsUser");
            }
            else Check(CreateProcess(executable, command, 0, 0, true,
                flags, environment, work, ref startup, out info), journal, "CreateProcess");
            Check(AssignProcessToJobObject(job, info.Process), journal, "AssignProcessToJobObject");
            if (ResumeThread(info.Thread) == uint.MaxValue) Check(false, journal, "ResumeThread");
            var wait = WaitForSingleObject(info.Process, (uint)spec.TimeoutMilliseconds);
            if (wait == WaitTimeout)
            {
                exitCode = -1;
                journal.Add($"observation-window-expired={spec.TimeoutMilliseconds}ms");
            }
            else if (wait != WaitObject0)
            {
                Check(false, journal, "WaitForSingleObject");
            }
            else
            {
                Check(GetExitCodeProcess(info.Process, out var code), journal, "GetExitCodeProcess");
                exitCode = unchecked((int)code);
            }
        }
        catch (Exception error)
        {
            exitCode = -2;
            journal.Add(error is Win32Exception native
                ? $"{native.Message} native={native.NativeErrorCode}"
                : error.Message);
        }
        finally
        {
            if (job != 0) CloseHandle(job); // KILL_ON_JOB_CLOSE tears down the whole tree.
            if (info.Process != 0) TerminateProcess(info.Process, 1);
            if (info.Thread != 0) CloseHandle(info.Thread);
            if (info.Process != 0) CloseHandle(info.Process);
            if (stdIn != 0) CloseHandle(stdIn);
            if (stdOut != 0) CloseHandle(stdOut);
            if (stdErr != 0) CloseHandle(stdErr);
            if (attributes != 0) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (capabilities != 0) Marshal.FreeHGlobal(capabilities);
            if (policy != 0) Marshal.FreeHGlobal(policy);
            if (handleList != 0) Marshal.FreeHGlobal(handleList);
            if (inheritHandle != 0) Marshal.FreeHGlobal(inheritHandle);
            if (environment != 0) Marshal.FreeHGlobal(environment);
            if (capabilityEntries != 0) Marshal.FreeHGlobal(capabilityEntries);
            if (restrictingSid != 0) Marshal.FreeHGlobal(restrictingSid);
            if (restrictingSidBytes != 0) Marshal.FreeHGlobal(restrictingSidBytes);
            if (restrictedToken != 0) CloseHandle(restrictedToken);
            if (primaryToken != 0) CloseHandle(primaryToken);
            foreach (var capabilitySid in capabilitySids) LocalFree(capabilitySid);
            if (sid != 0) FreeSid(sid);
            if (profileCreated)
            {
                var cleanup = DeleteAppContainerProfile(spec.ProfileName);
                journal.Add($"profile-cleanup={cleanup}");
            }
            // Probes must not leave permissions behind that contaminate later no-grant baselines.
            // Work directories are disposable; dependency DACLs are restored after all children die.
            foreach (var pair in readAcls.Reverse())
            {
                try
                {
                    var directory = new DirectoryInfo(pair.Key);
                    var acl = directory.GetAccessControl();
                    acl.SetSecurityDescriptorSddlForm(pair.Value, AccessControlSections.Access);
                    directory.SetAccessControl(acl);
                    journal.Add($"read-acl-restored={pair.Key}");
                }
                catch (Exception error)
                {
                    exitCode = -2;
                    journal.Add($"read-acl-restore-failed={pair.Key}: {error.Message}");
                }
            }
        }
        result = new SandboxResult(exitCode, journal);
        return exitCode;
    }

    // The child's TEMP/TMP and PATH must point into the dedicated run directory and the
    // probed toolchain, so the environment block is built explicitly instead of inherited.
    private static nint BuildEnvironment(Dictionary<string, string> variables, List<string> journal)
    {
        var builder = new StringBuilder();
        foreach (var pair in variables) builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        var bytes = Encoding.Unicode.GetBytes(builder.Append('\0').ToString());
        var block = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, block, bytes.Length);
        journal.Add($"environment-bytes={bytes.Length} tail={bytes[^4]:X2},{bytes[^3]:X2},{bytes[^2]:X2},{bytes[^1]:X2} first={Encoding.Unicode.GetString(bytes, 0, Math.Min(24, bytes.Length)).Replace('\0', '|')}");
        return block;
    }

    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { public nint Sid; public uint Attributes; }
    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool DeriveCapabilitySidsFromName(string name, out nint groups, out int groupCount, out nint sids, out int sidCount);

    // Standard Windows argv quoting: a backslash run before a quote (including the closing
    // one) is doubled. Paths such as \\.\pipe\name end with a backslash, so the naive
    // '"' + value + '"' form would escape its own closing quote and merge two arguments.
    private static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("Invalid argument.");
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

    private static nint OpenInheritable(string? path, bool truncate, nint securityAttributes)
    {
        // The handle must be created inheritable: PROC_THREAD_ATTRIBUTE_HANDLE_LIST rejects
        // non-inheritable handles with ERROR_INVALID_PARAMETER.
        var handle = CreateFile(path ?? "NUL", truncate ? (0x40000000 | 0x00000002) : 0x80000000,
            0x00000001, securityAttributes, path is null ? 3u : (truncate ? 2u : 3u), 0x00000080, 0);
        if (handle == -1) throw new Win32Exception(Marshal.GetLastWin32Error(), "Open standard handle");
        return handle;
    }

    private static void Grant(string path, SecurityIdentifier sid, FileSystemRights rights, List<string> journal)
    {
        var directory = new DirectoryInfo(path);
        var acl = directory.GetAccessControl();
        // The volume may grant the owning user Modify through Authenticated Users, which lacks
        // WRITE_OWNER needed for the integrity label. This only widens a probe/work directory.
        if (rights == FileSystemRights.Modify)
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(sid, rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
        journal.Add($"grant={rights} path={path} sid={sid.Value}");
    }

    private static void SetLowIntegrity(string path)
    {
        Check(ConvertStringSecurityDescriptorToSecurityDescriptor("S:(ML;OICI;NW;;;LW)", 1, out var descriptor, out _),
            [], "ConvertStringSecurityDescriptor");
        try
        {
            if (!SetFileSecurity(path, LabelSecurityInformation, descriptor))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetFileSecurity");
        }
        finally { LocalFree(descriptor); }
    }

    private static void Check(bool ok, List<string> journal, string operation)
    {
        if (!ok)
        {
            var error = new Win32Exception(Marshal.GetLastWin32Error(), operation);
            journal.Add($"{operation} failed native={error.NativeErrorCode}");
            throw error;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public uint Length; public nint SecurityDescriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityCapabilities { public nint AppContainerSid, Capabilities; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfo
    {
        public int Size; public nint Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCount, YCount, Fill, Flags;
        public ushort ShowWindow, ReservedSize; public nint ReservedBytes, StdIn, StdOut, StdErr;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public nint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public nint Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
    {
        public BasicLimits Basic; public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int CreateAppContainerProfile(string name, string displayName, string description, nint capabilities, uint count, out nint sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeleteAppContainerProfile(string name);
    [DllImport("advapi32.dll")] private static extern nint FreeSid(nint sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out nint descriptor, out uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetFileSecurity(string file, uint information, nint descriptor);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")]
    private static extern bool CreateProcess(string app, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern nint CreateFile(string file, uint access, uint share, nint securityAttributes, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(nint job, nuint informationClass, ref ExtendedLimits limits, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(nint process, uint code);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(nint process, uint access, out nint token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int information, out int value, uint length, out uint returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(nint token, int information, nint value, uint length, out uint returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool CreateRestrictedToken(nint token, uint flags, uint disableCount, nint disable, uint deleteCount, nint delete, uint restrictCount, nint restrict, out nint result);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessAsUserW")]
    private static extern bool CreateProcessAsUser(nint token, string app, StringBuilder command, nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInformation info);
}

internal sealed record SandboxResult(int ExitCode, IReadOnlyList<string> Journal);
