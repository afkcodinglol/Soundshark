param([switch]$Enable, [switch]$RestoreIfUnused, [string]$TestRoot, [string]$TestBackupDirectory)
$ErrorActionPreference = 'Stop'
$base = [Microsoft.Win32.Registry]::LocalMachine
$capture = 'SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture'
$policy = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Audio'
$backupRoot = Join-Path $PSScriptRoot 'PolicyBackup'
if ($TestRoot) {
    if ($TestRoot -notmatch '^Software\\SoundsharkNativeChecks\\[0-9a-fA-F-]{36}$' -or !$TestBackupDirectory) { throw 'Invalid isolated policy fixture.' }
    $base = [Microsoft.Win32.Registry]::CurrentUser; $capture = $TestRoot; $policy = "$TestRoot\Policy"; $backupRoot = Join-Path $TestBackupDirectory 'PolicyBackup'
} else {
    $protectedRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'SoundsharkAudio'
    if (![string]::Equals([IO.Path]::GetFullPath($PSScriptRoot), [IO.Path]::GetFullPath($protectedRoot), [StringComparison]::OrdinalIgnoreCase)) { throw 'Run policy changes only from protected microphone setup.' }
}
$file = Join-Path $backupRoot 'AudioPolicy.json'
$name = 'DisableProtectedAudioDG'
function SavePolicy($value) {
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    $temporary = $file + '.new'; $value | ConvertTo-Json | Set-Content -LiteralPath $temporary
    if (Test-Path -LiteralPath $file) { [IO.File]::Replace($temporary, $file, [NullString]::Value) } else { [IO.File]::Move($temporary, $file) }
}
if ($RestoreIfUnused) {
    $devices = $base.OpenSubKey($capture)
    if ($devices) { try { foreach ($id in $devices.GetSubKeyNames()) {
        $fx = $devices.OpenSubKey("$id\FxProperties")
        if ($fx) { try { if ($fx.GetValue('{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},7') -eq '{A08799CD-8535-492D-A1B4-F4C8B51E8779}') { return } } finally { $fx.Dispose() } }
    } } finally { $devices.Dispose() } }
    if (!(Test-Path -LiteralPath $file)) { return }
    $saved = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ($saved.Restored) { return }
    $key = $base.OpenSubKey($policy, $true)
    if (!$key) { return }
    try {
        if ($key.GetValue($name) -ne 1 -or $key.GetValueKind($name) -ne [Microsoft.Win32.RegistryValueKind]::DWord) {
            $exists = $key.GetValueNames() -contains $name
            if ((!$saved.Exists -and !$exists) -or ($saved.Exists -and $exists -and $key.GetValueKind($name).ToString() -eq $saved.Kind -and $key.GetValue($name) -eq $saved.Value)) { $saved.Restored=$true; SavePolicy $saved; return }
            Write-Warning 'Audio policy changed after setup; its current value was preserved.'; return
        }
        if ($saved.Exists) { $key.SetValue($name, $saved.Value, [Microsoft.Win32.RegistryValueKind]([Enum]::Parse([Microsoft.Win32.RegistryValueKind], $saved.Kind))) }
        else { $key.DeleteValue($name, $false) }
        $key.Flush(); $saved.Restored = $true
        SavePolicy $saved
    } finally { $key.Dispose() }
    return
}
if (!$Enable) { throw 'Choose Enable or RestoreIfUnused.' }
$key = $base.CreateSubKey($policy)
try {
    $exists = $key.GetValueNames() -contains $name
    $snapshot = [pscustomobject]@{ Exists=$exists; Kind=$(if ($exists) { $key.GetValueKind($name).ToString() } else { 'DWord' }); Value=$(if ($exists) { $key.GetValue($name) } else { $null }); Restored=$false }
    if (Test-Path -LiteralPath $file) {
        $saved = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        if (!$saved.Restored) {
            if ($exists -and $key.GetValueKind($name) -eq [Microsoft.Win32.RegistryValueKind]::DWord -and $key.GetValue($name) -eq 1) { return }
            throw 'Audio policy changed after setup. Restore or review the protected backup first.'
        }
        if ($saved.Exists -ne $snapshot.Exists -or ($saved.Exists -and ($saved.Kind -ne $snapshot.Kind -or $saved.Value -ne $snapshot.Value))) { throw 'The current audio policy differs from its original backup.' }
    }
    SavePolicy $snapshot
    $key.SetValue($name, 1, [Microsoft.Win32.RegistryValueKind]::DWord); $key.Flush()
} finally { $key.Dispose() }
