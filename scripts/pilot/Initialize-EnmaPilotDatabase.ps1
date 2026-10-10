[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigRoot,
    [switch]$ConfirmPilotInitialization,
    [string]$DockerCommand = "docker",
    [string]$DotnetCommand = "dotnet"
)

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
$postgresPort = Get-PilotPort -Settings $settings -Key "PILOT_POSTGRES_PORT"

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

$connectionString = "Host=127.0.0.1;Port=$postgresPort;Database=$($settings.PILOT_POSTGRES_DB);Username=$($settings.PILOT_POSTGRES_USER);Password=$($settings.PILOT_POSTGRES_PASSWORD)"
$previousConnection = [System.Environment]::GetEnvironmentVariable(
    "ENMA_DESIGNTIME_CONNECTION_STRING",
    "Process")
try {
    [System.Environment]::SetEnvironmentVariable(
        "ENMA_DESIGNTIME_CONNECTION_STRING",
        $connectionString,
        "Process")
    $dotnet = Assert-CommandAvailable -Command $DotnetCommand
    Invoke-PilotCommand -Command $dotnet -Arguments @(
        "tool", "run", "dotnet-ef", "database", "update",
        "--project", (Join-Path $repositoryRoot "src\Enma.Infrastructure\Enma.Infrastructure.csproj"),
        "--startup-project", (Join-Path $repositoryRoot "src\Enma.Infrastructure\Enma.Infrastructure.csproj"),
        "--context", "EnmaDbContext")
}
finally {
    [System.Environment]::SetEnvironmentVariable(
        "ENMA_DESIGNTIME_CONNECTION_STRING",
        $previousConnection,
        "Process")
}

Write-Host "PILOT DATABASE INITIALIZATION PASS"
