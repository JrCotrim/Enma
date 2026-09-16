Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-CommandAvailable {
    param([Parameter(Mandatory = $true)][string]$Command)

    if ($null -eq (Get-Command -Name $Command -ErrorAction SilentlyContinue)) {
        throw "Required command '$Command' is not available on PATH."
    }
}

function Invoke-NativeCapture {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [string[]]$Arguments = @(),
        [string]$FailureMessage = "Native command failed."
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try {
        $output = & $Command @Arguments 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($exitCode -ne 0) {
        throw "$FailureMessage Exit code: $exitCode."
    }

    return @($output | ForEach-Object { $_.ToString() })
}

function Assert-SafeDockerName {
    param([Parameter(Mandatory = $true)][string]$Name)

    if ($Name -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$') {
        throw "Docker resource name is invalid."
    }
}

function Get-NormalizedFullPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $fullPath = Get-NormalizedFullPath -Path $Path
    $fullParent = Get-NormalizedFullPath -Path $Parent
    $prefix = $fullParent + [System.IO.Path]::DirectorySeparatorChar

    return $fullPath.Equals(
            $fullParent,
            [System.StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.StartsWith(
            $prefix,
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Assert-OutsideRepository {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )

    if (Test-PathWithin -Path $Path -Parent $RepositoryRoot) {
        throw "Backup and drill data must be outside the repository."
    }

    $currentPath = Get-NormalizedFullPath -Path $Path
    while (-not [string]::IsNullOrWhiteSpace($currentPath)) {
        if (Test-Path -LiteralPath $currentPath) {
            $item = Get-Item -LiteralPath $currentPath -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Backup and drill paths must not traverse links or reparse points."
            }
        }

        $parent = [System.IO.Directory]::GetParent($currentPath)
        if ($null -eq $parent -or $parent.FullName -ceq $currentPath) {
            break
        }
        $currentPath = $parent.FullName
    }
}

function Get-ContainedChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$RelativePath
    )

    if ([System.IO.Path]::IsPathRooted($RelativePath)) {
        throw "Inventory path must be relative."
    }

    $candidate = Get-NormalizedFullPath -Path (Join-Path $Parent $RelativePath)
    if (-not (Test-PathWithin -Path $candidate -Parent $Parent) -or
        $candidate.Equals(
            (Get-NormalizedFullPath -Path $Parent),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Inventory path escapes its expected directory."
    }

    return $candidate
}

function Get-Sha256Hex {
    param([Parameter(Mandatory = $true)][string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Write-JsonFile {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Depth = 12
    )

    $json = $Value | ConvertTo-Json -Depth $Depth
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $json, $utf8WithoutBom)
}

function Protect-SensitiveDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Sensitive directory does not exist."
    }
    if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
        throw "This PowerShell 5.1-first script requires Windows ACL support."
    }

    $inheritance = [System.Security.AccessControl.InheritanceFlags]::ContainerInherit -bor
        [System.Security.AccessControl.InheritanceFlags]::ObjectInherit
    $propagation = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    $fullControl = [System.Security.AccessControl.FileSystemRights]::FullControl
    $security = New-Object System.Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)

    $identities = @(
        [System.Security.Principal.WindowsIdentity]::GetCurrent().User,
        (New-Object System.Security.Principal.SecurityIdentifier("S-1-5-18")),
        (New-Object System.Security.Principal.SecurityIdentifier("S-1-5-32-544"))
    )
    foreach ($identity in $identities) {
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            $identity,
            $fullControl,
            $inheritance,
            $propagation,
            $allow)
        $security.AddAccessRule($rule)
    }

    Set-Acl -LiteralPath $Path -AclObject $security
}

