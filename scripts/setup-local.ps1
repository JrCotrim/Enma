[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Shared database role wrapper (D34). Loaded first, so the helpers below keep
# their own definitions.
. (Join-Path $PSScriptRoot "pilot\Pilot.Common.ps1")

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Title,

        [Parameter(Mandatory = $true)]
        [string]$Command,

        [string[]]$Arguments = @(),

        [switch]$CaptureOutput
    )

    Write-Host ""
    Write-Host "==> $Title"

    if ($CaptureOutput) {
        $commandOutput = & $Command @Arguments 2>&1
    }
    else {
        & $Command @Arguments
    }

    $commandExitCode = $LASTEXITCODE
    if ($commandExitCode -ne 0) {
        $exception = New-Object System.Exception(
            "Command '$Command' failed with exit code $commandExitCode."
        )
        $exception.Data["ExitCode"] = $commandExitCode
        throw $exception
    }

    if ($CaptureOutput) {
        return $commandOutput
    }
}

function Assert-CommandAvailable {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Command
    )

    if ($null -eq (Get-Command -Name $Command -ErrorAction SilentlyContinue)) {
        throw "Required command '$Command' is not available on PATH."
    }
}

function Read-LocalEnvironment {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    $requiredKeys = @(
        "POSTGRES_DB",
        "POSTGRES_USER",
        "POSTGRES_PASSWORD",
        "POSTGRES_PORT",
        "POSTGRES_MIGRATOR_USER",
        "POSTGRES_MIGRATOR_PASSWORD",
        "POSTGRES_APP_USER",
        "POSTGRES_APP_PASSWORD",
        "MINIO_ROOT_USER",
        "MINIO_ROOT_PASSWORD",
        "MINIO_APP_ACCESS_KEY",
        "MINIO_APP_SECRET_KEY",
        "MINIO_API_PORT",
        "MINIO_CONSOLE_PORT"
    )
    $settings = @{}
    $lineNumber = 0

    foreach ($line in Get-Content -LiteralPath $Path) {
        $lineNumber++
        $trimmedLine = $line.Trim()

        if ([string]::IsNullOrWhiteSpace($trimmedLine) -or
            $trimmedLine.StartsWith("#", [System.StringComparison]::Ordinal)) {
            continue
        }

        $separatorIndex = $line.IndexOf("=", [System.StringComparison]::Ordinal)
        if ($separatorIndex -lt 1) {
            throw "Invalid setting on line $lineNumber of .env; expected KEY=VALUE."
        }

        $key = $line.Substring(0, $separatorIndex).Trim()
        if ($requiredKeys -notcontains $key) {
            continue
        }

        if ($settings.ContainsKey($key)) {
            throw "Duplicate required setting '$key' in .env."
        }

        $value = $line.Substring($separatorIndex + 1).Trim()
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw "Required setting '$key' in .env must not be empty."
        }

        $settings[$key] = $value
    }

    foreach ($requiredKey in $requiredKeys) {
        if (-not $settings.ContainsKey($requiredKey)) {
            throw "Required setting '$requiredKey' is missing from .env."
        }
    }

    return $settings
}

function Get-ValidatedPort {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable]$Settings,

        [Parameter(Mandatory = $true)]
        [string]$Key
    )

    $port = 0
    if ($Settings[$Key] -notmatch "^\d+$" -or
        -not [int]::TryParse($Settings[$Key], [ref]$port)) {
        throw "$Key must be numeric."
    }

    if ($port -lt 1 -or $port -gt 65535) {
        throw "$Key must be between 1 and 65535."
    }

    return $port
}

