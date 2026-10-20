param([ValidateSet('Prepare', 'Mount', 'Remount', 'Inspect')][string]$Action = 'Inspect')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$base = Join-Path $repo '.tools/p1-volume'
$imagePath = Join-Path $base 'p1-work.vhd'
$mountPath = (Join-Path $base 'mount') + '\'
$manifestPath = Join-Path $base 'manifest.json'
$capacity = [long]2147483648

function Assert-PlainAncestors([string]$Path) {
    $itemPath = [IO.Path]::GetFullPath($Path)
    while ($itemPath) {
        if (Test-Path -LiteralPath $itemPath) {
            $item = Get-Item -LiteralPath $itemPath -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Linked path refused: $itemPath" }
        }
        $parent = Split-Path $itemPath -Parent
        if ($parent -eq $itemPath) { break }
        $itemPath = $parent
    }
}
function Get-OwnedDisk {
    $images = @(Get-DiskImage -ImagePath $imagePath)
    if ($images.Count -ne 1 -or !$images[0].Attached -or $images[0].Size -ne $capacity) { throw 'Image is not attached with the expected capacity' }
    $disks = @($images[0] | Get-Disk)
    if ($disks.Count -ne 1) { throw 'Image must resolve to exactly one disk' }
    $disk = $disks[0]
    if ($disk.Size -ne $capacity -or $disk.IsBoot -or $disk.IsSystem -or $disk.BusType -ne 'File Backed Virtual') {
        throw 'Refusing a disk that is not the dedicated 2GiB virtual disk'
    }
    return $disk
}
function Set-ImageAcl {
    # The virtual disk device inherits the backing image's security on attach.
    $acl = New-Object Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.SetOwner($owner)
    foreach ($sid in @($owner, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'Allow'))
    }
    ([IO.FileInfo]::new($imagePath)).SetAccessControl($acl)
}
function Get-VerifiedVolume($manifest) {
    $disk = Get-OwnedDisk
    if ([string]$disk.UniqueId -ne [string]$manifest.diskUniqueId -or [string]$disk.PartitionStyle -ne 'GPT') { throw 'Disk identity mismatch' }
    $parts = @($disk | Get-Partition | Where-Object { $_.Guid -eq $manifest.partitionGuid })
    if ($parts.Count -ne 1) { throw 'Partition identity mismatch' }
    $volumes = @($parts[0] | Get-Volume)
    if ($volumes.Count -ne 1 -or $volumes[0].FileSystem -ne 'NTFS' -or $volumes[0].UniqueId -ne $manifest.volumeUniqueId) { throw 'Volume identity mismatch' }
    if ($volumes[0].Size -gt $capacity -or $volumes[0].Size -le 0) { throw 'Volume capacity mismatch' }
    return $parts[0]
}

