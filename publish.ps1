<#
    GameFPS release packer
    Usage:
        .\publish.ps1                     # self-contained (DEFAULT, same shape as a GitHub Release)
        .\publish.ps1 -SelfContained      # same as default, kept for older invocations
        .\publish.ps1 -FrameworkDependent # dev-only, needs .NET 10 Desktop Runtime on the target

    Self-contained is the default on purpose: everything under the output dir is
    what gets zipped and uploaded as a Release asset, so a build that silently
    dropped the .NET runtime would ship a package strangers cannot start. The
    final verify step below fails the build if the output no longer matches a
    release shape. It also deletes runtime logs left behind by running the app
    (electron.log / hotkey.log / session-debug.log / crosshair.log) and then
    proves the code files are byte-identical to the repo copy: "same as the
    release" means the CODE matches, not that the two file trees are equal.

    Output: dist\GameFPS\   -> double click GameFPS.exe (one UAC prompt)
    Keep this file pure ASCII: Windows PowerShell 5.1 decodes scripts as ANSI.
#>
param(
    [string]$OutputDir,
    [switch]$SelfContained,
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $OutputDir) { $OutputDir = Join-Path $root 'dist\GameFPS' }

if ($SelfContained -and $FrameworkDependent) {
    throw '-SelfContained and -FrameworkDependent are mutually exclusive'
}
# Default is self-contained; only an explicit -FrameworkDependent opts out.
$isFrameworkDependent = [bool]$FrameworkDependent

Write-Host "==> output: $OutputDir"
if ($isFrameworkDependent) {
    Write-Host '==> mode  : FRAMEWORK-DEPENDENT (dev only, not release shape)'
} else {
    Write-Host '==> mode  : SELF-CONTAINED (release shape)'
}

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host '==> [1/6] publish C# backend'
$dotnetArgs = @('publish', (Join-Path $root 'backend\GameFPS.Backend'),
                '-c', 'Release', '--nologo', '-o', $OutputDir)
if ($isFrameworkDependent) {
    $dotnetArgs += @('--self-contained', 'false')
} else {
    $dotnetArgs += @('-r', 'win-x64', '--self-contained', 'true')
}
& dotnet @dotnetArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host '==> [2/6] copy frontend'
$appSrc = Join-Path $root 'app'
$appDst = Join-Path $OutputDir 'app'
New-Item -ItemType Directory -Force -Path $appDst | Out-Null
foreach ($f in @('main.js', 'preload.js', 'package.json')) {
    Copy-Item (Join-Path $appSrc $f) $appDst -Force
}
Copy-Item (Join-Path $appSrc 'src') $appDst -Recurse -Force

Write-Host '==> [3/6] copy Electron runtime (about 370MB)'
$distSrc = Join-Path $appSrc 'node_modules\electron\dist'
if (-not (Test-Path $distSrc)) { throw "Electron runtime not found: $distSrc" }
$distDst = Join-Path $appDst 'electron'
Copy-Item $distSrc $distDst -Recurse -Force

Write-Host '==> [4/6] trim locales'
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
Write-Host '==> [5/6] slimming'
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

# A release must be runnable on a machine that never installed .NET, so the
# output is checked against the same file set a Release asset is expected to
# carry. Fail loudly here instead of discovering it from a stranger's bug report.
# Note: the output dir is deleted and rebuilt at the top of this script, so any
# runtime log (electron.log / hotkey.log / session-debug.log) can only appear
# AFTER a build, by running the app out of that dir. Those are removed by
# docs\sanitize-artifact.ps1, which is what to run right before you hand a
# directory over as the deliverable.
Write-Host '==> [6/6] verify release shape'
$must = @(
    'GameFPS.exe',
    'GameFPS.dll',
    'GameFPS.Core.dll',
    'app\main.js',
    'app\preload.js',
    'app\src\index.html',
    'app\electron\electron.exe',
    'app\electron\icudtl.dat',
    'app\electron\resources.pak',
    'app\electron\dxcompiler.dll',
    'app\electron\d3dcompiler_47.dll',
    'app\electron\resources\default_app.asar'
)
if (-not $isFrameworkDependent) {
    $must += @('coreclr.dll', 'clrjit.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')
}
$missing = @()
foreach ($rel in $must) {
    if (-not (Test-Path (Join-Path $OutputDir $rel))) { $missing += $rel }
}
if ($missing.Count -gt 0) {
    Write-Host ('    MISSING: ' + ($missing -join ', '))
    throw 'output does not match release shape (see MISSING list above)'
}

# Drop runtime leftovers and prove the shipped code files match the repo. One
# implementation lives in docs\sanitize-artifact.ps1 so a hand-copied artifact
# dir and a freshly built one are checked the same way.
& (Join-Path $PSScriptRoot 'docs\sanitize-artifact.ps1') -OutputDir $OutputDir

$size = (Get-ChildItem $OutputDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
# A self-contained build is ~403 MB. Anything well under that means the runtime
# did not get packed even though the expected files above happened to exist.
if (-not $isFrameworkDependent -and $size -lt 350) {
    throw ('self-contained output is only {0:N0} MB, runtime probably missing' -f $size)
}

Write-Host ''
Write-Host '==> done'
Write-Host "    entry: $OutputDir\GameFPS.exe"
Write-Host ("    size : {0:N0} MB" -f $size)
Write-Host ("    mode : {0}" -f $(if ($isFrameworkDependent) { 'FRAMEWORK-DEPENDENT' } else { 'SELF-CONTAINED (release shape)' }))
Write-Host ("    files checked: {0}" -f $must.Count)
