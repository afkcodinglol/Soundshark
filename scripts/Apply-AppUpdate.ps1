param([Parameter(Mandatory=$true)][string]$PlanPath)
$ErrorActionPreference = 'Stop'
$originals = @()
$entries = @()
$replacementStarted = $false
$backup = Join-Path (Split-Path -Parent $PlanPath) 'Backup'
$log = Join-Path (Split-Path -Parent $PlanPath) 'apply.log'
function Get-Sha([string]$path) {
    $stream=[IO.File]::OpenRead($path); $algorithm=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-','') }
    finally { $stream.Dispose(); $algorithm.Dispose() }
}
function Protected-Path([string]$relative) {
    $first=$relative.Replace('\','/').Split('/')[0]
    return $first -in @('Library','Workspaces','Logs','ToolSets','Updates','settings.json','app-update-hold.json','.git') -or $first -match '^unins[0-9]+\.(exe|dat|msg)$'
}
function Program-Files([string]$root) {
    $stack=New-Object 'System.Collections.Generic.Stack[System.IO.DirectoryInfo]'
    $stack.Push((New-Object IO.DirectoryInfo($root)))
    while ($stack.Count) {
        foreach ($item in $stack.Pop().EnumerateFileSystemInfos()) {
            $absolute=[IO.Path]::GetFullPath($item.FullName)
            if (!$absolute.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Existing app path escaped its directory.' }
            $relative=$absolute.Substring($root.Length+1)
            if (Protected-Path $relative) { continue }
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Program files contain a redirected filesystem path. Update cancelled.' }
            if ($item -is [IO.DirectoryInfo]) { $stack.Push($item) } else { $item }
        }
    }
}
try {
    $plan=Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json
    $target=[IO.Path]::GetFullPath($plan.Target).TrimEnd('\')
    $stage=[IO.Path]::GetFullPath($plan.Stage).TrimEnd('\')
    $hold=Join-Path $target 'app-update-hold.json'
    $holdPrevious=if([IO.File]::Exists($hold)){[IO.File]::ReadAllText($hold)}else{$null}
    if ($target.Length -le 3 -or !(Test-Path -LiteralPath (Join-Path $target 'Soundshark.exe') -PathType Leaf)) { throw 'Invalid app update destination.' }
    $parent=Get-Process -Id $plan.ParentPid -ErrorAction SilentlyContinue
    if ($parent) {
        if ([Math]::Abs(($parent.StartTime.ToUniversalTime()-[datetime]::Parse($plan.ParentStartedUtc).ToUniversalTime()).TotalSeconds) -gt 1) { throw 'The original application process changed.' }
        if (!$parent.WaitForExit(300000)) { throw 'Soundshark did not close. Update was not applied.' }
    }
    $seen=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $plan.Files) {
        $relative=[string]$file.Relative
        $parts=$relative.Replace('\','/').Split('/')
        if ([IO.Path]::IsPathRooted($relative) -or !$seen.Add($relative) -or @($parts | Where-Object { !$_ -or $_ -eq '.' -or $_ -eq '..' -or $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count) { throw 'Unsafe update path.' }
        if (Protected-Path $relative) { throw 'An update attempted to replace user data or installer records.' }
        $destination=[IO.Path]::GetFullPath((Join-Path $target $relative))
        $source=[IO.Path]::GetFullPath((Join-Path $stage $relative))
        if (!$destination.StartsWith($target+'\',[StringComparison]::OrdinalIgnoreCase) -or !$source.StartsWith($stage+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Update path escaped its directory.' }
        if ((Get-Item -LiteralPath $source).Length -ne $file.Length -or (Get-Sha $source) -ne $file.Sha256) { throw 'Staged update file changed.' }
        $entries += [pscustomobject]@{Source=$source;Destination=$destination;Relative=$relative}
    }
    if (!$seen.Contains('Soundshark.exe') -or !$seen.Contains('Soundshark.dll') -or !$seen.Contains('Soundshark.Core.dll')) { throw 'Incomplete app update.' }
    # Enumerate only program files. Protected folders are skipped without walking them.
    foreach ($file in @(Program-Files $target)) {
        $relative=$file.FullName.Substring($target.Length+1)
        $saved=Join-Path $backup $relative
        New-Item -ItemType Directory -Path (Split-Path $saved) -Force | Out-Null
        [IO.File]::Copy($file.FullName,$saved,$true)
        $originals += [pscustomobject]@{Destination=$file.FullName;Backup=$saved;Relative=$relative;Hash=(Get-Sha $saved)}
    }
    $oldVersion='unknown'
    try { $oldVersion=[Reflection.AssemblyName]::GetAssemblyName((Join-Path $backup 'Soundshark.dll')).Version.ToString() } catch { }
    $manifest=@{Target=$target;Version=$oldVersion;Files=@($originals | ForEach-Object { @{Relative=$_.Relative;Sha256=$_.Hash;Length=(Get-Item -LiteralPath $_.Backup).Length} })}
    [IO.File]::WriteAllText((Join-Path (Split-Path -Parent $PlanPath) 'backup.json'),($manifest | ConvertTo-Json -Depth 8))
    # Check locks before removing anything. All program files, including obsolete DLLs,
    # have a backup; Library/settings and the installed uninstaller are left in place.
    foreach ($old in $originals) { $handle=[IO.File]::Open($old.Destination,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $handle.Dispose() }
    $replacementStarted=$true
    foreach ($old in $originals) {
        if (!$old.Destination.StartsWith($target+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe removal target.' }
        Remove-Item -LiteralPath $old.Destination -Force
    }
    foreach ($entry in $entries) {
        New-Item -ItemType Directory -Path (Split-Path $entry.Destination) -Force | Out-Null
        [IO.File]::Copy($entry.Source,$entry.Destination,$true)
    }
    if ($plan.SuppressVersion) { [IO.File]::WriteAllText($hold,[string]$plan.SuppressVersion) }
    elseif ([IO.File]::Exists($hold)) { Remove-Item -LiteralPath $hold -Force }
    # Older updater builds already honor this check timestamp. Keep them paused
    # after rollback too; their manual Check Now still works. New builds clear it
    # once the user explicitly returns to the skipped version or a newer one.
    if ($plan.CheckStamp) {
        if ($plan.SuppressVersion) {
            [IO.File]::WriteAllText([string]$plan.CheckStamp,'Automatic updates paused after rollback. Check manually to resume.')
            [IO.File]::SetLastWriteTimeUtc([string]$plan.CheckStamp,[datetime]::UtcNow.AddYears(100))
        } elseif ([IO.File]::Exists([string]$plan.CheckStamp)) { Remove-Item -LiteralPath ([string]$plan.CheckStamp) -Force }
    }
    Set-Content -LiteralPath $log -Value 'Update applied successfully. Program files fully replaced; obsolete files removed. Previous program files are retained in Backup. User data preserved.'
    if ($plan.Restart) { Start-Process -FilePath (Join-Path $target 'Soundshark.exe') -WorkingDirectory $target -WindowStyle Normal }
    exit 0
} catch {
    $failure=$_.Exception.ToString()
    if ($replacementStarted) {
        try { if ($null -ne $holdPrevious) { [IO.File]::WriteAllText($hold,$holdPrevious) } elseif ([IO.File]::Exists($hold)) { Remove-Item -LiteralPath $hold -Force } } catch { $failure += "`nRestore update preference: " + $_.Exception.ToString() }
        $oldPaths=New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($old in $originals) { [void]$oldPaths.Add($old.Destination) }
        foreach ($entry in $entries) {
            try { if (!$oldPaths.Contains($entry.Destination) -and [IO.File]::Exists($entry.Destination)) { Remove-Item -LiteralPath $entry.Destination -Force } }
            catch { $failure += "`nRollback: " + $_.Exception.ToString() }
        }
        foreach ($old in $originals) {
            try {
                if ([IO.File]::Exists($old.Destination) -and (Get-Sha $old.Destination) -eq $old.Hash) { continue }
                New-Item -ItemType Directory -Path (Split-Path $old.Destination) -Force | Out-Null
                [IO.File]::Copy($old.Backup,$old.Destination,$true)
            } catch { $failure += "`nRollback: " + $_.Exception.ToString() }
        }
    }
    Set-Content -LiteralPath $log -Value $failure
    exit 1
}
