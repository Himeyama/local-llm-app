<#
.SYNOPSIS
Starts the project-local Open WebUI instance for the local llama-server.

.EXAMPLE
.\Start-OpenWebUI.ps1

.EXAMPLE
.\Start-OpenWebUI.ps1 -Port 3001
#>

[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateRange(1024, 65535)]
    [int]$Port = 3000
)

# Repair the venv base interpreter path when this repository has been moved.
$localPythonRoot = Join-Path $PSScriptRoot 'runtime\python'
$venvConfig = Join-Path $PSScriptRoot 'openwebui\.venv\pyvenv.cfg'
if ((Test-Path -LiteralPath (Join-Path $localPythonRoot 'python.exe')) -and (Test-Path -LiteralPath $venvConfig)) {
    $config = [System.IO.File]::ReadAllText($venvConfig)
    $config = [regex]::Replace($config, '(?m)^home = .*$', ('home = ' + $localPythonRoot))
    [System.IO.File]::WriteAllText($venvConfig, $config, [System.Text.UTF8Encoding]::new($false))
}

$openWebUiRoot = Join-Path $PSScriptRoot 'openwebui'
$openWebUiExe = Join-Path $openWebUiRoot '.venv\Scripts\python.exe'
$dataPath = Join-Path $openWebUiRoot 'data'

if (-not (Test-Path -LiteralPath $openWebUiExe -PathType Leaf)) {
    Write-Error "OpenWebUI が見つかりません: $openWebUiExe。README のインストール手順を実行してください。"
    exit 1
}

if (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    Write-Error "ポート $Port は既に使用中です。既存の OpenWebUI を停止するか、-Port で別のポートを指定してください。"
    exit 1
}

New-Item -ItemType Directory -Path $dataPath -Force | Out-Null

# Saved settings take precedence over environment defaults. Migrate only the
# former local llama.cpp endpoint; preserve all other settings and chat data.
$pythonExe = Join-Path $openWebUiRoot '.venv\Scripts\python.exe'
& $pythonExe (Join-Path $PSScriptRoot 'configure_openwebui.py') (Join-Path $dataPath 'webui.db')
if ($LASTEXITCODE -ne 0) {
    Write-Error 'Open WebUI の接続先設定を更新できませんでした。'
    exit $LASTEXITCODE
}

# Keep OpenWebUI local and persist its account, settings, and chat history in
# the project-local data directory. The OpenAI-compatible connection is
# preconfigured for the llama-server started by Start-LlamaServer.ps1.
$env:DATA_DIR = $dataPath
$env:HF_HOME = Join-Path $openWebUiRoot 'cache\huggingface'
$env:XDG_CACHE_HOME = Join-Path $openWebUiRoot 'cache'
$env:WEBUI_AUTH = 'False'
$env:OPENAI_API_BASE_URLS = 'http://127.0.0.1:9931/v1'
$env:OPENAI_API_KEY = ''
$env:OPENAI_API_KEYS = ''
$env:OPENAI_API_CONFIGS = '{"0":{"enable":true,"provider":"llama.cpp"}}'
$env:AIOHTTP_CLIENT_TIMEOUT_MODEL_LIST = '30'

Write-Host "OpenWebUI を起動します: http://127.0.0.1:$Port" -ForegroundColor Cyan
Write-Host 'llama-server の接続先: http://127.0.0.1:9931/v1' -ForegroundColor DarkCyan

$previousLocation = Get-Location
try {
    # Keep the generated .webui_secret_key beside the ignored OpenWebUI data.
    Set-Location -LiteralPath $openWebUiRoot
    & $pythonExe -c 'from open_webui import app; app()' serve --host '127.0.0.1' --port ([string]$Port)
    exit $LASTEXITCODE
}
finally {
    Set-Location -LiteralPath $previousLocation
}
