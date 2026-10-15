[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigRoot,
    [int]$MailpitUiPort = 8125,
    [int]$WebHttpsPort = 5443,
    # Adds the identity settings an existing pilot.env lacks (the D33 database
    # roles, the D36 read-only MinIO backup identity), each group with fresh
    # random values. No other setting is changed, and no secret is printed.
    [switch]$AddMissingIdentities
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Pilot.Common.ps1")

function New-PilotSecret {
    param([int]$ByteCount = 32)

    $bytes = New-Object byte[] $ByteCount
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($bytes) }
    finally { $generator.Dispose() }
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}

# Identity settings added after the first Pilot configurations, by group: a
# group is added whole or not at all. Role names follow
# ^[a-z_][a-z0-9_]{0,62}$ (D34); passwords are URL-safe.
function New-PilotIdentityGroups {
    $roleSuffix = (New-PilotSecret -ByteCount 9).ToLowerInvariant().Replace('-', '_')
    return [ordered]@{
        "database roles" = @(
            "PILOT_POSTGRES_MIGRATOR_USER=enma_pilot_migrator_$roleSuffix",
            "PILOT_POSTGRES_MIGRATOR_PASSWORD=$(New-PilotSecret)",
            "PILOT_POSTGRES_APP_USER=enma_pilot_app_$roleSuffix",
            "PILOT_POSTGRES_APP_PASSWORD=$(New-PilotSecret)"
        )
        "storage backup identity" = @(
            "PILOT_MINIO_BACKUP_ACCESS_KEY=enma-pilot-backup-$((New-PilotSecret -ByteCount 9).ToLowerInvariant())",
            "PILOT_MINIO_BACKUP_SECRET_KEY=$(New-PilotSecret)"
        )
    }
}

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$configRootFullPath = [System.IO.Path]::GetFullPath($ConfigRoot)
Assert-PathOutsideRepository -Path $configRootFullPath -RepositoryRoot $repositoryRoot
$environmentPath = Join-Path $configRootFullPath "pilot.env"
$utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)

if ($AddMissingIdentities) {
    if (-not (Test-Path -LiteralPath $environmentPath -PathType Leaf)) {
        throw "Pilot configuration does not exist; create it without -AddMissingIdentities."
    }
    $original = [System.IO.File]::ReadAllText($environmentPath)
    $existingKeys = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($original -split "`r?`n")) {
        if ($line -match '^\s*([A-Z0-9_]+)\s*=') {
            $existingKeys.Add($Matches[1])
        }
    }

    $addedLines = New-Object System.Collections.Generic.List[string]
    $groups = New-PilotIdentityGroups
    foreach ($group in $groups.Keys) {
        $keys = @($groups[$group] | ForEach-Object { $_.Substring(0, $_.IndexOf('=')) })
        $present = @($keys | Where-Object { $existingKeys.Contains($_) })
        if ($present.Count -eq $keys.Count) {
            continue
        }
        if ($present.Count -gt 0) {
            throw "Pilot configuration has only some $group settings; refusing to change it."
        }
        foreach ($line in $groups[$group]) { $addedLines.Add($line) }
        Write-Host "Adding the Pilot $group settings."
    }
    if ($addedLines.Count -eq 0) {
        Write-Host "PILOT IDENTITY SETTINGS ALREADY PRESENT"
        return
    }

    Protect-PilotDirectory -Path $configRootFullPath
    $separator = if ($original.Length -eq 0 -or $original.EndsWith("`n")) { "" } else { "`r`n" }
    [System.IO.File]::AppendAllText(
        $environmentPath,
        $separator + ($addedLines -join "`r`n") + "`r`n",
        $utf8WithoutBom)
    try {
        Assert-PilotIdentitySettings -Settings (Read-PilotEnvironment -Path $environmentPath)
    }
    catch {
        [System.IO.File]::WriteAllText($environmentPath, $original, $utf8WithoutBom)
        throw
    }
    Protect-PilotDirectory -Path $configRootFullPath

    Write-Host "PILOT IDENTITY SETTINGS ADDED"
    Write-Host "Secrets were written only to the restricted external configuration root."
    return
}

$ports = @($MailpitUiPort, $WebHttpsPort)
if (@($ports | Where-Object { $_ -lt 1 -or $_ -gt 65535 }).Count -gt 0 -or
    @($ports | Select-Object -Unique).Count -ne $ports.Count -or
    @($ports | Where-Object { @(5014, 7028, 5173, 1025, 8025) -contains $_ }).Count -gt 0) {
    throw "Pilot ports must be valid, unique, and distinct from Development."
}

if (-not (Test-Path -LiteralPath $configRootFullPath -PathType Container)) {
    New-Item -ItemType Directory -Path $configRootFullPath | Out-Null
}
if (Test-Path -LiteralPath $environmentPath) {
    throw "Pilot configuration already exists; it will not be overwritten."
}

Protect-PilotDirectory -Path $configRootFullPath

$suffix = (New-PilotSecret -ByteCount 9).ToLowerInvariant()
$lines = @(
    "PILOT_POSTGRES_DB=enma_pilot",
    "PILOT_POSTGRES_USER=enma_pilot_$suffix",
    "PILOT_POSTGRES_PASSWORD=$(New-PilotSecret)",
    "PILOT_MINIO_ROOT_USER=enma-pilot-root-$suffix",
    "PILOT_MINIO_ROOT_PASSWORD=$(New-PilotSecret)",
    "PILOT_MINIO_APP_ACCESS_KEY=enma-pilot-app-$suffix",
    "PILOT_MINIO_APP_SECRET_KEY=$(New-PilotSecret)",
    "PILOT_MAILPIT_UI_PORT=$MailpitUiPort",
    "PILOT_WEB_HTTPS_PORT=$WebHttpsPort"
)
$groups = New-PilotIdentityGroups
foreach ($group in $groups.Keys) {
    $lines += $groups[$group]
}
[System.IO.File]::WriteAllLines($environmentPath, $lines, $utf8WithoutBom)
Protect-PilotDirectory -Path $configRootFullPath

Write-Host "PILOT CONFIGURATION CREATED"
Write-Host "Secrets were written only to the restricted external configuration root."
Write-Host "No Docker resources, databases, migrations, or application processes were started."
