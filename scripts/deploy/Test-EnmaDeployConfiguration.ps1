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

$storageIdentityKeys = @(
    "ENMA_MINIO_ROOT_USER", "ENMA_MINIO_ROOT_PASSWORD",
    "ENMA_MINIO_APP_ACCESS_KEY", "ENMA_MINIO_APP_SECRET_KEY",
    "ENMA_MINIO_BACKUP_ACCESS_KEY", "ENMA_MINIO_BACKUP_SECRET_KEY")

# D36: the backup identity exists and shares no credential with the root or
# application identities; backup.sh puts it in a URL, so it is URL-safe.
function Assert-StorageIdentityValues {
    param([Parameter(Mandatory = $true)][hashtable]$Values)

    foreach ($key in $storageIdentityKeys) {
        if (-not $Values.ContainsKey($key) -or [string]::IsNullOrEmpty($Values[$key])) {
            throw "Storage identity setting '$key' is missing."
        }
    }
    foreach ($key in @("ENMA_MINIO_BACKUP_ACCESS_KEY", "ENMA_MINIO_BACKUP_SECRET_KEY")) {
        if ($Values[$key] -cnotmatch '^[A-Za-z0-9._~-]+$') {
            throw "Storage setting '$key' must be URL-safe."
        }
    }
    $accessKeys = @($Values.ENMA_MINIO_ROOT_USER, $Values.ENMA_MINIO_APP_ACCESS_KEY, $Values.ENMA_MINIO_BACKUP_ACCESS_KEY)
    $secrets = @($Values.ENMA_MINIO_ROOT_PASSWORD, $Values.ENMA_MINIO_APP_SECRET_KEY, $Values.ENMA_MINIO_BACKUP_SECRET_KEY)
    if (@($accessKeys | Select-Object -Unique).Count -ne 3 -or
        @($secrets | Select-Object -Unique).Count -ne 3) {
        throw "The MinIO root, application and backup credentials must all differ."
    }
}

