[CmdletBinding()]
param([string]$PowerShellExe = "powershell.exe")

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$initializationScript = Join-Path $PSScriptRoot "Initialize-EnmaPilotDatabase.ps1"
$testRoot = Join-Path $env:TEMP (
    "Enma\pilot-database-initialization-tests\" + [Guid]::NewGuid().ToString("N"))
$configRoot = Join-Path $testRoot "config"
$dockerCommand = Join-Path $testRoot "fake-docker.ps1"
$dotnetCommand = Join-Path $testRoot "fake-dotnet.ps1"
$dotnetLog = Join-Path $testRoot "dotnet-invoked.txt"

function Invoke-Scenario {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Mode,
        [Parameter(Mandatory = $true)][bool]$ShouldSucceed,
        [Parameter(Mandatory = $true)][string]$ExpectedMessage
    )

    if (Test-Path -LiteralPath $dotnetLog) {
        [System.IO.File]::Delete($dotnetLog)
    }
    $previousMode = [System.Environment]::GetEnvironmentVariable(
        "ENMA_PILOT_TEST_SQL_MODE",
        "Process")
    $previousLog = [System.Environment]::GetEnvironmentVariable(
        "ENMA_PILOT_TEST_DOTNET_LOG",
        "Process")
    try {
        [System.Environment]::SetEnvironmentVariable(
            "ENMA_PILOT_TEST_SQL_MODE",
            $Mode,
            "Process")
        [System.Environment]::SetEnvironmentVariable(
            "ENMA_PILOT_TEST_DOTNET_LOG",
            $dotnetLog,
            "Process")
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = "Continue"
        try {
            $output = & $PowerShellExe -NoProfile -ExecutionPolicy Bypass `
                -File $initializationScript `
                -ConfigRoot $configRoot `
                -ConfirmPilotInitialization `
                -DockerCommand $dockerCommand `
                -DotnetCommand $dotnetCommand 2>&1 | Out-String
            $exitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousPreference
        }
    }
    finally {
        [System.Environment]::SetEnvironmentVariable(
            "ENMA_PILOT_TEST_SQL_MODE",
            $previousMode,
            "Process")
        [System.Environment]::SetEnvironmentVariable(
            "ENMA_PILOT_TEST_DOTNET_LOG",
            $previousLog,
            "Process")
    }

    if ($ShouldSucceed) {
        if ($exitCode -ne 0 -or
            $output -notmatch [regex]::Escape($ExpectedMessage) -or
            -not (Test-Path -LiteralPath $dotnetLog)) {
            throw "$Name did not complete the validated initialization path."
        }
    }
    elseif ($exitCode -eq 0 -or
        $output -notmatch [regex]::Escape($ExpectedMessage) -or
        (Test-Path -LiteralPath $dotnetLog) -or
        $output -match "PILOT DATABASE INITIALIZATION PASS") {
        throw "$Name did not fail closed before EF Core execution."
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
            "PILOT_POSTGRES_PORT=5543",
            "PILOT_MINIO_ROOT_USER=enma-pilot-root-test",
            "PILOT_MINIO_ROOT_PASSWORD=synthetic-minio-root-test-only",
            "PILOT_MINIO_APP_ACCESS_KEY=enma-pilot-app-test",
            "PILOT_MINIO_APP_SECRET_KEY=synthetic-minio-app-test-only",
            "PILOT_MINIO_API_PORT=9100",
            "PILOT_MINIO_CONSOLE_PORT=9101",
            "PILOT_MAILPIT_SMTP_PORT=1125",
            "PILOT_MAILPIT_UI_PORT=8125",
            "PILOT_API_HTTPS_PORT=7041",
            "PILOT_WEB_HTTPS_PORT=7042",
            "PILOT_HTTPS_PFX_PATH=C:\synthetic\pilot-test.pfx",
            "PILOT_HTTPS_PFX_PASSWORD=synthetic-pfx-test-only",
            "PILOT_DATA_PROTECTION_KEYS_PATH=C:\synthetic\pilot-keys"
        ))

    [System.IO.File]::WriteAllText($dockerCommand, @'
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$CommandArguments)

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
    Write-Output "true"
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

    [System.IO.File]::WriteAllText($dotnetCommand, @'
$log = [System.Environment]::GetEnvironmentVariable(
    "ENMA_PILOT_TEST_DOTNET_LOG",
    "Process")
if ([string]::IsNullOrWhiteSpace($log)) {
    $global:LASTEXITCODE = 20
    return
}
[System.IO.File]::WriteAllText($log, "invoked")
$global:LASTEXITCODE = 0
'@)

    Invoke-Scenario `
        -Name "valid SQL identity permits EF Core" `
        -Mode "valid" `
        -ShouldSucceed $true `
        -ExpectedMessage "PILOT DATABASE INITIALIZATION PASS"
    Invoke-Scenario `
        -Name "null SQL identity blocks EF Core" `
        -Mode "null" `
        -ShouldSucceed $false `
        -ExpectedMessage "Pilot PostgreSQL identity query did not return exactly one non-empty database name."
    Invoke-Scenario `
        -Name "empty SQL identity blocks EF Core" `
        -Mode "empty" `
        -ShouldSucceed $false `
        -ExpectedMessage "Pilot PostgreSQL identity query did not return exactly one non-empty database name."
    Invoke-Scenario `
        -Name "SQL execution error blocks EF Core" `
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
