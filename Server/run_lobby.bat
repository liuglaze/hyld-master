@echo off
setlocal
cd /d "%~dp0"
for %%I in ("%~dp0..") do set "HYLD_REPO=%%~fI"

REM Local-development defaults only. Explicit deployment environment wins.
REM HYLD_PMNET_DS is obsolete: the retired battle backend cannot be selected.
if not defined HYLD_PMNET_DS_EXE set "HYLD_PMNET_DS_EXE=%HYLD_REPO%\HyldDS\HyldDS.exe"
if not defined HYLD_PMNET_DS_WORKDIR set "HYLD_PMNET_DS_WORKDIR=%HYLD_REPO%\HyldDS"
if not defined HYLD_PMNET_DS_BOOTSTRAP_DIR set "HYLD_PMNET_DS_BOOTSTRAP_DIR=%TEMP%\HyldDSBootstrap"
if not defined HYLD_PMNET_CONTENT_MANIFEST set "HYLD_PMNET_CONTENT_MANIFEST=%HYLD_REPO%\Client\Assets\Resources\PMNet\BattleContentV1.json"

if not exist "bin\Debug\net8.0\Server.dll" (
  echo [ERROR] Build Server/Server.csproj first. No legacy fallback is available.
  exit /b 1
)
if not exist "%HYLD_PMNET_DS_EXE%" (
  echo [ERROR] DS executable missing: "%HYLD_PMNET_DS_EXE%"
  exit /b 1
)
if not exist "%HYLD_PMNET_DS_WORKDIR%\." (
  echo [ERROR] DS working directory missing: "%HYLD_PMNET_DS_WORKDIR%"
  exit /b 1
)
if not exist "%HYLD_PMNET_CONTENT_MANIFEST%" (
  echo [ERROR] Battle content manifest missing: "%HYLD_PMNET_CONTENT_MANIFEST%"
  exit /b 1
)
echo [Lobby] DS: "%HYLD_PMNET_DS_EXE%"
echo [Lobby] Workdir: "%HYLD_PMNET_DS_WORKDIR%"
echo [Lobby] Bootstrap directory: "%HYLD_PMNET_DS_BOOTSTRAP_DIR%"
echo [Lobby] Manifest: "%HYLD_PMNET_CONTENT_MANIFEST%"
echo [Lobby] Rebuild Lobby, DS and Client from the same sources before integration testing.
if /i "%~1"=="--check-only" exit /b 0

dotnet "bin\Debug\net8.0\Server.dll"
exit /b %ERRORLEVEL%
