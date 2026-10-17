# GameHelper 实机验证 workflow（开发完成后执行）
#
# 前置条件：
#   - 用户可能正开着 GameHelper 实例（快捷方式指向 publish 目录），绝不能停止或杀死它。
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

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "GameHelper.ConsoleHost\GameHelper.ConsoleHost.csproj"
$publishDir = Join-Path $repoRoot "GameHelper.ConsoleHost\bin\$Configuration\net8.0-windows\$Runtime\publish"

# ---- 1. Publish to a standalone temp directory (never locked by a running instance) ----
$tempPublish = Join-Path ([IO.Path]::GetTempPath()) ("GameHelper.Verify." + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempPublish | Out-Null

Write-Host "[1/3] Publishing ConsoleHost to temp directory..."
& dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained true -o $tempPublish --nologo
if ($LASTEXITCODE -ne 0) {
    Remove-Item -Path $tempPublish -Recurse -Force -ErrorAction SilentlyContinue
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$exeName = "GameHelper.ConsoleHost.exe"
$tempExe = Join-Path $tempPublish $exeName
if (-not (Test-Path $tempExe)) {
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
        Remove-Item Env:GAMEHELPER_DATA_DIR -ErrorAction SilentlyContinue
        Remove-Item Env:GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE -ErrorAction SilentlyContinue
        Remove-Item -Path $sandbox -Recurse -Force -ErrorAction SilentlyContinue
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
$relaunchAfterDeploy = $false
try {
    if (Test-Path $publishDir) {
        # A running instance holds open handles on its exe/dll files; renaming the directory
        # would fail or yank files from under it. Detect by process path, not by probing the
        # directory (the directory itself stays writable while the exe is locked).
        $runningInstances = @(Get-Process -Name GameHelper.ConsoleHost -ErrorAction SilentlyContinue | Where-Object {
            # An elevated instance may hide its path from this verifier. Preserve it conservatively.
            [string]::IsNullOrWhiteSpace($_.Path) -or $_.Path -eq (Join-Path $publishDir "GameHelper.ConsoleHost.exe")
        })

        if ($runningInstances.Count -gt 0) {
            if (-not $Restart) {
                Write-Host "Publish directory is in use by a running GameHelper (PID list below); keeping new build at:"
                Write-Host "  $tempPublish"
                foreach ($inst in $runningInstances) {
                    Write-Host ("  running instance: PID " + $inst.Id)
                }
                Write-Host 'Close it, then re-run: powershell -File scripts\verify-on-machine.ps1 -SkipVerify'
                $deployed = $false
            }
            else {
                # Standing direct-update policy: stop the instance, clean orphaned ETW sessions,
                # swap the directory atomically, and relaunch. In-flight game sessions survive
                # the restart via the in-flight snapshot recovery mechanism.
                Write-Host "Direct update (-Restart): stopping running GameHelper instance(s); in-flight sessions are recovered on relaunch."
                foreach ($inst in $runningInstances) {
                    Write-Host ("  stopping PID " + $inst.Id)
                    Stop-Process -Id $inst.Id -Force
                }

                $deadline = (Get-Date).AddSeconds(30)
                do {
                    Start-Sleep -Milliseconds 500
                    $runningInstances = @(Get-Process -Name GameHelper.ConsoleHost -ErrorAction SilentlyContinue | Where-Object {
                        [string]::IsNullOrWhiteSpace($_.Path) -or $_.Path -eq (Join-Path $publishDir "GameHelper.ConsoleHost.exe")
                    })
                } while ($runningInstances.Count -gt 0 -and (Get-Date) -lt $deadline)
                if ($runningInstances.Count -gt 0) {
                    throw "Running GameHelper instance(s) did not exit within 30s; aborting deploy."
                }

                # Force-killed ETW sessions do not die with the process; stop orphans by name.
                $logmanList = & logman.exe -ets 2>$null
                foreach ($line in @($logmanList)) {
                    if ($line -match '^(GameHelper-ETW-\S+)') {
                        Write-Host ("  stopping orphaned ETW session: " + $Matches[1])
                        & logman.exe stop $Matches[1] -ets 2>$null | Out-Null
                    }
                }

                $backupDir = "$publishDir.old-" + [Guid]::NewGuid().ToString("N")
                Move-Item -Path $publishDir -Destination $backupDir -Force
                try {
                    # $publishDir no longer exists, so this moves the temp dir to that exact path.
                    Move-Item -Path $tempPublish -Destination $publishDir -Force
                    $deployed = $true
                    $relaunchAfterDeploy = $true
                    Remove-Item -Path $backupDir -Recurse -Force -ErrorAction SilentlyContinue
                }
                catch {
                    # Roll the old directory back if the swap of the new one fails.
                    Move-Item -Path $backupDir -Destination $publishDir -Force
                    throw
                }
            }
        }
        else {
            $backupDir = "$publishDir.old-" + [Guid]::NewGuid().ToString("N")
            Move-Item -Path $publishDir -Destination $backupDir -Force
            try {
                # $publishDir no longer exists, so this moves the temp dir to that exact path.
                Move-Item -Path $tempPublish -Destination $publishDir -Force
                $deployed = $true
                Remove-Item -Path $backupDir -Recurse -Force -ErrorAction SilentlyContinue
            }
            catch {
                # Roll the old directory back if the swap of the new one fails.
                Move-Item -Path $backupDir -Destination $publishDir -Force
                throw
            }
        }
    }
    else {
        New-Item -ItemType Directory -Path (Split-Path -Parent $publishDir) -Force | Out-Null
        Move-Item -LiteralPath $tempPublish -Destination $publishDir
        $deployed = $true
    }
}
finally {
    if ($deployed) {
        Remove-Item -Path $tempPublish -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Deploy complete."
    }
    else {
        Write-Host "New build kept at: $tempPublish"
        Write-Host "Close the running GameHelper, then re-run: powershell -File scripts\verify-on-machine.ps1 -SkipVerify"
    }
}

if ($relaunchAfterDeploy) {
    # Launch without capturing stdout (a headless pipe-captured launch hangs at startup).
    Write-Host "Relaunching GameHelper from the publish directory..."
    $relaunched = Start-Process -FilePath (Join-Path $publishDir $exeName) -WorkingDirectory $publishDir -PassThru
    Start-Sleep -Seconds 5
    if ($relaunched.HasExited) {
        throw "Relaunched GameHelper exited immediately (PID $($relaunched.Id)); check its logs."
    }
    Write-Host ("New GameHelper instance is running (PID " + $relaunched.Id + ").")
}
