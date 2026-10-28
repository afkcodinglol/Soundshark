# Draft — Confirm Soundshark APO Signing Eligibility

For the developer to review and submit through Microsoft's Hardware Developer Program support route. This draft has not been sent.

I am an individual developer building a personal Windows 10/11 x64 audio application, Soundshark. Its independently implemented user-mode capture endpoint APO mixes selected clips into microphone audio, keeping the existing physical microphone selected in communications apps. It preserves existing effects and supports restoration. It does not read protected media or disable audio/signature protections.

Your componentized APO sample requests PETrust, while Hardware Developer Program enrollment describes organization verification and an EV certificate. Before purchasing services, please confirm:

1. Can an individual enroll for the required APO signing path? If a sole proprietorship qualifies, what verified business identity and EV eligibility are required?
2. Is an APO configured for a user-selected existing microphone eligible without being shipped by the microphone OEM? What packaging/device-association requirements apply?
3. What embedded/catalog signature levels are required in protected AudioDG on retail Windows 10/11? Does Artifact Signing Public Trust satisfy these, or is a Hardware Developer Program submission required?
4. Which route applies to limited private testing with a friend versus general release? Are HLK logs required for PETrust, and which current APO playlist applies?
5. Are additional DRM attributes/agreements needed for a capture-only mixing component? Which current submission documentation should we follow?

The DLL builds and passes controlled interface/mixer tests, but is unsigned and blocked from endpoint installation. No certification or live protected-graph compatibility is claimed.
