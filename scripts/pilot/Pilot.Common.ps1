Set-StrictMode -Version Latest

# Settings read from the external pilot.env. Older files may still carry the
# pre-container keys (database/MinIO/SMTP/API ports, HTTPS PFX, key path);
# they are ignored.
$script:PilotRequiredKeys = @(
    "PILOT_POSTGRES_DB",
    "PILOT_POSTGRES_USER",
    "PILOT_POSTGRES_PASSWORD",
    "PILOT_POSTGRES_MIGRATOR_USER",
    "PILOT_POSTGRES_MIGRATOR_PASSWORD",
    "PILOT_POSTGRES_APP_USER",
    "PILOT_POSTGRES_APP_PASSWORD",
    "PILOT_MINIO_ROOT_USER",
    "PILOT_MINIO_ROOT_PASSWORD",
    "PILOT_MINIO_APP_ACCESS_KEY",
    "PILOT_MINIO_APP_SECRET_KEY",
    "PILOT_MAILPIT_UI_PORT",
    "PILOT_WEB_HTTPS_PORT"
)

# The Pilot is served by Caddy on this dedicated *.localhost hostname, bound to
# 127.0.0.1:PILOT_WEB_HTTPS_PORT.
$script:PilotHostName = "enma-pilot.localhost"
$script:PilotImageTag = "pilot"
$script:PilotDataVolumes = @(
    "enma_pilot_postgres_data",
    "enma_pilot_minio_data",
    "enma_pilot_mailpit_data"
)
$script:PilotContainers = @(
    "enma-pilot-postgres",
    "enma-pilot-minio",
    "enma-pilot-minio-bootstrap",
    "enma-pilot-mailpit",
    "enma-pilot-api",
    "enma-pilot-caddy"
)

function Read-PilotEnvironment {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Pilot configuration file was not found."
    }

    $settings = @{}
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $Path) {
        $lineNumber++
        $trimmed = $line.Trim()
        if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed.StartsWith("#")) {
            continue
        }

        $separator = $line.IndexOf("=", [System.StringComparison]::Ordinal)
        if ($separator -lt 1) {
            throw "Invalid Pilot setting on line $lineNumber; expected KEY=VALUE."
        }
        $key = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1).Trim()
        if ($settings.ContainsKey($key)) {
            throw "Duplicate Pilot setting '$key'."
        }
        $settings[$key] = $value
    }

    foreach ($key in $script:PilotRequiredKeys) {
        if (-not $settings.ContainsKey($key) -or
            [string]::IsNullOrWhiteSpace($settings[$key]) -or
            $settings[$key] -match '^<.+>$') {
            throw "Pilot setting '$key' is missing or still contains a placeholder."
        }
    }

    return $settings
}

function Get-PilotPort {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Settings,
        [Parameter(Mandatory = $true)][string]$Key
    )

    $port = 0
    if ($Settings[$Key] -notmatch '^\d+$' -or
        -not [int]::TryParse($Settings[$Key], [ref]$port) -or
        $port -lt 1 -or $port -gt 65535) {
        throw "Pilot setting '$Key' must be a port between 1 and 65535."
    }
    return $port
}

function Assert-PilotIdentitySettings {
    param([Parameter(Mandatory = $true)][hashtable]$Settings)

    foreach ($key in @(
        "PILOT_POSTGRES_DB", "PILOT_POSTGRES_USER",
        "PILOT_POSTGRES_MIGRATOR_USER", "PILOT_POSTGRES_APP_USER",
        "PILOT_MINIO_ROOT_USER", "PILOT_MINIO_APP_ACCESS_KEY")) {
        if ($Settings[$key] -notmatch 'pilot') {
            throw "Pilot identity setting '$key' must contain the marker 'pilot'."
        }
    }
    # D33/D34: the same rules as Production (scripts/production/common.sh).
    foreach ($key in @("PILOT_POSTGRES_MIGRATOR_USER", "PILOT_POSTGRES_APP_USER")) {
        if (-not (Test-EnmaDatabaseRoleName -Name $Settings[$key])) {
            throw "Pilot setting '$key' must match ^[a-z_][a-z0-9_]{0,62}$ and not start with pg_."
        }
    }
    foreach ($key in @("PILOT_POSTGRES_MIGRATOR_PASSWORD", "PILOT_POSTGRES_APP_PASSWORD")) {
        if ($Settings[$key] -cnotmatch '^[A-Za-z0-9._~-]{16,}$') {
            throw "Pilot setting '$key' must be URL-safe with at least 16 characters."
        }
    }
    $roles = @(
        $Settings.PILOT_POSTGRES_USER,
        $Settings.PILOT_POSTGRES_MIGRATOR_USER,
        $Settings.PILOT_POSTGRES_APP_USER)
    if (@($roles | Select-Object -Unique).Count -ne $roles.Count) {
        throw "Pilot PostgreSQL superuser, migrator and application roles must differ."
    }
    $secrets = @(
        $Settings.PILOT_POSTGRES_PASSWORD,
        $Settings.PILOT_POSTGRES_MIGRATOR_PASSWORD,
        $Settings.PILOT_POSTGRES_APP_PASSWORD,
        $Settings.PILOT_MINIO_ROOT_PASSWORD,
        $Settings.PILOT_MINIO_APP_SECRET_KEY)
    if (@($secrets | Select-Object -Unique).Count -ne $secrets.Count) {
        throw "Pilot credentials must be distinct from one another."
    }
}

