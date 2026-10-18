$ErrorActionPreference = 'Continue'
$root = 'C:\Users\awa486\Desktop\GameFPSMonitor'

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
