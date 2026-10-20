param([Parameter(Mandatory=$true)][string]$ExecutablePath, [Parameter(Mandatory=$true)][string]$EvidencePath)
$ErrorActionPreference='Stop'
if (-not [Environment]::UserInteractive) { throw 'Interactive desktop required' }
$evidence=[IO.Path]::GetFullPath($EvidencePath)
$dataRoot=Join-Path $evidence 'synthetic-data'
if (Test-Path $dataRoot) { throw 'Use a fresh hybrid evidence folder' }
New-Item -ItemType Directory -Force $dataRoot | Out-Null
Set-Content (Join-Path $dataRoot 'hybrid-synthetic.marker') 'Synthetic test data only' -Encoding UTF8
$previous=$env:STUDYHARNESS_DATA_ROOT
$process=$null
try {
    $env:STUDYHARNESS_DATA_ROOT=$dataRoot
    foreach ($restart in @($false,$true)) {
        $process=Start-Process -FilePath $ExecutablePath -ArgumentList ('--hybrid-selfcheck --evidence "'+$evidence+'"') -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(60000)) { throw 'Hybrid UI timed out' }
        if ($process.ExitCode -ne 0) { throw "Hybrid UI failed; inspect $evidence" }
    }
    $first=Get-Content (Join-Path $evidence 'ui.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $second=Get-Content (Join-Path $evidence 'restart.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($first.failed -ne 0 -or $second.failed -ne 0 -or @($first.checks).Count -lt 15 -or @($second.checks).Count -lt 12) { throw 'Incomplete hybrid UI checks' }
    Write-Output "Hybrid UI: $(@($first.checks).Count+@($second.checks).Count) checks including independent restart."
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:STUDYHARNESS_DATA_ROOT=$previous
}
