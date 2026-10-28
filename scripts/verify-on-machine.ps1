# GameHelper 实机验证 workflow（开发完成后执行）
#
# 前置条件：
#   - 默认保留运行中的实例；-Restart 只停止能确认来自目标发布目录的实例。
#   - 发布目录可能被运行实例锁定，因此先发布到独立临时目录，再尝试整体替换 publish 目录。
#
# 步骤：
#   1. dotnet publish 到独立临时目录（不被运行实例锁定）。
#   2. 启动独立测试实例（GAMEHELPER_DATA_DIR 沙盒 + 禁用单实例互斥），执行实机验证命令。
#   3. 验证通过后，尝试把新产物目录整体替换到快捷方式指向的 publish 目录；
#      若文件被运行实例锁定，保留临时产物并提示用户下次启动前重试。
#
# 用法：
#   powershell -File scripts\verify-on-machine.ps1             完整发布 + 验证 + 部署
#   powershell -File scripts\verify-on-machine.ps1 -SkipVerify 只发布 + 部署，跳过实机验证
#   powershell -File scripts\verify-on-machine.ps1 -SkipVerify -Restart
#       直接更新策略：运行中的实例会被强制结束（在途游戏会话由启动恢复机制续算，不丢数据）、
#       清理孤儿 ETW 会话、原子换入新产物并重启确认存活。
param(
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [switch] $SkipVerify,
    [switch] $SkipDeploy,
    [switch] $Restart,
    [switch] $RequireClean,
    [switch] $AllowDegradedMonitor,
    [switch] $VerifyRuntime,
    [string] $DiagnosticsDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path $PSScriptRoot 'start-checked-instance.ps1')

if ($VerifyRuntime -and $SkipVerify) { throw '-VerifyRuntime cannot be combined with -SkipVerify.' }
if (-not [string]::IsNullOrWhiteSpace($DiagnosticsDirectory)) {
    $DiagnosticsDirectory = [IO.Path]::GetFullPath($DiagnosticsDirectory)
    New-Item -ItemType Directory -Path $DiagnosticsDirectory -Force | Out-Null
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path

if ($RequireClean) {
    $dirty = @(& git -C $repoRoot status --short 2>$null)
    if ($dirty.Count -gt 0) {
        throw ("Working tree has uncommitted changes; commit them or omit -RequireClean:" + [Environment]::NewLine + ($dirty -join [Environment]::NewLine))
    }
}
$projectPath = Join-Path $repoRoot "GameHelper.ConsoleHost\GameHelper.ConsoleHost.csproj"
$publishDir = Join-Path $repoRoot "GameHelper.ConsoleHost\bin\$Configuration\net8.0-windows\$Runtime\publish"
$tempRoot = [IO.Path]::GetTempPath()

function Assert-ContainedDirectory([string] $Path, [string] $Root) {
    $absolutePath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $absoluteRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $absolutePath.StartsWith($absoluteRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing directory operation outside '$absoluteRoot': $absolutePath"
    }

    # Resolve existing path components too: a junction must not redirect a destructive
    # operation outside the checked workspace/temp root.
    $ancestor = $absolutePath
    while (-not [string]::IsNullOrEmpty($ancestor)) {
        if (Test-Path -LiteralPath $ancestor) {
            $item = Get-Item -LiteralPath $ancestor -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Refusing directory operation through a reparse point: $ancestor"
            }
        }
        $parent = Split-Path -Parent $ancestor
        if ([string]::IsNullOrEmpty($parent) -or $parent -eq $ancestor) { break }
        $ancestor = $parent
    }
}

function Remove-CheckedDirectory([string] $Path, [string] $Root) {
    Assert-ContainedDirectory $Path $Root
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
}

function Move-CheckedDirectory([string] $Source, [string] $Destination, [string] $SourceRoot, [string] $DestinationRoot) {
    Assert-ContainedDirectory $Source $SourceRoot
    Assert-ContainedDirectory $Destination $DestinationRoot
    Move-Item -LiteralPath $Source -Destination $Destination
}

