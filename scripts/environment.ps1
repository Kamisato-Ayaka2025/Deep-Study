$ErrorActionPreference = 'Stop'
$script:RepoRoot = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_HOME = Join-Path $RepoRoot '.tools/home'
$env:NUGET_PACKAGES = Join-Path $RepoRoot '.tools/nuget'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $RepoRoot '.tools/http-cache'
$env:NUGET_SCRATCH = Join-Path $RepoRoot '.tools/nuget-scratch'
$env:TEMP = Join-Path $RepoRoot '.tools/tmp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP, $env:NUGET_SCRATCH | Out-Null
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$localDotnet = Join-Path $RepoRoot '.tools/dotnet/dotnet.exe'
$script:Dotnet = if ($env:STUDYHARNESS_DOTNET) { $env:STUDYHARNESS_DOTNET } elseif (Test-Path $localDotnet) { $localDotnet } elseif (Get-Command dotnet -ErrorAction SilentlyContinue) { (Get-Command dotnet).Source } else { $localDotnet }
$env:DOTNET_ROOT = Split-Path $Dotnet -Parent
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
