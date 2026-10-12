[CmdletBinding()]
param(
    # Last migration already applied in the target database ("0" = empty database).
    [string]$FromMigration = "0",
    # Migration to stop at; defaults to the latest one in this checkout.
    [string]$ToMigration = "",
    [string]$DotnetCommand = "dotnet"
)

# D14: writes the NON-idempotent SQL of the selected migration range to
# artifacts/migrations/ (gitignored) for human review only. Deployments apply
# migrations with the migrator image, never with this script. No database
# connection is opened.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

foreach ($migration in @($FromMigration, $ToMigration)) {
    if ($migration -notmatch '^[A-Za-z0-9_]*$') {
        throw "Migration identifiers may contain only letters, digits, and underscores."
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$outputDirectory = Join-Path $repositoryRoot "artifacts\migrations"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$target = if ([string]::IsNullOrEmpty($ToMigration)) { "latest" } else { $ToMigration }
$outputPath = Join-Path $outputDirectory "enma-migrations-$FromMigration-to-$target.sql"
$project = Join-Path $repositoryRoot "src\Enma.Infrastructure\Enma.Infrastructure.csproj"

$arguments = @(
    "tool", "run", "dotnet-ef", "migrations", "script", $FromMigration)
if (-not [string]::IsNullOrEmpty($ToMigration)) {
    $arguments += $ToMigration
}
$arguments += @(
    "--project", $project,
    "--startup-project", $project,
    "--context", "EnmaDbContext",
    "--output", $outputPath)

Push-Location $repositoryRoot
try {
    & $DotnetCommand tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed." }
    & $DotnetCommand @arguments
    if ($LASTEXITCODE -ne 0) { throw "Migration SQL generation failed." }
}
finally {
    Pop-Location
}

Write-Host "Review-only SQL written to $outputPath"
Write-Host "It is not idempotent and must not be applied by hand; use the migrator image."
