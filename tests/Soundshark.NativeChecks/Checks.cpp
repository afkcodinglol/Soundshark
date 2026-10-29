#define SOUNDSHARK_NATIVE_TEST
#include "../../src/Soundshark.AudioNative/SoundsharkApo.cpp"
#include <cstdio>
#include <cstdlib>
#include <vector>

int checks = 0;
void Check(bool condition, const char* name) { if (!condition) { std::printf("FAIL %s\n", name); std::exit(1); } ++checks; std::printf("PASS %s\n", name); }
void Fill(shark::Shared& s, LONG64 generation, LONG64 frames, LONG flags = shark::Active) {
    ZeroMemory(&s, sizeof(s)); s.h.magic = shark::Magic; s.h.version = shark::Version; s.h.capacity = shark::Capacity;
    s.h.rate = 48000; s.h.channels = 2; s.h.generation = generation; s.h.written = s.h.total = frames;
    s.h.producerHeartbeat = 1000; s.h.voiceGain = s.h.soundGain = 1; s.h.flags = flags;
    for (LONG64 i = 0; i < frames && i < shark::Capacity; ++i) { s.samples[i * 2] = .2f; s.samples[i * 2 + 1] = .4f; }
}
bool Near(float a, float b) { return std::fabs(a - b) < .00001f; }
int main() {
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    IClassFactory* factory = nullptr;
    Check(SUCCEEDED(DllGetClassObject(CLSID_SoundsharkMicrophone, __uuidof(IClassFactory), reinterpret_cast<void**>(&factory))), "COM export returns the Windows class factory");
    IAudioProcessingObject* created = nullptr;
    Check(SUCCEEDED(factory->CreateInstance(nullptr, __uuidof(IAudioProcessingObject), reinterpret_cast<void**>(&created))), "class factory creates a standalone APO"); created->Release();
    class Controller final : public IUnknown {
    public:
        ULONG refs = 1;
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override { if (!result) return E_POINTER; *result = nullptr; if (iid != __uuidof(IUnknown)) return E_NOINTERFACE; *result = this; AddRef(); return S_OK; }
        ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
        ULONG STDMETHODCALLTYPE Release() override { return --refs; }
    } controller;
    IUnknown* inner = nullptr;
    Check(SUCCEEDED(factory->CreateInstance(&controller, __uuidof(IUnknown), reinterpret_cast<void**>(&inner))), "Windows audio engine can aggregate the APO through its inner unknown");
    inner->QueryInterface(__uuidof(IAudioProcessingObject), reinterpret_cast<void**>(&created));
    IUnknown* identity = nullptr; created->QueryInterface(__uuidof(IUnknown), reinterpret_cast<void**>(&identity));
    bool identityMatches = identity == &controller; identity->Release(); created->Release(); inner->Release(); factory->Release();
    Check(identityMatches && controller.refs == 1, "aggregated APO preserves controlling identity and balanced references");
    auto s = new shark::Shared(); shark::Cursor cursor;
    Check(VirtualLock(s, sizeof(shark::Shared)) != 0, "compact shared ring fits the controlled host locked-memory quota"); VirtualUnlock(s, sizeof(shark::Shared));
    float voice[8]{.1f,.1f,.1f,.1f,.1f,.1f,.1f,.1f}, out[8]{};
    Fill(*s, 1, 4); shark::Process(s, cursor, voice, false, out, 4, 2, 48000, 1100);
    Check(Near(out[0], .3f) && Near(out[1], .5f), "native mixer adds clip to physical voice");
    Fill(*s, 2, 4, shark::Active | shark::BlockVoice); shark::Process(s, cursor, voice, false, out, 4, 2, 48000, 1100);
    Check(Near(out[0], .2f) && Near(out[1], .4f), "block voice replaces only the transmitted microphone buffer");
    shark::Process(s, cursor, voice, false, out, 4, 2, 48000, 1101);
    Check(Near(out[0], .1f) && cursor.frame == 4, "EOF restores physical voice without repeating stale audio");
    Fill(*s, 22, 1); s->h.voiceGain = .5f;
    shark::Process(s, cursor, voice, false, out, 4, 2, 48000, 1100);
    Check(Near(out[0], .25f) && Near(out[2], .1f), "voice volume applies only to clip frames and restores within the EOF block");
    Fill(*s, 3, 4, shark::Active | shark::BlockVoice); s->h.written = 0;
    shark::Process(s, cursor, voice, false, out, 4, 2, 48000, 1100);
    Check(Near(out[0], 0) && cursor.frame == 0, "temporary starvation keeps voice blocked and does not skip clip frames");
    shark::Process(s, cursor, voice, false, out, 4, 2, 48000, 1800);
    Check(Near(out[0], .1f), "producer timeout fails open to physical voice");
    Fill(*s, 4, 4, shark::Active | shark::BlockVoice | shark::Paused);
    shark::Process(s, cursor, voice, false, out, 4, 2, 48000, 1100);
    Check(Near(out[0], .1f) && cursor.frame == 0, "pause restores voice and holds the sound timeline");
    Fill(*s, 5, 4); shark::Process(s, cursor, nullptr, true, out, 4, 2, 48000, 1100);
    Check(Near(out[0], .2f), "silent microphone input still produces valid uploaded sound");
    Fill(*s, 6, 4); shark::Process(s, cursor, voice, false, out, 4, 1, 48000, 1100);
    Check(Near(out[0], .4f), "stereo clip downmixes correctly for a mono microphone");
    Fill(*s, 7, 4); s->samples[0] = NAN; s->samples[1] = INFINITY;
    shark::Process(s, cursor, voice, false, out, 4, 2, 48000, 1100);
    Check(Near(out[0], .1f) && Near(out[1], .1f), "nonfinite clip data cannot contaminate capture samples");
    Fill(*s, 8, 4); s->h.voiceGain = s->h.soundGain = 2;
    float loud[8]{1,1,1,1,1,1,1,1}; shark::Process(s, cursor, loud, false, out, 4, 2, 48000, 1100);
    Check(*std::max_element(out, out + 8) <= .98001f, "summed native audio has bounded block-peak protection");
    Fill(*s, 9, 4); s->h.written = shark::Capacity + 1; s->h.total = s->h.written;
    shark::Process(s, cursor, voice, false, out, 4, 2, 48000, 1100);
    Check(Near(out[0], .1f), "ring overrun cannot replay overwritten data or gate physical voice");
    Fill(*s, 10, 4); shark::Process(s, cursor, voice, false, out, 4, 2, 44100, 1100);
    Check(Near(out[0], .1f), "format mismatch passes physical voice unchanged");
    // Exercise voice-only EQ while no clip is playing, using a known 1 kHz tone.
    Fill(*s, 30, 0, 0); s->h.eqEnabled = 1; s->h.eqSequence = 2; s->h.eqHeartbeat = 1000;
    for (int b = 0; b < 5; ++b) s->h.eqCoefficients[b * 5] = 1;
    double w = 2 * 3.141592653589793 * 1000 / 48000, a = std::pow(10., 6. / 40), alpha = std::sin(w) / 1.6, a0 = 1 + alpha / a;
    double coefficients[]{(1 + alpha * a) / a0, -2 * std::cos(w) / a0, (1 - alpha * a) / a0, -2 * std::cos(w) / a0, (1 - alpha / a) / a0};
    for (int i = 0; i < 5; ++i) s->h.eqCoefficients[10 + i] = coefficients[i];
    float tone[256]{}, filtered[256]{}; double energyIn = 0, energyOut = 0; bool separate = true;
    cursor = shark::Cursor{};
    for (int block = 0; block < 100; ++block) {
        for (int f = 0; f < 128; ++f) tone[f * 2] = static_cast<float>(.05 * std::sin(w * (block * 128 + f)));
        shark::Process(s, cursor, tone, false, filtered, 128, 2, 48000, 1100);
        if (block > 10) for (int f = 0; f < 128; ++f) { energyIn += tone[f * 2] * tone[f * 2]; energyOut += filtered[f * 2] * filtered[f * 2]; separate &= filtered[f * 2 + 1] == 0; }
    }
    Check(std::fabs(std::sqrt(energyOut / energyIn) - std::pow(10., 6. / 20)) < .01, "idle microphone EQ applies measured six dB gain at the chosen band");
    Check(separate, "microphone EQ keeps independent channel histories without cross-talk");
    shark::Process(s, cursor, tone, false, filtered, 128, 2, 48000, 2600);
    Check(Near(filtered[0], tone[0]) && !cursor.eqActive, "stale EQ controller fails open to unfiltered voice");
    s->h.eqCoefficients[13] = 5; ++s->h.eqSequence;
    shark::Process(s, cursor, tone, false, filtered, 128, 2, 48000, 1100);
    Check(Near(filtered[0], tone[0]), "incomplete EQ snapshot cannot enter the realtime filter");
    ++s->h.eqSequence; shark::Process(s, cursor, tone, false, filtered, 128, 2, 48000, 1100);
    Check(Near(filtered[0], tone[0]) && !cursor.eqActive, "unstable EQ coefficients are rejected without blocking voice");
    for (int i = 0; i < 25; ++i) s->h.eqCoefficients[i] = i % 5 == 0 ? 2. : 0.;
    s->h.eqSequence += 2; shark::Process(s, cursor, loud, false, out, 4, 2, 48000, 1100);
    Check(*std::max_element(out, out + 8) <= .98001f, "EQ-only microphone boosts also receive peak protection");
    s->h.eqEnabled = 0; shark::Process(s, cursor, voice, false, out, 4, 2, 48000, 1100);
    Check(Near(out[0], voice[0]), "disabling EQ immediately restores original voice");
    // Exercise actual COM/APO lifecycle, format negotiation, lock and callback without registration.
    Apo* apo = new Apo(); APOInitSystemEffects init{}; init.APOInit.clsid = CLSID_SoundsharkMicrophone; init.APOInit.cbSize = sizeof(init);
    Check(SUCCEEDED(apo->Initialize(sizeof(init), reinterpret_cast<BYTE*>(&init))), "APO initializes through the Windows interface without endpoint mutation");
    UNCOMPRESSEDAUDIOFORMAT format{}; format.guidFormatType = KSDATAFORMAT_SUBTYPE_IEEE_FLOAT; format.dwSamplesPerFrame = 2; format.dwBytesPerSampleContainer = 4; format.dwValidBitsPerSample = 32; format.fFramesPerSecond = 48000; format.dwChannelMask = 3;
    IAudioMediaType* media = nullptr; Check(SUCCEEDED(CreateAudioMediaTypeFromUncompressedAudioFormat(&format, &media)), "native media type negotiates float stereo");
    APO_CONNECTION_DESCRIPTOR input{}, output{};
    input.Type = output.Type = APO_CONNECTION_BUFFER_TYPE_EXTERNAL;
    input.pBuffer = reinterpret_cast<UINT_PTR>(voice); output.pBuffer = reinterpret_cast<UINT_PTR>(out);
    input.u32MaxFrameCount = output.u32MaxFrameCount = 4; input.pFormat = output.pFormat = media;
    input.u32Signature = output.u32Signature = APO_CONNECTION_DESCRIPTOR_SIGNATURE;
    APO_CONNECTION_DESCRIPTOR* inputs[]{&input}; APO_CONNECTION_DESCRIPTOR* outputs[]{&output};
    Check(SUCCEEDED(apo->LockForProcess(1, inputs, 1, outputs)), "Windows APO lock validates matching input/output buffers");
    Fill(*s, 11, 4); apo->AttachForHarness(s); s->h.producerHeartbeat = GetTickCount64();
    apo->UnlockForProcess(); Check(SUCCEEDED(apo->LockForProcess(1, inputs, 1, outputs)), "native endpoint reader claims a tracked IPC slot");
    APO_CONNECTION_PROPERTY ip{}, op{}; ip.pBuffer = input.pBuffer; op.pBuffer = output.pBuffer; ip.u32ValidFrameCount = 4; ip.u32BufferFlags = BUFFER_VALID;
    APO_CONNECTION_PROPERTY* ips[]{&ip}; APO_CONNECTION_PROPERTY* ops[]{&op}; apo->APOProcess(1, ips, 1, ops);
    Check(op.u32BufferFlags == BUFFER_VALID && op.u32ValidFrameCount == 4 && Near(out[0], .3f), "actual APO callback mixes frames and marks output valid");
    apo->DetachForHarness(); Check(SUCCEEDED(apo->UnlockForProcess()), "native APO unlock releases its process state");
    apo->Release(); media->Release(); delete s;
    Check(DllCanUnloadNow() == S_OK, "native COM objects release cleanly");
    std::printf("%d native checks passed\n", checks); CoUninitialize();
}
