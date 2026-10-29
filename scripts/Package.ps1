param([string]$SigningCertificateThumbprint, [string]$SignToolPath, [string]$ArtifactSigningMetadataPath, [switch]$ReuseNative)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if ($ReuseNative) {
        # App-only updates retain the exact previously tested microphone DLL and installer.
        foreach ($file in 'SoundsharkMicrophone.dll','Soundshark-Microphone-Setup-x64.exe') {
            if (!(Test-Path -LiteralPath (Join-Path $root "outputs/native/$file"))) { throw 'The existing native package is missing. Build without -ReuseNative.' }
            if ((Get-FileHash (Join-Path $root "outputs/native/$file")).Hash -ne (Get-FileHash (Join-Path $root "outputs/app/Native/$file")).Hash) { throw 'The native package differs from the previous published payload. Build without -ReuseNative.' }
        }
        if ($SigningCertificateThumbprint -or $ArtifactSigningMetadataPath) { throw 'Build without -ReuseNative when signing a release.' }
    } else {
        & (Join-Path $PSScriptRoot 'BuildNative.ps1') -Checks
        if (!$?) { throw 'Native build or checks failed' }
    }
    if ($SigningCertificateThumbprint) {
        if ($SigningCertificateThumbprint -notmatch '^[0-9a-fA-F]{40}$' -or !$SignToolPath -or ![IO.Path]::IsPathRooted($SignToolPath) -or !(Test-Path -LiteralPath $SignToolPath -PathType Leaf)) { throw 'Supply a valid certificate thumbprint and absolute Windows SDK signtool path.' }
        & $SignToolPath sign /sha1 $SigningCertificateThumbprint /ph /fd SHA256 /tr 'https://timestamp.digicert.com' /td SHA256 outputs/native/SoundsharkMicrophone.dll
        if ($LASTEXITCODE -ne 0 -or (Get-AuthenticodeSignature -LiteralPath 'outputs/native/SoundsharkMicrophone.dll').Status -ne 'Valid') { throw 'Native signing or trust verification failed; no package was produced.' }
    }
    if (!$ReuseNative) {
        & 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' packaging\SoundsharkMicrophone.iss
        if ($LASTEXITCODE -ne 0) { throw 'Microphone setup packaging failed' }
    }
    if ($ArtifactSigningMetadataPath) { & (Join-Path $PSScriptRoot 'SignApp.ps1') -MetadataPath $ArtifactSigningMetadataPath -Files (Join-Path $root 'outputs/native/Soundshark-Microphone-Setup-x64.exe') -Sign }
    # Publish into an empty directory so obsolete dependencies never enter a release.
    $freshPublish = Join-Path $root ('outputs/publish-' + [Guid]::NewGuid().ToString('N'))
    dotnet publish src\Soundshark -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $freshPublish
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
    $publishedApp = [IO.Path]::GetFullPath((Join-Path $root 'outputs/app'))
    $expectedApp = [IO.Path]::GetFullPath((Join-Path $root 'outputs/app'))
    if ($publishedApp -ne $expectedApp -or !(Test-Path -LiteralPath (Join-Path $freshPublish 'Soundshark.exe'))) { throw 'Unexpected publish output path.' }
    if (Test-Path -LiteralPath (Join-Path $publishedApp 'Library')) { throw 'Build output contains a Library directory. Preserve it outside outputs/app before packaging.' }
    if (Test-Path -LiteralPath $publishedApp) { Remove-Item -LiteralPath $publishedApp -Recurse -Force }
    Move-Item -LiteralPath $freshPublish -Destination $publishedApp
    if ($ArtifactSigningMetadataPath) { & (Join-Path $PSScriptRoot 'SignApp.ps1') -MetadataPath $ArtifactSigningMetadataPath -Files @((Join-Path $root 'outputs/app/Soundshark.exe'), (Join-Path $root 'outputs/app/Soundshark.dll')) -Sign }
    Copy-Item docs\NOTICES.txt outputs\app\NOTICES.txt -Force
    Copy-Item docs\DEPENDENCIES.md outputs\app\DEPENDENCIES.md -Force
    Copy-Item README.md outputs\app\README.md -Force
    Copy-Item LICENSE outputs\app\LICENSE -Force
    New-Item -ItemType Directory -Force -Path outputs/app/docs | Out-Null
    Copy-Item docs/MICROPHONE.md,docs/SIGNING.md,docs/SIGNING-INQUIRY.md,docs/UPDATES.md,docs/USER-GUIDE.md,docs/SOUNDS-HUB.md -Destination outputs/app/docs -Force
    $nativeFolder = Join-Path $root 'outputs/app/Native'
    New-Item -ItemType Directory -Force -Path $nativeFolder | Out-Null
    Copy-Item outputs/native/SoundsharkMicrophone.dll,outputs/native/Soundshark-Microphone-Setup-x64.exe,docs/MICROPHONE.md -Destination $nativeFolder -Force
    $developmentNotice = Join-Path $nativeFolder 'UNSIGNED-DEVELOPMENT.txt'
    # Ordinary publisher signing does not establish Microsoft protected-environment approval.
    Set-Content -LiteralPath $developmentNotice -Value 'Experimental physical microphone playback: unsigned native DLL. Explicit experimental setup disables protected AudioDG system-wide, backs up the original setting and restores it after the last configured microphone is removed. Windows restart and live testing are required. See MICROPHONE.md.'
    Copy-Item docs/SIGNING.md,docs/SIGNING-INQUIRY.md -Destination $nativeFolder -Force
    Copy-Item docs\ACCEPTANCE.md outputs\ACCEPTANCE.md -Force
    & 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' packaging\Soundshark.iss
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
    if ($ArtifactSigningMetadataPath) { & (Join-Path $PSScriptRoot 'SignApp.ps1') -MetadataPath $ArtifactSigningMetadataPath -Files (Join-Path $root 'outputs/Soundshark-Setup-x64.exe') -Sign }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sourceZip = Join-Path $root 'outputs\Soundshark-source.zip'
    if (Test-Path -LiteralPath $sourceZip) { Remove-Item -LiteralPath $sourceZip }
    $archive = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $sourceFiles = @(Get-ChildItem src,tests,assets,scripts,packaging,docs -Recurse -File | Where-Object FullName -NotMatch '\\(bin|obj|node_modules)\\') + @(Get-Item global.json,.gitignore,README.md,LICENSE,update-source.json)
        if (Test-Path -LiteralPath 'hub-source.json') { $sourceFiles += Get-Item hub-source.json }
        foreach ($sourceFile in $sourceFiles) {
            $entry = $sourceFile.FullName.Substring($root.Length + 1).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $sourceFile.FullName, $entry) | Out-Null
        }
    } finally { $archive.Dispose() }
    $portableZip = Join-Path $root 'outputs\Soundshark-portable-x64.zip'
    if (Test-Path -LiteralPath $portableZip) { Remove-Item -LiteralPath $portableZip }
    [IO.Compression.ZipFile]::CreateFromDirectory((Join-Path $root 'outputs\app'), $portableZip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $checksums = foreach ($assetName in 'Soundshark-Setup-x64.exe','Soundshark-portable-x64.zip','Soundshark-source.zip') {
        $hash = (Get-FileHash -LiteralPath (Join-Path $root "outputs/$assetName") -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $assetName"
    }
    $checksums | Set-Content -LiteralPath (Join-Path $root 'outputs/SHA256SUMS.txt')
} finally { Pop-Location }
