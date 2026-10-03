$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$files = @(Get-ChildItem -LiteralPath $root -Filter '*.ps1') + @(Get-ChildItem -LiteralPath (Join-Path $root 'Cli') -Filter '*.ps1')
foreach ($file in $files) {
    $tokens = $null; $issues = $null
    [System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$issues) | Out-Null
    if ($issues.Count) { throw ($file.Name + ": " + (($issues | ForEach-Object { $_.Message }) -join "; ")) }
}
Write-Output ('PASS: PowerShell ' + $PSVersionTable.PSVersion + ', ' + $files.Count + ' scripts')
