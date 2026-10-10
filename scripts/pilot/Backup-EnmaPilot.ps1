[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigRoot,
    [Parameter(Mandatory = $true)][string]$BackupRoot,
    [switch]$ConfirmQuiesced,
    [string]$DockerCommand = "docker"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "..\operations\BackupRestore.Common.ps1")
. (Join-Path $PSScriptRoot "Pilot.Common.ps1")

if (-not $ConfirmQuiesced) {
    throw "Pilot backup requires -ConfirmQuiesced after the Pilot application is stopped."
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$configRootFullPath = [System.IO.Path]::GetFullPath($ConfigRoot)
$backupRootFullPath = [System.IO.Path]::GetFullPath($BackupRoot)
Assert-PathOutsideRepository -Path $configRootFullPath -RepositoryRoot $repositoryRoot
Assert-PathOutsideRepository -Path $backupRootFullPath -RepositoryRoot $repositoryRoot
$temporaryRoot = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd('\', '/')
if ($backupRootFullPath.Equals($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    $backupRootFullPath.StartsWith(
        $temporaryRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Pilot backups must not use the system temporary directory."
}
if (-not (Test-Path -LiteralPath $backupRootFullPath -PathType Container)) {
    throw "Pilot backup root must already exist on the approved external device."
}

$settings = Read-PilotEnvironment -Path (Join-Path $configRootFullPath "pilot.env")
Assert-PilotIdentitySettings -Settings $settings
$keysPath = [System.IO.Path]::GetFullPath($settings.PILOT_DATA_PROTECTION_KEYS_PATH)
Assert-PathWithin -Path $keysPath -Parent $configRootFullPath
if (-not (Test-Path -LiteralPath $keysPath -PathType Container) -or
    @(Get-ChildItem -LiteralPath $keysPath -File -Filter "key-*.xml").Count -lt 1) {
    throw "Pilot Data Protection key ring is missing or empty."
}

$docker = Assert-CommandAvailable -Command $DockerCommand
foreach ($container in @("enma-pilot-postgres", "enma-pilot-minio")) {
    Assert-PilotDockerResourceOwnership `
        -Kind container -Name $container -DockerCommand $docker
    if (-not (Test-PilotContainerRunning -Name $container -DockerCommand $docker)) {
        throw "Pilot source container '$container' is not running."
    }
}
Assert-PilotContainerEnvironment -Name "enma-pilot-postgres" -DockerCommand $docker -Expected @{
    POSTGRES_DB = $settings.PILOT_POSTGRES_DB
    POSTGRES_USER = $settings.PILOT_POSTGRES_USER
    POSTGRES_PASSWORD = $settings.PILOT_POSTGRES_PASSWORD
}
Assert-PilotContainerEnvironment -Name "enma-pilot-minio" -DockerCommand $docker -Expected @{
    MINIO_ROOT_USER = $settings.PILOT_MINIO_ROOT_USER
    MINIO_ROOT_PASSWORD = $settings.PILOT_MINIO_ROOT_PASSWORD
}

$before = @{}
foreach ($directory in Get-ChildItem -LiteralPath $backupRootFullPath -Directory) {
    $before[$directory.FullName] = $true
}

$backupScript = Join-Path $PSScriptRoot "..\operations\Backup-Enma.ps1"
$null = & $backupScript `
    -BackupRoot $backupRootFullPath `
    -SourcePostgresContainer "enma-pilot-postgres" `
    -SourceMinioContainer "enma-pilot-minio" `
    -BucketName "enma-pilot-documents" `
    -DockerCommand $docker `
    -ConfirmQuiesced 6>$null

$created = @(Get-ChildItem -LiteralPath $backupRootFullPath -Directory |
    Where-Object { -not $before.ContainsKey($_.FullName) -and $_.Name -notlike "*.partial" })
if ($created.Count -ne 1) {
    throw "Pilot backup could not identify exactly one new backup directory."
}

$backupDirectory = $created[0].FullName
try {
    $recoveryDirectory = Join-Path $backupDirectory "recovery"
    $keyBackupDirectory = Join-Path $recoveryDirectory "data-protection-keys"
    New-Item -ItemType Directory -Path $keyBackupDirectory | Out-Null
    Get-ChildItem -LiteralPath $keysPath -File -Filter "key-*.xml" |
        Copy-Item -Destination $keyBackupDirectory

    $keyInventory = @(Get-ObjectInventory -ObjectsDirectory $keyBackupDirectory)
    if ($keyInventory.Count -lt 1) {
        throw "Pilot Data Protection key backup is empty."
    }
    $manifestPath = Join-Path $backupDirectory "manifest.json"
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest | Add-Member -NotePropertyName environment -NotePropertyValue "Pilot"
    $manifest | Add-Member -NotePropertyName recovery -NotePropertyValue ([pscustomobject]@{
        dataProtectionKeysDirectory = "recovery/data-protection-keys"
        dataProtectionKeys = $keyInventory
        inventorySha256 = Get-StringSha256 -Value (
            $keyInventory | ConvertTo-Json -Depth 5 -Compress)
    })
    Write-JsonFile -Value $manifest -Path $manifestPath
}
catch {
    $incompletePath = "$backupDirectory.incomplete"
    if (-not (Test-Path -LiteralPath $incompletePath)) {
        Move-Item -LiteralPath $backupDirectory -Destination $incompletePath
    }
    throw
}

Write-Host "PILOT BACKUP PASS"
Write-Host "Backup directory: $backupDirectory"
Write-Host "Database, documents, and Data Protection keys have integrity inventories."
