# GUI/Terminal processes may retain the PATH from before a CLI was installed.
# Refresh it in this child process only, and include standard per-user locations.
$cliProfile = [Environment]::GetFolderPath('UserProfile')
$cliLocalData = [Environment]::GetFolderPath('LocalApplicationData')
$cliRoamingData = [Environment]::GetFolderPath('ApplicationData')
$cliPathEntries = @($env:PATH -split ';') +
    @([Environment]::GetEnvironmentVariable('PATH', 'User') -split ';') +
    @([Environment]::GetEnvironmentVariable('PATH', 'Machine') -split ';') + @(
        (Join-Path $cliProfile '.local\bin'),
        (Join-Path $cliRoamingData 'npm'),
        (Join-Path $cliLocalData 'Programs\OpenAI\Codex\bin'),
        (Join-Path $cliLocalData 'Microsoft\WindowsApps')
    )
$cliCodexRoot = Join-Path $cliLocalData 'OpenAI\Codex\bin'
if (Test-Path -LiteralPath $cliCodexRoot -PathType Container) {
    $cliPathEntries += $cliCodexRoot
    $cliVersions = Get-ChildItem -LiteralPath $cliCodexRoot -Directory | Sort-Object LastWriteTime -Descending
    foreach ($cliVersion in $cliVersions) {
        if (Test-Path -LiteralPath (Join-Path $cliVersion.FullName 'codex.exe') -PathType Leaf) {
            $cliPathEntries += $cliVersion.FullName
            break
        }
    }
}
$env:PATH = (($cliPathEntries | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
    ForEach-Object { [Environment]::ExpandEnvironmentVariables($_.Trim()) } | Select-Object -Unique) -join ';')
