[CmdletBinding()]
param([string]$PythonExe = 'python')
$ErrorActionPreference = 'Stop'
# Repair the venv base interpreter path when this repository has been moved.
$localPythonRoot = Join-Path $PSScriptRoot 'runtime\python'
$venvConfig = Join-Path $PSScriptRoot 'openwebui\.venv\pyvenv.cfg'
if ((Test-Path -LiteralPath (Join-Path $localPythonRoot 'python.exe')) -and (Test-Path -LiteralPath $venvConfig)) {
    $config = [System.IO.File]::ReadAllText($venvConfig)
    $config = [regex]::Replace($config, '(?m)^home = .*$', ('home = ' + $localPythonRoot))
    [System.IO.File]::WriteAllText($venvConfig, $config, [System.Text.UTF8Encoding]::new($false))
}

$venv = Join-Path $PSScriptRoot 'openwebui\.venv'
$localPython = Join-Path $PSScriptRoot 'runtime\python\python.exe'
if ($PythonExe -eq 'python' -and (Test-Path -LiteralPath $localPython)) { $PythonExe = $localPython }
if (-not (Test-Path -LiteralPath (Join-Path $venv 'Scripts\python.exe'))) {
    & $PythonExe -m venv $venv
    if ($LASTEXITCODE -ne 0) { throw 'Python 3.11 で仮想環境を作成してください。' }
}
& (Join-Path $venv 'Scripts\python.exe') -m pip install 'open-webui==0.11.3'
if ($LASTEXITCODE -ne 0) { throw 'Open WebUI のインストールに失敗しました。' }
