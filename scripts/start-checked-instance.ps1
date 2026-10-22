# Interactive launch and visibility verification, shared by deployment and published tests.
# Function-only script: importing it never publishes, deploys, or starts a process.
# Default startup is windowless; -Interactive is only for explicit legacy UI verification.
function Start-RestoredDesktop([string] $Executable) {
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Executable
    $start.Arguments = '--restore'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $instance = [Diagnostics.Process]::Start($start)
    # A GUI must not retain the deployment caller's captured output handles.
    $instance.StandardInput.Close()
    $instance.StandardOutput.Close()
    $instance.StandardError.Close()
    return $instance
}

function Start-CheckedInstance([string] $Directory, [ref] $StartedProcess,
    [string] $ConsoleProbeScript = (Join-Path $PSScriptRoot 'probe-console-window.ps1'), [switch] $Interactive, [switch] $LegacyBackground) {
    if ($Interactive) { return Start-CheckedInteractiveInstance $Directory $StartedProcess $ConsoleProbeScript }
    if ($LegacyBackground) {
        # Rollback may restore a release predating the WPF background entry.
        $legacy = Start-Process -FilePath (Join-Path $Directory 'GameHelper.ConsoleHost.exe') -ArgumentList @('monitor', '--background') `
            -WorkingDirectory $Directory -WindowStyle Hidden -PassThru `
            -RedirectStandardOutput (Join-Path $Directory 'rollback-runtime.log') -RedirectStandardError (Join-Path $Directory 'rollback-runtime-error.log')
        $StartedProcess.Value = $legacy
        Start-Sleep -Seconds 5
        if ($legacy.HasExited) { throw 'Restored legacy runtime exited before readiness.' }
        return $legacy
    }
    $desktop = Join-Path $Directory 'desktop\GameHelper.Desktop.exe'
    $resultFile = Join-Path ([IO.Path]::GetTempPath()) ('GameHelper.Background.' + [guid]::NewGuid().ToString('N') + '.json')
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $desktop
    $start.Arguments = '--background --check "' + $resultFile + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $broker = [Diagnostics.Process]::Start($start)
    try {
        if (-not $broker.WaitForExit(35000)) { $broker.Kill(); throw 'Background runtime startup timed out.' }
        if ($broker.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $resultFile)) { throw 'Background runtime startup failed; see GameHelper.Desktop-error.txt and config.yml.runtime.log.' }
        $result = Get-Content -LiteralPath $resultFile -Raw | ConvertFrom-Json
        if (-not $result.Success -or $result.RuntimeProcessId -le 0) { throw 'Background runtime IPC is unavailable.' }
        $instance = Get-Process -Id $result.RuntimeProcessId -ErrorAction Stop
        if ($instance.Path -ne (Join-Path $Directory 'GameHelper.ConsoleHost.exe')) { throw 'Background runtime returned an unexpected executable path.' }
        $StartedProcess.Value = $instance
        $probe = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $ConsoleProbeScript -TargetProcessId $instance.Id -RequireNoConsole
        if ($LASTEXITCODE -ne 0) { throw "Background runtime unexpectedly has a console: $probe" }
        Write-Host "Background runtime ready without a console (PID $($instance.Id))."
        return $instance
    }
    finally {
        $broker.Dispose()
        if (Test-Path -LiteralPath $resultFile) { Remove-Item -LiteralPath $resultFile -Force }
    }
}

function Start-CheckedInteractiveInstance([string] $Directory, [ref] $StartedProcess, [string] $ConsoleProbeScript) {
    # This is the user's interactive console, which must remain visible after deployment.
    $exePath = Join-Path $Directory "GameHelper.ConsoleHost.exe"
    $launchedAt = (Get-Date).AddSeconds(-1)
    $instance = Start-Process -FilePath $exePath `
        -WorkingDirectory $Directory -WindowStyle Normal -PassThru
    Start-Sleep -Seconds 2
    $instance.Refresh()
    if ($instance.HasExited) {
        # 宿主转移到 Windows Terminal 时先拉起的进程会立刻退出：按"同路径 + 启动时间"接管
        # 随后出现的真实交互实例，后续可见性探测针对新 PID 进行。
        $deadline = (Get-Date).AddSeconds(20)
        $instance = $null
        while ($null -eq $instance -and (Get-Date) -lt $deadline) {
            Start-Sleep -Milliseconds 500
            $instance = Get-Process -Name GameHelper.ConsoleHost -ErrorAction SilentlyContinue | Where-Object {
                try { $_.Path -eq $exePath -and $_.StartTime -ge $launchedAt } catch { $false }
            } | Sort-Object StartTime -Descending | Select-Object -First 1
        }
        if ($null -eq $instance) {
            throw "No ConsoleHost appeared after launch; check its logs."
        }
        Write-Host "Interactive ConsoleHost identified (PID $($instance.Id))."
    }
    $StartedProcess.Value = $instance
    Start-Sleep -Seconds 5
    $instance.Refresh()
    if ($instance.HasExited) {
        throw "GameHelper exited immediately after launch (PID $($instance.Id)); check its logs."
    }
    $visibility = & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $ConsoleProbeScript -TargetProcessId $instance.Id
    if ($LASTEXITCODE -ne 0) {
        throw "GameHelper has no visible interactive console (PID $($instance.Id)); startup verification failed: $visibility"
    }
    Write-Host "Interactive console visibility verified for PID $($instance.Id)."
    return $instance
}
