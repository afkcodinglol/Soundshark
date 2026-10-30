param([string]$Version = '1.9.2-local')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($Version -notmatch '^\d+\.\d+\.\d+-local$') { throw 'This script packages local previews only.' }
$destination = Join-Path $root ('outputs/virtual-mic-local/' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss'))
$app = Join-Path $destination 'Soundshark'
$native = Join-Path $app 'Native'
New-Item -ItemType Directory -Path $native -Force | Out-Null
Push-Location $root
try {
    dotnet publish src/Soundshark -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false "-p:Version=$Version" -o $app --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Local publish failed.' }
    Copy-Item -LiteralPath outputs/native/SoundsharkMicrophone.dll -Destination $native
    & 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' "/O$native" packaging/SoundsharkMicrophone.iss
    if ($LASTEXITCODE -ne 0) { throw 'Local microphone cleanup/setup packaging failed.' }
    New-Item -ItemType Directory -Path (Join-Path $app 'docs') | Out-Null
    Copy-Item README.md,LICENSE -Destination $app
    Copy-Item docs/*.md,docs/NOTICES.txt,docs/EXPERIMENTAL-MICROPHONE.txt -Destination (Join-Path $app 'docs')
    Copy-Item docs/MICROPHONE.md -Destination $native
    Set-Content -LiteralPath (Join-Path $native 'UNSIGNED-DEVELOPMENT.txt') -Value 'Optional physical microphone support is unsigned and requires explicit experimental setup, disabling protected AudioDG. VB-CABLE routing requires no policy change. See docs/MICROPHONE.md.'
    Set-Content -LiteralPath (Join-Path $app 'LOCAL-PREVIEW.txt') -Value "Soundshark $Version local preview. Not a public release. VB-CABLE is not included. Live cable/Discord/game/Windows 11 validation is outstanding. See docs/VIRTUAL-MIC-VALIDATION.md."
    $original = (Get-FileHash -LiteralPath outputs/native/SoundsharkMicrophone.dll).Hash
    if ((Get-FileHash -LiteralPath (Join-Path $native 'SoundsharkMicrophone.dll')).Hash -ne $original) { throw 'Native DLL copy hash mismatch.' }
    $manifest = Get-ChildItem -LiteralPath $app -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($app.Length + 1).Replace('\','/')
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant(), $relative
    }
    $manifest | Set-Content -LiteralPath (Join-Path $destination 'FILES-SHA256.txt')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $destination "Soundshark-$Version-portable-x64.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($app, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    '{0}  {1}' -f (Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant(), ([IO.Path]::GetFileName($zip)) | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt')
    Write-Output "LOCAL_PORTABLE=$zip"
} finally { Pop-Location }
