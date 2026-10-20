param()
. "$PSScriptRoot/environment.ps1"
$exe = Join-Path $RepoRoot 'src/StudyHarness.Desktop/bin/Release/net10.0-windows10.0.19041.0/StudyHarness.Desktop.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build the Release desktop first with verify.ps1 -Phase P2' }
# Interactive application requested by the user; no elevated launch.
Start-Process -FilePath $exe