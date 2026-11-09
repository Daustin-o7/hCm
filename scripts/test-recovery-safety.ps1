#requires -Version 7.0
[CmdletBinding()]
param([string]$ArtifactsPath = $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }))
. "$PSScriptRoot/recovery-common.ps1"
$root = Join-Path (Assert-ProtectedRecoveryPath $ArtifactsPath) ('recovery-selfcheck-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$results = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [scriptblock]$Action, [bool]$Reject = $false) {
    $threw = $false
    try { & $Action | Out-Null } catch { $threw = $true }
    $passed = $threw -eq $Reject
    $results.Add(@{ name = $Name; passed = $passed })
    if (!$passed) { throw "Self-check failed: $Name" }
}
function Save-Manifest($Manifest) {
    $Manifest | ConvertTo-Json -Depth 6 | Set-Content "$root/manifest.json"
    (Get-FileHash "$root/manifest.json" -Algorithm SHA256).Hash | Set-Content "$root/manifest.json.sha256"
}
foreach ($directory in @('uploads', 'keys', 'configuration')) { New-Item -ItemType Directory -Path "$root/$directory" | Out-Null }
foreach ($file in @('database.dump', 'roles.sql', 'uploads/synthetic.txt', 'keys/synthetic.txt', 'configuration/synthetic.txt')) {
    Set-Content "$root/$file" 'synthetic self-check only'
}
$manifest = @{ version = 1; writersStopped = $true; protectedStorageConfirmed = $true; files = @(Get-ChildItem $root -Recurse -File | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash $_.FullName).Hash }
}) }
Save-Manifest $manifest
Check 'complete synthetic manifest accepted' { Get-RecoveryManifest $root }
Check 'verify-only entry point performs no restore' { & "$PSScriptRoot/restore-db.ps1" -BackupDirectory $root -VerifyOnly }
Check 'missing manifest checksum rejected' { Remove-Item "$root/manifest.json.sha256"; Get-RecoveryManifest $root } $true
Save-Manifest $manifest
Check 'damaged file rejected' { Add-Content "$root/uploads/synthetic.txt" 'changed'; Get-RecoveryManifest $root } $true
Set-Content "$root/uploads/synthetic.txt" 'synthetic self-check only'
Check 'missing key rejected' { Rename-Item "$root/keys/synthetic.txt" 'missing.txt'; Get-RecoveryManifest $root } $true
Rename-Item "$root/keys/missing.txt" 'synthetic.txt'
Check 'unmanifested nested manifest rejected' { Set-Content "$root/uploads/manifest.json" '{}'; Get-RecoveryManifest $root } $true
Remove-Item "$root/uploads/manifest.json"
$original = $manifest.files[0].path
Check 'traversal rejected' { $manifest.files[0].path = '../outside'; Save-Manifest $manifest; Get-RecoveryManifest $root } $true
$manifest.files[0].path = $original
Save-Manifest $manifest
Check 'non-boolean stopped-writers claim rejected' { $manifest.writersStopped = 'false'; Save-Manifest $manifest; Get-RecoveryManifest $root } $true
$manifest.writersStopped = $true
Save-Manifest $manifest
Check 'ambiguous normalized path rejected' { $manifest.files[0].path = 'uploads//synthetic.txt'; Save-Manifest $manifest; Get-RecoveryManifest $root } $true
$manifest.files[0].path = $original
Save-Manifest $manifest
Check 'zero-byte database dump rejected' {
    [IO.File]::WriteAllBytes("$root/database.dump", [byte[]]@())
    $entry = $manifest.files | Where-Object path -eq 'database.dump'
    $entry.bytes = 0; $entry.sha256 = (Get-FileHash "$root/database.dump").Hash
    Save-Manifest $manifest
    Get-RecoveryManifest $root
} $true
Set-Content "$root/database.dump" 'synthetic self-check only'
$entry = $manifest.files | Where-Object path -eq 'database.dump'
$entry.bytes = (Get-Item "$root/database.dump").Length; $entry.sha256 = (Get-FileHash "$root/database.dump").Hash
Save-Manifest $manifest
Check 'source repository destination rejected' { Assert-ProtectedRecoveryPath (Split-Path $PSScriptRoot -Parent) } $true
Check 'production database name rejected' { Assert-IsolatedRestoreTarget 'localhost' 5432 'hospital_crm' $true } $true
Check 'remote host rejected' { Assert-IsolatedRestoreTarget 'production.example' 5432 'hospital_crm_restore_123456abcdef' $true } $true
Check 'loopback lookalike rejected' { Assert-IsolatedRestoreTarget 'localhost.evil.example' 5432 'hospital_crm_restore_123456abcdef' $true } $true
Check 'missing approval rejected' { Assert-IsolatedRestoreTarget 'localhost' 5432 'hospital_crm_restore_123456abcdef' $false } $true
Check 'out-of-range port rejected' { Assert-IsolatedRestoreTarget 'localhost' 0 'hospital_crm_restore_123456abcdef' $true } $true
Check 'explicit isolated target accepted' { Assert-IsolatedRestoreTarget '127.0.0.1' 55432 'hospital_crm_restore_123456abcdef' $true }
Check 'restore entry point rejects live name before connecting' { & "$PSScriptRoot/restore-db.ps1" -BackupDirectory $root -PgHost localhost -Database hospital_crm -ApprovedIsolatedTarget } $true
Check 'restore output cannot alter the original recovery set' {
    try {
        & "$PSScriptRoot/restore-db.ps1" -BackupDirectory $root -PgHost 127.0.0.1 -Database hospital_crm_restore_123456abcdef -User synthetic -RestoreFilesDirectory "$root/restored" -ApprovedIsolatedTarget -ProtectedStorageConfirmed -RolesReviewed
    } catch {
        if ($_.Exception.Message -eq 'Restore output must not alter the original recovery set.') { return }
        throw
    }
    throw 'Expected recovery-set boundary rejection before connecting.'
}
Check 'backup refuses unstopped writers before connecting' { & "$PSScriptRoot/backup-db.ps1" -BackupDir $root -PgHost localhost -Database synthetic -User synthetic -UploadsPath "$root/uploads" -KeysPath "$root/keys" -ConfigurationPath "$root/configuration" -ReleaseId synthetic } $true
$results | ConvertTo-Json | Set-Content (Join-Path $ArtifactsPath 'part2-recovery-selfcheck.json')
Write-Output "$($results.Count) recovery safety checks passed. Synthetic files only; no PostgreSQL call or actual backup/restore was performed."
