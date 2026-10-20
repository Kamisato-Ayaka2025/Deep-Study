param([string]$MingwRoot = 'D:\Qt\Tools\mingw1310_64')
. "$PSScriptRoot/environment.ps1"
Set-Location $RepoRoot
$out = Join-Path $RepoRoot ('artifacts/review-P1-20261015/clean-toolchain-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $out | Out-Null
Write-Output "Evidence: $out"
$copy = Join-Path $out 'toolchain'
New-Item -ItemType Directory -Path $copy | Out-Null
# Only the fresh private copy receives a protected DACL. Source security is never copied or edited.
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
Set-Acl -LiteralPath $copy -AclObject $acl
$parts = @('bin', 'lib', 'libexec', 'include', 'x86_64-w64-mingw32')
$before = @{}
foreach ($part in $parts) {
    $source = Join-Path $MingwRoot $part
    $before[$part] = (Get-Acl -LiteralPath $source).Sddl
    & robocopy.exe $source (Join-Path $copy $part) /E /COPY:DAT /DCOPY:DAT /XJ /R:0 /W:0 /NFL /NDL /NJH /NJS > "$out/copy-$part.log"
    if ($LASTEXITCODE -ge 8) { throw "Copy failed: $part" }
}
$before | ConvertTo-Json | Set-Content "$out/source-acl-before.json" -Encoding UTF8
$packageAceCount = 0
$files = @(Get-ChildItem -LiteralPath $copy -Recurse -Force)
foreach ($file in $files) {
    foreach ($rule in (Get-Acl -LiteralPath $file.FullName).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.IdentityReference.Value -like 'S-1-15-*') { $packageAceCount++ }
    }
}
if ($packageAceCount -ne 0) { throw 'Private copy contains package ACEs' }
& $Dotnet build probes/StudyHarness.IsolationProbe -c Release -m:1 > "$out/build.log"
if ($LASTEXITCODE -ne 0) { throw 'Probe build failed' }
$probe = Join-Path $RepoRoot 'probes/StudyHarness.IsolationProbe/bin/Release/net10.0-windows/StudyHarness.IsolationProbe.dll'
$results = @()
foreach ($variant in @('no-grant', 'per-profile-grant')) {
    $root = Join-Path $out $variant
    $work = Join-Path $root 'work'
    New-Item -ItemType Directory -Force $work | Out-Null
    $profile = 'StudyHarness.CleanProbe.' + [Guid]::NewGuid().ToString('N')
    $local = Join-Path $work 'appdata'
    New-Item -ItemType Directory -Force "$local/Packages/$($profile.ToLowerInvariant())/AC/Temp" | Out-Null
    Set-Content "$work/hello.c" '#include <stdio.h>
int main(void) { puts("clean-toolchain-hello"); return 0; }' -Encoding ASCII
    $spec = @{
        profileName=$profile; lowPrivilege=$false; root=$root; workRoot=$work
        readRoots=@($(if ($variant -eq 'per-profile-grant') { $parts | ForEach-Object { Join-Path $copy $_ } }))
        sandboxExecutable="$copy/bin/gcc.exe"; arguments=@('-Wall','-Wextra','-Werror',"$work/hello.c",'-o',"$work/hello.exe")
        standardOutputPath="$root/compile-stdout.txt"; standardErrorPath="$root/compile-stderr.txt"; timeoutMilliseconds=60000
        environment=@{SystemRoot=$env:SystemRoot; WINDIR=$env:SystemRoot; PATH="$copy/bin;$env:SystemRoot/System32"; USERPROFILE=$env:USERPROFILE; LOCALAPPDATA=$local; TEMP=$work; TMP=$work}
    }
    # A running fixture must report a real read refusal. Compiler loader failure alone is insufficient.
    & "$MingwRoot/bin/gcc.exe" -Wall -Wextra -Werror -static -o "$work/read-probe.exe" fixtures/runner/restricted-token-probe.c -lws2_32 -ladvapi32 -lshell32
    if ($LASTEXITCODE -ne 0) { throw 'Read fixture build failed' }
    $readSpec = $spec.Clone()
    $readSpec.sandboxExecutable = "$work/read-probe.exe"
    $readSpec.arguments = @("$work/hello.c", "$copy/x86_64-w64-mingw32/include/stdio.h")
    $readSpec.standardOutputPath = "$root/read-stdout.json"
    $readSpec.standardErrorPath = "$root/read-stderr.txt"
    $readSpec | ConvertTo-Json -Depth 6 | Set-Content "$root/read-spec.json" -Encoding UTF8
    & $Dotnet $probe --spec "$root/read-spec.json" "$root/read-launch.json"
    $readLaunch = Get-Content "$root/read-launch.json" -Raw | ConvertFrom-Json
    if ($readLaunch.exitCode -ne 0) { throw 'Read fixture did not execute' }
    $read = Get-Content "$root/read-stdout.json" -Raw | ConvertFrom-Json
    $spec | ConvertTo-Json -Depth 6 | Set-Content "$root/compile-spec.json" -Encoding UTF8
    & $Dotnet $probe --spec "$root/compile-spec.json" "$root/compile-launch.json"
    if ($LASTEXITCODE -ne 0) { throw 'Probe failed' }
    $launch = Get-Content "$root/compile-launch.json" -Raw | ConvertFrom-Json
    $runExit = $null
    $stdout = ''
    if ($launch.exitCode -eq 0 -and (Test-Path "$work/hello.exe")) {
        $spec.profileName = 'StudyHarness.CleanProbe.' + [Guid]::NewGuid().ToString('N')
        New-Item -ItemType Directory -Force "$local/Packages/$($spec.profileName.ToLowerInvariant())/AC/Temp" | Out-Null
        $spec.sandboxExecutable = "$work/hello.exe"
        $spec.arguments = @()
        $spec.standardOutputPath = "$root/run-stdout.txt"
        $spec.standardErrorPath = "$root/run-stderr.txt"
        $spec | ConvertTo-Json -Depth 6 | Set-Content "$root/run-spec.json" -Encoding UTF8
        & $Dotnet $probe --spec "$root/run-spec.json" "$root/run-launch.json"
        $runExit = (Get-Content "$root/run-launch.json" -Raw | ConvertFrom-Json).exitCode
        $stdout = (Get-Content "$root/run-stdout.txt" -Raw).Trim()
    }
    $results += @{ variant=$variant; read=$read; launch=$launch; runExit=$runExit; stdout=$stdout }
}
$sourceUnchanged = $true
$copyClean = $true
foreach ($part in $parts) {
    if ((Get-Acl -LiteralPath (Join-Path $MingwRoot $part)).Sddl -ne $before[$part]) { $sourceUnchanged=$false }
    if ((Get-Acl -LiteralPath (Join-Path $copy $part)).Sddl -match 'S-1-15-') { $copyClean=$false }
}
foreach ($file in $files) {
    foreach ($rule in (Get-Acl -LiteralPath $file.FullName).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.IdentityReference.Value -like 'S-1-15-*') { $copyClean=$false }
    }
}
$passed = $sourceUnchanged -and $copyClean -and $results[0].launch.exitCode -ne 0 -and
    $results[0].read.controlReadError -eq 0 -and $results[0].read.unrelatedReadError -eq 5 -and
    $results[1].read.controlReadError -eq 0 -and $results[1].read.unrelatedReadError -eq 0 -and
    $results[1].launch.exitCode -eq 0 -and $results[1].runExit -eq 0 -and $results[1].stdout -eq 'clean-toolchain-hello'
@{ passed=[bool]$passed; scope='MinGW private copy mechanism baseline, not full P1 acceptance'; copiedEntries=$files.Count; baselinePackageAces=$packageAceCount; sourceRootAclsUnchanged=$sourceUnchanged; copyRootAclsRestored=$copyClean; results=$results } |
    ConvertTo-Json -Depth 8 | Set-Content "$out/summary.json" -Encoding UTF8
Get-Content "$out/summary.json"
if (-not $passed) { exit 1 }
