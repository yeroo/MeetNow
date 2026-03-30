# Setup-Recorder.ps1 — One-click setup for MeetNow Recorder + Transcriber
# Run: powershell -ExecutionPolicy Bypass -File Setup-Recorder.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "=== MeetNow Recorder Setup ===" -ForegroundColor Cyan

# 1. Check .NET SDK
Write-Host "`n[1/5] Checking .NET SDK..." -ForegroundColor Yellow
try {
    $dotnetVersion = & dotnet --version 2>$null
    if ($dotnetVersion -match "^[89]\." -or $dotnetVersion -match "^[1-9]\d+\.") {
        Write-Host "  .NET SDK $dotnetVersion found" -ForegroundColor Green
    } else {
        throw "old version"
    }
} catch {
    Write-Host "  .NET 9+ SDK not found. Install from: https://dotnet.microsoft.com/download" -ForegroundColor Red
    exit 1
}

# 2. Check Python
Write-Host "`n[2/5] Checking Python..." -ForegroundColor Yellow
try {
    $pyVersion = & python --version 2>$null
    Write-Host "  $pyVersion found" -ForegroundColor Green
} catch {
    Write-Host "  Python not found. Install from: https://python.org/downloads" -ForegroundColor Red
    exit 1
}

# 3. Install Python dependencies
Write-Host "`n[3/5] Installing Python dependencies..." -ForegroundColor Yellow
& python -m pip install --quiet -r "$root\MeetNow.Recorder.Transcriber\requirements.txt"
if ($LASTEXITCODE -ne 0) {
    Write-Host "  pip install failed" -ForegroundColor Red
    exit 1
}
Write-Host "  Dependencies installed" -ForegroundColor Green

# 4. Build recorder
Write-Host "`n[4/5] Building MeetNow Recorder..." -ForegroundColor Yellow
& dotnet build "$root\MeetNow.Recorder\MeetNow.Recorder.csproj" --configuration Release --verbosity quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "  Build failed" -ForegroundColor Red
    exit 1
}
Write-Host "  Build succeeded" -ForegroundColor Green

# 5. Copy transcriber to build output
Write-Host "`n[5/5] Copying transcriber to build output..." -ForegroundColor Yellow
$buildDir = "$root\MeetNow.Recorder\bin\Release\net9.0-windows10.0.19041.0\win-x64"
$transcriberDest = "$buildDir\MeetNow.Recorder.Transcriber"
if (Test-Path $transcriberDest) { Remove-Item $transcriberDest -Recurse -Force }
Copy-Item "$root\MeetNow.Recorder.Transcriber" $transcriberDest -Recurse
Remove-Item "$transcriberDest\transcriber\__pycache__" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "  Transcriber copied" -ForegroundColor Green

# 6. Enable transcriber in appsettings
$appsettings = "$buildDir\appsettings.json"
if (Test-Path $appsettings) {
    $json = Get-Content $appsettings -Raw | ConvertFrom-Json
    $json.transcription.enabled = $true
    $json | ConvertTo-Json -Depth 10 | Set-Content $appsettings
    Write-Host "`n  Transcriber enabled in appsettings.json" -ForegroundColor Green
}

Write-Host "`n=== Setup Complete ===" -ForegroundColor Cyan
Write-Host "`nTo run:" -ForegroundColor White
Write-Host "  dotnet run --project MeetNow.Recorder\MeetNow.Recorder.csproj --configuration Release --no-build" -ForegroundColor Gray
Write-Host "`nOr directly:" -ForegroundColor White
Write-Host "  $buildDir\MeetNow.Recorder.exe" -ForegroundColor Gray
Write-Host "`nRecordings go to: %LOCALAPPDATA%\MeetNow\Recordings" -ForegroundColor Gray
Write-Host "First run will download Whisper model (~500MB)" -ForegroundColor Gray
