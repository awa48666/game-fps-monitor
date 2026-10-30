<#
    sanitize-artifact.ps1 -- make an existing artifact dir match a release.

    "Matches the release" means the CODE files are identical, not that the two
    file trees have equal counts. A directory that has been double-clicked and
    run accumulates runtime logs (electron.log, hotkey.log, session-debug.log,
    crosshair.log) which no release asset contains; they are machine state, they
    leak local paths and PIDs, and they say nothing about code shape. This script
    drops those and then proves every shipped code file is byte-identical to the
    repo copy.

    Usage (run from anywhere, no elevation needed):
        powershell -File docs\sanitize-artifact.ps1 -OutputDir "C:\path\to\artifact"
        powershell -File docs\sanitize-artifact.ps1 -OutputDir "C:\path\to\artifact" -StripPdb

    It does NOT build anything. Build with publish.ps1, then run the app to
    verify, then run this before you call the directory delivered.

    Keep this file pure ASCII: Windows PowerShell 5.1 decodes scripts as ANSI.
#>
param(
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [switch]$StripPdb
)
$ErrorActionPreference = 'Stop'

# The repo root is the parent of this script's own folder (docs\), never a
# hard-coded user path -- see v1.0.29 for why that mattered.
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath $OutputDir)) { throw "output dir not found: $OutputDir" }

Write-Host '==> [1/3] remove runtime state from the artifact dir'
$logs = @(Get-ChildItem -LiteralPath $OutputDir -Recurse -File -Filter '*.log' -ErrorAction SilentlyContinue)
foreach ($f in $logs) { Remove-Item -LiteralPath $f.FullName -Force }
if ($logs.Count -gt 0) {
    Write-Host ('    removed: ' + (($logs | ForEach-Object { $_.FullName.Substring($OutputDir.Length + 1) }) -join ', '))
} else {
    Write-Host '    no runtime logs present'
}
if ($StripPdb) {
    $pdbs = @(Get-ChildItem -LiteralPath $OutputDir -Recurse -File -Filter '*.pdb' -ErrorAction SilentlyContinue)
    foreach ($f in $pdbs) { Remove-Item -LiteralPath $f.FullName -Force }
    Write-Host ('    removed pdb: ' + $pdbs.Count)
}

# Anything else that only appears after someone ran the app out of this dir.
$extra = @(Get-ChildItem -LiteralPath $OutputDir -Recurse -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'settings.json' -or $_.Name -eq 'sessions.json' })
if ($extra.Count -gt 0) {
    Write-Host '    WARNING: user data found inside the artifact dir:'
    $extra | ForEach-Object { Write-Host ('      ' + $_.FullName) }
    Write-Host '    Data should live in %LOCALAPPDATA%\GameFPSMonitor (v1.0.4 onward).'
}

Write-Host '==> [2/3] verify code files are identical to repo source'
$codeFiles = @(
    'app\main.js',
    'app\preload.js',
    'app\src\index.html',
    'app\src\app.js',
    'app\src\style.css',
    'app\package.json'
)
$mismatch = @()
foreach ($rel in $codeFiles) {
    $src = Join-Path $repoRoot $rel
    $out = Join-Path $OutputDir $rel
    if (-not (Test-Path -LiteralPath $src)) { $mismatch += ($rel + ' (no repo source)'); continue }
    if (-not (Test-Path -LiteralPath $out)) { $mismatch += ($rel + ' (not in artifact)'); continue }
    $a = (Get-FileHash -LiteralPath $src -Algorithm MD5).Hash
    $b = (Get-FileHash -LiteralPath $out -Algorithm MD5).Hash
    if ($a -ne $b) { $mismatch += ($rel + ' (md5 differs)') }
}
if ($mismatch.Count -gt 0) {
    Write-Host ('    CODE MISMATCH: ' + ($mismatch -join ', '))
    throw 'artifact code files differ from repo source -- rebuild with publish.ps1'
}
Write-Host ('    identical: ' + ($codeFiles -join ', '))

Write-Host '==> [3/3] report final shape'
$files = @(Get-ChildItem -LiteralPath $OutputDir -Recurse -File -ErrorAction SilentlyContinue)
$sizeMB = ($files | Measure-Object Length -Sum).Sum / 1MB
$stillLogs = @(Get-ChildItem -LiteralPath $OutputDir -Recurse -File -Filter '*.log' -ErrorAction SilentlyContinue).Count
if ($stillLogs -gt 0) { throw ('{0} log file(s) still present after cleanup' -f $stillLogs) }
if (-not (Test-Path -LiteralPath (Join-Path $OutputDir 'GameFPS.exe'))) { throw 'GameFPS.exe missing: not an artifact dir?' }

Write-Host ''
Write-Host '==> done'
Write-Host ("    entry : $OutputDir\GameFPS.exe")
Write-Host ("    files : $($files.Count)  (no runtime logs left)")
Write-Host ("    size  : {0:N0} MB" -f $sizeMB)
