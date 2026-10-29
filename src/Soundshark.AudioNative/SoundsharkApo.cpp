#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <initguid.h>
#include <audioenginebaseapo.h>
#include <baseaudioprocessingobject.h>
#include <mmdeviceapi.h>
#include <functiondiscoverykeys_devpkey.h>
#include <sddl.h>
#include <propvarutil.h>
#include <new>
#include <evntprov.h>
#include "AudioBridge.h"

DEFINE_GUID(CLSID_SoundsharkMicrophone, 0xa08799cd,0x8535,0x492d,0xa1,0xb4,0xf4,0xc8,0xb5,0x1e,0x87,0x79);
DEFINE_PROPERTYKEY(PKEY_SharkOriginal, 0xa08799cd,0x8535,0x492d,0xa1,0xb4,0xf4,0xc8,0xb5,0x1e,0x87,0x79, 1);
DEFINE_PROPERTYKEY(PKEY_SharkUserSid, 0xa08799cd,0x8535,0x492d,0xa1,0xb4,0xf4,0xc8,0xb5,0x1e,0x87,0x79, 2);
static LONG objects = 0, serverLocks = 0;
// Initialization diagnostics only. The real-time audio callback never logs.
void Trace(const wchar_t* stage, HRESULT result = S_OK) {
    struct Provider { REGHANDLE handle = 0; Provider() { EventRegister(&CLSID_SoundsharkMicrophone, nullptr, nullptr, &handle); } ~Provider() { if (handle) EventUnregister(handle); } };
    static Provider provider;
    wchar_t message[256]{}; swprintf_s(message, L"%s HRESULT=0x%08X", stage, static_cast<unsigned>(result));
    if (provider.handle) EventWriteString(provider.handle, 4, 1, message);
}
void TraceGuid(const wchar_t* stage, REFGUID guid) { wchar_t id[40]{}, message[128]{}; StringFromGUID2(guid, id, 40); swprintf_s(message, L"%s %s", stage, id); Trace(message); }
// A small standard media-type implementation avoids an ATL runtime dependency.
class MediaType final : public IAudioMediaType {
    LONG refs = 1;
    UNCOMPRESSEDAUDIOFORMAT format;
    WAVEFORMATEXTENSIBLE wave{};
public:
    explicit MediaType(const UNCOMPRESSEDAUDIOFORMAT& value) : format(value) {
        InterlockedIncrement(&objects);
        wave.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE; wave.Format.nChannels = static_cast<WORD>(value.dwSamplesPerFrame);
        wave.Format.nSamplesPerSec = static_cast<DWORD>(value.fFramesPerSecond); wave.Format.wBitsPerSample = static_cast<WORD>(value.dwBytesPerSampleContainer * 8);
        wave.Format.nBlockAlign = static_cast<WORD>(wave.Format.nChannels * value.dwBytesPerSampleContainer);
        wave.Format.nAvgBytesPerSec = wave.Format.nSamplesPerSec * wave.Format.nBlockAlign;
        wave.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
        wave.Samples.wValidBitsPerSample = static_cast<WORD>(value.dwValidBitsPerSample); wave.dwChannelMask = value.dwChannelMask; wave.SubFormat = value.guidFormatType;
    }
    ~MediaType() { InterlockedDecrement(&objects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override { if (!result) return E_POINTER; *result = nullptr; if (iid != __uuidof(IUnknown) && iid != __uuidof(IAudioMediaType)) return E_NOINTERFACE; *result = this; AddRef(); return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto value = InterlockedDecrement(&refs); if (!value) delete this; return value; }
    HRESULT STDMETHODCALLTYPE IsCompressedFormat(BOOL* compressed) override { if (!compressed) return E_POINTER; *compressed = FALSE; return S_OK; }
    HRESULT STDMETHODCALLTYPE IsEqual(IAudioMediaType* other, DWORD* flags) override {
        if (!other || !flags) return E_POINTER; *flags = 0; UNCOMPRESSEDAUDIOFORMAT f{};
        HRESULT hr = other->GetUncompressedAudioFormat(&f); if (FAILED(hr)) return hr;
        if (f.guidFormatType == format.guidFormatType) *flags |= AUDIOMEDIATYPE_EQUAL_FORMAT_TYPES;
        if (f.dwSamplesPerFrame == format.dwSamplesPerFrame && f.dwBytesPerSampleContainer == format.dwBytesPerSampleContainer && f.dwValidBitsPerSample == format.dwValidBitsPerSample && f.fFramesPerSecond == format.fFramesPerSecond && f.dwChannelMask == format.dwChannelMask) *flags |= AUDIOMEDIATYPE_EQUAL_FORMAT_DATA | AUDIOMEDIATYPE_EQUAL_FORMAT_USER_DATA;
        return *flags == 14 ? S_OK : S_FALSE;
    }
    const WAVEFORMATEX* STDMETHODCALLTYPE GetAudioFormat() override { return &wave.Format; }
    HRESULT STDMETHODCALLTYPE GetUncompressedAudioFormat(UNCOMPRESSEDAUDIOFORMAT* result) override { if (!result) return E_POINTER; *result = format; return S_OK; }
};
STDAPI CreateAudioMediaTypeFromUncompressedAudioFormat(const UNCOMPRESSEDAUDIOFORMAT* format, IAudioMediaType** result) {
    if (!result) return E_POINTER; *result = nullptr;
    if (!format || format->dwSamplesPerFrame == 0 || format->dwSamplesPerFrame > 32 || !std::isfinite(format->fFramesPerSecond) || format->fFramesPerSecond < 8000 || format->fFramesPerSecond > 384000 || format->dwBytesPerSampleContainer == 0 || format->dwBytesPerSampleContainer > 8 || format->dwValidBitsPerSample > format->dwBytesPerSampleContainer * 8) return E_INVALIDARG;
    *result = new(std::nothrow) MediaType(*format); return *result ? S_OK : E_OUTOFMEMORY;
}
#pragma warning(push)
#pragma warning(disable:4815)
static const CRegAPOProperties<1> properties(CLSID_SoundsharkMicrophone, L"Soundshark Microphone Mixer",
    L"Soundshark", 1, 0, __uuidof(IAudioProcessingObject));
#pragma warning(pop)

class Apo final : public CBaseAudioProcessingObject, public IAudioSystemEffects2 {
    LONG refs = 1;
    IUnknown* controlling = nullptr;
    class InnerUnknown final : public IUnknown {
        Apo* owner;
    public:
        explicit InnerUnknown(Apo* value) : owner(value) {}
        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override { return owner->NondelegatingQueryInterface(iid, result); }
        ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&owner->refs); }
        ULONG STDMETHODCALLTYPE Release() override { LONG count = InterlockedDecrement(&owner->refs); if (!count) delete owner; return count; }
    } inner{this};
    HANDLE mapping = nullptr;
    shark::Shared* shared = nullptr;
    shark::Cursor cursor;
    UINT channels = 0, rate = 0, maxFrames = 0;
    IAudioProcessingObject* original = nullptr;
    IAudioProcessingObjectRT* originalRt = nullptr;
    IAudioProcessingObjectConfiguration* originalConfig = nullptr;
    bool originalLocked = false;
    void Detach() {
        if (shared) {
            if (cursor.slot >= 0) InterlockedExchange64(&shared->h.reader[cursor.slot].token, 0);
            VirtualUnlock(shared, sizeof(shark::Shared)); UnmapViewOfFile(shared); shared = nullptr;
        }
        if (mapping) { CloseHandle(mapping); mapping = nullptr; }
    }
    void Connect(IPropertyStore* endpoint, IPropertyStore* effects) {
        PROPVARIANT id{}, sid{};
        if (!endpoint || !effects) { Trace(L"Connect missing property stores", E_POINTER); return; }
        HRESULT idResult = endpoint->GetValue(PKEY_AudioEndpoint_GUID, &id); Trace(L"Connect endpoint GUID", idResult);
        if (FAILED(idResult)) return;
        if (SUCCEEDED(effects->GetValue(PKEY_SharkUserSid, &sid)) && id.vt == VT_LPWSTR && sid.vt == VT_LPWSTR) {
            GUID check{}; PSID parsed = nullptr;
            if (SUCCEEDED(CLSIDFromString(id.pwszVal, &check)) && ConvertStringSidToSidW(sid.pwszVal, &parsed)) {
                wchar_t name[128]{}, security[256]{}, canonical[40]{};
                StringFromGUID2(check, canonical, 40);
                swprintf_s(name, L"Global\\Soundshark-Audio-v1-%s", canonical);
                swprintf_s(security, L"D:P(A;;GA;;;SY)(A;;GA;;;LS)(A;;GA;;;%s)", sid.pwszVal);
                PSECURITY_DESCRIPTOR descriptor = nullptr;
                if (ConvertStringSecurityDescriptorToSecurityDescriptorW(security, SDDL_REVISION_1, &descriptor, nullptr)) {
                    SECURITY_ATTRIBUTES attributes{sizeof(attributes), descriptor, FALSE};
                    mapping = CreateFileMappingW(INVALID_HANDLE_VALUE, &attributes, PAGE_READWRITE, 0, sizeof(shark::Shared), name);
                    Trace(L"Connect create mapping", mapping ? S_OK : HRESULT_FROM_WIN32(GetLastError()));
                    bool created = GetLastError() != ERROR_ALREADY_EXISTS;
                    if (mapping) shared = static_cast<shark::Shared*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(shark::Shared)));
                    if (shared && !VirtualLock(shared, sizeof(shark::Shared))) { Trace(L"Connect VirtualLock", HRESULT_FROM_WIN32(GetLastError())); Detach(); }
                    if (shared && created) {
                        shared->h.version = shark::Version; shared->h.capacity = shark::Capacity;
                        shared->h.voiceGain = shared->h.soundGain = 1;
                        InterlockedExchange(&shared->h.magic, shark::Magic);
                    }
                    if (shared && (shark::Read(shared->h.magic) != shark::Magic || shark::Read(shared->h.version) != shark::Version)) Detach();
                    Trace(shared ? L"Connect ready" : L"Connect not ready");
                    LocalFree(descriptor);
                }
                LocalFree(parsed);
            }
        }
        if (id.vt != VT_LPWSTR || sid.vt != VT_LPWSTR) Trace(L"Connect missing GUID or user SID", E_INVALIDARG);
        PropVariantClear(&id); PropVariantClear(&sid);
    }
