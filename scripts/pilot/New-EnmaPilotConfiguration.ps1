[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigRoot,
    [int]$PostgresPort = 5543,
    [int]$MinioApiPort = 9100,
    [int]$MinioConsolePort = 9101,
    [int]$MailpitSmtpPort = 1125,
    [int]$MailpitUiPort = 8125,
    [int]$ApiHttpsPort = 7443,
    [int]$WebHttpsPort = 5443
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Pilot.Common.ps1")

function New-PilotSecret {
    param([int]$ByteCount = 32)

    $bytes = New-Object byte[] $ByteCount
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) }
    finally { $generator.Dispose() }
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$configRootFullPath = [System.IO.Path]::GetFullPath($ConfigRoot)
Assert-PathOutsideRepository -Path $configRootFullPath -RepositoryRoot $repositoryRoot

$ports = @(
    $PostgresPort, $MinioApiPort, $MinioConsolePort, $MailpitSmtpPort,
    $MailpitUiPort, $ApiHttpsPort, $WebHttpsPort)
if (@($ports | Where-Object { $_ -lt 1 -or $_ -gt 65535 }).Count -gt 0 -or
    @($ports | Select-Object -Unique).Count -ne $ports.Count -or
    @($ports | Where-Object { @(5014, 7028, 5173, 1025, 8025) -contains $_ }).Count -gt 0) {
    throw "Pilot ports must be valid, unique, and distinct from Development."
}

if (-not (Test-Path -LiteralPath $configRootFullPath -PathType Container)) {
    New-Item -ItemType Directory -Path $configRootFullPath | Out-Null
}
$environmentPath = Join-Path $configRootFullPath "pilot.env"
if (Test-Path -LiteralPath $environmentPath) {
    throw "Pilot configuration already exists; it will not be overwritten."
}

$certificateDirectory = Join-Path $configRootFullPath "https"
$keysDirectory = Join-Path $configRootFullPath "data-protection-keys"
$logsDirectory = Join-Path $configRootFullPath "logs"
foreach ($directory in @($certificateDirectory, $keysDirectory, $logsDirectory)) {
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        New-Item -ItemType Directory -Path $directory | Out-Null
    }
}
Protect-PilotDirectory -Path $configRootFullPath

$certificatePath = Join-Path $certificateDirectory "enma-pilot.pfx"
if (Test-Path -LiteralPath $certificatePath) {
    throw "Pilot certificate path already exists; it will not be overwritten."
}
$certificatePassword = New-PilotSecret
$dotnet = Assert-CommandAvailable -Command "dotnet"
Invoke-PilotCommand -Command $dotnet -Arguments @(
    "dev-certs", "https", "--export-path", $certificatePath,
    "--password", $certificatePassword, "--trust")

$suffix = (New-PilotSecret -ByteCount 9).ToLowerInvariant()
$lines = @(
    "PILOT_POSTGRES_DB=enma_pilot",
    "PILOT_POSTGRES_USER=enma_pilot_$suffix",
    "PILOT_POSTGRES_PASSWORD=$(New-PilotSecret)",
    "PILOT_POSTGRES_PORT=$PostgresPort",
    "PILOT_MINIO_ROOT_USER=enma-pilot-root-$suffix",
    "PILOT_MINIO_ROOT_PASSWORD=$(New-PilotSecret)",
    "PILOT_MINIO_APP_ACCESS_KEY=enma-pilot-app-$suffix",
    "PILOT_MINIO_APP_SECRET_KEY=$(New-PilotSecret)",
    "PILOT_MINIO_API_PORT=$MinioApiPort",
    "PILOT_MINIO_CONSOLE_PORT=$MinioConsolePort",
    "PILOT_MAILPIT_SMTP_PORT=$MailpitSmtpPort",
    "PILOT_MAILPIT_UI_PORT=$MailpitUiPort",
    "PILOT_API_HTTPS_PORT=$ApiHttpsPort",
    "PILOT_WEB_HTTPS_PORT=$WebHttpsPort",
    "PILOT_HTTPS_PFX_PATH=$certificatePath",
    "PILOT_HTTPS_PFX_PASSWORD=$certificatePassword",
    "PILOT_DATA_PROTECTION_KEYS_PATH=$keysDirectory"
)
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines($environmentPath, $lines, $utf8WithoutBom)
Protect-PilotDirectory -Path $configRootFullPath

Write-Host "PILOT CONFIGURATION CREATED"
Write-Host "Secrets were written only to the restricted external configuration root."
Write-Host "No Docker resources, databases, migrations, or application processes were started."
