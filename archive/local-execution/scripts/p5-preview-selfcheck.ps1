param([Parameter(Mandatory=$true)][string]$ExecutablePath, [Parameter(Mandatory=$true)][string]$EvidencePath)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) { throw 'blocked: interactive desktop required' }
$evidence = [IO.Path]::GetFullPath($EvidencePath)
if (Test-Path -LiteralPath (Join-Path $evidence 'preview-ui.json')) { throw 'Use a fresh evidence directory' }
New-Item -ItemType Directory -Force $evidence | Out-Null
$before = $env:STUDYHARNESS_DATA_ROOT
$process = $null
try {
    $env:STUDYHARNESS_DATA_ROOT = Join-Path $evidence 'synthetic-data'
    $arguments = '--p5-preview-check --evidence "' + $evidence + '"'
    $process = Start-Process -FilePath $ExecutablePath -ArgumentList $arguments -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(45000)) { throw 'P5 preview UI timed out' }
    if ($process.ExitCode -ne 0) { throw "P5 preview UI failed; inspect $evidence" }
    $report = Get-Content (Join-Path $evidence 'preview-ui.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($report.failed -ne 0 -or @($report.checks).Count -ne 2 -or $report.realCodeEnabled) { throw 'Incomplete or unsafe preview checks' }
    Write-Output 'P5 preview: 2 UI checks passed. Not a P5 execution/acceptance gate.'
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:STUDYHARNESS_DATA_ROOT = $before
}
