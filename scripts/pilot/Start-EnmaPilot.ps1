[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigRoot,
    [switch]$InfrastructureOnly,
    [string]$DockerCommand = "docker"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Pilot.Common.ps1")

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$configRootFullPath = [System.IO.Path]::GetFullPath($ConfigRoot)
$environmentPath = Join-Path $configRootFullPath "pilot.env"
Assert-PathOutsideRepository -Path $configRootFullPath -RepositoryRoot $repositoryRoot
if (-not (Test-Path -LiteralPath $configRootFullPath -PathType Container)) {
    throw "Pilot configuration root does not exist."
}
$settings = Read-PilotEnvironment -Path $environmentPath
Assert-PilotIdentitySettings -Settings $settings
Protect-PilotDirectory -Path $configRootFullPath

$certificatePath = [System.IO.Path]::GetFullPath($settings.PILOT_HTTPS_PFX_PATH)
$keysPath = [System.IO.Path]::GetFullPath($settings.PILOT_DATA_PROTECTION_KEYS_PATH)
Assert-PathWithin -Path $certificatePath -Parent $configRootFullPath
Assert-PathWithin -Path $keysPath -Parent $configRootFullPath
if (-not (Test-Path -LiteralPath $certificatePath -PathType Leaf)) {
    throw "Pilot HTTPS certificate was not found."
}
if (-not (Test-Path -LiteralPath $keysPath -PathType Container)) {
    throw "Pilot Data Protection key directory was not found."
}

$portKeys = @(
    "PILOT_POSTGRES_PORT", "PILOT_MINIO_API_PORT",
    "PILOT_MINIO_CONSOLE_PORT", "PILOT_MAILPIT_SMTP_PORT",
    "PILOT_MAILPIT_UI_PORT", "PILOT_API_HTTPS_PORT",
    "PILOT_WEB_HTTPS_PORT"
)
$ports = @{}
foreach ($key in $portKeys) { $ports[$key] = Get-PilotPort -Settings $settings -Key $key }
if (@($ports.Values | Select-Object -Unique).Count -ne $ports.Count) {
    throw "Pilot ports must be unique."
}
$developmentPorts = @(5014, 7028, 5173, 1025, 8025)
$developmentEnvironmentPath = Join-Path $repositoryRoot ".env"
if (Test-Path -LiteralPath $developmentEnvironmentPath -PathType Leaf) {
    foreach ($line in Get-Content -LiteralPath $developmentEnvironmentPath) {
        if ($line -match '^(POSTGRES_PORT|MINIO_API_PORT|MINIO_CONSOLE_PORT)=(\d+)$') {
            $developmentPorts += [int]$Matches[2]
        }
    }
}
if (@($ports.Values | Where-Object { $developmentPorts -contains $_ }).Count -gt 0) {
    throw "Pilot ports overlap a Development port."
}

$developmentSecrets = @{}
if (Test-Path -LiteralPath $developmentEnvironmentPath -PathType Leaf) {
    foreach ($line in Get-Content -LiteralPath $developmentEnvironmentPath) {
        if ($line -match '^(POSTGRES_PASSWORD|MINIO_ROOT_PASSWORD|MINIO_APP_SECRET_KEY)=(.+)$') {
            $developmentSecrets[$Matches[1]] = $Matches[2].Trim()
        }
    }
}
$secretMap = @{
    PILOT_POSTGRES_PASSWORD = "POSTGRES_PASSWORD"
    PILOT_MINIO_ROOT_PASSWORD = "MINIO_ROOT_PASSWORD"
    PILOT_MINIO_APP_SECRET_KEY = "MINIO_APP_SECRET_KEY"
}
foreach ($pilotKey in $secretMap.Keys) {
    $developmentKey = $secretMap[$pilotKey]
    if ($developmentSecrets.ContainsKey($developmentKey) -and
        $settings[$pilotKey] -ceq $developmentSecrets[$developmentKey]) {
        throw "Pilot credentials must differ from Development credentials."
    }
}

$docker = Assert-CommandAvailable -Command $DockerCommand
$composePath = Join-Path $repositoryRoot "compose.pilot.yaml"
$composeArguments = @(
    "compose", "--project-name", "enma-pilot", "--file", $composePath,
    "--env-file", $environmentPath
)
$services = @(Invoke-PilotCommand -Command $docker -Arguments ($composeArguments + @("config", "--services")) -CaptureOutput)
$volumes = @(Invoke-PilotCommand -Command $docker -Arguments ($composeArguments + @("config", "--volumes")) -CaptureOutput)
$expectedServices = @("postgres", "minio", "minio-bootstrap", "mailpit")
$expectedVolumes = @("enma_pilot_postgres_data", "enma_pilot_minio_data", "enma_pilot_mailpit_data")
if (@(Compare-Object $expectedServices $services).Count -ne 0 -or
    @(Compare-Object $expectedVolumes $volumes).Count -ne 0) {
    throw "Pilot Compose resolved unexpected services or volumes."
}

foreach ($name in @("enma-pilot-postgres", "enma-pilot-minio", "enma-pilot-minio-bootstrap", "enma-pilot-mailpit")) {
    Assert-PilotDockerResourceOwnership -Kind container -Name $name -DockerCommand $docker
}
foreach ($name in $expectedVolumes) {
    Assert-PilotDockerResourceOwnership -Kind volume -Name $name -DockerCommand $docker
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
Assert-PilotContainerEnvironment -Name "enma-pilot-minio-bootstrap" -DockerCommand $docker -Expected @{
    MINIO_ROOT_USER = $settings.PILOT_MINIO_ROOT_USER
    MINIO_ROOT_PASSWORD = $settings.PILOT_MINIO_ROOT_PASSWORD
    MINIO_APP_ACCESS_KEY = $settings.PILOT_MINIO_APP_ACCESS_KEY
    MINIO_APP_SECRET_KEY = $settings.PILOT_MINIO_APP_SECRET_KEY
}

$infrastructurePorts = @(
    @{ Name = "enma-pilot-postgres"; ContainerPort = 5432; Port = $ports.PILOT_POSTGRES_PORT; Purpose = "PostgreSQL" },
    @{ Name = "enma-pilot-minio"; ContainerPort = 9000; Port = $ports.PILOT_MINIO_API_PORT; Purpose = "MinIO API" },
    @{ Name = "enma-pilot-minio"; ContainerPort = 9001; Port = $ports.PILOT_MINIO_CONSOLE_PORT; Purpose = "MinIO console" },
    @{ Name = "enma-pilot-mailpit"; ContainerPort = 1025; Port = $ports.PILOT_MAILPIT_SMTP_PORT; Purpose = "Mailpit SMTP" },
    @{ Name = "enma-pilot-mailpit"; ContainerPort = 8025; Port = $ports.PILOT_MAILPIT_UI_PORT; Purpose = "Mailpit UI" }
)
foreach ($binding in $infrastructurePorts) {
    if (Test-PilotContainerRunning -Name $binding.Name -DockerCommand $docker) {
        Assert-RunningPilotContainerPort `
            -Name $binding.Name `
            -ContainerPort $binding.ContainerPort `
            -HostPort $binding.Port `
            -DockerCommand $docker
    }
    else {
        Assert-LoopbackPortAvailable -Port $binding.Port -Purpose $binding.Purpose
    }
}
if (-not $InfrastructureOnly) {
    Assert-LoopbackPortAvailable -Port $ports.PILOT_API_HTTPS_PORT -Purpose "API HTTPS"
    Assert-LoopbackPortAvailable -Port $ports.PILOT_WEB_HTTPS_PORT -Purpose "frontend HTTPS"
}

Invoke-PilotCommand -Command $docker -Arguments ($composeArguments + @(
    "up", "--detach", "--wait", "postgres", "minio", "mailpit"))
Invoke-PilotCommand -Command $docker -Arguments ($composeArguments + @(
    "up", "--detach", "minio-bootstrap"))
$bootstrapExitCode = Invoke-PilotCommand -Command $docker -Arguments @(
    "wait", "enma-pilot-minio-bootstrap") -CaptureOutput
if ($bootstrapExitCode.ToString().Trim() -cne "0") {
    throw "Pilot MinIO bootstrap failed."
}
Write-Host "Pilot infrastructure is healthy and isolated."
if ($InfrastructureOnly) {
    Write-Host "Application processes were not started; no migrations were applied."
    return
}

$logDirectory = Join-Path $configRootFullPath "logs"
if (-not (Test-Path -LiteralPath $logDirectory -PathType Container)) {
    New-Item -ItemType Directory -Path $logDirectory | Out-Null
}
Protect-PilotDirectory -Path $logDirectory
$timestamp = [DateTime]::UtcNow.ToString("yyyyMMddTHHmmssZ")

$apiEnvironment = @{
    ASPNETCORE_ENVIRONMENT = "Pilot"
    ASPNETCORE_URLS = "https://127.0.0.1:$($ports.PILOT_API_HTTPS_PORT)"
    ASPNETCORE_Kestrel__Certificates__Default__Path = $certificatePath
    ASPNETCORE_Kestrel__Certificates__Default__Password = $settings.PILOT_HTTPS_PFX_PASSWORD
    ConnectionStrings__Database = "Host=127.0.0.1;Port=$($ports.PILOT_POSTGRES_PORT);Database=$($settings.PILOT_POSTGRES_DB);Username=$($settings.PILOT_POSTGRES_USER);Password=$($settings.PILOT_POSTGRES_PASSWORD)"
    DataProtection__KeysPath = $keysPath
    DocumentStorage__ServiceUrl = "http://127.0.0.1:$($ports.PILOT_MINIO_API_PORT)"
    DocumentStorage__AccessKey = $settings.PILOT_MINIO_APP_ACCESS_KEY
    DocumentStorage__SecretKey = $settings.PILOT_MINIO_APP_SECRET_KEY
    EmailVerification__DevelopmentDelivery__VerificationPageUrl = "https://localhost:$($ports.PILOT_WEB_HTTPS_PORT)/verify-email"
    EmailVerification__DevelopmentDelivery__PasswordRecoveryPageUrl = "https://localhost:$($ports.PILOT_WEB_HTTPS_PORT)/reset-password"
    EmailVerification__DevelopmentDelivery__SmtpPort = $ports.PILOT_MAILPIT_SMTP_PORT.ToString()
    Authentication__Google__Enabled = "false"
}
$webEnvironment = @{
    ENMA_PILOT_API_TARGET = "https://127.0.0.1:$($ports.PILOT_API_HTTPS_PORT)"
    ENMA_PILOT_HTTPS_PFX_PATH = $certificatePath
    ENMA_PILOT_HTTPS_PFX_PASSWORD = $settings.PILOT_HTTPS_PFX_PASSWORD
}

$environmentKeys = @($apiEnvironment.Keys) + @($webEnvironment.Keys) |
    Select-Object -Unique
$previousEnvironment = @{}
$api = $null
$web = $null
try {
    foreach ($key in $environmentKeys) {
        $previousEnvironment[$key] = [System.Environment]::GetEnvironmentVariable($key, "Process")
        [System.Environment]::SetEnvironmentVariable($key, $null, "Process")
    }
    foreach ($key in $apiEnvironment.Keys) {
        [System.Environment]::SetEnvironmentVariable($key, $apiEnvironment[$key], "Process")
    }
    $dotnet = Assert-CommandAvailable -Command "dotnet"
    $api = Start-Process -FilePath $dotnet -ArgumentList @(
        "run", "--no-launch-profile", "--project", (Join-Path $repositoryRoot "src\Enma.Api\Enma.Api.csproj")) `
        -WorkingDirectory $repositoryRoot -PassThru `
        -RedirectStandardOutput (Join-Path $logDirectory "api-$timestamp.log") `
        -RedirectStandardError (Join-Path $logDirectory "api-$timestamp.error.log")

    foreach ($key in $environmentKeys) {
        [System.Environment]::SetEnvironmentVariable($key, $null, "Process")
    }
    foreach ($key in $webEnvironment.Keys) {
        [System.Environment]::SetEnvironmentVariable($key, $webEnvironment[$key], "Process")
    }
    $npm = Assert-CommandAvailable -Command "npm.cmd"
    $web = Start-Process -FilePath $npm -ArgumentList @(
        "run", "dev", "--", "--config", "vite.pilot.config.ts",
        "--port", $ports.PILOT_WEB_HTTPS_PORT.ToString()) `
        -WorkingDirectory (Join-Path $repositoryRoot "src\Enma.Web") -PassThru `
        -RedirectStandardOutput (Join-Path $logDirectory "web-$timestamp.log") `
        -RedirectStandardError (Join-Path $logDirectory "web-$timestamp.error.log")
}
catch {
    foreach ($process in @($api, $web)) {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id
        }
    }
    throw
}
finally {
    foreach ($key in $environmentKeys) {
        [System.Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key], "Process")
    }
}

Write-Host "ENMA Pilot is running at https://localhost:$($ports.PILOT_WEB_HTTPS_PORT)."
Write-Host "Mailpit is available at http://127.0.0.1:$($ports.PILOT_MAILPIT_UI_PORT)."
Write-Host "No migrations were applied. Logs are stored in the restricted Pilot configuration root."
try {
    while (-not $api.HasExited -and -not $web.HasExited) { Start-Sleep -Seconds 1 }
    throw "A Pilot application process exited; inspect the restricted Pilot logs."
}
finally {
    foreach ($process in @($api, $web)) {
        if ($null -ne $process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id
        }
    }
}
