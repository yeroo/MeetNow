$dbDir = Join-Path $env:LOCALAPPDATA 'Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\WV2Profile_tfw\IndexedDB\https_teams.microsoft.com_0.indexeddb.leveldb'

# Scan WAL for messagetype values and call-related content
$logPath = Join-Path $dbDir '001739.log'
$fs = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
$bytes = New-Object byte[] $fs.Length
$null = $fs.Read($bytes, 0, $fs.Length)
$fs.Close()
$text = [System.Text.Encoding]::UTF8.GetString($bytes)

Write-Host "=== Searching for messagetype values ==="
$idx = 0
$mtypes = @{}
while (($idx = $text.IndexOf('messagetype', $idx)) -ge 0) {
    $start = $idx
    $end = [Math]::Min($text.Length, $idx + 80)
    $snippet = $text.Substring($start, $end - $start) -replace '[^\x20-\x7e]','.'
    # Extract the value after messagetype
    if ($snippet -match 'messagetype.{1,5}([A-Za-z/]+)') {
        $mt = $Matches[1]
        if (-not $mtypes.ContainsKey($mt)) {
            $mtypes[$mt] = 0
            Write-Host "messagetype: $mt (at offset $idx)"
            Write-Host "  context: $snippet"
        }
        $mtypes[$mt]++
    }
    $idx++
}
Write-Host ""
Write-Host "=== messagetype distribution ==="
foreach ($kv in $mtypes.GetEnumerator() | Sort-Object Value -Descending) {
    Write-Host "  $($kv.Key): $($kv.Value)"
}

Write-Host ""
Write-Host "=== Searching for non-empty callId values ==="
$idx = 0
$callCount = 0
while (($idx = $text.IndexOf('callId', $idx)) -ge 0) {
    $start = [Math]::Max(0, $idx - 10)
    $end = [Math]::Min($text.Length, $idx + 100)
    $snippet = $text.Substring($start, $end - $start) -replace '[^\x20-\x7e]','.'
    # Check if there's a non-empty value after callId
    if ($snippet -match 'callId.{1,3}([0-9a-f]{8,})') {
        Write-Host "Non-empty callId at $idx`: $snippet"
        $callCount++
    }
    $idx++
}
Write-Host "Non-empty callId count: $callCount"

Write-Host ""
Write-Host "=== Searching for Event/Call or call-specific message types ==="
foreach ($pattern in @('Event/Call','ThreadActivity/Call','missed','declined','no answer','ended','started a call','calling')) {
    $idx = $text.IndexOf($pattern)
    if ($idx -ge 0) {
        $start = [Math]::Max(0, $idx - 40)
        $end = [Math]::Min($text.Length, $idx + 80)
        $snippet = $text.Substring($start, $end - $start) -replace '[^\x20-\x7e]','.'
        Write-Host "Found '$pattern' at $idx`: $snippet"
    } else {
        Write-Host "Not found: '$pattern'"
    }
}