function Get-StringSha256 {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyString()]
        [string]$Value
    )

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Value)
        return ([System.BitConverter]::ToString(
            $sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-PostgresQuery {
    param(
        [Parameter(Mandatory = $true)][string]$DockerCommand,
        [Parameter(Mandatory = $true)][string]$ContainerName,
        [Parameter(Mandatory = $true)][string]$Query
    )

    $queryBytes = [System.Text.Encoding]::UTF8.GetBytes($Query)
    $encodedQuery = [Convert]::ToBase64String($queryBytes)
    $output = Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "exec", $ContainerName,
        "sh", "-c",
        'printf %s "$1" | base64 -d | psql -X -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At',
        "enma-query", $encodedQuery
    ) -FailureMessage "PostgreSQL read failed."

    return ($output -join "`n").Trim()
}

function Get-DatabaseCounts {
    param(
        [Parameter(Mandatory = $true)][string]$DockerCommand,
        [Parameter(Mandatory = $true)][string]$ContainerName
    )

    $query = @"
SELECT json_build_object(
  'organizations', (SELECT count(*) FROM organizations),
  'users', (SELECT count(*) FROM users),
  'organization_memberships', (SELECT count(*) FROM organization_memberships),
  'clients', (SELECT count(*) FROM clients),
  'legal_processes', (SELECT count(*) FROM legal_processes),
  'legal_deadlines', (SELECT count(*) FROM legal_deadlines),
  'legal_tasks', (SELECT count(*) FROM legal_tasks),
  'legal_documents', (SELECT count(*) FROM legal_documents),
  'client_payment_plans', (SELECT count(*) FROM client_payment_plans),
  'payment_installments', (SELECT count(*) FROM payment_installments),
  'audit_logs', (SELECT count(*) FROM audit_logs),
  'notifications', (SELECT count(*) FROM notifications));
"@

    return (Get-PostgresQuery `
        -DockerCommand $DockerCommand `
        -ContainerName $ContainerName `
        -Query $query) | ConvertFrom-Json
}

function Get-MinioListing {
    param(
        [Parameter(Mandatory = $true)][string]$DockerCommand,
        [Parameter(Mandatory = $true)][string]$ContainerName,
        [Parameter(Mandatory = $true)][string]$BucketName
    )

    Assert-SafeDockerName -Name $BucketName
    $lines = Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "exec", $ContainerName,
        "sh", "-c",
        'export MC_HOST_enmasource="http://$MINIO_ROOT_USER:$MINIO_ROOT_PASSWORD@127.0.0.1:9000"; mc ls --recursive --json "enmasource/$1"',
        "enma-minio-list", $BucketName
    ) -FailureMessage "MinIO bucket listing failed."

    $items = @()
    foreach ($line in $lines) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $item = $line | ConvertFrom-Json
        if ($item.status -eq "error") {
            throw "MinIO bucket listing failed."
        }

        if ($item.type -eq "file") {
            $items += [pscustomobject]@{
                key = [string]$item.key
                sizeBytes = [int64]$item.size
            }
        }
    }

    return @($items | Sort-Object -Property key)
}

function Get-ListingSummary {
    param([Parameter(Mandatory = $true)][object[]]$Items)

    $canonical = @($Items | Sort-Object -Property key | ForEach-Object {
        [pscustomobject]@{
            key = [string]$_.key
            sizeBytes = [int64]$_.sizeBytes
        }
    })
    $json = $canonical | ConvertTo-Json -Depth 4 -Compress
    $totalBytes = 0L
    foreach ($item in $canonical) {
        $totalBytes += [int64]$item.sizeBytes
    }

    return [pscustomobject]@{
        objectCount = $canonical.Count
        totalBytes = $totalBytes
        inventorySha256 = Get-StringSha256 -Value $json
    }
}

function Get-ObjectInventory {
    param([Parameter(Mandatory = $true)][string]$ObjectsDirectory)

    if (-not (Test-Path -LiteralPath $ObjectsDirectory -PathType Container)) {
        throw "Object backup directory is missing."
    }

    $root = Get-NormalizedFullPath -Path $ObjectsDirectory
    $rootItem = Get-Item -LiteralPath $root -Force
    if (($rootItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Object backup root must not be a link or reparse point."
    }
    foreach ($entry in Get-ChildItem -LiteralPath $root -Force -Recurse) {
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Object backup must not contain links or reparse points."
        }
    }
    $items = @()
    foreach ($file in Get-ChildItem -LiteralPath $root -File -Recurse) {
        if (-not (Test-PathWithin -Path $file.FullName -Parent $root)) {
            throw "Object backup contains a path outside its root."
        }

        $relativePath = $file.FullName.Substring($root.Length).TrimStart(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
        $key = $relativePath.Replace(
            [System.IO.Path]::DirectorySeparatorChar,
            [char]'/' )

        $items += [pscustomobject]@{
            key = $key
            sizeBytes = [int64]$file.Length
            sha256 = Get-Sha256Hex -Path $file.FullName
        }
    }

    return @($items | Sort-Object -Property key)
}

function Get-ContainerId {
    param(
        [Parameter(Mandatory = $true)][string]$DockerCommand,
        [Parameter(Mandatory = $true)][string]$ContainerName
    )

    $value = Invoke-NativeCapture -Command $DockerCommand -Arguments @(
        "inspect", "--format", "{{.Id}}", $ContainerName
    ) -FailureMessage "Required source container is unavailable."
    return ($value -join "").Trim()
}

function Get-SourceEvidence {
    param(
        [Parameter(Mandatory = $true)][string]$DockerCommand,
        [Parameter(Mandatory = $true)][string]$PostgresContainer,
        [Parameter(Mandatory = $true)][string]$MinioContainer,
        [Parameter(Mandatory = $true)][string]$BucketName
    )

    $postgresId = Get-ContainerId -DockerCommand $DockerCommand -ContainerName $PostgresContainer
    $minioId = Get-ContainerId -DockerCommand $DockerCommand -ContainerName $MinioContainer
    $latestMigration = Get-PostgresQuery `
        -DockerCommand $DockerCommand `
        -ContainerName $PostgresContainer `
        -Query 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;'
    $counts = Get-DatabaseCounts -DockerCommand $DockerCommand -ContainerName $PostgresContainer
    $listing = Get-MinioListing `
        -DockerCommand $DockerCommand `
        -ContainerName $MinioContainer `
        -BucketName $BucketName
    $summary = Get-ListingSummary -Items $listing

    return [pscustomobject]@{
        postgresContainerId = $postgresId
        minioContainerId = $minioId
        latestMigration = $latestMigration
        databaseCounts = $counts
        objectCount = $summary.objectCount
        objectTotalBytes = $summary.totalBytes
        objectListingSha256 = $summary.inventorySha256
    }
}

function Test-EquivalentJson {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    return (($Left | ConvertTo-Json -Depth 12 -Compress) -ceq
        ($Right | ConvertTo-Json -Depth 12 -Compress))
}

function Assert-EnmaApiStopped {
    $apiProcesses = @(Get-CimInstance Win32_Process -ErrorAction Stop |
        Where-Object {
            $_.Name -in @("dotnet.exe", "Enma.Api.exe") -and
            $_.CommandLine -match 'Enma[\\/]Api|Enma\.Api'
        })

    if ($apiProcesses.Count -gt 0) {
        $processIds = ($apiProcesses | Select-Object -ExpandProperty ProcessId) -join ", "
        throw "Enma.Api is running (PID(s): $processIds). Stop only those API processes before backup."
    }
}

function New-RandomSecret {
    param([int]$ByteCount = 32)

    $bytes = New-Object byte[] $ByteCount
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $rng.GetBytes($bytes)
    }
    finally {
        $rng.Dispose()
    }

    return ([Convert]::ToBase64String($bytes)).Replace("/", "_").Replace("+", "-")
}
