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
