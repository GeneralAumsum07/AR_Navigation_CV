#include <cmath>
#include <cstdlib>
#include "walldepth.h"

// Host-only C API fixture: proves selftest accepts correct output and refuses NaN output.
// Never link this into the real plugin or Android application.
extern "C" {
int wd_init(const char*, const char*, char*, int) { return 0; }
int wd_input_size() { return 12; }
int wd_output_size() { return 2; }
int wd_submit(const uint8_t*, int) { return 1; }
int wd_poll(float* out, int, double* ms)
{
    const char* invalid = std::getenv("WD_SELFTEST_NAN");
    out[0] = invalid && invalid[0] == '1' ? NAN : 1.f;
    out[1] = 2.f;
    *ms = 1.;
    return 1;
}
int wd_describe(char* out, int n) { if (n > 0) out[0] = 0; return 0; }
int wd_last_error(char* out, int n) { if (n > 0) out[0] = 0; return 0; }
void wd_shutdown() { }
}
