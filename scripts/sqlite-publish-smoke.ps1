param([string] $Runtime = 'win-x64')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$temp = Join-Path ([IO.Path]::GetTempPath()) ('GameHelper.SqlitePublish.' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    foreach ($layout in @('false', 'true')) {
        $publish = Join-Path $temp "publish-$layout"
        & dotnet publish (Join-Path $repoRoot 'GameHelper.ConsoleHost/GameHelper.ConsoleHost.csproj') -c Release -r $Runtime --self-contained $layout -o $publish
        if ($LASTEXITCODE -ne 0) { throw 'ConsoleHost publish failed.' }
        # Check the real application artifact before adding the test driver.
        $native = Join-Path $publish 'e_sqlite3.dll'
        if (-not (Test-Path $native)) { throw "Missing SQLite native library in self-contained=$layout" }
        $nativeHash = (Get-FileHash $native -Algorithm SHA256).Hash
        $desktopNative = Join-Path $publish 'desktop/e_sqlite3.dll'
        if (-not (Test-Path $desktopNative) -or -not (Test-Path (Join-Path $publish 'desktop/Microsoft.Data.Sqlite.dll'))) {
            throw "Desktop SQLite dependencies missing in self-contained=$layout"
        }
        if ((Get-FileHash $desktopNative -Algorithm SHA256).Hash -ne $nativeHash) { throw 'Desktop and host SQLite native libraries differ.' }
        if (-not (Test-Path (Join-Path $publish 'Microsoft.Data.Sqlite.dll'))) { throw 'Missing SQLite managed assembly.' }
        $probeBuild = Join-Path $temp "probe-$layout"
        & dotnet publish (Join-Path $repoRoot 'tools/GameHelper.SqliteProbe/GameHelper.SqliteProbe.csproj') -c Release -r $Runtime --self-contained $layout -o $probeBuild
        if ($LASTEXITCODE -ne 0) { throw 'SQLite probe publish failed.' }
        # Only copy the driver; every managed/native dependency remains the production artifact.
        foreach ($file in @('GameHelper.SqliteProbe.exe', 'GameHelper.SqliteProbe.dll', 'GameHelper.SqliteProbe.deps.json', 'GameHelper.SqliteProbe.runtimeconfig.json')) {
            Copy-Item (Join-Path $probeBuild $file) $publish
        }
        $eventName = 'Local\GH.SqliteStart.' + [Guid]::NewGuid().ToString('N')
        $readyName = 'Local\GH.SqliteReady.' + [Guid]::NewGuid().ToString('N')
        $start = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $eventName)
        $ready = [Threading.Semaphore]::new(0, 4, $readyName)
        $processes = @()
        try {
            $data = Join-Path $temp "data-$layout"
            for ($i = 0; $i -lt 4; $i++) {
                $out = Join-Path $temp "$layout-$i.out"
                $err = Join-Path $temp "$layout-$i.err"
                $process = Start-Process (Join-Path $publish 'GameHelper.SqliteProbe.exe') -ArgumentList @("`"$data`"", $eventName, $readyName) -PassThru -NoNewWindow -RedirectStandardOutput $out -RedirectStandardError $err
                $processes += @{ Process = $process; Out = $out; Err = $err }
            }
            for ($i = 0; $i -lt 4; $i++) {
                if (-not $ready.WaitOne(30000)) { throw 'SQLite child process did not reach the start barrier.' }
            }
            $start.Set() | Out-Null
            $ids = @()
            foreach ($item in $processes) {
                if (-not $item.Process.WaitForExit(30000)) { throw 'SQLite child process timed out.' }
                if ($item.Process.ExitCode -ne 0) { throw (Get-Content $item.Err -Raw) }
                $result = Get-Content $item.Out -Raw | ConvertFrom-Json
                $ids += $result.DatabaseId
                Write-Host "self-contained=$layout SQLite=$($result.SqliteVersion) database=$($result.DatabaseId) native-SHA256=$nativeHash"
            }
            if (@($ids | Select-Object -Unique).Count -ne 1) { throw 'Concurrent processes created different database identities.' }
            if (@(Get-ChildItem $data -Filter '*.db').Count -ne 1) { throw 'Concurrent initialization created more than one database.' }
        }
        finally {
            foreach ($item in $processes) {
                if (-not $item.Process.HasExited) { $item.Process.Kill(); $item.Process.WaitForExit() }
                $item.Process.Dispose()
            }
            $ready.Dispose()
            $start.Dispose()
        }
        foreach ($boundary in @('completion.commit.before', 'completion.commit.after')) {
            $crashData = Join-Path $temp "crash-$layout-$boundary"
            $driver = Join-Path $publish 'GameHelper.SqliteProbe.exe'
            & $driver session-prepare $crashData
            if ($LASTEXITCODE -ne 0) { throw 'Session crash fixture failed.' }
            $reachedName = 'Local\GH.SqliteCommit.' + [Guid]::NewGuid().ToString('N')
            $reached = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::ManualReset, $reachedName)
            $victim = $null
            try {
                $victim = Start-Process $driver -ArgumentList @('session-crash', "`"$crashData`"", $boundary, $reachedName) -PassThru -NoNewWindow
                if (-not $reached.WaitOne(30000)) { throw "Child did not reach SQLite boundary $boundary" }
                $victim.Kill()
                $victim.WaitForExit()
                $state = & $driver session-inspect $crashData | ConvertFrom-Json
                if ($LASTEXITCODE -ne 0) { throw 'Post-kill inspect failed.' }
                $expectedRows = if ($boundary.EndsWith('before')) { 0 } else { 1 }
                if ($state.Sessions -ne $expectedRows -or $state.Inflight -ne (1 - $expectedRows)) {
                    throw "Non-atomic completion after kill at $boundary"
                }
                for ($retry = 0; $retry -lt 2; $retry++) {
                    $result = & $driver session-recover $crashData | ConvertFrom-Json
                    if ($LASTEXITCODE -ne 0 -or $result.Sessions -ne 1 -or $result.Inflight -ne 0 -or $result.Revision -ne 1) {
                        throw "Session duplicated/lost after recovery at $boundary"
                    }
                }
            }
            finally {
                if ($null -ne $victim) { if (-not $victim.HasExited) { $victim.Kill(); $victim.WaitForExit() }; $victim.Dispose() }
                $reached.Dispose()
            }
            Write-Host "Real SQLite child kill and repeated recovery passed: $layout $boundary"
        }
        Write-Host "SQLite production artifact and four-process initialization passed: self-contained=$layout"
        if ($env:GITHUB_STEP_SUMMARY) {
            "SQLite win-x64 self-contained=$layout : native e_sqlite3.dll SHA256=$nativeHash; real SQL and four-process initialization passed." >> $env:GITHUB_STEP_SUMMARY
        }
    }
}
finally { Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue }
