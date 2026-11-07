#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupDirectory,
    [switch]$VerifyOnly,
    [string]$PgHost,
    [int]$PgPort = 5432,
    [string]$Database,
    [string]$User,
    [string]$RestoreFilesDirectory,
    [switch]$ApprovedIsolatedTarget,
    [switch]$ProtectedStorageConfirmed,
    [switch]$RolesReviewed
)
. "$PSScriptRoot/recovery-common.ps1"
$source = Assert-ProtectedRecoveryPath $BackupDirectory
$manifest = Get-RecoveryManifest $source
if ($VerifyOnly) { Write-Output "Recovery set integrity passed ($(@($manifest.files).Count) files); no database or file restore performed."; return }
Assert-IsolatedRestoreTarget $PgHost $PgPort $Database $ApprovedIsolatedTarget.IsPresent
if (!$ProtectedStorageConfirmed -or !$RolesReviewed -or [string]::IsNullOrWhiteSpace($User) -or [string]::IsNullOrWhiteSpace($RestoreFilesDirectory)) {
    throw 'Reviewed pre-provisioned owners/roles, protected destination and an explicit restore user/files directory are required.'
}
$destination = Assert-ProtectedRecoveryPath $RestoreFilesDirectory
if (Test-Path -LiteralPath $destination) { throw 'The restored-files directory must not exist.' }
if ($destination.StartsWith($source.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Restore output must not alter the original recovery set.'
}
$parent = Split-Path $destination -Parent
Assert-NoRecoveryLinks $parent
$env:PGCONNECT_TIMEOUT = '5'
$pg = @('--no-password', "--host=$PgHost", "--port=$PgPort", "--username=$User", "--dbname=$Database")
# No CREATE/DROP database, --clean, --no-owner or --no-privileges: fail if anything conflicts.
$query = "SELECT current_database() = '$Database' AND NOT EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname NOT IN ('pg_catalog','information_schema') AND n.nspname NOT LIKE 'pg_toast%') AND NOT EXISTS (SELECT FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname NOT IN ('pg_catalog','information_schema'));"
$empty = Invoke-RecoveryPg psql ($pg + @('-X', '-A', '-t', '-v', 'ON_ERROR_STOP=1', '-c', $query))
if (($empty -join '').Trim() -ne 't') { throw 'Restore target is not an empty isolated database.' }
Invoke-RecoveryPg pg_restore ($pg + @('--exit-on-error', '--single-transaction', (Join-Path $source 'database.dump'))) | Out-Null
New-Item -ItemType Directory -Path $destination | Out-Null
if (!$IsWindows) { [IO.File]::SetUnixFileMode($destination, [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute) }
foreach ($name in @('uploads', 'keys', 'configuration')) {
    Copy-Item -LiteralPath (Join-Path $source $name) -Destination $destination -Recurse
}
foreach ($entry in $manifest.files | Where-Object path -match '^(uploads|keys|configuration)/') {
    if ((Get-FileHash -LiteralPath (Join-Path $destination $entry.path) -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Restored file checksum mismatch; keep the isolated target offline.' }
}
Write-Output 'Database and files restored into the isolated target. Keep jobs/egress stopped; role, RLS, business, key-decryption and credential-cutover acceptance is still required.'
