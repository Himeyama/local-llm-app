<#
.SYNOPSIS
Starts Claude Code against the local llama-server instance.

.EXAMPLE
.\Start-LocalClaude.ps1

.EXAMPLE
.\Start-LocalClaude.ps1 -p "このプロジェクトを要約して"

.EXAMPLE
.\Start-LocalClaude.ps1 -Capture
#>

$Capture = $args -contains '-Capture'
$ClaudeArgs = @($args | Where-Object { $_ -ne '-Capture' })
$ChildInteractive = $env:LOCAL_CLAUDE_CHILD -eq '1'
$Capture = $Capture -or $env:LOCAL_CLAUDE_CAPTURE -eq '1'
Remove-Item Env:LOCAL_CLAUDE_CHILD -ErrorAction SilentlyContinue
Remove-Item Env:LOCAL_CLAUDE_CAPTURE -ErrorAction SilentlyContinue

# Older revisions set these internal overrides in the caller's PowerShell
# process. Environment changes survive after a script exits, so clear stale
# values before using the supported --autocompact option below.
Remove-Item Env:CLAUDE_CODE_AUTO_COMPACT_WINDOW -ErrorAction SilentlyContinue
Remove-Item Env:CLAUDE_AUTOCOMPACT_PCT_OVERRIDE -ErrorAction SilentlyContinue
Remove-Item Env:CLAUDE_CODE_MAX_CONTEXT_TOKENS -ErrorAction SilentlyContinue
Remove-Item Env:CLAUDE_CODE_DISABLE_UNKNOWN_MODEL_WINDOW_ENFORCEMENT -ErrorAction SilentlyContinue

# llama-server exposes Claude-compatible Messages API at /v1/messages.
$normalizeProxyUrl = 'http://127.0.0.1:8081'
$captureProxyUrl = 'http://127.0.0.1:8082'
$captureWebUrl = 'http://127.0.0.1:8083/?token=local'
$env:ANTHROPIC_BASE_URL = $normalizeProxyUrl
$env:ANTHROPIC_API_KEY = 'local'

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

