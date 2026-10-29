$ErrorActionPreference = 'Continue'
# Derive repo root from this script's location (script lives in <root>\docs\),
# so it works on any machine regardless of the Windows user name.
$root = Split-Path -Parent $PSScriptRoot

# NOTE: keep this file pure ASCII. The Windows PowerShell 5.1 host decodes
# script files as ANSI, so non-ASCII characters would be mangled.

Write-Output '--- 1. stop stale GameFPSMonitor* ETW sessions ---'
logman query -ets 2>$null | Select-String 'GameFPSMonitor' | ForEach-Object {
    $n = ($_.Line -split '\s+')[0]
    if ($n) {
        logman stop $n -ets 2>$null | Out-Null
        Write-Output "    stopped: $n"
    }
}
Start-Sleep -Seconds 1

Write-Output '--- 2. kill old processes ---'
taskkill /F /T /IM GameFPS.exe 2>$null | Out-Null
taskkill /F /T /IM electron.exe 2>$null | Out-Null
Start-Sleep -Seconds 2

Write-Output '--- 3. build ---'
Set-Location "$root\backend\GameFPS.Backend"
dotnet build -c Release --nologo 2>&1 | Out-File "$root\docs\build.log" -Encoding utf8

Write-Output '--- 4. launch ---'
Start-Process "$root\backend\GameFPS.Backend\bin\Release\net10.0-windows\GameFPS.exe"
Start-Sleep -Seconds 3
Write-Output '--- done ---'
