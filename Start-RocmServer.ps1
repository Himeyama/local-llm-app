<#
.SYNOPSIS
Starts the independent Rust/HIP text inference server (no llama.cpp).
#>
[CmdletBinding()]
param(
    [string]$ModelPath = 'models\Qwen3.8-27B-Uncensored\Qwen3.8-27B-Uncensored-Q4_K_M.gguf',
    [string]$RocmBin = $env:LOCAL_ROCM_BIN,
    [ValidateRange(65536, 262144)]
    [int]$ContextSize = 131072,
    [switch]$LanAccess,
    [switch]$Build
)
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($ModelPath)) { $ModelPath = Join-Path $PSScriptRoot $ModelPath }
if ([string]::IsNullOrWhiteSpace($RocmBin)) { $RocmBin = Join-Path $PSScriptRoot 'runtime\rocm\bin' }
if (-not [IO.Path]::IsPathRooted($RocmBin)) { $RocmBin = Join-Path $PSScriptRoot $RocmBin }
if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) { throw "GGUF model not found: $ModelPath" }
if (-not (Test-Path -LiteralPath (Join-Path $RocmBin 'amdhip64_7.dll') -PathType Leaf)) { throw "ROCm 7 SDK not found: $RocmBin. Set -RocmBin or LOCAL_ROCM_BIN." }
$manifest = Join-Path $PSScriptRoot 'NativeInference\Cargo.toml'
$builtExecutable = Join-Path $PSScriptRoot 'NativeInference\target\release\local-rocm.exe'
$executable = Join-Path $PSScriptRoot 'runtime\native\local-rocm.exe'
if ($Build) {
    $cargo = Get-Command cargo -ErrorAction SilentlyContinue
    $cargoPath = if ($cargo) { $cargo.Source } else { Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe' }
    & $cargoPath build --locked --release --manifest-path $manifest
    if ($LASTEXITCODE -ne 0) { throw 'Rust build failed.' }
    $executable = $builtExecutable
}
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { $executable = $builtExecutable }
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Native executable not found. Run this script with -Build first.' }
$listenAddress = if ($LanAccess) { '0.0.0.0' } else { '127.0.0.1' }
& $executable serve $ModelPath $RocmBin ([string]$ContextSize) $listenAddress '9931'
exit $LASTEXITCODE
