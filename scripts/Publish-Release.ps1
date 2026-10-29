param([string]$Repository = 'afkcodinglol/Soundshark', [switch]$Publish)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if ($Repository -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]*/[A-Za-z0-9][A-Za-z0-9_.-]*$' -or $Repository.Contains('..')) { throw 'Use owner/repository for the release source.' }
$ghCommand = Get-Command gh -ErrorAction SilentlyContinue
$gh = if ($ghCommand) { $ghCommand.Source } else { Join-Path $root 'outputs/publisher/gh/bin/gh.exe' }
if (!(Test-Path -LiteralPath $gh)) { throw 'Install GitHub CLI, then run gh auth login once. See docs/UPDATES.md.' }
Push-Location $root
try {
    $repoText = & $gh api "repos/$Repository"
    if ($LASTEXITCODE -ne 0) { throw 'Sign in using gh auth login and verify access to the release repository.' }
    $repo = ($repoText | Out-String) | ConvertFrom-Json
    if ($repo.private) { throw 'The releases repository must be public so recipients can update without signing in.' }
    [xml]$project = Get-Content src/Soundshark/Soundshark.csproj
    $version = @($project.Project.PropertyGroup.Version | Where-Object { $_ })[0]
    if (!$version -or !(Get-Content packaging/Soundshark.iss -Raw).Contains('AppVersion "'+$version+'"')) { throw 'App and installer versions must match.' }
    $history = Get-Content assets/UpdateHistory.json -Raw | ConvertFrom-Json
    $entry = @($history | Where-Object Version -eq $version)
    if ($entry.Count -ne 1) { throw 'Add this version to assets/UpdateHistory.json before publishing.' }
    [IO.File]::WriteAllText((Join-Path $root 'update-source.json'), (@{Repository=$Repository} | ConvertTo-Json -Compress))
    dotnet run --no-restore --project tests/Soundshark.Checks -c Release -- outputs/qa
    if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed; no release was published.' }
    dotnet run --no-restore --project tests/Soundshark.Checks -c Release -- outputs/qa/release-hub --hub
    if ($LASTEXITCODE -ne 0) { throw 'Hub account checks failed; no release was published.' }
    npm --prefix src/Soundshark.Hub ci --ignore-scripts
    if ($LASTEXITCODE -ne 0) { throw 'Hub test dependency setup failed; no release was published.' }
    npm --prefix src/Soundshark.Hub test
    if ($LASTEXITCODE -ne 0) { throw 'Hub backend checks failed; no release was published.' }
    & (Join-Path $PSScriptRoot 'Package.ps1') -ReuseNative
    if (!$?) { throw 'Packaging failed; no release was published.' }
    $notesPath = Join-Path $root 'outputs/release-notes.md'
    [IO.File]::WriteAllText($notesPath, ($entry[0].Title + "`n`n" + (($entry[0].Changes | ForEach-Object { '- ' + $_ }) -join "`n")))
    & $gh api "repos/$Repository/git/ref/heads/$($repo.default_branch)" --silent 2>$null
    if ($LASTEXITCODE -ne 0) {
        $readme = "# Soundshark`n`nWindows audio studio and soundboard.`n`nDownload the latest installer or portable ZIP from this repository's Releases page. Version 1.8.0 and newer can check for and apply later releases automatically.`n`nThe app keeps Library sounds and settings during updates. Experimental physical-microphone support is optional; see the documentation included with the app.`n"
        $request = Join-Path $root 'outputs/repository-readme-request.json'
        New-Item -ItemType Directory -Path (Split-Path $request) -Force | Out-Null
        [IO.File]::WriteAllText($request, (@{message='Set up Soundshark releases';content=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($readme))} | ConvertTo-Json -Compress))
        & $gh api --method PUT "repos/$Repository/contents/README.md" --input $request
        if ($LASTEXITCODE -ne 0) { throw 'The empty release repository needs an initial README commit before releases can be created.' }
    }
    $releaseAssets = @('outputs/Soundshark-Setup-x64.exe','outputs/Soundshark-portable-x64.zip','outputs/Soundshark-source.zip','outputs/SHA256SUMS.txt')
    & $gh release create "v$version" --repo $Repository --title "Soundshark $version" --notes-file $notesPath --draft --target $repo.default_branch @releaseAssets
    if ($LASTEXITCODE -ne 0) { throw 'Draft release creation failed. Existing releases are never overwritten.' }
    $releases = ((& $gh api "repos/$Repository/releases?per_page=100") | Out-String) | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0) { throw 'Could not verify the uploaded draft.' }
    $matching = @($releases | Where-Object { $_.tag_name -eq "v$version" -and $_.draft })
    if ($matching.Count -ne 1) { throw 'Could not uniquely identify the uploaded draft.' }
    $release = $matching[0]
    foreach ($file in $releaseAssets) {
        $asset = @($release.assets | Where-Object name -eq (Split-Path $file -Leaf))
        if ($asset.Count -ne 1 -or $asset[0].digest -ne ('sha256:' + (Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant())) { throw 'Uploaded digest did not match. The release remains a draft.' }
    }
    if ($Publish) {
        $publishRequest = Join-Path $root 'outputs/release-publish.json'
        [IO.File]::WriteAllText($publishRequest, (@{draft=$false;make_latest='true'} | ConvertTo-Json -Compress))
        & $gh api --method PATCH "repos/$Repository/releases/$($release.id)" --input $publishRequest --silent
        if ($LASTEXITCODE -ne 0) { throw 'Publishing failed; inspect the draft in GitHub.' }
        Write-Output "Published https://github.com/$Repository/releases/tag/v$version"
    } else { Write-Output 'Draft uploaded and verified. Publish it on GitHub to send this update to recipients.' }
} finally { Pop-Location }
