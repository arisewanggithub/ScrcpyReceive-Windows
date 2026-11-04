param(
    [ValidateSet('arm64', 'x64')][string]$Architecture = 'arm64',
    [string]$CacheDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\native-cache'),
    # Optional read-only archive source; every archive is still verified before use.
    [string]$ArchiveDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null

function Expand-Pinned($name, $url, $sha256) {
    $zip = Join-Path $CacheDirectory "$name.zip"
    if (-not (Test-Path -LiteralPath $zip)) {
        $source = if ($ArchiveDirectory) { Join-Path $ArchiveDirectory "$name.zip" } else { $null }
        if ($source -and (Test-Path -LiteralPath $source)) {
            if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $sha256) { throw "Invalid source archive: $name" }
            Copy-Item -LiteralPath $source -Destination $zip
        } else {
            $partial = "$zip.partial"
            if (Test-Path -LiteralPath $partial) { throw "Preserving an existing partial download: $partial" }
            try {
                & curl.exe --fail --location --silent --show-error $url --output $partial
                if ($LASTEXITCODE -ne 0) { throw "Download failed: $name" }
                if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $sha256) { throw "Checksum mismatch: $name" }
                Move-Item -LiteralPath $partial -Destination $zip
            } finally { if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial } }
        }
    }
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $sha256) { throw "Invalid cached archive: $name" }
    $directory = Join-Path $CacheDirectory "$name-$($sha256.Substring(0, 12))"
    if (-not (Test-Path -LiteralPath (Join-Path $directory '.verified'))) {
        if (Test-Path -LiteralPath $directory) { throw "Preserving incomplete extraction: $directory" }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $directory)
        Set-Content -LiteralPath (Join-Path $directory '.verified') -Value $sha256 -Encoding ASCII
    }
    return $directory
}

function Copy-Preserving($source, $destination) {
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
            throw "Existing native file differs; preserved without overwrite: $destination"
        }
    } else { Copy-Item -LiteralPath $source -Destination $destination }
}

function Get-PeImports($file) {
    $bytes = [IO.File]::ReadAllBytes($file)
    $pe = [BitConverter]::ToInt32($bytes, 60)
    if ([BitConverter]::ToUInt32($bytes, $pe) -ne 0x4550) { throw "Not a PE file: $file" }
    $machine = [BitConverter]::ToUInt16($bytes, $pe + 4)
    $expected = if ($Architecture -eq 'arm64') { 0xAA64 } else { 0x8664 }
    if ($machine -ne $expected) { throw "Wrong native architecture: $file" }
    $sections = [BitConverter]::ToUInt16($bytes, $pe + 6)
    $optional = $pe + 24
    if ([BitConverter]::ToUInt16($bytes, $optional) -ne 0x20B) { throw "PE32+ is required: $file" }
    $sectionStart = $optional + [BitConverter]::ToUInt16($bytes, $pe + 20)
    function Convert-Rva([uint32]$rva) {
        for ($i = 0; $i -lt $sections; $i++) {
            $s = $sectionStart + $i * 40
            $va = [BitConverter]::ToUInt32($bytes, $s + 12)
            $length = [Math]::Max([BitConverter]::ToUInt32($bytes, $s + 8), [BitConverter]::ToUInt32($bytes, $s + 16))
            if ($rva -ge $va -and $rva -lt ($va + $length)) {
                return [int]([BitConverter]::ToUInt32($bytes, $s + 20) + $rva - $va)
            }
        }
        throw "Invalid PE RVA: $file"
    }
    $importRva = [BitConverter]::ToUInt32($bytes, $optional + 120)
    if ($importRva -eq 0) { return }
    $entry = Convert-Rva $importRva
    while ([BitConverter]::ToUInt32($bytes, $entry + 12) -ne 0) {
        $nameOffset = Convert-Rva ([BitConverter]::ToUInt32($bytes, $entry + 12))
        $end = $nameOffset
        while ($bytes[$end] -ne 0) { $end++ }
        [Text.Encoding]::ASCII.GetString($bytes, $nameOffset, $end - $nameOffset)
        $entry += 20
    }
}

