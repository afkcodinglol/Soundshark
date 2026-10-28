# Soundshark updates

Recipients need no GitHub login. The public release source is configured in update-source.json. Soundshark checks once at launch, downloads the latest stable portable package, verifies GitHub's SHA-256 asset digest, stages the app, and applies it after Soundshark closes. Update Soundshark can check immediately and install/restart. Unsaved audio still requires the existing save/discard decision. Recording is never interrupted to install an update. Update Log works offline and lists the documented versions from 1.0.0 onward.

The same updater supports installed and portable copies. It fully replaces the program files in the current app directory, removing obsolete files instead of mixing old and new code. Library, settings, recovery data and native system microphone registration are not updated or removed. All existing program files are backed up first; failed replacements restore the previous program, including obsolete files. Installed uninstaller records are preserved. A failed helper writes apply.log in LocalAppData/Soundshark/Updates. A downloaded app update does not automatically change the system-wide microphone component or policy.

## One-time publisher setup

1. Keep https://github.com/afkcodinglol/Soundshark public, with an initial commit/default branch.
2. Install GitHub CLI from https://cli.github.com/ (or `winget install --id GitHub.cli --exact`). Run `gh auth login` and sign in as an account that can create releases in the repository. Credentials stay on the publisher PC; the app never ships them.
3. Distribute version 1.8.0 or newer once. Older copies have no app updater and need that initial installer/portable download manually.

## Publish future updates

1. Make and test the app changes. Update the version in src/Soundshark/Soundshark.csproj and packaging/Soundshark.iss, and add its user-facing notes at the top of assets/UpdateHistory.json.
2. Run `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish-Release.ps1`. This runs regression checks, packages the app using the existing tested native component, uploads a draft release and verifies all uploaded asset digests. Check the draft and notes on GitHub, then publish it. To publish immediately after successful verification, add `-Publish`.
3. Once published, recipients' startup checks find the release. The publisher script refuses to overwrite an existing release. Drafts and prereleases are never installed automatically.

If a native microphone update is needed, build/test its installer separately instead of reusing the old native package. A GitHub upload and a local package build are separate steps; a local build alone does not deliver updates to anyone.

## Release history

assets/UpdateHistory.json is the single user-facing history used by Update Log and the publishing script. Entries describe historical behavior, including later replacements and the 1.7.4/1.7.5 volume investigation. No release dates were invented for older builds.

## Roll Back Soundshark

The footer rollback button previews the most recent older local backup for this app directory. Restore And Restart verifies its recorded hashes and assembly version before using the normal unsaved-audio shutdown flow. Full program replacement retains current Library audio, settings and microphone registration. Backup manifests are created by the 1.8.3 helper and newer; an older update backup without that manifest is not offered. Installed and portable copies have separate backups, and moving the app to another directory does not transfer rollback history. Automatic checks pause after a rollback, including old updater versions. Use Update Soundshark to explicitly update again; returning to the skipped version or newer resumes checks.

## Available update hints

Update Soundshark and Update Import Tools glow green when startup checks find an available update. The soft breathing effect becomes a steady glow with Performance Mode. No recurring checks run while the app stays open. Explicit update button actions still check versions before installing. Import tool startup checks download only release metadata, and current or newer executables are never reinstalled just for matching the latest version.
