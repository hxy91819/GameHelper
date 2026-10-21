# Interactive launch and visibility verification, shared by deployment and published tests.
# Function-only script: importing it never publishes, deploys, or starts a process.
function Start-CheckedInstance([string] $Directory, [ref] $StartedProcess,
    [string] $ConsoleProbeScript = (Join-Path $PSScriptRoot 'probe-console-window.ps1')) {
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
