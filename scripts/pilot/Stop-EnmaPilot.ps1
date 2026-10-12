[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ConfigRoot,
    # Stops only the API and Caddy, leaving PostgreSQL, MinIO and Mailpit
    # running (the quiesced state that Backup-EnmaPilot.ps1 requires).
    [switch]$ApplicationOnly,
    [string]$DockerCommand = "docker"
)

# Stops containers of the enma-pilot Compose project only. Containers are
# stopped, never removed, and no volume is touched.

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot "Pilot.Common.ps1")

$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$configRootFullPath = [System.IO.Path]::GetFullPath($ConfigRoot)
Assert-PathOutsideRepository -Path $configRootFullPath -RepositoryRoot $repositoryRoot
$settings = Read-PilotEnvironment -Path (Join-Path $configRootFullPath "pilot.env")
Assert-PilotIdentitySettings -Settings $settings

$docker = Assert-CommandAvailable -Command $DockerCommand
foreach ($name in $script:PilotContainers) {
    Assert-PilotDockerResourceOwnership -Kind container -Name $name -DockerCommand $docker
}

$services = if ($ApplicationOnly) { @("caddy", "api") } else { @() }
$composeArguments = Get-PilotComposeArguments -RepositoryRoot $repositoryRoot
Invoke-WithPilotComposeEnvironment `
    -Environment (Get-PilotComposeEnvironment -Settings $settings) `
    -ScriptBlock {
        Invoke-PilotCommand -Command $docker -Arguments (
            $composeArguments + @("stop") + $services)
    }

if ($ApplicationOnly) {
    Write-Host "Pilot API and Caddy stopped; data services keep running."
}
else {
    Write-Host "All enma-pilot containers stopped; volumes were not touched."
}