function Test-EnmaDatabaseRoleName {
    param([AllowEmptyString()][string]$Name)

    return $Name -cmatch '^[a-z_][a-z0-9_]{0,62}$' -and -not $Name.StartsWith("pg_")
}

# D34: runs deploy/postgres/enma-roles.sql inside a PostgreSQL container as its
# bootstrap superuser (the container's own POSTGRES_USER) over the local
# socket, then one call: "roles" before migrations and restores, "grants"
# after them. Shared by the Pilot, the restore drill and Development. The
# passwords travel only on psql's stdin, never in arguments, environment
# variables or files, and are redacted from any error output.
function Invoke-EnmaDatabaseRoleScript {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("roles", "grants")][string]$Phase,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [Parameter(Mandatory = $true)][string]$Container,
        [Parameter(Mandatory = $true)][string]$MigratorRole,
        [Parameter(Mandatory = $true)][string]$AppRole,
        [string]$MigratorPassword = "",
        [string]$AppPassword = "",
        [string]$DockerCommand = "docker"
    )

    if (-not (Test-EnmaDatabaseRoleName -Name $MigratorRole) -or
        -not (Test-EnmaDatabaseRoleName -Name $AppRole)) {
        throw "Database role names must match ^[a-z_][a-z0-9_]{0,62}$ and not start with pg_."
    }
    if ($Phase -eq "roles") {
        foreach ($password in @($MigratorPassword, $AppPassword)) {
            if ($password -cnotmatch '^[A-Za-z0-9._~-]{16,}$') {
                throw "Database role passwords must be URL-safe with at least 16 characters."
            }
        }
        $call = "SELECT pg_temp.enma_apply_roles('$MigratorRole', '$AppRole', " +
            "'$MigratorPassword', '$AppPassword');"
    }
    else {
        $call = "SELECT pg_temp.enma_apply_grants('$MigratorRole', '$AppRole');"
    }

    $script = [System.IO.File]::ReadAllText(
        (Join-Path $RepositoryRoot "deploy\postgres\enma-roles.sql"))
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = ($script + "`n" + $call + "`n") | & $DockerCommand exec -i $Container `
            sh -c 'exec psql -X -q -At -v ON_ERROR_STOP=1 -U $POSTGRES_USER -d $POSTGRES_DB' 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -ne 0) {
        $message = (@($output) | ForEach-Object { $_.ToString() }) -join " "
        foreach ($password in @($MigratorPassword, $AppPassword)) {
            if (-not [string]::IsNullOrEmpty($password)) {
                $message = $message.Replace($password, "[redacted]")
            }
        }
        throw "Database $Phase phase failed: $message"
    }
}

function Invoke-PilotDatabaseRoles {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Settings,
        [Parameter(Mandatory = $true)][ValidateSet("roles", "grants")][string]$Phase,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot,
        [string]$DockerCommand = "docker"
    )

    Invoke-EnmaDatabaseRoleScript `
        -Phase $Phase `
        -RepositoryRoot $RepositoryRoot `
        -Container "enma-pilot-postgres" `
        -MigratorRole $Settings.PILOT_POSTGRES_MIGRATOR_USER `
        -AppRole $Settings.PILOT_POSTGRES_APP_USER `
        -MigratorPassword $Settings.PILOT_POSTGRES_MIGRATOR_PASSWORD `
        -AppPassword $Settings.PILOT_POSTGRES_APP_PASSWORD `
        -DockerCommand $DockerCommand
    Write-Host "Pilot database $Phase applied."
}

function Assert-PathOutsideRepository {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    $candidate = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $repository = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    if ($candidate.Equals($repository, [System.StringComparison]::OrdinalIgnoreCase) -or
        $candidate.StartsWith(
            $repository + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Pilot configuration and recovery paths must remain outside the repository."
    }
}

function Assert-PathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $candidate = [System.IO.Path]::GetFullPath($Path)
    $parentPath = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/') +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith(
        $parentPath,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Pilot secret material must remain inside the Pilot configuration root."
    }
}

function Protect-PilotDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if ([System.Environment]::OSVersion.Platform -ne
        [System.PlatformID]::Win32NT) {
        throw "Pilot secret storage requires Windows ACL support."
    }

    $inheritance = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    $fullControl = [System.Security.AccessControl.FileSystemRights]::FullControl
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    $identities = @(
        [System.Security.Principal.WindowsIdentity]::GetCurrent().User,
        (New-Object System.Security.Principal.SecurityIdentifier("S-1-5-18")),
        (New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544"))
    )
    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    foreach ($identity in $identities) {
        $security.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $identity,
            $fullControl,
            $inheritance,
            [System.Security.AccessControl.PropagationFlags]::None,
            $allow)))
    }
    # Writes only the DACL; Set-Acl rewrites the whole descriptor and needs
    # privileges a non-elevated Windows PowerShell session does not hold.
    (Get-Item -LiteralPath $Path).SetAccessControl($security)
}

function Assert-CommandAvailable {
    param([Parameter(Mandatory = $true)][string]$Command)

    $resolved = Get-Command -Name $Command -ErrorAction SilentlyContinue
    if ($null -eq $resolved) {
        throw "Required command '$Command' is not available on PATH."
    }
    return $resolved.Source
}

function Invoke-PilotCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [string[]]$Arguments = @(),
        [switch]$CaptureOutput
    )

    if ($CaptureOutput) {
        $output = & $Command @Arguments 2>&1
    }
    else {
        & $Command @Arguments
    }
    if ($LASTEXITCODE -ne 0) {
        throw "Pilot command '$Command' failed with exit code $LASTEXITCODE."
    }
    if ($CaptureOutput) { return $output }
}

function Assert-PilotDockerResourceOwnership {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("container", "volume")][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$DockerCommand = "docker"
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $label = & $DockerCommand $Kind inspect `
            --format '{{index .Labels "com.docker.compose.project"}}' $Name 2>$null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    if ($exitCode -eq 0 -and $label.ToString().Trim() -cne "enma-pilot") {
        throw "Existing Docker $Kind '$Name' is not owned by the enma-pilot project."
    }
}