if ($serverContextTokens -lt 100000 -or $serverContextTokens -gt 1000000) {
    Write-Error "llama-server のコンテキスト長は $serverContextTokens トークンです。Claude Code の --autocompact には 100000～1000000 が必要です。llama-server の -c を範囲内にして再起動してください。"
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

$env:ANTHROPIC_MODEL = $localModel

# Prevent Claude Code from trying to use a separate cloud "small/fast" model.
$env:ANTHROPIC_DEFAULT_OPUS_MODEL = $localModel
$env:ANTHROPIC_DEFAULT_SONNET_MODEL = $localModel
$env:ANTHROPIC_DEFAULT_HAIKU_MODEL = $localModel
$env:CLAUDE_CODE_SUBAGENT_MODEL = $localModel

# Claude Code otherwise clamps unknown local model IDs to a 200k context even
# when --autocompact is larger. This advertises the model limit; the public
# --autocompact option below remains the only compaction-threshold setting.
$env:CLAUDE_CODE_MAX_CONTEXT_TOKENS = [string]$serverContextTokens

# Pass the running llama-server context length through Claude Code's public
# CLI option. Do not use the internal auto-compact environment-variable
# override; --autocompact remains the source of truth for the threshold.
$autoCompactWindow = [string]$serverContextTokens
$reasoningEffort = 'max'

$proxyHealthUrl = "$normalizeProxyUrl/health"
$proxyReady = $false
try {
    $proxyHealth = Invoke-RestMethod -Uri $proxyHealthUrl -TimeoutSec 2
    $proxyReady = $proxyHealth.visionPassthrough -eq $true -and $proxyHealth.upstream -eq 'http://127.0.0.1:9931'
}
catch { }

if (-not $proxyReady) {
    $proxyScript = Join-Path $PSScriptRoot 'LocalClaudeNormalizeProxy.mjs'
    $nodeExe = (Get-Command node -ErrorAction SilentlyContinue).Source
    if (-not (Test-Path -LiteralPath $proxyScript) -or -not $nodeExe) {
        Write-Error 'ローカル正規化プロキシまたは Node.js が見つかりません。'
        exit 1
    }

    $proxyLog = Join-Path $PSScriptRoot 'LocalClaudeNormalizeProxy.log'
    $proxyErrorLog = Join-Path $PSScriptRoot 'LocalClaudeNormalizeProxy.error.log'
    Start-Process -FilePath $nodeExe -ArgumentList @($proxyScript) -WindowStyle Hidden -RedirectStandardOutput $proxyLog -RedirectStandardError $proxyErrorLog

    $ready = $false
    foreach ($attempt in 1..25) {
        Start-Sleep -Milliseconds 200
        try {
            $proxyHealth = Invoke-RestMethod -Uri $proxyHealthUrl -TimeoutSec 1
            if ($proxyHealth.visionPassthrough -eq $true -and $proxyHealth.upstream -eq 'http://127.0.0.1:9931') {
                $ready = $true
                break
            }
        }
        catch { }
    }
    if (-not $ready) {
        Write-Error "ポート 9931 接続対応のローカル正規化プロキシを起動できません。古いプロキシがポート 8081 で動いている場合は停止してください。ログ: $proxyLog / $proxyErrorLog"
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
        $captureLog = Join-Path $PSScriptRoot 'mitmweb.log'
        $captureErrorLog = Join-Path $PSScriptRoot 'mitmweb.error.log'
        $mitmwebArguments = @(
            '--mode', "reverse:$normalizeProxyUrl",
            '--listen-host', '127.0.0.1',
            '--listen-port', '8082',
            '--web-host', '127.0.0.1',
            '--web-port', '8083',
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

    $env:ANTHROPIC_BASE_URL = $captureProxyUrl
    Write-Host "通信キャプチャー: $captureWebUrl" -ForegroundColor Yellow
}

if ($ClaudeArgs.Count -eq 0 -and -not $ChildInteractive -and ([Console]::IsInputRedirected -or [Console]::IsOutputRedirected)) {
    # Claude switches to print mode when it has no terminal. Open a real
    # PowerShell window so a no-argument invocation remains interactive.
    $shellExe = Join-Path $PSHOME 'pwsh.exe'
    if (-not (Test-Path -LiteralPath $shellExe)) {
        $shellExe = Join-Path $PSHOME 'powershell.exe'
    }
    Write-Host '標準入力が対話端末ではないため、新しい PowerShell ウィンドウで Claude を起動します。' -ForegroundColor Yellow
    $env:LOCAL_CLAUDE_CHILD = '1'
    if ($Capture) {
        $env:LOCAL_CLAUDE_CAPTURE = '1'
    }
    Start-Process -FilePath $shellExe -WorkingDirectory (Get-Location).Path -ArgumentList @('-NoExit', '-File', $PSCommandPath)
    exit 0
}
if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
    Write-Error 'claude コマンドが見つかりません。Claude Code をインストールし、PATH に追加してください。'
    exit 1
}

$compactSystemPrompt = 'You are a concise local coding assistant. Follow the user request. Use available tools only when necessary, and never claim results you did not obtain.'
$claudeInvocation = @()
for ($argumentIndex = 0; $argumentIndex -lt $ClaudeArgs.Count; $argumentIndex++) {
    $argument = [string]$ClaudeArgs[$argumentIndex]
    if ([string]::IsNullOrWhiteSpace($argument) -or
        $argument -eq '--full-local-tools' -or
        $argument -eq '--full-local-prompt' -or
        $argument -like '--autocompact=*' -or
        $argument -like '--effort=*') {
        continue
    }
    if ($argument -eq '--autocompact') {
        # Discard its value as well. /props is authoritative for this launcher.
        if ($argumentIndex + 1 -lt $ClaudeArgs.Count) {
            $argumentIndex++
        }
        continue
    }
    if ($argument -eq '--effort') {
        # This launcher always starts the local model with maximum reasoning.
        if ($argumentIndex + 1 -lt $ClaudeArgs.Count) {
            $argumentIndex++
        }
        continue
    }
    $claudeInvocation += $argument
}
$useBareMode = $ClaudeArgs -notcontains '--full-local-tools'
$useCompactPrompt = $ClaudeArgs -notcontains '--full-local-prompt'
if ($useBareMode -and $claudeInvocation -notcontains '--bare') {
    # Qwen works reliably with Claude Code's minimal local prompt. Use
    # --full-local-tools to opt into the experimental full tool-enabled prompt.
    $claudeInvocation = @('--bare') + $claudeInvocation
}
if ($useCompactPrompt -and $claudeInvocation -notcontains '--system-prompt') {
    $claudeInvocation = @('--system-prompt', $compactSystemPrompt) + $claudeInvocation
}
$claudeInvocation = @('--autocompact', $autoCompactWindow, '--effort', $reasoningEffort) + $claudeInvocation

Write-Host "Claude Code -> $env:ANTHROPIC_BASE_URL (model: $localModel; context: $serverContextTokens; effort: $reasoningEffort; bare: $useBareMode; compact: $useCompactPrompt)" -ForegroundColor Cyan
& claude @claudeInvocation
exit $LASTEXITCODE
