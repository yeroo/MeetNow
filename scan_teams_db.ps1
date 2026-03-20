$path = Join-Path $env:LOCALAPPDATA 'Packages\MSTeams_8wekyb3d8bbwe\LocalCache\Microsoft\MSTeams\EBWebView\WV2Profile_tfw\IndexedDB\https_teams.microsoft.com_0.indexeddb.leveldb\001739.log'
Write-Host "Exists: $(Test-Path $path)"
if (Test-Path $path) {
    Write-Host "Size: $((Get-Item $path).Length)"
    $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    $bytes = New-Object byte[] $fs.Length
    $null = $fs.Read($bytes, 0, $fs.Length)
    $fs.Close()
    Write-Host "Bytes read: $($bytes.Length)"
    $text = [System.Text.Encoding]::UTF8.GetString($bytes)
    $keywords = @('call','thread','compose','message','missed','Event/Call','messagetype','voip')
    foreach ($kw in $keywords) {
        if ($text -match $kw) { Write-Host "Contains: $kw" } else { Write-Host "Missing: $kw" }
    }
    # Find all field-like patterns near 'call'
    $idx = 0
    while (($idx = $text.IndexOf('call', $idx)) -ge 0) {
        $start = [Math]::Max(0, $idx - 30)
        $end = [Math]::Min($text.Length, $idx + 50)
        $snippet = $text.Substring($start, $end - $start) -replace '[^\x20-\x7e]','.'
        Write-Host "call@$idx`: $snippet"
        $idx++
        if ($idx -gt 430000) { break }
    }
}
