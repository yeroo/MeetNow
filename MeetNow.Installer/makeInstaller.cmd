@echo off
setlocal
cd /d "%~dp0"

set PUBLISH_DIR=..\MeetNow\bin\Release\net8.0-windows\win-x64\publish
set OUTPUT_DIR=Release

echo === Building MeetNow (self-contained single-file) ===
dotnet publish ..\MeetNow\MeetNow.csproj -c Release
if errorlevel 1 (
    echo Build failed!
    exit /b 1
)

echo === Creating installer package ===
if not exist %OUTPUT_DIR% mkdir %OUTPUT_DIR%

if exist %OUTPUT_DIR%\Installer.7z del %OUTPUT_DIR%\Installer.7z
if exist %OUTPUT_DIR%\Installer.exe del %OUTPUT_DIR%\Installer.exe

REM Create a staging directory with flat files for the SFX archive
if exist _staging rd /s /q _staging
mkdir _staging
copy "%PUBLISH_DIR%\MeetNow.exe" _staging\
xcopy "%PUBLISH_DIR%\SFX" _staging\SFX\ /E /I /Y 2>nul
copy install.ps1 _staging\

"C:\Program Files\7-Zip\7z.exe" a %OUTPUT_DIR%\Installer.7z .\\_staging\*
copy /b 7zSD.sfx + config.txt + %OUTPUT_DIR%\Installer.7z %OUTPUT_DIR%\Installer.exe

if exist %OUTPUT_DIR%\Installer.7z del %OUTPUT_DIR%\Installer.7z
rd /s /q _staging

echo === Done ===
echo Installer: %OUTPUT_DIR%\Installer.exe
