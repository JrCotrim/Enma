[CmdletBinding()]
param([string]$DockerCommand = "docker")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$composePath = Join-Path $repositoryRoot "compose.pilot.yaml"
$composeText = Get-Content -LiteralPath $composePath -Raw

foreach ($forbidden in @(
    "container_name: enma-postgres",
    "container_name: enma-minio",
    "container_name: enma-mailpit",
    "name: enma_enma_postgres_data",
    "name: enma_enma_minio_data")) {
    if ($composeText.IndexOf(
        $forbidden,
        [System.StringComparison]::Ordinal) -ge 0) {
        throw "Pilot Compose contains a Development resource name."
    }
}
if ($composeText -notmatch '(?m)^name: enma-pilot$') {
    throw "Pilot Compose is missing its exclusive project name."
}
$publishedPorts = [regex]::Matches($composeText, '(?m)^\s+- "([^"\r\n]+):\d+"\s*$')
if ($publishedPorts.Count -ne 5 -or
    @($publishedPorts | Where-Object { -not $_.Groups[1].Value.StartsWith("127.0.0.1:") }).Count -gt 0) {
    throw "Every Pilot published port must bind explicitly to 127.0.0.1."
}

$policy = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "infrastructure\minio\enma-pilot-documents-app-policy.json") `
    -Raw | ConvertFrom-Json
if (@($policy.Statement.Resource) -notcontains "arn:aws:s3:::enma-pilot-documents/*") {
    throw "Pilot MinIO policy does not target the exclusive bucket."
}

$appsettings = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "src\Enma.Api\appsettings.Pilot.json") `
    -Raw | ConvertFrom-Json
if ($appsettings.Authentication.Google.Enabled -ne $false -or
    $appsettings.DocumentStorage.BucketName -cne "enma-pilot-documents") {
    throw "Pilot application settings do not fail closed for Google or storage."
}

$parseErrors = New-Object System.Collections.Generic.List[object]
foreach ($scriptPath in Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.ps1") {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $scriptPath.FullName,
        [ref]$tokens,
        [ref]$errors)
    foreach ($error in $errors) { $parseErrors.Add($error) }
}
if ($parseErrors.Count -gt 0) {
    throw "Pilot PowerShell syntax validation failed: $($parseErrors[0].Message)"
}

$launcherText = Get-Content -LiteralPath (
    Join-Path $PSScriptRoot "Start-EnmaPilot.ps1") -Raw
if ($launcherText -match 'database\s+update|MigrateAsync|setup-local') {
    throw "Pilot launcher must not apply migrations or invoke Development setup."
}
if ($launcherText -notmatch 'ASPNETCORE_ENVIRONMENT\s*=\s*"Pilot"' -or
    $launcherText -notmatch 'Authentication__Google__Enabled\s*=\s*"false"') {
    throw "Pilot launcher is missing explicit environment or Google guards."
}
$webEnvironmentBlock = [regex]::Match(
    $launcherText,
    '(?s)\$webEnvironment\s*=\s*@\{(.*?)\n\}')
if (-not $webEnvironmentBlock.Success -or
    $webEnvironmentBlock.Value -match 'ConnectionStrings|POSTGRES|MINIO|DocumentStorage') {
    throw "Pilot frontend environment must not receive backend or storage credentials."
}

$docker = Get-Command -Name $DockerCommand -ErrorAction SilentlyContinue
if ($null -ne $docker) {
    $temporaryDirectory = Join-Path $env:TEMP (
        "Enma\pilot-config-test\" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $temporaryDirectory -Force | Out-Null
    try {
        $environmentPath = Join-Path $temporaryDirectory "pilot.env"
        $syntheticEnvironment = @(
            "PILOT_POSTGRES_DB=enma_pilot_test",
            "PILOT_POSTGRES_USER=enma_pilot_test",
            "PILOT_POSTGRES_PASSWORD=synthetic-postgres-test-only",
            "PILOT_POSTGRES_PORT=5543",
            "PILOT_MINIO_ROOT_USER=enma-pilot-root-test",
            "PILOT_MINIO_ROOT_PASSWORD=synthetic-minio-root-test-only",
            "PILOT_MINIO_APP_ACCESS_KEY=enma-pilot-app-test",
            "PILOT_MINIO_APP_SECRET_KEY=synthetic-minio-app-test-only",
            "PILOT_MINIO_API_PORT=9100",
            "PILOT_MINIO_CONSOLE_PORT=9101",
            "PILOT_MAILPIT_SMTP_PORT=1125",
            "PILOT_MAILPIT_UI_PORT=8125"
        )
        [System.IO.File]::WriteAllLines($environmentPath, $syntheticEnvironment)
        $rendered = & $docker.Source compose `
            --project-name enma-pilot `
            --file $composePath `
            --env-file $environmentPath `
            config --format json 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Pilot Compose rendering failed."
        }
        $model = ($rendered -join "`n") | ConvertFrom-Json
        foreach ($service in $model.services.PSObject.Properties.Value) {
            $portsProperty = $service.PSObject.Properties["ports"]
            if ($null -eq $portsProperty) { continue }
            foreach ($port in @($portsProperty.Value)) {
                if ($null -ne $port -and $port.host_ip -cne "127.0.0.1") {
                    throw "Rendered Pilot Compose contains a non-loopback port."
                }
            }
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryDirectory) {
            [System.IO.Directory]::Delete($temporaryDirectory, $true)
        }
    }
}

Write-Host "PILOT CONFIGURATION TESTS PASS"
