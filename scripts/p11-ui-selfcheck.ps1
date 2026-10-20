param([Parameter(Mandatory=$true)][string]$ExecutablePath, [Parameter(Mandatory=$true)][string]$EvidencePath)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) { throw 'Interactive desktop required' }
$evidence = [IO.Path]::GetFullPath($EvidencePath)
$dataRoot = Join-Path $evidence 'synthetic-data'
if (Test-Path $dataRoot) { throw 'Use a fresh P11 evidence directory; never overwrite data' }
New-Item -ItemType Directory -Force $dataRoot | Out-Null
Set-Content (Join-Path $dataRoot 'p11-synthetic.marker') 'Isolated synthetic performance evidence.' -Encoding UTF8
$previous = $env:STUDYHARNESS_DATA_ROOT
$process = $null
try {
    $env:STUDYHARNESS_DATA_ROOT = $dataRoot
    $process = Start-Process -FilePath $ExecutablePath -ArgumentList ('--p11-selfcheck --evidence "' + $evidence + '"') -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(180000)) { throw 'P11 UI timed out' }
    if ($process.ExitCode -ne 0) { throw "P11 UI failed; inspect $evidence" }
    $report = Get-Content (Join-Path $evidence 'performance.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($report.failed -ne 0 -or $report.baseline -or @($report.samples).Count -ne 15 -or @($report.interaction).Count -ne 5 -or @($report.checks).Count -lt 39) { throw 'Incomplete P11 evidence' }
    Write-Output "P11 UI: $(@($report.checks).Count) checks, 15 graph samples, 5 delayed local preparation samples passed."
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:STUDYHARNESS_DATA_ROOT = $previous
}
