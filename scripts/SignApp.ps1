param([Parameter(Mandatory=$true)][string]$MetadataPath, [Parameter(Mandatory=$true)][string[]]$Files, [switch]$Sign)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$toolsFile = Join-Path $root '.work/signing/tools.json'
if (!(Test-Path -LiteralPath $toolsFile)) { throw 'Run scripts/GetSigningTools.ps1 first.' }
$tools = Get-Content -LiteralPath $toolsFile -Raw | ConvertFrom-Json
if (!(Test-Path -LiteralPath $tools.SignTool) -or !(Test-Path -LiteralPath $tools.Dlib)) { throw 'Signing tools are missing. Run GetSigningTools.ps1 again.' }
$metadataFile = [IO.Path]::GetFullPath($MetadataPath)
$metadata = Get-Content -LiteralPath $metadataFile -Raw | ConvertFrom-Json
$endpoint = [Uri]$metadata.Endpoint
if (!$endpoint.IsAbsoluteUri -or $endpoint.Scheme -ne 'https' -or $endpoint.DnsSafeHost -notmatch '^[a-z]+\.codesigning\.azure\.net$' -or $endpoint.UserInfo -or $endpoint.Query -or $endpoint.AbsolutePath -ne '/') { throw 'Use the exact HTTPS regional Artifact Signing endpoint from Azure.' }
foreach ($property in 'CodeSigningAccountName','CertificateProfileName') { if ([string]::IsNullOrWhiteSpace($metadata.$property) -or $metadata.$property -match '[<>]') { throw "Fill in $property from your validated Azure profile." } }
$targets = foreach ($file in $Files) {
    $path = [IO.Path]::GetFullPath($file)
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Signing input is missing: $path" }
    if ([IO.Path]::GetFileName($path) -eq 'SoundsharkMicrophone.dll') { throw 'This app-signing workflow cannot approve the protected-audio DLL. Use the Microsoft hardware signing workflow.' }
    $path
}
if (!$Sign) { Write-Output 'App signing inputs validated offline. No files were signed or uploaded, and protected-audio trust remains unverified.'; exit 0 }
if (!(Get-Command az -ErrorAction SilentlyContinue)) { throw 'Install Azure CLI and sign in with az login first. No signing request was sent.' }
$logFolder = Join-Path $root 'outputs/signing'; New-Item -ItemType Directory -Force -Path $logFolder | Out-Null
$receipt = @()
foreach ($target in $targets) {
    $before = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    & $tools.SignTool sign /v /fd SHA256 /tr 'http://timestamp.acs.microsoft.com' /td SHA256 /dlib $tools.Dlib /dmdf $metadataFile $target
    if ($LASTEXITCODE -ne 0) { throw 'Artifact Signing failed. Check Azure sign-in, identity validation, role and profile. No release should be distributed.' }
    & $tools.SignTool verify /pa /all /v $target
    if ($LASTEXITCODE -ne 0) { throw 'Signed app verification failed.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $target
    if ($signature.Status -ne 'Valid' -or !$signature.TimeStamperCertificate) { throw 'The app signature needs a valid trust chain and timestamp.' }
    $receipt += [pscustomobject]@{ File=$target; BeforeSHA256=$before; SignedSHA256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash; Publisher=$signature.SignerCertificate.Subject; TimestampPublisher=$signature.TimeStamperCertificate.Subject; UTC=[DateTime]::UtcNow.ToString('o') }
}
$receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $logFolder ('app-signing-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '.json'))
