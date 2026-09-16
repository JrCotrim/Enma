[CmdletBinding()]
param(
    [string]$PowerShellExe = "powershell.exe",
    [string]$DockerCommand = "docker"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "BackupRestore.Common.ps1")

function Assert-ScriptFails {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $PowerShellExe -NoProfile -ExecutionPolicy Bypass `
            -File $ScriptPath @Arguments 2>&1 | Out-String
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -eq 0) {
        throw "$Name unexpectedly succeeded."
    }
    if ($output -notmatch [regex]::Escape($ExpectedMessage)) {
        throw "$Name failed without the expected clear message."
    }
    Write-Host "PASS: $Name"
}

Assert-CommandAvailable -Command $PowerShellExe

$backupScript = Join-Path $PSScriptRoot "Backup-Enma.ps1"
$restoreScript = Join-Path $PSScriptRoot "Test-EnmaRestore.ps1"
$testRoot = Join-Path $env:TEMP ("Enma\guard-tests\" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

try {
    Assert-ScriptFails `
        -Name "backup requires quiesced confirmation" `
        -ScriptPath $backupScript `
        -Arguments @("-BackupRoot", (Join-Path $testRoot "backups")) `
        -ExpectedMessage "Backup consistent requires API without writers"

    Assert-ScriptFails `
        -Name "backup source dependency unavailable" `
        -ScriptPath $backupScript `
        -Arguments @(
            "-BackupRoot", (Join-Path $testRoot "source-unavailable"),
            "-SourcePostgresContainer", "enma-missing-source-postgres",
            "-DockerCommand", $DockerCommand,
            "-ConfirmQuiesced") `
        -ExpectedMessage "Required source container is unavailable"

    $missingManifest = Join-Path $testRoot "missing-manifest"
    New-Item -ItemType Directory -Path $missingManifest | Out-Null
    Assert-ScriptFails `
        -Name "restore manifest missing" `
        -ScriptPath $restoreScript `
        -Arguments @("-BackupPath", $missingManifest) `
        -ExpectedMessage "Backup manifest is missing"

    $invalidManifest = Join-Path $testRoot "invalid-manifest"
    New-Item -ItemType Directory -Path $invalidManifest | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $invalidManifest "manifest.json"), "{")
    Assert-ScriptFails `
        -Name "restore manifest invalid" `
        -ScriptPath $restoreScript `
        -Arguments @("-BackupPath", $invalidManifest) `
        -ExpectedMessage "Backup manifest is invalid JSON"

    $fixture = Join-Path $testRoot "fixture"
    New-Item -ItemType Directory -Path $fixture | Out-Null
    $emptyInventory = @()
    $baseManifest = [ordered]@{
        formatVersion = 1
        backupId = "guard-test"
        mode = "quiesced"
        database = [ordered]@{
            dumpFile = "database.dump"
            dumpSizeBytes = 3
            dumpSha256 = ("0" * 64)
            latestMigration = "guard-test"
            counts = [ordered]@{}
        }
        objectStorage = [ordered]@{
            bucket = "enma-documents"
            directory = "objects"
            objectCount = 0
            totalBytes = 0
            inventorySha256 = Get-StringSha256 -Value ($emptyInventory | ConvertTo-Json -Compress)
            objects = $emptyInventory
        }
    }
    Write-JsonFile -Value $baseManifest -Path (Join-Path $fixture "manifest.json")
    Assert-ScriptFails `
        -Name "restore dump missing" `
        -ScriptPath $restoreScript `
        -Arguments @("-BackupPath", $fixture) `
        -ExpectedMessage "PostgreSQL dump is missing"

    [System.IO.File]::WriteAllBytes((Join-Path $fixture "database.dump"), [byte[]](1, 2, 3))
    Assert-ScriptFails `
        -Name "restore dump hash mismatch" `
        -ScriptPath $restoreScript `
        -Arguments @("-BackupPath", $fixture) `
        -ExpectedMessage "PostgreSQL dump SHA-256 mismatch"

    $baseManifest.database.dumpSha256 = Get-Sha256Hex -Path (Join-Path $fixture "database.dump")
    Write-JsonFile -Value $baseManifest -Path (Join-Path $fixture "manifest.json")
    Assert-ScriptFails `
        -Name "restore object backup missing" `
        -ScriptPath $restoreScript `
        -Arguments @("-BackupPath", $fixture) `
        -ExpectedMessage "Object backup is missing"

    New-Item -ItemType Directory -Path (Join-Path $fixture "objects") | Out-Null
    Write-JsonFile -Value $baseManifest -Path (Join-Path $fixture "manifest.json")
    Assert-ScriptFails `
        -Name "restore original target rejected" `
        -ScriptPath $restoreScript `
        -Arguments @(
            "-BackupPath", $fixture,
            "-PostgresContainerName", "enma-postgres") `
        -ExpectedMessage "is not an isolated drill resource"

    Assert-ScriptFails `
        -Name "restore dependency unavailable" `
        -ScriptPath $restoreScript `
        -Arguments @(
            "-BackupPath", $fixture,
            "-DockerCommand", "enma-command-that-does-not-exist") `
        -ExpectedMessage "is not available on PATH"

    Write-Host "GUARD TESTS PASS"
}
finally {
    $guardRoot = Join-Path $env:TEMP "Enma\guard-tests"
    if ((Test-Path -LiteralPath $testRoot) -and
        (Test-PathWithin -Path $testRoot -Parent $guardRoot)) {
        [System.IO.Directory]::Delete($testRoot, $true)
    }
}
