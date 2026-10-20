param([string]$DataRoot)
. "$PSScriptRoot/environment.ps1"
Set-Location $RepoRoot
if ($DataRoot) { $env:STUDYHARNESS_DATA_ROOT = [IO.Path]::GetFullPath($DataRoot) }
& $Dotnet run --project src/StudyHarness.Desktop --no-restore -c Release
if ($LASTEXITCODE -ne 0) { throw "Desktop exited with $LASTEXITCODE" }
