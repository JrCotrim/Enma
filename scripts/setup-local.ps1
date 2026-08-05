[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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
        "POSTGRES_PORT"
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

    $postgresPort = 0
    if ($settings["POSTGRES_PORT"] -notmatch "^\d+$" -or
        -not [int]::TryParse($settings["POSTGRES_PORT"], [ref]$postgresPort)) {
        throw "POSTGRES_PORT must be numeric."
    }

    if ($postgresPort -lt 1 -or $postgresPort -gt 65535) {
        throw "POSTGRES_PORT must be between 1 and 65535."
    }

    Invoke-CheckedCommand -Title "Confirm Docker availability" -Command "docker" -Arguments @(
        "version"
    )

    Invoke-CheckedCommand -Title "Start local PostgreSQL" -Command "docker" -Arguments @(
        "compose",
        "--env-file",
        ".env",
        "up",
        "-d",
        "postgres"
    )

    $maximumHealthChecks = 60
    $healthCheckIntervalSeconds = 2
    $postgresHealthy = $false

    Write-Host ""
    Write-Host "==> Wait for PostgreSQL health check"

    for ($attempt = 1; $attempt -le $maximumHealthChecks; $attempt++) {
        $containerStateOutput = Invoke-CheckedCommand `
            -Title "Inspect PostgreSQL container (attempt $attempt of $maximumHealthChecks)" `
            -Command "docker" `
            -Arguments @(
                "inspect",
                "--format",
                "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}",
                "enma-postgres"
            ) `
            -CaptureOutput

        $containerState = ($containerStateOutput | Select-Object -Last 1).ToString().Trim()
        $stateParts = $containerState.Split("|")
        if ($stateParts.Length -ne 2) {
            throw "Unable to determine the PostgreSQL container state."
        }

        $runtimeState = $stateParts[0]
        $healthState = $stateParts[1]
        Write-Host "PostgreSQL container state: $runtimeState; health: $healthState."

        if ($runtimeState -eq "running" -and $healthState -eq "healthy") {
            $postgresHealthy = $true
            break
        }

        if ($runtimeState -in @("dead", "exited", "removing") -or
            $healthState -eq "unhealthy") {
            throw "PostgreSQL failed to become healthy (state: $runtimeState; health: $healthState)."
        }

        Start-Sleep -Seconds $healthCheckIntervalSeconds
    }

    if (-not $postgresHealthy) {
        throw "PostgreSQL did not become healthy within the allowed timeout."
    }

    $connectionString = (
        "Host=localhost;Port={0};Database={1};Username={2};Password={3}" -f
            $postgresPort,
            $settings["POSTGRES_DB"],
            $settings["POSTGRES_USER"],
            $settings["POSTGRES_PASSWORD"]
    )

    Invoke-CheckedCommand -Title "Configure the API database User Secret" -Command "dotnet" -Arguments @(
        "user-secrets",
        "set",
        "ConnectionStrings:Database",
        $connectionString,
        "--project",
        ".\src\Enma.Api\Enma.Api.csproj"
    )

    Invoke-CheckedCommand -Title "Restore local .NET tools" -Command "dotnet" -Arguments @(
        "tool",
        "restore"
    )

    $previousDesignTimeConnectionString = [Environment]::GetEnvironmentVariable(
        "ENMA_DESIGNTIME_CONNECTION_STRING",
        [EnvironmentVariableTarget]::Process
    )

    try {
        $env:ENMA_DESIGNTIME_CONNECTION_STRING = $connectionString

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
    Write-Host "Local environment setup completed."
    Write-Host "Start the API with:"
    Write-Host 'dotnet run --project ".\src\Enma.Api\Enma.Api.csproj"'
    Write-Host "The API will use the database connection string stored in .NET User Secrets."
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