# No caller-supplied path, disk number, capacity, executable, or format target.
Assert-PlainAncestors $base
if ($Action -ne 'Inspect') {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator preparation/mount required; never elevate the Runner' }
}
if ($Action -eq 'Prepare') {
    # A failed/partial preparation is deliberately not retried over existing data.
    if (Test-Path -LiteralPath $base) { throw 'Preparation directory already exists; refusing to reuse or format it' }
    $drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($base))
    if ($drive.DriveFormat -ne 'NTFS' -or $drive.AvailableFreeSpace -lt ($capacity + 1GB)) { throw 'Need NTFS and at least 3GiB free' }
    [IO.Directory]::CreateDirectory($base) | Out-Null
    Start-Transcript -Path (Join-Path $base 'prepare.log') | Out-Null
    try {
        if ($imagePath -match '["\r\n]' -or $imagePath -match '[^\x20-\x7e]') { throw 'DiskPart preparation requires an ASCII-only image path' }
        $diskpartScript = Join-Path $base 'create-only.txt'
        # DiskPart is used ONLY to create a new image; never select/clean/format a disk.
        [IO.File]::WriteAllText($diskpartScript, "create vdisk file=`"$imagePath`" maximum=2048 type=fixed`r`nexit`r`n", [Text.Encoding]::ASCII)
        & "$env:SystemRoot/System32/diskpart.exe" /s $diskpartScript
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $imagePath)) { throw 'Fixed VHD creation failed' }
        $imageFile = Get-Item -LiteralPath $imagePath
        if ($imageFile.Length -ne ($capacity + 512)) { throw 'Unexpected fixed VHD file length' }
        Assert-PlainAncestors $imagePath
        Set-ImageAcl
        Mount-DiskImage -ImagePath $imagePath -StorageType VHD -Access ReadWrite -NoDriveLetter | Out-Null
        $disk = Get-OwnedDisk
        if ($disk.PartitionStyle -ne 'RAW' -or @($disk | Get-Partition -ErrorAction SilentlyContinue).Count -ne 0) { throw 'New image is not empty; refusing initialization' }
        $disk = $disk | Initialize-Disk -PartitionStyle GPT -PassThru
        $partition = $disk | New-Partition -UseMaximumSize
        # Resolve through the image again immediately before formatting. Physical
        # partitions are never enumerated as candidates or accepted as parameters.
        $verified = Get-OwnedDisk
        if ($verified.UniqueId -ne $disk.UniqueId -or $partition.DiskNumber -ne $verified.Number) { throw 'Disk changed before formatting' }
        $volume = $partition | Format-Volume -FileSystem NTFS -NewFileSystemLabel 'StudyHarnessP1' -Force -Confirm:$false
        [IO.Directory]::CreateDirectory($mountPath) | Out-Null
        $partition | Add-PartitionAccessPath -AccessPath $mountPath
        $manifest = [ordered]@{ schemaVersion=1; capacityBytes=$capacity; imagePath=$imagePath; mountPath=$mountPath; diskUniqueId=[string]$verified.UniqueId; partitionGuid=[string]$partition.Guid; volumeUniqueId=[string]$volume.UniqueId; realCodeEnabled=$false }
        $manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding UTF8
        Write-Output 'Prepared dedicated volume. This does not enable real-code execution.'
    } catch {
        $_ | Out-String | Set-Content -LiteralPath (Join-Path $base 'failure.txt') -Encoding UTF8
        throw
    } finally { Stop-Transcript | Out-Null }
}
if (!(Test-Path -LiteralPath $manifestPath)) { throw 'No completed volume manifest' }
Assert-PlainAncestors $manifestPath
Assert-PlainAncestors $imagePath
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.capacityBytes -ne $capacity -or $manifest.imagePath -ne $imagePath -or $manifest.mountPath -ne $mountPath) { throw 'Manifest scope mismatch' }
if ((Get-Item -LiteralPath $imagePath).Length -ne ($capacity + 512)) { throw 'Fixed image changed size' }
if ($Action -eq 'Remount') {
    $null = Get-VerifiedVolume $manifest
    if (@(Get-Process -Name 'StudyHarness.Runner','program','testhost' -ErrorAction SilentlyContinue).Count -ne 0) { throw 'Close all Runner/probe processes before remount' }
    Dismount-DiskImage -ImagePath $imagePath
    Set-ImageAcl
}
if ($Action -eq 'Mount' -or $Action -eq 'Remount') {
    if (!(Get-DiskImage -ImagePath $imagePath).Attached) { Mount-DiskImage -ImagePath $imagePath -StorageType VHD -Access ReadWrite -NoDriveLetter | Out-Null }
    $partition = Get-VerifiedVolume $manifest
    if (!$partition.DriveLetter -or [int][char]$partition.DriveLetter -eq 0) {
        $partition | Add-PartitionAccessPath -AssignDriveLetter
    }
    if ($partition.AccessPaths -notcontains $mountPath) {
        Assert-PlainAncestors $mountPath
        if (@(Get-ChildItem -LiteralPath $mountPath -Force).Count -ne 0) { throw 'Mount directory is not empty' }
        $partition | Add-PartitionAccessPath -AccessPath $mountPath
    }
}
# Storage CIM hides disk association from this machine's ordinary user token.
# Runtime inspection uses the actual mount-point volume GUID and size instead;
# full image/disk/partition association is checked by the administrator on Mount.
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class P1VolumeNative {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool GetVolumeNameForVolumeMountPoint(string path, StringBuilder name, uint size);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);
}
'@
$volumeName = New-Object Text.StringBuilder 128
if (![P1VolumeNative]::GetVolumeNameForVolumeMountPoint($mountPath, $volumeName, 128)) { throw 'Dedicated mount missing; no host-directory fallback' }
if ($volumeName.ToString() -ne $manifest.volumeUniqueId) { throw 'Mounted volume identity mismatch' }
[UInt64]$available=0; [UInt64]$total=0; [UInt64]$free=0
if (![P1VolumeNative]::GetDiskFreeSpaceEx($mountPath, [ref]$available, [ref]$total, [ref]$free) -or $total -gt $capacity -or $total -lt 1GB) { throw 'Mounted volume capacity mismatch' }
if ($Action -ne 'Inspect') {
    # Fresh NTFS defaults do not grant the ordinary owner all rights required to
    # label its per-run directories. Configure ONLY the verified dedicated root.
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $owner = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.SetOwner($owner)
    foreach ($sid in @($owner, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    # SetNamedSecurityInfo on the mount-point name can update the host-side
    # reparse object instead of the mounted root. Address the verified volume.
    ([IO.DirectoryInfo]::new($volumeName.ToString())).SetAccessControl($acl)
}
[ordered]@{ status='verified'; verificationScope='mount-volume-guid-and-capacity'; imagePath=$imagePath; mountPath=$mountPath; capacityBytes=$capacity; volumeBytes=$total; freeBytes=$free; volumeUniqueId=$volumeName.ToString(); realCodeEnabled=$false } | ConvertTo-Json
