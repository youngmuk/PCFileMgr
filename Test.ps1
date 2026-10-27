param([switch]$RecycleSmoke)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build (Join-Path $PSScriptRoot 'PCFileMgr.Tests\PCFileMgr.Tests.csproj') -c Release -m:1 -nr:false -p:UseSharedCompilation=false -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
$testExe = Join-Path $PSScriptRoot 'PCFileMgr.Tests\bin\Release\net10.0-windows\PCFileMgr.Tests.exe'
if ($RecycleSmoke) { & $testExe --recycle-smoke } else { & $testExe }
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
