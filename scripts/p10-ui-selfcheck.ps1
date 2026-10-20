param([Parameter(Mandatory=$true)][string]$ExecutablePath, [Parameter(Mandatory=$true)][string]$EvidencePath)
$ErrorActionPreference = 'Stop'
if (-not [Environment]::UserInteractive) { throw 'Interactive desktop required' }
$evidence = [IO.Path]::GetFullPath($EvidencePath)
$dataRoot = Join-Path $evidence 'synthetic-data'
New-Item -ItemType Directory -Force $dataRoot | Out-Null
Set-Content (Join-Path $dataRoot 'p10-synthetic.marker') 'Isolated synthetic UI evidence, never learner mastery.' -Encoding UTF8
$previous = $env:STUDYHARNESS_DATA_ROOT
$process = $null
try {
    $env:STUDYHARNESS_DATA_ROOT = $dataRoot
    foreach ($restart in @($false,$true)) {
        $arguments = '--p10-selfcheck --evidence "' + $evidence + '"'
        $process = Start-Process -FilePath $ExecutablePath -ArgumentList $arguments -PassThru -WindowStyle Hidden
        if (-not $process.WaitForExit(120000)) { throw 'P10 UI timed out' }
        if ($process.ExitCode -ne 0) { throw "P10 UI failed; inspect $evidence" }
    }
    $first = Get-Content (Join-Path $evidence 'ui.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $second = Get-Content (Join-Path $evidence 'restart.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($first.failed -ne 0 -or $second.failed -ne 0 -or @($first.checks).Count -ne 74 -or @($second.checks).Count -ne 59) { throw 'Incomplete P10 UI checks' }
    @{ checks=@($first.checks)+@($second.checks); failed=0; manualPending=$true; codeExecuted=$false } | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence 'p10-ui-summary.json') -Encoding UTF8
    Write-Output 'P10 UI: 133 checks passed including fresh-process restart.'
} finally {
    if ($process -and -not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
    $env:STUDYHARNESS_DATA_ROOT = $previous
}
