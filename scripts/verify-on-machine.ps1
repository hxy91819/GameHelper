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
    [switch] $Restart
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
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

function Start-CheckedInstance([string] $Directory, [ref] $StartedProcess) {
    $instance = Start-Process -FilePath (Join-Path $Directory "GameHelper.ConsoleHost.exe") `
        -WorkingDirectory $Directory -WindowStyle Hidden -PassThru
    $StartedProcess.Value = $instance
    Start-Sleep -Seconds 5
    $instance.Refresh()
    if ($instance.HasExited) {
        throw "GameHelper exited immediately after launch (PID $($instance.Id)); check its logs."
    }
    return $instance
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
  autoStartMonitor: false
  launchOnStartup: false
games:
  - dataKey: verify_smoke
    executable: verify_smoke.exe
    displayName: "Verify Smoke Game"
    enabled: true
    hdr: false
"@ | Set-Content -Path $configPath -Encoding UTF8

    $previousDataDirectory = $env:GAMEHELPER_DATA_DIR
    $previousDisableSingleInstance = $env:GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE
    $env:GAMEHELPER_DATA_DIR = $sandbox
    $env:GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE = "1"
    try {
        $validateOutput = & $tempExe validate-config --config $configPath 2>&1
        if ($LASTEXITCODE -ne 0 -or (($validateOutput -join "`n") -notmatch "Config is valid\.")) {
            throw "Test instance validate-config failed:`n$($validateOutput -join "`n")"
        }

        $listOutput = & $tempExe config list --config $configPath 2>&1
        if ($LASTEXITCODE -ne 0 -or (($listOutput -join "`n") -notmatch "verify_smoke")) {
            throw "Test instance config list failed:`n$($listOutput -join "`n")"
        }

        Write-Host "Test instance verification passed (data dir sandboxed at $sandbox)."
    }
    finally {
        $env:GAMEHELPER_DATA_DIR = $previousDataDirectory
        $env:GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE = $previousDisableSingleInstance
        Remove-CheckedDirectory $sandbox $tempRoot
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
try {
    $instances = @(Get-Process -Name GameHelper.ConsoleHost -ErrorAction SilentlyContinue)
    $unknownInstances = @($instances | Where-Object { [string]::IsNullOrWhiteSpace($_.Path) })
    if ($unknownInstances.Count -gt 0) {
        throw "Cannot verify the executable path of GameHelper PID(s) $($unknownInstances.Id -join ', '); preserving all instances and the existing deployment."
    }
    $runningInstances = @($instances | Where-Object { $_.Path -eq (Join-Path $publishDir $exeName) })
    if ($runningInstances.Count -gt 0 -and -not $Restart) {
        Write-Host "Publish directory is in use by GameHelper PID(s) $($runningInstances.Id -join ', '); keeping the existing deployment."
        return
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
    if ($Restart) {
        Write-Host "Launching GameHelper from the updated publish directory..."
        $relaunched = Start-CheckedInstance $publishDir ([ref] $relaunched)
        Write-Host "New GameHelper instance is running (PID $($relaunched.Id))."
    }
    $deployed = $true

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
                $restored = Start-CheckedInstance $publishDir ([ref] $restored)
                Write-Host "Previous GameHelper instance restored (PID $($restored.Id))."
            }
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
