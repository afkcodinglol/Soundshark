# Current experimental route

Version 1.5.1 supports explicitly approved unsigned microphone setup by disabling protected AudioDG system-wide, with backup/restoration. No paid signing is required for that mode. The release-signing research below applies to a future configuration that retains Windows audio protections. See MICROPHONE.md for current behavior.

# Soundshark Signing Setup — Personal Project

Prepared 2026-10-17. No paid resource, publisher identity, certificate trust or live microphone setting has been created or changed.

A normal app signature identifies the publisher of Soundshark.exe and its installers. It does not demonstrate that the microphone DLL can load in protected AudioDG. Microsoft's current [APO sample requests PETrust](https://github.com/microsoft/Windows-driver-samples/blob/main/audio/sysvad/TabletAudioSample/ComponentizedApoSample.inx). Physical microphone playback remains disabled pending the appropriate Microsoft signature and live validation, even if the app is signed.

## First step for microphone signing

Confirm individual/sole-developer eligibility and the submission route with Microsoft before buying a certificate. The [Hardware Developer Program prerequisites](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/hardware-program-register) describe an organization, Entra administrator, legal contact and EV certificate. [Driver signing offerings](https://learn.microsoft.com/en-us/windows-hardware/drivers/dashboard/driver-signing-offerings) distinguish HLK certification from attestation for testing and list PETrust among supported processing levels. These documents do not establish that this personal project's device-independent attachment model will be accepted.

Use the [support inquiry draft](SIGNING-INQUIRY.md) to get those points confirmed. It has not been sent. Do not buy an OV/EV certificate or Azure subscription assuming it alone enables microphone playback. The existing installer is a development attachment/restore mechanism, not a completed driver-store INF/catalog submission. An accepted package and applicable Microsoft tests are still needed.

## Optional ordinary app signing

For an individual located in the US or Canada, [Azure Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart) supports Public Trust profiles. Protected-audio acceptance remains unconfirmed. Microsoft's [app signing comparison](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/code-signing-options) lists approximately US$9.99/month; check checkout currency/tax and [current pricing](https://azure.microsoft.com/en-us/pricing/details/artifact-signing/) before subscribing. [A paid Azure subscription is required](https://learn.microsoft.com/en-us/azure/artifact-signing/faq), with full monthly tier billing rather than proration. Signing does not guarantee immediate removal of SmartScreen warnings.

If you choose ordinary app signing:

1. Use your own Azure account and Individual billing identity matching your ID. Complete identity verification directly with Microsoft; keep identity documents and private credentials out of this repository/chat.
2. Follow the quickstart to register Microsoft.CodeSigning, create the account, complete Individual identity validation and create a **Public Trust** profile. Public Trust Test is not a release profile. Assign Identity Verifier and Certificate Profile Signer roles as appropriate.
3. Run GetSigningTools.ps1. It downloads pinned official SDK/client packages, verifies NuGet signatures and Microsoft's SignTool signature, without machine-wide installation. .NET 8 x64 is already present here. Other machines also need prerequisites from Microsoft's [integration guide](https://learn.microsoft.com/en-us/azure/artifact-signing/how-to-signing-integrations).
4. Install official Azure CLI if needed and run `az login`. Copy packaging/artifact-signing.example.json to .work/signing/metadata.json, filling the account, profile and exact regional endpoint. The example uses Azure CLI credentials only and contains no password/key. Your validated legal name identifies the publisher; the app brand does not replace publisher identity.
5. Validate offline, then package with the metadata argument after profile approval. An explicit signing run contacts Microsoft's service and may incur usage. No signing request was made during preparation.

```
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/GetSigningTools.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/SignApp.ps1 -MetadataPath .work/signing/metadata.json -Files outputs/app/Soundshark.exe
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Package.ps1 -ArtifactSigningMetadataPath .work/signing/metadata.json
```

SignApp.ps1 defaults to offline validation. Explicit -Sign enables signing, verifies trust/timestamps and saves hash/publisher receipts under outputs/signing. It refuses SoundsharkMicrophone.dll because this workflow cannot approve protected-audio trust. Package.ps1 signs the microphone **installer executable**, app EXE/DLL and final app installer in packaging order; the microphone DLL remains disabled. The portable archive includes signed app files. Local-certificate Package parameters only pre-sign the DLL, without granting protected-environment approval or enabling transmission.

## After Microsoft confirms the native route

Prepare the required APO INF/catalog/submission with PETrust and only additional attributes Microsoft specifies. Complete applicable HLK/compatibility evidence and submit through the approved account. Retain returned binaries, submission IDs, hashes and verification results. Verify intended code, rebuild the installer around the returned DLL and run MICROPHONE.md's real acceptance checks. An approved-native packaging path is pending the actual approval format. Never replace the DLL with a different build after signing.