$ffHash = if ($Architecture -eq 'arm64') { 'FDA0F9C4BB02839B95CBFA6F85B8120DAF0C3C2B17146C5C38024261973E6F58' } else { 'B58D10B69B0AD578585C43FEE48833D199DDAD4A7DEBC8CF9CE839F0A61C569B' }
$ff = Expand-Pinned "ffmpeg-7.1.1-$Architecture-lgpl" `
    "https://github.com/System233/ffmpeg-msvc-prebuilt/releases/download/ffmpeg-7.1.1-r2/ffmpeg-7.1.1-r2_$Architecture-windows-shared-lgpl.zip" $ffHash
$srt = Expand-Pinned 'srt-1.5.7' 'https://github.com/Haivision/srt/archive/refs/tags/v1.5.7.zip' `
    '2F87560102B95137E851B4E64909087BCBCD3CEE13C7F354809804A3E29A0D85'
$srtSource = Join-Path $srt 'srt-1.5.7'
$coreSource = Join-Path $srtSource 'srtcore\core.cpp'
$original = @'
        if (!self->m_bTLPktDrop)
        {
            rxready = !info.seq_gap && is_time_to_deliver;
        }
'@
$replacement = @'
        if (!self->m_bTLPktDrop)
        {
            rxready = !info.seq_gap && is_time_to_deliver;
            // ScrcpyReceive-Windows: wait for retransmission, not an expired delivery deadline.
            if (info.seq_gap && is_time_to_deliver)
                tsNextDelivery = steady_clock::time_point();
        }
