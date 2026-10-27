param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:MSBuildEnableWorkloadResolver = 'false'
$project = Join-Path $PSScriptRoot 'PCFileMgr\PCFileMgr.csproj'
$destination = Join-Path $PSScriptRoot 'artifacts\PCFileMgr'
$contained = if ($FrameworkDependent) { 'false' } else { 'true' }
dotnet publish $project -c Release -r win-x64 --self-contained $contained -o $destination -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -m:1 -nr:false -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Write-Output "Ready: $destination\PCFileMgr.exe"
