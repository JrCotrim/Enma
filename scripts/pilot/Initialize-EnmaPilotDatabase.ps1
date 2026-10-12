[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigRoot,
    [switch]$ConfirmPilotInitialization,
    [string]$DockerCommand = "docker"
)

# Applies the pending migrations to the Pilot database with the migrator image
# (D14), only after proving the target is the Pilot PostgreSQL and the Pilot
# API is stopped. Start-EnmaPilot.ps1 calls it between build and start.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Pilot.Common.ps1")

if (-not $ConfirmPilotInitialization) {
    throw "Pilot database initialization requires -ConfirmPilotInitialization."
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$configRootFullPath = [System.IO.Path]::GetFullPath($ConfigRoot)
Assert-PathOutsideRepository -Path $configRootFullPath -RepositoryRoot $repositoryRoot
$settings = Read-PilotEnvironment -Path (Join-Path $configRootFullPath "pilot.env")
Assert-PilotIdentitySettings -Settings $settings

$docker = Assert-CommandAvailable -Command $DockerCommand
Assert-PilotDockerResourceOwnership `
    -Kind container -Name "enma-pilot-postgres" -DockerCommand $docker
Assert-PilotContainerEnvironment -Name "enma-pilot-postgres" -DockerCommand $docker -Expected @{
    POSTGRES_DB = $settings.PILOT_POSTGRES_DB
    POSTGRES_USER = $settings.PILOT_POSTGRES_USER
    POSTGRES_PASSWORD = $settings.PILOT_POSTGRES_PASSWORD
}
if (-not (Test-PilotContainerRunning `
    -Name "enma-pilot-postgres" -DockerCommand $docker)) {
    throw "Pilot PostgreSQL is not running. Start only the Pilot infrastructure first."
}
Assert-PilotDockerResourceOwnership `
    -Kind container -Name "enma-pilot-api" -DockerCommand $docker
if (Test-PilotContainerRunning -Name "enma-pilot-api" -DockerCommand $docker) {
    throw "The Pilot API is running. Stop it before applying migrations."
}

try {
    $databaseName = @(Invoke-PilotCommand -Command $docker -Arguments @(
        "exec", "enma-pilot-postgres", "psql", "-X", "-At",
        "-U", $settings.PILOT_POSTGRES_USER,
        "-d", $settings.PILOT_POSTGRES_DB,
        "-c", "select current_database();") -CaptureOutput)
}
catch {
    throw "Pilot PostgreSQL identity query failed."
}
if ($databaseName.Count -ne 1 -or
    [string]::IsNullOrWhiteSpace([string]$databaseName[0])) {
    throw "Pilot PostgreSQL identity query did not return exactly one non-empty database name."
}
if (([string]$databaseName[0]).Trim() -cne $settings.PILOT_POSTGRES_DB) {
    throw "Pilot PostgreSQL identity does not match the external Pilot configuration."
}

$before = Get-PilotAppliedMigration -Settings $settings -DockerCommand $docker
$composeArguments = Get-PilotComposeArguments -RepositoryRoot $repositoryRoot
Invoke-WithPilotComposeEnvironment `
    -Environment (Get-PilotComposeEnvironment -Settings $settings) `
    -ScriptBlock {
        Invoke-PilotCommand -Command $docker -Arguments ($composeArguments + @(
            "--profile", "migrate", "run", "--rm", "--no-deps", "migrate"))
    }
$after = Get-PilotAppliedMigration -Settings $settings -DockerCommand $docker

Write-Host "Pilot migrations: $before -> $after"
Write-Host "PILOT DATABASE INITIALIZATION PASS"
