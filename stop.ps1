$proc = Get-Process -Name "GameHelper.ConsoleHost" -ErrorAction SilentlyContinue
if ($proc) {
    $proc | Stop-Process -Force
    Write-Host "Stopped GameHelper.ConsoleHost (PID: $($proc.Id -join ', '))"
} else {
    Write-Host "GameHelper.ConsoleHost is not running."
}
