# Windowless launch and readiness verification, shared by deployment and published tests.
# Function-only script: importing it never publishes, deploys, or starts a process.
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
    [string] $ConsoleProbeScript = (Join-Path $PSScriptRoot 'probe-console-window.ps1'), [switch] $LegacyBackground) {
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
