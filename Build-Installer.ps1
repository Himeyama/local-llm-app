[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,
    [string]$RocmRoot,
    [string]$HsisSource,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$arguments = @((Join-Path $PSScriptRoot 'Packaging\build_installer.py'))
if ($Version) { $arguments += @('--version', $Version) }
if ($RocmRoot) { $arguments += @('--rocm-root', $RocmRoot) }
if ($HsisSource) { $arguments += @('--hsis-source', $HsisSource) }
if ($SkipBuild) { $arguments += '--skip-build' }
& python @arguments
if ($LASTEXITCODE -ne 0) { throw 'インストーラーの作成に失敗しました。' }
