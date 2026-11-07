# Shared by the backup/restore entry points. No operation runs when dot-sourced.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-ProtectedRecoveryPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
    if ($full -eq $repo -or $full.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Recovery material must be outside the source repository on approved encrypted storage.'
    }
    return $full
}

function Assert-NoRecoveryLinks([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    for ($ancestor = $item; $null -ne $ancestor; $ancestor = [IO.Directory]::GetParent($ancestor.FullName)) {
        if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Recovery paths must not contain symbolic links or junctions.' }
    }
    if ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $Path -Force) {
            Assert-NoRecoveryLinks $child.FullName
        }
    }
}

function Get-RecoveryManifest([string]$Path) {
    Assert-NoRecoveryLinks $Path
    $manifestPath = Join-Path $Path 'manifest.json'
    $checksumPath = Join-Path $Path 'manifest.json.sha256'
    if (!(Test-Path -LiteralPath $checksumPath -PathType Leaf)) { throw 'Recovery manifest checksum is required.' }
    $expected = (Get-Content -LiteralPath $checksumPath -Raw).Trim()
    if ($expected -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -ne $expected) {
        throw 'Recovery manifest checksum mismatch.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.version -ne 1 -or $manifest.writersStopped -isnot [bool] -or $manifest.writersStopped -ne $true -or
        $manifest.protectedStorageConfirmed -isnot [bool] -or $manifest.protectedStorageConfirmed -ne $true) { throw 'Unsupported or incomplete recovery set.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        if ($entry.path -notmatch '^(database\.dump|roles\.sql|uploads/[^\\:]+|keys/[^\\:]+|configuration/[^\\:]+)$' -or
            @($entry.path.Split('/') | Where-Object { !$_ -or $_ -match '[. ]$|[<>"|?*\x00-\x1f]' }).Count -or !$seen.Add($entry.path)) {
            throw 'Unsafe or duplicate recovery manifest path.'
        }
        $file = Join-Path $Path $entry.path
        if (!(Test-Path -LiteralPath $file -PathType Leaf) -or $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
            (Get-Item -LiteralPath $file).Length -ne $entry.bytes -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Recovery file missing, truncated or checksum mismatch.' }
    }
    if (!$seen.Contains('database.dump') -or !$seen.Contains('roles.sql') -or
        !@($manifest.files | Where-Object path -like 'keys/*').Count -or
        !@($manifest.files | Where-Object path -like 'configuration/*').Count) { throw 'Database, roles, key material and configuration are required.' }
    if (@($manifest.files | Where-Object { $_.path -in @('database.dump', 'roles.sql') -and $_.bytes -le 0 }).Count) {
        throw 'Database dump and role inventory must not be empty.'
    }
    foreach ($directory in @('uploads', 'keys', 'configuration')) {
        if (!(Test-Path -LiteralPath (Join-Path $Path $directory) -PathType Container)) { throw 'Recovery directory missing.' }
    }
    $actualFiles = @(Get-ChildItem -LiteralPath $Path -File -Recurse -Force | Where-Object {
        [IO.Path]::GetRelativePath($Path, $_.FullName) -notin @('manifest.json', 'manifest.json.sha256')
    })
    if ($actualFiles.Count -ne $seen.Count) { throw 'Recovery set contains unmanifested files.' }
    return $manifest
}

function Assert-IsolatedRestoreTarget([string]$HostName, [int]$Port, [string]$Database, [bool]$Approved) {
    if (!$Approved -or $HostName -cnotin @('localhost', '127.0.0.1', '::1') -or $Port -lt 1 -or $Port -gt 65535 -or
        $Database -cnotmatch '^hospital_crm_restore_[a-f0-9]{12}$') {
        throw 'Restore requires explicit approval, a literal loopback host and a new hospital_crm_restore_<12 lowercase hex> database.'
    }
}

function Invoke-RecoveryPg([string]$Tool, [string[]]$Arguments) {
    $command = Get-Command $Tool -CommandType Application -ErrorAction Stop
    $output = & $command.Source @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { throw "$Tool failed; recovery is incomplete. Diagnose only in the restricted operator environment." }
    return $output
}
