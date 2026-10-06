// Unit test for tensor_convert.h. Runs on the phone (aarch64, for __fp16); exit code = failures.
#include <cmath>
#include <cstdio>
#include <vector>

#include "tensor_convert.h"

static int g_failures = 0;
#define CHECK(cond) do { if (!(cond)) { std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); ++g_failures; } } while (0)
static bool Near(float a, float b, float tol = 1e-6f) { return std::fabs(a - b) <= tol; }

int main()
{
    using namespace wd;
    // 2x2 image, pixels (R,G,B): p0 = (255,0,128), p1 = (0,255,0), p2 = (0,0,255), p3 = (51,102,153).
    const uint8_t rgb[12] = {255, 0, 128, 0, 255, 0, 0, 0, 255, 51, 102, 153};

    {   // float32 NHWC: values are /255 in pixel-interleaved order.
        std::vector<float> f(12);
        CHECK(PackInput(rgb, 2, Layout::NHWC, DType::F32, Quant{}, f.data(), f.size() * 4));
        CHECK(Near(f[0], 1.f) && Near(f[1], 0.f) && Near(f[2], 128.f / 255.f));
        CHECK(Near(f[11], 153.f / 255.f));
    }
    {   // float32 NCHW: all R first, then G, then B.
        std::vector<float> f(12);
        CHECK(PackInput(rgb, 2, Layout::NCHW, DType::F32, Quant{}, f.data(), f.size() * 4));
        CHECK(Near(f[0], 1.f) && Near(f[1], 0.f) && Near(f[2], 0.f) && Near(f[3], 51.f / 255.f));   // R plane
        CHECK(Near(f[4], 0.f) && Near(f[5], 1.f) && Near(f[7], 102.f / 255.f));                    // G plane
        CHECK(Near(f[10], 1.f) && Near(f[11], 153.f / 255.f));                                     // B plane
    }
    {   // uint8 with scale 1/255, offset 0 reproduces the byte.
        std::vector<uint8_t> q(12);
        CHECK(PackInput(rgb, 2, Layout::NHWC, DType::U8, Quant{1.f / 255.f, 0}, q.data(), q.size()));
        for (int i = 0; i < 12; ++i) CHECK(q[i] == rgb[i]);
    }
    {   // uint16 with a negative offset: real = scale * (q + offset)  =>  q = real/scale - offset.
        std::vector<uint16_t> q(12);
        CHECK(PackInput(rgb, 2, Layout::NHWC, DType::U16, Quant{0.001f, -100}, q.data(), q.size() * 2));
        CHECK(q[0] == 1100);           // 1.0 / 0.001 + 100
        CHECK(q[1] == 100);            // 0.0
        CHECK(q[2] == 602);            // 0.50196 / 0.001 + 100 = 601.96 -> 602
    }
    {   // Quantisation clamps instead of wrapping.
        std::vector<uint8_t> q(12);
        CHECK(PackInput(rgb, 2, Layout::NHWC, DType::U8, Quant{0.001f, 0}, q.data(), q.size()));
        CHECK(q[0] == 255);
    }
    {   // float16 round trip.
        std::vector<uint16_t> h(12);
        CHECK(PackInput(rgb, 2, Layout::NHWC, DType::F16, Quant{}, h.data(), h.size() * 2));
        float back[12];
        CHECK(UnpackOutput(h.data(), 12, DType::F16, Quant{}, back));
        CHECK(Near(back[0], 1.f, 1e-3f) && Near(back[2], 128.f / 255.f, 1e-3f));
    }
    {   // Dequantise uint8 and uint16.
        const uint8_t q8[2] = {15, 10};
        float out[2];
        CHECK(UnpackOutput(q8, 2, DType::U8, Quant{0.1f, -10}, out));
        CHECK(Near(out[0], 0.5f, 1e-6f) && Near(out[1], 0.f));
        const uint16_t q16[1] = {600};
        CHECK(UnpackOutput(q16, 1, DType::U16, Quant{0.001f, -100}, out));
        CHECK(Near(out[0], 0.5f, 1e-6f));
    }
    {   // Wrong destination size or bad scale is refused, not written past the end.
        std::vector<float> f(11);
        CHECK(!PackInput(rgb, 2, Layout::NHWC, DType::F32, Quant{}, f.data(), f.size() * 4));
        std::vector<uint8_t> q(12);
        CHECK(!PackInput(rgb, 2, Layout::NHWC, DType::U8, Quant{0.f, 0}, q.data(), q.size()));
    }
    std::printf(g_failures == 0 ? "convert_test: all passed\n" : "convert_test: %d failure(s)\n", g_failures);
    return g_failures;
}
