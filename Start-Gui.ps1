[CmdletBinding()]
param([switch]$BuildOnly)
$ErrorActionPreference = 'Stop'
Push-Location -LiteralPath $PSScriptRoot
try {
    & dotnet build .\LocalLlm.Gui.csproj -c Debug
    if ($LASTEXITCODE -ne 0) { throw 'GUI のビルドに失敗しました。' }
    if (-not $BuildOnly) {
        $exe = Join-Path $PSScriptRoot 'bin\Debug\net8.0-windows10.0.22621.0\win-x64\LocalLlm.Gui.exe'
        Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot -WindowStyle Normal
    }
}
finally { Pop-Location }
