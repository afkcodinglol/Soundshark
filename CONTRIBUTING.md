# Contributing to Soundshark

Bug reports, feature ideas, documentation improvements, and code patches are welcome.

## Report a problem

[Open an issue](https://github.com/afkcodinglol/Soundshark/issues) with your Soundshark version, Windows version, steps to reproduce, and what you expected to happen. For an audio-device problem, include the device and selected playback or recording route.

Use **Open Logs** in the app footer to find a diagnostic reference. Review logs before sharing and remove personal paths, private links, or other sensitive information. Do not attach recordings containing other people's private audio.

## Suggest an improvement

Describe the task you want to accomplish and how the change would help. A short example or annotated screenshot is useful. Check existing issues first so related discussion stays together.

## Work on the code

Application source is currently published in the `Soundshark-source.zip` asset on the [Releases page](https://github.com/afkcodinglol/Soundshark/releases/latest). The repository itself hosts project documentation and downloads.

1. Extract the source archive and install the .NET SDK specified in `global.json` on Windows x64.
2. Make a focused change and preserve original audio files and user settings.
3. Build and run the relevant checks described in the [README](README.md#build-it-yourself).
4. Open an issue describing the change, attach a patch or link to your work, and state what you tested. Distinguish automated checks from real-device or voice-app testing.

Documentation changes in this repository can be submitted as pull requests. For a larger code change, start an issue so its approach and source version are clear before preparing a patch.

Soundshark's own code is covered by the [MIT license](LICENSE). Keep third-party notices and licenses intact. Optional native microphone support has additional setup and validation requirements; read the [microphone guide](docs/MICROPHONE.md) before changing it.
