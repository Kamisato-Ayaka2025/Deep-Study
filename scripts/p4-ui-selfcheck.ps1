param([Parameter(Mandatory=$true)][string]$ExecutablePath, [Parameter(Mandatory=$true)][string]$EvidencePath)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) { throw 'blocked: interactive desktop required' }
$repo = Split-Path $PSScriptRoot -Parent
$evidence = [IO.Path]::GetFullPath($EvidencePath)
New-Item -ItemType Directory -Force $evidence | Out-Null
$dataRoot = Join-Path $evidence 'synthetic-data'
$probe = Join-Path $repo 'tests/StudyHarness.HostProbe/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.HostProbe.exe'
& $probe --p4-seed --data-root $dataRoot
if ($LASTEXITCODE -ne 0) { throw 'P4 synthetic seed failed' }
$before = $env:STUDYHARNESS_DATA_ROOT
$process = $null
try {
    $env:STUDYHARNESS_DATA_ROOT = $dataRoot
    foreach ($restart in @($false,$true)) {
        $arguments = '--p4-selfcheck --evidence "' + $evidence + '"'
        if ($restart) { $arguments += ' --restart-check' }
        # Visible is intentional: this gate validates the actual desktop reader.
        $process = Start-Process -FilePath $ExecutablePath -ArgumentList $arguments -PassThru
        if (-not $process.WaitForExit(120000)) { throw 'P4 UI timed out' }
        if ($process.ExitCode -ne 0) { throw "P4 UI failed; inspect $evidence" }
    }
    $first = Get-Content (Join-Path $evidence 'ui.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $second = Get-Content (Join-Path $evidence 'restart.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($first.failed -ne 0 -or $second.failed -ne 0 -or @($first.checks).Count -ne 4 -or @($second.checks).Count -ne 2) { throw 'Incomplete P4 UI checks' }
    @{checks=@($first.checks)+@($second.checks);failed=0;leftoverProcesses=0;manualPending=$true} | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence 'p4-ui-summary.json') -Encoding UTF8
    Write-Output 'P4 UI: 6 checks passed, including fresh-process restart.'
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:STUDYHARNESS_DATA_ROOT = $before
}
