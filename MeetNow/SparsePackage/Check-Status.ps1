Write-Host "=== TrustedPeople certs ==="
Get-ChildItem Cert:\CurrentUser\TrustedPeople | Where-Object { $_.Subject -like '*MeetNow*' } | Format-List Subject, Thumbprint

Write-Host "=== Root certs ==="
Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Subject -like '*MeetNow*' } | Format-List Subject, Thumbprint

Write-Host "=== Developer Mode / Sideloading ==="
try {
    $key = Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue
    Write-Host "AllowAllTrustedApps: $($key.AllowAllTrustedApps)"
    Write-Host "AllowDevelopmentWithoutDevLicense: $($key.AllowDevelopmentWithoutDevLicense)"
} catch {
    Write-Host "Could not read AppModelUnlock registry key"
}

Write-Host ""
Write-Host "=== Registered MeetNow packages ==="
Get-AppxPackage -Name 'MeetNow*' 2>$null | Format-List Name, Status, InstallLocation