'@
# Without this fix, an overdue gap spins under m_RecvLock and can prevent receive/cancellation.
$coreText = [IO.File]::ReadAllText($coreSource).Replace("`r`n", "`n")
$original = $original.Replace("`r`n", "`n")
$replacement = $replacement.Replace("`r`n", "`n")
if (-not $coreText.Contains($replacement)) {
    if ($coreText.Split(@($original), [StringSplitOptions]::None).Count -ne 2) {
        throw 'Pinned SRT loss-wait patch does not match exactly once; source preserved.'
    }
    [IO.File]::WriteAllText($coreSource, $coreText.Replace($original, $replacement), (New-Object Text.UTF8Encoding($false)))
}
$receiverSourceArchive = Join-Path $CacheDirectory 'srt-1.5.7-receiver-lossfix1-portable-source.zip'
if (-not (Test-Path -LiteralPath $receiverSourceArchive)) {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($receiverSourceArchive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in @(Get-ChildItem -LiteralPath $srtSource -Recurse -File | Sort-Object FullName)) {
            $entry = $file.FullName.Substring($srtSource.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry) | Out-Null
        }
    } finally { $archive.Dispose() }
}
$hostCpu = $env:PROCESSOR_ARCHITEW6432
if (-not $hostCpu) { $hostCpu = $env:PROCESSOR_ARCHITECTURE }
if ($hostCpu -eq 'ARM64') {
    $hostName = 'arm64'; $zigCpu = 'aarch64'; $cmakeCpu = 'arm64'; $ninjaCpu = 'winarm64'
    $zigHash = 'B5AAC0CCC40DD91E8311B1F257717D8E3903B5FEFB8F659DE6D65A840AD1D0E7'
    $cmakeHash = 'FA648FD417F44E6CB08928964A480ADE0D18DF421F9B623639DBA22F9B301E4E'
    $ninjaHash = 'FB959B674970E36A7C9A23191524B80FB5298FC71FC98BFA42456BCC0A8DFB2F'
} elseif ($hostCpu -eq 'AMD64') {
    $hostName = 'x64'; $zigCpu = 'x86_64'; $cmakeCpu = 'x86_64'; $ninjaCpu = 'win'
    $zigHash = '554F5378228923FFD558EAC35E21AF020C73789D87AFEABF4BFD16F2E6FEED2C'
    $cmakeHash = 'D163CD3AB4959B0A53FA8988F2DDBD2E6C501658201E6A154386BAD9DBE4F836'
    $ninjaHash = '26A40FA8595694DEC2FAD4911E62D29E10525D2133C9A4230B66397774AE25BF'
} else { throw "Unsupported build host: $hostCpu" }
$zig = Expand-Pinned "zig-0.14.1-$hostName" "https://ziglang.org/download/0.14.1/zig-$zigCpu-windows-0.14.1.zip" $zigHash
$cmake = Expand-Pinned "cmake-3.31.6-$hostName" "https://github.com/Kitware/CMake/releases/download/v3.31.6/cmake-3.31.6-windows-$cmakeCpu.zip" $cmakeHash
$ninja = Expand-Pinned "ninja-1.13.1-$hostName" "https://github.com/ninja-build/ninja/releases/download/v1.13.1/ninja-$ninjaCpu.zip" $ninjaHash
$zigExe = (Join-Path $zig "zig-$zigCpu-windows-0.14.1\zig.exe").Replace('\', '/')
$target = if ($Architecture -eq 'arm64') { 'aarch64-windows-gnu' } else { 'x86_64-windows-gnu' }
$processor = if ($Architecture -eq 'arm64') { 'ARM64' } else { 'AMD64' }
$toolchain = Join-Path $CacheDirectory "receiver-srt-$Architecture.cmake"
$configuration = @"
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR $processor)
set(CMAKE_C_COMPILER "$zigExe")
set(CMAKE_C_COMPILER_ARG1 cc)
set(CMAKE_CXX_COMPILER "$zigExe")
set(CMAKE_CXX_COMPILER_ARG1 c++)
set(CMAKE_C_FLAGS_INIT "-target $target")
set(CMAKE_CXX_FLAGS_INIT "-target $target")
set(CMAKE_RC_COMPILER "$zigExe")
set(CMAKE_RC_COMPILER_ARG1 rc)
"@
if (Test-Path -LiteralPath $toolchain) {
    if ((Get-Content -LiteralPath $toolchain -Raw).Trim() -ne $configuration.Trim()) { throw "Preserving different toolchain: $toolchain" }
} else { Set-Content -LiteralPath $toolchain -Value $configuration -Encoding ASCII }
$build = Join-Path $CacheDirectory "receiver-srt-1.5.7-lossfix1-$Architecture"
$cmakeExe = Join-Path $cmake "cmake-3.31.6-windows-$cmakeCpu\bin\cmake.exe"
# Keep Zig's own compiler caches inside this project's explicitly selected cache.
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $CacheDirectory 'zig-global'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $CacheDirectory "zig-local-$Architecture"
& $cmakeExe -S (Join-Path $srt 'srt-1.5.7') -B $build -G Ninja `
    --log-level=ERROR -Wno-deprecated --no-warn-unused-cli `
    "-DCMAKE_TOOLCHAIN_FILE=$toolchain" "-DCMAKE_MAKE_PROGRAM=$(Join-Path $ninja 'ninja.exe')" `
    -DCMAKE_BUILD_TYPE=Release -DENABLE_APPS=OFF -DENABLE_ENCRYPTION=OFF `
    -DENABLE_SHARED=ON -DENABLE_STATIC=OFF -DENABLE_STDCXX_SYNC=ON -DENABLE_LOGGING=OFF
if ($LASTEXITCODE -ne 0) { throw 'Pinned libsrt configuration failed.' }
& $cmakeExe --build $build --parallel 4
if ($LASTEXITCODE -ne 0) { throw 'Pinned libsrt build failed.' }

