$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tools = Join-Path $root 'tools'
$sources = Join-Path $root 'outputs\dependency-sources'
New-Item -ItemType Directory -Force $tools,$sources,(Join-Path $tools 'licenses') | Out-Null
$ff = (Get-Command ffmpeg.exe).Source
if ((& $ff -version | Select-Object -First 1) -notmatch '^ffmpeg version 8\.1\.2-full_build-www\.gyan\.dev ') { throw 'Install the pinned Gyan FFmpeg 8.1.2 full build before fetching tools.' }
$ffroot = Split-Path (Split-Path $ff -Parent) -Parent
Copy-Item -LiteralPath $ff -Destination (Join-Path $tools 'ffmpeg.exe')
Copy-Item -LiteralPath (Join-Path (Split-Path $ff -Parent) 'ffprobe.exe') -Destination (Join-Path $tools 'ffprobe.exe')
Copy-Item -LiteralPath (Join-Path $ffroot 'LICENSE') -Destination (Join-Path $tools 'licenses\FFmpeg-GPL.txt')
Copy-Item -LiteralPath (Join-Path $ffroot 'README.txt') -Destination (Join-Path $tools 'licenses\FFmpeg-build.txt') -ErrorAction SilentlyContinue
$ytdlp = '2026.08.19'
$deno = 'v2.9.7'
$ytbase = "https://github.com/yt-dlp/yt-dlp/releases/download/$ytdlp"
if (-not (Test-Path (Join-Path $tools 'yt-dlp.exe'))) { Invoke-WebRequest "$ytbase/yt-dlp.exe" -OutFile (Join-Path $tools 'yt-dlp.exe') }
$sums = (Invoke-WebRequest "$ytbase/SHA2-256SUMS").Content
if ($sums -is [byte[]]) { $sums = [Text.Encoding]::UTF8.GetString($sums) }
$expected = (($sums -split "`n" | Where-Object { $_ -match '  yt-dlp.exe\s*$' }) -split '\s+')[0]
if ((Get-FileHash (Join-Path $tools 'yt-dlp.exe') -Algorithm SHA256).Hash -ne $expected) { throw 'yt-dlp checksum mismatch' }
$release = Invoke-RestMethod "https://api.github.com/repos/denoland/deno/releases/tags/$deno"
$asset = $release.assets | Where-Object name -eq 'deno-x86_64-pc-windows-msvc.zip'
$zip = Join-Path $sources 'deno-win-x64.zip'
Invoke-WebRequest $asset.browser_download_url -OutFile $zip
if (-not $asset.digest -or ('sha256:' + (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()) -ne $asset.digest) { throw 'Deno checksum mismatch' }
Expand-Archive -LiteralPath $zip -DestinationPath $tools -Force
Invoke-WebRequest "https://raw.githubusercontent.com/yt-dlp/yt-dlp/$ytdlp/LICENSE" -OutFile (Join-Path $tools 'licenses\yt-dlp.txt')
Invoke-WebRequest "https://raw.githubusercontent.com/denoland/deno/$deno/LICENSE.md" -OutFile (Join-Path $tools 'licenses\Deno.txt')
Invoke-WebRequest "https://github.com/yt-dlp/yt-dlp/archive/refs/tags/$ytdlp.zip" -OutFile (Join-Path $sources 'yt-dlp-source.zip')
Invoke-WebRequest "https://github.com/denoland/deno/archive/refs/tags/$deno.zip" -OutFile (Join-Path $sources 'deno-source.zip')
# GPL FFmpeg corresponding source and build recipes, alongside the unmodified build.
Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-full_build.7z' -OutFile (Join-Path $sources 'ffmpeg-8.1.2-full_build.7z')
$ffDigest = (Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-8.1.2-full_build.7z.sha256').Content
if ($ffDigest -is [byte[]]) { $ffDigest = [Text.Encoding]::UTF8.GetString($ffDigest) }
if ((Get-FileHash (Join-Path $sources 'ffmpeg-8.1.2-full_build.7z')).Hash -ne $ffDigest.Trim()) { throw 'FFmpeg archive checksum mismatch' }
Invoke-WebRequest 'https://ffmpeg.org/releases/ffmpeg-8.1.2.tar.xz' -OutFile (Join-Path $sources 'ffmpeg-8.1.2.tar.xz')
Invoke-WebRequest 'https://github.com/yt-dlp/yt-dlp/raw/refs/tags/2026.08.19/THIRD_PARTY_LICENSES.txt' -OutFile (Join-Path $tools 'licenses\yt-dlp-third-party.txt') -ErrorAction Continue
Invoke-WebRequest 'https://raw.githubusercontent.com/naudio/NAudio/v2.2.1/license.txt' -OutFile (Join-Path $tools 'licenses\NAudio-MIT.txt')
Invoke-WebRequest 'https://github.com/naudio/NAudio/archive/refs/tags/v2.2.1.zip' -OutFile (Join-Path $sources 'NAudio-2.2.1-source.zip')
Invoke-WebRequest 'https://raw.githubusercontent.com/dotnet/runtime/v10.0.12/LICENSE.TXT' -OutFile (Join-Path $tools 'licenses\dotnet-MIT.txt')
Invoke-WebRequest 'https://raw.githubusercontent.com/dotnet/runtime/v10.0.12/THIRD-PARTY-NOTICES.TXT' -OutFile (Join-Path $tools 'licenses\dotnet-third-party.txt')
Invoke-WebRequest 'https://raw.githubusercontent.com/dotnet/wpf/v10.0.12/LICENSE.TXT' -OutFile (Join-Path $tools 'licenses\WPF-MIT.txt')
Invoke-WebRequest 'https://raw.githubusercontent.com/dotnet/wpf/v10.0.12/THIRD-PARTY-NOTICES.TXT' -OutFile (Join-Path $tools 'licenses\WPF-third-party.txt')
Copy-Item 'C:\Program Files (x86)\Inno Setup 6\License.txt' (Join-Path $tools 'licenses\Inno-Setup.txt')
$manifest = [ordered]@{ ffmpeg='8.1.2-full_build-www.gyan.dev'; 'yt-dlp'=$ytdlp; deno=$deno; files=@(Get-ChildItem $tools -File | ForEach-Object { @{ name=$_.Name; sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } }) }
$manifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $tools 'manifest.json') -Encoding utf8
