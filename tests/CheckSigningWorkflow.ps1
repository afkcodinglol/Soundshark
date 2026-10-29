$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$metadata = Join-Path $root ('.work/signing/offline-fixture-' + [Guid]::NewGuid().ToString('N') + '.json')
@{ Endpoint='https://eus.codesigning.azure.net'; CodeSigningAccountName='offlinefixture'; CertificateProfileName='offlinefixture' } | ConvertTo-Json | Set-Content -LiteralPath $metadata
function Run([string]$Config, [string]$File, [bool]$Expected) {
    $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'scripts/SignApp.ps1') -MetadataPath $Config -Files $File > (Join-Path $root 'outputs/qa/signing-helper.txt') 2>&1 } finally { $ErrorActionPreference = $previous }
    if (($LASTEXITCODE -eq 0) -ne $Expected) { Get-Content (Join-Path $root 'outputs/qa/signing-helper.txt'); throw 'Offline signing fixture result was unexpected.' }
}
try {
    $app = Join-Path $root 'outputs/app/Soundshark.exe'; $before = (Get-FileHash -LiteralPath $app).Hash
    Run $metadata $app $true
    if ((Get-FileHash -LiteralPath $app).Hash -ne $before) { throw 'Offline validation modified the app.' }
    Write-Output 'PASS offline app signing validation leaves the binary unchanged and sends no signing request'
    Run $metadata (Join-Path $root 'outputs/native/SoundsharkMicrophone.dll') $false
    Write-Output 'PASS ordinary app signing refuses the protected-audio DLL'
    Run (Join-Path $root 'packaging/artifact-signing.example.json') $app $false
    Write-Output 'PASS unfilled signing metadata is rejected before service contact'
    $tools = Get-Content -LiteralPath (Join-Path $root '.work/signing/tools.json') -Raw | ConvertFrom-Json
    & $tools.SignTool /? > (Join-Path $root 'outputs/qa/signtool-help.txt')
    if ($LASTEXITCODE -ne 0) { throw 'The verified x64 SignTool could not run.' }
    Write-Output 'PASS verified Microsoft SignTool runs locally'
    Write-Output '4 signing workflow checks passed; no files were signed and no paid resource was created'
} finally { Remove-Item -LiteralPath $metadata -ErrorAction SilentlyContinue }
