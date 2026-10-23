<#
.SYNOPSIS
    Inspect, search and assign Steam cover art for the live GameHelper library.

.DESCRIPTION
    Works on the user's real data (no GameHelper build required):
      config.yml                     games list
      config.yml.desktop-artwork.json  dataKey -> Steam AppID
      cache/steam-posters/<appid>.jpg poster cache read by the desktop client

    Actions:
      List    games without a manual association / cached poster (default)
      Search  Steam store search for a title (US + CN stores)
      Add     verify an AppID, download its library capsule, persist the association
      Verify  report stale associations and links whose poster file is missing

.EXAMPLE
    powershell -NoProfile -File poster-tool.ps1 -Action List

.EXAMPLE
    powershell -NoProfile -File poster-tool.ps1 -Action Search -Term "Being a DIK"

.EXAMPLE
    powershell -NoProfile -File poster-tool.ps1 -Action Add -DataKey beingadik -AppId 1126320
#>
[CmdletBinding()]
param(
    [ValidateSet('List', 'Search', 'Add', 'Verify')]
    [string]$Action = 'List',
    [string]$Term,
    [string]$DataKey,
    [uint32]$AppId,
    [string]$ConfigPath = (Join-Path $env:APPDATA 'GameHelper\config.yml'),
    # Allow non-game app types (soundtrack, DLC) when explicitly requested.
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
Add-Type -AssemblyName System.Drawing
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$configPath = [IO.Path]::GetFullPath($ConfigPath)
$dataDir = Split-Path -Parent $configPath
$linksPath = $configPath + '.desktop-artwork.json'
$cacheDir = Join-Path $dataDir 'cache\steam-posters'

function Read-Games {
    if (-not (Test-Path -LiteralPath $configPath)) { throw "Config not found: $configPath" }
    $games = New-Object System.Collections.ArrayList
    $cur = $null
    foreach ($line in [IO.File]::ReadAllLines($configPath, [Text.Encoding]::UTF8)) {
        if ($line -match '^  - dataKey:\s*(.+?)\s*$') {
            if ($null -ne $cur) { [void]$games.Add($cur) }
            $cur = [pscustomobject]@{ DataKey = $matches[1]; Name = ''; Executable = ''; Target = '' }
        }
        elseif ($null -ne $cur) {
            if ($line -match '^    displayName:\s*(.+?)\s*$') { $cur.Name = $matches[1].Trim("'") }
            elseif ($line -match '^    executable:\s*(.+?)\s*$') { $cur.Executable = $matches[1].Trim() }
            elseif ($line -match '^      target:\s*(.+?)\s*$') { $cur.Target = $matches[1].Trim() }
        }
    }
    if ($null -ne $cur) { [void]$games.Add($cur) }
    return $games
}

function Read-Links {
    $links = @{}
    if (Test-Path -LiteralPath $linksPath) {
        $text = [IO.File]::ReadAllText($linksPath, [Text.Encoding]::UTF8)
        if ($text.Trim().Length -gt 0) {
            $obj = $text | ConvertFrom-Json
            foreach ($p in $obj.PSObject.Properties) { $links[$p.Name] = [uint32]$p.Value }
        }
    }
    return $links
}

function Write-Links([hashtable]$links) {
    $json = $links | ConvertTo-Json -Compress
    $tmp = $linksPath + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($tmp, $json, (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tmp -Destination $linksPath -Force
}

function Get-AutoAppId([string]$target) {
    if ($target -match 'rungameid/(\d+)') { return [uint32]$matches[1] }
    return $null
}

function Test-SteamClientPoster([uint32]$id) {
    $steam = $env:GAMEHELPER_STEAM_ROOT
    if (-not $steam) {
        try { $steam = (Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name SteamPath -ErrorAction Stop).SteamPath } catch { }
    }
    if (-not $steam) { return $false }
    $libraryCache = Join-Path $steam 'appcache\librarycache'
    if (Test-Path -LiteralPath (Join-Path $libraryCache ($id.ToString() + '_library_600x900.jpg'))) { return $true }
    $appCache = Join-Path $libraryCache $id.ToString()
    if (Test-Path -LiteralPath $appCache) {
        $file = Get-ChildItem -LiteralPath $appCache -Filter '*library_600x900*.jpg' -ErrorAction SilentlyContinue | Select-Object -First 1
        return [bool]$file
    }
    return $false
}

function Invoke-RestJson([string]$url) {
    for ($i = 0; $i -lt 3; $i++) {
        try { return Invoke-RestMethod -Uri $url -TimeoutSec 20 -UseBasicParsing }
        catch { if ($i -eq 2) { throw }; Start-Sleep -Seconds 2 }
    }
}

function Search-Steam([string]$term) {
    if ([string]::IsNullOrWhiteSpace($term)) { throw 'Search needs -Term.' }
    $found = @{}
    foreach ($store in @(@('english', 'US'), @('schinese', 'CN'))) {
        $url = 'https://store.steampowered.com/api/storesearch/?l=' + $store[0] + '&cc=' + $store[1] +
               '&term=' + [uri]::EscapeDataString($term)
        try { $result = Invoke-RestJson $url } catch { continue }
        foreach ($item in @($result.items)) {
            if ($null -eq $item -or $item.type -ne 'app') { continue }
            $id = [string]$item.id
            if (-not $found.ContainsKey($id)) {
                $found[$id] = [pscustomobject]@{ AppId = [uint32]$item.id; Name = [string]$item.name }
            }
        }
    }
    if ($found.Count -eq 0) { Write-Output "No Steam app results for '$term'."; return }
    $found.Values | Sort-Object AppId | Select-Object -First 12 | Format-Table -AutoSize
}

function Get-StoreItem([uint32]$id) {
    $body = @{
        ids     = @(@{ appid = $id })
        context = @{ language = 'english'; country_code = 'US' }
        data_request = @{ include_assets = $true }
    } | ConvertTo-Json -Depth 6 -Compress
    $url = 'https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=' + [uri]::EscapeDataString($body)
    $response = Invoke-RestJson $url
    if ($null -eq $response.response -or $null -eq $response.response.store_items) { return $null }
    return @($response.response.store_items)[0]
}

function Save-Poster([uint32]$id, $item) {
    $urls = New-Object System.Collections.Generic.List[string]
    if ($null -ne $item -and $null -ne $item.assets -and $item.assets.asset_url_format) {
        $fmt = [string]$item.assets.asset_url_format
        foreach ($key in @('library_capsule_2x', 'library_capsule')) {
            $file = $item.assets.$key
            if ($file) {
                $urls.Add('https://shared.steamstatic.com/store_item_assets/' + $fmt.Replace('${FILENAME}', [string]$file))
            }
        }
    }
    # The unhashed fallback is a grey placeholder for apps whose assets are versioned;
    # it is only used when StoreBrowse metadata is unavailable.
    $urls.Add("https://shared.steamstatic.com/store_item_assets/steam/apps/$id/library_600x900_2x.jpg")
    $urls.Add("https://shared.steamstatic.com/store_item_assets/steam/apps/$id/library_600x900.jpg")

    if (-not (Test-Path -LiteralPath $cacheDir)) { New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null }
    $target = Join-Path $cacheDir ($id.ToString() + '.jpg')
    $tmp = Join-Path $cacheDir ($id.ToString() + '.' + [guid]::NewGuid().ToString('N') + '.tmp')

    foreach ($url in $urls) {
        try {
            for ($i = 0; $i -lt 3; $i++) {
                try { Invoke-WebRequest -Uri $url -OutFile $tmp -TimeoutSec 30 -UseBasicParsing; break }
                catch { if ($i -eq 2) { throw }; Start-Sleep -Seconds 2 }
            }
            $length = (Get-Item -LiteralPath $tmp).Length
            if ($length -lt 20000 -or $length -gt 5MB) { throw 'Unexpected image size.' }
            $img = [System.Drawing.Image]::FromFile($tmp)
            try { $w = $img.Width; $h = $img.Height } finally { $img.Dispose() }
            if ($w -lt 200 -or $h -lt 200) { throw "Image too small: ${w}x${h}." }
            Move-Item -LiteralPath $tmp -Destination $target -Force
            return [pscustomobject]@{ Path = $target; Width = $w; Height = $h; Bytes = $length; Url = $url }
        }
        catch {
            if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
        }
    }
    throw "Could not download a valid poster for AppID $id."
}

switch ($Action) {
    'List' {
        $games = Read-Games
        $links = Read-Links
        $rows = New-Object System.Collections.ArrayList
        $noPoster = New-Object System.Collections.ArrayList
        foreach ($game in $games) {
            $id = $null
            if ($links.ContainsKey($game.DataKey)) { $id = [uint32]$links[$game.DataKey] }
            else { $id = Get-AutoAppId $game.Target }
            $cached = $false
            if ($null -ne $id) {
                $cached = (Test-Path -LiteralPath (Join-Path $cacheDir ($id.ToString() + '.jpg'))) -or (Test-SteamClientPoster $id)
            }
            [void]$rows.Add([pscustomobject]@{
                DataKey = $game.DataKey; Name = $game.Name; AppId = $id; Poster = $cached
            })
            if (-not $cached) { [void]$noPoster.Add($game.DataKey) }
        }
        Write-Output ("Games: {0}   with poster: {1}   without: {2}" -f $games.Count, ($games.Count - $noPoster.Count), $noPoster.Count)
        if ($noPoster.Count -gt 0) {
            Write-Output '--- without poster (manual link or rungameid target not found / not cached) ---'
            $rows | Where-Object { -not $_.Poster } | Format-Table -AutoSize
        }
        $stale = @($links.Keys | Where-Object { $k = $_; -not ($games | Where-Object { $_.DataKey -eq $k }) })
        if ($stale.Count -gt 0) { Write-Output ('Stale associations: ' + ($stale -join ', ')) }
    }
    'Search' { Search-Steam $Term }
    'Add' {
        if ([string]::IsNullOrWhiteSpace($DataKey)) { throw 'Add needs -DataKey.' }
        if ($AppId -eq 0) { throw 'Add needs -AppId.' }
        $games = Read-Games
        $game = $games | Where-Object { $_.DataKey -eq $DataKey } | Select-Object -First 1
        if ($null -eq $game) { throw "DataKey '$DataKey' is not in $configPath" }

        $item = Get-StoreItem $AppId
        if ($null -eq $item) { throw "Steam AppID $AppId was not found." }
        $type = [int]$item.type
        if ($type -ne 0 -and -not $Force) {
            throw "AppID $AppId is Steam type $type ('$($item.name)'), not a base game. Re-run with -Force only if this is intentional."
        }
        Write-Output ("Store item: {0}  [{1}]  type={2}" -f $item.name, $AppId, $type)

        $poster = Save-Poster $AppId $item
        $links = Read-Links
        $links[$DataKey] = $AppId
        Write-Links $links
        Write-Output ("Saved {0}x{1} {2} KB -> {3}" -f $poster.Width, $poster.Height, [math]::Round($poster.Bytes / 1KB), $poster.Path)
        Write-Output ("Linked '{0}' -> {1}; total associations: {2}" -f $DataKey, $AppId, $links.Count)
        Write-Output 'Close and reopen the GameHelper library window to load the new association.'
    }
    'Verify' {
        $games = Read-Games
        $links = Read-Links
        $keys = @($games | ForEach-Object { $_.DataKey })
        $stale = @($links.Keys | Where-Object { $keys -notcontains $_ })
        $missing = @($links.Keys | Where-Object {
            $id = [uint32]$links[$_]
            -not (Test-Path -LiteralPath (Join-Path $cacheDir ($id.ToString() + '.jpg'))) -and -not (Test-SteamClientPoster $id)
        })
        Write-Output ("Games: {0}   associations: {1}   stale: {2}   missing poster file: {3}" -f $keys.Count, $links.Count, $stale.Count, $missing.Count)
        if ($stale.Count -gt 0) { Write-Output ('Stale: ' + ($stale -join ', ')) }
        if ($missing.Count -gt 0) { Write-Output ('Missing cache: ' + ($missing -join ', ')) }
    }
}