function Wait-ForHealthyContainer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName
    )

    $maximumHealthChecks = 60
    $healthCheckIntervalSeconds = 2

    Write-Host ""
    Write-Host "==> Wait for $DisplayName health check"

    for ($attempt = 1; $attempt -le $maximumHealthChecks; $attempt++) {
        $containerStateOutput = Invoke-CheckedCommand `
            -Title "Inspect $DisplayName container (attempt $attempt of $maximumHealthChecks)" `
            -Command "docker" `
            -Arguments @(
                "inspect",
                "--format",
                "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}",
                $ContainerName
            ) `
            -CaptureOutput

        $containerState = ($containerStateOutput | Select-Object -Last 1).ToString().Trim()
        $stateParts = $containerState.Split("|")
        if ($stateParts.Length -ne 2) {
            throw "Unable to determine the $DisplayName container state."
        }

        $runtimeState = $stateParts[0]
        $healthState = $stateParts[1]
        Write-Host "$DisplayName container state: $runtimeState; health: $healthState."

        if ($runtimeState -eq "running" -and $healthState -eq "healthy") {
            return
        }

        if ($runtimeState -in @("dead", "exited", "removing") -or
            $healthState -eq "unhealthy") {
            throw "$DisplayName failed to become healthy (state: $runtimeState; health: $healthState)."
        }

        Start-Sleep -Seconds $healthCheckIntervalSeconds
    }

    throw "$DisplayName did not become healthy within the allowed timeout."
}

function Wait-ForSuccessfulOneShotContainer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,

        [Parameter(Mandatory = $true)]
        [string]$DisplayName
    )

    $maximumChecks = 60
    $checkIntervalSeconds = 2

    Write-Host ""
    Write-Host "==> Wait for $DisplayName"

    for ($attempt = 1; $attempt -le $maximumChecks; $attempt++) {
        $containerStateOutput = Invoke-CheckedCommand `
            -Title "Inspect $DisplayName container (attempt $attempt of $maximumChecks)" `
            -Command "docker" `
            -Arguments @(
                "inspect",
                "--format",
                "{{.State.Status}}|{{.State.ExitCode}}",
                $ContainerName
            ) `
            -CaptureOutput

        $containerState = ($containerStateOutput | Select-Object -Last 1).ToString().Trim()
        $stateParts = $containerState.Split("|")
        if ($stateParts.Length -ne 2) {
            throw "Unable to determine the $DisplayName container state."
        }

        $runtimeState = $stateParts[0]
        $exitCode = 0

        if ($runtimeState -eq "exited") {
            if (-not [int]::TryParse($stateParts[1], [ref]$exitCode)) {
                throw "Unable to determine the $DisplayName exit code."
            }

            if ($exitCode -eq 0) {
                Write-Host "$DisplayName completed successfully."
                return
            }

            throw "$DisplayName failed with exit code $exitCode."
        }

        if ($runtimeState -in @("dead", "removing")) {
            throw "$DisplayName entered unexpected state '$runtimeState'."
        }

        Start-Sleep -Seconds $checkIntervalSeconds
    }

    throw "$DisplayName did not complete within the allowed timeout."
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$locationChanged = $false
$scriptExitCode = 0

