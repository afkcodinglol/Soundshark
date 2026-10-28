# Dependency inventory

Build: .NET SDK 10.0.401, target net10.0-windows, win-x64. Publish is self-contained; no SDK or installed .NET is needed by users. Exact NuGet resolution is locked in packages.lock.json.

| Component | Pinned version | Provenance |
|---|---|---|
| NAudio | 2.2.1 | NuGet / https://github.com/naudio/NAudio |
| FFmpeg / ffprobe | 8.1.2-full_build-www.gyan.dev | https://www.gyan.dev/ffmpeg/builds/ |
| yt-dlp | 2026.08.19 | https://github.com/yt-dlp/yt-dlp/releases/tag/2026.08.19 |
| Deno | v2.9.7 | https://github.com/denoland/deno/releases/tag/v2.9.7 |

`Tools/manifest.json` lists SHA-256 for each executable. The fetch script verifies yt-dlp against the release SHA2-256SUMS and Deno against its GitHub release asset digest. FFmpeg comes from the local WinGet installation; its executables are also compared with the pinned upstream archive during delivery. Source archives live in `outputs/dependency-sources`.

Verified FFmpeg archive SHA-256: `0fff188997a499b5382e0f66e845d4556c48c54f0113ebed4853d556dbdd7059`. Extracted upstream ffmpeg.exe and ffprobe.exe hashes matched both bundled executables. Self-contained runtime: Microsoft.NETCore.App 10.0.12 and Microsoft.WindowsDesktop.App 10.0.12. Runtime/WPF MIT and third-party notices, NAudio MIT, and Inno Setup license are included under Tools/licenses.

The update button fetches the latest stable yt-dlp and Deno from the official GitHub release APIs, requires SHA-256 digests, stages and smoke-tests both tools, records the resolved versions, then switches one pointer under `%LOCALAPPDATA%/Soundshark`. Cancel/failure leaves active tools intact. Rollback swaps the pointer to the prior set. No background automatic updates. FFmpeg stays pinned; updating it requires a new app package.

yt-dlp's official Windows executable includes EJS. Deno is supplied explicitly with `--js-runtimes`; user yt-dlp configuration and cookies are ignored. URL imports require a network connection. Local recording/editing/export is offline.

Third-party redistribution audit: the FFmpeg source archive covers FFmpeg itself; the full build also statically links many libraries listed in its build README. A complete corresponding-source archive for every external library and its exact build scripts is not yet assembled. Treat this delivery as a local test build; complete that audit before public redistribution. This limitation does not prevent local audio operation.
