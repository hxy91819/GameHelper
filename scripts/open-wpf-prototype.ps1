# Builds and opens the independent read-only WPF experiment. No production host is stopped.
param([switch] $Demo, [string] $ConfigPath, [string] $SnapshotDirectory)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$project = Join-Path $repoRoot 'prototypes\GameHelper.WpfPrototype\GameHelper.WpfPrototype.csproj'
# Each run publishes a complete independent directory, never overwriting a live deployment.
$destination = Join-Path $repoRoot ('artifacts\wpf-prototype\' + [guid]::NewGuid().ToString('N'))
& dotnet publish $project -c Release -r win-x64 --self-contained false -o $destination --nologo
if ($LASTEXITCODE -ne 0) { throw 'WPF prototype publish failed.' }
& icacls $destination /setintegritylevel '(OI)(CI)M' /T /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not normalize prototype integrity labels.' }
$exe = Join-Path $destination 'GameHelper.WpfPrototype.exe'
$arguments = @()
foreach ($value in @($ConfigPath, $SnapshotDirectory)) {
    if ($value -and ($value.Contains('"') -or $value.EndsWith('\'))) {
        throw 'Paths must not contain a quotation mark or end in a backslash.'
    }
}
if ($Demo) { $arguments += '--demo' }
if ($ConfigPath) { $arguments += @('--config', ('"' + $ConfigPath + '"')) }
if ($SnapshotDirectory) { $arguments += @('--snapshot', ('"' + $SnapshotDirectory + '"')) }
$launch = @{ FilePath = $exe; PassThru = $true }
if ($arguments.Count -gt 0) { $launch.ArgumentList = $arguments }
if ($SnapshotDirectory) { $launch.Wait = $true }
# This window is explicitly requested for evaluating the prototype.
$instance = Start-Process @launch
Write-Output "WPF prototype: $exe (PID $($instance.Id))"
if ($SnapshotDirectory -and $instance.ExitCode -ne 0) { throw "WPF preview verification failed: $SnapshotDirectory" }
