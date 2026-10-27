# Deploy health probe. Records what was deployed (commit, branch, dirty files) and proves the
# relaunched instance can actually work: a Low-integrity image silently loses %AppData% writes and
# the kernel ETW session, and "PID alive + window visible" does not catch either.
param(
    [Parameter(Mandatory = $true)][string] $Directory,
    [string] $RepoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path,
    [int] $ProcessId = 0,
    [switch] $AllowDegradedMonitor
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-DeployManifest {
    param([string] $PublishDirectory, [string] $Root)

    $commit = $null
    $branch = $null
    $dirty = @()
    try {
        $commit = (& git -C $Root rev-parse HEAD 2>$null | Select-Object -First 1)
        $branch = (& git -C $Root rev-parse --abbrev-ref HEAD 2>$null | Select-Object -First 1)
        $dirty = @(& git -C $Root status --short 2>$null)
    }
    catch {
        Write-Warning "Could not read the git state for the deploy manifest: $($_.Exception.Message)"
    }

    $exe = Join-Path $PublishDirectory 'GameHelper.ConsoleHost.exe'
    $version = $null
    if (Test-Path -LiteralPath $exe) {
        $version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    }

    $manifest = [ordered]@{
        deployedAtUtc  = (Get-Date).ToUniversalTime().ToString('o')
        commit         = $commit
        branch         = $branch
        productVersion = $version
        startupMode    = 'background'
        dirtyFiles     = @($dirty)
    }
    $path = Join-Path $PublishDirectory 'deploy-manifest.json'
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $path -Encoding UTF8

    if ($dirty.Count -gt 0) {
        Write-Warning "Working tree has $($dirty.Count) uncommitted change(s); the deployed build may not match commit $commit (see $path)."
        foreach ($line in $dirty) { Write-Host "    $line" }
    }
}

function Get-ProcessIntegrityLevel {
    param([int] $TargetProcessId)

    if (-not ('GameHelper.DeployIntegrityProbe' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace GameHelper
{
    public static class DeployIntegrityProbe
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int returned);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr text);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr handle);

        public static string Get(int pid)
        {
            IntPtr process = OpenProcess(0x1000, false, pid);
            if (process == IntPtr.Zero) { return null; }
            IntPtr token;
            if (!OpenProcessToken(process, 0x8, out token)) { CloseHandle(process); return null; }
            IntPtr buffer = Marshal.AllocHGlobal(512);
            int returned;
            string result = null;
            if (GetTokenInformation(token, 25, buffer, 512, out returned))
            {
                IntPtr sid = Marshal.ReadIntPtr(buffer);
                IntPtr text;
                if (ConvertSidToStringSid(sid, out text)) { result = Marshal.PtrToStringUni(text); LocalFree(text); }
            }
            Marshal.FreeHGlobal(buffer);
            CloseHandle(token);
            CloseHandle(process);
            return result;
        }
    }
}
"@
    }

    return [GameHelper.DeployIntegrityProbe]::Get($TargetProcessId)
}

function Test-EtwSession {
    param([int] $TargetProcessId, [int] $TimeoutSeconds = 10)

    $prefix = 'GameHelper-ETW-' + $TargetProcessId + '-'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        $sessions = @(& logman.exe query -ets 2>$null)
        foreach ($line in $sessions) {
            if ($line -like ('*' + $prefix + '*')) { return $true }
        }
        if ((Get-Date) -ge $deadline) { return $false }
        Start-Sleep -Milliseconds 500
    }
}

function Resolve-InstanceProcess {
    param([int] $TargetProcessId, [string] $PublishDirectory)

    $process = Get-Process -Id $TargetProcessId -ErrorAction SilentlyContinue
    if ($null -ne $process) { return $process }

    # The launched bootstrap may exit after handing off to the background runtime, so the PID
    # captured at launch can be gone by the time the probe runs. Fall back to the newest instance
    # of the deployed executable; a genuine crash still yields null because nothing replaces it.
    $exePath = Join-Path $PublishDirectory 'GameHelper.ConsoleHost.exe'
    return Get-Process -Name 'GameHelper.ConsoleHost' -ErrorAction SilentlyContinue |
        Where-Object {
            try { $_.Path -eq $exePath -and $_.StartTime -ge (Get-Date).AddMinutes(-2) } catch { $false }
        } |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
}

