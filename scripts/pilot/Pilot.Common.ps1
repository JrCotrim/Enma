Set-StrictMode -Version Latest

$script:PilotRequiredKeys = @(
    "PILOT_POSTGRES_DB",
    "PILOT_POSTGRES_USER",
    "PILOT_POSTGRES_PASSWORD",
    "PILOT_POSTGRES_PORT",
    "PILOT_MINIO_ROOT_USER",
    "PILOT_MINIO_ROOT_PASSWORD",
    "PILOT_MINIO_APP_ACCESS_KEY",
    "PILOT_MINIO_APP_SECRET_KEY",
    "PILOT_MINIO_API_PORT",
    "PILOT_MINIO_CONSOLE_PORT",
    "PILOT_MAILPIT_SMTP_PORT",
    "PILOT_MAILPIT_UI_PORT",
    "PILOT_API_HTTPS_PORT",
    "PILOT_WEB_HTTPS_PORT",
    "PILOT_HTTPS_PFX_PATH",
    "PILOT_HTTPS_PFX_PASSWORD",
    "PILOT_DATA_PROTECTION_KEYS_PATH"
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
        "PILOT_MINIO_ROOT_USER", "PILOT_MINIO_APP_ACCESS_KEY")) {
        if ($Settings[$key] -notmatch 'pilot') {
            throw "Pilot identity setting '$key' must contain the marker 'pilot'."
        }
    }
    $secrets = @(
        $Settings.PILOT_POSTGRES_PASSWORD,
        $Settings.PILOT_MINIO_ROOT_PASSWORD,
        $Settings.PILOT_MINIO_APP_SECRET_KEY,
        $Settings.PILOT_HTTPS_PFX_PASSWORD)
    if (@($secrets | Select-Object -Unique).Count -ne $secrets.Count) {
        throw "Pilot credentials must be distinct from one another."
    }
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
    Set-Acl -LiteralPath $Path -AclObject $security
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
