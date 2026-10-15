[CmdletBinding()]
param([string]$DockerCommand = "docker")

# Static checks of the Production deployment artifact (D13-D15): renders the
# Compose model with synthetic values and verifies the edge-only exposure,
# restart/log policies, environment, the database identities (D33/D34), and
# that production.env.example names every variable the Compose files read.
# Every identity check also runs against broken synthetic values that it must
# reject, so a check that stops failing closed fails here.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$databaseIdentityKeys = @(
    "ENMA_POSTGRES_USER", "ENMA_POSTGRES_PASSWORD",
    "ENMA_POSTGRES_MIGRATOR_USER", "ENMA_POSTGRES_MIGRATOR_PASSWORD",
    "ENMA_POSTGRES_APP_USER", "ENMA_POSTGRES_APP_PASSWORD")

# Mirrors assert_database_identities in scripts/production/common.sh.
function Assert-DatabaseIdentityValues {
    param([Parameter(Mandatory = $true)][hashtable]$Values)

    foreach ($key in $databaseIdentityKeys) {
        if (-not $Values.ContainsKey($key) -or [string]::IsNullOrEmpty($Values[$key])) {
            throw "Database identity setting '$key' is missing."
        }
    }
    foreach ($key in @("ENMA_POSTGRES_MIGRATOR_USER", "ENMA_POSTGRES_APP_USER")) {
        if ($Values[$key] -cnotmatch '^[a-z_][a-z0-9_]{0,62}$' -or $Values[$key].StartsWith("pg_")) {
            throw "Database role name '$key' is invalid."
        }
    }
    foreach ($key in @("ENMA_POSTGRES_MIGRATOR_PASSWORD", "ENMA_POSTGRES_APP_PASSWORD")) {
        if ($Values[$key] -cnotmatch '^[A-Za-z0-9._~-]{16,}$') {
            throw "Database password '$key' must be URL-safe with at least 16 characters."
        }
    }
    $names = @($Values.ENMA_POSTGRES_USER, $Values.ENMA_POSTGRES_MIGRATOR_USER, $Values.ENMA_POSTGRES_APP_USER)
    $passwords = @($Values.ENMA_POSTGRES_PASSWORD, $Values.ENMA_POSTGRES_MIGRATOR_PASSWORD, $Values.ENMA_POSTGRES_APP_PASSWORD)
    if (@($names | Select-Object -Unique).Count -ne 3) {
        throw "The PostgreSQL superuser, migrator and application roles must differ."
    }
    if (@($passwords | Select-Object -Unique).Count -ne 3) {
        throw "The PostgreSQL superuser, migrator and application passwords must differ."
    }
}

function ConvertFrom-ConnectionString {
    param([Parameter(Mandatory = $true)][string]$Value)

    $parts = @{}
    foreach ($pair in $Value.Split(';')) {
        $separator = $pair.IndexOf('=')
        if ($separator -gt 0) {
            $parts[$pair.Substring(0, $separator).Trim()] = $pair.Substring($separator + 1)
        }
    }
    return $parts
}

