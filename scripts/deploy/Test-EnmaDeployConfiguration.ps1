[CmdletBinding()]
param([string]$DockerCommand = "docker")

# Static checks of the Production deployment artifact (D13-D15): renders the
# Compose model with synthetic values and verifies the edge-only exposure,
# restart/log policies, environment, and that production.env.example names
# every variable the Compose files read.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$deployRoot = Join-Path $repositoryRoot "deploy"
$basePath = Join-Path $deployRoot "compose.yaml"
$productionPath = Join-Path $deployRoot "compose.production.yaml"
$examplePath = Join-Path $deployRoot "production.env.example"

$referenced = @(
    foreach ($path in @($basePath, $productionPath)) {
        [regex]::Matches(
            (Get-Content -LiteralPath $path -Raw),
            '(?<!\$)\$\{([A-Z0-9_]+)') | ForEach-Object { $_.Groups[1].Value }
    }) | Sort-Object -Unique
$exampleText = Get-Content -LiteralPath $examplePath -Raw
$documented = @([regex]::Matches($exampleText, '(?m)^#?\s*([A-Z0-9_]+)=') |
    ForEach-Object { $_.Groups[1].Value }) | Sort-Object -Unique
$missing = @($referenced | Where-Object { $documented -notcontains $_ })
if ($missing.Count -gt 0) {
    throw "production.env.example does not document: $($missing -join ', ')"
}
if ($exampleText -match '(?m)^[A-Z0-9_]*(PASSWORD|SECRET|SECRET_KEY)=(?!<|$).+') {
    throw "production.env.example must contain placeholders only."
}

$caddyfile = Get-Content -LiteralPath (Join-Path $deployRoot "caddy\Caddyfile.production") -Raw
$shared = Get-Content -LiteralPath (Join-Path $deployRoot "caddy\enma.caddy") -Raw
if ($caddyfile -notmatch 'Strict-Transport-Security' -or
    $caddyfile -match 'tls internal' -or
    $caddyfile -notmatch '(?m)^\s+import enma_security_headers$') {
    throw "Production Caddyfile must use public TLS, HSTS, and the shared headers."
}
foreach ($required in @(
    "default-src 'self'", "script-src 'self';", "object-src 'none'",
    "base-uri 'self'", "frame-ancestors 'none'", "connect-src 'self'",
    "X-Content-Type-Options", "Referrer-Policy", "-Server", "header_up -X-Forwarded-Host")) {
    if ($shared.IndexOf($required, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Shared Caddy configuration is missing '$required'."
    }
}
# D11: reverse_proxy passes the public Host through; a rewrite could change it.
if ($shared -match '(?m)^\s*header_up\s+Host\b') {
    throw "Shared Caddy configuration must not rewrite the Host header."
}
if ($shared -match "unsafe-inline|unsafe-eval") {
    throw "The CSP must not allow unsafe-inline or unsafe-eval."
}

$docker = Get-Command -Name $DockerCommand -ErrorAction SilentlyContinue
if ($null -eq $docker) {
    Write-Host "Docker not found; Compose rendering checks were skipped."
    Write-Host "DEPLOY CONFIGURATION TESTS PASS"
    return
}

$previous = @{}
try {
    foreach ($name in $referenced) {
        $previous[$name] = [System.Environment]::GetEnvironmentVariable($name, "Process")
        $value = switch ($name) {
            "ENMA_PUBLIC_HOST" { "enma.example.test" }
            "ENMA_SMTP_PORT" { "587" }
            "ENMA_SMTP_SECURITY" { "StartTls" }
            "ENMA_GOOGLE_ENABLED" { "false" }
            "ENMA_EDGE_SUBNET" { "10.247.31.0/28" }
            "ENMA_EDGE_DYNAMIC_RANGE" { "10.247.31.8/29" }
            "ENMA_EDGE_PROXY_IP" { "10.247.31.2" }
            default { "synthetic-" + $name.ToLowerInvariant().Replace("_", "-") }
        }
        [System.Environment]::SetEnvironmentVariable($name, $value, "Process")
    }
    $rendered = & $docker.Source compose `
        --file $basePath `
        --file $productionPath `
        --profile migrate `
        config --format json 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Production Compose rendering failed."
    }
}
finally {
    foreach ($name in $previous.Keys) {
        [System.Environment]::SetEnvironmentVariable($name, $previous[$name], "Process")
    }
}