function Reset-PublishIntegrityLabel {
    param([Parameter(Mandatory = $true)][string] $Directory)

    # A build produced inside a sandboxed/isolated shell can inherit a Low mandatory integrity
    # label through the whole output tree. Windows caps a process at the image file's label, so a
    # Low-labelled GameHelper.exe cannot write %AppData% (Medium) or create the kernel ETW session
    # (both surface as Access Denied). Normalise the deployed tree back to Medium; best effort.
    try {
        $output = & icacls.exe $Directory /setintegritylevel '(OI)(CI)M' /T 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "Could not normalise the integrity label of '$Directory': $($output | Select-Object -Last 3 | Out-String)"
        }
    }
    catch {
        Write-Warning "Could not normalise the integrity label of '$Directory': $($_.Exception.Message)"
    }
}

function Stop-OrphanedEtwSessions {
    $logmanList = & logman.exe -ets 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Could not enumerate ETW sessions; leaving them untouched."
        return
    }

    foreach ($line in @($logmanList)) {
        # Only the current owner-PID + GUID format proves which process owns the
        # session. Preserve legacy/unknown sessions and every live owner, including
        # isolated verification instances.
        if ($line -notmatch '^\s*(GameHelper-ETW-([1-9][0-9]*)-[0-9a-fA-F]{32})(?:\s|$)') { continue }
        $sessionName = $Matches[1]
        $ownerProcessId = 0
        if (-not [int]::TryParse($Matches[2], [ref] $ownerProcessId)) { continue }
        try {
            $owner = [Diagnostics.Process]::GetProcessById($ownerProcessId)
            $owner.Dispose()
            continue
        }
        catch [ArgumentException] {
            # Only a missing PID establishes that the owner is dead. Other errors
            # (e.g. access denied) leave the session untouched.
        }
        catch { continue }

        Write-Host "  stopping orphaned ETW session: $sessionName"
        & logman.exe stop $sessionName -ets 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "Could not stop orphaned ETW session: $sessionName" }
    }
}

Assert-ContainedDirectory $publishDir $repoRoot

