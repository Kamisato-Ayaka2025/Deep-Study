# P1 isolation mechanism probe (developer-only, superseded by verify.ps1 -Phase P1).
#
# Purpose: compare AppContainer launch variants against the P1 hard gate so the
# isolation ADR can be based on measurements instead of assumptions. It only uses
# synthetic sentinels and controlled listeners inside this run's evidence directory.
#
# Every check reports raw Win32/Winsock error numbers. A run that cannot even reach
# the fixture is recorded as blocked, never as pass.
param(
    [string]$MsvcSetup,
    [string]$Gcc,
    [string[]]$Variants = @('lpac-grant', 'lpac-nogrant', 'appcontainer-grant')
)
. "$PSScriptRoot/environment.ps1"
Set-Location $RepoRoot
$runId = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)
$out = Join-Path $RepoRoot "artifacts/acceptance/P1-probe/$runId"
New-Item -ItemType Directory -Force $out | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$summary = @{ phase = 'P1'; purpose = 'mechanism-selection'; status = 'running'; checks = @(); startedAt = [DateTimeOffset]::UtcNow.ToString('o') }
function Save-Checkpoint {
    $summary.checks = $checks.ToArray()
    $temporary = Join-Path $out 'summary.tmp'
    $summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination (Join-Path $out 'summary.json') -Force
}
function Invoke-Native([string]$Name, [string]$Executable, [string[]]$Arguments) {
    # Native stderr becomes a terminating error while $ErrorActionPreference is Stop, so
    # lower it around the call and capture both streams explicitly.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & $Executable @Arguments 2>&1 | ForEach-Object { $_.ToString() }; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    $output | Set-Content -LiteralPath (Join-Path $out "$Name.log") -Encoding UTF8
    if ($code -ne 0) { throw "$Name failed (exit $code), see $Name.log" }
}
function Get-Toolchains {
    if (-not $MsvcSetup) {
        $cl = (Get-Command cl.exe -ErrorAction Stop).Source
        $versionRoot = [IO.Directory]::GetParent($cl).Parent.Parent.Parent.FullName
        $vcRoot = [IO.Directory]::GetParent($versionRoot).Parent.Parent.FullName
        $MsvcSetup = Join-Path $vcRoot 'Auxiliary/Build/vcvars64.bat'
    }
    if (-not (Test-Path -LiteralPath $MsvcSetup)) { throw 'MSVC environment script not found; provide -MsvcSetup.' }
    if ($MsvcSetup.Contains('"')) { throw 'Invalid MSVC setup path' }
    # Only the installed vendor setup script is executed, never generated build commands.
    # The setup script also writes to stderr (e.g. a missing vswhere); $ErrorActionPreference
    # is Stop here, so route both streams into a file instead of through PowerShell.
    $envFile = Join-Path $env:TEMP 'p1-vcvars-env.txt'
    & cmd.exe /d /s /c ('"' + $MsvcSetup + '" >nul 2>nul && set > "' + $envFile + '"')
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $envFile)) { throw 'MSVC vendor environment discovery failed' }
    $lines = Get-Content -LiteralPath $envFile -Encoding UTF8
    $env = @{}
    foreach ($line in $lines) {
        if ($line -match '^([^=]+)=(.*)$' -and $matches[1] -in @('INCLUDE', 'LIB', 'LIBPATH', 'PATH', 'WindowsSdkDir', 'VCToolsInstallDir')) {
            $env[$matches[1]] = $matches[2]
            [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
        }
    }
    if (-not $Gcc) { $Gcc = (Get-Command gcc.exe -ErrorAction Stop).Source }
    $msvcCl = (Get-Command cl.exe -ErrorAction Stop).Source
    $msvcRead = @(Split-Path $msvcCl) + @($env.INCLUDE.Split(';') + $env.LIB.Split(';') | Where-Object { $_ -and (Test-Path -LiteralPath $_) })
    # stdio.h lives in the Windows SDK ucrt include, not in the VC include directory.
    $msvcInclude = ($env.INCLUDE.Split(';') | Where-Object { $_ -and (Test-Path -LiteralPath (Join-Path $_ 'stdio.h')) } | Select-Object -First 1)
    $mingwRoot = [IO.Directory]::GetParent([IO.Directory]::GetParent($Gcc).FullName).FullName
    $mingwRead = @('bin', 'lib', 'libexec', 'include', 'x86_64-w64-mingw32') | ForEach-Object { Join-Path $mingwRoot $_ } | Where-Object { Test-Path -LiteralPath $_ }
    @{
        MSVC  = @{ Cl = $msvcCl; Gcc = $null; Read = $msvcRead; Header = (Join-Path $msvcInclude 'stdio.h'); Environment = $env }
        MinGW = @{ Cl = $null; Gcc = $Gcc; Read = $mingwRead; Header = (Join-Path $mingwRoot 'x86_64-w64-mingw32/include/stdio.h'); Environment = @{} }
    }
}
function New-Scenario([string]$Root, [string]$Toolchain) {
    New-Item -ItemType Directory -Force $Root, (Join-Path $Root 'bin'), (Join-Path $Root 'work') | Out-Null
}
Save-Checkpoint
Write-Output "P1 mechanism probe: $out"
try {
    Invoke-Native 'probe-build' $Dotnet @('build', 'probes/StudyHarness.IsolationProbe', '-c', 'Release', '-m:1')
    $probe = Join-Path $RepoRoot 'probes/StudyHarness.IsolationProbe/bin/Release/net10.0-windows/StudyHarness.IsolationProbe.dll'
    $toolchains = Get-Toolchains
    $outside = Join-Path $out 'outside'
    New-Item -ItemType Directory -Force $outside | Out-Null
    Set-Content -LiteralPath (Join-Path $outside 'sentinel.txt') -Value 'synthetic P1 sentinel; not user data' -Encoding UTF8

    # Controlled listeners. Nothing outside this run may be contacted.
    $loopback = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $loopback.Start()
    $port = $loopback.LocalEndpoint.Port
    $lanAddress = (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -notlike '127.*' -and $_.AddressState -eq 'Preferred' } |
        Select-Object -First 1 -ExpandProperty IPAddress)
    if (-not $lanAddress) { $lanAddress = '127.0.0.1' }
    $lan = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Parse($lanAddress), $port)
    $lan.Start()
    $udp = [Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new([Net.IPAddress]::Parse($lanAddress), 0))
    $udpPort = $udp.Client.LocalEndPoint.Port
    $pipeName = 'StudyHarness.Probe.' + [Guid]::NewGuid().ToString('N')
    $pipe = [IO.Pipes.NamedPipeServerStream]::new($pipeName, [IO.Pipes.PipeDirection]::InOut, 1)
    $summary.endpoints = @{ loopbackPort = $port; lanAddress = $lanAddress; udpPort = $udpPort; pipeName = $pipeName }

    foreach ($toolchain in $toolchains.Keys) {
        $tool = $toolchains[$toolchain]
        $fixtureRoot = Join-Path $out "$toolchain/fixture"
        New-Item -ItemType Directory -Force (Join-Path $fixtureRoot 'bin') | Out-Null
        $fixture = Join-Path $fixtureRoot 'bin/capability-probe.exe'
        $source = Join-Path $RepoRoot 'fixtures/runner/capability-probe.c'
        if ($toolchain -eq 'MSVC') {
            Invoke-Native "$toolchain-fixture-compile" $tool.Cl @('/nologo', '/W4', '/WX', '/MT', $source, "/Fe:$fixture", "/Fo:$(Join-Path $fixtureRoot 'bin/fixture.obj')", 'ws2_32.lib', 'advapi32.lib')
        } else {
            Invoke-Native "$toolchain-fixture-compile" $tool.Gcc @('-Wall', '-Wextra', '-Werror', '-static', '-municode', $source, '-o', $fixture, '-lws2_32')
        }

        foreach ($variant in $Variants) {
            $name = "$toolchain/$variant"
            $root = Join-Path $out $name
            New-Scenario $root $toolchain
            $work = Join-Path $root 'work'
            $junction = Join-Path $work 'escape'
            # Host-created junction pointing outside the dedicated root.
            New-Item -ItemType Junction -Path $junction -Target $outside -ErrorAction SilentlyContinue | Out-Null
            $childReport = Join-Path $work 'child.json'
            $report = Join-Path $work 'probe.json'
            $readRoots = @($fixtureRoot) + $(if ($variant -like '*nogrant*') { @() } else { $tool.Read })
            $spec = @{
                profileName   = 'StudyHarness.Probe.' + [Guid]::NewGuid().ToString('N')
                lowPrivilege  = ($variant -like 'lpac*')
                root          = $root
                readRoots     = $readRoots
                workRoot      = $work
                sandboxExecutable = $fixture
                arguments     = @(
                    (Join-Path $outside 'sentinel.txt'),
                    (Join-Path $outside 'escape-write.txt'),
                    (Join-Path $junction 'escape-write.txt'),
                    ('\\.\pipe\' + $pipeName),
                    "$port", $lanAddress, "$udpPort", 'studyharness.invalid.example',
                    $fixture, $childReport, $tool.Header, $report
                )
                standardOutputPath = (Join-Path $root 'stdout.txt')
                standardErrorPath  = (Join-Path $root 'stderr.txt')
                timeoutMilliseconds = 60000
                extraReadSids       = [string[]]$(if ($variant -like '*arap*') { @('S-1-15-2-2') } else { @() })
            }
            $specPath = Join-Path $root 'spec.json'
            $resultPath = Join-Path $root 'launch.json'
            $spec | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $specPath -Encoding UTF8
            & $Dotnet $probe --spec $specPath $resultPath *> (Join-Path $root 'launcher.log')
            $launch = if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
            $observed = if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
            # Listener-side evidence: a blocked fixture must leave the controlled targets untouched.
            $arrivals = @{ tcpPending = $lan.Pending(); udpPending = $udp.Available; node = $env:COMPUTERNAME }
            $checks.Add(@{
                id       = "probe/$name"
                status   = $(if ($observed) { 'observed' } else { 'blocked' })
                launch   = $launch
                result   = $observed
                arrivals = $arrivals
                evidence = $name
            })
            Save-Checkpoint
        }

        # The decisive compiler test: compile and run inside the container, with TEMP/TMP
        # inside the dedicated run directory and no inherited user environment.
        $compileRoot = Join-Path $out "$toolchain/compile"
        $compileWork = Join-Path $compileRoot 'work'
        New-Item -ItemType Directory -Force $compileRoot, $compileWork | Out-Null
        # The source, the outputs and both streams live in the dedicated work root, which is
        # the only place the container may write; readRoots stays empty so a pass proves the
        # vendor ACLs already cover the toolchain (no elevation, no ACL mutation).
        # No custom environment block: the toolchain needs the caller's INCLUDE/LIB/PATH, and
        # an AppContainer already redirects TEMP/TMP to its own private profile storage.
        $source = Join-Path $compileWork 'hello.c'
        @('#include <stdio.h>', 'int main(void) { printf("study-harness-isolated-hello\n"); return 0; }') |
            Set-Content -LiteralPath $source -Encoding ASCII
        $hello = Join-Path $compileWork 'hello.exe'
        $compileSpec = @{
            profileName = 'StudyHarness.Probe.' + [Guid]::NewGuid().ToString('N')
            lowPrivilege = $false
            root = $compileRoot
            readRoots = @()
            workRoot = $compileWork
            sandboxExecutable = $(if ($toolchain -eq 'MSVC') { $tool.Cl } else { $tool.Gcc })
            arguments = $(if ($toolchain -eq 'MSVC') {
                @('/nologo', '/W4', '/WX', '/TC', $source, "/Fe:$hello", "/Fo:$compileWork\\", "/Fd:$compileWork\\compile.pdb")
            } else {
                @('-Wall', '-Wextra', '-Werror', $source, '-o', $hello)
            })
            standardOutputPath = (Join-Path $compileWork 'compile-stdout.txt')
            standardErrorPath = (Join-Path $compileWork 'compile-stderr.txt')
            timeoutMilliseconds = 300000
            extraReadSids = [string[]]@()
        }
        $compileSpecPath = Join-Path $compileRoot 'compile-spec.json'
        $compileSpec | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $compileSpecPath -Encoding UTF8
        & $Dotnet $probe --spec $compileSpecPath (Join-Path $compileRoot 'compile-launch.json') *> (Join-Path $compileRoot 'compile-launcher.log')
        $compileLaunch = Get-Content -LiteralPath (Join-Path $compileRoot 'compile-launch.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $compiled = Test-Path -LiteralPath $hello
        $runExit = $null
        $runOutput = $null
        if ($compiled) {
            $runSpec = $compileSpec.Clone()
            $runSpec.sandboxExecutable = $hello
            $runSpec.arguments = @()
            $runSpec.standardOutputPath = (Join-Path $compileWork 'run-stdout.txt')
            $runSpec.standardErrorPath = (Join-Path $compileWork 'run-stderr.txt')
            $runSpec.timeoutMilliseconds = 60000
            $runSpecPath = Join-Path $compileRoot 'run-spec.json'
            $runSpec | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $runSpecPath -Encoding UTF8
            & $Dotnet $probe --spec $runSpecPath (Join-Path $compileRoot 'run-launch.json') *> (Join-Path $compileRoot 'run-launcher.log')
            $runExit = (Get-Content -LiteralPath (Join-Path $compileRoot 'run-launch.json') -Raw -Encoding UTF8 | ConvertFrom-Json).exitCode
            $runOutput = [string](Get-Content -LiteralPath (Join-Path $compileWork 'run-stdout.txt') -Raw -Encoding UTF8 -ErrorAction SilentlyContinue)
        }
        $checks.Add(@{
            id = "isolated-compile/$toolchain"
            status = $(if ($compileLaunch.exitCode -eq 0 -and $compiled -and $runExit -eq 0 -and $runOutput -match 'study-harness-isolated-hello') { 'pass' } else { 'blocked' })
            compileExitCode = $compileLaunch.exitCode
            compiled = $compiled
            runExitCode = $runExit
            runOutput = $runOutput
            evidence = "$toolchain/compile"
        })
        Save-Checkpoint
    }
    $loopback.Stop(); $lan.Stop(); $udp.Dispose(); $pipe.Dispose()
    $summary.status = 'observed'
}
catch {
    $checks.Add(@{ id = 'preflight'; status = 'blocked'; reason = $_.Exception.Message })
    $summary.status = 'blocked'
}
finally {
    Save-Checkpoint
    Write-Output "P1 mechanism probe status: $($summary.status). Evidence: $out"
}
exit 2
