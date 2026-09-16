[CmdletBinding()]
param(
    [string]$BackupRoot = (Join-Path $env:TEMP "Enma\backups"),
    [string]$SourcePostgresContainer = "enma-postgres",
    [string]$SourceMinioContainer = "enma-minio",
    [string]$BucketName = "enma-documents",
    [string]$DockerCommand = "docker",
    [switch]$ConfirmQuiesced
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "BackupRestore.Common.ps1")

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$containerTemporaryRoot = $null
$partialDirectory = $null
$backupRootFullPath = Get-NormalizedFullPath -Path $BackupRoot

try {
    if (-not $ConfirmQuiesced) {
        throw "Backup consistent requires API without writers during the window. Pass -ConfirmQuiesced only after stopping Enma.Api."
    }

    Assert-SafeDockerName -Name $SourcePostgresContainer
    Assert-SafeDockerName -Name $SourceMinioContainer
    Assert-SafeDockerName -Name $BucketName
    Assert-CommandAvailable -Command $DockerCommand
    Assert-CommandAvailable -Command "git"
    Assert-EnmaApiStopped

    Assert-OutsideRepository -Path $backupRootFullPath -RepositoryRoot $repositoryRoot
    if (-not (Test-Path -LiteralPath $backupRootFullPath -PathType Container)) {
        New-Item -ItemType Directory -Path $backupRootFullPath | Out-Null
    }

    $backupId = "enma-{0}-{1}" -f `
        [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ"),
        ([Guid]::NewGuid().ToString("N").Substring(0, 8))
    $finalDirectory = Join-Path $backupRootFullPath $backupId
    $partialDirectory = "$finalDirectory.partial"
    if ((Test-Path -LiteralPath $finalDirectory) -or
        (Test-Path -LiteralPath $partialDirectory)) {
        throw "Backup destination already exists."
    }
    New-Item -ItemType Directory -Path $partialDirectory | Out-Null
    Protect-SensitiveDirectory -Path $partialDirectory

    Write-Host "Capturing read-only source evidence."
    $sourceBefore = Get-SourceEvidence `
        -DockerCommand $DockerCommand `
        -PostgresContainer $SourcePostgresContainer `
        -MinioContainer $SourceMinioContainer `
        -BucketName $BucketName

    $token = [Guid]::NewGuid().ToString("N")
    $containerTemporaryRoot = "/tmp/enma-backup-$token"
    $dumpContainerPath = "$containerTemporaryRoot/database.dump"

    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "exec", $SourcePostgresContainer,
        "sh", "-c",
        'set -eu; mkdir -p "$1"; pg_dump -Fc -U "$POSTGRES_USER" -d "$POSTGRES_DB" -f "$2"; pg_restore -l "$2" >/dev/null',
        "enma-backup", $containerTemporaryRoot, $dumpContainerPath
    ) -FailureMessage "PostgreSQL backup failed." | Out-Null

    $dumpPath = Join-Path $partialDirectory "database.dump"
    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "cp", "${SourcePostgresContainer}:$dumpContainerPath", $dumpPath
    ) -FailureMessage "Copying the PostgreSQL dump failed." | Out-Null

    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "exec", $SourceMinioContainer,
        "sh", "-c",
        'set -eu; mkdir -p "$2/objects"; export MC_HOST_enmasource="http://$MINIO_ROOT_USER:$MINIO_ROOT_PASSWORD@127.0.0.1:9000"; mc mirror --overwrite "enmasource/$1" "$2/objects" >/dev/null',
        "enma-backup", $BucketName, $containerTemporaryRoot
    ) -FailureMessage "Object-storage backup failed." | Out-Null

    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "cp", "${SourceMinioContainer}:$containerTemporaryRoot/objects", $partialDirectory
    ) -FailureMessage "Copying the object backup failed." | Out-Null

    $objectsDirectory = Join-Path $partialDirectory "objects"
    $objectInventory = @(Get-ObjectInventory -ObjectsDirectory $objectsDirectory)
    $objectTotalBytes = 0L
    foreach ($item in $objectInventory) {
        $objectTotalBytes += [int64]$item.sizeBytes
    }

    if ($objectInventory.Count -ne $sourceBefore.objectCount -or
        $objectTotalBytes -ne $sourceBefore.objectTotalBytes) {
        throw "Object backup count or byte total does not match the quiesced source."
    }

    $sourceAfter = Get-SourceEvidence `
        -DockerCommand $DockerCommand `
        -PostgresContainer $SourcePostgresContainer `
        -MinioContainer $SourceMinioContainer `
        -BucketName $BucketName
    if (-not (Test-EquivalentJson -Left $sourceBefore -Right $sourceAfter)) {
        throw "Source evidence changed during backup; the snapshot is not consistent."
    }

    $postgresVersion = Get-PostgresQuery `
        -DockerCommand $DockerCommand `
        -ContainerName $SourcePostgresContainer `
        -Query "SHOW server_version;"
    $databaseName = Get-PostgresQuery `
        -DockerCommand $DockerCommand `
        -ContainerName $SourcePostgresContainer `
        -Query "SELECT current_database();"
    $pgDumpVersion = (Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "exec", $SourcePostgresContainer, "pg_dump", "--version"
    ) -FailureMessage "Reading pg_dump version failed." | Select-Object -First 1).Trim()
    $mcVersion = (Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "exec", $SourceMinioContainer, "mc", "--version"
    ) -FailureMessage "Reading MinIO client version failed." | Select-Object -First 1).Trim()
    $gitCheckpoint = (Invoke-NativeCapture -Command "git" -Arguments @(
        "-C", $repositoryRoot, "rev-parse", "--short", "HEAD"
    ) -FailureMessage "Reading Git checkpoint failed." | Select-Object -First 1).Trim()

    $manifest = [ordered]@{
        formatVersion = 1
        backupId = $backupId
        createdAtUtc = [DateTime]::UtcNow.ToString("o")
        mode = "quiesced"
        gitCheckpoint = $gitCheckpoint
        database = [ordered]@{
            name = $databaseName
            postgresVersion = $postgresVersion
            toolVersion = $pgDumpVersion
            latestMigration = $sourceBefore.latestMigration
            dumpFile = "database.dump"
            dumpSizeBytes = [int64](Get-Item -LiteralPath $dumpPath).Length
            dumpSha256 = Get-Sha256Hex -Path $dumpPath
            counts = $sourceBefore.databaseCounts
        }
        objectStorage = [ordered]@{
            bucket = $BucketName
            toolVersion = $mcVersion
            directory = "objects"
            objectCount = $objectInventory.Count
            totalBytes = $objectTotalBytes
            inventorySha256 = Get-StringSha256 -Value ($objectInventory | ConvertTo-Json -Depth 5 -Compress)
            objects = $objectInventory
        }
        sourceEvidence = [ordered]@{
            before = $sourceBefore
            after = $sourceAfter
        }
    }

    Write-JsonFile -Value $manifest -Path (Join-Path $partialDirectory "manifest.json")
    Move-Item -LiteralPath $partialDirectory -Destination $finalDirectory
    $partialDirectory = $null

    Write-Host "BACKUP PASS"
    Write-Host "Backup directory: $finalDirectory"
    Write-Host "Dump SHA-256: $($manifest.database.dumpSha256)"
    Write-Host "Objects: $($manifest.objectStorage.objectCount); bytes: $($manifest.objectStorage.totalBytes)"
}
finally {
    if ($null -ne $containerTemporaryRoot -and
        $containerTemporaryRoot -match '^/tmp/enma-backup-[0-9a-f]{32}$') {
        foreach ($container in @($SourcePostgresContainer, $SourceMinioContainer)) {
            try {
                & $DockerCommand exec $container sh -c 'rm -rf -- "$1"' "enma-backup-cleanup" $containerTemporaryRoot 2>$null
            }
            catch {
                Write-Warning "Temporary container backup files could not be removed."
            }
        }
    }

    if ($null -ne $partialDirectory -and
        (Test-Path -LiteralPath $partialDirectory) -and
        (Test-PathWithin -Path $partialDirectory -Parent $backupRootFullPath)) {
        [System.IO.Directory]::Delete($partialDirectory, $true)
        Write-Warning "Incomplete backup data was removed."
    }
}
