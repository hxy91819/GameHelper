param(
    [switch]$TreatWarningsAsErrors = $false
)

$ErrorActionPreference = "Stop"

Write-Host "Building GameHelper.sln with analyzers..." -ForegroundColor Cyan

$extraArgs = @()
if ($TreatWarningsAsErrors) {
    $extraArgs += "/p:TreatWarningsAsErrors=true"
    Write-Host "  TreatWarningsAsErrors: ON (warnings will fail the build)" -ForegroundColor Yellow
} else {
    Write-Host "  TreatWarningsAsErrors: OFF (warnings reported but non-blocking)" -ForegroundColor DarkGray
}

$buildOutput = dotnet build GameHelper.sln 2>&1
$exitCode = $LASTEXITCODE

$warnings = $buildOutput | Where-Object {
    $_ -match "warning (CA1801|CA1823|IDE0051|IDE0052|IDE0059):"
}

if ($warnings.Count -gt 0) {
    Write-Host ""
    Write-Host "=== Lint: Actionable Analyzer Warnings ===" -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host $_ -ForegroundColor Yellow }
    Write-Host ""
    Write-Host "Found $($warnings.Count) actionable warning(s). Fix or suppress in .globalanalyzerconfig." -ForegroundColor Yellow
    if ($TreatWarningsAsErrors) {
        Write-Host "FAIL: Warnings treated as errors." -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host ""
    Write-Host "Lint: No actionable analyzer warnings found." -ForegroundColor Green
}

if ($exitCode -ne 0) {
    Write-Host "FAIL: Build errors detected." -ForegroundColor Red
    exit $exitCode
}

Write-Host "Running tests..." -ForegroundColor Cyan
dotnet test GameHelper.sln --no-build 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: Tests failed." -ForegroundColor Red
    exit 1
}

Write-Host "PASS: Lint check complete." -ForegroundColor Green
exit 0