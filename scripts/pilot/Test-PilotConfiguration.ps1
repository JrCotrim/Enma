[CmdletBinding()]
param([string]$DockerCommand = "docker")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$deployRoot = Join-Path $repositoryRoot "deploy"
$basePath = Join-Path $deployRoot "compose.yaml"
$composePath = Join-Path $deployRoot "compose.pilot.yaml"
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
if ($composeText -notmatch '(?m)^\s+ASPNETCORE_ENVIRONMENT: Pilot$' -or
    $composeText -notmatch '(?m)^\s+Authentication__Google__Enabled: "false"$') {
    throw "Pilot Compose is missing explicit environment or Google guards."
}

$caddyfile = Get-Content -LiteralPath (Join-Path $deployRoot "caddy\Caddyfile.pilot") -Raw
if ($caddyfile -notmatch '(?m)^\s+tls internal$' -or
    $caddyfile -notmatch '(?m)^\s+import enma_security_headers$' -or
    $caddyfile -match 'Strict-Transport-Security') {
    throw "Pilot Caddyfile must use the local CA, the shared headers, and no HSTS."
}

if (Test-Path -LiteralPath (Join-Path $repositoryRoot "src\Enma.Web\vite.pilot.config.ts")) {
    throw "The Vite Pilot configuration must not exist; the Pilot serves the build."
}

$policy = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "infrastructure\minio\enma-pilot-documents-app-policy.json") `
    -Raw | ConvertFrom-Json
if (@($policy.Statement.Resource) -notcontains "arn:aws:s3:::enma-pilot-documents/*") {
    throw "Pilot MinIO policy does not target the exclusive bucket."
}
# D36: the backup identity lists the exclusive bucket and reads its objects,
# nothing else.
$backupPolicy = Get-Content -LiteralPath (
    Join-Path $repositoryRoot "infrastructure\minio\enma-pilot-documents-backup-policy.json") `
    -Raw | ConvertFrom-Json