function Test-PilotContainerRunning {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$DockerCommand = "docker"
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $state = & $DockerCommand container inspect `
            --format '{{.State.Running}}' $Name 2>$null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    return $exitCode -eq 0 -and $state.ToString().Trim() -ceq "true"
}

function Test-PilotDockerResourceExists {
    param(
        [Parameter(Mandatory = $true)][ValidateSet("container", "volume")][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$DockerCommand = "docker"
    )

    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        & $DockerCommand $Kind inspect $Name 2>$null | Out-Null
        return $LASTEXITCODE -eq 0
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
}

function Assert-PilotContainerEnvironment {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][hashtable]$Expected,
        [string]$DockerCommand = "docker"
    )

    if (-not (Test-PilotDockerResourceExists `
        -Kind container -Name $Name -DockerCommand $DockerCommand)) {
        return
    }
    $environmentJson = Invoke-PilotCommand -Command $DockerCommand -Arguments @(
        "container", "inspect", "--format", "{{json .Config.Env}}", $Name) `
        -CaptureOutput
    $actual = @{}
    foreach ($entry in (($environmentJson -join "") | ConvertFrom-Json)) {
        $separator = $entry.IndexOf("=", [System.StringComparison]::Ordinal)
        if ($separator -gt 0) {
            $actual[$entry.Substring(0, $separator)] = $entry.Substring($separator + 1)
        }
    }
    foreach ($key in $Expected.Keys) {
        if (-not $actual.ContainsKey($key) -or
            $actual[$key] -cne $Expected[$key]) {
            throw "Existing Pilot container '$Name' does not match the external Pilot configuration."
        }
    }
}

function Assert-RunningPilotContainerPort {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][int]$ContainerPort,
        [Parameter(Mandatory = $true)][int]$HostPort,
        [string]$DockerCommand = "docker"
    )

    $binding = Invoke-PilotCommand -Command $DockerCommand -Arguments @(
        "port", $Name, "$ContainerPort/tcp") -CaptureOutput
    $expected = "127.0.0.1:$HostPort"
    if (@($binding).Count -ne 1 -or $binding.ToString().Trim() -cne $expected) {
        throw "Running Pilot container '$Name' does not use the expected loopback binding."
    }
}

function Assert-LoopbackPortAvailable {
    param(
        [Parameter(Mandatory = $true)][int]$Port,
        [Parameter(Mandatory = $true)][string]$Purpose
    )

    $listener = [System.Net.Sockets.TcpListener]::new(
        [System.Net.IPAddress]::Loopback,
        $Port)
    try {
        $listener.Start()
    }
    catch [System.Net.Sockets.SocketException] {
        throw "Pilot $Purpose port $Port is already in use."
    }
    finally {
        $listener.Stop()
    }
}

function Get-PilotComposeArguments {
    param([Parameter(Mandatory = $true)][string]$RepositoryRoot)

    return @(
        "compose", "--project-name", "enma-pilot",
        "--file", (Join-Path $RepositoryRoot "deploy\compose.yaml"),
        "--file", (Join-Path $RepositoryRoot "deploy\compose.pilot.yaml")
    )
}

function Get-PilotComposeEnvironment {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Settings,
        [string]$Revision = "unknown"
    )

    return @{
        ENMA_IMAGE_TAG = $script:PilotImageTag
        ENMA_REVISION = $Revision
        ENMA_PUBLIC_HOST = $script:PilotHostName
        ENMA_PILOT_HTTPS_PORT = (Get-PilotPort -Settings $Settings -Key "PILOT_WEB_HTTPS_PORT").ToString()
        ENMA_PILOT_MAILPIT_UI_PORT = (Get-PilotPort -Settings $Settings -Key "PILOT_MAILPIT_UI_PORT").ToString()
        ENMA_POSTGRES_DB = $Settings.PILOT_POSTGRES_DB
        ENMA_POSTGRES_USER = $Settings.PILOT_POSTGRES_USER
        ENMA_POSTGRES_PASSWORD = $Settings.PILOT_POSTGRES_PASSWORD
        ENMA_POSTGRES_MIGRATOR_USER = $Settings.PILOT_POSTGRES_MIGRATOR_USER
        ENMA_POSTGRES_MIGRATOR_PASSWORD = $Settings.PILOT_POSTGRES_MIGRATOR_PASSWORD
        ENMA_POSTGRES_APP_USER = $Settings.PILOT_POSTGRES_APP_USER
        ENMA_POSTGRES_APP_PASSWORD = $Settings.PILOT_POSTGRES_APP_PASSWORD
        ENMA_MINIO_ROOT_USER = $Settings.PILOT_MINIO_ROOT_USER
        ENMA_MINIO_ROOT_PASSWORD = $Settings.PILOT_MINIO_ROOT_PASSWORD
        ENMA_MINIO_APP_ACCESS_KEY = $Settings.PILOT_MINIO_APP_ACCESS_KEY
        ENMA_MINIO_APP_SECRET_KEY = $Settings.PILOT_MINIO_APP_SECRET_KEY
    }
}

# Compose reads the ENMA_* variables from this process only for the duration
# of the script block; secrets are never written to another file.
function Invoke-WithPilotComposeEnvironment {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Environment,
        [Parameter(Mandatory = $true)][scriptblock]$ScriptBlock
    )

    $previous = @{}
    try {
        foreach ($key in $Environment.Keys) {
            $previous[$key] = [System.Environment]::GetEnvironmentVariable($key, "Process")
            [System.Environment]::SetEnvironmentVariable($key, $Environment[$key], "Process")
        }
        & $ScriptBlock
    }
    finally {
        foreach ($key in $previous.Keys) {
            [System.Environment]::SetEnvironmentVariable($key, $previous[$key], "Process")
        }
    }
}

function Get-PilotAppliedMigration {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Settings,
        [string]$DockerCommand = "docker"
    )

    # Sent on stdin: Windows PowerShell mangles double quotes in native
    # arguments, and the quoted identifiers are case-sensitive.
    $query = @'
select to_regclass('public."__EFMigrationsHistory"') is not null as has_history \gset
\if :has_history
select coalesce(max("MigrationId"), '(none)') from "__EFMigrationsHistory";
\else
select '(none)';
\endif
'@
    $result = @($query | & $DockerCommand exec -i enma-pilot-postgres `
        psql -X -At -v ON_ERROR_STOP=1 `
        -U $Settings.PILOT_POSTGRES_USER `
        -d $Settings.PILOT_POSTGRES_DB `
        -f -)
    if ($LASTEXITCODE -ne 0) {
        throw "Pilot migration history query failed."
    }
    if ($result.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$result[0])) {
        throw "Pilot migration history query did not return exactly one value."
    }
    return ([string]$result[0]).Trim()
}
