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

Write-Host '==> [4/5] trim locales'
$locales = Join-Path $distDst 'locales'
if (Test-Path $locales) {
    Get-ChildItem $locales -Filter '*.pak' | Where-Object {
        $_.Name -notin @('zh-CN.pak', 'en-US.pak')
    } | Remove-Item -Force -ErrorAction SilentlyContinue
}

# Slimming pass. Every item below was verified to keep the app launchable.
# Do NOT add: dxcompiler.dll / dxil.dll / d3dcompiler_47.dll / resources.pak /
# icudtl.dat / v8 snapshot / *.pak in electron root / resources\default_app.asar
# -- the app fails to start without them (electron.exe exits with code 1).
Write-Host '==> [5/5] slimming'
$slim = @(
    # pure-text license dump, 19 MB of HTML
    (Join-Path $distDst 'LICENSES.chromium.html'),
    # software Vulkan fallback, unused on any machine with a GPU
    (Join-Path $distDst 'vk_swiftshader.dll'),
    (Join-Path $distDst 'vk_swiftshader_icd.json'),
    (Join-Path $distDst 'vulkan-1.dll'),
    # designer + debug-symbol reader, not needed at runtime
    (Join-Path $OutputDir 'System.Windows.Forms.Design.dll'),
    (Join-Path $OutputDir 'Microsoft.DiaSymReader.Native.amd64.dll'),
    # DIA native libs for other architectures
    (Join-Path $OutputDir 'arm64'),
    (Join-Path $OutputDir 'x86')
)
foreach ($p in $slim) {
    if (Test-Path $p) { Remove-Item $p -Recurse -Force -ErrorAction SilentlyContinue }
}

# .NET satellite resources: keep Chinese only
$keepCultures = @('zh-Hans', 'zh-Hant', 'zh-CN', 'zh-TW')
Get-ChildItem $OutputDir -Directory -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match '^[a-z]{2}(-[A-Za-z]{2,4})?$' -and $keepCultures -notcontains $_.Name
} | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

$size = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
Write-Host ''
Write-Host '==> done'
Write-Host "    entry: $OutputDir\GameFPS.exe"
Write-Host ("    size : {0:N0} MB" -f $size)