$failures = New-Object System.Collections.Generic.List[string]

Write-DeployManifest -PublishDirectory $Directory -Root $RepoRoot

$instance = $null
if ($ProcessId -gt 0) {
    $instance = Resolve-InstanceProcess -TargetProcessId $ProcessId -PublishDirectory $Directory
    if ($null -eq $instance) {
        $failures.Add("GameHelper (PID $ProcessId) exited before the health check and no replacement instance appeared.")
    }
    elseif ($instance.Id -ne $ProcessId) {
        Write-Host "Instance transferred from PID $ProcessId to PID $($instance.Id); checking the live process."
        $ProcessId = $instance.Id
    }
}

if ($null -ne $instance) {
    $integrity = $null
    try {
        $integrity = Get-ProcessIntegrityLevel -TargetProcessId $ProcessId
    }
    catch {
        Write-Warning "Integrity probe unavailable: $($_.Exception.Message)"
    }

    if ($null -ne $integrity) {
        $rid = [int]($integrity.Substring($integrity.LastIndexOf('-') + 1))
        if ($rid -lt 8192) {
            $failures.Add("GameHelper PID $ProcessId runs at Low integrity ($integrity): the published exe inherited a Low mandatory label. Run icacls on the publish directory with /setintegritylevel (OI)(CI)M /T, then redeploy.")
        }
    }
    else {
        Write-Warning "Could not read the integrity level of PID $ProcessId; skipping that check."
    }

    $dataRoot = if ([string]::IsNullOrWhiteSpace($env:GAMEHELPER_DATA_DIR)) { $env:APPDATA } else { $env:GAMEHELPER_DATA_DIR }
    $dataDirectory = Join-Path $dataRoot 'GameHelper'
    $probe = Join-Path $dataDirectory ('.deploy-probe-' + [guid]::NewGuid().ToString('N'))
    try {
        if (-not (Test-Path -LiteralPath $dataDirectory)) {
            New-Item -ItemType Directory -Path $dataDirectory -Force | Out-Null
        }
        [System.IO.File]::WriteAllText($probe, 'ok')
        [System.IO.File]::Delete($probe)
    }
    catch {
        $failures.Add("Data directory is not writable: $dataDirectory ($($_.Exception.Message))")
        try { if (Test-Path -LiteralPath $probe) { [System.IO.File]::Delete($probe) } } catch { }
    }

    $monitorMode = $null
    $configPath = Join-Path $dataDirectory 'config.yml'
    if (Test-Path -LiteralPath $configPath) {
        $match = Select-String -LiteralPath $configPath -Pattern '^\s*monitor\s*:\s*(\S+)' | Select-Object -First 1
        if ($null -ne $match) { $monitorMode = $match.Matches[0].Groups[1].Value.Trim('"', "'") }
    }

    if ($null -ne $monitorMode -and $monitorMode -ieq 'etw') {
        if (-not (Test-EtwSession -TargetProcessId $ProcessId)) {
            $message = "monitor: ETW is configured but PID $ProcessId owns no ETW session; the app fell back to WMI (degraded monitoring)."
            if ($AllowDegradedMonitor) {
                Write-Warning $message
            }
            else {
                $failures.Add($message + ' Fix ETW access, set monitor: WMI, or pass -AllowDegradedMonitor.')
            }
        }
    }
}

if ($failures.Count -gt 0) {
    $details = ($failures | ForEach-Object { '  - ' + $_ }) -join [Environment]::NewLine
    throw ("Deployed instance health check failed:" + [Environment]::NewLine + $details)
}

if ($null -ne $instance) {
    Write-Host "Deploy health check passed (integrity, data directory, ETW)."
}
else {
    Write-Host "Deploy manifest written."
}
