param(
    [Parameter(Mandatory = $true)]
    [System.Net.IPAddress] $BindAddress,

    [ValidatePattern('^[a-zA-Z0-9_-]{1,64}$')]
    [string] $NodeId = 'local',

    [ValidateSet('GuardedObserve', 'DesktopObserve', 'AuthoringVm')]
    [string] $Profile = 'GuardedObserve'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

enum NodeSetupProfile {
    GuardedObserve
    DesktopObserve
    AuthoringVm
}

function Get-NodeAccessArguments {
    param([NodeSetupProfile] $SelectedProfile)

    switch ($SelectedProfile) {
        ([NodeSetupProfile]::DesktopObserve) {
            return @{ Scopes = 'observe'; UiGuard = 'desktop' }
        }
        ([NodeSetupProfile]::AuthoringVm) {
            return @{ Scopes = 'observe,ui,macro.write'; UiGuard = 'desktop' }
        }
        default {
            return @{ Scopes = 'observe'; UiGuard = 'guarded' }
        }
    }
}

if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitOperatingSystem) {
    throw 'This package requires 64-bit Windows. Use the correct architecture package.'
}
if ($BindAddress.Equals([System.Net.IPAddress]::Any) -or
    $BindAddress.Equals([System.Net.IPAddress]::IPv6Any)) {
    throw 'Bind to the actual reachable node IP, not a wildcard certificate address.'
}

$sourceExecutable = Join-Path $PSScriptRoot 'k-lani-bot-node.exe'
if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
    throw 'The verified k-lani-bot-node.exe must be beside this setup script.'
}

$installationDirectory = Join-Path $env:LOCALAPPDATA 'Programs\k-lani-bot-node'
$stateDirectory = Join-Path $env:LOCALAPPDATA "k-lani-bot\nodes\$NodeId"
$workspaceDirectory = Join-Path $env:USERPROFILE "k-lani-bot-work\$NodeId"
$installedExecutable = Join-Path $installationDirectory 'k-lani-bot-node.exe'
$configurationFile = Join-Path $stateDirectory 'node.json'

# Existing private state and paired credentials must survive a repeated setup.
if (Test-Path -LiteralPath $stateDirectory) {
    throw 'The node state already exists. Existing installation was preserved.'
}
if (Test-Path -LiteralPath $installedExecutable) {
    throw 'The node executable already exists. Review an update separately.'
}

Write-Host 'ALPHA: authorized automation can change or delete data. AI decisions can be wrong.'
Write-Host 'This setup runs as the logged-in user; it creates no service or firewall rule.'
$access = Get-NodeAccessArguments -SelectedProfile ([NodeSetupProfile] $Profile)
New-Item -ItemType Directory -Path $installationDirectory -Force | Out-Null
Copy-Item -LiteralPath $sourceExecutable -Destination $installedExecutable

$initializationArguments = @(
    '--init',
    '--workspace', $workspaceDirectory,
    '--state', $stateDirectory,
    '--node', $NodeId,
    '--bind', $BindAddress.ToString(),
    '--port', '5443',
    '--scopes', $access.Scopes,
    '--ui-guard', $access.UiGuard,
    '--consent-mode', 'ask'
)
& $installedExecutable @initializationArguments
if ($LASTEXITCODE -ne 0) {
    throw 'Node initialization failed. Inspect the existing files before attempting recovery.'
}

Write-Host 'Initialization complete. Keep this private state outside Git and model workspaces:'
Write-Host $stateDirectory
Write-Host 'To start the visible node in this Windows session:'
Write-Host ('& "{0}" --serve --config "{1}"' -f $installedExecutable, $configurationFile)
Write-Host 'Copy only nodes.json and credential to the controller by a private transfer.'
Write-Host 'Private CA/server keys and PFX files remain on this machine.'
