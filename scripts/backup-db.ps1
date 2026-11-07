#requires -Version 7.0
# Complete cold recovery set. Destination encryption/ACLs and stopped writers must be independently verified.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupDir,
    [Parameter(Mandatory)][string]$PgHost,
    [ValidateRange(1, 65535)][int]$PgPort = 5432,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9_]+$')][string]$Database,
    [Parameter(Mandatory)][string]$User,
    [Parameter(Mandatory)][string]$UploadsPath,
    [Parameter(Mandatory)][string]$KeysPath,
    [Parameter(Mandatory)][string]$ConfigurationPath,
    [Parameter(Mandatory)][string]$ReleaseId,
    [switch]$WritersStopped,
    [switch]$ProtectedStorageConfirmed
)
. "$PSScriptRoot/recovery-common.ps1"
if (!$WritersStopped -or !$ProtectedStorageConfirmed) { throw 'Stop all application/job writers and verify encrypted restricted destination storage before backup.' }
$root = Assert-ProtectedRecoveryPath $BackupDir
if (!(Test-Path -LiteralPath $root -PathType Container)) { throw 'Provision the protected backup destination before running this script.' }
Assert-NoRecoveryLinks $root
$sources = @{ uploads = $UploadsPath; keys = $KeysPath; configuration = $ConfigurationPath }
foreach ($path in $sources.Values) {
    if (!(Test-Path -LiteralPath $path -PathType Container)) { throw 'Every configured recovery source directory must exist.' }
    Assert-NoRecoveryLinks $path
    $source = [IO.Path]::GetFullPath($path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if ($root -eq $source -or $root.StartsWith($source + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Backup destination must not be inside a recovery source.'
    }
}
foreach ($name in @('keys', 'configuration')) {
    if (!@(Get-ChildItem -LiteralPath $sources[$name] -File -Recurse -Force).Count) { throw 'Key and configuration recovery directories must not be empty.' }
}
$started = [DateTimeOffset]::UtcNow
$target = Join-Path $root ('backup_' + $started.ToString('yyyyMMdd_HHmmssZ') + '_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $target | Out-Null
if (!$IsWindows) { [IO.File]::SetUnixFileMode($target, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) }
$env:PGCONNECT_TIMEOUT = '5'
$pg = @('--no-password', "--host=$PgHost", "--port=$PgPort", "--username=$User")
Invoke-RecoveryPg pg_dump ($pg + @('--format=custom', '--blobs', "--file=$(Join-Path $target 'database.dump')", $Database)) | Out-Null
Invoke-RecoveryPg pg_dumpall ($pg + @('--roles-only', '--no-role-passwords', "--database=$Database", "--file=$(Join-Path $target 'roles.sql')")) | Out-Null
foreach ($name in $sources.Keys) {
    $destination = Join-Path $target $name
    New-Item -ItemType Directory -Path $destination | Out-Null
    foreach ($child in Get-ChildItem -LiteralPath $sources[$name] -Force) {
        Copy-Item -LiteralPath $child.FullName -Destination $destination -Recurse -Force
    }
}
$files = @(Get-ChildItem -LiteralPath $target -File -Recurse -Force | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($target, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
})
@{ version = 1; startedUtc = $started.ToString('O'); completedUtc = [DateTimeOffset]::UtcNow.ToString('O'); releaseId = $ReleaseId;
   writersStopped = $true; protectedStorageConfirmed = $true; files = $files } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $target 'manifest.json') -Encoding utf8
(Get-FileHash -LiteralPath (Join-Path $target 'manifest.json') -Algorithm SHA256).Hash |
    Set-Content -LiteralPath (Join-Path $target 'manifest.json.sha256') -Encoding ascii
$null = Get-RecoveryManifest $target
Write-Output "Recovery set created: $target ($($files.Count) files). Offsite retrieval and business recovery remain unverified."
