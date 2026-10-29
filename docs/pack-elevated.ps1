$ErrorActionPreference = 'Continue'
# Derive repo root from this script's location (script lives in <root>\docs\),
# so it works on any machine regardless of the Windows user name.
$root = Split-Path -Parent $PSScriptRoot

# NOTE: keep this file pure ASCII (Windows PowerShell 5.1 reads scripts as ANSI).

Write-Output '--- stop running instances + release file locks ---'
taskkill /F /T /IM GameFPS.exe 2>$null | Out-Null
taskkill /F /T /IM electron.exe 2>$null | Out-Null
logman query -ets 2>$null | Select-String 'GameFPSMonitor' | ForEach-Object {
    $n = ($_.Line -split '\s+')[0]
    if ($n) { logman stop $n -ets 2>$null | Out-Null }
}
Start-Sleep -Seconds 3

Write-Output '--- publish ---'
& "$root\publish.ps1"
Write-Output '--- pack done ---'
