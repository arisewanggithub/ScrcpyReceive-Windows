param(
    [ValidateSet('arm64', 'x64')][string]$Architecture = 'arm64',
    [string]$CacheDirectory = "$env:LOCALAPPDATA\ScrcpyReceive-Windows-build-cache"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hash = if ($Architecture -eq 'arm64') { 'FDA0F9C4BB02839B95CBFA6F85B8120DAF0C3C2B17146C5C38024261973E6F58' } else { 'B58D10B69B0AD578585C43FEE48833D199DDAD4A7DEBC8CF9CE839F0A61C569B' }
New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
$archive = Join-Path $CacheDirectory "ffmpeg-7.1.1-$Architecture-lgpl.zip"
if (-not (Test-Path -LiteralPath $archive)) {
    $partial = Join-Path $CacheDirectory ([guid]::NewGuid().ToString('N') + '.partial')
    try {
        & curl.exe -fL "https://github.com/System233/ffmpeg-msvc-prebuilt/releases/download/ffmpeg-7.1.1-r2/ffmpeg-7.1.1-r2_$Architecture-windows-shared-lgpl.zip" -o $partial
        if ($LASTEXITCODE -ne 0) { throw 'Native dependency download failed.' }
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $hash) { throw 'Native archive SHA256 mismatch.' }
        Move-Item -LiteralPath $partial -Destination $archive
    } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial } }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $hash) { throw 'Cached native archive SHA256 mismatch; cache was left untouched.' }
$native = Join-Path $root "native\win-$Architecture"
if ((Test-Path -LiteralPath $native) -and @(Get-ChildItem -LiteralPath $native -Force).Count -gt 0) {
    throw 'Native output already exists. Preserve it; select a clean checkout for regeneration.'
}
$extract = Join-Path $CacheDirectory ("receiver-extract-" + [guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
New-Item -ItemType Directory -Path $extract | Out-Null
try {
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archive, $extract)
    New-Item -ItemType Directory -Path $native -Force | Out-Null
    foreach ($dll in @('avcodec-61.dll', 'avutil-59.dll', 'swresample-5.dll')) {
        Copy-Item -LiteralPath (Join-Path $extract "bin\$dll") -Destination $native
    }
    Copy-Item -LiteralPath (Join-Path $extract 'LICENSE.txt') -Destination (Join-Path $native 'FFmpeg-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $extract 'share\ffmpeg\copyright') -Destination (Join-Path $native 'FFmpeg-copyright.txt')
    Copy-Item -LiteralPath (Join-Path $extract 'CONTROL') -Destination (Join-Path $native 'FFmpeg-build-dependencies.txt')
    Copy-Item -LiteralPath (Join-Path $extract 'share\ffmpeg\vcpkg.spdx.json') -Destination (Join-Path $native 'FFmpeg-dependencies.spdx.json')
} finally {
    $owned = (Resolve-Path -LiteralPath $extract).Path
    if ((Split-Path -Parent $owned) -ne (Resolve-Path -LiteralPath $CacheDirectory).Path -or
        (Split-Path -Leaf $owned) -notmatch '^receiver-extract-[a-f0-9]{32}$') { throw 'Refusing unsafe extraction cleanup.' }
    Remove-Item -LiteralPath $owned -Recurse -Force
}
Write-Output "Prepared checksum-pinned FFmpeg 7.1.1 LGPL runtime for $Architecture. No drivers or machine settings changed."