# The API holds only the application role, the migrator only its own role,
# and the bootstrap superuser stays in the postgres service.
function Assert-DatabaseConnections {
    param([Parameter(Mandatory = $true)]$Model)

    $superuser = $Model.services.postgres.environment.POSTGRES_USER
    $superuserPassword = $Model.services.postgres.environment.POSTGRES_PASSWORD
    $api = ConvertFrom-ConnectionString -Value $Model.services.api.environment.ConnectionStrings__Database
    $migrator = ConvertFrom-ConnectionString -Value $Model.services.migrate.environment.ENMA_DESIGNTIME_CONNECTION_STRING
    foreach ($connection in @($api, $migrator)) {
        if ($connection["Host"] -cne "postgres" -or
            $connection["Gss Encryption Mode"] -cne "Disable" -or
            [string]::IsNullOrEmpty($connection["Username"]) -or
            [string]::IsNullOrEmpty($connection["Password"])) {
            throw "A database connection string is incomplete or lost Gss Encryption Mode=Disable."
        }
    }
    if ($api["Username"] -ceq $superuser -or $api["Password"] -ceq $superuserPassword) {
        throw "The API must not connect as the PostgreSQL superuser."
    }
    if ($api["Username"] -ceq $migrator["Username"] -or $api["Password"] -ceq $migrator["Password"]) {
        throw "The API must not connect as the migrator."
    }
    if ($migrator["Username"] -ceq $superuser -or $migrator["Password"] -ceq $superuserPassword) {
        throw "The migrator must not connect as the PostgreSQL superuser."
    }
    $foreignPasswords = @{
        api = @($superuserPassword, $migrator["Password"])
        migrate = @($superuserPassword, $api["Password"])
    }
    foreach ($service in $foreignPasswords.Keys) {
        $environmentJson = $Model.services.$service.environment | ConvertTo-Json -Compress
        foreach ($password in $foreignPasswords[$service]) {
            if ($environmentJson.Contains($password)) {
                throw "Service '$service' receives another identity's database password."
            }
        }
    }
}

# A negative case must be rejected, and for the expected reason.
function Assert-Rejected {
    param(
        [Parameter(Mandatory = $true)][string]$Case,
        [Parameter(Mandatory = $true)][scriptblock]$Check,
        [Parameter(Mandatory = $true)][string]$Expected
    )

    $message = $null
    try { & $Check | Out-Null }
    catch { $message = $_.Exception.Message }
    if ($null -eq $message) {
        throw "Negative check was not rejected: $Case."
    }
    if ($message -notmatch $Expected) {
        throw "Negative check '$Case' was rejected for an unexpected reason: $message"
    }
}

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

foreach ($key in $databaseIdentityKeys) {
    if ($referenced -notcontains $key) {
        throw "The Compose files must read the database identity setting '$key'."
    }
}

$synthetic = @{}
foreach ($name in $referenced) {
    $synthetic[$name] = switch ($name) {
        "ENMA_PUBLIC_HOST" { "enma.example.test" }
        "ENMA_SMTP_PORT" { "587" }
        "ENMA_SMTP_SECURITY" { "StartTls" }
        "ENMA_GOOGLE_ENABLED" { "false" }
        "ENMA_EDGE_SUBNET" { "10.247.31.0/28" }
        "ENMA_EDGE_DYNAMIC_RANGE" { "10.247.31.8/29" }
        "ENMA_EDGE_PROXY_IP" { "10.247.31.2" }
        "ENMA_POSTGRES_USER" { "synthetic_bootstrap" }
        "ENMA_POSTGRES_MIGRATOR_USER" { "synthetic_migrator" }
        "ENMA_POSTGRES_APP_USER" { "synthetic_app" }
        default { "synthetic-" + $name.ToLowerInvariant().Replace("_", "-") }
    }
}

function Copy-Values {
    param([hashtable]$Changes = @{}, [string[]]$Remove = @())

    $copy = $synthetic.Clone()
    foreach ($key in $Changes.Keys) { $copy[$key] = $Changes[$key] }
    foreach ($key in $Remove) { $copy.Remove($key) }
    return $copy
}

