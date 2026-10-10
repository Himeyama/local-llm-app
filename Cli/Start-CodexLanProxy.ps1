[CmdletBinding()]
param([ValidateRange(1, 65535)][int]$Port = 8087)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Initialize-CliEnvironment.ps1')
$nodeCommand = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $nodeCommand) { throw 'Codex の LAN 接続には Node.js が必要です。Node.js をインストールして再試行してください。' }
$env:LOCAL_CODEX_PROXY_HOST = '0.0.0.0'
$env:LOCAL_CODEX_PROXY_PORT = [string]$Port
$env:LOCAL_LLAMA_SERVER_URL = 'http://127.0.0.1:9931'
& $nodeCommand.Source (Join-Path $PSScriptRoot 'LocalCodexNormalizeProxy.mjs')
exit $LASTEXITCODE
