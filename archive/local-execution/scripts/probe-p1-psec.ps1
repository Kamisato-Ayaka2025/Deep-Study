param([ValidateSet('msvc','mingw')][string]$Toolchain='mingw', [string]$PythonPath=(Join-Path $env:USERPROFILE '.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'))
$ErrorActionPreference='Stop'
. "$PSScriptRoot/environment.ps1"
Set-Location $RepoRoot
$out=Join-Path $RepoRoot ('artifacts/review-P1-20261015/psec/matrix-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+$Toolchain)
$work=Join-Path $out 'work'
New-Item -ItemType Directory -Force $work | Out-Null
Copy-Item "$RepoRoot/probes/PsecNative/spec.py" "$out/spec.py"
Copy-Item $PSCommandPath "$out/driver.ps1"
$outside=New-Item -ItemType Directory "$out/outside"
foreach($kind in @('ordinary','aap','arap')) {
    $file="$outside/$kind.txt"
    Set-Content $file 'synthetic sentinel' -Encoding ASCII
    $acl=[Security.AccessControl.FileSecurity]::new()
    $acl.SetAccessRuleProtection($true,$false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.WindowsIdentity]::GetCurrent().User,'FullControl','Allow'))
    if($kind -ne 'ordinary') {
        $sid=if($kind -eq 'aap') {'S-1-15-2-1'} else {'S-1-15-2-2'}
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'Read','Allow'))
    }
    Set-Acl -LiteralPath $file -AclObject $acl
    if([IO.File]::ReadAllText($file) -ne "synthetic sentinel`r`n") { throw 'host read control failed' }
}
New-Item -ItemType Junction -Path "$work/escape" -Target $outside.FullName | Out-Null
$source=Get-Content "$RepoRoot/probes/PsecNative/escape.c" -Raw
[IO.File]::WriteAllText("$work/main.c",$source,[Text.Encoding]::UTF8)
$launcher=Get-Content "$RepoRoot/probes/PsecNative/launch.c" -Raw
[IO.File]::WriteAllText("$out/launch.c",$launcher)
& gcc.exe -Wall -Wextra -Werror -static -municode "$out/launch.c" -o "$out/launch.exe" -luserenv -lshell32 -lole32 -ladvapi32
if($LASTEXITCODE -ne 0) { throw 'launcher compile failed' }
if($Toolchain -eq 'msvc') {
    $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $installation=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
    $vcvars=Join-Path $installation 'VC/Auxiliary/Build/vcvars64.bat'
    & cmd.exe /d /s /c ('"'+$vcvars+'" >nul 2>nul && set') | ForEach-Object {
        if($_ -match '^(INCLUDE|LIB|LIBPATH|PATH)=(.*)$') { [Environment]::SetEnvironmentVariable($matches[1],$matches[2],'Process') }
    }
    $compiler=(Get-Command cl.exe).Source
    $roots=@(Split-Path $compiler)+@(($env:INCLUDE+';'+$env:LIB).Split(';') | Where-Object { $_ -and (Test-Path $_) })
    $headerFile=$env:INCLUDE.Split(';') | ForEach-Object { Join-Path $_ 'stdio.h' } | Where-Object { Test-Path $_ } | Select-Object -First 1
    $compileCommand='"'+$compiler+'" /nologo /W4 /WX /MT "'+$work+'\main.c" /Fe:"'+$work+'\escape.exe" /Fo:"'+$work+'\main.obj"'
    $compileCommand += ' ' + (($env:INCLUDE.Split(';') | Where-Object { $_ } | ForEach-Object { '/I"'+$_+'"' }) -join ' ')
    $compileCommand += ' /link ws2_32.lib shell32.lib advapi32.lib userenv.lib ole32.lib ' + (($env:LIB.Split(';') | Where-Object { $_ } | ForEach-Object { '/LIBPATH:"'+$_+'"' }) -join ' ')
} else {
    $compiler=(Get-Command gcc.exe).Source
    $toolRoot=Split-Path (Split-Path $compiler)
    $roots=@('bin','include','lib','libexec','x86_64-w64-mingw32') | ForEach-Object { Join-Path $toolRoot $_ }
    $headerFile=Join-Path $toolRoot 'x86_64-w64-mingw32/include/stdio.h'
    [IO.File]::WriteAllText("$work/linker.specs", "*linker:`nld`n`n", [Text.Encoding]::ASCII)
    $compileCommand='"'+$compiler+'" -Wall -Wextra -Werror -static "'+$work+'\main.c" -o "'+$work+'\escape.exe" -lws2_32 -lshell32 -ladvapi32 -luserenv -lole32 -B"'+$toolRoot+'/bin/" -fno-use-linker-plugin -specs="'+$work+'/linker.specs"'
}
if(!(Test-Path $headerFile)) { throw 'header control missing' }
ConvertTo-Json -InputObject @($roots) | Set-Content "$out/roots.json" -Encoding UTF8
& $PythonPath "$out/spec.py" $work "$out/spec.bin" ([IO.DriveInfo]::GetDrives().Name -join ';') "$out/roots.json"
if($LASTEXITCODE -ne 0) { throw 'spec failed' }
$env:LOCALAPPDATA=$work; $env:TEMP=$work; $env:TMP=$work
[IO.File]::WriteAllBytes("$out/compile-command.txt",[Text.Encoding]::Unicode.GetBytes($compileCommand))
& "$out/launch.exe" "$out/spec.bin" $work $compiler unused unused "$out/compile-output.txt" "$out/compile-command.txt" > "$out/compile-launch.log"
if($LASTEXITCODE -ne 0) { throw "PSEC compile failed: $out/compile-output.txt" }
Copy-Item "$RepoRoot/probes/PsecNative/network.c" "$work/network.c"
$networkCompile=$compileCommand.Replace('\main.c','\network.c').Replace('\escape.exe','\network.exe')
[IO.File]::WriteAllBytes("$out/network-compile-command.txt",[Text.Encoding]::Unicode.GetBytes($networkCompile))
& "$out/launch.exe" "$out/spec.bin" $work $compiler unused unused "$out/network-compile-output.txt" "$out/network-compile-command.txt" > "$out/network-compile-launch.log"
if($LASTEXITCODE -ne 0) { throw 'PSEC network fixture compile failed' }
& "$work/network.exe" > "$out/network-host-before.json"
if($LASTEXITCODE -ne 0) { throw 'Host network fixture failed' }
[IO.File]::WriteAllBytes("$out/network-command.txt",[Text.Encoding]::Unicode.GetBytes('"'+$work+'\network.exe"'))
& "$out/launch.exe" "$out/spec.bin" $work "$work/network.exe" unused unused "$out/network-psec.json" "$out/network-command.txt" > "$out/network-launch.log"
if($LASTEXITCODE -ne 0) { throw 'PSEC network fixture failed' }
& "$work/network.exe" > "$out/network-host-after.json"
if($LASTEXITCODE -ne 0) { throw 'Host network fixture failed' }
$before=Get-Content "$out/network-host-before.json" -Raw | ConvertFrom-Json
$after=Get-Content "$out/network-host-after.json" -Raw | ConvertFrom-Json
$contained=Get-Content "$out/network-psec.json" -Raw | ConvertFrom-Json
$networkV4Passed=$before.tcpV4Error -eq 0 -and $after.tcpV4Error -eq 0 -and $before.dnsResponse -eq 1 -and $after.dnsResponse -eq 1 -and $contained.tcpV4Error -eq 10013 -and $contained.dnsResponse -eq 0 -and $contained.udpError -eq 10060
@{ipv4Passed=$networkV4Passed;ipv6Scope="excluded-by-user-unverified";hostBefore=$before;hostAfter=$after;contained=$contained} | ConvertTo-Json | Set-Content "$out/network-summary.json" -Encoding UTF8
$ip=[Net.Dns]::GetHostAddresses([Net.Dns]::GetHostName()) | Where-Object AddressFamily -eq InterNetwork | Select-Object -First 1
if(!$ip -or [Net.IPAddress]::IsLoopback($ip)) { throw 'non-loopback host target missing' }
$tcp=[Net.Sockets.TcpListener]::new($ip,0); $tcp.Start()
$udp=[Net.Sockets.UdpClient]::new([Net.IPEndPoint]::new($ip,0))
$pipeName='psec-matrix-'+[guid]::NewGuid().ToString('N')
$pipe=[IO.Pipes.NamedPipeServerStream]::new($pipeName,[IO.Pipes.PipeDirection]::InOut,1,[IO.Pipes.PipeTransmissionMode]::Byte,[IO.Pipes.PipeOptions]::Asynchronous)
try {
    foreach($listener in @($tcp)) {
        $client=[Net.Sockets.TcpClient]::new($listener.LocalEndpoint.AddressFamily)
        $client.Connect($listener.LocalEndpoint.Address,$listener.LocalEndpoint.Port)
        $accepted=$listener.AcceptTcpClient(); $accepted.Dispose(); $client.Dispose()
    }
    $dns=[Net.Dns]::GetHostAddresses('example.com').Count
    $udpControl=[Net.Sockets.UdpClient]::new()
    try {
        $packet=[Text.Encoding]::ASCII.GetBytes('synthetic-control')
        $null=$udpControl.Send($packet,$packet.Length,$udp.Client.LocalEndPoint)
        $udp.Client.ReceiveTimeout=3000
        $remote=[Net.IPEndPoint]::new([Net.IPAddress]::Any,0)
        $received=$udp.Receive([ref]$remote)
        if([Text.Encoding]::ASCII.GetString($received) -ne 'synthetic-control') { throw 'UDP host control failed' }
    } finally { $udpControl.Dispose() }
    $argsList=@("$work/escape.exe","$outside/ordinary.txt","$outside/write.txt","$work/../outside/traversal.txt","$work/escape/junction.txt","\\.\pipe\$pipeName","\\.\pipe\$pipeName",$tcp.LocalEndpoint.Port,$ip.ToString(),$udp.Client.LocalEndPoint.Port,'example.com',"$work/escape.exe","$work/child.json",$headerFile,"$work/report.json","$outside/aap.txt",0,[Net.Dns]::GetHostName(),'actual-profile-derived-by-fixture',"$outside/arap.txt")
    $command=($argsList | ForEach-Object { '"'+$_+'"' }) -join ' '
    [IO.File]::WriteAllBytes("$out/command.txt",[Text.Encoding]::Unicode.GetBytes($command))
    $env:LOCALAPPDATA=$work; $env:TEMP=$work; $env:TMP=$work
    & "$out/launch.exe" "$out/spec.bin" $work "$work/escape.exe" unused unused "$out/stdout.txt" "$out/command.txt" > "$out/launch.log"
    $code=$LASTEXITCODE
    $report=if(Test-Path "$work/report.json") { Get-Content "$work/report.json" -Raw | ConvertFrom-Json } else { $null }
    $childReport=if(Test-Path "$work/child.json") { Get-Content "$work/child.json" -Raw | ConvertFrom-Json } else { $null }
    $sentinel=[IO.Path]::GetFullPath("$outside/arap.txt")
    $aliases=@($sentinel,$sentinel.ToUpperInvariant(),('\\?\'+$sentinel),[IO.Path]::GetFullPath("$work/escape/arap.txt"))
    foreach($alias in $aliases) { if([IO.File]::ReadAllText($alias) -ne "synthetic sentinel`r`n") { throw 'alias host control failed' } }
    $aliasCommand='"'+$work+'\escape.exe" --read-aliases '+(($aliases | ForEach-Object { '"'+$_+'"' }) -join ' ')
    [IO.File]::WriteAllBytes("$out/aliases-command.txt",[Text.Encoding]::Unicode.GetBytes($aliasCommand))
    & "$out/launch.exe" "$out/spec.bin" $work "$work/escape.exe" unused unused "$out/aliases-output.txt" "$out/aliases-command.txt" > "$out/aliases-launch.log"
    $aliasExit=$LASTEXITCODE
    $aliasErrors=@(Get-Content "$out/aliases-output.txt")
    $aliasesPassed=$aliasExit -eq 0 -and $aliasErrors.Count -eq $aliases.Count -and @($aliasErrors | Where-Object { $_ -ne '5' }).Count -eq 0
    @{paths=$aliases;errors=$aliasErrors;hostReadable=$true;passed=$aliasesPassed} | ConvertTo-Json | Set-Content "$out/aliases.json" -Encoding UTF8
    $expected=@{originalProfileWriteError=5;isAppContainer=1;winsockStartupError=0;insideWriteError=0;headerReadError=0;containerLoopbackError=10013;tempWithinWork=1;tempWriteError=0;tempFileWithinWork=1;unrelatedReadError=5;ambientAapReadError=5;ambientArapReadError=5;outsideWriteError=5;traversalWriteError=5;junctionWriteError=5;controlPipeConnectError=5;decoyPipeConnectError=5;dnsError=11001;childSpawnError=0;childIsAppContainer=1;tcpV4Error=10013;childConnectError=10013}
    $failures=@($expected.Keys | Where-Object { $null -eq $report -or $null -eq $report.$_ -or $report.$_ -ne $expected[$_] })
    if($null -eq $childReport -or $childReport.ambientReadError -ne 5) { $failures+='child-ambient-read-denied' }
    if(!$aliasesPassed) { $failures+='alias-read-denied' }
    if(!$networkV4Passed) { $failures+='external-ipv4' }
    if($tcp.Pending() -or $udp.Available -gt 0) { $failures+='local-listeners-untouched' }
    [IO.File]::WriteAllBytes("$out/resource-command.txt",[Text.Encoding]::Unicode.GetBytes('"'+$work+'\escape.exe" --resource-probe'))
    & "$out/launch.exe" "$out/spec.bin" $work "$work/escape.exe" unused unused "$out/resource-output.json" "$out/resource-command.txt" > "$out/resource-launch.log"
    $resourceExit=$LASTEXITCODE
    $resource=Get-Content "$out/resource-output.json" -Raw | ConvertFrom-Json
    if($resourceExit -ne 0 -or $resource.memoryError -notin @(8,1455) -or $resource.spawned -ne 15 -or $resource.spawnError -eq 0) { $failures+='resource-memory-process-limits' }
    $lifecycle=@()
    foreach($mode in @('stop','crash')) {
        $pidFile="$work/$mode-pids.txt"
        $stopFile="$out/$mode-stop.txt"
        $hangCommand='"'+$work+'\escape.exe" --hang-tree "'+$pidFile+'"'
        [IO.File]::WriteAllBytes("$out/$mode-command.txt",[Text.Encoding]::Unicode.GetBytes($hangCommand))
        $launchArguments=@("$out/spec.bin",$work,"$work/escape.exe",'unused','unused',"$out/$mode-output.txt","$out/$mode-command.txt",$stopFile)
        $quotedArguments=($launchArguments | ForEach-Object { '"'+$_+'"' }) -join ' '
        $owner=Start-Process -FilePath "$out/launch.exe" -ArgumentList $quotedArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput "$out/$mode-launch.log" -RedirectStandardError "$out/$mode-launch-error.log"
        $children=@()
        try {
            $null=$owner.Handle
            $deadline=[DateTime]::UtcNow.AddSeconds(15)
            while(!(Test-Path $pidFile) -and !$owner.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100; $owner.Refresh() }
            if(!(Test-Path $pidFile)) { throw "Lifecycle child not ready: $mode" }
            $ids=@(Get-Content $pidFile | ForEach-Object { [int]$_ })
            if($ids.Count -ne 2) { throw 'Expected two live child processes' }
            $children=@($ids | ForEach-Object { [Diagnostics.Process]::GetProcessById([int]$_) })
            # Pin handles before stopping the owner, avoiding PID reuse in the verdict.
            foreach($child in $children) { $null=$child.Handle }
            Start-Sleep -Seconds 2
            $running=@($children | Where-Object { !$_.HasExited }).Count -eq 2
            if($mode -eq 'stop') { [IO.File]::WriteAllText($stopFile,'stop'); if(!$owner.WaitForExit(15000)) { throw 'Stop did not finish' } }
            else { $owner.Kill(); $owner.WaitForExit() }
            $exited=@($children | ForEach-Object { $_.WaitForExit(10000) })
            $passed=$running -and @($exited | Where-Object { !$_ }).Count -eq 0
            $lifecycle+=@{mode=$mode;observedAlive=$running;allExited=$passed;ids=$ids;ownerExit=$owner.ExitCode}
            if(!$passed) { $failures+="lifecycle-$mode" }
        } finally {
            if(!$owner.HasExited) { $owner.Kill(); $owner.WaitForExit() }
            foreach($child in $children) { $child.Dispose() }
            $owner.Dispose()
        }
    }
    $lifecycle | ConvertTo-Json -Depth 3 | Set-Content "$out/lifecycle.json" -Encoding UTF8
    @{toolchain=$Toolchain;compiler=$compiler;compileInPsec=$true;launcherExit=$code;testedSubsetPassed=($code -eq 0 -and $failures.Count -eq 0);failedCases=$failures;fullP1Acceptance=$false;externalNetworkVerification='ipv4-tested-ipv6-excluded';ipv6Scope='excluded-by-user-unverified';hostTcpControls=$true;hostDnsCount=$dns;tcpReceived=$tcp.Pending();udpReceived=$udp.Available;report=$report} | ConvertTo-Json -Depth 5 | Set-Content "$out/summary.json" -Encoding UTF8
    Write-Output "Evidence: $out"
    Get-Content "$out/summary.json"
    if($code -ne 0 -or $failures.Count -gt 0) { throw 'PSEC subset failed' }
} finally { $tcp.Stop();$udp.Dispose();$pipe.Dispose() }
