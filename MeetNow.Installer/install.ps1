$ErrorActionPreference = "Stop"

$appName = "MeetNow"
$installDir = Join-Path $env:LOCALAPPDATA $appName

# Create install directory
if (-not (Test-Path $installDir)) {
    New-Item -ItemType Directory -Path $installDir | Out-Null
}

# Copy files
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Copy-Item "$scriptDir\MeetNow.exe" -Destination $installDir -Force
if (Test-Path "$scriptDir\SFX") {
    Copy-Item "$scriptDir\SFX" -Destination $installDir -Recurse -Force
}

# Add to Windows startup (current user)
$regPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $regPath -Name $appName -Value (Join-Path $installDir "MeetNow.exe")

# Create desktop shortcut
$desktopPath = [Environment]::GetFolderPath("Desktop")
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut("$desktopPath\$appName.lnk")
$shortcut.TargetPath = Join-Path $installDir "MeetNow.exe"
$shortcut.WorkingDirectory = $installDir
$shortcut.IconLocation = Join-Path $installDir "MeetNow.exe"
$shortcut.Save()

Write-Host ""
Write-Host "$appName installed successfully!" -ForegroundColor Green
Write-Host "  Location: $installDir"
Write-Host "  Added to Windows startup"
Write-Host "  Desktop shortcut created"
Write-Host ""
Write-Host "Press any key to launch $appName..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")

Start-Process (Join-Path $installDir "MeetNow.exe")
