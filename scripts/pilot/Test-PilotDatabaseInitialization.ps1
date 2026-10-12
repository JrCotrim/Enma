[CmdletBinding()]
param([string]$PowerShellExe = "powershell.exe")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$initializationScript = Join-Path $PSScriptRoot "Initialize-EnmaPilotDatabase.ps1"
$testRoot = Join-Path $env:TEMP (
    "Enma\pilot-database-initialization-tests\" + [Guid]::NewGuid().ToString("N"))
$configRoot = Join-Path $testRoot "config"
$dockerCommand = Join-Path $testRoot "fake-docker.ps1"
$migratorLog = Join-Path $testRoot "migrator-invoked.txt"

function Invoke-Scenario {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][bool]$ShouldSucceed,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage,
        [bool]$ApiRunning = $false
    )

    if (Test-Path -LiteralPath $migratorLog) {
        [System.IO.File]::Delete($migratorLog)
    }
    $variables = @{
        ENMA_PILOT_TEST_SQL_MODE = $Mode
        ENMA_PILOT_TEST_MIGRATOR_LOG = $migratorLog
        ENMA_PILOT_TEST_API_RUNNING = $ApiRunning.ToString().ToLowerInvariant()
    }
    $previous = @{}
    try {
        foreach ($key in $variables.Keys) {
            $previous[$key] = [System.Environment]::GetEnvironmentVariable($key, "Process")
            [System.Environment]::SetEnvironmentVariable($key, $variables[$key], "Process")
        }
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = & $PowerShellExe -NoProfile -ExecutionPolicy Bypass `
                -File $initializationScript `
                -ConfigRoot $configRoot `
                -ConfirmPilotInitialization `
                -DockerCommand $dockerCommand 2>&1 | Out-String
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousPreference
        }
    }
    finally {
        foreach ($key in $previous.Keys) {
            [System.Environment]::SetEnvironmentVariable($key, $previous[$key], "Process")
        }
    }

    if ($ShouldSucceed) {
        if ($exitCode -ne 0 -or
            $output -notmatch [regex]::Escape($ExpectedMessage) -or
            -not (Test-Path -LiteralPath $migratorLog)) {
            throw "$Name did not complete the validated initialization path."
        }
    }
    elseif ($exitCode -eq 0 -or
        $output -notmatch [regex]::Escape($ExpectedMessage) -or
        (Test-Path -LiteralPath $migratorLog) -or
        $output -match "PILOT DATABASE INITIALIZATION PASS") {
        throw "$Name did not fail closed before the migrator ran."
    }
    Write-Host "PASS: $Name"
}

New-Item -ItemType Directory -Path $configRoot -Force | Out-Null
try {
    [System.IO.File]::WriteAllLines(
        (Join-Path $configRoot "pilot.env"),
        @(
            "PILOT_POSTGRES_DB=enma_pilot_test",
            "PILOT_POSTGRES_USER=enma_pilot_test",
            "PILOT_POSTGRES_PASSWORD=synthetic-postgres-test-only",
            "PILOT_MINIO_ROOT_USER=enma-pilot-root-test",
            "PILOT_MINIO_ROOT_PASSWORD=synthetic-minio-root-test-only",
            "PILOT_MINIO_APP_ACCESS_KEY=enma-pilot-app-test",
            "PILOT_MINIO_APP_SECRET_KEY=synthetic-minio-app-test-only",
            "PILOT_MAILPIT_UI_PORT=8125",
            "PILOT_WEB_HTTPS_PORT=7042"
        ))

    # A plain script (no param block): native-style flags such as -i or -d
    # must reach it as arguments, not bind to PowerShell common parameters.
    [System.IO.File]::WriteAllText($dockerCommand, @'
$CommandArguments = [string[]]$args

$global:LASTEXITCODE = 0
$joined = $CommandArguments -join " "
if ($joined -match "json \.Config\.Env") {
    Write-Output (@(
        "POSTGRES_DB=enma_pilot_test",
        "POSTGRES_USER=enma_pilot_test",
        "POSTGRES_PASSWORD=synthetic-postgres-test-only"
    ) | ConvertTo-Json -Compress)
    return
}
if ($joined -match "com\.docker\.compose\.project") {
    Write-Output "enma-pilot"
    return
}
if ($joined -match "\.State\.Running") {
    if ($joined -match "enma-pilot-api") {
        Write-Output $env:ENMA_PILOT_TEST_API_RUNNING
    }
    else {
        Write-Output "true"
    }
    return
}
if ($CommandArguments.Count -ge 1 -and $CommandArguments[0] -ceq "compose") {
    if ($joined -match "--profile migrate run --rm --no-deps migrate$" -and
        $env:ENMA_POSTGRES_PASSWORD -ceq "synthetic-postgres-test-only") {
        [System.IO.File]::WriteAllText($env:ENMA_PILOT_TEST_MIGRATOR_LOG, "invoked")
        return
    }
    $global:LASTEXITCODE = 21
    return
}
if ($CommandArguments.Count -ge 3 -and
    $CommandArguments[0] -ceq "exec" -and
    $CommandArguments[1] -ceq "-i" -and
    $CommandArguments[2] -ceq "enma-pilot-postgres") {
    Write-Output "20260101000000_SyntheticMigration"
    return
}
if ($CommandArguments.Count -ge 3 -and
    $CommandArguments[0] -ceq "exec" -and
    $CommandArguments[1] -ceq "enma-pilot-postgres") {
    if ($CommandArguments[2] -cne "psql" -or $CommandArguments -contains "sh") {
        $global:LASTEXITCODE = 18
        return
    }
    switch ($env:ENMA_PILOT_TEST_SQL_MODE) {
        "valid" { Write-Output "enma_pilot_test" }
        "null" { return }
        "empty" { Write-Output "" }
        "error" {
            Write-Output "synthetic query failure"
            $global:LASTEXITCODE = 17
        }
        default { $global:LASTEXITCODE = 19 }
    }
}
'@)

    Invoke-Scenario `
        -Name "valid SQL identity runs the migrator" `
        -Mode "valid" `
        -ShouldSucceed $true `
        -ExpectedMessage "PILOT DATABASE INITIALIZATION PASS"
    Invoke-Scenario `
        -Name "running Pilot API blocks the migrator" `
        -Mode "valid" `
        -ShouldSucceed $false `
        -ExpectedMessage "The Pilot API is running. Stop it before applying migrations." `
        -ApiRunning $true
    Invoke-Scenario `
        -Name "null SQL identity blocks the migrator" `
        -Mode "null" `
        -ShouldSucceed $false `
        -ExpectedMessage "Pilot PostgreSQL identity query did not return exactly one non-empty database name."
    Invoke-Scenario `
        -Name "empty SQL identity blocks the migrator" `
        -Mode "empty" `
        -ShouldSucceed $false `
        -ExpectedMessage "Pilot PostgreSQL identity query did not return exactly one non-empty database name."
    Invoke-Scenario `
        -Name "SQL execution error blocks the migrator" `
        -Mode "error" `
        -ShouldSucceed $false `
        -ExpectedMessage "Pilot PostgreSQL identity query failed."

    Write-Host "PILOT DATABASE INITIALIZATION TESTS PASS"
}
finally {
    $testParent = Join-Path $env:TEMP "Enma\pilot-database-initialization-tests"
    if ((Test-Path -LiteralPath $testRoot) -and
        [System.IO.Path]::GetFullPath($testRoot).StartsWith(
            [System.IO.Path]::GetFullPath($testParent) +
                [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        [System.IO.Directory]::Delete($testRoot, $true)
    }
}
