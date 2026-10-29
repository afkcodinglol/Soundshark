#pragma once
#include <windows.h>
#include <cmath>

namespace shark {
constexpr ULONG Magic = 0x53484B31, Version = 3, Capacity = 8192, HeaderBytes = 4096;
constexpr LONG Active = 1, BlockVoice = 2, Paused = 4;
struct alignas(8) Reader { volatile LONG64 token, generation, frame, heartbeat; };
struct alignas(8) Header {
    volatile LONG magic, version, capacity, rate, channels, flags;
    volatile LONG64 generation, written, total, producerHeartbeat, consumerHeartbeat;
    volatile LONG64 consumed;
    float voiceGain, soundGain, peak;
    volatile LONG readers;
    BYTE reserved[40];
    Reader reader[32];
    volatile LONG64 eqHeartbeat;
    volatile LONG eqSequence, eqEnabled;
    double eqCoefficients[25];
};
static_assert(offsetof(Header, generation) == 24 && offsetof(Header, reader) == 128);
static_assert(offsetof(Header, eqHeartbeat) == 1152 && offsetof(Header, eqCoefficients) == 1168);
struct Shared { Header h; BYTE padding[HeaderBytes - sizeof(Header)]; float samples[Capacity * 2]; };
static_assert(offsetof(Shared, samples) == HeaderBytes);
inline LONG64 Read(volatile LONG64& value) { return InterlockedCompareExchange64(&value, 0, 0); }
inline LONG Read(volatile LONG& value) { return InterlockedCompareExchange(&value, 0, 0); }
inline float Finite(float f) { return std::isfinite(f) ? f : 0.f; }
struct Cursor {
    LONG64 generation = -1, frame = 0; int slot = -1; LONG64 token = 0;
    LONG eqSequence = -1; UINT eqRate = 0; bool eqActive = false;
    double coefficients[25]{}, state[8][5][2]{};
};

// Bounded, allocation-free and nonblocking. Voice passes unchanged on stale/invalid IPC.
#pragma code_seg(push, "RT_CODE")
inline void Process(Shared* shared, Cursor& cursor, const float* input, bool silent,
                    float* output, UINT frames, UINT channels, UINT rate, LONG64 now) {
    bool valid = shared && Read(shared->h.magic) == Magic && Read(shared->h.version) == Version
        && Read(shared->h.capacity) == Capacity && Read(shared->h.rate) == static_cast<LONG>(rate);
    LONG64 age = valid ? now - Read(shared->h.producerHeartbeat) : 10000;
    bool fresh = valid && age >= -100 && age < 750;
    LONG64 eqAge = valid ? now - Read(shared->h.eqHeartbeat) : 10000;
    bool equalize = valid && channels <= 8 && eqAge >= -100 && eqAge < 1500 && Read(shared->h.eqEnabled) == 1;
    LONG sequence = valid ? Read(shared->h.eqSequence) : -1;
    if (equalize && !(sequence & 1) && (sequence != cursor.eqSequence || rate != cursor.eqRate || !cursor.eqActive)) {
        double coefficients[25]; bool safe = true;
        for (int i = 0; i < 25; ++i) { coefficients[i] = shared->h.eqCoefficients[i]; safe &= std::isfinite(coefficients[i]) && std::fabs(coefficients[i]) < 8; }
        // Validate each biquad's poles, and take only a complete control-thread snapshot. No retry loop.
        for (int b = 0; b < 5; ++b) {
            double a1 = coefficients[b * 5 + 3], a2 = coefficients[b * 5 + 4];
            safe &= std::fabs(a2) < 1 && 1 + a1 + a2 > 0 && 1 - a1 + a2 > 0;
        }
        if (Read(shared->h.eqSequence) == sequence && safe) {
            for (int i = 0; i < 25; ++i) cursor.coefficients[i] = coefficients[i];
            ZeroMemory(cursor.state, sizeof(cursor.state)); cursor.eqSequence = sequence; cursor.eqRate = rate;
        } else equalize = false;
    }
    equalize &= !(sequence & 1) && cursor.eqSequence == sequence && cursor.eqRate == rate;
    if (!equalize && cursor.eqActive) ZeroMemory(cursor.state, sizeof(cursor.state));
    cursor.eqActive = equalize;
    LONG flags = fresh ? Read(shared->h.flags) : 0;
    LONG64 generation = valid ? Read(shared->h.generation) : -1;
    if (generation != cursor.generation) { cursor.generation = generation; cursor.frame = 0; }
    LONG64 written = valid ? Read(shared->h.written) : 0, total = valid ? Read(shared->h.total) : 0;
    bool sound = (flags & Active) && !(flags & Paused) && total > 0 && cursor.frame < total;
    // An overwritten/inconsistent ring must never replay unrelated samples or keep voice gated.
    if (written < cursor.frame || written - cursor.frame > Capacity || total < written) { sound = false; fresh = false; flags = 0; }
    float voiceGain = sound ? Finite(shared->h.voiceGain) : 1.f;
    float soundGain = fresh ? Finite(shared->h.soundGain) : 0.f;
    voiceGain = voiceGain < 0 ? 0 : voiceGain > 2 ? 2 : voiceGain;
    soundGain = soundGain < 0 ? 0 : soundGain > 2 ? 2 : soundGain;
    float peak = 0;
    bool mixed = false;
    for (UINT f = 0; f < frames; ++f) {
        bool activeFrame = sound && cursor.frame < total;
        bool have = activeFrame && cursor.frame < written;
        float left = 0, right = 0;
        if (have) {
            auto offset = static_cast<UINT>(cursor.frame % Capacity) * 2;
            left = Finite(shared->samples[offset]) * soundGain;
            right = Finite(shared->samples[offset + 1]) * soundGain;
            ++cursor.frame;
        }
        // Keep block-voice active during starvation, but not after EOF, pause, stop or timeout.
        bool gate = activeFrame && (flags & BlockVoice);
        mixed |= activeFrame;
        for (UINT c = 0; c < channels; ++c) {
            UINT i = f * channels + c;
            float voice = silent || gate || !input ? 0.f : Finite(input[i]) * (activeFrame ? voiceGain : 1.f);
            if (equalize && gate) ZeroMemory(cursor.state[c], sizeof(cursor.state[c]));
            if (equalize && !gate) {
                double value = voice;
                for (int b = 0; b < 5; ++b) {
                    auto* k = cursor.coefficients + b * 5; auto* z = cursor.state[c][b];
                    double filtered = k[0] * value + z[0];
                    z[0] = k[1] * value - k[3] * filtered + z[1]; z[1] = k[2] * value - k[4] * filtered;
                    if (std::fabs(z[0]) < 1e-20) z[0] = 0; if (std::fabs(z[1]) < 1e-20) z[1] = 0;
                    value = filtered;
                }
                voice = Finite(static_cast<float>(value));
            }
            float clip = channels == 1 ? (left + right) * .5f : (c % 2 ? right : left);
            output[i] = voice + clip;
            float magnitude = std::fabs(output[i]); if (magnitude > peak) peak = magnitude;
        }
    }
    // Block peak protection for the summed stream; not advertised as a true-peak limiter.
    if ((mixed || equalize) && peak > .98f) {
        float scale = .98f / peak;
        for (UINT i = 0; i < frames * channels; ++i) output[i] *= scale;
        peak = .98f;
    }
    if (valid) {
        shared->h.peak = peak;
        InterlockedExchange64(&shared->h.consumerHeartbeat, now);
        if (cursor.slot >= 0) {
            auto& r = shared->h.reader[cursor.slot];
            InterlockedExchange64(&r.generation, generation);
            InterlockedExchange64(&r.frame, cursor.frame);
            InterlockedExchange64(&r.heartbeat, now);
        }
        InterlockedExchange64(&shared->h.consumed, cursor.frame);
    }
}
#pragma code_seg(pop)
}
