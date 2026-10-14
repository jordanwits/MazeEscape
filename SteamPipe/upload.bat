@echo off
setlocal
rem Uploads Builds\Detour_Release to Steam (app 5042850) with SteamPipe.
rem
rem One-time setup:
rem   1. Download the Steamworks SDK (partner.steamgames.com > Downloads) and unzip it.
rem   2. Point STEAMWORKS_SDK at its "sdk" folder: set it as an environment variable, or edit the default below.
rem   3. Check the depot id in Steamworks (App Admin > SteamPipe > Depots). These scripts assume 5042851.
rem   4. In Steamworks (Installation > General), add a launch option for executable Detour.exe.
rem
rem Usage:   upload.bat ^<steam-account-name^>
rem steamcmd asks for the password and Steam Guard code itself. Never put them in this file.
rem The build uploads without going live. Set it live on a branch in Steamworks (SteamPipe > Builds).

if "%STEAMWORKS_SDK%"=="" set "STEAMWORKS_SDK=C:\steamworks_sdk\sdk"
set "STEAMCMD=%STEAMWORKS_SDK%\tools\ContentBuilder\builder\steamcmd.exe"
set "BUILD=%~dp0..\Builds\Detour_Release"

if "%~1"=="" (
    echo Usage: upload.bat ^<steam-account-name^>
    exit /b 1
)
if not exist "%STEAMCMD%" (
    echo steamcmd not found at "%STEAMCMD%". Set STEAMWORKS_SDK to the Steamworks SDK's sdk folder.
    exit /b 1
)
if not exist "%BUILD%\Detour.exe" (
    echo No build at "%BUILD%". Make a release build to Builds\Detour_Release\Detour.exe first.
    exit /b 1
)
if exist "%BUILD%\steam_appid.txt" (
    echo "%BUILD%" holds steam_appid.txt, so it is a development build. Make a release build first.
    exit /b 1
)

"%STEAMCMD%" +login %1 +run_app_build "%~dp0app_build_5042850.vdf" +quit
exit /b %ERRORLEVEL%