public:
    explicit Apo(IUnknown* outer = nullptr) : CBaseAudioProcessingObject(&static_cast<const APO_REG_PROPERTIES&>(properties)), controlling(outer) { InterlockedIncrement(&objects); }
    ~Apo() override {
        if (originalLocked) originalConfig->UnlockForProcess();
        Detach(); if (originalRt) originalRt->Release(); if (originalConfig) originalConfig->Release(); if (original) original->Release();
        InterlockedDecrement(&objects);
    }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override {
        return controlling ? controlling->QueryInterface(iid, result) : NondelegatingQueryInterface(iid, result);
    }
    HRESULT NondelegatingQueryInterface(REFIID iid, void** result) {
        if (!result) return E_POINTER; *result = nullptr;
        if (iid == __uuidof(IUnknown)) { *result = static_cast<IUnknown*>(&inner); inner.AddRef(); return S_OK; }
        if (iid == __uuidof(IAudioProcessingObject)) *result = static_cast<IAudioProcessingObject*>(this);
        else if (iid == __uuidof(IAudioProcessingObjectRT)) *result = static_cast<IAudioProcessingObjectRT*>(this);
        else if (iid == __uuidof(IAudioProcessingObjectConfiguration)) *result = static_cast<IAudioProcessingObjectConfiguration*>(this);
        else if (iid == __uuidof(IAudioSystemEffects) || iid == __uuidof(IAudioSystemEffects2)) *result = static_cast<IAudioSystemEffects2*>(this);
        else return E_NOINTERFACE;
        AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return controlling ? controlling->AddRef() : inner.AddRef(); }
    ULONG STDMETHODCALLTYPE Release() override { return controlling ? controlling->Release() : inner.Release(); }
    ULONG ReleaseInitialReference() { return inner.Release(); }
    HRESULT STDMETHODCALLTYPE Initialize(UINT size, BYTE* data) override {
        Trace(L"Initialize enter");
        if (m_bIsInitialized) return APOERR_ALREADY_INITIALIZED;
        if (!data || size < sizeof(APOInitSystemEffects)) return E_INVALIDARG;
        auto init = reinterpret_cast<APOInitSystemEffects*>(data);
        if (init->APOInit.clsid != CLSID_SoundsharkMicrophone) return E_INVALIDARG;
        PROPVARIANT previous{};
        if (init->pAPOSystemEffectsProperties && SUCCEEDED(init->pAPOSystemEffectsProperties->GetValue(PKEY_SharkOriginal, &previous)) && previous.vt == VT_LPWSTR && previous.pwszVal[0]) {
            CLSID clsid{}; HRESULT hr = CLSIDFromString(previous.pwszVal, &clsid);
            if (SUCCEEDED(hr) && clsid == CLSID_SoundsharkMicrophone) hr = E_INVALIDARG;
            if (SUCCEEDED(hr)) hr = CoCreateInstance(clsid, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&original));
            if (SUCCEEDED(hr)) hr = original->QueryInterface(IID_PPV_ARGS(&originalRt));
            if (SUCCEEDED(hr)) hr = original->QueryInterface(IID_PPV_ARGS(&originalConfig));
            if (SUCCEEDED(hr)) {
                auto copy = static_cast<BYTE*>(CoTaskMemAlloc(size));
                if (!copy) hr = E_OUTOFMEMORY;
                else { CopyMemory(copy, data, size); reinterpret_cast<APOInitBaseStruct*>(copy)->clsid = clsid; hr = original->Initialize(size, copy); CoTaskMemFree(copy); }
            }
            Trace(L"Initialize original APO", hr); PropVariantClear(&previous); if (FAILED(hr)) return hr;
        } else PropVariantClear(&previous);
        Connect(init->pAPOEndpointProperties, init->pAPOSystemEffectsProperties);
        m_bIsInitialized = true; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetEffectsList(GUID** effects, UINT* count, HANDLE event) override {
        if (!effects || !count) return E_POINTER; *effects = nullptr; *count = 0;
        IAudioSystemEffects2* previous = nullptr; GUID* existing = nullptr; UINT amount = 0;
        if (original && SUCCEEDED(original->QueryInterface(IID_PPV_ARGS(&previous)))) { HRESULT hr = previous->GetEffectsList(&existing, &amount, event); previous->Release(); if (FAILED(hr)) { CoTaskMemFree(existing); return hr; } }
        if (amount > 1024 || amount > 0 && !existing) { CoTaskMemFree(existing); return E_INVALIDARG; }
        auto list = static_cast<GUID*>(CoTaskMemAlloc((amount + 1) * sizeof(GUID))); if (!list) { CoTaskMemFree(existing); return E_OUTOFMEMORY; }
        if (amount) CopyMemory(list, existing, amount * sizeof(GUID)); list[amount] = CLSID_SoundsharkMicrophone;
        CoTaskMemFree(existing); *effects = list; *count = amount + 1; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE IsInputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) override {
        HRESULT hr = CBaseAudioProcessingObject::IsInputFormatSupported(opposite, requested, supported);
        if (hr == S_OK && original) { if (supported && *supported) { (*supported)->Release(); *supported = nullptr; } return original->IsInputFormatSupported(opposite, requested, supported); }
        return hr;
    }
    HRESULT STDMETHODCALLTYPE IsOutputFormatSupported(IAudioMediaType* opposite, IAudioMediaType* requested, IAudioMediaType** supported) override {
        HRESULT hr = CBaseAudioProcessingObject::IsOutputFormatSupported(opposite, requested, supported);
        if (hr == S_OK && original) { if (supported && *supported) { (*supported)->Release(); *supported = nullptr; } return original->IsOutputFormatSupported(opposite, requested, supported); }
        return hr;
    }
    HRESULT STDMETHODCALLTYPE LockForProcess(UINT ni, APO_CONNECTION_DESCRIPTOR** in, UINT no, APO_CONNECTION_DESCRIPTOR** out) override {
        HRESULT hr = CBaseAudioProcessingObject::LockForProcess(ni, in, no, out); Trace(L"Lock base", hr); if (FAILED(hr)) return hr;
        channels = GetSamplesPerFrame(); rate = static_cast<UINT>(GetFramesPerSecond()); maxFrames = out[0]->u32MaxFrameCount;
        if (originalConfig) { hr = originalConfig->LockForProcess(ni, in, no, out); if (FAILED(hr)) { CBaseAudioProcessingObject::UnlockForProcess(); return hr; } originalLocked = true; }
        if (shared) {
            InterlockedExchange(&shared->h.rate, rate); InterlockedExchange(&shared->h.channels, channels);
            cursor.token = InterlockedIncrement(&shared->h.readers);
            for (int i = 0; i < 32; ++i) if (InterlockedCompareExchange64(&shared->h.reader[i].token, cursor.token, 0) == 0) { cursor.slot = i; break; }
        }
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE UnlockForProcess() override {
        if (originalLocked) { originalConfig->UnlockForProcess(); originalLocked = false; }
        if (shared && cursor.slot >= 0) { InterlockedExchange64(&shared->h.reader[cursor.slot].token, 0); cursor.slot = -1; }
        return CBaseAudioProcessingObject::UnlockForProcess();
    }
    HRESULT STDMETHODCALLTYPE GetLatency(HNSTIME* latency) override { if (!latency) return E_POINTER; *latency = 0; return original ? original->GetLatency(latency) : S_OK; }
#ifdef SOUNDSHARK_NATIVE_TEST
    void AttachForHarness(shark::Shared* value) { shared = value; }
    void DetachForHarness() { shared = nullptr; }
#endif
#pragma AVRT_CODE_BEGIN
    void STDMETHODCALLTYPE APOProcess(UINT ni, APO_CONNECTION_PROPERTY** in, UINT no, APO_CONNECTION_PROPERTY** out) override {
        if (ni != 1 || no != 1 || !in || !out || !in[0] || !out[0]) return;
        UINT frames = in[0]->u32ValidFrameCount;
        if (!m_bIsLocked || frames > maxFrames || in[0]->u32BufferFlags == BUFFER_INVALID) { out[0]->u32ValidFrameCount = 0; out[0]->u32BufferFlags = BUFFER_INVALID; return; }
        const float* input = reinterpret_cast<const float*>(in[0]->pBuffer);
        bool silent = in[0]->u32BufferFlags == BUFFER_SILENT;
        if (originalRt) { originalRt->APOProcess(ni, in, no, out); frames = out[0]->u32ValidFrameCount; input = reinterpret_cast<const float*>(out[0]->pBuffer); silent = out[0]->u32BufferFlags == BUFFER_SILENT; if (out[0]->u32BufferFlags == BUFFER_INVALID || frames > maxFrames) return; }
        shark::Process(cursor.slot >= 0 ? shared : nullptr, cursor, input, silent, reinterpret_cast<float*>(out[0]->pBuffer), frames, channels, rate, GetTickCount64());
        out[0]->u32ValidFrameCount = frames; out[0]->u32BufferFlags = BUFFER_VALID;
    }
#pragma AVRT_CODE_END
};