$model = ($rendered -join "`n") | ConvertFrom-Json
if ($model.name -cne "enma-production") {
    throw "Production Compose must use the enma-production project."
}
$expectedServices = @("api", "caddy", "migrate", "minio", "minio-bootstrap", "postgres")
$services = @($model.services.PSObject.Properties.Name | Sort-Object)
if (@(Compare-Object $expectedServices $services).Count -ne 0) {
    throw "Production Compose resolved unexpected services."
}
$oneShot = @("minio-bootstrap", "migrate")
foreach ($service in $model.services.PSObject.Properties) {
    $definition = $service.Value
    $portsProperty = $definition.PSObject.Properties["ports"]
    if ($null -ne $portsProperty -and @($portsProperty.Value).Count -gt 0) {
        if ($service.Name -cne "caddy") {
            throw "Only Caddy may publish ports; '$($service.Name)' does."
        }
        foreach ($port in @($portsProperty.Value)) {
            if ([int]$port.target -notin @(80, 443)) {
                throw "Caddy publishes an unexpected port."
            }
        }
    }
    # Base images are pinned by multi-arch index digest; only the locally
    # built release images are referenced by tag.
    if ($definition.image -notmatch '^enma-(api|migrator):' -and
        $definition.image -notmatch ':[^/@]+@sha256:[0-9a-f]{64}$') {
        throw "Service '$($service.Name)' image must be pinned by tag and digest."
    }
    $expectedRestart = if ($oneShot -contains $service.Name) { "no" } else { "unless-stopped" }
    if ($definition.restart -cne $expectedRestart) {
        throw "Service '$($service.Name)' has an unexpected restart policy."
    }
    if ($definition.logging.driver -cne "json-file" -or
        [string]::IsNullOrEmpty($definition.logging.options.'max-size') -or
        [string]::IsNullOrEmpty($definition.logging.options.'max-file')) {
        throw "Service '$($service.Name)' lacks rotated json-file logging."
    }
}
if ($model.services.api.environment.ASPNETCORE_ENVIRONMENT -cne "Production" -or
    $model.services.api.environment.Deployment__TrustedProxy__KnownProxies__0 -cne
        $model.services.caddy.networks.edge.ipv4_address) {
    throw "The Production API environment or proxy trust is wrong."
}
# D18: no shared network namespaces; data services only on the internal
# backend network, and only the API and Caddy on edge.
$expectedNetworks = @{
    "postgres" = @("backend")
    "minio" = @("backend")
    "minio-bootstrap" = @("backend")
    "migrate" = @("backend")
    "api" = @("backend", "edge")
    "caddy" = @("edge")
}
foreach ($name in $expectedNetworks.Keys) {
    $definition = $model.services.$name
    if ($null -ne $definition.PSObject.Properties["network_mode"]) {
        throw "Service '$name' must not share another network namespace."
    }
    $actual = @($definition.networks.PSObject.Properties.Name | Sort-Object)
    if (@(Compare-Object $expectedNetworks[$name] $actual).Count -ne 0) {
        throw "Service '$name' is attached to unexpected networks."
    }
}
if ($model.services.api.environment.DocumentStorage__ServiceUrl -cne "http://minio:9000") {
    throw "The API must reach MinIO by its service name on the backend network."
}
if (@($model.services.migrate.profiles) -notcontains "migrate") {
    throw "The migrator must run only through its dedicated profile."
}
if ($model.networks.backend.internal -ne $true) {
    throw "The backend network must be internal."
}

Write-Host "DEPLOY CONFIGURATION TESTS PASS"
