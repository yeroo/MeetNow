$teamsBase = Join-Path $env:LOCALAPPDATA 'Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\WV2Profile_tfw'

# Check Local Storage LevelDB
$lsDir = Join-Path $teamsBase 'Local Storage\leveldb'
Write-Host "=== Local Storage ==="
if (Test-Path $lsDir) {
    Get-ChildItem $lsDir | ForEach-Object { Write-Host "  $($_.Name) ($($_.Length))" }

    foreach ($f in Get-ChildItem $lsDir -Filter '*.log') {
        $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
        $bytes = New-Object byte[] $fs.Length
        $null = $fs.Read($bytes, 0, $fs.Length)
        $fs.Close()
        $text = [System.Text.Encoding]::UTF8.GetString($bytes)
        if ($text -match 'call') {
            Write-Host "  Found 'call' in $($f.Name)"
            $idx = 0
            $shown = 0
            while (($idx = $text.IndexOf('call', $idx)) -ge 0 -and $shown -lt 10) {
                $start = [Math]::Max(0, $idx - 30)
                $end = [Math]::Min($text.Length, $idx + 60)
                $snippet = $text.Substring($start, $end - $start) -replace '[^\x20-\x7e]','.'
                Write-Host "    @$idx`: $snippet"
                $idx++
                $shown++
            }
        }
    }
} else {
    Write-Host "  Not found"
}

# Check Session Storage
$ssDir = Join-Path $teamsBase 'Session Storage'
Write-Host ""
Write-Host "=== Session Storage ==="
if (Test-Path $ssDir) {
    Get-ChildItem $ssDir | ForEach-Object { Write-Host "  $($_.Name) ($($_.Length))" }
} else {
    Write-Host "  Not found"
}

# Check the tfw directory
$tfwDir = Join-Path $env:LOCALAPPDATA 'Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\tfw'
Write-Host ""
Write-Host "=== tfw directory ==="
if (Test-Path $tfwDir) {
    Get-ChildItem $tfwDir -Recurse -File | Select-Object -First 20 | ForEach-Object {
        Write-Host "  $($_.FullName.Replace($tfwDir, '')) ($($_.Length))"
    }
} else {
    Write-Host "  Not found"
}

# Check blob storage for the IndexedDB
$blobDir = Join-Path $env:LOCALAPPDATA 'Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\WV2Profile_tfw\IndexedDB\https_teams.microsoft.com_0.indexeddb.blob'
Write-Host ""
Write-Host "=== Blob Storage ==="
if (Test-Path $blobDir) {
    $dirs = Get-ChildItem $blobDir -Directory
    Write-Host "  Subdirectories: $($dirs.Count)"
    $dirs | Select-Object -First 10 | ForEach-Object { Write-Host "  $($_.Name)" }
} else {
    Write-Host "  Not found"
}
