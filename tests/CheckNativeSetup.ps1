$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$testId = [Guid]::NewGuid().ToString()
$registryRoot = "Software\SoundsharkNativeChecks\$testId"
$endpoint = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
$backups = Join-Path $root "outputs/qa/native-registry-$testId"
$slot = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7'
$clsid = '{A08799CD-8535-492D-A1B4-F4C8B51E8779}'
$key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$registryRoot\$endpoint\FxProperties")
$key.SetValue('UnrelatedSetting', 'Keep Me')
function RunHelper([switch]$Restore, [bool]$Expected = $true) {
    $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File', (Join-Path $root 'scripts/ConfigureMicrophone.ps1'), '-TestRoot', $registryRoot, '-TestBackupDirectory', $backups, '-EndpointGuid', $endpoint, '-UserSid', 'S-1-5-21-1-2-3-1001', '-Experimental')
    if ($Restore) { $arguments += '-Restore' }
    $previousPreference = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & powershell @arguments > (Join-Path $root 'outputs/qa/native-registry-helper.txt') 2>&1 } finally { $ErrorActionPreference = $previousPreference }
    if (($LASTEXITCODE -eq 0) -ne $Expected) { Get-Content (Join-Path $root 'outputs/qa/native-registry-helper.txt'); throw 'Registry helper result did not match expectation.' }
}
try {
    RunHelper
    $policyKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$registryRoot\Policy")
    if ($policyKey.GetValue('DisableProtectedAudioDG') -ne 1 -or !(Test-Path -LiteralPath (Join-Path $backups 'PolicyBackup/AudioPolicy.json'))) { throw 'Missing experimental policy/backup' }
    $policyKey.Dispose(); Write-Output 'PASS experimental audio policy is backed up before being enabled'
    if ($key.GetValue($slot) -ne $clsid -or $key.GetValue('UnrelatedSetting') -ne 'Keep Me') { throw 'Attach fixture failed' }; Write-Output 'PASS native attachment preserves unrelated endpoint properties'
    if (!(Test-Path -LiteralPath (Join-Path $backups ($endpoint + '.json')))) { throw 'Missing original backup' }; Write-Output 'PASS original effect properties are backed up before attachment'
    RunHelper -Restore
    $policyKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$registryRoot\Policy")
    if ($policyKey.GetValueNames() -contains 'DisableProtectedAudioDG') { throw 'Original policy absence not restored' }
    $policyKey.Dispose(); Write-Output 'PASS last endpoint removal restores original policy absence'
    if ($key.GetValueNames() -contains $slot) { throw 'Restore did not remove previously absent slot' }; Write-Output 'PASS restore preserves originally absent properties'
    RunHelper
    if ($key.GetValue($slot) -ne $clsid) { throw 'Reattach failed' }; Write-Output 'PASS reattachment reuses the verified original backup'
    $key.SetValue($slot, '{F72B5964-56FC-40C7-945D-FBDA95C9D236}')
    RunHelper -Restore -Expected $false
    if ($key.GetValue($slot) -ne '{F72B5964-56FC-40C7-945D-FBDA95C9D236}') { throw 'Third-party value was overwritten' }; Write-Output 'PASS restore refuses to overwrite a later third-party change'
    $key.SetValue($slot, $clsid); RunHelper -Restore
    $key.Dispose()
    $endpoint = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
    $fresh = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$registryRoot\$endpoint"); $fresh.Dispose()
    RunHelper
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$registryRoot\$endpoint\FxProperties")
    if ($key.GetValue($slot) -ne $clsid -or $key.GetValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},0') -ne '{00000000-0000-0000-0000-000000000000}') { throw 'Clean effects store attachment failed' }
    $key.Dispose(); $key = $null
    Write-Output 'PASS clean microphone creates a backed-up effects store and association'
    RunHelper -Restore
    $remaining = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$registryRoot\$endpoint\FxProperties")
    if ($remaining) { $remaining.Dispose(); throw 'Created effects store was not removed' }
    Write-Output 'PASS clean microphone restore removes only its empty created effects store'
    $endpoint = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$registryRoot\$endpoint\FxProperties")
    $originalEffect = '{F72B5964-56FC-40C7-945D-FBDA95C9D236}'
    $modesName = '{d3993a3f-99c2-4402-b5ec-a92a0367664b},7'
    $key.SetValue($slot, $originalEffect); $key.SetValue($modesName, [string[]]@('{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}'), [Microsoft.Win32.RegistryValueKind]::MultiString)
    RunHelper
    if (@($key.GetValue($modesName)).Count -ne 1) { throw 'Existing processing modes were expanded' }
    RunHelper -Restore
    if ($key.GetValue($slot) -ne $originalEffect) { throw 'Existing effect was not restored' }
    Write-Output 'PASS default-only microphone effects keep their exact original modes and restore their original class'
    $policyKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$registryRoot\Policy")
    $policyKey.SetValue('DisableProtectedAudioDG', 0, [Microsoft.Win32.RegistryValueKind]::DWord)
    $otherBackups = Join-Path $backups 'ExplicitZero'
    & (Join-Path $root 'scripts/ExperimentalAudioPolicy.ps1') -Enable -TestRoot $registryRoot -TestBackupDirectory $otherBackups
    $holdId = [Guid]::NewGuid().ToString('B')
    $hold = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$registryRoot\$holdId\FxProperties"); $hold.SetValue($slot, $clsid)
    & (Join-Path $root 'scripts/ExperimentalAudioPolicy.ps1') -RestoreIfUnused -TestRoot $registryRoot -TestBackupDirectory $otherBackups
    if ($policyKey.GetValue('DisableProtectedAudioDG') -ne 1) { throw 'Policy restored while another Soundshark microphone remained configured' }
    $hold.DeleteValue($slot); $hold.Dispose()
    Write-Output 'PASS global policy stays enabled while another Soundshark microphone remains configured'
    & (Join-Path $root 'scripts/ExperimentalAudioPolicy.ps1') -RestoreIfUnused -TestRoot $registryRoot -TestBackupDirectory $otherBackups
    if ($policyKey.GetValue('DisableProtectedAudioDG') -ne 0) { throw 'Explicit zero not restored' }
    Write-Output 'PASS explicit protected-audio policy value is restored exactly'
    & (Join-Path $root 'scripts/ExperimentalAudioPolicy.ps1') -Enable -TestRoot $registryRoot -TestBackupDirectory $otherBackups
    $policyKey.SetValue('DisableProtectedAudioDG', 2, [Microsoft.Win32.RegistryValueKind]::DWord)
    & (Join-Path $root 'scripts/ExperimentalAudioPolicy.ps1') -RestoreIfUnused -TestRoot $registryRoot -TestBackupDirectory $otherBackups
    if ($policyKey.GetValue('DisableProtectedAudioDG') -ne 2) { throw 'Later policy change overwritten' }
    $policyKey.Dispose(); Write-Output 'PASS later third-party policy changes are preserved'
    Write-Output '13 isolated registry checks passed; no live audio endpoint was changed'
} finally {
    if ($key) { $key.Dispose() }
    # Exact fixture subtree created above; never enumerate or remove real audio-device keys.
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($registryRoot, $false)
}
