param(
    [Parameter(Mandatory = $true)][ValidateSet('arm64', 'x64')][string]$Architecture,
    [Parameter(Mandatory = $true)][string]$PublishedDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$PackageName,
    [Parameter(Mandatory = $true)][string]$Publisher,
    [Parameter(Mandatory = $true)][string]$PublisherDisplayName
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.0$') { throw 'MSIX version must be MAJOR.MINOR.PATCH.0.' }
$number = [version]::Parse($Version)
if ($number.Major -gt 65535 -or $number.Minor -gt 65535 -or $number.Build -gt 65535) { throw 'MSIX version component exceeds 65535.' }
if ($PackageName -notmatch '^[A-Za-z0-9.-]{3,50}$' -or -not $Publisher.StartsWith('CN=') -or
    [string]::IsNullOrWhiteSpace($PublisherDisplayName)) { throw 'Exact receiver-specific package identity is required.' }
if ((Test-Path -LiteralPath $OutputDirectory) -and @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -ne 0) {
    throw 'Package output must be new or empty; existing work is preserved.'
}
$published = (Resolve-Path -LiteralPath $PublishedDirectory).Path
foreach ($required in @('ScrcpyReceive.App.exe', 'ScrcpyReceive.App.pri', 'coreclr.dll', 'Microsoft.UI.Xaml.dll', 'LICENSE', 'THIRD_PARTY_NOTICES.txt',
    "native\win-$Architecture\avcodec-61.dll", "native\win-$Architecture\avutil-59.dll", "native\win-$Architecture\FFmpeg-LICENSE.txt",
    'licenses\ffmpeg.autogen-7.1.1\LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $published $required))) { throw "Incomplete self-contained publication: $required" }
}
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $published 'ScrcpyReceive.App.dll')).Version
if ($assemblyVersion -ne $number) {
    throw "Package version $Version differs from application build version $assemblyVersion. Publish with -Version $($number.ToString(3)) first."
}
if (@(Get-ChildItem -LiteralPath $published -Recurse -File -Filter '*.pdb').Count -gt 0) { throw 'Publication contains prohibited PDBs.' }
$nativeFiles = @('avcodec-61.dll', 'avutil-59.dll', 'swresample-5.dll',
    'FFmpeg-LICENSE.txt', 'FFmpeg-copyright.txt', 'FFmpeg-build-dependencies.txt', 'FFmpeg-dependencies.spdx.json')
$nativeDirectories = @(Get-ChildItem -LiteralPath (Join-Path $published 'native') -Force)
if ($nativeDirectories.Count -ne 1 -or -not $nativeDirectories[0].PSIsContainer -or
    $nativeDirectories[0].Name -ne "win-$Architecture") {
    throw 'Publication must contain only the selected native architecture.'
}
$nativeDirectory = Join-Path $published "native\win-$Architecture"
$nativeEntries = @(Get-ChildItem -LiteralPath $nativeDirectory -Force)
if ($nativeEntries.Count -ne $nativeFiles.Count -or
    @($nativeEntries | Where-Object { $_.PSIsContainer -or $nativeFiles -notcontains $_.Name }).Count -ne 0) {
    throw 'Publication contains unexpected native runtime assets.'
}

