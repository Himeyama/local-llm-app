$ErrorActionPreference = 'Stop'
$originalCliPath = $env:PATH
try {
    $env:PATH = Join-Path $env:SystemRoot 'System32'
    . (Join-Path $PSScriptRoot '..\Cli\Initialize-CliEnvironment.ps1')
    foreach ($name in @('claude', 'codex', 'node')) {
        $command = Get-Command $name -CommandType Application,ExternalScript -ErrorAction Stop | Select-Object -First 1
        if (-not (Test-Path -LiteralPath $command.Source -PathType Leaf)) { throw ('Missing executable: ' + $name) }
        Write-Output ('PASS: stale PATH resolves ' + $name + ' -> ' + $command.Source)
    }
}
finally { $env:PATH = $originalCliPath }
