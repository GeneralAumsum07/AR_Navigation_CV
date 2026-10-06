// On-device check of the whole plugin: same frame as Phase 1, compare with qnn-net-run's output.
//   wd_selftest <context.bin> <lib_dir> <input.rgb> <expected.raw> [runs]
// Passes when max |plugin - qnn-net-run| <= 0.1% of the expected output's range. A layout,
// scale or offset bug produces errors of the order of the whole range, so this tolerance
// separates "right" from "wrong" while allowing rounding differences in input quantisation.
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <fstream>
#include <thread>
#include <vector>

#include "walldepth.h"

static bool ReadAll(const char* path, std::vector<char>& out)
{
    std::ifstream f(path, std::ios::binary | std::ios::ate);
    if (!f) return false;
    out.resize(static_cast<size_t>(f.tellg()));
    f.seekg(0);
    return static_cast<bool>(f.read(out.data(), static_cast<std::streamsize>(out.size())));
}

int main(int argc, char** argv)
{
    if (argc < 5) { std::fprintf(stderr, "usage: wd_selftest <ctx> <libdir> <input.rgb> <expected.raw> [runs]\n"); return 2; }
    int runs = argc > 5 ? std::atoi(argv[5]) : 50;
    char msg[512];
    if (wd_init(argv[1], argv[2], msg, sizeof msg) != 0) { std::fprintf(stderr, "init failed: %s\n", msg); return 2; }
    wd_describe(msg, sizeof msg);
    std::printf("%s\n", msg);

    std::vector<char> rgb, expectedBytes;
    if (!ReadAll(argv[3], rgb) || static_cast<int>(rgb.size()) != wd_input_size()) { std::fprintf(stderr, "bad input file\n"); return 2; }
    if (!ReadAll(argv[4], expectedBytes) || static_cast<int>(expectedBytes.size()) != wd_output_size() * 4) { std::fprintf(stderr, "bad expected file\n"); return 2; }
    const float* expected = reinterpret_cast<const float*>(expectedBytes.data());

    std::vector<float> out(static_cast<size_t>(wd_output_size()));
    std::vector<double> times;
    float maxDiff = 0.f, lo = INFINITY, hi = -INFINITY;
    for (int r = 0; r < runs; ++r) {
        if (wd_submit(reinterpret_cast<const uint8_t*>(rgb.data()), static_cast<int>(rgb.size())) != 1) { std::fprintf(stderr, "submit refused\n"); return 1; }
        double ms = 0;
        int rc;
        while ((rc = wd_poll(out.data(), static_cast<int>(out.size()), &ms)) == 0)
            std::this_thread::sleep_for(std::chrono::microseconds(200));
        if (rc < 0) { wd_last_error(msg, sizeof msg); std::fprintf(stderr, "inference failed: %s\n", msg); return 1; }
        times.push_back(ms);
        if (r == 0) {
            for (size_t i = 0; i < out.size(); ++i) {
                if (!std::isfinite(expected[i])) continue;
                lo = std::min(lo, expected[i]);
                hi = std::max(hi, expected[i]);
                maxDiff = std::max(maxDiff, std::fabs(out[i] - expected[i]));
            }
        }
    }
    std::sort(times.begin(), times.end());
    double p50 = times[times.size() / 2], p95 = times[std::min(times.size() - 1, times.size() * 95 / 100)];
    float tol = 1e-3f * (hi - lo);
    std::printf("runs=%d p50=%.2f ms p95=%.2f ms  max|diff|=%g  range=%g  tol=%g\n", runs, p50, p95, maxDiff, hi - lo, tol);
    wd_shutdown();
    bool pass = hi > lo && maxDiff <= tol;
    std::printf(pass ? "selftest: PASS\n" : "selftest: FAIL\n");
    return pass ? 0 : 1;
}
