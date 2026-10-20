param([string]$DataRoot, [switch]$Demo)
. "$PSScriptRoot/environment.ps1"
$exe = Join-Path $RepoRoot 'src/StudyHarness.Desktop/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.Desktop.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build Release first with verify.ps1 -Phase P4.' }
if ($Demo) {
    if ($DataRoot) { throw 'Demo uses a dedicated synthetic root; do not specify DataRoot.' }
    $env:STUDYHARNESS_DATA_ROOT = Join-Path $RepoRoot ('artifacts/p4-manual-demo/' + [Guid]::NewGuid().ToString('N'))
    $probe = Join-Path $RepoRoot 'tests/StudyHarness.HostProbe/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.HostProbe.exe'
    & $probe --p4-seed --data-root $env:STUDYHARNESS_DATA_ROOT
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic demo setup failed' }
    Write-Output 'Synthetic Fake demonstration only; no real API or code execution.'
    Start-Process -FilePath $exe -ArgumentList '--p4-demo'
} else {
    $env:STUDYHARNESS_DATA_ROOT = if ($DataRoot) { [IO.Path]::GetFullPath($DataRoot) } else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'StudyHarness' }
    Start-Process -FilePath $exe
}
