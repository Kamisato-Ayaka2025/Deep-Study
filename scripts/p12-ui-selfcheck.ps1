param([Parameter(Mandatory=$true)][string]$ExecutablePath, [Parameter(Mandatory=$true)][string]$EvidencePath)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) { throw 'Interactive desktop required' }
$evidence = [IO.Path]::GetFullPath($EvidencePath)
$dataRoot = Join-Path $evidence 'synthetic-data'
if (Test-Path $dataRoot) { throw 'Use a fresh P12 evidence directory' }
New-Item -ItemType Directory -Force $dataRoot | Out-Null
Set-Content (Join-Path $dataRoot 'p12-synthetic.marker') 'P12 isolated synthetic evidence' -Encoding UTF8
$previous = $env:STUDYHARNESS_DATA_ROOT
$process = $null
try {
    $env:STUDYHARNESS_DATA_ROOT = $dataRoot
    foreach ($pass in @('ui','restart')) {
        $process = Start-Process -FilePath $ExecutablePath -ArgumentList ('--p12-selfcheck --evidence "' + $evidence + '"') -PassThru -WindowStyle Hidden
        if ($pass -eq 'ui') {
            $deadline = [DateTime]::UtcNow.AddSeconds(120)
            while (-not (Test-Path (Join-Path $evidence 'crash-ready')) -and -not $process.HasExited -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
            if (-not (Test-Path (Join-Path $evidence 'crash-ready'))) { throw 'P12 first pass failed or timed out' }
            $process.Kill(); $process.WaitForExit()
            Write-Output 'Terminated without graceful shutdown after last input commit; restart must recover it.'
        } elseif (-not $process.WaitForExit(120000)) { throw 'P12 UI timed out' }
        if ($pass -eq 'restart' -and $process.ExitCode -ne 0) { throw "P12 UI failed; inspect $evidence/failure.json" }
        $report = Get-Content (Join-Path $evidence "$pass.json") -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($report.failed -ne 0 -or @($report.checks).Count -lt 17) { throw 'Incomplete P12 evidence' }
        Write-Output "P12 ${pass}: $(@($report.checks).Count) checks passed."
    }
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:STUDYHARNESS_DATA_ROOT = $previous
}