function Assert-PeArchitecture([string]$Path) {
    $stream = [System.IO.File]::OpenRead($Path)
    $reader = New-Object System.IO.BinaryReader($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5a4d) { throw "Invalid PE file: $Path" }
        $stream.Position = 0x3c
        $offset = $reader.ReadInt32()
        $stream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550) { throw "Invalid PE signature: $Path" }
        $machine = $reader.ReadUInt16()
        $expected = if ($Architecture -eq 'arm64') { 0xaa64 } else { 0x8664 }
        if ($machine -ne $expected) { throw "Wrong native architecture in $Path" }
    } finally { $reader.Dispose() }
}
foreach ($native in @('ScrcpyReceive.App.exe', 'coreclr.dll', 'Microsoft.UI.Xaml.dll',
    "native\win-$Architecture\avcodec-61.dll", "native\win-$Architecture\avutil-59.dll", "native\win-$Architecture\swresample-5.dll")) {
    Assert-PeArchitecture (Join-Path $published $native)
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$stage = Join-Path $output 'staging'
New-Item -ItemType Directory -Path $stage | Out-Null
Get-ChildItem -LiteralPath $published -Force | Copy-Item -Destination $stage -Recurse
$assets = Join-Path $stage 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
Add-Type -AssemblyName System.Drawing
foreach ($size in @(44, 50, 150)) {
    $image = New-Object System.Drawing.Bitmap($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($image)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [single]($size * 0.06))
    try {
        $graphics.Clear([System.Drawing.Color]::FromArgb(24, 78, 64))
        $graphics.DrawRectangle($pen, [single]($size * 0.16), [single]($size * 0.2), [single]($size * 0.68), [single]($size * 0.5))
        $graphics.DrawLine($pen, [single]($size * 0.35), [single]($size * 0.83), [single]($size * 0.65), [single]($size * 0.83))
        $image.Save((Join-Path $assets "Receiver$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally { $pen.Dispose(); $graphics.Dispose(); $image.Dispose() }
}
$identity = [System.Security.SecurityElement]::Escape($PackageName)
$publisherXml = [System.Security.SecurityElement]::Escape($Publisher)
$display = [System.Security.SecurityElement]::Escape($PublisherDisplayName)
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
 xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
 IgnorableNamespaces="uap rescap">
 <Identity Name="$identity" Publisher="$publisherXml" Version="$Version" ProcessorArchitecture="$Architecture"/>
 <Properties>
  <DisplayName>ScrcpyReceive-Windows</DisplayName>
  <PublisherDisplayName>$display</PublisherDisplayName>
  <Logo>Assets\Receiver50.png</Logo>
  <Description>Windows hardware H.264, HEVC and AV1 receiver preview with audio over TCP for trusted private networks.</Description>
 </Properties>
 <Resources><Resource Language="en-us"/></Resources>
 <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22621.0" MaxVersionTested="10.0.26100.0"/></Dependencies>
 <Applications>
  <Application Id="App" Executable="ScrcpyReceive.App.exe" EntryPoint="Windows.FullTrustApplication">
   <uap:VisualElements DisplayName="ScrcpyReceive-Windows" Description="Hardware video and audio receiver over TCP"
    BackgroundColor="#184E40" Square44x44Logo="Assets\Receiver44.png" Square150x150Logo="Assets\Receiver150.png"/>
  </Application>
 </Applications>
 <Capabilities>
  <Capability Name="internetClient"/>
  <Capability Name="privateNetworkClientServer"/>
  <rescap:Capability Name="runFullTrust"/>
 </Capabilities>
</Package>
"@
[System.IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding($false)))
$cache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x64' }
$tool = Join-Path $cache "microsoft.windows.sdk.buildtools\10.0.26100.4654\bin\10.0.26100.0\$hostArch\makeappx.exe"
if (-not (Test-Path -LiteralPath $tool)) { throw 'Pinned MakeAppx tool missing. Restore the application first.' }
$package = Join-Path $output "ScrcpyReceive-Windows-$Architecture.msix"
& $tool pack /d $stage /p $package /h SHA256 *> (Join-Path $output 'makeappx.log')
if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $output 'makeappx.log') -Tail 20; throw 'MSIX schema validation/packing failed.' }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($package)
try {
    if (@($archive.Entries | Where-Object { $_.FullName -match '\.pdb$' }).Count -ne 0) { throw 'MSIX contains PDBs.' }
    $entry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $entry) { throw 'MSIX manifest missing.' }
    $reader = New-Object System.IO.StreamReader($entry.Open())
    try { [xml]$packed = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ($packed.Package.Identity.Name -ne $PackageName -or $packed.Package.Identity.Publisher -ne $Publisher -or
        $packed.Package.Identity.ProcessorArchitecture -ne $Architecture -or $packed.Package.Identity.Version -ne $Version) {
        throw 'Packed identity differs from explicit receiver identity.'
    }
} finally { $archive.Dispose() }
Get-FileHash -LiteralPath $package -Algorithm SHA256
Write-Output "Unsigned package only; nothing installed or submitted: $package"
