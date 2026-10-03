<#
.SYNOPSIS
Starts a local multimodal llama-server API using the ROCm/HIP backend.

The llama.cpp Web UI is disabled so OpenWebUI or another OpenAI-compatible
client can provide the user interface.

.EXAMPLE
.\Start-LlamaServer.ps1

.EXAMPLE
.\Start-LlamaServer.ps1 -ContextSize 262144

.EXAMPLE
.\Start-LlamaServer.ps1 -MtpDraftTokens 0

.EXAMPLE
.\Start-LlamaServer.ps1 -ModelPath 'models\Qwen3.8-27B-Uncensored\Qwen3.8-27B-Uncensored-Q4_K_M.gguf'

.EXAMPLE
.\Start-LlamaServer.ps1 -ContextSize 262144 -MtpDraftTokens 0 -ModelPath 'models\Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced-Q4_K_M.gguf' -MmprojPath 'models\mmproj-Gemma4-12B-QAT-Uncensored-HauhauCS-Balanced-BF16.gguf'

.EXAMPLE
.\Start-LlamaServer.ps1 -Help
#>

[CmdletBinding(PositionalBinding = $false)]
param(
    [ValidateRange(65536, 1000000)]
    [int]$ContextSize = 131072,

    [ValidateRange(1, 512)]
    [int]$Threads = 12,

    # 0 disables MTP for models without an embedded draft head.
    [ValidateRange(0, 16)]
    [int]$MtpDraftTokens = 2,

    [ValidateNotNullOrEmpty()]
    [string]$ModelPath = 'models\Qwen3.8-27B-Uncensored\Qwen3.8-27B-Uncensored-Q4_K_M.gguf',

    [ValidateNotNullOrEmpty()]
    [string]$MmprojPath = 'models\Qwen3.8-27B-Uncensored\mmproj-Qwen3.8-27B-Uncensored-F16.gguf',

    [string]$ServerExe,

    [switch]$Help,

    # Claude Code uses --effort max. Accept that redundant spelling here so
    # copied launcher commands do not get mis-bound to -Threads.
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$RemainingArguments
)

if ($RemainingArguments.Count -gt 0) {
    $remainingArgumentText = $RemainingArguments -join ' '
    if ($remainingArgumentText -notmatch '^(?:(?:--effort|-effort)\s+max\s*)+$') {
        Write-Error "未対応の追加引数です: $remainingArgumentText"
        exit 1
    }
    Write-Verbose '既定で最大推論強度を使用するため、指定された --effort max は無視します。'
}

if ($Help) {
    Get-Help -Name $PSCommandPath -Detailed
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ServerExe)) {
    $ServerExe = 'runtime\llama.cpp\llama-server.exe'
}
if (-not [System.IO.Path]::IsPathRooted($ServerExe)) {
    $ServerExe = Join-Path $PSScriptRoot $ServerExe
}

$modelPath = Join-Path $PSScriptRoot $ModelPath
$mmprojPath = Join-Path $PSScriptRoot $MmprojPath
$modelAlias = [System.IO.Path]::GetFileNameWithoutExtension($modelPath)

if (-not (Test-Path -LiteralPath $ServerExe -PathType Leaf)) {
    Write-Error "llama-server.exe が見つかりません: $ServerExe（runtime\llama.cpp に実行環境を配置してください）"
    exit 1
}
if (-not (Test-Path -LiteralPath $modelPath)) {
    Write-Error "GGUF モデルが見つかりません: $modelPath"
    exit 1
}
if (-not (Test-Path -LiteralPath $mmprojPath -PathType Leaf)) {
    Write-Error "画像入力用 mmproj が見つかりません: $mmprojPath"
    exit 1
}
if (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort 9931 -State Listen -ErrorAction SilentlyContinue) {
    Write-Error 'ポート 9931 は既に使用中です。既存の llama-server を停止してから実行してください。'
    exit 1
}

$serverArgs = @(
    '-m', $modelPath,
    '--mmproj', $mmprojPath,
    '--alias', $modelAlias,
    '-ngl', '99',
    '-c', [string]$ContextSize,
    '-np', '1',
    '-b', '2048',
    '-ub', '512',
    '-fa', 'on',
    '-ctk', 'q8_0',
    '-ctv', 'q8_0',
    '--cache-prompt',
    '--reasoning', 'on',
    # The Qwen chat template accepts xhigh/medium/low, not Claude Code's
    # public max spelling. xhigh is the template's highest reasoning level.
    '--reasoning-effort', 'xhigh',
    '-t', [string]$Threads,
    '-tb', [string]$Threads,
    '--host', '127.0.0.1',
    '--port', '9931',
    '--cors-origins', 'localhost',
    # OpenWebUI and Claude Code use the OpenAI-compatible API directly.
    '--no-webui'
)

if ($modelAlias -eq 'Qwen3.8-27B-Uncensored-Q4_K_M') {
    $serverArgs += @('--image-min-tokens', '1024')
}

if ($MtpDraftTokens -gt 0) {
    $serverArgs += @(
        '--spec-type', 'draft-mtp',
        '--spec-draft-n-max', [string]$MtpDraftTokens,
        '--spec-draft-ngl', '99'
    )
}

Write-Host "ROCm llama-server を起動します（コンテキスト: $ContextSize / threads: $Threads / MTP draft: $MtpDraftTokens / reasoning: xhigh / webui: off）..." -ForegroundColor Cyan
& $ServerExe @serverArgs
exit $LASTEXITCODE