try {
    Assert-CommandAvailable -Command "dotnet"
    Assert-CommandAvailable -Command "docker"

    $requiredFiles = @(
        "compose.yaml",
        ".env.example",
        "src\Enma.Api\Enma.Api.csproj",
        "src\Enma.Infrastructure\Enma.Infrastructure.csproj",
        "Enma.slnx"
    )

    foreach ($requiredFile in $requiredFiles) {
        $requiredPath = Join-Path $repositoryRoot $requiredFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required file was not found: $requiredPath"
        }
    }

    Push-Location -LiteralPath $repositoryRoot
    $locationChanged = $true

    $environmentPath = Join-Path $repositoryRoot ".env"
    if (Test-Path -LiteralPath $environmentPath -PathType Leaf) {
        Write-Host "Reusing existing local .env file."
    }
    else {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot ".env.example") -Destination $environmentPath
        Write-Host "Created local .env file from .env.example."
    }

    $settings = Read-LocalEnvironment -Path $environmentPath

    $postgresPort = Get-ValidatedPort `
        -Settings $settings `
        -Key "POSTGRES_PORT"
    $minioApiPort = Get-ValidatedPort `
        -Settings $settings `
        -Key "MINIO_API_PORT"
    $minioConsolePort = Get-ValidatedPort `
        -Settings $settings `
        -Key "MINIO_CONSOLE_PORT"

    if ($minioApiPort -eq $minioConsolePort -or
        $minioApiPort -eq $postgresPort -or
        $minioConsolePort -eq $postgresPort) {
        throw "POSTGRES_PORT, MINIO_API_PORT, and MINIO_CONSOLE_PORT must be distinct."
    }

    if ($settings["MINIO_ROOT_USER"] -eq $settings["MINIO_APP_ACCESS_KEY"]) {
        throw "MINIO_ROOT_USER and MINIO_APP_ACCESS_KEY must be different identities."
    }

    if ($settings["MINIO_ROOT_PASSWORD"].Length -lt 8 -or
        $settings["MINIO_APP_SECRET_KEY"].Length -lt 8) {
        throw "MinIO development secrets must be at least 8 characters long."
    }

    if ($settings["MINIO_ROOT_PASSWORD"] -eq $settings["MINIO_APP_SECRET_KEY"]) {
        throw "MinIO root and application secrets must be different."
    }

    foreach ($key in @("POSTGRES_MIGRATOR_USER", "POSTGRES_APP_USER")) {
        if (-not (Test-EnmaDatabaseRoleName -Name $settings[$key])) {
            throw "$key must match ^[a-z_][a-z0-9_]{0,62}$ and not start with pg_."
        }
    }
    foreach ($key in @("POSTGRES_MIGRATOR_PASSWORD", "POSTGRES_APP_PASSWORD")) {
        if ($settings[$key] -cnotmatch '^[A-Za-z0-9._~-]{16,}$') {
            throw "$key must be URL-safe with at least 16 characters."
        }
    }
    $databaseRoles = @($settings["POSTGRES_USER"], $settings["POSTGRES_MIGRATOR_USER"], $settings["POSTGRES_APP_USER"])
    $databasePasswords = @($settings["POSTGRES_PASSWORD"], $settings["POSTGRES_MIGRATOR_PASSWORD"], $settings["POSTGRES_APP_PASSWORD"])
    if (@($databaseRoles | Select-Object -Unique).Count -ne 3 -or
        @($databasePasswords | Select-Object -Unique).Count -ne 3) {
        throw "The PostgreSQL superuser, migrator and application roles and passwords must all differ."
    }

    Invoke-CheckedCommand -Title "Confirm Docker availability" -Command "docker" -Arguments @(
        "version"
    )

    Invoke-CheckedCommand -Title "Start local PostgreSQL and private MinIO" -Command "docker" -Arguments @(
        "compose",
        "--env-file",
        ".env",
        "up",
        "-d",
        "postgres",
        "minio",
        "minio-bootstrap"
    )

    Wait-ForHealthyContainer `
        -ContainerName "enma-postgres" `
        -DisplayName "PostgreSQL"

    Wait-ForHealthyContainer `
        -ContainerName "enma-minio" `
        -DisplayName "MinIO"

    Wait-ForSuccessfulOneShotContainer `
        -ContainerName "enma-minio-bootstrap" `
        -DisplayName "MinIO bootstrap"

    # D33: the migrations run as the migrator and the API connects as the
    # application role; POSTGRES_USER is used only inside the container.
    $migratorConnectionString = (
        "Host=localhost;Port={0};Database={1};Username={2};Password={3}" -f
            $postgresPort,
            $settings["POSTGRES_DB"],
            $settings["POSTGRES_MIGRATOR_USER"],
            $settings["POSTGRES_MIGRATOR_PASSWORD"]
    )
    $connectionString = (
        "Host=localhost;Port={0};Database={1};Username={2};Password={3}" -f
            $postgresPort,
            $settings["POSTGRES_DB"],
            $settings["POSTGRES_APP_USER"],
            $settings["POSTGRES_APP_PASSWORD"]
    )

    Invoke-CheckedCommand -Title "Configure the API document-storage access key User Secret" -Command "dotnet" -Arguments @(
        "user-secrets",
        "set",
        "DocumentStorage:AccessKey",
        $settings["MINIO_APP_ACCESS_KEY"],
        "--project",
        ".\src\Enma.Api\Enma.Api.csproj"
    )

    Invoke-CheckedCommand -Title "Configure the API document-storage secret key User Secret" -Command "dotnet" -Arguments @(
        "user-secrets",
        "set",
        "DocumentStorage:SecretKey",
        $settings["MINIO_APP_SECRET_KEY"],
        "--project",
        ".\src\Enma.Api\Enma.Api.csproj"
    )

    Invoke-CheckedCommand -Title "Configure the API document-storage endpoint User Secret" -Command "dotnet" -Arguments @(
        "user-secrets",
        "set",
        "DocumentStorage:ServiceUrl",
        ("http://127.0.0.1:{0}" -f $minioApiPort),
        "--project",
        ".\src\Enma.Api\Enma.Api.csproj"
    )

    Invoke-CheckedCommand -Title "Restore local .NET tools" -Command "dotnet" -Arguments @(
        "tool",
        "restore"
    )

    # Roles phase first: on a database created before the roles it is also the
    # idempotent conversion (D34).
    Write-Host ""
    Write-Host "==> Apply the PostgreSQL roles (deploy/postgres/enma-roles.sql)"
    Invoke-EnmaDatabaseRoleScript `
        -Phase roles `
        -RepositoryRoot $repositoryRoot `
        -Container "enma-postgres" `
        -MigratorRole $settings["POSTGRES_MIGRATOR_USER"] `
        -AppRole $settings["POSTGRES_APP_USER"] `
        -MigratorPassword $settings["POSTGRES_MIGRATOR_PASSWORD"] `
        -AppPassword $settings["POSTGRES_APP_PASSWORD"]

    $previousDesignTimeConnectionString = [Environment]::GetEnvironmentVariable(
        "ENMA_DESIGNTIME_CONNECTION_STRING",
        [EnvironmentVariableTarget]::Process
    )

    try {
        $env:ENMA_DESIGNTIME_CONNECTION_STRING = $migratorConnectionString

        Invoke-CheckedCommand -Title "Apply existing EF Core migrations" -Command "dotnet" -Arguments @(
            "tool",
            "run",
            "dotnet-ef",
            "database",
            "update",
            "--project",
            ".\src\Enma.Infrastructure\Enma.Infrastructure.csproj",
            "--startup-project",
            ".\src\Enma.Infrastructure\Enma.Infrastructure.csproj",
            "--context",
            "EnmaDbContext"
        )
    }
    finally {
        if ($null -eq $previousDesignTimeConnectionString) {
            Remove-Item -Path "Env:\ENMA_DESIGNTIME_CONNECTION_STRING" -ErrorAction SilentlyContinue
        }
        else {
            $env:ENMA_DESIGNTIME_CONNECTION_STRING = $previousDesignTimeConnectionString
        }
    }

    Write-Host ""
    Write-Host "==> Apply the PostgreSQL grants (deploy/postgres/enma-roles.sql)"
    Invoke-EnmaDatabaseRoleScript `
        -Phase grants `
        -RepositoryRoot $repositoryRoot `
        -Container "enma-postgres" `
        -MigratorRole $settings["POSTGRES_MIGRATOR_USER"] `
        -AppRole $settings["POSTGRES_APP_USER"] `
        -MigratorPassword $settings["POSTGRES_MIGRATOR_PASSWORD"] `
        -AppPassword $settings["POSTGRES_APP_PASSWORD"]

    # Only after the grants: the API never points at a role without them.
    Invoke-CheckedCommand -Title "Configure the API database User Secret (application role)" -Command "dotnet" -Arguments @(
        "user-secrets",
        "set",
        "ConnectionStrings:Database",
        $connectionString,
        "--project",
        ".\src\Enma.Api\Enma.Api.csproj"
    )

    Write-Host ""
    Write-Host "Local environment setup completed."
    Write-Host "Start the API with:"
    Write-Host 'dotnet run --project ".\src\Enma.Api\Enma.Api.csproj"'
    Write-Host "The API will use database and private document-storage settings stored in local configuration/User Secrets."
}
catch {
    if ($_.Exception.Data.Contains("ExitCode")) {
        $scriptExitCode = [int]$_.Exception.Data["ExitCode"]
    }
    else {
        $scriptExitCode = 1
    }

    [Console]::Error.WriteLine("Local environment setup failed: {0}", $_.Exception.Message)
}
finally {
    if ($locationChanged) {
        Pop-Location
    }
}

exit $scriptExitCode
