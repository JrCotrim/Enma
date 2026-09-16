[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$BackupPath,
    [string]$ReportRoot = (Join-Path $env:TEMP "Enma\restore-drills"),
    [string]$SourcePostgresContainer = "enma-postgres",
    [string]$SourceMinioContainer = "enma-minio",
    [string]$PostgresContainerName,
    [string]$MinioContainerName,
    [string]$PostgresVolumeName,
    [string]$MinioVolumeName,
    [string]$DockerCommand = "docker",
    [switch]$KeepDrillResources
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "BackupRestore.Common.ps1")

function Assert-DrillResourceName {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string[]]$ForbiddenNames
    )

    Assert-SafeDockerName -Name $Name
    if ($Name -notmatch 'restore-drill' -or $ForbiddenNames -contains $Name) {
        throw "Restore target '$Name' is not an isolated drill resource."
    }
}

function Assert-DockerTargetAbsent {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("container", "volume")][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        if ($Kind -eq "container") {
            & $DockerCommand inspect $Name 2>&1 | Out-Null
        }
        else {
            & $DockerCommand volume inspect $Name 2>&1 | Out-Null
        }
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -eq 0) {
        throw "Restore-drill $Kind target already exists."
    }
}

function Wait-ForPostgres {
    param([Parameter(Mandatory = $true)][string]$ContainerName)

    for ($attempt = 1; $attempt -le 60; $attempt++) {
        & $DockerCommand exec $ContainerName sh -c `
            'pg_isready -U "$POSTGRES_USER" -d "$POSTGRES_DB" >/dev/null 2>&1' 2>$null
        if ($LASTEXITCODE -eq 0) {
            return
        }
        Start-Sleep -Seconds 1
    }
    throw "Restore-drill PostgreSQL did not become ready."
}

function Wait-ForMinio {
    param([Parameter(Mandatory = $true)][string]$ContainerName)

    for ($attempt = 1; $attempt -le 60; $attempt++) {
        & $DockerCommand exec $ContainerName mc ready local 2>$null | Out-Null
        if ($LASTEXITCODE -eq 0) {
            return
        }
        Start-Sleep -Seconds 1
    }
    throw "Restore-drill MinIO did not become ready."
}

function Test-DocumentFormat {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ContentType
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    switch ($ContentType) {
        "application/pdf" {
            return $bytes.Length -ge 5 -and
                [System.Text.Encoding]::ASCII.GetString($bytes, 0, 5) -ceq "%PDF-"
        }
        "image/png" {
            $signature = [byte[]](0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a)
            if ($bytes.Length -lt $signature.Length) { return $false }
            for ($index = 0; $index -lt $signature.Length; $index++) {
                if ($bytes[$index] -ne $signature[$index]) { return $false }
            }
            return $true
        }
        "image/jpeg" {
            return $bytes.Length -ge 3 -and
                $bytes[0] -eq 0xff -and $bytes[1] -eq 0xd8 -and $bytes[2] -eq 0xff
        }
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document" {
            $requiredEntry = "word/document.xml"
        }
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" {
            $requiredEntry = "xl/workbook.xml"
        }
        default { return $false }
    }

    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive(
            $stream,
            [System.IO.Compression.ZipArchiveMode]::Read,
            $false)
        try {
            return $null -ne $archive.GetEntry("[Content_Types].xml") -and
                $null -ne $archive.GetEntry($requiredEntry)
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-CountsMatch {
    param(
        [Parameter(Mandatory = $true)]$Expected,
        [Parameter(Mandatory = $true)]$Actual
    )

    foreach ($property in $Expected.PSObject.Properties) {
        $actualProperty = $Actual.PSObject.Properties[$property.Name]
        if ($null -eq $actualProperty -or
            [int64]$actualProperty.Value -ne [int64]$property.Value) {
            throw "Restored database count mismatch for '$($property.Name)'."
        }
    }
}

function Remove-DrillContainer {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$DrillId
    )

    if ($Name -notmatch 'restore-drill') {
        throw "Refusing to remove a container without the restore-drill marker."
    }
    $labelsJson = Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "inspect", "--format", "{{json .Config.Labels}}", $Name
    ) -FailureMessage "Unable to verify drill container ownership."
    $labels = (($labelsJson -join "").Trim() | ConvertFrom-Json)
    if ([string]$labels.'enma.restore-drill' -cne $DrillId) {
        throw "Refusing to remove a container not owned by this drill."
    }
    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "rm", "-f", $Name
    ) -FailureMessage "Unable to remove drill container." | Out-Null
}

function Remove-DrillVolume {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$DrillId
    )

    if ($Name -notmatch 'restore-drill') {
        throw "Refusing to remove a volume without the restore-drill marker."
    }
    $labelsJson = Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "volume", "inspect", "--format", "{{json .Labels}}", $Name
    ) -FailureMessage "Unable to verify drill volume ownership."
    $labels = (($labelsJson -join "").Trim() | ConvertFrom-Json)
    if ([string]$labels.'enma.restore-drill' -cne $DrillId) {
        throw "Refusing to remove a volume not owned by this drill."
    }
    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "volume", "rm", $Name
    ) -FailureMessage "Unable to remove drill volume." | Out-Null
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$backupFullPath = Get-NormalizedFullPath -Path $BackupPath
Assert-OutsideRepository -Path $backupFullPath -RepositoryRoot $repositoryRoot
if (-not (Test-Path -LiteralPath $backupFullPath -PathType Container)) {
    throw "Backup directory is missing."
}

$manifestPath = Join-Path $backupFullPath "manifest.json"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Backup manifest is missing."
}
try {
    $manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
}
catch {
    throw "Backup manifest is invalid JSON."
}

$requiredManifestProperties = @(
    "formatVersion", "backupId", "mode", "database", "objectStorage"
)
foreach ($propertyName in $requiredManifestProperties) {
    if ($null -eq $manifest.PSObject.Properties[$propertyName]) {
        throw "Backup manifest is invalid: missing '$propertyName'."
    }
}
if ([int]$manifest.formatVersion -ne 1 -or $manifest.mode -cne "quiesced") {
    throw "Backup manifest format or consistency mode is unsupported."
}
if ($null -eq $manifest.database.PSObject.Properties["dumpFile"] -or
    $null -eq $manifest.database.PSObject.Properties["dumpSha256"] -or
    $null -eq $manifest.database.PSObject.Properties["latestMigration"] -or
    $null -eq $manifest.database.PSObject.Properties["counts"] -or
    $null -eq $manifest.objectStorage.PSObject.Properties["directory"] -or
    $null -eq $manifest.objectStorage.PSObject.Properties["objects"] -or
    $null -eq $manifest.objectStorage.PSObject.Properties["bucket"] -or
    $null -eq $manifest.objectStorage.PSObject.Properties["objectCount"] -or
    $null -eq $manifest.objectStorage.PSObject.Properties["totalBytes"] -or
    $null -eq $manifest.objectStorage.PSObject.Properties["inventorySha256"] -or
    $null -eq $manifest.database.PSObject.Properties["dumpSizeBytes"]) {
    throw "Backup manifest is missing required database or object-storage fields."
}

$dumpFileName = [string]$manifest.database.dumpFile
if ([System.IO.Path]::GetFileName($dumpFileName) -cne $dumpFileName) {
    throw "Dump filename must not contain a path."
}
$dumpPath = Get-ContainedChildPath -Parent $backupFullPath -RelativePath $dumpFileName
if (-not (Test-Path -LiteralPath $dumpPath -PathType Leaf)) {
    throw "PostgreSQL dump is missing."
}
if ((Get-Sha256Hex -Path $dumpPath) -cne ([string]$manifest.database.dumpSha256).ToLowerInvariant()) {
    throw "PostgreSQL dump SHA-256 mismatch."
}
if ([int64](Get-Item -LiteralPath $dumpPath).Length -ne [int64]$manifest.database.dumpSizeBytes) {
    throw "PostgreSQL dump size does not match the manifest."
}

$objectDirectoryName = [string]$manifest.objectStorage.directory
if ([System.IO.Path]::GetFileName($objectDirectoryName) -cne $objectDirectoryName) {
    throw "Object backup directory name must not contain a path."
}
$objectsDirectory = Get-ContainedChildPath -Parent $backupFullPath -RelativePath $objectDirectoryName
if (-not (Test-Path -LiteralPath $objectsDirectory -PathType Container)) {
    throw "Object backup is missing."
}
$backupInventory = @(Get-ObjectInventory -ObjectsDirectory $objectsDirectory)
$manifestInventory = @($manifest.objectStorage.objects)
if (-not (Test-EquivalentJson -Left $manifestInventory -Right $backupInventory)) {
    throw "Object backup inventory does not match the manifest."
}
$inventoryHash = Get-StringSha256 -Value ($backupInventory | ConvertTo-Json -Depth 5 -Compress)
if ($inventoryHash -cne ([string]$manifest.objectStorage.inventorySha256).ToLowerInvariant()) {
    throw "Object backup inventory SHA-256 mismatch."
}
$backupTotalBytes = 0L
foreach ($item in $backupInventory) { $backupTotalBytes += [int64]$item.sizeBytes }
if ($backupInventory.Count -ne [int]$manifest.objectStorage.objectCount -or
    $backupTotalBytes -ne [int64]$manifest.objectStorage.totalBytes) {
    throw "Object backup count or byte total does not match the manifest."
}

$drillId = "restore-drill-{0}" -f ([Guid]::NewGuid().ToString("N").Substring(0, 12))
if ([string]::IsNullOrWhiteSpace($PostgresContainerName)) { $PostgresContainerName = "enma-$drillId-postgres" }
if ([string]::IsNullOrWhiteSpace($MinioContainerName)) { $MinioContainerName = "enma-$drillId-minio" }
if ([string]::IsNullOrWhiteSpace($PostgresVolumeName)) { $PostgresVolumeName = "enma-$drillId-postgres-data" }
if ([string]::IsNullOrWhiteSpace($MinioVolumeName)) { $MinioVolumeName = "enma-$drillId-minio-data" }

Assert-DrillResourceName -Name $PostgresContainerName -ForbiddenNames @("enma-postgres")
Assert-DrillResourceName -Name $MinioContainerName -ForbiddenNames @("enma-minio")
Assert-DrillResourceName -Name $PostgresVolumeName -ForbiddenNames @("enma_postgres_data")
Assert-DrillResourceName -Name $MinioVolumeName -ForbiddenNames @("enma_minio_data")
Assert-SafeDockerName -Name ([string]$manifest.objectStorage.bucket)
Assert-CommandAvailable -Command $DockerCommand
Assert-DockerTargetAbsent -Kind container -Name $PostgresContainerName
Assert-DockerTargetAbsent -Kind container -Name $MinioContainerName
Assert-DockerTargetAbsent -Kind volume -Name $PostgresVolumeName
Assert-DockerTargetAbsent -Kind volume -Name $MinioVolumeName

$reportRootFullPath = Get-NormalizedFullPath -Path $ReportRoot
Assert-OutsideRepository -Path $reportRootFullPath -RepositoryRoot $repositoryRoot
if (-not (Test-Path -LiteralPath $reportRootFullPath -PathType Container)) {
    New-Item -ItemType Directory -Path $reportRootFullPath | Out-Null
}
$reportDirectory = Join-Path $reportRootFullPath $drillId
if (Test-Path -LiteralPath $reportDirectory) {
    throw "Restore report directory already exists."
}
New-Item -ItemType Directory -Path $reportDirectory | Out-Null
Protect-SensitiveDirectory -Path $reportDirectory

$secretDirectory = Join-Path $env:TEMP ("Enma\restore-secrets\" + $drillId)
New-Item -ItemType Directory -Path $secretDirectory -Force | Out-Null
Protect-SensitiveDirectory -Path $secretDirectory
$postgresEnvPath = Join-Path $secretDirectory "postgres.env"
$minioEnvPath = Join-Path $secretDirectory "minio.env"
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText(
    $postgresEnvPath,
    "POSTGRES_DB=enma_restore_drill`nPOSTGRES_USER=enma_restore_drill`nPOSTGRES_PASSWORD=$(New-RandomSecret)`n",
    $utf8WithoutBom)
[System.IO.File]::WriteAllText(
    $minioEnvPath,
    "MINIO_ROOT_USER=enma-restore-drill`nMINIO_ROOT_PASSWORD=$(New-RandomSecret)`n",
    $utf8WithoutBom)

$createdContainers = New-Object System.Collections.Generic.List[string]
$createdVolumes = New-Object System.Collections.Generic.List[string]
$startedAt = [DateTime]::UtcNow
$failure = $null
$report = [ordered]@{
    startedAtUtc = $startedAt.ToString("o")
    completedAtUtc = $null
    sourceBackupId = [string]$manifest.backupId
    target = [ordered]@{
        drillId = $drillId
        postgresContainer = $PostgresContainerName
        minioContainer = $MinioContainerName
        postgresVolume = $PostgresVolumeName
        minioVolume = $MinioVolumeName
        postgresContainerId = $null
        minioContainerId = $null
    }
    cleanTarget = [ordered]@{ postgres = $false; minio = $false }
    postgresRestore = "FAIL"
    latestMigration = $null
    databaseCounts = $null
    foreignKeysValidated = $false
    objectRestore = "FAIL"
    objectCount = 0
    missingObjects = @()
    orphanObjects = @()
    hashMismatches = @()
    metadataMismatches = @()
    documentUsability = $null
    sourceBefore = $null
    sourceAfter = $null
    sourcePreserved = $false
    overall = "FAIL"
    error = $null
}

try {
    $report.sourceBefore = Get-SourceEvidence `
        -DockerCommand $DockerCommand `
        -PostgresContainer $SourcePostgresContainer `
        -MinioContainer $SourceMinioContainer `
        -BucketName ([string]$manifest.objectStorage.bucket)

    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "volume", "create", "--label", "enma.restore-drill=$drillId", $PostgresVolumeName
    ) -FailureMessage "Creating PostgreSQL drill volume failed." | Out-Null
    $createdVolumes.Add($PostgresVolumeName)
    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "volume", "create", "--label", "enma.restore-drill=$drillId", $MinioVolumeName
    ) -FailureMessage "Creating MinIO drill volume failed." | Out-Null
    $createdVolumes.Add($MinioVolumeName)

    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "run", "-d", "--name", $PostgresContainerName,
        "--label", "enma.restore-drill=$drillId",
        "--env-file", $postgresEnvPath,
        "--mount", "source=$PostgresVolumeName,target=/var/lib/postgresql",
        "postgres:18-alpine"
    ) -FailureMessage "Starting isolated PostgreSQL failed." | Out-Null
    $createdContainers.Add($PostgresContainerName)
    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "run", "-d", "--name", $MinioContainerName,
        "--label", "enma.restore-drill=$drillId",
        "--env-file", $minioEnvPath,
        "--mount", "source=$MinioVolumeName,target=/data",
        "minio/minio:RELEASE.2025-09-07T16-13-09Z",
        "server", "/data"
    ) -FailureMessage "Starting isolated MinIO failed." | Out-Null
    $createdContainers.Add($MinioContainerName)

    Wait-ForPostgres -ContainerName $PostgresContainerName
    Wait-ForMinio -ContainerName $MinioContainerName
    $report.target.postgresContainerId = Get-ContainerId -DockerCommand $DockerCommand -ContainerName $PostgresContainerName
    $report.target.minioContainerId = Get-ContainerId -DockerCommand $DockerCommand -ContainerName $MinioContainerName

    $preRestoreTableCount = Get-PostgresQuery `
        -DockerCommand $DockerCommand `
        -ContainerName $PostgresContainerName `
        -Query "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public';"
    if ([int]$preRestoreTableCount -ne 0) {
        throw "PostgreSQL drill target is not empty."
    }
    $report.cleanTarget.postgres = $true

    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "exec", $MinioContainerName,
        "sh", "-c",
        'export MC_HOST_enmatarget="http://$MINIO_ROOT_USER:$MINIO_ROOT_PASSWORD@127.0.0.1:9000"; if mc stat "enmatarget/$1" >/dev/null 2>&1; then exit 9; fi',
        "enma-empty-check", ([string]$manifest.objectStorage.bucket)
    ) -FailureMessage "MinIO drill target is not empty." | Out-Null
    $report.cleanTarget.minio = $true

    $targetDumpPath = "/tmp/$drillId-database.dump"
    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "cp", $dumpPath, "${PostgresContainerName}:$targetDumpPath"
    ) -FailureMessage "Copying dump into drill PostgreSQL failed." | Out-Null
    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "exec", $PostgresContainerName,
        "sh", "-c",
        'pg_restore --exit-on-error --no-owner --no-privileges -U "$POSTGRES_USER" -d "$POSTGRES_DB" "$1"',
        "enma-restore", $targetDumpPath
    ) -FailureMessage "PostgreSQL restore failed." | Out-Null

    $restoredMigration = Get-PostgresQuery `
        -DockerCommand $DockerCommand `
        -ContainerName $PostgresContainerName `
        -Query 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;'
    if ($restoredMigration -cne [string]$manifest.database.latestMigration) {
        throw "Restored latest migration does not match the backup."
    }
    $restoredCounts = Get-DatabaseCounts -DockerCommand $DockerCommand -ContainerName $PostgresContainerName
    Assert-CountsMatch -Expected $manifest.database.counts -Actual $restoredCounts
    $unvalidatedForeignKeys = Get-PostgresQuery `
        -DockerCommand $DockerCommand `
        -ContainerName $PostgresContainerName `
        -Query "SELECT count(*) FROM pg_constraint WHERE contype = 'f' AND NOT convalidated;"
    if ([int]$unvalidatedForeignKeys -ne 0) {
        throw "Restored database has unvalidated foreign keys."
    }
    $report.postgresRestore = "PASS"
    $report.latestMigration = $restoredMigration
    $report.databaseCounts = $restoredCounts
    $report.foreignKeysValidated = $true

    $targetObjectRoot = "/tmp/$drillId"
    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "exec", $MinioContainerName, "mkdir", "-p", $targetObjectRoot
    ) -FailureMessage "Preparing MinIO drill import failed." | Out-Null
    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "cp", $objectsDirectory, "${MinioContainerName}:$targetObjectRoot/objects"
    ) -FailureMessage "Copying object backup into drill MinIO failed." | Out-Null
    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "exec", $MinioContainerName,
        "sh", "-c",
        'set -eu; export MC_HOST_enmatarget="http://$MINIO_ROOT_USER:$MINIO_ROOT_PASSWORD@127.0.0.1:9000"; mc mb "enmatarget/$1" >/dev/null; mc anonymous set none "enmatarget/$1" >/dev/null; mc mirror --overwrite "$2/objects" "enmatarget/$1" >/dev/null; mkdir -p "$2/verified"; mc mirror --overwrite "enmatarget/$1" "$2/verified" >/dev/null',
        "enma-restore", ([string]$manifest.objectStorage.bucket), $targetObjectRoot
    ) -FailureMessage "Object-storage restore failed." | Out-Null

    Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "cp", "${MinioContainerName}:$targetObjectRoot/verified", $reportDirectory
    ) -FailureMessage "Copying restored objects for verification failed." | Out-Null
    $verifiedDirectory = Join-Path $reportDirectory "verified"
    $restoredInventory = @(Get-ObjectInventory -ObjectsDirectory $verifiedDirectory)

    $documentsJson = Get-PostgresQuery `
        -DockerCommand $DockerCommand `
        -ContainerName $PostgresContainerName `
        -Query "SELECT coalesce(json_agg(json_build_object('id', id, 'key', stored_object_key, 'contentType', content_type, 'sizeBytes', size_bytes, 'sha256', encode(content_hash_sha256, 'hex')) ORDER BY stored_object_key), '[]'::json) FROM legal_documents;"
    $parsedDocuments = $documentsJson | ConvertFrom-Json
    $documents = @($parsedDocuments | ForEach-Object { $_ })
    $restoredByKey = @{}
    foreach ($item in $restoredInventory) { $restoredByKey[[string]$item.key] = $item }
    $documentsByKey = @{}
    foreach ($document in $documents) { $documentsByKey[[string]$document.key] = $document }

    $missingObjects = New-Object System.Collections.Generic.List[string]
    $orphanObjects = New-Object System.Collections.Generic.List[string]
    $hashMismatches = New-Object System.Collections.Generic.List[string]
    $metadataMismatches = New-Object System.Collections.Generic.List[string]
    foreach ($document in $documents) {
        $key = [string]$document.key
        if (-not $restoredByKey.ContainsKey($key)) {
            $missingObjects.Add($key)
            continue
        }
        $restoredObject = $restoredByKey[$key]
        if ([int64]$restoredObject.sizeBytes -ne [int64]$document.sizeBytes) {
            $metadataMismatches.Add($key)
        }
        if ([string]$restoredObject.sha256 -cne ([string]$document.sha256).ToLowerInvariant()) {
            $hashMismatches.Add($key)
        }
    }
    foreach ($item in $restoredInventory) {
        if (-not $documentsByKey.ContainsKey([string]$item.key)) {
            $orphanObjects.Add([string]$item.key)
        }
    }
    $missingObjectArray = @($missingObjects.ToArray())
    $orphanObjectArray = @($orphanObjects.ToArray())
    $hashMismatchArray = @($hashMismatches.ToArray())
    $metadataMismatchArray = @($metadataMismatches.ToArray())
    $report.missingObjects = $missingObjectArray
    $report.orphanObjects = $orphanObjectArray
    $report.hashMismatches = $hashMismatchArray
    $report.metadataMismatches = $metadataMismatchArray
    if (-not (Test-EquivalentJson -Left $backupInventory -Right $restoredInventory)) {
        throw "Restored object inventory does not match the backup."
    }
    if ($missingObjectArray.Count -gt 0 -or $orphanObjectArray.Count -gt 0 -or
        $hashMismatchArray.Count -gt 0 -or $metadataMismatchArray.Count -gt 0) {
        throw "Restored document metadata and objects are inconsistent."
    }
    if ($documents.Count -lt 1) {
        throw "Restore drill requires at least one real LegalDocument."
    }

    $sample = $documents[0]
    $samplePath = Get-ContainedChildPath -Parent $verifiedDirectory -RelativePath ([string]$sample.key)
    $formatValid = Test-DocumentFormat -Path $samplePath -ContentType ([string]$sample.contentType)
    if (-not $formatValid) {
        throw "Restored sample document failed format validation."
    }

    $report.objectRestore = "PASS"
    $report.objectCount = $restoredInventory.Count
    $report.documentUsability = [ordered]@{
        id = [string]$sample.id
        key = [string]$sample.key
        contentType = [string]$sample.contentType
        sizeBytes = [int64]$sample.sizeBytes
        hashMatch = $true
        formatValid = $true
    }

    $report.sourceAfter = Get-SourceEvidence `
        -DockerCommand $DockerCommand `
        -PostgresContainer $SourcePostgresContainer `
        -MinioContainer $SourceMinioContainer `
        -BucketName ([string]$manifest.objectStorage.bucket)
    $report.sourcePreserved = Test-EquivalentJson -Left $report.sourceBefore -Right $report.sourceAfter
    if (-not $report.sourcePreserved) {
        throw "Source evidence changed during the restore drill."
    }

    $report.overall = "PASS"
    Write-Host "RESTORE DRILL PASS"
    Write-Host "Latest migration: $restoredMigration"
    Write-Host "Objects restored and verified: $($restoredInventory.Count)"
}
catch {
    $failure = $_
    $report.error = $_.Exception.Message
    Write-Error $_.Exception.Message
}
finally {
    $report.completedAtUtc = [DateTime]::UtcNow.ToString("o")
    Write-JsonFile -Value $report -Path (Join-Path $reportDirectory "report.json")

    if (-not $KeepDrillResources) {
        foreach ($container in @($createdContainers.ToArray())) {
            Remove-DrillContainer -Name $container -DrillId $drillId
        }
        foreach ($volume in @($createdVolumes.ToArray())) {
            Remove-DrillVolume -Name $volume -DrillId $drillId
        }
    }

    if (Test-Path -LiteralPath $secretDirectory -PathType Container) {
        if (-not (Test-PathWithin -Path $secretDirectory -Parent (Join-Path $env:TEMP "Enma\restore-secrets"))) {
            throw "Refusing to remove an unexpected secret directory."
        }
        [System.IO.Directory]::Delete($secretDirectory, $true)
    }

    if (Test-Path -LiteralPath (Join-Path $reportDirectory "verified") -PathType Container) {
        [System.IO.Directory]::Delete((Join-Path $reportDirectory "verified"), $true)
    }
}

Write-Host "Report: $(Join-Path $reportDirectory 'report.json')"
if ($null -ne $failure) {
    throw $failure
}
