param(
    [string]$InstallDir = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

$pfxPath = Join-Path $InstallDir 'MeetNow.pfx'
$cerPath = Join-Path $InstallDir 'MeetNow.cer'
$msixPath = Join-Path $InstallDir 'MeetNow.msix'
$password = 'MeetNow2024!'

# Step 1: Create self-signed certificate
Write-Host "Creating self-signed certificate..." -ForegroundColor Cyan
$pwd = ConvertTo-SecureString -String $password -Force -AsPlainText

# Remove old cert if exists
Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq 'CN=MeetNow' } | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem Cert:\CurrentUser\TrustedPeople | Where-Object { $_.Subject -eq 'CN=MeetNow' } | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem Cert:\CurrentUser\Root | Where-Object { $_.Subject -eq 'CN=MeetNow' } | Remove-Item -Force -ErrorAction SilentlyContinue

$cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=MeetNow' `
    -KeyUsage DigitalSignature -FriendlyName 'MeetNow Sparse Package' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter (Get-Date).AddYears(10) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pwd | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
Write-Host "Certificate created: $($cert.Thumbprint)" -ForegroundColor Green

# Step 2: Build MSIX (if not already built)
if (-not (Test-Path $msixPath)) {
    Write-Host "Building MSIX..." -ForegroundColor Cyan
    $sdkBin = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Directory |
        Where-Object { $_.Name -match '^\d+\.' } |
        Sort-Object Name -Descending | Select-Object -First 1
    $makeappx = Join-Path $sdkBin.FullName "x64\makeappx.exe"

    if (-not (Test-Path $makeappx)) {
        Write-Error "makeappx.exe not found in Windows SDK"
        exit 1
    }

    $sparseDir = Join-Path $PSScriptRoot ""
    & $makeappx pack /d $sparseDir /p $msixPath /nv /o
    if ($LASTEXITCODE -ne 0) { Write-Error "makeappx failed"; exit 1 }
    Write-Host "MSIX built: $msixPath" -ForegroundColor Green
}

# Step 3: Sign MSIX
Write-Host "Signing MSIX..." -ForegroundColor Cyan
$sdkBin = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Directory |
    Where-Object { $_.Name -match '^\d+\.' } |
    Sort-Object Name -Descending | Select-Object -First 1
$signtool = Join-Path $sdkBin.FullName "x64\signtool.exe"

& $signtool sign /fd SHA256 /a /f $pfxPath /p $password $msixPath
if ($LASTEXITCODE -ne 0) { Write-Error "signtool failed"; exit 1 }
Write-Host "MSIX signed" -ForegroundColor Green

# Step 4: Register sparse package
Write-Host "Registering sparse package..." -ForegroundColor Cyan
try {
    # Remove old registration if exists
    Get-AppxPackage -Name 'MeetNow.App' -ErrorAction SilentlyContinue | Remove-AppxPackage -ErrorAction SilentlyContinue
} catch {}

Add-AppxPackage -Path $msixPath -ExternalLocation $InstallDir
Write-Host "Sparse package registered!" -ForegroundColor Green
Write-Host ""
Write-Host "Done! MeetNow now has notification listener access." -ForegroundColor Green
Write-Host "Run MeetNow.exe to start monitoring Teams notifications." -ForegroundColor White
