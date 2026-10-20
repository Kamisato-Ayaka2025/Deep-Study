param([Parameter(Mandatory=$true)][string]$ExecutablePath, [Parameter(Mandatory=$true)][string]$EvidencePath)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) { throw 'Interactive desktop required' }
$evidence = [IO.Path]::GetFullPath($EvidencePath)
New-Item -ItemType Directory -Force $evidence | Out-Null
$before = $env:STUDYHARNESS_DATA_ROOT
$process = $null
try {
    $env:STUDYHARNESS_DATA_ROOT = Join-Path $evidence 'synthetic-data'
    foreach ($restart in @($false,$true)) {
        $arguments = $(if ($restart) { '--restart-check ' } else { '' }) + '--p6-selfcheck --evidence "' + $evidence + '"'
        $process = Start-Process -FilePath $ExecutablePath -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(90000)) { throw 'P6 UI timed out' }
        if ($process.ExitCode -ne 0) { throw 'P6 desktop check failed' }
    }
    $first = Get-Content (Join-Path $evidence 'ui.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $second = Get-Content (Join-Path $evidence 'restart.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($first.failed -ne 0 -or $second.failed -ne 0 -or @($first.checks).Count -ne 12 -or @($second.checks).Count -ne 13) { throw 'Incomplete P6 desktop checks' }
    @{checks=@($first.checks)+@($second.checks);failed=0;manualPending=$true} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $evidence 'p6-ui-summary.json') -Encoding UTF8
    Write-Output 'P6: 25 actual desktop checks passed; no C compilation or execution.'
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:STUDYHARNESS_DATA_ROOT = $before
}
