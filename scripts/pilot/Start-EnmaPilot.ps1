[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigRoot,
    [switch]$InfrastructureOnly,
    [switch]$SkipBuild,
    [string]$DockerCommand = "docker"
)

# Starts the Pilot on the Production images and topology (D3, D14, D15):
# build the API and migrator images, start PostgreSQL/MinIO/Mailpit, apply
# migrations with the migrator while the API is stopped, then start the API
# behind Caddy at https://enma-pilot.localhost:<PILOT_WEB_HTTPS_PORT>.
# -InfrastructureOnly starts only the data services (used for backups).

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

$httpsPort = Get-PilotPort -Settings $settings -Key "PILOT_WEB_HTTPS_PORT"
$mailpitUiPort = Get-PilotPort -Settings $settings -Key "PILOT_MAILPIT_UI_PORT"
if ($httpsPort -eq $mailpitUiPort) {
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
if (@(@($httpsPort, $mailpitUiPort) | Where-Object { $developmentPorts -contains $_ }).Count -gt 0) {
    throw "Pilot ports overlap a Development port."
}

$developmentSecrets = @{}
if (Test-Path -LiteralPath $developmentEnvironmentPath -PathType Leaf) {
    foreach ($line in Get-Content -LiteralPath $developmentEnvironmentPath) {
        if ($line -match '^(POSTGRES_PASSWORD|POSTGRES_MIGRATOR_PASSWORD|POSTGRES_APP_PASSWORD|MINIO_ROOT_PASSWORD|MINIO_APP_SECRET_KEY)=(.+)$') {
            $developmentSecrets[$Matches[1]] = $Matches[2].Trim()
        }
    }
}
$secretMap = @{
    PILOT_POSTGRES_PASSWORD = "POSTGRES_PASSWORD"
    PILOT_POSTGRES_MIGRATOR_PASSWORD = "POSTGRES_MIGRATOR_PASSWORD"
    PILOT_POSTGRES_APP_PASSWORD = "POSTGRES_APP_PASSWORD"
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
$revision = "unknown"
$git = Get-Command -Name "git" -ErrorAction SilentlyContinue
if ($null -ne $git) {
    $revision = (& $git.Source -C $repositoryRoot rev-parse HEAD).Trim()
    if (@(& $git.Source -C $repositoryRoot status --porcelain).Count -gt 0) {
        $revision += "-dirty"
    }
}
$composeArguments = Get-PilotComposeArguments -RepositoryRoot $repositoryRoot
$composeEnvironment = Get-PilotComposeEnvironment -Settings $settings -Revision $revision

Invoke-WithPilotComposeEnvironment -Environment $composeEnvironment -ScriptBlock {
    $services = @(Invoke-PilotCommand -Command $docker -Arguments ($composeArguments + @("config", "--services")) -CaptureOutput)
    $volumes = @(Invoke-PilotCommand -Command $docker -Arguments ($composeArguments + @("config", "--volumes")) -CaptureOutput)
    $expectedServices = @("postgres", "minio", "minio-bootstrap", "mailpit", "api", "caddy")
    $expectedVolumes = @("postgres_data", "documents_data", "dataprotection_keys", "caddy_data", "caddy_config", "mailpit_data")
    if (@(Compare-Object $expectedServices $services).Count -ne 0 -or
        @(Compare-Object $expectedVolumes $volumes).Count -ne 0) {
        throw "Pilot Compose resolved unexpected services or volumes."
    }
}

foreach ($name in $script:PilotContainers) {
    Assert-PilotDockerResourceOwnership -Kind container -Name $name -DockerCommand $docker
}
foreach ($name in @($script:PilotDataVolumes + @(
    "enma_pilot_dataprotection_keys", "enma_pilot_caddy_data", "enma_pilot_caddy_config"))) {
    Assert-PilotDockerResourceOwnership -Kind volume -Name $name -DockerCommand $docker
}
# The data volumes are external to Compose so it can never recreate them.
# A first start creates them empty, labelled as Pilot resources.
foreach ($name in $script:PilotDataVolumes) {
    if (-not (Test-PilotDockerResourceExists -Kind volume -Name $name -DockerCommand $docker)) {
        Invoke-PilotCommand -Command $docker -Arguments @(
            "volume", "create", "--label", "com.docker.compose.project=enma-pilot", $name) |
            Out-Null
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

$bindings = @(
    @{ Name = "enma-pilot-mailpit"; ContainerPort = 8025; Port = $mailpitUiPort; Purpose = "Mailpit UI" },
    @{ Name = "enma-pilot-caddy"; ContainerPort = 443; Port = $httpsPort; Purpose = "HTTPS" }
)
foreach ($binding in $bindings) {
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

Invoke-WithPilotComposeEnvironment -Environment $composeEnvironment -ScriptBlock {
    if (-not $SkipBuild -and -not $InfrastructureOnly) {
        Invoke-PilotCommand -Command $docker -Arguments ($composeArguments + @(
            "--profile", "migrate", "build", "api", "migrate"))
    }
    Invoke-PilotCommand -Command $docker -Arguments ($composeArguments + @(
        "up", "--detach", "--wait", "postgres", "minio", "mailpit"))
    Invoke-PilotCommand -Command $docker -Arguments ($composeArguments + @(
        "up", "--detach", "--no-deps", "minio-bootstrap"))
    $bootstrapExitCode = Invoke-PilotCommand -Command $docker -Arguments @(
        "wait", "enma-pilot-minio-bootstrap") -CaptureOutput
    if ($bootstrapExitCode.ToString().Trim() -cne "0") {
        throw "Pilot MinIO bootstrap failed."
    }
}
Write-Host "Pilot data services are healthy and isolated."
if ($InfrastructureOnly) {
    Write-Host "The API was not started; no migrations were applied."
    return
}

Invoke-WithPilotComposeEnvironment -Environment $composeEnvironment -ScriptBlock {
    Invoke-PilotCommand -Command $docker -Arguments ($composeArguments + @("stop", "api"))
}
& (Join-Path $PSScriptRoot "Initialize-EnmaPilotDatabase.ps1") `
    -ConfigRoot $configRootFullPath `
    -ConfirmPilotInitialization `
    -DockerCommand $docker
Invoke-WithPilotComposeEnvironment -Environment $composeEnvironment -ScriptBlock {
    Invoke-PilotCommand -Command $docker -Arguments ($composeArguments + @(
        "up", "--detach", "--wait", "--no-deps", "api", "caddy"))
}

# Readiness through the edge, verified against Caddy's local root CA (also
# saved for the optional Windows trust step in docs/operations/local-pilot.md).
$rootCertificatePath = Join-Path $configRootFullPath "enma-pilot-caddy-root.crt"
$origin = "https://$($script:PilotHostName):$httpsPort"
$curl = Assert-CommandAvailable -Command "curl.exe"
$ready = $false
for ($attempt = 1; $attempt -le 30 -and -not $ready; $attempt++) {
    Start-Sleep -Seconds 2
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $docker cp "enma-pilot-caddy:/data/caddy/pki/authorities/local/root.crt" $rootCertificatePath 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { continue }
        # The local CA publishes no revocation lists; the chain is still
        # verified against Caddy's root.
        $status = & $curl --silent --output NUL --write-out "%{http_code}" `
            --cacert $rootCertificatePath --ssl-no-revoke `
            --resolve "$($script:PilotHostName):$($httpsPort):127.0.0.1" `
            "$origin/health/ready" 2>$null
        $ready = $LASTEXITCODE -eq 0 -and $status -eq "200"
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}
if (-not $ready) {
    throw "The Pilot did not become ready through Caddy; inspect 'docker logs enma-pilot-api'."
}

Write-Host "ENMA Pilot is running at $origin (fictitious data only)."
Write-Host "Mailpit is available at http://127.0.0.1:$mailpitUiPort."
Write-Host "Caddy's local root CA was saved to $rootCertificatePath."
Write-Host "Stop it with scripts\pilot\Stop-EnmaPilot.ps1."
