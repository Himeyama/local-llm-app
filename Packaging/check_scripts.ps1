$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
foreach ($name in @('Build-Installer.ps1', 'Start-LlamaServer.ps1', 'Start-RocmServer.ps1', 'Cli\Initialize-CliEnvironment.ps1')) {
    $tokens = $null
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repository $name), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count -gt 0) { throw ($name + ': ' + ($parseErrors.Message -join '; ')) }
    Write-Output ('PASS: ' + $name)
}
