@echo off
if exist Release\Installer.7z (
    del Release\Installer.7z
)

if exist Release\Installer.exe (
    del Release\Installer.exe
)
"C:\Program Files\7-Zip\7z.exe" a Release\Installer.7z Release\MeetNow.Installer.msi Release\setup.exe
copy /b 7zSD.sfx + config.txt + Release\Installer.7z Release\Installer.exe
if exist Release\Installer.7z (
    del Release\Installer.7z
)
