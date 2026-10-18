<#
    GameFPS release packer
    Usage:
        .\publish.ps1                     # framework-dependent (needs .NET 10 Desktop Runtime)
        .\publish.ps1 -SelfContained      # self-contained (no runtime needed, much bigger)

    Output: dist\GameFPS\   -> double click GameFPS.exe (one UAC prompt)
    Keep this file pure ASCII: Windows PowerShell 5.1 decodes scripts as ANSI.
#>
param(
    [string]$OutputDir,
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root 'dist\GameFPS' }

Write-Host "==> output: $OutputDir"

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host '==> [1/4] publish C# backend'
$dotnetArgs = @('publish', (Join-Path $root 'backend\GameFPS.Backend'),
                '-c', 'Release', '--nologo', '-o', $OutputDir)
if ($SelfContained) {
    $dotnetArgs += @('-r', 'win-x64', '--self-contained', 'true')
} else {
    $dotnetArgs += @('--self-contained', 'false')
}
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host '==> [2/4] copy frontend'
$appSrc = Join-Path $root 'app'
$appDst = Join-Path $OutputDir 'app'
New-Item -ItemType Directory -Force -Path $appDst | Out-Null
foreach ($f in @('main.js', 'preload.js', 'package.json')) {
    Copy-Item (Join-Path $appSrc $f) $appDst -Force
}
Copy-Item (Join-Path $appSrc 'src') $appDst -Recurse -Force

Write-Host '==> [3/4] copy Electron runtime (about 370MB)'
$distSrc = Join-Path $appSrc 'node_modules\electron\dist'
if (-not (Test-Path $distSrc)) { throw "Electron runtime not found: $distSrc" }
$distDst = Join-Path $appDst 'electron'
Copy-Item $distSrc $distDst -Recurse -Force

Write-Host '==> [4/4] trim locales'
$locales = Join-Path $distDst 'locales'
if (Test-Path $locales) {
    Get-ChildItem $locales -Filter '*.pak' | Where-Object {
        $_.Name -notin @('zh-CN.pak', 'en-US.pak')
    } | Remove-Item -Force -ErrorAction SilentlyContinue
}

$size = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ''
Write-Host '==> done'
Write-Host "    entry: $OutputDir\GameFPS.exe"
Write-Host ("    size : {0:N0} MB" -f $size)
