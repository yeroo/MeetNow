# MeetNow - Publish GitHub Release
# Builds, publishes, and creates a GitHub release with MeetNow.exe

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot
$ProjectPath = Join-Path $RepoRoot "MeetNow\MeetNow.csproj"
$PublishDir = Join-Path $RepoRoot "MeetNow\bin\Release\net8.0-windows\win-x64\publish"

# --- Get latest release version from GitHub ---
Write-Host "`n--- MeetNow Release Publisher ---" -ForegroundColor Cyan

$latestTag = $null
try {
    $latestTag = gh release view --json tagName --jq ".tagName" 2>$null
} catch {}

if ($latestTag) {
    Write-Host "Latest release: $latestTag" -ForegroundColor Yellow
    # Parse version and bump patch
    $versionMatch = [regex]::Match($latestTag, 'v?(\d+)\.(\d+)\.(\d+)')
    if ($versionMatch.Success) {
        $major = [int]$versionMatch.Groups[1].Value
        $minor = [int]$versionMatch.Groups[2].Value
        $patch = [int]$versionMatch.Groups[3].Value + 1
        $suggestedVersion = "v$major.$minor.$patch"
    } else {
        $suggestedVersion = "v1.0.0"
    }
} else {
    Write-Host "No existing releases found." -ForegroundColor Yellow
    $suggestedVersion = "v1.0.0"
}

# --- Ask user for version ---
$userInput = Read-Host "Version to release [$suggestedVersion]"
$version = if ($userInput.Trim()) { $userInput.Trim() } else { $suggestedVersion }

# Ensure version starts with 'v'
if (-not $version.StartsWith("v")) { $version = "v$version" }

Write-Host "`nWill publish: $version" -ForegroundColor Green

# --- Build & Publish ---
Write-Host "`nBuilding..." -ForegroundColor Cyan
dotnet publish $ProjectPath -c Release
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$exePath = Join-Path $PublishDir "MeetNow.exe"
if (-not (Test-Path $exePath)) { throw "MeetNow.exe not found at $exePath" }

$sizeMB = [math]::Round((Get-Item $exePath).Length / 1MB, 1)
Write-Host "Built: MeetNow.exe ($sizeMB MB)" -ForegroundColor Green

# --- Create GitHub Release ---
Write-Host "`nCreating GitHub release $version..." -ForegroundColor Cyan

gh release create $version $exePath `
    --title "MeetNow $version" `
    --generate-notes

if ($LASTEXITCODE -ne 0) { throw "Failed to create release" }

Write-Host "`nDone! Release $version published." -ForegroundColor Green
gh release view $version --web
