# Install the pinned official SDK locally; no machine-wide installation.
. "$PSScriptRoot/environment.ps1"

# Windows PowerShell 5.1 renders a progress bar per read for Invoke-WebRequest and
# Invoke-RestMethod. On this ~300 MB SDK archive that dominates the transfer time and
# stalls it outright once the progress stream is redirected to a file, which is how
# verify.ps1 and unattended runs capture output. Both calls below are non-interactive.
$ProgressPreference = 'SilentlyContinue'
Set-Location $RepoRoot
$version = (Get-Content global.json -Raw | ConvertFrom-Json).sdk.version
if ((Test-Path '.tools/dotnet/dotnet.exe') -and ((& $Dotnet --version) -eq $version)) { return }
$metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json' -UseBasicParsing
$entry = $metadata.releases | ForEach-Object { $_.sdks } | Where-Object version -eq $version | Select-Object -First 1
$file = $entry.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -like '*.zip' } | Select-Object -First 1
if (-not $file) { throw "Pinned SDK unavailable: $version" }
New-Item -ItemType Directory -Force '.tools' | Out-Null
# A partial transfer leaves a short file, which the hash check below rejects rather
# than extracting a corrupt SDK; remove it so the next attempt starts clean.
if (Test-Path '.tools/sdk.zip') { Remove-Item -Force '.tools/sdk.zip' }
Invoke-WebRequest $file.url -OutFile '.tools/sdk.zip' -UseBasicParsing
if ((Get-FileHash '.tools/sdk.zip' -Algorithm SHA512).Hash -ne $file.hash) { throw 'SDK hash mismatch' }
Expand-Archive '.tools/sdk.zip' '.tools/dotnet' -Force