class Factory final : public IClassFactory {
    LONG refs = 1;
public:
    Factory() { InterlockedIncrement(&objects); }
    ~Factory() { InterlockedDecrement(&objects); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** result) override { if (!result) return E_POINTER; *result = nullptr; if (iid != __uuidof(IUnknown) && iid != __uuidof(IClassFactory)) return E_NOINTERFACE; *result = this; AddRef(); return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&refs); }
    ULONG STDMETHODCALLTYPE Release() override { auto value = InterlockedDecrement(&refs); if (!value) delete this; return value; }
    HRESULT STDMETHODCALLTYPE CreateInstance(IUnknown* outer, REFIID iid, void** result) override { TraceGuid(L"Factory CreateInstance", iid); if (outer) Trace(L"Factory aggregation requested"); if (!result) return E_POINTER; *result = nullptr; if (outer && iid != __uuidof(IUnknown)) return CLASS_E_NOAGGREGATION; auto apo = new(std::nothrow) Apo(outer); if (!apo) return E_OUTOFMEMORY; HRESULT hr = apo->NondelegatingQueryInterface(iid, result); apo->ReleaseInitialReference(); return hr; }
    HRESULT STDMETHODCALLTYPE LockServer(BOOL lock) override { if (lock) InterlockedIncrement(&serverLocks); else InterlockedDecrement(&serverLocks); return S_OK; }
};
STDAPI DllCanUnloadNow() { return objects == 0 && serverLocks == 0 ? S_OK : S_FALSE; }
STDAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** result) {
    Trace(L"DllGetClassObject");
    TraceGuid(L"Requested class", clsid); TraceGuid(L"Requested factory interface", iid);
    if (clsid != CLSID_SoundsharkMicrophone) return CLASS_E_CLASSNOTAVAILABLE;
    auto factory = new(std::nothrow) Factory(); if (!factory) return E_OUTOFMEMORY; HRESULT hr = factory->QueryInterface(iid, result); factory->Release(); return hr;
}
