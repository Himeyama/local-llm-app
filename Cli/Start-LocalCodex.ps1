<#
.SYNOPSIS
Starts OpenAI Codex CLI against the local llama-server instance.

.EXAMPLE
.\Start-LocalCodex.ps1

.EXAMPLE
.\Start-LocalCodex.ps1 "このプロジェクトを要約して"

.EXAMPLE
.\Start-LocalCodex.ps1 -p "このプロジェクトを要約して"

.EXAMPLE
.\Start-LocalCodex.ps1 -Capture
#>

$Capture = $args -contains '-Capture'
$CodexArgs = @($args | Where-Object { $_ -ne '-Capture' })
$ChildInteractive = $env:LOCAL_CODEX_CHILD -eq '1'
$Capture = $Capture -or $env:LOCAL_CODEX_CAPTURE -eq '1'
Remove-Item Env:LOCAL_CODEX_CHILD -ErrorAction SilentlyContinue
Remove-Item Env:LOCAL_CODEX_CAPTURE -ErrorAction SilentlyContinue

# llama-server exposes the OpenAI-compatible Responses API at
# /v1/responses (codex 0.157+ no longer accepts wire_api = "chat"). The local
# normalization proxy rewrites Codex's system prompt the same way the Claude
# launcher's proxy does.
$normalizeProxyUrl = 'http://127.0.0.1:8084'
$captureProxyUrl = 'http://127.0.0.1:8085'
$captureWebUrl = 'http://127.0.0.1:8086/?token=local'
$apiBaseUrl = $normalizeProxyUrl
$env:OPENAI_API_KEY = 'local'

try {
    $serverProps = Invoke-RestMethod -Uri 'http://127.0.0.1:9931/props' -TimeoutSec 3
    $serverContextTokens = [int]$serverProps.default_generation_settings.n_ctx
}
catch {
    Write-Error 'llama-server (http://127.0.0.1:9931) に接続できません。先に llama-server を起動してください。'
    exit 1
}

if (-not $serverProps.modalities.vision) {
    Write-Error 'llama-server の画像入力が無効です。対応する mmproj を指定して llama-server を再起動してください。'
    exit 1
}

$loadedModelPath = [string]$serverProps.model_path
if ([string]::IsNullOrWhiteSpace($loadedModelPath)) {
    $loadedModelPath = [string]$serverProps.model_alias
}
if ([string]::IsNullOrWhiteSpace($loadedModelPath)) {
    Write-Error 'llama-server から読み込み済みモデルのパスを取得できません。'
    exit 1
}
$localModel = [System.IO.Path]::GetFileNameWithoutExtension($loadedModelPath)

# The Qwen chat template's highest reasoning level is xhigh; this launcher
# always starts the local model with maximum reasoning.
$reasoningEffort = 'xhigh'

$proxyHealthUrl = "$normalizeProxyUrl/health"
$proxyReady = $false
try {
    $proxyHealth = Invoke-RestMethod -Uri $proxyHealthUrl -TimeoutSec 2
    $proxyReady = $proxyHealth.visionPassthrough -eq $true -and $proxyHealth.toolCompatibility -eq 1 -and $proxyHealth.upstream -eq 'http://127.0.0.1:9931'
}
catch { }

if (-not $proxyReady) {
    $proxyScript = Join-Path $PSScriptRoot 'LocalCodexNormalizeProxy.mjs'
    $nodeExe = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (-not (Test-Path -LiteralPath $proxyScript) -or -not $nodeExe) {
        Write-Error 'ローカル正規化プロキシまたは Node.js が見つかりません。'
        exit 1
    }

    $proxyLog = Join-Path $PSScriptRoot 'LocalCodexNormalizeProxy.log'
    $proxyErrorLog = Join-Path $PSScriptRoot 'LocalCodexNormalizeProxy.error.log'
    Start-Process -FilePath $nodeExe -ArgumentList @($proxyScript) -WindowStyle Hidden -RedirectStandardOutput $proxyLog -RedirectStandardError $proxyErrorLog

    $ready = $false
    foreach ($attempt in 1..25) {
        Start-Sleep -Milliseconds 200
        try {
            $proxyHealth = Invoke-RestMethod -Uri $proxyHealthUrl -TimeoutSec 1
            if ($proxyHealth.visionPassthrough -eq $true -and $proxyHealth.toolCompatibility -eq 1 -and $proxyHealth.upstream -eq 'http://127.0.0.1:9931') {
                $ready = $true
                break
            }
        }
        catch { }
    }
    if (-not $ready) {
        Write-Error "ポート 9931 接続対応のローカル正規化プロキシを起動できません。古いプロキシがポート 8084 で動いている場合は停止してください。ログ: $proxyLog / $proxyErrorLog"
        exit 1
    }
}

