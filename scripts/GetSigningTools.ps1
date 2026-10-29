$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$destination = Join-Path $root '.work/signing'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packages = @{'microsoft.windows.sdk.buildtools'='10.0.28000.2705'; 'microsoft.artifactsigning.client'='1.0.146'}
foreach ($name in $packages.Keys) {
    $version = $packages[$name]; $folder = Join-Path $destination "$name-$version"
    $archive = Join-Path $destination "$name.$version.nupkg"
    if (!(Test-Path -LiteralPath $archive)) { Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$name/$version/$name.$version.nupkg" -OutFile $archive }
    dotnet nuget verify $archive --all
    if ($LASTEXITCODE -ne 0) { throw "NuGet signature verification failed for $name. No tools were executed." }
    if (!(Test-Path -LiteralPath $folder)) { [IO.Compression.ZipFile]::ExtractToDirectory($archive, $folder) }
}
$signtool = Get-ChildItem -LiteralPath (Join-Path $destination 'microsoft.windows.sdk.buildtools-10.0.28000.2705') -Filter signtool.exe -Recurse -File | Where-Object FullName -Match '\\x64\\' | Select-Object -First 1
$dlib = Get-ChildItem -LiteralPath (Join-Path $destination 'microsoft.artifactsigning.client-1.0.146') -Filter Azure.CodeSigning.Dlib.dll -Recurse -File | Where-Object FullName -Match '\\x64\\' | Select-Object -First 1
if (!$signtool -or !$dlib) { throw 'The official signing tools were not found in the pinned packages.' }
$signature = Get-AuthenticodeSignature -LiteralPath $signtool.FullName
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw 'SignTool is not signed by a trusted Microsoft publisher.' }
[pscustomobject]@{ SignTool=$signtool.FullName; Dlib=$dlib.FullName; Packages=$packages } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'tools.json')
Write-Output 'Verified signing tools prepared locally. No account, paid resource, certificate trust or audio endpoint was changed.'
