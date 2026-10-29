param([string]$Repository = 'afkcodinglol/Soundshark', [string]$Message = 'Publish Soundshark source')
$ErrorActionPreference = 'Stop'
if ($Repository -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$' -or $Repository.Contains('..')) { throw 'Use owner/repository.' }
$root = Split-Path $PSScriptRoot -Parent
$ghCommand = Get-Command gh -ErrorAction SilentlyContinue
$gh = if ($ghCommand) { $ghCommand.Source } else { Join-Path $root 'outputs/publisher/gh/bin/gh.exe' }
$requestPath = Join-Path $root ('outputs/source-request-' + [Guid]::NewGuid().ToString('N') + '.json')
$endpoint = "repos/$Repository"
function Request([string]$Path, [string]$Method='GET', $Payload=$null) {
    if ($null -ne $Payload) {
        [IO.File]::WriteAllText($requestPath,($Payload | ConvertTo-Json -Depth 30 -Compress),[Text.UTF8Encoding]::new($false))
        $result = & $gh api --method $Method "$endpoint/$Path" --input $requestPath
    } else { $result = & $gh api "$endpoint/$Path" }
    if ($LASTEXITCODE -ne 0) { throw "GitHub API failed: $Method $Path" }
    return (($result | Out-String) | ConvertFrom-Json)
}
function BlobHash([byte[]]$Bytes) {
    $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA1)
    try {
        $hash.AppendData([Text.Encoding]::ASCII.GetBytes("blob $($Bytes.Length)" + [char]0))
        $hash.AppendData($Bytes)
        return ([BitConverter]::ToString($hash.GetHashAndReset())).Replace('-','').ToLowerInvariant()
    } finally { $hash.Dispose() }
}
$repo = ((& $gh api $endpoint | Out-String) | ConvertFrom-Json)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read repository.' }
$branch = $repo.default_branch
$head = Request "git/ref/heads/$branch"
$parent = Request "git/commits/$($head.object.sha)"
$tree = Request "git/trees/$($parent.tree.sha)?recursive=1"
if ($tree.truncated) { throw 'Remote tree is truncated; refusing an incomplete comparison.' }
$remote = @{}; foreach ($entry in $tree.tree) { if ($entry.type -eq 'blob') { $remote[$entry.path]=$entry.sha } }
$files = @()
foreach ($folder in @('src','tests','assets','scripts','packaging','docs')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $root $folder) -Recurse -File | Where-Object FullName -NotMatch '\\(bin|obj|node_modules)\\'
}
$files += Get-Item -LiteralPath @((Join-Path $root 'global.json'),(Join-Path $root '.gitignore'),(Join-Path $root 'README.md'),(Join-Path $root 'CONTRIBUTING.md'),(Join-Path $root 'LICENSE'),(Join-Path $root 'update-source.json'),(Join-Path $root 'hub-source.json'))
$entries = @(); $expected = @{}; $utf8 = [Text.UTF8Encoding]::new($false,$true)
foreach ($file in $files) {
    $path = $file.FullName.Substring($root.Length+1).Replace('\','/')
    if ($file.Name -match '^\.env|\.(pem|key|pfx|p12|log)$|^(credentials|secrets)\.') { throw "Unexpected private file: $path" }
    $bytes = [IO.File]::ReadAllBytes($file.FullName); $sha = BlobHash $bytes; $expected[$path]=$sha
    if ($remote[$path] -eq $sha) { continue }
    $text = $null
    if ($bytes.Length -lt 200000 -and [Array]::IndexOf($bytes,[byte]0) -lt 0) { try { $text=$utf8.GetString($bytes) } catch [Text.DecoderFallbackException] { } }
    if ($null -ne $text) { $entries += @{path=$path;mode='100644';type='blob';content=$text} }
    else {
        $blob = Request 'git/blobs' 'POST' @{content=[Convert]::ToBase64String($bytes);encoding='base64'}
        if ($blob.sha -ne $sha) { throw "Uploaded blob mismatch: $path" }
        $entries += @{path=$path;mode='100644';type='blob';sha=$blob.sha}
    }
}
if (!$entries.Count) { Write-Output 'Source already matches GitHub.'; return }
Write-Output "Preparing $($entries.Count) changed source files."
$newTree = Request 'git/trees' 'POST' @{base_tree=$parent.tree.sha;tree=$entries}
$verified = Request "git/trees/$($newTree.sha)?recursive=1"
if ($verified.truncated) { throw 'Cannot verify truncated source tree.' }
$actual=@{}; foreach ($entry in $verified.tree) { if($entry.type -eq 'blob') { $actual[$entry.path]=$entry.sha } }
foreach($path in $expected.Keys) { if($actual[$path] -ne $expected[$path]) { throw "Source verification failed: $path" } }
$commit = Request 'git/commits' 'POST' @{message=$Message;tree=$newTree.sha;parents=@($head.object.sha)}
$current = Request "git/ref/heads/$branch"
if($current.object.sha -ne $head.object.sha) { throw 'Branch changed during preparation; no branch update made.' }
$null = Request "git/refs/heads/$branch" 'PATCH' @{sha=$commit.sha;force=$false}
$current = Request "git/ref/heads/$branch"
if($current.object.sha -ne $commit.sha) { throw 'Branch verification failed.' }
$receipt=@{Repository=$Repository;Branch=$branch;Commit=$commit.sha;PreviousCommit=$head.object.sha;VerifiedFiles=$expected.Count;ChangedFiles=$entries.Count;UTC=[DateTime]::UtcNow.ToString('o')}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'outputs/github-source-published.json')
Write-Output "Published and verified $($expected.Count) source files: https://github.com/$Repository/commit/$($commit.sha)"
