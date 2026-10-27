param([switch]$Audio)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$taskProjectRoot = Split-Path -Parent $PSScriptRoot
$taskDotnet = Join-Path $taskProjectRoot '.tooling\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $taskDotnet)) { $taskDotnet = (Get-Command dotnet).Source }
$taskArguments = @('run','--project',(Join-Path $taskProjectRoot 'LanAudio.Tests\LanAudio.Tests.csproj'),'-c','Release','--')
if ($Audio) { $taskArguments += '--audio' }
& $taskDotnet @taskArguments
if ($LASTEXITCODE -ne 0) { throw 'LAN tests failed.' }
