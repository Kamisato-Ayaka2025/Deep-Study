param([Parameter(Mandatory=$true)][string]$ExecutablePath, [Parameter(Mandatory=$true)][string]$EvidencePath)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) { throw 'Interactive desktop required' }
$evidence = [IO.Path]::GetFullPath($EvidencePath)
New-Item -ItemType Directory -Force $evidence | Out-Null
$before = $env:STUDYHARNESS_DATA_ROOT
$process = $null
try {
    $env:STUDYHARNESS_DATA_ROOT = Join-Path $evidence 'synthetic-data'
    $arguments = '--web-selfcheck --evidence "' + $evidence + '"'
    $process = Start-Process -FilePath $ExecutablePath -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(90000)) { throw 'Web desktop check timed out' }
    if ($process.ExitCode -ne 0) { throw 'Web desktop check failed' }
    $result = Get-Content (Join-Path $evidence 'web-ui.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($result.failed -ne 0 -or @($result.checks).Count -ne 10) { throw 'Incomplete web desktop checks' }
    Write-Output 'Web: 10 actual desktop checks passed, deterministic network fixture; real network checked separately.'
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:STUDYHARNESS_DATA_ROOT = $before
}