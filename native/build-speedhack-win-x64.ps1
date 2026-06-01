param(
    [string]$ToolchainBin
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$src = Join-Path $repoRoot 'native/src/GameHelper.Speedhack.c'
$outDir = Join-Path $repoRoot 'native/win-x64'
$outDll = Join-Path $outDir 'GameHelper.Speedhack.dll'
$outLib = Join-Path $outDir 'GameHelper.Speedhack.lib'

if (-not $ToolchainBin) {
    $candidates = @(
        "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\MartinStorsjo.LLVM-MinGW.UCRT_Microsoft.Winget.Source_8wekyb3d8bbwe\llvm-mingw-20260224-ucrt-x86_64\bin",
        "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\MartinStorsjo.LLVM-MinGW.MSVCRT_Microsoft.Winget.Source_8wekyb3d8bbwe\llvm-mingw-20260224-msvcrt-x86_64\bin"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path (Join-Path $candidate 'x86_64-w64-mingw32-gcc.exe')) {
            $ToolchainBin = $candidate
            break
        }
    }
}

if (-not $ToolchainBin) {
    throw 'Unable to locate MinGW toolchain. Set -ToolchainBin to the folder containing x86_64-w64-mingw32-gcc.exe.'
}

$gcc = Join-Path $ToolchainBin 'x86_64-w64-mingw32-gcc.exe'
if (-not (Test-Path $gcc)) {
    throw "Compiler not found: $gcc"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$include1 = Join-Path $repoRoot 'native/third_party/minhook/include'
$include2 = Join-Path $repoRoot 'native/third_party/minhook/src'
$include3 = Join-Path $repoRoot 'native/third_party/minhook/src/hde'

$buffer = Join-Path $repoRoot 'native/third_party/minhook/src/buffer.c'
$hook = Join-Path $repoRoot 'native/third_party/minhook/src/hook.c'
$trampoline = Join-Path $repoRoot 'native/third_party/minhook/src/trampoline.c'
$hde64 = Join-Path $repoRoot 'native/third_party/minhook/src/hde/hde64.c'

$args = @(
    '-std=c11',
    '-O2',
    '-D_WIN32_WINNT=0x0601',
    '-I', $include1,
    '-I', $include2,
    '-I', $include3,
    $src,
    $buffer,
    $hook,
    $trampoline,
    $hde64,
    '-shared',
    '-o', $outDll,
    "-Wl,--out-implib,$outLib",
    '-lpsapi',
    '-lwinmm'
)

& $gcc @args
if ($LASTEXITCODE -ne 0) {
    throw "Native build failed with exit code $LASTEXITCODE"
}

Write-Host "Built: $outDll"
