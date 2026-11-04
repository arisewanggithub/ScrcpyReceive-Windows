param(
    [ValidateSet('arm64', 'x64')][string]$Architecture = 'arm64',
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root "artifacts\win-$Architecture" }
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
    throw 'Publish requires a new or empty output directory; existing work will not be overwritten.'
}
$platform = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'x64' }
if (-not (Test-Path -LiteralPath (Join-Path $root "native\win-$Architecture\avcodec-61.dll"))) {
    throw "Pinned hardware decoder runtime missing. Run tools\PrepareNative.ps1 -Architecture $Architecture first."
}
foreach ($srtFile in @('avformat-61.dll', 'srt.dll', 'SRT-LICENSE.txt', 'SRT-1.5.7-source.zip', 'SRT-1.5.7-receiver-source.zip',
    'FFmpeg-LICENSE.txt', 'FFmpeg-copyright.txt', 'FFmpeg-build-dependencies.txt', 'FFmpeg-dependencies.spdx.json',
    'SRT-runtime-notices\Zig-LICENSE.txt', 'SRT-runtime-notices\libcxx-LICENSE.txt',
    'SRT-runtime-notices\libcxxabi-LICENSE.txt', 'SRT-runtime-notices\mingw-COPYING.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root "native\win-$Architecture\$srtFile"))) {
        throw "Pinned SRT/fMP4 runtime/notice missing: $srtFile. Run tools\PrepareSrt.ps1 -Architecture $Architecture first."
    }
}
& dotnet publish (Join-Path $root 'src\ScrcpyReceive.App\ScrcpyReceive.App.csproj') -c Release `
    -r "win-$Architecture" "-p:Platform=$platform" --self-contained true `
    -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Self-contained receiver publish failed.' }
foreach ($required in @('ScrcpyReceive.App.exe', 'ScrcpyReceive.App.pri', 'coreclr.dll', 'Microsoft.UI.Xaml.dll',
    "native\win-$Architecture\avcodec-61.dll", "native\win-$Architecture\avutil-59.dll", "native\win-$Architecture\swresample-5.dll",
    "native\win-$Architecture\avformat-61.dll", "native\win-$Architecture\srt.dll",
    "native\win-$Architecture\SRT-LICENSE.txt", "native\win-$Architecture\SRT-1.5.7-source.zip",
    "native\win-$Architecture\SRT-1.5.7-receiver-source.zip",
    "native\win-$Architecture\SRT-runtime-notices\Zig-LICENSE.txt",
    "native\win-$Architecture\SRT-runtime-notices\libcxx-LICENSE.txt",
    "native\win-$Architecture\SRT-runtime-notices\libcxxabi-LICENSE.txt",
    "native\win-$Architecture\SRT-runtime-notices\mingw-COPYING.txt")) {
    if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory $required))) { throw "Missing runtime/resource: $required" }
}
$otherArchitecture = if ($Architecture -eq 'arm64') { 'x64' } else { 'arm64' }
if (Test-Path -LiteralPath (Join-Path $OutputDirectory "native\win-$otherArchitecture")) {
    throw 'Publication contains native dependencies for the wrong architecture.'
}
if (@(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File -Filter '*.pdb').Count -ne 0) {
    throw 'Debug symbols must not be distributed in receiver packages.'
}
foreach ($notice in @('LICENSE', 'PRIVACY.md', 'THIRD_PARTY_NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $root $notice) -Destination $OutputDirectory
}
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$assetsPath = Join-Path $root 'src\ScrcpyReceive.App\obj\project.assets.json'
$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$licenses = Join-Path $OutputDirectory 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $package = Join-Path $cache $library.Name.ToLowerInvariant().Replace('/', '\')
    if (-not (Test-Path -LiteralPath $package)) { throw "Cannot locate restored package notices: $($library.Name)" }
    $destination = Join-Path $licenses ($library.Name.Replace('/', '-'))
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    foreach ($file in @(Get-ChildItem -LiteralPath $package -File | Where-Object { $_.Name -match '(?i)license|notice|copyright|\.nuspec$' })) {
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}
Write-Output "Self-contained untrimmed development app: $OutputDirectory"
