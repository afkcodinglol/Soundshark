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
function RunHelper([switch]$Restore, [switch]$RestoreAll, [bool]$Expected = $true) {
    $arguments = @('-NoProfile','-ExecutionPolicy','Bypass','-File', (Join-Path $root 'scripts/ConfigureMicrophone.ps1'), '-TestRoot', $registryRoot, '-TestBackupDirectory', $backups, '-EndpointGuid', $endpoint, '-UserSid', 'S-1-5-21-1-2-3-1001', '-Experimental')
    if ($Restore) { $arguments += '-Restore' }
    if ($RestoreAll) { $arguments += '-RestoreAll' }
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
    $repairRequired=$false
    try { & (Join-Path $root 'scripts/ExperimentalAudioPolicy.ps1') -RestoreIfUnused -TestRoot $registryRoot -TestBackupDirectory $otherBackups } catch { $repairRequired=$_.Exception.Message -like '*preserved*' }
    if (!$repairRequired) { throw 'Conflicting policy did not request repair' }
    if ($policyKey.GetValue('DisableProtectedAudioDG') -ne 2) { throw 'Later policy change overwritten' }
    $policyKey.DeleteValue('DisableProtectedAudioDG', $false)
    $policyKey.Dispose(); Write-Output 'PASS later third-party policy changes are preserved'
    # Independent cleanup fixtures retain original endpoint and policy backups throughout.
    $endpoint = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
    $fixture = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$registryRoot\$endpoint"); $fixture.Dispose()
    RunHelper
    $backupFile = Join-Path $backups ($endpoint + '.json')
    [IO.File]::Move($backupFile, $backupFile + '.hold')
    RunHelper -RestoreAll -Expected $false
    [IO.File]::Move($backupFile + '.hold', $backupFile)
    Write-Output 'PASS missing endpoint backup blocks full cleanup'
    $policyFile = Join-Path $backups 'PolicyBackup/AudioPolicy.json'
    [IO.File]::Move($policyFile, $policyFile + '.hold')
    RunHelper -RestoreAll -Expected $false
    [IO.File]::Move($policyFile + '.hold', $policyFile)
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$registryRoot\$endpoint\FxProperties", $true)
    if ($key.GetValue($slot) -ne $clsid) { throw 'Missing policy backup partially restored endpoint' }
    Write-Output 'PASS missing policy backup leaves endpoint routing untouched and requests repair'
    $originalModes = [string[]]$key.GetValue($modesName)
    $key.SetValue($modesName, [string[]]@('{11111111-1111-1111-1111-111111111111}'), [Microsoft.Win32.RegistryValueKind]::MultiString)
    RunHelper -RestoreAll -Expected $false
    if ($key.GetValue($modesName)[0] -ne '{11111111-1111-1111-1111-111111111111}') { throw 'Conflicting modes overwritten' }
    $key.SetValue($modesName, $originalModes, [Microsoft.Win32.RegistryValueKind]::MultiString)
    Write-Output 'PASS cleanup preserves conflicting processing modes'
    $firstEndpoint = $endpoint
    $endpoint = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
    $fixture = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$registryRoot\$endpoint"); $fixture.Dispose(); RunHelper
    $otherKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$registryRoot\$endpoint\FxProperties", $true)
    $otherKey.SetValue($slot, '{22222222-2222-2222-2222-222222222222}')
    RunHelper -RestoreAll -Expected $false
    if ($key.GetValue($slot) -ne $clsid) { throw 'All-endpoint preflight restored one endpoint before finding conflict' }
    $otherKey.SetValue($slot, $clsid); $otherKey.Dispose()
    Write-Output 'PASS full cleanup preflights every endpoint before making changes'
    $saved = Get-Content -LiteralPath $backupFile -Raw | ConvertFrom-Json
    $saved.PSObject.Properties.Remove('InstalledValues'); $saved.PSObject.Properties.Remove('UserSid'); $saved.Version = 2
    $saved | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $backupFile
    RunHelper -RestoreAll
    $key.Dispose(); $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey("$registryRoot\$firstEndpoint\FxProperties")
    if ($key -and $key.GetValueNames() -contains $slot) { throw 'Legacy endpoint backup not restored' }
    Write-Output 'PASS legacy version 2 endpoint backups restore with the original setup user'
    RunHelper -RestoreAll
    Write-Output 'PASS repeated full cleanup is idempotent'
    $policyKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey("$registryRoot\Policy")
    $policyKey.SetValue('DisableProtectedAudioDG', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
    $originalOne = Join-Path $backups 'OriginalOne'
    & (Join-Path $root 'scripts/ExperimentalAudioPolicy.ps1') -Enable -TestRoot $registryRoot -TestBackupDirectory $originalOne
    & (Join-Path $root 'scripts/ExperimentalAudioPolicy.ps1') -RestoreIfUnused -TestRoot $registryRoot -TestBackupDirectory $originalOne
    & (Join-Path $root 'scripts/ExperimentalAudioPolicy.ps1') -RestoreIfUnused -TestRoot $registryRoot -TestBackupDirectory $originalOne
    if ($policyKey.GetValue('DisableProtectedAudioDG') -ne 1) { throw 'Original disabled policy was changed' }
    $policyKey.Dispose(); Write-Output 'PASS an originally disabled policy is preserved and permits repeated cleanup'
    Write-Output '20 isolated registry checks passed; no live audio endpoint was changed'
} finally {
    if ($key) { $key.Dispose() }
    # Exact fixture subtree created above; never enumerate or remove real audio-device keys.
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($registryRoot, $false)
}
