$lsDir = Join-Path $env:LOCALAPPDATA 'Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\WV2Profile_tfw\Local Storage\leveldb'

Write-Host "=== Scanning Local Storage for call-related data ==="
foreach ($f in Get-ChildItem $lsDir -Filter '*.log','*.ldb' -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.log','.ldb' }) {
    $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
    $bytes = New-Object byte[] $fs.Length
    $null = $fs.Read($bytes, 0, $fs.Length)
    $fs.Close()
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)

    foreach ($pattern in @('callHistory','callLog','missedCall','recentCall','callType','Event/Call')) {
        if ($text -match $pattern) {
            Write-Host "Found '$pattern' in $($f.Name)"
            $idx = $text.IndexOf($pattern)
            $start = [Math]::Max(0, $idx - 50)
            $end = [Math]::Min($text.Length, $idx + 100)
            $snippet = $text.Substring($start, $end - $start) -replace '[^\x20-\x7e]','.'
            Write-Host "  $snippet"
        }
    }
}

# Also scan Session Storage
$ssDir = Join-Path $env:LOCALAPPDATA 'Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\WV2Profile_tfw\Session Storage'
Write-Host ""
Write-Host "=== Scanning Session Storage ==="
foreach ($f in Get-ChildItem $ssDir -Filter '*.log','*.ldb' -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.log','.ldb' }) {
    $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite')
    $bytes = New-Object byte[] $fs.Length
    $null = $fs.Read($bytes, 0, $fs.Length)
    $fs.Close()
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)

    foreach ($pattern in @('call','Call')) {
        $idx = 0
        $shown = 0
        while (($idx = $text.IndexOf($pattern, $idx)) -ge 0 -and $shown -lt 5) {
            $start = [Math]::Max(0, $idx - 20)
            $end = [Math]::Min($text.Length, $idx + 60)
            $snippet = $text.Substring($start, $end - $start) -replace '[^\x20-\x7e]','.'
            if ($snippet -match 'call[A-Z]|Call[A-Z]|callH|callL|callT|missedC|recentC') {
                Write-Host "Found in $($f.Name): $snippet"
                $shown++
            }
            $idx++
        }
    }
}

# Scan the WAL files of the main IndexedDB more comprehensively
# Look for Event/Call messagetype in ALL data (including encoded V8 fields)
$dbDir = Join-Path $env:LOCALAPPDATA 'Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\WV2Profile_tfw\IndexedDB\https_teams.microsoft.com_0.indexeddb.leveldb'
Write-Host ""
Write-Host "=== Unique field names in WAL ==="
$logPath = Join-Path $dbDir '001739.log'
$fs = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
$bytes = New-Object byte[] $fs.Length
$null = $fs.Read($bytes, 0, $fs.Length)
$fs.Close()
$text = [System.Text.Encoding]::UTF8.GetString($bytes)
$fieldMatches = [regex]::Matches($text, '[\x20-\x7e]{3,40}(?=[\x22])')
$fields = @{}
foreach ($m in $fieldMatches) {
    $val = $m.Value -replace '[^\w/]',''
    if ($val.Length -ge 3 -and $val.Length -le 30) {
        if (-not $fields.ContainsKey($val)) { $fields[$val] = 0 }
        $fields[$val]++
    }
}
$fields.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 80 | ForEach-Object {
    Write-Host "  $($_.Key): $($_.Value)"
}
