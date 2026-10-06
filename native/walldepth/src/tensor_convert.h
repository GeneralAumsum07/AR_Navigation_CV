// Pure conversions between the app's RGB bytes / float results and the network's tensor
// encodings. No QNN types here, so this file is unit-tested on its own (test/convert_test.cpp).
#pragma once

#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace wd {

enum class DType { F32, F16, U8, U16 };
enum class Layout { NHWC, NCHW };

// QNN's scale/offset encoding: real = scale * (q + offset). Offsets are usually <= 0.
struct Quant {
    float scale = 1.f;
    int32_t offset = 0;
};

inline size_t BytesPer(DType t)
{
    switch (t) {
        case DType::F32: return 4;
        case DType::F16: return 2;
        case DType::U16: return 2;
        default: return 1;
    }
}

inline bool IsQuantised(DType t) { return t == DType::U8 || t == DType::U16; }

inline uint32_t Quantise(float x, Quant q, uint32_t maxQ)
{
    // Clamp, never wrap: a wrapped value would turn a bright pixel black.
    long v = std::lround(x / q.scale) - q.offset;
    if (v < 0) v = 0;
    if (v > static_cast<long>(maxQ)) v = static_cast<long>(maxQ);
    return static_cast<uint32_t>(v);
}

// RGB bytes (size x size x 3, row 0 = top) -> network input in [0, 1], in the tensor's layout
// and type. The graph does its own mean/std normalisation (Task 7 records this from the model).
inline bool PackInput(const uint8_t* rgb, int size, Layout layout, DType type, Quant q, void* dst, size_t dstBytes)
{
    if (!rgb || !dst || size <= 0) return false;
    const size_t n = static_cast<size_t>(size) * size;
    if (dstBytes != n * 3 * BytesPer(type)) return false;
    if (IsQuantised(type) && !(q.scale > 0.f)) return false;

    // The input has only 256 levels: encode each level once rather than 800k divisions per frame.
    float levels[256];
    uint16_t codes[256];
    const uint32_t maxQ = type == DType::U8 ? 255u : 65535u;
    for (int i = 0; i < 256; ++i) {
        levels[i] = static_cast<float>(i) / 255.f;
        codes[i] = IsQuantised(type) ? static_cast<uint16_t>(Quantise(levels[i], q, maxQ)) : 0;
    }

    auto* out = static_cast<uint8_t*>(dst);
    for (size_t p = 0; p < n; ++p) {
        for (int c = 0; c < 3; ++c) {
            const uint8_t level = rgb[p * 3 + c];
            const size_t i = layout == Layout::NHWC ? p * 3 + c : c * n + p;
            switch (type) {
                case DType::F32: std::memcpy(out + i * 4, &levels[level], 4); break;
                case DType::F16: { __fp16 h = static_cast<__fp16>(levels[level]); std::memcpy(out + i * 2, &h, 2); break; }
                case DType::U8: out[i] = static_cast<uint8_t>(codes[level]); break;
                case DType::U16: std::memcpy(out + i * 2, &codes[level], 2); break;
            }
        }
    }
    return true;
}

// Network output (count elements) -> float.
inline bool UnpackOutput(const void* src, size_t count, DType type, Quant q, float* dst)
{
    if (!src || !dst) return false;
    const auto* in = static_cast<const uint8_t*>(src);
    for (size_t i = 0; i < count; ++i) {
        switch (type) {
            case DType::F32: std::memcpy(&dst[i], in + i * 4, 4); break;
            case DType::F16: { __fp16 h; std::memcpy(&h, in + i * 2, 2); dst[i] = static_cast<float>(h); break; }
            case DType::U8: dst[i] = q.scale * (static_cast<float>(in[i]) + static_cast<float>(q.offset)); break;
            case DType::U16: { uint16_t v; std::memcpy(&v, in + i * 2, 2); dst[i] = q.scale * (static_cast<float>(v) + static_cast<float>(q.offset)); break; }
        }
    }
    return true;
}

}  // namespace wd