$native = Join-Path $root "native\win-$Architecture"
New-Item -ItemType Directory -Path $native -Force | Out-Null
$queue = New-Object 'System.Collections.Generic.Queue[string]'
@('avformat-61.dll', 'avcodec-61.dll', 'avutil-59.dll', 'swresample-5.dll') | ForEach-Object { $queue.Enqueue($_) }
$seen = @{}
$system = @('kernel32.dll', 'ntdll.dll', 'user32.dll', 'advapi32.dll', 'bcrypt.dll', 'crypt32.dll',
    'ws2_32.dll', 'secur32.dll', 'shell32.dll', 'ole32.dll', 'oleaut32.dll', 'gdi32.dll',
    'msvcrt.dll', 'ucrtbase.dll', 'normaliz.dll', 'winmm.dll', 'shlwapi.dll', 'psapi.dll',
    'd3d11.dll', 'dxgi.dll', 'dxva2.dll', 'mfplat.dll', 'mfuuid.dll', 'strmiids.dll',
    'vfw32.dll', 'oleacc.dll', 'avrt.dll', 'dwmapi.dll', 'winspool.drv', 'comdlg32.dll',
    'iphlpapi.dll', 'cfgmgr32.dll', 'wsock32.dll')
while ($queue.Count -gt 0) {
    $dll = $queue.Dequeue()
    if ($seen.ContainsKey($dll)) { continue }
    $seen[$dll] = $true
    $source = Join-Path $ff "bin\$dll"
    foreach ($dependency in @(Get-PeImports $source)) {
        if (Test-Path -LiteralPath (Join-Path $ff "bin\$dependency")) { $queue.Enqueue($dependency) }
        elseif ($dependency -notlike 'api-ms-win-*' -and $dependency -notlike 'ext-ms-win-*' -and $system -notcontains $dependency) {
            throw "Unresolved non-system dependency of ${dll}: $dependency"
        }
    }
    Copy-Preserving $source (Join-Path $native $dll)
}
$srtDll = Join-Path $build 'libsrt.dll'
foreach ($dependency in @(Get-PeImports $srtDll)) {
    if ($dependency -notlike 'api-ms-win-*' -and $system -notcontains $dependency) { throw "Unresolved libsrt dependency: $dependency" }
}
Copy-Preserving $srtDll (Join-Path $native 'srt.dll')
@{
    'LICENSE.txt' = 'FFmpeg-LICENSE.txt'
    'share\ffmpeg\copyright' = 'FFmpeg-copyright.txt'
    'CONTROL' = 'FFmpeg-build-dependencies.txt'
    'share\ffmpeg\vcpkg.spdx.json' = 'FFmpeg-dependencies.spdx.json'
}.GetEnumerator() | ForEach-Object { Copy-Preserving (Join-Path $ff $_.Key) (Join-Path $native $_.Value) }
Copy-Preserving (Join-Path $srt 'srt-1.5.7\LICENSE') (Join-Path $native 'SRT-LICENSE.txt')
Copy-Preserving (Join-Path $CacheDirectory 'srt-1.5.7.zip') (Join-Path $native 'SRT-1.5.7-source.zip')
Copy-Preserving $receiverSourceArchive (Join-Path $native 'SRT-1.5.7-receiver-source.zip')
$runtimeNotices = Join-Path $native 'SRT-runtime-notices'
New-Item -ItemType Directory -Path $runtimeNotices -Force | Out-Null
$zigRoot = Join-Path $zig "zig-$zigCpu-windows-0.14.1"
@{
    'LICENSE' = 'Zig-LICENSE.txt'
    'lib\libcxx\LICENSE.TXT' = 'libcxx-LICENSE.txt'
    'lib\libcxxabi\LICENSE.TXT' = 'libcxxabi-LICENSE.txt'
    'lib\libc\mingw\COPYING' = 'mingw-COPYING.txt'
}.GetEnumerator() | ForEach-Object { Copy-Preserving (Join-Path $zigRoot $_.Key) (Join-Path $runtimeNotices $_.Value) }
Write-Output "Prepared pinned SRT/fMP4 runtime and verified PE dependency closure for win-$Architecture; existing native files preserved."
