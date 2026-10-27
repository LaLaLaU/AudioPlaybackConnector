param(
    [switch]$Portable,
    [string]$BluetoothExe = ''
)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$taskProjectRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskProjectRoot '.tooling\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet).Source }
$taskApp = Join-Path $taskProjectRoot 'LanAudio\LanAudio.csproj'
$taskOutput = Join-Path $taskProjectRoot 'dist\AudioPlaybackConnector-LAN-win-x64'
if ($Portable) {
    & $taskDotnet publish $taskApp -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $taskOutput --nologo
} else {
    & $taskDotnet publish $taskApp -c Release -r win-x64 --self-contained false -p:DebugType=None -p:DebugSymbols=false -o $taskOutput --nologo
}
if ($LASTEXITCODE -ne 0) { throw 'LAN publish failed.' }
if ($BluetoothExe) {
    if (-not (Test-Path -LiteralPath $BluetoothExe -PathType Leaf)) { throw 'Bluetooth executable not found.' }
    if ((Split-Path -Leaf $BluetoothExe) -ne 'AudioPlaybackConnector64.exe') { throw 'Expected the upstream AudioPlaybackConnector64.exe.' }
    Copy-Item -LiteralPath $BluetoothExe -Destination (Join-Path $taskOutput 'AudioPlaybackConnector64.exe')
}
Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'docs\快速开始.txt') -Destination $taskOutput
Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'docs\THIRD-PARTY-NOTICES.txt') -Destination $taskOutput
Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'LICENSE') -Destination (Join-Path $taskOutput 'LICENSE.upstream.txt')
$taskDotnetRoot = Split-Path -Parent $taskDotnet
Copy-Item -LiteralPath (Join-Path $taskDotnetRoot 'LICENSE.txt') -Destination (Join-Path $taskOutput 'LICENSE.dotnet.txt')
Copy-Item -LiteralPath (Join-Path $taskDotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $taskOutput 'ThirdPartyNotices.dotnet.txt')
Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'scripts\Configure-HomeNetwork.ps1') -Destination $taskOutput
if (Test-Path -LiteralPath (Join-Path $taskProjectRoot 'docs\验证记录.md')) {
    Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'docs\验证记录.md') -Destination $taskOutput
}
$taskHashes = Get-ChildItem -LiteralPath $taskOutput -File | Where-Object { $_.Name -ne 'SHA256SUMS.txt' } | Sort-Object Name | ForEach-Object {
    $taskHash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
    "$($taskHash.Hash)  $($_.Name)"
}
$taskHashes | Set-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS.txt') -Encoding utf8
Write-Output "Built: $taskOutput"
