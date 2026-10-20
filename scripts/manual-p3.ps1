param([string]$DataRoot)
. "$PSScriptRoot/environment.ps1"
$exe = Join-Path $RepoRoot 'src/StudyHarness.Desktop/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.Desktop.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build the Release desktop with verify.ps1 -Phase P3 first.' }
# Pin the intended root explicitly; do not accidentally inherit an automated UI fixture root.
$env:STUDYHARNESS_DATA_ROOT = if ($DataRoot) { [IO.Path]::GetFullPath($DataRoot) } else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'StudyHarness' }
Write-Output "Study Harness data: $env:STUDYHARNESS_DATA_ROOT"
# User-facing interactive workbench; no elevation, key on command line or policy change.
Start-Process -FilePath $exe