Assert-DatabaseIdentityValues -Values $synthetic
$identityNegatives = [ordered]@{
    "invalid migrator role name" = @((Copy-Values -Changes @{ ENMA_POSTGRES_MIGRATOR_USER = "Synthetic-Migrator" }), "role name 'ENMA_POSTGRES_MIGRATOR_USER' is invalid")
    "invalid application role name" = @((Copy-Values -Changes @{ ENMA_POSTGRES_APP_USER = "synthetic app" }), "role name 'ENMA_POSTGRES_APP_USER' is invalid")
    "reserved application role name" = @((Copy-Values -Changes @{ ENMA_POSTGRES_APP_USER = "pg_synthetic" }), "role name 'ENMA_POSTGRES_APP_USER' is invalid")
    "application role is the superuser" = @((Copy-Values -Changes @{ ENMA_POSTGRES_APP_USER = $synthetic.ENMA_POSTGRES_USER }), "roles must differ")
    "application role is the migrator" = @((Copy-Values -Changes @{ ENMA_POSTGRES_APP_USER = $synthetic.ENMA_POSTGRES_MIGRATOR_USER }), "roles must differ")
    "application password equals the superuser's" = @((Copy-Values -Changes @{ ENMA_POSTGRES_APP_PASSWORD = $synthetic.ENMA_POSTGRES_PASSWORD }), "passwords must differ")
    "application password equals the migrator's" = @((Copy-Values -Changes @{ ENMA_POSTGRES_APP_PASSWORD = $synthetic.ENMA_POSTGRES_MIGRATOR_PASSWORD }), "passwords must differ")
    "migrator password equals the superuser's" = @((Copy-Values -Changes @{ ENMA_POSTGRES_MIGRATOR_PASSWORD = $synthetic.ENMA_POSTGRES_PASSWORD }), "passwords must differ")
    "migrator password not URL-safe" = @((Copy-Values -Changes @{ ENMA_POSTGRES_MIGRATOR_PASSWORD = "synthetic;password;value" }), "must be URL-safe")
    "application password missing" = @((Copy-Values -Remove @("ENMA_POSTGRES_APP_PASSWORD")), "'ENMA_POSTGRES_APP_PASSWORD' is missing")
}
foreach ($case in $identityNegatives.Keys) {
    $values, $expected = $identityNegatives[$case]
    Assert-Rejected -Case $case -Expected $expected -Check { Assert-DatabaseIdentityValues -Values $values }
}

$docker = Get-Command -Name $DockerCommand -ErrorAction SilentlyContinue
if ($null -eq $docker) {
    Write-Host "Docker not found; Compose rendering checks were skipped."
    Write-Host "DEPLOY CONFIGURATION TESTS PASS"
    return
}

function Invoke-ComposeRender {
    param([Parameter(Mandatory = $true)][hashtable]$Values)

    $previous = @{}
    try {
        foreach ($name in $referenced) {
            $previous[$name] = [System.Environment]::GetEnvironmentVariable($name, "Process")
            $value = if ($Values.ContainsKey($name)) { $Values[$name] } else { $null }
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
    return ($rendered -join "`n") | ConvertFrom-Json
}

$model = Invoke-ComposeRender -Values $synthetic
Assert-DatabaseConnections -Model $model
$renderNegatives = [ordered]@{
    "API connects as the superuser" = @((Copy-Values -Changes @{ ENMA_POSTGRES_APP_USER = $synthetic.ENMA_POSTGRES_USER; ENMA_POSTGRES_APP_PASSWORD = $synthetic.ENMA_POSTGRES_PASSWORD }), "API must not connect as the PostgreSQL superuser")
    "API connects as the migrator" = @((Copy-Values -Changes @{ ENMA_POSTGRES_APP_USER = $synthetic.ENMA_POSTGRES_MIGRATOR_USER; ENMA_POSTGRES_APP_PASSWORD = $synthetic.ENMA_POSTGRES_MIGRATOR_PASSWORD }), "API must not connect as the migrator")
}
foreach ($case in $renderNegatives.Keys) {
    $values, $expected = $renderNegatives[$case]
    Assert-Rejected -Case $case -Expected $expected -Check { Assert-DatabaseConnections -Model (Invoke-ComposeRender -Values $values) }
}
foreach ($key in $databaseIdentityKeys) {
    $values = Copy-Values -Remove @($key)
    Assert-Rejected -Case "Compose renders without $key" -Expected "Production Compose rendering failed|required variable $key is missing a value" -Check { Invoke-ComposeRender -Values $values }
}
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