# The backup policy grants exactly ListBucket on the bucket and GetObject on
# its objects.
function Assert-BackupPolicy {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Bucket
    )

    $policy = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $granted = @(foreach ($statement in @($policy.Statement)) {
        if ($statement.Effect -cne "Allow" -or $null -ne $statement.PSObject.Properties["Condition"]) {
            throw "Backup policy '$Path' has an unexpected statement."
        }
        foreach ($action in @($statement.Action)) {
            foreach ($resource in @($statement.Resource)) { "$action $resource" }
        }
    }) | Sort-Object
    $expected = @("s3:GetObject arn:aws:s3:::$Bucket/*", "s3:ListBucket arn:aws:s3:::$Bucket")
    if (@(Compare-Object $expected $granted).Count -ne 0) {
        throw "Backup policy '$Path' must grant only ListBucket on the bucket and GetObject on its objects."
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

# Mirrors resolve_edge_mode and is_tailnet_ipv4 in
# scripts/production/common.sh (D39).
function Assert-EdgeModeValues {
    param([Parameter(Mandatory = $true)][hashtable]$Values)

    $mode = $Values["ENMA_EDGE_MODE"]
    if ($mode -cnotin @("public", "tailnet")) {
        throw "ENMA_EDGE_MODE must be public or tailnet."
    }
    if ($mode -ceq "public") {
        return
    }
    $octet = '(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])'
    if ([string]$Values["ENMA_EDGE_BIND_ADDRESS"] -cnotmatch "^100\.(6[4-9]|[7-9][0-9]|1[01][0-9]|12[0-7])\.$octet\.$octet$") {
        throw "ENMA_EDGE_BIND_ADDRESS must be the node's Tailscale IPv4 address (100.64.0.0/10)."
    }
    if (-not ([string]$Values["ENMA_EDGE_TLS_DIR"]).StartsWith("/")) {
        throw "ENMA_EDGE_TLS_DIR must be an absolute directory."
    }
}

# Tailnet edge (D-B, D-C as amended): Caddy publishes exactly 443/tcp on the
# configured Tailscale IPv4 and nothing on IPv6 (docker-proxy would hide the
# client address), serves Caddyfile.tailnet, and reads the certificate
# directory read-only.
function Assert-TailnetEdge {
    param(
        [Parameter(Mandatory = $true)]$Model,
        [Parameter(Mandatory = $true)][hashtable]$Values
    )

    $ports = @($Model.services.caddy.ports)
    foreach ($port in $ports) {
        $hostIpProperty = $port.PSObject.Properties["host_ip"]
        $hostIp = if ($null -eq $hostIpProperty) { "" } else { [string]$hostIpProperty.Value }
        if ([string]::IsNullOrEmpty($hostIp) -or $hostIp -in @("0.0.0.0", "::", "[::]")) {
            throw "Tailnet Caddy publishes a port on every interface."
        }
        if ([int]$port.target -ne 443 -or [string]$port.published -cne "443" -or $port.protocol -cne "tcp") {
            throw "Tailnet Caddy publishes a port other than 443/tcp."
        }
    }
    if ($ports.Count -ne 1 -or [string]$ports[0].host_ip -cne $Values.ENMA_EDGE_BIND_ADDRESS) {
        throw "Tailnet Caddy must publish 443/tcp exactly once, on the Tailscale IPv4 bind address."
    }
    $volumes = @($Model.services.caddy.volumes)
    $caddyfiles = @($volumes | Where-Object { $_.target -ceq "/etc/caddy/Caddyfile" })
    if ($caddyfiles.Count -ne 1 -or $caddyfiles[0].read_only -ne $true -or
        ($caddyfiles[0].source -replace '\\', '/') -notlike "*/deploy/caddy/Caddyfile.tailnet") {
        throw "Tailnet Caddy must mount only Caddyfile.tailnet as its Caddyfile."
    }
    $certificates = @($volumes | Where-Object { $_.target -ceq "/etc/caddy/tls" })
    if ($certificates.Count -ne 1 -or $certificates[0].type -cne "bind" -or
        $certificates[0].read_only -ne $true -or
        ($certificates[0].source -replace '\\', '/') -notlike "*$($Values.ENMA_EDGE_TLS_DIR)") {
        throw "Tailnet Caddy must mount ENMA_EDGE_TLS_DIR read-only at /etc/caddy/tls."
    }
}

# Everything but Caddy's ports, Caddyfile and certificate mounts is the same
# in both modes: services, networks, proxy trust, images, restart, logging.
function Get-ModelOutsideEdge {
    param([Parameter(Mandatory = $true)]$Model)

    $copy = $Model | ConvertTo-Json -Depth 32 | ConvertFrom-Json
    $copy.services.caddy.PSObject.Properties.Remove("ports")
    $copy.services.caddy.volumes = @($copy.services.caddy.volumes |
        Where-Object { $_.target -cnotin @("/etc/caddy/Caddyfile", "/etc/caddy/tls") })
    return $copy | ConvertTo-Json -Depth 32 -Compress
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
$tailnetPath = Join-Path $deployRoot "compose.tailnet.yaml"
$examplePath = Join-Path $deployRoot "production.env.example"

$referenced = @(
    foreach ($path in @($basePath, $productionPath, $tailnetPath)) {
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
# A literal value, not a placeholder: the CI egress check renders the example.
if ($exampleText -cnotmatch '(?m)^ENMA_EDGE_MODE=public\r?$') {
    throw "production.env.example must set ENMA_EDGE_MODE=public."
}

# The `grep -E[i]q '<pattern>' -- "$ENMA_ENV_FILE"` guards of deploy.sh, read
# from the script itself so a changed guard is tested as it is. An example
# with its placeholders filled in must pass every guard, so a copied example
# is deployable; the guards must still reject placeholders and a connection
# string with Include Error Detail.
$deployScript = Get-Content -LiteralPath (Join-Path $repositoryRoot "scripts\production\deploy.sh") -Raw
$envGuards = @([regex]::Matches($deployScript, "grep -E(i?)q '([^']+)' -- `"\`$ENMA_ENV_FILE`"") | ForEach-Object {
    $options = if ($_.Groups[1].Value -ceq "i") { "(?mi)" } else { "(?m)" }
    $options + $_.Groups[2].Value.Replace("[[:space:]]", "\s")
})
if ($envGuards.Count -ne 2) {
    throw "deploy.sh must keep its two environment file guards (placeholders, Include Error Detail)."
}
function Test-EnvGuards {
    param([Parameter(Mandatory = $true)][string]$Text)
    return @($envGuards | Where-Object { $Text -match $_ }).Count -eq 0
}
$filledExample = $exampleText -replace '<[^>]*>', 'synthetic'
if (-not (Test-EnvGuards -Text $filledExample)) {
    throw "production.env.example with its placeholders filled in fails a deploy.sh guard."
}
foreach ($rejected in @(
    $exampleText,
    "$filledExample`nENMA_EXTRA=Host=postgres;Include Error Detail=true`n",
    "$filledExample`nENMA_EXTRA=Host=postgres;includeerrordetail=true`n")) {
    if (Test-EnvGuards -Text $rejected) {
        throw "A deploy.sh environment file guard no longer rejects placeholders or Include Error Detail."
    }
}

# D40/D41: the renewal unit runs renew-cert.sh like the backup unit runs
# backup.sh, and its timer fires daily with a randomized delay.
$systemdRoot = Join-Path $deployRoot "systemd"
$renewService = Get-Content -LiteralPath (Join-Path $systemdRoot "enma-renew-cert.service") -Raw
$renewTimer = Get-Content -LiteralPath (Join-Path $systemdRoot "enma-renew-cert.timer") -Raw
foreach ($required in @(
    '(?m)^Type=oneshot$',
    '(?m)^Environment=ENMA_ENV_FILE=/etc/enma/production\.env$',
    '(?m)^ExecStart=/opt/enma/scripts/production/renew-cert\.sh$',
    '(?m)^PrivateTmp=yes$',
    '(?m)^NoNewPrivileges=yes$')) {
    if ($renewService -cnotmatch $required) {
        throw "enma-renew-cert.service does not match '$required'."
    }
}
foreach ($required in @(
    '(?m)^OnCalendar=\*-\*-\* \d\d:\d\d:\d\d America/Sao_Paulo$',
    '(?m)^RandomizedDelaySec=\S+$',
    '(?m)^Persistent=true$',
    '(?m)^WantedBy=timers\.target$')) {
    if ($renewTimer -cnotmatch $required) {
        throw "enma-renew-cert.timer does not match '$required'."
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot "scripts\production\renew-cert.sh") -PathType Leaf)) {
    throw "enma-renew-cert.service runs a script that does not exist."
}

# D48: the heartbeat unit runs heartbeat.sh every 5 minutes with at most 30 s
# of randomized delay, and production.env.example documents the heartbeat
# URLs the script reads.
$heartbeatService = Get-Content -LiteralPath (Join-Path $systemdRoot "enma-heartbeat.service") -Raw
$heartbeatTimer = Get-Content -LiteralPath (Join-Path $systemdRoot "enma-heartbeat.timer") -Raw
foreach ($required in @(
    '(?m)^Type=oneshot$',
    '(?m)^Environment=ENMA_ENV_FILE=/etc/enma/production\.env$',
    '(?m)^ExecStart=/opt/enma/scripts/production/heartbeat\.sh$',
    '(?m)^PrivateTmp=yes$',
    '(?m)^NoNewPrivileges=yes$',
    '(?m)^TimeoutStartSec=\S+$')) {
    if ($heartbeatService -cnotmatch $required) {
        throw "enma-heartbeat.service does not match '$required'."
    }
}
foreach ($required in @(
    '(?m)^OnCalendar=\*:0/5$',
    '(?m)^WantedBy=timers\.target$')) {
    if ($heartbeatTimer -cnotmatch $required) {
        throw "enma-heartbeat.timer does not match '$required'."
    }
}
$heartbeatDelay = [regex]::Match($heartbeatTimer, '(?m)^RandomizedDelaySec=(\d+)s?$')
if ($heartbeatDelay.Success -and [int]$heartbeatDelay.Groups[1].Value -gt 30) {
    throw "enma-heartbeat.timer must not delay a beat by more than 30 seconds."
}
if ($heartbeatTimer -cmatch '(?m)^RandomizedDelaySec=' -and -not $heartbeatDelay.Success) {
    throw "enma-heartbeat.timer must give RandomizedDelaySec in seconds."
}
$heartbeatScriptPath = Join-Path $repositoryRoot "scripts\production\heartbeat.sh"
if (-not (Test-Path -LiteralPath $heartbeatScriptPath -PathType Leaf)) {
    throw "enma-heartbeat.service runs a script that does not exist."
}
$heartbeatKeys = @([regex]::Matches((Get-Content -LiteralPath $heartbeatScriptPath -Raw), 'ENMA_HEALTH_HEARTBEAT_[A-Z_]+_URL') |
    ForEach-Object { $_.Value }) | Sort-Object -Unique
if ($heartbeatKeys.Count -ne 2) {
    throw "heartbeat.sh must read the success and failure heartbeat URLs."
}
foreach ($key in $heartbeatKeys) {
    if ($exampleText -cnotmatch "(?m)^$key=<https://[^>]+>\r?$") {
        throw "production.env.example must document $key with an https placeholder."
    }
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
# D-A: the tailnet edge loads the host's certificate files; Caddy issues and
# fetches nothing, and serves no HTTP redirect listener.
$tailnetCaddyfile = Get-Content -LiteralPath (Join-Path $deployRoot "caddy\Caddyfile.tailnet") -Raw
foreach ($required in @(
    '(?m)^\s+auto_https disable_redirects$',
    '(?m)^import /etc/caddy/enma\.caddy$',
    '(?m)^\s+tls /etc/caddy/tls/tls\.crt /etc/caddy/tls/tls\.key$',
    '(?m)^\s+import enma_security_headers$',
    '(?m)^\s+header Strict-Transport-Security "max-age=31536000"$',
    '(?m)^\s+import enma_api_proxy$')) {
    if ($tailnetCaddyfile -cnotmatch $required) {
        throw "Tailnet Caddyfile does not match '$required'."
    }
}
$tailnetDirectives = @($tailnetCaddyfile -split "`n" | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
if ($tailnetDirectives -match 'tls internal|get_certificate|includeSubDomains|acme|(?m)^\s+email\b') {
    throw "Tailnet Caddyfile must load the certificate files only, without ACME or includeSubDomains."
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
        "ENMA_EDGE_BIND_ADDRESS" { "100.100.100.1" }
        "ENMA_EDGE_TLS_DIR" { "/etc/enma/tls" }
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

foreach ($key in $storageIdentityKeys) {
    if ($referenced -notcontains $key) {
        throw "The Compose files must read the storage identity setting '$key'."
    }
}
Assert-StorageIdentityValues -Values $synthetic
$storageNegatives = [ordered]@{
    "backup access key equals the root user" = @((Copy-Values -Changes @{ ENMA_MINIO_BACKUP_ACCESS_KEY = $synthetic.ENMA_MINIO_ROOT_USER }), "credentials must all differ")
    "backup access key equals the application's" = @((Copy-Values -Changes @{ ENMA_MINIO_BACKUP_ACCESS_KEY = $synthetic.ENMA_MINIO_APP_ACCESS_KEY }), "credentials must all differ")
    "backup secret equals the root password" = @((Copy-Values -Changes @{ ENMA_MINIO_BACKUP_SECRET_KEY = $synthetic.ENMA_MINIO_ROOT_PASSWORD }), "credentials must all differ")
    "backup secret equals the application's" = @((Copy-Values -Changes @{ ENMA_MINIO_BACKUP_SECRET_KEY = $synthetic.ENMA_MINIO_APP_SECRET_KEY }), "credentials must all differ")
    "backup secret not URL-safe" = @((Copy-Values -Changes @{ ENMA_MINIO_BACKUP_SECRET_KEY = "synthetic/backup@secret" }), "must be URL-safe")
    "backup access key missing" = @((Copy-Values -Remove @("ENMA_MINIO_BACKUP_ACCESS_KEY")), "'ENMA_MINIO_BACKUP_ACCESS_KEY' is missing")
}
foreach ($case in $storageNegatives.Keys) {
    $values, $expected = $storageNegatives[$case]
    Assert-Rejected -Case $case -Expected $expected -Check { Assert-StorageIdentityValues -Values $values }
}
$backupPolicyPath = Join-Path $repositoryRoot "infrastructure\minio\enma-documents-backup-policy.json"
Assert-BackupPolicy -Path $backupPolicyPath -Bucket "enma-documents"
$writablePolicyPath = Join-Path $repositoryRoot "infrastructure\minio\enma-documents-app-policy.json"
Assert-Rejected -Case "a writable policy as the backup policy" -Expected "must grant only ListBucket" -Check {
    Assert-BackupPolicy -Path $writablePolicyPath -Bucket "enma-documents"
}

Assert-EdgeModeValues -Values (Copy-Values -Changes @{ ENMA_EDGE_MODE = "public" })
$tailnetValues = Copy-Values -Changes @{ ENMA_EDGE_MODE = "tailnet" }
Assert-EdgeModeValues -Values $tailnetValues
$edgeNegatives = [ordered]@{
    "edge mode missing" = @((Copy-Values), "ENMA_EDGE_MODE must be public or tailnet")
    "edge mode not public or tailnet" = @((Copy-Values -Changes @{ ENMA_EDGE_MODE = "Tailnet" }), "ENMA_EDGE_MODE must be public or tailnet")
    "IPv4 bind on every interface" = @((Copy-Values -Changes @{ ENMA_EDGE_MODE = "tailnet"; ENMA_EDGE_BIND_ADDRESS = "0.0.0.0" }), "Tailscale IPv4 address")
    "IPv4 bind outside 100.64.0.0/10" = @((Copy-Values -Changes @{ ENMA_EDGE_MODE = "tailnet"; ENMA_EDGE_BIND_ADDRESS = "100.128.0.1" }), "Tailscale IPv4 address")
    "IPv4 bind missing" = @((Copy-Values -Changes @{ ENMA_EDGE_MODE = "tailnet" } -Remove @("ENMA_EDGE_BIND_ADDRESS")), "Tailscale IPv4 address")
    "IPv6 address as the bind" = @((Copy-Values -Changes @{ ENMA_EDGE_MODE = "tailnet"; ENMA_EDGE_BIND_ADDRESS = "fd7a:115c:a1e0::1" }), "Tailscale IPv4 address")
    "certificate directory missing" = @((Copy-Values -Changes @{ ENMA_EDGE_MODE = "tailnet" } -Remove @("ENMA_EDGE_TLS_DIR")), "ENMA_EDGE_TLS_DIR must be an absolute directory")
    "certificate directory relative" = @((Copy-Values -Changes @{ ENMA_EDGE_MODE = "tailnet"; ENMA_EDGE_TLS_DIR = "tls" }), "ENMA_EDGE_TLS_DIR must be an absolute directory")
}
foreach ($case in $edgeNegatives.Keys) {
    $values, $expected = $edgeNegatives[$case]
    Assert-Rejected -Case $case -Expected $expected -Check { Assert-EdgeModeValues -Values $values }
}

$docker = Get-Command -Name $DockerCommand -ErrorAction SilentlyContinue
if ($null -eq $docker) {
    Write-Host "Docker not found; Compose rendering checks were skipped."
    Write-Host "DEPLOY CONFIGURATION TESTS PASS"
    exit 0
}

function Invoke-ComposeRender {
    param([Parameter(Mandatory = $true)][hashtable]$Values, [switch]$Tailnet)

    $files = @("--file", $basePath, "--file", $productionPath)
    if ($Tailnet) { $files += @("--file", $tailnetPath) }
    $previous = @{}
    try {
        foreach ($name in $referenced) {
            $previous[$name] = [System.Environment]::GetEnvironmentVariable($name, "Process")
            $value = if ($Values.ContainsKey($name)) { $Values[$name] } else { $null }
            [System.Environment]::SetEnvironmentVariable($name, $value, "Process")
        }
        $rendered = & $docker.Source compose @files `
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
foreach ($key in $databaseIdentityKeys + @("ENMA_MINIO_BACKUP_ACCESS_KEY", "ENMA_MINIO_BACKUP_SECRET_KEY")) {
    $values = Copy-Values -Remove @($key)
    Assert-Rejected -Case "Compose renders without $key" -Expected "Production Compose rendering failed|required variable $key is missing a value" -Check { Invoke-ComposeRender -Values $values }
}
$bootstrap = $model.services.'minio-bootstrap'
if (@($bootstrap.volumes | Where-Object {
        $_.target -ceq "/policies/backup-policy.json" -and $_.read_only -eq $true -and
        ($_.source -replace '\\', '/') -like "*/infrastructure/minio/enma-documents-backup-policy.json" }).Count -ne 1 -or
    $bootstrap.environment.MINIO_BACKUP_ACCESS_KEY -cne $synthetic.ENMA_MINIO_BACKUP_ACCESS_KEY -or
    (@($bootstrap.command) -join "`n") -notmatch 'mc admin policy attach enma "\$+DOCUMENTS_BUCKET-backup" --user "\$+MINIO_BACKUP_ACCESS_KEY"') {
    throw "The storage bootstrap must create the backup identity with its read-only policy."
}
if (($model.services.api.environment | ConvertTo-Json -Compress).Contains($synthetic.ENMA_MINIO_BACKUP_SECRET_KEY)) {
    throw "The API must not receive the backup identity."
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

# D-B/D-C: the tailnet override changes only Caddy's ports and mounts.
$tailnetModel = Invoke-ComposeRender -Values $tailnetValues -Tailnet
Assert-TailnetEdge -Model $tailnetModel -Values $tailnetValues
if ((Get-ModelOutsideEdge -Model $tailnetModel) -cne (Get-ModelOutsideEdge -Model $model)) {
    throw "The tailnet override changes more than Caddy's ports and mounts."
}
$publicVolumes = @($model.services.caddy.volumes | Where-Object { $_.target -ceq "/etc/caddy/Caddyfile" })
if ($publicVolumes.Count -ne 1 -or ($publicVolumes[0].source -replace '\\', '/') -notlike "*/deploy/caddy/Caddyfile.production" -or
    @($model.services.caddy.volumes | Where-Object { $_.target -ceq "/etc/caddy/tls" }).Count -ne 0) {
    throw "Public mode must keep Caddyfile.production and no certificate directory."
}
$tailnetRenderNegatives = [ordered]@{
    "tailnet IPv4 bind missing" = @((Copy-Values -Remove @("ENMA_EDGE_BIND_ADDRESS")), "Production Compose rendering failed")
    "tailnet certificate directory missing" = @((Copy-Values -Remove @("ENMA_EDGE_TLS_DIR")), "Production Compose rendering failed")
}
foreach ($case in $tailnetRenderNegatives.Keys) {
    $values, $expected = $tailnetRenderNegatives[$case]
    Assert-Rejected -Case $case -Expected $expected -Check { Invoke-ComposeRender -Values $values -Tailnet }
}
$tailnetJson = $tailnetModel | ConvertTo-Json -Depth 32
$port80 = $tailnetJson | ConvertFrom-Json
$port80.services.caddy.ports += [pscustomobject]@{ mode = "ingress"; host_ip = $tailnetValues.ENMA_EDGE_BIND_ADDRESS; target = 80; published = "80"; protocol = "tcp" }
$udp = $tailnetJson | ConvertFrom-Json
$udp.services.caddy.ports[0].protocol = "udp"
$ipv6 = $tailnetJson | ConvertFrom-Json
$ipv6.services.caddy.ports += [pscustomobject]@{ mode = "ingress"; host_ip = "fd7a:115c:a1e0::1"; target = 443; published = "443"; protocol = "tcp" }
$tailnetModelNegatives = [ordered]@{
    "port 80 reintroduced" = @($port80, $tailnetValues, "port other than 443/tcp")
    "HTTP/3 port" = @($udp, $tailnetValues, "port other than 443/tcp")
    "IPv6 bind reintroduced" = @($ipv6, $tailnetValues, "exactly once, on the Tailscale IPv4")
    "IPv4 bind on every interface" = @((Invoke-ComposeRender -Values (Copy-Values -Changes @{ ENMA_EDGE_BIND_ADDRESS = "0.0.0.0" }) -Tailnet), $tailnetValues, "on every interface")
    "IPv4 bind not the configured address" = @($tailnetModel, (Copy-Values -Changes @{ ENMA_EDGE_BIND_ADDRESS = "100.100.100.2" }), "exactly once, on the Tailscale IPv4")
    "production ports kept" = @($model, $tailnetValues, "on every interface")
}
foreach ($case in $tailnetModelNegatives.Keys) {
    $candidate, $values, $expected = $tailnetModelNegatives[$case]
    Assert-Rejected -Case $case -Expected $expected -Check { Assert-TailnetEdge -Model $candidate -Values $values }
}

Write-Host "DEPLOY CONFIGURATION TESTS PASS"
# The rejected renders above leave $LASTEXITCODE non-zero, and a pwsh step
# exits with $LASTEXITCODE; every failure throws before reaching this line.
exit 0
