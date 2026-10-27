# Run manually in an elevated PowerShell only when you want to configure the
# physical home-network adapter. No action occurs by shipping or opening the app.
param(
    [Parameter(Mandatory = $true)][string]$InterfaceAlias,
    [int]$Port = 29864,
    [int]$DiscoveryPort = 29865,
    [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$taskApp = Join-Path $PSScriptRoot 'AudioPlaybackConnectorLAN.exe'
if (-not (Test-Path -LiteralPath $taskApp)) { throw 'Place this script beside AudioPlaybackConnectorLAN.exe in the release folder.' }
if ($Port -lt 1024 -or $Port -gt 65535 -or $DiscoveryPort -lt 1024 -or $DiscoveryPort -gt 65535) { throw 'Ports must be 1024..65535.' }
$taskProfile = Get-NetConnectionProfile -InterfaceAlias $InterfaceAlias
$taskProfile | Select-Object InterfaceAlias,NetworkCategory
Write-Output "Proposed: set '$InterfaceAlias' to Private; permit this app's UDP $Port (audio) and $DiscoveryPort (discovery) from LocalSubnet on Private networks."
if (-not $Apply) { Write-Output 'Preview only. Add -Apply to make the above changes as administrator.'; return }
$taskIsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $taskIsAdmin) { throw 'Open PowerShell as administrator before using -Apply.' }
Set-NetConnectionProfile -InterfaceAlias $InterfaceAlias -NetworkCategory Private
foreach ($taskPort in @($Port, $DiscoveryPort)) {
    $taskRuleName = "AudioPlaybackConnectorLAN-UDP-$taskPort"
    $taskLabel = if ($taskPort -eq $DiscoveryPort) { 'discovery' } else { 'audio' }
    if (Get-NetFirewallRule -Name $taskRuleName -ErrorAction SilentlyContinue) {
        Set-NetFirewallRule -Name $taskRuleName -Enabled True -Direction Inbound -Action Allow -Profile Private -Program $taskApp
        Get-NetFirewallRule -Name $taskRuleName | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -Protocol UDP -LocalPort $taskPort
        Get-NetFirewallRule -Name $taskRuleName | Get-NetFirewallAddressFilter | Set-NetFirewallAddressFilter -RemoteAddress LocalSubnet
    } else {
        New-NetFirewallRule -Name $taskRuleName -DisplayName "AudioPlaybackConnector LAN $taskLabel (UDP $taskPort)" -Direction Inbound -Action Allow -Profile Private -Program $taskApp -Protocol UDP -LocalPort $taskPort -RemoteAddress LocalSubnet | Out-Null
    }
}
Write-Output 'Home-network configuration applied.'
