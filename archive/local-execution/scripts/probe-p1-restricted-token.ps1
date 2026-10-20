param()
. "$PSScriptRoot/environment.ps1"
Set-Location $RepoRoot
$out = Join-Path $RepoRoot ('artifacts/review-P1-20261014/turn007/restricted-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force $out | Out-Null
Write-Output "Evidence: $out"
$probe = Join-Path $RepoRoot 'probes/StudyHarness.IsolationProbe/bin/Release/net10.0-windows/StudyHarness.IsolationProbe.dll'
& $Dotnet build probes/StudyHarness.IsolationProbe -c Release -m:1 > "$out/build.log"
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed' }
foreach ($variant in @('standard', 'restricted')) {
    $root = Join-Path $out $variant
    $work = Join-Path $root 'work'
    New-Item -ItemType Directory -Force $work | Out-Null
    $exe = Join-Path $work 'probe.exe'
    & gcc.exe -Wall -Wextra -Werror -O1 -static -o $exe fixtures/runner/restricted-token-probe.c -lws2_32 -ladvapi32 -lshell32
    if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed' }
    Set-Content "$work/control.txt" 'synthetic allowed control' -Encoding ASCII
    $sentinel = Join-Path $root 'unrelated.txt'
    Set-Content $sentinel 'synthetic unrelated file' -Encoding ASCII
    $acl = Get-Acl $sentinel
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-15-2-1'), 'Read', 'Allow'))
    Set-Acl $sentinel $acl
    $profile = 'StudyHarness.RestrictedProbe.' + [Guid]::NewGuid().ToString('N')
    $local = Join-Path $work 'appdata'
    New-Item -ItemType Directory -Force "$local/Packages/$($profile.ToLowerInvariant())/AC/Temp" | Out-Null
    $spec = @{
        profileName=$profile; lowPrivilege=$false; restrictedPrimaryToken=($variant -eq 'restricted')
        root=$root; readRoots=@(); workRoot=$work; sandboxExecutable=$exe
        arguments=@("$work/control.txt", $sentinel)
        standardOutputPath="$root/stdout.json"; standardErrorPath="$root/stderr.txt"; timeoutMilliseconds=15000
        environment=@{SystemRoot=$env:SystemRoot; WINDIR=$env:SystemRoot; PATH="$env:SystemRoot/System32"; USERPROFILE=$env:USERPROFILE; LOCALAPPDATA=$local; TEMP=$work; TMP=$work}
    }
    $spec | ConvertTo-Json -Depth 6 | Set-Content "$root/spec.json" -Encoding UTF8
    & $Dotnet $probe --spec "$root/spec.json" "$root/launch.json"
    if ($LASTEXITCODE -ne 0) { throw 'Probe harness failed' }
    Write-Output $variant
    Get-Content "$root/launch.json"
    Get-Content "$root/stdout.json"
}
