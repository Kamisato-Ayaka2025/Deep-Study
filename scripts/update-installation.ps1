param([string]$InstallDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $InstallDirectory) { $InstallDirectory = Read-Host 'Path to the OLD folder containing StudyHarness.Desktop.exe' }
$install = [IO.Path]::GetFullPath($InstallDirectory.Trim().Trim('"')).TrimEnd('\')
$payload = Join-Path $PSScriptRoot 'payload'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'upgrade-files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.version -ne '1.10.0') { throw 'Unexpected upgrade manifest version.' }
if (Get-Process -Name 'StudyHarness.Desktop' -ErrorAction SilentlyContinue) { throw 'Exit Deep Study from its tray icon before upgrading. No files changed.' }
if (-not (Test-Path -LiteralPath (Join-Path $install 'StudyHarness.Desktop.exe') -PathType Leaf)) { throw 'Select the old application folder, not a data folder or the upgrade package.' }
$oldVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $install 'StudyHarness.Desktop.dll')).Version.ToString(3)
if ($oldVersion -notin @('1.6.0','1.6.2','1.10.0')) { throw "Unsupported installed version: $oldVersion. This package supports 1.6.0 and 1.6.2 (and retrying 1.10.0)." }
if ($install.Equals($PSScriptRoot,[StringComparison]::OrdinalIgnoreCase) -or $install.StartsWith($PSScriptRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Extract the upgrade into a separate folder outside the old application.' }

function Assert-NoLink([string]$Path) {
    $current = $Path
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked paths are not supported: $current" }
        }
        $parent = Split-Path -Parent $current
        if ($parent -eq $current) { break }
        $current = $parent
    }
}
Assert-NoLink $install
$seen = @{}
$entries = @($manifest.files)
if ($entries.Count -lt 10) { throw 'Incomplete upgrade manifest.' }
foreach ($entry in $entries) {
    $relative = [string]$entry.path
    if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.?([\\/]|$)|:') { throw 'Invalid package path.' }
    if ($relative -notmatch '^(ReaderAssets[\\/](.+\.(js|css|html|woff2?|ttf)|vendor[\\/](LICENSE|provenance\.json))|runtimes[\\/](win-x64|win-x86|win-arm64)[\\/]native[\\/][^\\/]+\.dll|[^\\/]+\.(dll|exe|deps\.json|runtimeconfig\.json))$') { throw "Not an application file: $relative" }
    if ($seen.ContainsKey($relative.ToLowerInvariant())) { throw 'Duplicate package entry.' }
    $seen[$relative.ToLowerInvariant()] = $true
    $source = Join-Path $payload $relative
    $target = Join-Path $install $relative
    Assert-NoLink $source
    Assert-NoLink $target
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Package checksum failed: $relative. No files changed." }
}
# Only allowlisted application files are touched. Databases, caches, credentials,
# custom launchers and everything outside the installation directory stay in place.
$backup = Join-Path $install ('.deepstudy-upgrade-backups\'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8))
Assert-NoLink $backup
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$applied = New-Object 'Collections.Generic.List[object]'
try {
    # Complete all backups before replacing the first application file.
    foreach ($entry in $entries) {
        $target = Join-Path $install $entry.path
        if (Test-Path -LiteralPath $target) {
            $saved = Join-Path $backup $entry.path
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved)) | Out-Null
            Copy-Item -LiteralPath $target -Destination $saved
            if ((Get-FileHash -LiteralPath $saved).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { throw 'Backup checksum failed.' }
        }
    }
    foreach ($entry in $entries) {
        $target = Join-Path $install $entry.path
        $saved = Join-Path $backup $entry.path
        $applied.Add(@{target=$target;backup=$saved;existed=(Test-Path -LiteralPath $saved)})
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath (Join-Path $payload $entry.path) -Destination $target -Force
        if ((Get-FileHash -LiteralPath $target).Hash -ne $entry.sha256) { throw "Installed checksum failed: $($entry.path)" }
    }
    @{from=$oldVersion;to='1.10.0';files=$entries.Count;dataModified=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'upgrade-result.json') -Encoding UTF8
} catch {
    $failure = $_
    $rollbackErrors = New-Object 'Collections.Generic.List[string]'
    for ($i=$applied.Count-1; $i -ge 0; $i--) {
        $entry = $applied[$i]
        try {
            if ($entry.existed) {
                # A locked file may have rejected the write without changing at all.
                if ((Test-Path -LiteralPath $entry.target) -and (Get-FileHash -LiteralPath $entry.target).Hash -eq (Get-FileHash -LiteralPath $entry.backup).Hash) { continue }
                Copy-Item -LiteralPath $entry.backup -Destination $entry.target -Force
            } elseif (Test-Path -LiteralPath $entry.target) { Remove-Item -LiteralPath $entry.target }
        } catch { $rollbackErrors.Add($entry.target) }
    }
    if ($rollbackErrors.Count) { throw "Upgrade failed and $($rollbackErrors.Count) program files could not be restored. Close other programs and restore the program-file backup at $backup. Data files were not touched. Original failure: $failure" }
    throw $failure
}
Write-Output "Upgraded $oldVersion to 1.10.0. Existing data and launchers are unchanged."
Write-Output "Application-file backup: $backup"
Write-Output 'Start Deep Study with your usual shortcut or launcher, using the same Windows account.'
