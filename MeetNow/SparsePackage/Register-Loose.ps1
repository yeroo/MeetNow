$ErrorActionPreference = 'Stop'

$manifestPath = Join-Path $PSScriptRoot 'AppxManifest.xml'
$externalDir = 'C:\Users\Boris.Kudriashov\Source\repos\MeetNow\MeetNow\bin\Debug\net8.0-windows10.0.19041.0\win-x64'

Write-Host "Registering loose manifest..."
Write-Host "  Manifest: $manifestPath"
Write-Host "  External: $externalDir"

try {
    # Remove old registration
    Get-AppxPackage -Name 'MeetNow.App' -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
} catch {}

try {
    # Register loose manifest with external location
    Add-AppxPackage -Register $manifestPath -ExternalLocation $externalDir
    Write-Host "SUCCESS: Sparse package registered!" -ForegroundColor Green
} catch {
    Write-Host "Loose registration failed: $_" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Trying with -AllowUnsigned..." -ForegroundColor Cyan
    try {
        Add-AppxPackage -Register $manifestPath -ExternalLocation $externalDir -AllowUnsigned
        Write-Host "SUCCESS with -AllowUnsigned!" -ForegroundColor Green
    } catch {
        Write-Host "AllowUnsigned also failed: $_" -ForegroundColor Red
        Write-Host ""
        Write-Host "Trying Developer Mode registration..." -ForegroundColor Cyan
        try {
            # Try enabling developer mode first
            $regPath = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
            Set-ItemProperty -Path $regPath -Name 'AllowAllTrustedApps' -Value 1 -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $regPath -Name 'AllowDevelopmentWithoutDevLicense' -Value 1 -ErrorAction SilentlyContinue
            Write-Host "Attempted to enable developer mode"

            Add-AppxPackage -Register $manifestPath -ExternalLocation $externalDir
            Write-Host "SUCCESS after enabling developer mode!" -ForegroundColor Green
        } catch {
            Write-Host "All approaches failed: $_" -ForegroundColor Red
            Write-Host ""
            Write-Host "You may need to enable Developer Mode manually:" -ForegroundColor White
            Write-Host "  Settings > System > For developers > Developer Mode = ON" -ForegroundColor White
        }
    }
}

Write-Host ""
Write-Host "=== Current registration status ==="
Get-AppxPackage -Name 'MeetNow*' 2>$null | Format-List Name, Status, InstallLocation
