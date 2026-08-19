[CmdletBinding()]
param(
    [switch]$SecurityAudit,
    [switch]$SkipRestore,
    [switch]$IntegrationTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Title,

        [Parameter(Mandatory = $true)]
        [string]$Command,

        [string[]]$Arguments = @()
    )

    Write-Host ""
    Write-Host "==> $Title"
    Write-Host ("    {0} {1}" -f $Command, ($Arguments -join " "))

    & $Command @Arguments
    $commandExitCode = $LASTEXITCODE

    if ($commandExitCode -ne 0) {
        $exception = New-Object System.Exception(
            "Command '$Command' failed with exit code $commandExitCode."
        )
        $exception.Data["ExitCode"] = $commandExitCode
        throw $exception
    }
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
        $containerState = & docker inspect `
            --format `
            "{{.State.Status}}|{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}" `
            $ContainerName

        if ($LASTEXITCODE -ne 0) {
            throw "Unable to inspect $DisplayName container."
        }

        $stateParts = $containerState.ToString().Trim().Split("|")
        if ($stateParts.Length -ne 2) {
            throw "Unable to determine the $DisplayName container state."
        }

        $runtimeState = $stateParts[0]
        $healthState = $stateParts[1]

        if ($runtimeState -eq "running" -and $healthState -eq "healthy") {
            Write-Host "$DisplayName is healthy."
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
        $containerState = & docker inspect `
            --format `
            "{{.State.Status}}|{{.State.ExitCode}}" `
            $ContainerName

        if ($LASTEXITCODE -ne 0) {
            throw "Unable to inspect $DisplayName container."
        }

        $stateParts = $containerState.ToString().Trim().Split("|")
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
$solutionPath = Join-Path $repositoryRoot "Enma.slnx"
$unitTestProjectPath = Join-Path $repositoryRoot "tests\Enma.UnitTests\Enma.UnitTests.csproj"
$integrationTestProjectPath = Join-Path $repositoryRoot "tests\Enma.IntegrationTests\Enma.IntegrationTests.csproj"
$locationChanged = $false
$scriptExitCode = 0

try {
    Write-Host "Repository root: $repositoryRoot"

    if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
        throw "Solution file was not found: $solutionPath"
    }

    if (-not (Test-Path -LiteralPath $unitTestProjectPath -PathType Leaf)) {
        throw "Unit test project was not found: $unitTestProjectPath"
    }

    if ($IntegrationTests -and
        -not (Test-Path -LiteralPath $integrationTestProjectPath -PathType Leaf)) {
        throw "Integration test project was not found: $integrationTestProjectPath"
    }

    Push-Location -LiteralPath $repositoryRoot
    $locationChanged = $true

    Invoke-CheckedCommand -Title "Initial Git status" -Command "git" -Arguments @(
        "status",
        "--short"
    )

    Invoke-CheckedCommand -Title "Git whitespace validation" -Command "git" -Arguments @(
        "diff",
        "--check"
    )

    if ($SkipRestore) {
        Write-Host ""
        Write-Host "==> Restore skipped by -SkipRestore"
    }
    else {
        Invoke-CheckedCommand -Title "Restore" -Command "dotnet" -Arguments @(
            "restore",
            ".\Enma.slnx"
        )
    }

    Invoke-CheckedCommand -Title "Build" -Command "dotnet" -Arguments @(
        "build",
        ".\Enma.slnx",
        "--no-restore"
    )

    Invoke-CheckedCommand -Title "Unit tests" -Command "dotnet" -Arguments @(
        "test",
        ".\tests\Enma.UnitTests\Enma.UnitTests.csproj",
        "--no-build"
    )

    if ($IntegrationTests) {
        $localEnvironmentPath = Join-Path $repositoryRoot ".env"
        $exampleEnvironmentPath = Join-Path $repositoryRoot ".env.example"
        $composeEnvironmentPath = if (Test-Path -LiteralPath $localEnvironmentPath -PathType Leaf) {
            $localEnvironmentPath
        }
        elseif (Test-Path -LiteralPath $exampleEnvironmentPath -PathType Leaf) {
            $exampleEnvironmentPath
        }
        else {
            throw "Integration tests require .env or .env.example for private MinIO."
        }

        Invoke-CheckedCommand -Title "Confirm Docker availability" -Command "docker" -Arguments @(
            "version"
        )

        Invoke-CheckedCommand -Title "Start private MinIO integration dependency" -Command "docker" -Arguments @(
            "compose",
            "--env-file",
            $composeEnvironmentPath,
            "up",
            "-d",
            "minio",
            "minio-bootstrap"
        )

        Wait-ForHealthyContainer `
            -ContainerName "enma-minio" `
            -DisplayName "MinIO"

        Wait-ForSuccessfulOneShotContainer `
            -ContainerName "enma-minio-bootstrap" `
            -DisplayName "MinIO bootstrap"

        Invoke-CheckedCommand -Title "Integration tests" -Command "dotnet" -Arguments @(
            "test",
            ".\tests\Enma.IntegrationTests\Enma.IntegrationTests.csproj",
            "--no-build"
        )
    }

    if ($SecurityAudit) {
        Invoke-CheckedCommand -Title "Package security audit" -Command "dotnet" -Arguments @(
            "list",
            ".\Enma.slnx",
            "package",
            "--vulnerable",
            "--include-transitive"
        )
    }

    Invoke-CheckedCommand -Title "Untracked files" -Command "git" -Arguments @(
        "ls-files",
        "--others",
        "--exclude-standard"
    )

    Invoke-CheckedCommand -Title "Git diff statistics" -Command "git" -Arguments @(
        "diff",
        "--stat"
    )

    Invoke-CheckedCommand -Title "Final Git status" -Command "git" -Arguments @(
        "status",
        "--short"
    )

    Write-Host ""
    Write-Host "Verification completed successfully."
}
catch {
    if ($_.Exception.Data.Contains("ExitCode")) {
        $scriptExitCode = [int]$_.Exception.Data["ExitCode"]
    }
    else {
        $scriptExitCode = 1
    }

    [Console]::Error.WriteLine("Verification failed: {0}", $_.Exception.Message)
}
finally {
    if ($locationChanged) {
        Pop-Location
    }
}

exit $scriptExitCode
