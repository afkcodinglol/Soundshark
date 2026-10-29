param(
    [string]$EndpointGuid,
    [string]$UserSid,
    [switch]$Restore,
    [switch]$RestoreAll,
    [switch]$PlanOnly,
    [switch]$Experimental,
    [string]$TestRoot,
    [string]$TestBackupDirectory
)
$ErrorActionPreference = 'Stop'
$clsid = '{A08799CD-8535-492D-A1B4-F4C8B51E8779}'
$slot = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7'
$modes = '{d3993a3f-99c2-4402-b5ec-a92a0367664b},7'
$previous = '{a08799cd-8535-492d-a1b4-f4c8b51e8779},1'
$sidProperty = '{a08799cd-8535-492d-a1b4-f4c8b51e8779},2'
$association = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},0'
$names = @($slot, $modes, $previous, $sidProperty, $association)
$requiredModes = @('{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}', '{98951333-B9CD-48B1-A0A3-FF40682D73F7}', '{9E90EA20-B493-4FD1-A1A8-7E1361A956CF}')
$base = [Microsoft.Win32.Registry]::LocalMachine
$root = 'SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture'
$backupDirectory = Join-Path $PSScriptRoot 'EndpointBackups'
if ($TestRoot) {
    if ($TestRoot -notmatch '^Software\\SoundsharkNativeChecks\\[0-9a-fA-F-]{36}$' -or !$TestBackupDirectory) { throw 'Invalid isolated test registry root.' }
    $base = [Microsoft.Win32.Registry]::CurrentUser; $root = $TestRoot; $backupDirectory = [IO.Path]::GetFullPath($TestBackupDirectory)
}
if (!$TestRoot -and !$PlanOnly) {
    $protectedRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'SoundsharkAudio'
    if (![string]::Equals([IO.Path]::GetFullPath($PSScriptRoot), [IO.Path]::GetFullPath($protectedRoot), [StringComparison]::OrdinalIgnoreCase)) { throw 'Run endpoint configuration only from the protected microphone support installation.' }
    $logDirectory = Join-Path $protectedRoot 'SetupLogs'
    New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null
    $setupLog = Join-Path $logDirectory ('setup-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') + '.log')
    Start-Transcript -LiteralPath $setupLog -Force | Out-Null
}
trap {
    if ($setupLog) { try { Add-Content -LiteralPath $setupLog -Value ($_.Exception.ToString() + [Environment]::NewLine + $_.ScriptStackTrace + [Environment]::NewLine + $_.InvocationInfo.PositionMessage) } catch { } }
    throw $_
}
function Canonical([string]$value) { return ([Guid]::Parse($value)).ToString('B').ToUpperInvariant() }
function Policy([switch]$Enable) {
    if ($PlanOnly) { return }
    $arguments = @{}
    if ($TestRoot) { $arguments.TestRoot=$TestRoot; $arguments.TestBackupDirectory=$TestBackupDirectory }
    if ($Enable) { $arguments.Enable=$true } else { $arguments.RestoreIfUnused=$true }
    & (Join-Path $PSScriptRoot 'ExperimentalAudioPolicy.ps1') @arguments
}
function Snapshot($key) {
    $result = @()
    foreach ($name in $names) {
        $exists = $key -and ($key.GetValueNames() -contains $name)
        $result += [pscustomobject]@{ Name=$name; Exists=$exists; Kind=$(if ($exists) { $key.GetValueKind($name).ToString() } else { 'String' }); Value=$(if ($exists) { $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null }) }
    }
    return $result
}
function RestoreValues($key, $values) {
    foreach ($value in $values) {
        if (!$value.Exists) { $key.DeleteValue($value.Name, $false); continue }
        $kind = [Microsoft.Win32.RegistryValueKind]([Enum]::Parse([Microsoft.Win32.RegistryValueKind], $value.Kind))
        $data = $value.Value
        if ($kind -eq [Microsoft.Win32.RegistryValueKind]::MultiString) { $data = [string[]]@($data | Where-Object { $null -ne $_ }) }
        if ($kind -eq [Microsoft.Win32.RegistryValueKind]::DWord) { $data = [int]$data }
        if ($kind -eq [Microsoft.Win32.RegistryValueKind]::QWord) { $data = [long]$data }
        if ($kind -eq [Microsoft.Win32.RegistryValueKind]::Binary) { $data = [byte[]]$data }
        $key.SetValue($value.Name, $data, $kind)
    }
    $key.Flush()
}
function RestoreEndpoint([string]$id) {
    $id = Canonical $id; $file = Join-Path $backupDirectory ($id + '.json')
    if (!(Test-Path -LiteralPath $file)) { throw "No Soundshark backup exists for $id. No endpoint changes were made." }
    $saved = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ((Canonical $saved.Endpoint) -ne $id) { throw 'Endpoint backup identity mismatch.' }
    $key = $base.OpenSubKey("$root\$id\FxProperties", !$PlanOnly)
    if (!$key) { return }
    try {
        if ($key.GetValue($slot) -ne $clsid) {
            # A previous restore may have completed before a subsequent installer failure.
            $original = $saved.Values | Where-Object Name -eq $slot
            if (($original.Exists -and $key.GetValue($slot) -eq $original.Value) -or (!$original.Exists -and !($key.GetValueNames() -contains $slot))) { return }
            throw 'Another application changed the endpoint effect. Restore was stopped to preserve its changes.'
        }
        if (!$PlanOnly) { RestoreValues $key $saved.Values }
    } finally { $key.Dispose() }
    if (!$PlanOnly -and $saved.CreatedEffectsStore) {
        $parent = $base.OpenSubKey("$root\$id", $true)
        try {
            $remaining = $parent.OpenSubKey('FxProperties')
            $empty = $remaining -and $remaining.ValueCount -eq 0 -and $remaining.SubKeyCount -eq 0
            if ($remaining) { $remaining.Dispose() }
            if ($empty) { $parent.DeleteSubKey('FxProperties', $false) }
        } finally { if ($parent) { $parent.Dispose() } }
    }
}
if ($RestoreAll) {
    $endpoints = $base.OpenSubKey($root)
    if ($endpoints) { try { foreach ($id in $endpoints.GetSubKeyNames()) { $fx = $endpoints.OpenSubKey("$id\FxProperties"); if ($fx) { try { if ($fx.GetValue($slot) -eq $clsid -and !(Test-Path -LiteralPath (Join-Path $backupDirectory ((Canonical $id) + '.json')))) { throw 'A configured endpoint has no rollback backup. Uninstall stopped.' } } finally { $fx.Dispose() } } } } finally { $endpoints.Dispose() } }
    if (Test-Path -LiteralPath $backupDirectory) { foreach ($file in Get-ChildItem -LiteralPath $backupDirectory -Filter '*.json' -File) { RestoreEndpoint ([IO.Path]::GetFileNameWithoutExtension($file.Name)) } }
    Policy; exit 0
}
$EndpointGuid = Canonical $EndpointGuid
if ($Restore) { RestoreEndpoint $EndpointGuid; Policy; exit 0 }
if (!$UserSid -or !([System.Security.Principal.SecurityIdentifier]::new($UserSid))) { throw 'A valid Windows user SID is required.' }
$key = $base.OpenSubKey("$root\$EndpointGuid\FxProperties", !$PlanOnly)
$createdEffectsStore = !$key
$endpointKey = $base.OpenSubKey("$root\$EndpointGuid")
if (!$endpointKey) { throw 'The selected microphone is no longer installed.' }
$endpointKey.Dispose()
try {
    $old = if ($key) { $key.GetValue($slot) } else { $null }
    if ($old -eq $clsid) {
        if ($key.GetValue($sidProperty) -ne $UserSid) { throw 'This microphone is configured for a different Windows user. Remove that setup first.' }
        if (!(Test-Path -LiteralPath (Join-Path $backupDirectory ($EndpointGuid + '.json')))) { throw 'Original endpoint backup is missing. Repair stopped to preserve the microphone configuration.' }
        if ($Experimental) { Policy -Enable }; exit 0
    }
    foreach ($property in @('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},15', '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},29')) {
        if ($key -and $key.GetValue($property)) { throw 'This microphone uses a composite effect pack. This build cannot safely attach to it.' }
    }
    if ($old) {
        [void][Guid]::Parse($old)
        $supported = @($key.GetValue($modes))
        if (!($supported -contains $requiredModes[0]) -and !($supported -contains $requiredModes[1])) { throw 'The existing microphone effect has no supported default or communications mode. Setup stopped to preserve its behavior.' }
        foreach ($mode in $supported) { [void][Guid]::Parse($mode) }
    } else {
        foreach ($property in @('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},1', '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},2')) {
            if ($key -and $key.GetValue($property)) { throw 'This microphone uses legacy effects. This build cannot safely replace its graph.' }
        }
    }
    $snapshot = Snapshot $key
    if ($PlanOnly) { [pscustomobject]@{ Endpoint=$EndpointGuid; OriginalEffect=$old; UserSid=$UserSid; Properties=$snapshot; SigningRequired=(!$Experimental); Experimental=$Experimental.IsPresent; RestartRequired=$true } | ConvertTo-Json -Depth 8; exit 0 }
    if (!$TestRoot) {
        $dll = Join-Path $PSScriptRoot 'SoundsharkMicrophone.dll'
        if (!$Experimental -and (Get-AuthenticodeSignature -LiteralPath $dll).Status -ne 'Valid') { throw 'The microphone DLL needs a trusted signature or explicitly approved experimental setup. Endpoint configuration was not changed.' }
    }
    New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
    $backup = Join-Path $backupDirectory ($EndpointGuid + '.json')
    $alreadyBackedUp = Test-Path -LiteralPath $backup
    if ($alreadyBackedUp) {
        $saved = Get-Content -LiteralPath $backup -Raw | ConvertFrom-Json
        if ((Canonical $saved.Endpoint) -ne $EndpointGuid -or (($saved.Values | ConvertTo-Json -Depth 8 -Compress) -ne ($snapshot | ConvertTo-Json -Depth 8 -Compress))) { throw 'An earlier endpoint backup differs from the current configuration. Setup stopped to preserve both.' }
    }
    $json = [pscustomobject]@{ Version=2; Endpoint=$EndpointGuid; CreatedEffectsStore=$createdEffectsStore; Values=$snapshot } | ConvertTo-Json -Depth 8
    if (!$alreadyBackedUp) { $temporary = $backup + '.new'; [IO.File]::WriteAllText($temporary, $json); [IO.File]::Move($temporary, $backup) }
    try {
        if ($Experimental) { Policy -Enable }
        if (!$key) { $key = $base.CreateSubKey("$root\$EndpointGuid\FxProperties") }
        if (!($key.GetValueNames() -contains $association)) { $key.SetValue($association, '{00000000-0000-0000-0000-000000000000}', [Microsoft.Win32.RegistryValueKind]::String) }
        $key.SetValue($previous, [string]$old, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue($sidProperty, $UserSid, [Microsoft.Win32.RegistryValueKind]::String)
        $key.SetValue($modes, [string[]]$(if ($old) { @($key.GetValue($modes)) } else { $requiredModes }), [Microsoft.Win32.RegistryValueKind]::MultiString)
        $key.SetValue($slot, $clsid, [Microsoft.Win32.RegistryValueKind]::String); $key.Flush()
    } catch { $failure = $_; try { if ($key) { RestoreValues $key $snapshot } } finally { if ($Experimental) { Policy } }; throw $failure }
} finally { if ($key) { $key.Dispose() } }