# ---- 1. Publish to a standalone temp directory (never locked by a running instance) ----
$tempPublish = Join-Path ([IO.Path]::GetTempPath()) ("GameHelper.Verify." + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempPublish | Out-Null

Write-Host "[1/3] Publishing ConsoleHost to temp directory..."
& dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained true -o $tempPublish --nologo
if ($LASTEXITCODE -ne 0) {
    Remove-CheckedDirectory $tempPublish $tempRoot
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exeName = "GameHelper.ConsoleHost.exe"
$tempExe = Join-Path $tempPublish $exeName
if (-not (Test-Path -LiteralPath $tempExe)) {
    throw "Published executable not found: $tempExe"
}

# ---- 2. Verify with an isolated test instance ----
if (-not $SkipVerify) {
    Write-Host "[2/3] Verifying with isolated test instance (sandboxed data dir)..."
    $sandbox = Join-Path ([IO.Path]::GetTempPath()) ("GameHelper.Sandbox." + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path (Join-Path $sandbox "GameHelper") | Out-Null

    $configPath = Join-Path (Join-Path $sandbox "GameHelper") "config.yml"
    @"
monitor: ETW
startup:
  launchOnStartup: false
games:
  - dataKey: verify_smoke
    executable: verify_smoke.exe
    displayName: "Verify Smoke Game"
    enabled: true
    hdr: false
"@ | Set-Content -Path $configPath -Encoding UTF8

    # Seed a real legacy history before first boot, so readiness/IPC prove activation rather than an empty database.
    $legacyHistory = Join-Path (Join-Path $sandbox 'GameHelper') 'playtime.csv'
    [IO.File]::WriteAllText($legacyHistory, "game,start_time,end_time,duration_minutes`r`nverify_smoke,2025-01-01T10:00:00,2025-01-01T10:17:00,17`r`n", [Text.UTF8Encoding]::new($false))
    $legacyHash = (Get-FileHash $legacyHistory -Algorithm SHA256).Hash
    $legacyTheme = $configPath + '.desktop-theme.json'
    [IO.File]::WriteAllText($legacyTheme, '{"Theme":"light"}', [Text.UTF8Encoding]::new($false))
    $legacyThemeHash = (Get-FileHash $legacyTheme -Algorithm SHA256).Hash
    $previousDataDirectory = $env:GAMEHELPER_DATA_DIR
    $previousDisableSingleInstance = $env:GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE
    $env:GAMEHELPER_DATA_DIR = $sandbox
    $env:GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE = "1"
    $testRuntime = $null
    $verificationStarted = Get-Date
    try {
        $validateOutput = & $tempExe validate-config --config $configPath 2>&1
        if ($LASTEXITCODE -ne 0 -or (($validateOutput -join "`n") -notmatch "Config is valid\.")) {
            throw "Test instance validate-config failed:`n$($validateOutput -join "`n")"
        }

        $listOutput = & $tempExe config list --config $configPath 2>&1
        if ($LASTEXITCODE -ne 0 -or (($listOutput -join "`n") -notmatch "verify_smoke")) {
            throw "Test instance config list failed:`n$($listOutput -join "`n")"
        }

        if ($VerifyRuntime) {
            # Reuse the deployment launch/health probes against only this temporary build.
            $testRuntime = Start-CheckedInstance $tempPublish ([ref] $testRuntime)
            & (Join-Path $PSScriptRoot 'assert-deploy-health.ps1') -Directory $tempPublish -RepoRoot $repoRoot `
                -ProcessId $testRuntime.Id -AllowDegradedMonitor:$AllowDegradedMonitor
            $database = Join-Path (Join-Path $sandbox 'GameHelper') 'userdata.db'
            $receipt = Join-Path (Join-Path $sandbox 'GameHelper') 'migration-complete.json'
            if (-not (Test-Path $database) -or -not (Test-Path $receipt)) { throw 'SQLite session storage did not activate.' }
            $manifestFiles = @(Get-ChildItem (Join-Path (Join-Path $sandbox 'GameHelper') 'migration-backups') -Filter 'manifest.json' -Recurse)
            $inventories = @($manifestFiles | ForEach-Object { [pscustomobject]@{ File = $_; Manifest = (Get-Content $_.FullName -Raw | ConvertFrom-Json) } })
            $historyInventories = @($inventories | Where-Object { @($_.Manifest.Sources | Where-Object { $_.Kind -eq 'csv' }).Count -eq 1 })
            $scopeInventories = @($inventories | Where-Object { @($_.Manifest.Sources | Where-Object { $_.Kind -eq 'csv' }).Count -eq 0 })
            if ($inventories.Count -ne 2 -or $historyInventories.Count -ne 1 -or $scopeInventories.Count -ne 1) {
                throw 'Expected one shared-history inventory and one incremental Desktop scope inventory.'
            }
            $scopeSources = @($scopeInventories[0].Manifest.Sources)
            if ($scopeSources.Count -ne 6 -or @($scopeSources | Where-Object { $_.ConfigPath -ne $configPath }).Count) {
                throw 'Incremental Desktop inventory must contain exactly this scope and six side documents.'
            }
            $themeSource = @($scopeSources | Where-Object { $_.Kind -eq 'theme' })[0]
            $themeOriginal = Join-Path $scopeInventories[0].File.DirectoryName $themeSource.BackupName
            if (-not $themeSource.Exists -or (Get-FileHash $themeOriginal -Algorithm SHA256).Hash -ne $legacyThemeHash) {
                throw 'Scope import did not preserve original Desktop theme bytes.'
            }
            $manifest = $historyInventories[0].Manifest
            $csvSource = @($manifest.Sources | Where-Object { $_.Kind -eq 'csv' })[0]
            $original = Join-Path $historyInventories[0].File.DirectoryName $csvSource.BackupName
            if ((Get-FileHash $original -Algorithm SHA256).Hash -ne $legacyHash) { throw 'Migration changed original history bytes.' }
            $runtimeLog = $configPath + '.runtime.log'
            if (-not (Test-Path -LiteralPath $runtimeLog) -or (Get-Item -LiteralPath $runtimeLog).Length -eq 0) {
                throw 'Background runtime did not write its log in the sandbox data directory.'
            }
            $libraryCheck = Join-Path $sandbox 'library-health.json'
            $libraryProbe = Start-Process -FilePath (Join-Path $tempPublish 'desktop\GameHelper.Desktop.exe') `
                -ArgumentList @('--check', ('"' + $libraryCheck + '"')) -WindowStyle Hidden -PassThru
            try {
                if (-not $libraryProbe.WaitForExit(15000)) { $libraryProbe.Kill(); throw 'Isolated library IPC probe timed out.' }
                if ($libraryProbe.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $libraryCheck)) { throw 'Isolated library IPC probe failed.' }
                $libraryResult = Get-Content -LiteralPath $libraryCheck -Raw | ConvertFrom-Json
                if (-not $libraryResult.Success -or $libraryResult.RuntimeProcessId -ne $testRuntime.Id) {
                    throw 'Library IPC did not connect to the isolated runtime.'
                }
                $migratedGame = @($libraryResult.Games | Where-Object { $_.Config.DataKey -eq 'verify_smoke' })
                if ($migratedGame.Count -ne 1 -or $migratedGame[0].TotalMinutes -ne 17) {
                    throw 'Post-migration library IPC lost the seeded playtime.'
                }
            }
            finally { $libraryProbe.Dispose() }
            # Stop only the isolated host before exercising the offline downgrade commands.
            Stop-Process -Id $testRuntime.Id -Force
            $testRuntime.WaitForExit(30000) | Out-Null
            $testRuntime.Dispose()
            $testRuntime = $null
            $downgradeOutput = & $tempExe storage prepare-downgrade --config $configPath 2>&1
            if ($LASTEXITCODE -ne 0) { throw "Isolated downgrade preparation failed: $downgradeOutput" }
            $downgradeManifests = @(Get-ChildItem (Join-Path (Join-Path $sandbox 'GameHelper') 'downgrade-backups') -Filter 'manifest.json' -Recurse)
            if ($downgradeManifests.Count -ne 1) { throw 'Downgrade preparation did not publish one verified archive.' }
            $preflight = & $tempExe storage check-downgrade --config $configPath 2>&1
            if ($LASTEXITCODE -ne 0 -or (($preflight -join "`n") -notmatch 'Downgrade preflight passed')) { throw "Downgrade preflight failed: $preflight" }
            # Reuse the real published ETW regression: game survives two host crashes and completes once.
            $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
            try {
                if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                    throw 'Storage restart verification requires an elevated Windows runner; refusing a silently skipped ETW test.'
                }
            } finally { $identity.Dispose() }
            $priorPublishExe = $env:GAMEHELPER_TEST_PUBLISH_EXE
            try {
                $env:GAMEHELPER_TEST_PUBLISH_EXE = $tempExe
                & dotnet test (Join-Path $repoRoot 'GameHelper.Tests/GameHelper.Tests.csproj') --configuration $Configuration `
                    --filter 'FullyQualifiedName~Monitor_WhenElevatedWithPublishedExe_ResumesAcrossTwoCrashesAndPersistsOneContinuousSession'
                if ($LASTEXITCODE -ne 0) { throw 'Published two-crash session recovery verification failed.' }
            } finally { $env:GAMEHELPER_TEST_PUBLISH_EXE = $priorPublishExe }
            Write-Host 'Isolated runtime verification passed (migration, IPC, two-crash session recovery and offline downgrade preflight).'
        }

        Write-Host "Test instance verification passed (data dir sandboxed at $sandbox)."
    }
    finally {
        try {
            if ($VerifyRuntime) {
                # Startup can fail before returning its PID. Only stop our temporary build.
                Get-Process -Name GameHelper.ConsoleHost, GameHelper.Desktop -ErrorAction SilentlyContinue |
                    Where-Object { $_.Path -eq $tempExe -or $_.Path -eq (Join-Path $tempPublish 'desktop\GameHelper.Desktop.exe') } |
                    ForEach-Object {
                        try { Stop-Process -InputObject $_ -Force; $_.WaitForExit(30000) | Out-Null }
                        catch { Write-Warning "Could not clean up isolated PID $($_.Exception.Message)" }
                    }
            }
        }
        finally {
            try {
                if ($DiagnosticsDirectory) {
                    $desktopError = Join-Path $tempRoot 'GameHelper.Desktop-error.txt'
                    if ((Test-Path -LiteralPath $desktopError) -and (Get-Item -LiteralPath $desktopError).LastWriteTime -ge $verificationStarted) {
                        Copy-Item -LiteralPath $desktopError -Destination $DiagnosticsDirectory -Force
                    }
                    Copy-Item -LiteralPath $sandbox -Destination (Join-Path $DiagnosticsDirectory 'sandbox') -Recurse -Force
                    Get-ChildItem -LiteralPath $tempPublish -Recurse -File -Include '*.log', '*error*.txt', 'deploy-manifest.json' |
                        Copy-Item -Destination $DiagnosticsDirectory -Force
                }
            }
            finally {
                $env:GAMEHELPER_DATA_DIR = $previousDataDirectory
                $env:GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE = $previousDisableSingleInstance
                Remove-CheckedDirectory $sandbox $tempRoot
            }
        }
    }
}
else {
    Write-Host "[2/3] Skipping verification (-SkipVerify)."
}

# ---- 3. Deploy to the publish directory referenced by the user's shortcut ----
if ($SkipDeploy) {
    Write-Host "[3/3] Skipping deploy (-SkipDeploy). New build kept at: $tempPublish"
    exit 0
}

Write-Host "[3/3] Deploying to publish directory: $publishDir"

$deployed = $false
$backupDir = $null
$newDirectoryInstalled = $false
$stoppedOldInstance = $false
$relaunched = $null
$desktopInstances = @()
$relaunchedDesktop = $null
$oldSupportsBackground = $false
if (Test-Path -LiteralPath (Join-Path $publishDir 'deploy-manifest.json')) {
    $oldManifest = Get-Content -LiteralPath (Join-Path $publishDir 'deploy-manifest.json') -Raw | ConvertFrom-Json
    $oldSupportsBackground = $null -ne $oldManifest.PSObject.Properties['startupMode'] -and $oldManifest.startupMode -eq 'background'
}
try {
    $instances = @(Get-Process -Name GameHelper.ConsoleHost -ErrorAction SilentlyContinue)
    $unknownInstances = @($instances | Where-Object { [string]::IsNullOrWhiteSpace($_.Path) })
    if ($unknownInstances.Count -gt 0) {
        throw "Cannot verify the executable path of GameHelper PID(s) $($unknownInstances.Id -join ', '); preserving all instances and the existing deployment."
    }
    $runningInstances = @($instances | Where-Object { $_.Path -eq (Join-Path $publishDir $exeName) })
    $desktopExe = Join-Path $publishDir 'desktop\GameHelper.Desktop.exe'
    $allDesktopInstances = @(Get-Process -Name GameHelper.Desktop -ErrorAction SilentlyContinue)
    if (@($allDesktopInstances | Where-Object { [string]::IsNullOrWhiteSpace($_.Path) }).Count -gt 0) {
        throw 'Cannot verify a desktop instance path; preserving the current deployment.'
    }
    $desktopInstances = @($allDesktopInstances | Where-Object { $_.Path -eq $desktopExe })
    if (($runningInstances.Count -gt 0 -or $desktopInstances.Count -gt 0) -and -not $Restart) {
        Write-Host "Publish directory is in use by GameHelper PID(s) $($runningInstances.Id -join ', '); keeping the existing deployment."
        return
    }

    foreach ($desktopInstance in $desktopInstances) {
        # UI drafts are persisted separately from configuration before direct-update restarts.
        Stop-Process -InputObject $desktopInstance -Force
        if (-not $desktopInstance.WaitForExit(30000)) { throw 'Desktop instance did not exit; aborting deploy.' }
    }

    foreach ($inst in $runningInstances) {
        Write-Host "  stopping target instance PID $($inst.Id)"
        Stop-Process -InputObject $inst -Force
        $stoppedOldInstance = $true
        if (-not $inst.WaitForExit(30000)) { throw "GameHelper PID $($inst.Id) did not exit within 30s; aborting deploy." }
    }
    if ($Restart) { Stop-OrphanedEtwSessions }

    if (Test-Path -LiteralPath $publishDir) {
        $backupDir = "$publishDir.old-" + [Guid]::NewGuid().ToString("N")
        Move-CheckedDirectory $publishDir $backupDir $repoRoot $repoRoot
    }
    else { New-Item -ItemType Directory -Path (Split-Path -Parent $publishDir) -Force | Out-Null }

    Move-CheckedDirectory $tempPublish $publishDir $tempRoot $repoRoot
    $newDirectoryInstalled = $true
    Reset-PublishIntegrityLabel -Directory $publishDir
    $healthScript = Join-Path $PSScriptRoot 'assert-deploy-health.ps1'
    & $healthScript -Directory $publishDir -RepoRoot $repoRoot
    if ($Restart) {
        Write-Host "Launching GameHelper from the updated publish directory..."
        $relaunched = Start-CheckedInstance $publishDir ([ref] $relaunched)
        & $healthScript -Directory $publishDir -RepoRoot $repoRoot -ProcessId $relaunched.Id -AllowDegradedMonitor:$AllowDegradedMonitor
        $libraryCheck = Join-Path $tempRoot ('GameHelper.LibraryHealth.' + [guid]::NewGuid().ToString('N') + '.json')
        $libraryProbe = Start-Process -FilePath $desktopExe -ArgumentList @('--check', ('"' + $libraryCheck + '"')) -WindowStyle Hidden -PassThru
        if (-not $libraryProbe.WaitForExit(15000)) { Stop-Process -InputObject $libraryProbe -Force; throw 'Desktop IPC health probe timed out.' }
        if ($libraryProbe.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $libraryCheck)) { throw 'Desktop cannot connect to the updated runtime.' }
        $libraryResult = Get-Content -LiteralPath $libraryCheck -Raw | ConvertFrom-Json
        if (-not $libraryResult.Success) { throw 'Desktop IPC health probe failed.' }
        Write-Host 'Desktop library IPC health check passed.'
        Write-Host "New GameHelper instance is running (PID $($relaunched.Id))."
        if ($desktopInstances.Count -gt 0) {
            $relaunchedDesktop = Start-RestoredDesktop $desktopExe
            Start-Sleep -Seconds 3
            $relaunchedDesktop.Refresh()
            if ($relaunchedDesktop.HasExited -or $relaunchedDesktop.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Desktop did not open after update.' }
        }
    }
    $deployed = $true

    # Migrate retired passive-launcher shortcuts back to the WPF library entry.
    $launcherExe = Join-Path $publishDir 'GameHelper.Launcher.exe'
    try {
            $shortcutShell = New-Object -ComObject WScript.Shell
            $libraryShortcut = $shortcutShell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'GameHelper 游戏库.lnk'))
            $libraryShortcut.TargetPath = $desktopExe
            $libraryShortcut.WorkingDirectory = Split-Path -Parent $desktopExe
            $libraryShortcut.Description = 'GameHelper 游戏库'
            $libraryShortcut.IconLocation = $desktopExe + ',0'
            $libraryShortcut.Save()
            foreach ($desktop in @([Environment]::GetFolderPath('DesktopDirectory'), [Environment]::GetFolderPath('CommonDesktopDirectory')) | Select-Object -Unique) {
                foreach ($shortcutFile in Get-ChildItem -LiteralPath $desktop -Filter '*.lnk' -ErrorAction SilentlyContinue) {
                    $shortcut = $shortcutShell.CreateShortcut($shortcutFile.FullName)
                    if ($shortcut.TargetPath -eq $launcherExe -or $shortcut.TargetPath -eq (Join-Path $publishDir $exeName)) {
                        if ([string]::IsNullOrWhiteSpace($shortcut.IconLocation)) { $shortcut.IconLocation = (Join-Path $publishDir $exeName) + ',0' }
                        $shortcut.TargetPath = $desktopExe
                        $shortcut.Arguments = ''
                        $shortcut.WorkingDirectory = Split-Path -Parent $desktopExe
                        $shortcut.Save()
                        Write-Host "Updated desktop library entry: $($shortcutFile.Name)"
                    }
                }
            }
    }
    catch { Write-Warning "Deployment passed, but a desktop shortcut could not be updated: $_" }

    # Keep the backup until startup has passed. Cleanup failure is harmless after a
    # successful deploy and must not trigger rollback of a running new instance.
    if ($backupDir) {
        try { Remove-CheckedDirectory $backupDir $repoRoot }
        catch { Write-Warning "Deployment succeeded; old build retained at ${backupDir}: $_" }
    }
}
catch {
    $deployFailure = $_
    try {
        if ($newDirectoryInstalled) {
            if ($null -ne $relaunchedDesktop -and -not $relaunchedDesktop.HasExited) {
                Stop-Process -InputObject $relaunchedDesktop -Force
                $relaunchedDesktop.WaitForExit(30000) | Out-Null
            }
            if ($null -ne $relaunched -and -not $relaunched.HasExited) {
                Stop-Process -InputObject $relaunched -Force
                if (-not $relaunched.WaitForExit(30000)) { throw "Failed new instance did not exit; rollback directory remains at $backupDir" }
            }
            # Preserve the failed new build for diagnosis and restore the original
            # directory by a whole-directory move, without mixing individual files.
            Move-CheckedDirectory $publishDir $tempPublish $repoRoot $tempRoot
        }
        if ($backupDir -and (Test-Path -LiteralPath $backupDir)) {
            Move-CheckedDirectory $backupDir $publishDir $repoRoot $repoRoot
            Write-Host "Previous publish directory restored."
        }
        if ($stoppedOldInstance -and (Test-Path -LiteralPath (Join-Path $publishDir $exeName))) {
            $remainingInstances = @(Get-Process -Name GameHelper.ConsoleHost -ErrorAction SilentlyContinue | Where-Object {
                [string]::IsNullOrWhiteSpace($_.Path) -or $_.Path -eq (Join-Path $publishDir $exeName)
            })
            if ($remainingInstances.Count -eq 0) {
                $restored = $null
                $restored = Start-CheckedInstance $publishDir ([ref] $restored) -LegacyBackground:(-not $oldSupportsBackground)
                Write-Host "Previous GameHelper instance restored (PID $($restored.Id))."
            }
        }
        if ($desktopInstances.Count -gt 0 -and (Test-Path -LiteralPath $desktopExe)) {
            Start-RestoredDesktop $desktopExe | Out-Null
        }
    }
    catch { Write-Warning "Deployment rollback requires attention: $_. Previous build: $backupDir" }
    throw $deployFailure
}
finally {
    if ($deployed) {
        Write-Host "Deploy complete."
    }
    else {
        Write-Host "New build kept at: $tempPublish"
        Write-Host "Close the running GameHelper, then re-run: powershell -File scripts\verify-on-machine.ps1 -SkipVerify"
    }
}
