@echo off
setlocal
cd /d "%~dp0"
if exist "artifacts\PCFileMgr\PCFileMgr.exe" (
  start "" "artifacts\PCFileMgr\PCFileMgr.exe" %*
  exit /b
)
set DOTNET_CLI_HOME=%~dp0.dotnet
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set MSBuildEnableWorkloadResolver=false
dotnet run --project "PCFileMgr\PCFileMgr.csproj" -c Release -- %*
if errorlevel 1 pause