if ($Capture) {
    $mitmweb = (Get-Command mitmweb -ErrorAction SilentlyContinue).Source
    if (-not $mitmweb) {
        Write-Error 'mitmweb が見つかりません。mitmproxy をインストールし、mitmweb を PATH に追加してください。'
        exit 1
    }

    $captureHealthUrl = "$captureProxyUrl/health"
    try {
        Invoke-WebRequest -Uri $captureHealthUrl -TimeoutSec 2 -UseBasicParsing | Out-Null
    }
    catch {
        $captureLog = Join-Path $PSScriptRoot 'mitmweb-codex.log'
        $captureErrorLog = Join-Path $PSScriptRoot 'mitmweb-codex.error.log'
        $mitmwebArguments = @(
            '--mode', "reverse:$normalizeProxyUrl",
            '--listen-host', '127.0.0.1',
            '--listen-port', '8085',
            '--web-host', '127.0.0.1',
            '--web-port', '8086',
            '--set', 'web_open_browser=false',
            '--set', 'web_password=local'
        )
        Start-Process -FilePath $mitmweb -ArgumentList $mitmwebArguments -WindowStyle Hidden -RedirectStandardOutput $captureLog -RedirectStandardError $captureErrorLog

        $ready = $false
        foreach ($attempt in 1..25) {
            Start-Sleep -Milliseconds 200
            try {
                Invoke-WebRequest -Uri $captureHealthUrl -TimeoutSec 1 -UseBasicParsing | Out-Null
                $ready = $true
                break
            }
            catch { }
        }
        if (-not $ready) {
            Write-Error "mitmweb を起動できません。ログ: $captureLog / $captureErrorLog"
            exit 1
        }
    }

    $apiBaseUrl = $captureProxyUrl
    Write-Host "通信キャプチャー: $captureWebUrl" -ForegroundColor Yellow
}

if ($CodexArgs.Count -eq 0 -and -not $ChildInteractive -and ([Console]::IsInputRedirected -or [Console]::IsOutputRedirected)) {
    # Codex's interactive TUI needs a real terminal. Open a new PowerShell
    # window so a no-argument invocation remains interactive.
    $shellExe = Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $shellExe)) {
        $shellExe = Join-Path $PSHOME 'powershell.exe'
    }
    Write-Host '標準入力が対話端末ではないため、新しい PowerShell ウィンドウで Codex を起動します。' -ForegroundColor Yellow
    $env:LOCAL_CODEX_CHILD = '1'
    if ($Capture) {
        $env:LOCAL_CODEX_CAPTURE = '1'
    }
    Start-Process -FilePath $shellExe -WorkingDirectory (Get-Location).Path -ArgumentList @('-NoExit', '-File', $PSCommandPath)
    exit 0
}
if (-not (Get-Command codex -ErrorAction SilentlyContinue)) {
    Write-Error 'codex コマンドが見つかりません。OpenAI Codex CLI をインストールし、PATH に追加してください。'
    exit 1
}

# Codex manages the local endpoint through its own configuration. Strip
# caller overrides for the managed keys so /props and this launcher remain
# the source of truth. Claude-style -p/--prompt keeps its value as codex's
# positional prompt argument.
$managedConfigKeys = @('model', 'model_provider', 'model_reasoning_effort', 'model_context_window')
$userInvocation = @()
for ($argumentIndex = 0; $argumentIndex -lt $CodexArgs.Count; $argumentIndex++) {
    $argument = [string]$CodexArgs[$argumentIndex]
    if ([string]::IsNullOrWhiteSpace($argument)) {
        continue
    }
    if ($argument -eq '-m' -or $argument -eq '--model') {
        # Discard its value as well. /props is authoritative for this launcher.
        if ($argumentIndex + 1 -lt $CodexArgs.Count) {
            $argumentIndex++
        }
        continue
    }
    if ($argument -eq '--effort') {
        # This launcher always starts the local model with maximum reasoning.
        if ($argumentIndex + 1 -lt $CodexArgs.Count) {
            $argumentIndex++
        }
        continue
    }
    if ($argument -like '--effort=*') {
        continue
    }
    if ($argument -eq '-c' -or $argument -eq '--config') {
        if ($argumentIndex + 1 -lt $CodexArgs.Count) {
            $configValue = [string]$CodexArgs[$argumentIndex + 1]
            $configKey = ($configValue -split '=', 2)[0]
            if ($configKey -in $managedConfigKeys -or $configKey -like 'model_providers.local.*') {
                $argumentIndex++
            }
            else {
                $userInvocation += $argument
                $userInvocation += $configValue
                $argumentIndex++
            }
        }
        continue
    }
    if ($argument -eq '-p' -or $argument -eq '--prompt') {
        # Claude-style prompt spelling; keep the value as the positional prompt.
        if ($argumentIndex + 1 -lt $CodexArgs.Count) {
            $userInvocation += $CodexArgs[$argumentIndex + 1]
            $argumentIndex++
        }
        continue
    }
    $userInvocation += $argument
}

$codexInvocation = @(
    '--model', $localModel,
    '-c', 'model_provider=local',
    '-c', 'model_providers.local.name=local-llama-server',
    '-c', "model_providers.local.base_url=${apiBaseUrl}/v1",
    '-c', 'model_providers.local.wire_api=responses',
    '-c', 'model_providers.local.env_key=OPENAI_API_KEY',
    '-c', "model_context_window=$serverContextTokens",
    '-c', "model_reasoning_effort=$reasoningEffort"
) + $userInvocation

Write-Host "Codex CLI -> ${apiBaseUrl}/v1 (model: $localModel; context: $serverContextTokens; effort: $reasoningEffort)" -ForegroundColor Cyan
if ($userInvocation.Count -eq 0) {
    & codex @codexInvocation
}
else {
    & codex 'exec' @codexInvocation
}
exit $LASTEXITCODE
