param(
    [string] $Configuration = "Release",
    [string] $Runtime = "win-x64",
    [bool] $SelfContained = $false,
    [switch] $SkipPublish
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectPath = Join-Path $repoRoot "GameHelper.ConsoleHost\GameHelper.ConsoleHost.csproj"
$projectDir = Split-Path -Parent $projectPath
$projectXml = [xml](Get-Content -Path $projectPath -Raw)
$targetFramework = $projectXml.Project.PropertyGroup |
    Where-Object { $_.TargetFramework } |
    Select-Object -First 1 -ExpandProperty TargetFramework

if ([string]::IsNullOrWhiteSpace($targetFramework)) {
    throw "Unable to resolve TargetFramework from $projectPath."
}

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }
$tempDir = Join-Path ([IO.Path]::GetTempPath()) ("GameHelper.PublishSmoke." + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDir | Out-Null
$previousSingleInstanceEnv = [Environment]::GetEnvironmentVariable("GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE")

$publishDir = if ($SkipPublish) {
    Join-Path $projectDir "bin\$Configuration\$targetFramework\$Runtime\publish"
}
else {
    Join-Path $tempDir "publish"
}

if (-not $SkipPublish) {
    Write-Host "Publishing ConsoleHost ($Configuration, $Runtime, self-contained=$selfContainedValue)..."
    & dotnet publish $projectPath -c $Configuration -r $Runtime --self-contained $selfContainedValue -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }
}
else {
    Write-Host "Skipping publish; validating existing ConsoleHost publish output."
}

try {
    $exePath = Join-Path $publishDir "GameHelper.ConsoleHost.exe"
    if (-not (Test-Path $exePath)) {
        throw "Published executable not found: $exePath"
    }

    $configPath = Join-Path $tempDir "config.yml"
    @"
monitor: ETW
startup:
  autoStartMonitor: false
  launchOnStartup: false
games:
  - dataKey: smoke_granblue
    executable: smoke.exe
    displayName: "Granblue Fantasy: Relink"
    enabled: true
    hdr: false
"@ | Set-Content -Path $configPath -Encoding UTF8

    Write-Host "Running validate-config against published executable..."
    $env:GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE = "1"
    $validateOutput = & $exePath validate-config --config $configPath 2>&1
    $validateExitCode = $LASTEXITCODE
    $validateText = $validateOutput -join "`n"
    Write-Host $validateText

    if ($validateExitCode -ne 0) {
        throw "validate-config failed with exit code $validateExitCode."
    }

    if ($validateText -notmatch "Config is valid\.") {
        throw "validate-config did not report success."
    }

    if ($validateText -notlike "*Validating: $configPath*") {
        throw "validate-config did not validate the smoke config path."
    }

    Write-Host "Running config list against published executable..."
    $listOutput = & $exePath config list --config $configPath 2>&1
    $listExitCode = $LASTEXITCODE
    $listText = $listOutput -join "`n"
    Write-Host $listText

    if ($listExitCode -ne 0) {
        throw "config list failed with exit code $listExitCode."
    }

    if ($listText -notmatch "smoke_granblue") {
        throw "config list did not include the smoke dataKey."
    }

    if ($listText -notmatch "DisplayName=Granblue Fantasy: Relink") {
        throw "config list did not preserve the smoke displayName."
    }

    Write-Host "ConsoleHost publish smoke passed."
}
finally {
    [Environment]::SetEnvironmentVariable("GAMEHELPER_CONSOLEHOST_DISABLE_SINGLE_INSTANCE", $previousSingleInstanceEnv)
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}
