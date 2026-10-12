[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigRoot,
    [int]$MailpitUiPort = 8125,
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

$ports = @($MailpitUiPort, $WebHttpsPort)
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

Protect-PilotDirectory -Path $configRootFullPath

$suffix = (New-PilotSecret -ByteCount 9).ToLowerInvariant()
$lines = @(
    "PILOT_POSTGRES_DB=enma_pilot",
    "PILOT_POSTGRES_USER=enma_pilot_$suffix",
    "PILOT_POSTGRES_PASSWORD=$(New-PilotSecret)",
    "PILOT_MINIO_ROOT_USER=enma-pilot-root-$suffix",
    "PILOT_MINIO_ROOT_PASSWORD=$(New-PilotSecret)",
    "PILOT_MINIO_APP_ACCESS_KEY=enma-pilot-app-$suffix",
    "PILOT_MINIO_APP_SECRET_KEY=$(New-PilotSecret)",
    "PILOT_MAILPIT_UI_PORT=$MailpitUiPort",
    "PILOT_WEB_HTTPS_PORT=$WebHttpsPort"
)
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllLines($environmentPath, $lines, $utf8WithoutBom)
Protect-PilotDirectory -Path $configRootFullPath

Write-Host "PILOT CONFIGURATION CREATED"
Write-Host "Secrets were written only to the restricted external configuration root."
Write-Host "No Docker resources, databases, migrations, or application processes were started."
