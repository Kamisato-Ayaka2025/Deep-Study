using System.Diagnostics;
namespace StudyHarness.Runner;
// A discovered toolchain, identified by a registered id. The host never supplies a compiler
// path: it names a toolchain id and the Runner resolves it locally, so a request cannot point
// the sandbox at an arbitrary executable.
internal sealed record Toolchain(string Id, string Compiler, string Header, Dictionary<string, string> Environment)
{
    // Only these discovered compiler variables enter the explicit Unicode environment block.
    // Three import libraries are linked into every bundle: Winsock and shell32, which the
    // fixtures need for their network probes and their wide command lines, and advapi32 for
    // the token queries that ask a process whether it is contained. MinGW links advapi32 by
    // default but naming it is harmless; MSVC does not, and its absence is a link error rather
    // than a warning. An unused system library costs nothing, and keeping the link line
    // identical for every bundle is what makes it impossible for a request to influence the
    // arguments. MSVC needs /link because /TC makes it treat any bare input as C source, and
    // MinGW needs the plain names rather than -municode, which selects a wWinMain startup that
    // a console program has no entry point for.
    public string[] ReadRoots => Id == "msvc"
        ? new[] { Path.GetDirectoryName(Compiler)! }.Concat(Paths("INCLUDE")).Concat(Paths("LIB")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        : new[] { "bin", "include", "lib", "libexec", "x86_64-w64-mingw32" }
            .Select(part => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Compiler)!, "..", part))).Where(Directory.Exists).ToArray();
    private IEnumerable<string> Paths(string key) => Environment.TryGetValue(key, out var value)
        ? value.Split(';', StringSplitOptions.RemoveEmptyEntries).Where(Directory.Exists).Select(Path.GetFullPath) : [];
    public string[] CompileArguments(string source, string output, string objectDirectory)
    {
        if (Id == "msvc") return ["/nologo", "/W4", "/WX", "/MT", "/TC", source, $"/Fe:{output}", $"/Fo:{objectDirectory}\\", $"/Fd:{objectDirectory}\\compile.pdb",
            .. Paths("INCLUDE").Select(path => "/I" + path), "/link", "ws2_32.lib", "shell32.lib", "advapi32.lib", "userenv.lib", "ole32.lib", .. Paths("LIB").Select(path => "/LIBPATH:" + path)];
        var specs = Path.Combine(objectDirectory, "linker.specs");
        File.WriteAllText(specs, "*linker:\nld\n\n");
        return ["-Wall", "-Wextra", "-Werror", "-static", source, "-o", output, "-lws2_32", "-lshell32", "-ladvapi32", "-luserenv", "-lole32",
            "-B" + Path.GetDirectoryName(Compiler)!.Replace('\\', '/') + "/", "-fno-use-linker-plugin", "-specs=" + specs];
    }
}
internal static class ToolchainDiscovery
{
    public static Toolchain? Discover(string id) => id switch
    {
        "msvc" => DiscoverMsvc(),
        "mingw" => DiscoverMinGW(),
        _ => null
    };
    private static Toolchain? DiscoverMsvc()
    {
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
        var vswhere = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        var installation = Capture(vswhere, "-latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath");
        if (string.IsNullOrWhiteSpace(installation)) return null;
        var root = installation.Trim();
        var vcvars = Path.Combine(root, "VC", "Auxiliary", "Build", "vcvars64.bat");
        if (!File.Exists(vcvars)) return null;
        var environment = ReadVendorEnvironment(vcvars);
        var compiler = NewestCompiler(Path.Combine(root, "VC", "Tools", "MSVC"));
        var header = environment.TryGetValue("INCLUDE", out var include)
            ? include.Split(';').Select(directory => Path.Combine(directory, "stdio.h")).FirstOrDefault(File.Exists)
            : null;
        if (environment.Count == 0 || compiler is null || header is null) return null;
        return new Toolchain("msvc", compiler, header, environment);
    }
    private static Toolchain? DiscoverMinGW()
    {
        var candidates = new List<string>();
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            if (directory.Length > 0) candidates.Add(Path.Combine(directory, "gcc.exe"));
        foreach (var root in KnownMinGWRoots()) candidates.Add(Path.Combine(root, "bin", "gcc.exe"));
        var compiler = candidates.FirstOrDefault(File.Exists);
        if (compiler is null) return null;
        var bin = Path.GetDirectoryName(compiler)!;
        var header = Path.Combine(bin, "..", "x86_64-w64-mingw32", "include", "stdio.h");
        if (!File.Exists(header)) return null;
        return new Toolchain("mingw", compiler, Path.GetFullPath(header), new Dictionary<string, string>
        {
            ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH")
        });
    }
    // Vendors install MinGW anywhere, so a fixed list is a starting point rather than a
    // contract. A missing toolchain is reported as TOOLCHAIN_MISSING, never worked around
    // by running the program without a compiler.
    private static IEnumerable<string> KnownMinGWRoots()
    {
        foreach (var drive in new[] { @"C:\", @"D:\", @"E:\" })
            foreach (var pattern in new[] { "Qt\\Tools\\mingw*", "msys64\\mingw64", "mingw64", "mingw32" })
            {
                string[] found;
                try { found = Directory.GetDirectories(drive, pattern); }
                catch (Exception) { continue; }
                foreach (var directory in found) yield return directory;
            }
    }
    private static string? NewestCompiler(string versions)
    {
        if (!Directory.Exists(versions)) return null;
        foreach (var directory in Directory.GetDirectories(versions).OrderByDescending(path => path, StringComparer.Ordinal))
        {
            var compiler = Path.Combine(directory, "bin", "HostX64", "x64", "cl.exe");
            if (File.Exists(compiler)) return compiler;
        }
        return null;
    }
    // The vendor setup script is the only supported way to learn a Visual Studio layout: it
    // resolves the Windows SDK, the UCRT and the MSVC include/lib sets. Only the installed
    // vendor script is executed; nothing generated is used as a command.
    private static Dictionary<string, string> ReadVendorEnvironment(string vcvars)
    {
        var environment = new Dictionary<string, string>();
        if (vcvars.Contains('"', StringComparison.Ordinal)) return environment;
        var output = Capture(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            $"/d /s /c \"\"{vcvars}\" >nul 2>nul && set\"", 120000);
        foreach (var line in output.Split('\n'))
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0) continue;
            var name = line[..separator].Trim();
            if (name is "INCLUDE" or "LIB" or "LIBPATH" or "PATH") environment[name] = line[(separator + 1)..].TrimEnd('\r');
        }
        return environment;
    }
    private static string Capture(string executable, string arguments, int timeoutMilliseconds = 30000)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        if (!process.Start()) return string.Empty;
        var output = process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        return process.WaitForExit(timeoutMilliseconds) ? output : string.Empty;
    }
}
