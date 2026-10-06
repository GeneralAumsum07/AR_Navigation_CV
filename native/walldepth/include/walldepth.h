/* C API of libwalldepth.so. One model, one request in flight. Not thread-safe against
 * wd_shutdown: the caller (QnnDepthInference) stops calling before it shuts down. */
#pragma once
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define WD_API __attribute__((visibility("default")))

/* Load the QNN context binary on the HTP. native_lib_dir is the app's extracted native
 * library directory; the DSP finds libQnnHtpV75Skel.so there via ADSP_LIBRARY_PATH (D3).
 * Returns 0 on success, -1 with a message in err. */
WD_API int wd_init(const char* context_path, const char* native_lib_dir, char* err, int err_len);
/* Bytes wd_submit expects: size * size * 3 (RGB, row 0 = top). 0 before init. */
WD_API int wd_input_size(void);
/* Floats wd_poll writes: size * size. 0 before init. */
WD_API int wd_output_size(void);
/* Copy the frame and start an inference. 1 = accepted, 0 = busy, -1 = not ready or failed. */
WD_API int wd_submit(const uint8_t* rgb, int len);
/* 1 = result copied into out (inference_ms = graph execute time), 0 = nothing ready, -1 = failed. */
WD_API int wd_poll(float* out, int len, double* inference_ms);
/* Human-readable tensor/encoding summary for logs. Returns the string length. */
WD_API int wd_describe(char* buf, int len);
/* Last error message. Returns its length. */
WD_API int wd_last_error(char* buf, int len);
/* Stop the worker and free everything. Safe to call twice. */
WD_API void wd_shutdown(void);

#ifdef __cplusplus
}
#endif