$backupGrants = @(foreach ($statement in @($backupPolicy.Statement)) {
    foreach ($action in @($statement.Action)) {
        foreach ($resource in @($statement.Resource)) { "$($statement.Effect) $action $resource" }
    }
}) | Sort-Object
if (@(Compare-Object @(
        "Allow s3:GetObject arn:aws:s3:::enma-pilot-documents/*",
        "Allow s3:ListBucket arn:aws:s3:::enma-pilot-documents") $backupGrants).Count -ne 0) {
    throw "Pilot MinIO backup policy must grant only ListBucket and GetObject on the exclusive bucket."
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

foreach ($launcher in @("Start-EnmaPilot.ps1", "Initialize-EnmaPilotDatabase.ps1")) {
    $launcherText = Get-Content -LiteralPath (Join-Path $PSScriptRoot $launcher) -Raw
    if ($launcherText -match 'database\s+update|MigrateAsync|setup-local|dotnet-ef') {
        throw "Pilot scripts must apply migrations only through the migrator image."
    }
}
$stopText = Get-Content -LiteralPath (Join-Path $PSScriptRoot "Stop-EnmaPilot.ps1") -Raw
if ($stopText -match '"down"|"rm"|volume\s+rm|"-v"|--volumes') {
    throw "The Pilot stop script must only stop containers."
}

$docker = Get-Command -Name $DockerCommand -ErrorAction SilentlyContinue
if ($null -ne $docker) {
    $synthetic = @{
        ENMA_IMAGE_TAG = "pilot"
        ENMA_PUBLIC_HOST = "enma-pilot.localhost"
        ENMA_PILOT_HTTPS_PORT = "5443"
        ENMA_PILOT_MAILPIT_UI_PORT = "8125"
        ENMA_POSTGRES_DB = "enma_pilot_test"
        ENMA_POSTGRES_USER = "enma_pilot_test"
        ENMA_POSTGRES_PASSWORD = "synthetic-postgres-test-only"
        ENMA_POSTGRES_MIGRATOR_USER = "enma_pilot_migrator_test"
        ENMA_POSTGRES_MIGRATOR_PASSWORD = "synthetic-migrator-test-only"
        ENMA_POSTGRES_APP_USER = "enma_pilot_app_test"
        ENMA_POSTGRES_APP_PASSWORD = "synthetic-app-test-only"
        ENMA_MINIO_ROOT_USER = "enma-pilot-root-test"
        ENMA_MINIO_ROOT_PASSWORD = "synthetic-minio-root-test-only"
        ENMA_MINIO_APP_ACCESS_KEY = "enma-pilot-app-test"
        ENMA_MINIO_APP_SECRET_KEY = "synthetic-minio-app-test-only"
        ENMA_MINIO_BACKUP_ACCESS_KEY = "enma-pilot-backup-test"
        ENMA_MINIO_BACKUP_SECRET_KEY = "synthetic-minio-backup-test-only"
    }
    $previous = @{}
    try {
        foreach ($key in $synthetic.Keys) {
            $previous[$key] = [System.Environment]::GetEnvironmentVariable($key, "Process")
            [System.Environment]::SetEnvironmentVariable($key, $synthetic[$key], "Process")
        }
        $rendered = & $docker.Source compose `
            --project-name enma-pilot `
            --file $basePath `
            --file $composePath `
            --profile migrate `
            config --format json 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Pilot Compose rendering failed."
        }
    }
    finally {
        foreach ($key in $previous.Keys) {
            [System.Environment]::SetEnvironmentVariable($key, $previous[$key], "Process")
        }
    }

    $model = ($rendered -join "`n") | ConvertFrom-Json
    $oneShot = @("minio-bootstrap", "migrate")
    foreach ($service in $model.services.PSObject.Properties) {
        $definition = $service.Value
        $portsProperty = $definition.PSObject.Properties["ports"]
        if ($null -ne $portsProperty -and @($portsProperty.Value).Count -gt 0) {
            if ($service.Name -notin @("caddy", "mailpit")) {
                throw "Pilot service '$($service.Name)' must not publish ports."
            }
            foreach ($port in @($portsProperty.Value)) {
                if ($port.host_ip -cne "127.0.0.1") {
                    throw "Rendered Pilot Compose contains a non-loopback port."
                }
            }
        }
        $expectedRestart = if ($oneShot -contains $service.Name) { "no" } else { "unless-stopped" }
        if ($definition.restart -cne $expectedRestart) {
            throw "Pilot service '$($service.Name)' has an unexpected restart policy."
        }
        if ($definition.logging.driver -cne "json-file" -or
            [string]::IsNullOrEmpty($definition.logging.options.'max-size') -or
            [string]::IsNullOrEmpty($definition.logging.options.'max-file')) {
            throw "Pilot service '$($service.Name)' lacks rotated json-file logging."
        }
    }
    # D18: no shared network namespaces. Mailpit's extra bridge exists only to
    # publish its UI on loopback and has no other member.
    $expectedNetworks = @{
        "postgres" = @("backend")
        "minio" = @("backend")
        "minio-bootstrap" = @("backend")
        "migrate" = @("backend")
        "mailpit" = @("backend", "mailpit-ui")
        "api" = @("backend", "edge")
        "caddy" = @("edge")
    }
    foreach ($service in $model.services.PSObject.Properties) {
        if ($null -ne $service.Value.PSObject.Properties["network_mode"]) {
            throw "Pilot service '$($service.Name)' must not share another network namespace."
        }
        $actual = @($service.Value.networks.PSObject.Properties.Name | Sort-Object)
        if (-not $expectedNetworks.ContainsKey($service.Name) -or
            @(Compare-Object $expectedNetworks[$service.Name] $actual).Count -ne 0) {
            throw "Pilot service '$($service.Name)' is attached to unexpected networks."
        }
    }
    if ($model.networks.backend.internal -ne $true) {
        throw "The Pilot backend network must be internal."
    }
    # M4: the services of the shared compose.yaml keep its hardening: all
    # capabilities dropped, exactly these added back, no-new-privileges.
    # Mailpit, defined only by the Pilot, is the one exception.
    $allowedCapabilities = @{
        "postgres" = @("CHOWN", "DAC_OVERRIDE", "SETGID", "SETUID")
        "minio" = @()
        "minio-bootstrap" = @()
        "migrate" = @()
        "api" = @()
        "caddy" = @("NET_BIND_SERVICE")
    }
    foreach ($service in $model.services.PSObject.Properties) {
        if ($service.Name -ceq "mailpit") { continue }
        if (-not $allowedCapabilities.ContainsKey($service.Name)) {
            throw "Pilot service '$($service.Name)' has no capability allowlist."
        }
        $properties = $service.Value.PSObject.Properties
        $drop = if ($null -eq $properties["cap_drop"]) { "" } else { @($properties["cap_drop"].Value) -join "," }
        $add = if ($null -eq $properties["cap_add"]) { "" } else { @($properties["cap_add"].Value | Sort-Object) -join "," }
        $options = if ($null -eq $properties["security_opt"]) { "" } else { @($properties["security_opt"].Value) -join "," }
        if ($drop -cne "ALL" -or $add -cne ($allowedCapabilities[$service.Name] -join ",") -or
            $options -cne "no-new-privileges:true" -or
            ($null -ne $properties["privileged"] -and $properties["privileged"].Value -eq $true)) {
            throw "Pilot service '$($service.Name)' lost the capability hardening of compose.yaml."
        }
    }
    if ($model.services.api.environment.EmailVerification__DevelopmentDelivery__SmtpHost -cne "mailpit") {
        throw "The Pilot API must reach Mailpit by its service name."
    }
    # D33: the API connects as the application role, the migrator as itself.
    if ($model.services.api.environment.ConnectionStrings__Database -notmatch
            ';Username=enma_pilot_app_test;Password=synthetic-app-test-only;' -or
        $model.services.migrate.environment.ENMA_DESIGNTIME_CONNECTION_STRING -notmatch
            ';Username=enma_pilot_migrator_test;Password=synthetic-migrator-test-only;') {
        throw "The Pilot API and migrator must use the application and migrator roles."
    }

    foreach ($volume in @("postgres_data", "documents_data", "mailpit_data")) {
        if ($model.volumes.$volume.external -ne $true) {
            throw "Pilot data volume '$volume' must be external to Compose."
        }
    }
}

Write-Host "PILOT CONFIGURATION TESTS PASS"
