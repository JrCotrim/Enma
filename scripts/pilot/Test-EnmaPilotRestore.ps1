[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigRoot,
    [Parameter(Mandatory = $true)][string]$BackupPath,
    [Parameter(Mandatory = $true)][string]$ReportRoot,
    [string]$DockerCommand = "docker"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Pilot.Common.ps1")

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$configRootFullPath = [System.IO.Path]::GetFullPath($ConfigRoot)
$reportRootFullPath = [System.IO.Path]::GetFullPath($ReportRoot)
Assert-PathOutsideRepository -Path $configRootFullPath -RepositoryRoot $repositoryRoot
Assert-PathOutsideRepository -Path $reportRootFullPath -RepositoryRoot $repositoryRoot
$temporaryRoot = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd('\', '/')
if ($reportRootFullPath.Equals($temporaryRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    $reportRootFullPath.StartsWith(
        $temporaryRoot + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Pilot restore reports must not use the system temporary directory."
}

$settings = Read-PilotEnvironment -Path (Join-Path $configRootFullPath "pilot.env")
Assert-PilotIdentitySettings -Settings $settings
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

& (Join-Path $PSScriptRoot "..\operations\Test-EnmaRestore.ps1") `
    -BackupPath ([System.IO.Path]::GetFullPath($BackupPath)) `
    -ReportRoot $reportRootFullPath `
    -SourcePostgresContainer "enma-pilot-postgres" `
    -SourceMinioContainer "enma-pilot-minio" `
    -DockerCommand $docker

Write-Host "PILOT RESTORE DRILL WRAPPER PASS"
