# Phase 1 — runtime spike (OnePlus 13R, QAIRT 2.51.0.260929181845)

- Updated: 2026-10-07
- Physical tests: explicitly deferred by Rachit; implementation may proceed without them.
- SDK: Rachit's completed archive was extracted to `C:/qairt/2.51.0.260929`. `sdk.yaml` and the actual generator identify build **2.51.0.260929181845**. Runtime pin updated after successful native compilation and context generation; model producer provenance remains 2.50.0.260828221209.
- Archive: 2,971,497,940 bytes, 12,460 members; member CRCs validated while extracting. Recorded SHA-256: `9b4ca798902f02a2543857f5de4d233830c044d19701b92fc66a12fdc292211f`.
- Licence: bundled `LICENSE.pdf`, page 1, §1(iv): “distribute and sublicense the Software solely in object code format and as incorporated in Your software application”. The SDK does not grant standalone distribution. Runtime files are staged in generated, git-ignored paths; the incorporated APK includes the SDK's licence and notice files unchanged.
- Context generation: both variants generated with the real Windows tool, exit code 0. SDK utility parsed both contexts and confirmed build 2.51.0.260929181845, SoC 57 and DSP architecture 75. The SDK's bundled QNN overview explicitly maps SM8650 to 57/v75.
- Host tools run without further downloads for this DLC/context workflow. SDK paths are set per invocation; persistent environment variables remain unchanged.

## Model tensors

The 2.51 context-binary utility confirms these names, shapes, data types and quantisation against the downloaded v0.63.0 model metadata. Those DLCs were produced with 2.50.0.260828221209; they have now been read and prepared by 2.51.

| | name | dims | dtype | quantisation (scale, zero point) |
|---|---|---|---|---|
| w8a16 graph | graph_f9_dd7sq | — | — | — |
| w8a16 input | image | [1, 518, 518, 3] (NHWC) | uint16 | 0.00002101432801282499, 9979 |
| w8a16 output | depth_estimates | [1, 518, 518, 1] | uint16 | 0.0002007981966016814, 0 |
| float graph | graph_zvnz3_ib | — | — | — |
| float input | image | [1, 518, 518, 3] (NHWC) | float32 | — |
| float output | depth_estimates | [1, 518, 518, 1] | float32 | — |

The input's real value range is [0, 1]. QNN scale-offset encoding uses the negative of the archive's zero point; the plugin reads the context's own quantisation data rather than hardcoding these values.

## Latency (qnn-net-run, burst, HTP v75)

| variant | inferences | p50 or average (say which) | max |
|---|---|---|---|
| w8a16 | not run | TBD — physical test deferred | TBD |
| float | not run | TBD — physical test deferred | TBD |

## Output parameterisation (analyse.py, frames with a tracked floor)

| variant | frames | median residual: inverse depth | median residual: depth | winner |
|---|---|---|---|---|
| w8a16 | not captured | TBD | TBD | unmeasured |
| float | not captured | TBD | TBD | unmeasured |

## Decisions

- Gate p50 ≤ 35 ms: **DEFERRED, not PASS**. Rachit's subsequent instruction permits implementation without physical tests.
- Parameterisation: **provisional AffineInverseDepth**, the supplied plan's default. Both modes remain supported; run `analyse.py` on real tracked-floor dumps before treating this choice as verified.
- Variant to ship: **provisional w8a16**, the supplied plan's default. Both models were fetched and all four archive/DLC SHA-256 hashes recorded and verified. The proposed 1.5× residual comparison remains unmeasured.
- Native compilation and host context generation: **PASS for 2.51**. Both contexts inspected successfully; w8a16 is staged as the provisional default. Full APK packaging is checked separately below. These checks do not prove NPU execution on the phone.

## Offline verification

- Python 3.13.11 and NumPy 2.4.6 are available.
- All three Python scripts compile and both analysis/input CLIs expose their documented options.
- The context-generator contract test invokes a fake SDK generator and confirms its arguments without changing persistent environment variables. The native offline tests cover converter math and self-test rejection of NaN output. These stubs do not produce deployable models or verify the pinned SDK headers.
- New SDK contract controls: wrong SDK identity was accepted before the fix and is now rejected. Missing host DLL search paths failed before the fix and now pass. Both generator and native build scripts enforce the runtime pin from the SDK's own identity.
- Unity suite: **190/190**, including two manifest dependency controls and a real UnityWebRequest native-handle disposal regression. Those three regressions failed under the old code before their fixes.
- Physical frame orientation, image/pose timestamp matching, latency and model-output parameterisation remain unverified.

## Packaging

- `libwalldepth.so`, `wd_convert_test` and `wd_selftest` compile/link against real 2.51 headers with Unity's ARM64 NDK. The initial Windows short-path link lost Clang's C++ driver selection and failed on `operator new/delete`; explicit `--driver-mode=g++` resolves it. All eight public `wd_*` entry points are exported.
- Plugin and SDK ARM64 libraries have 16 KB LOAD alignment. The skeleton is a Hexagon executable, not ARM64 code. It retains the SDK's own bytes/alignment and is packaged for disk extraction beside the ARM64 libraries.
- APK postprocessing declares `libcdsprpc.so` as an optional vendor native dependency, preserves QNN/native bytes through Gradle, and enables extraction. First APK inspection caught both the missing declaration and changed plugin/skeleton hashes; the corrected APK passes those controls. [Android native-library access](https://developer.android.com/guide/topics/manifest/uses-native-library-element), [Gradle JNI packaging](https://developer.android.com/reference/tools/gradle-api/8.13/com/android/build/api/dsl/JniLibsPackaging).
- Device DSP skeleton loading and self-test versus qnn-net-run remain **DEFERRED**.
- Final QNN Development APK: `Builds/Android/WallDistanceDemo_QNN.apk`, 111,022,864 bytes; build succeeded with zero errors in 109,879 ms. `check-apk.py` passes all 17 payload/manifest controls with `WALLDEPTH_ALLOW_MISSING` unset. Unity's 16 KB warning for the 4 KB Hexagon skeleton is retained; the ARM64 libraries themselves are 16 KB-aligned. The SDK's DSP binary has not been relinked or rewritten.
- Final APK SHA-256: `55bc315ef672cc5c20311cdb724a99d81f10f3f6ee14b09b7c3f0b5d0ab2b93f`.
- Task 17 conversion checks: missing `tensor_convert.h` produced the expected RED compiler failure. The completed conversion unit test passes on Windows with MinGW GCC (`__fp16` mapped to its IEEE binary16 `_Float16` storage type for this host-only run) and cross-compiles with Unity's Android ARM64/API-25 Clang. This verifies conversion math and target compilation, not QNN execution.
- Earlier missing-SDK guard refusal was recorded on 2026-10-06. It is now resolved by the real SDK build; the pinned-header compilation blocker is closed.

## Context artifacts and generation diagnostics

| variant | bytes | context SHA-256 |
|---|---:|---|
| w8a16 | 29,503,488 | da1bead05c562f00d9651e0fecf697050415bed1352a44987936685ead877250 |
| float | 52,191,232 | 19fadaefc71048e2f9e9ff5da196b6187953c4c8ccd424fd6a87b3782360db87 |

The generator returned success but emitted error-level diagnostics: missing `libcdsprpc.dll`, `Unsupported HTP Arch 1 75`, and `unexpected GraphHtpSettings option 66`. They are preserved in the local verification logs. Both resulting binaries parse correctly and explicitly target 57/v75; the significance of those diagnostics for phone execution/performance remains TBD until the deferred device checks. They are not treated as measured NPU success.

## Fallback decision (spec §4)

- First verified day QNN packaging blocked progress: 2026-10-06 — pinned SDK absent; anonymous SDK download returned HTTP 403. TBD — was packaging blocked on an earlier working day?
- SDK packaging blocker resolved: 2026-10-07 with Rachit's completed 2.51 download. An earlier first blocked working day remains TBD; the three-day trigger was not established.
- Remaining: physical DSP loading, native inference/self-test and performance/accuracy gates, explicitly deferred by Rachit.
- Unblocking actions taken: Rachit downloaded the SDK and authorised the version revision; archive extracted, licence read, native targets compiled, both contexts generated/inspected, and APK packaging controls implemented.
- Decision (Rachit): stay on QNN and use QAIRT 2.51, verifying compatibility — 2026-10-06. No LiteRT backend switch has been made.

Task 23 is a decision gate, with no implementation to run. Rachit's physical-test waiver permits the remaining code tasks; it does not establish NPU performance or authorise a backend replacement.

## SDK version revision authorised by Rachit

Rachit selected **“Use 2.51 and verify compatibility”** on 2026-10-06 and reported the download complete on 2026-10-07. `tools/models.lock.json` now pins runtime build 2.51.0.260929181845 and separately records `modelSourceQairt` 2.50.0.260828221209. The original four model archive/DLC hashes remain unchanged and were reverified.

Host compilation, DLC preparation, context metadata and APK packaging establish compatibility for those stages. Device runtime compatibility/performance is still unverified under the physical-test waiver.

```powershell
$env:QNN_SDK_ROOT = 'C:/qairt/2.51.0.260929'
powershell -ExecutionPolicy Bypass -File tools/phase1/make-context.ps1 -Variant depth_anything_v2-qnn_dlc-w8a16 -OutName depth_anything_v2_w8a16
powershell -ExecutionPolicy Bypass -File native/walldepth/build-android.ps1
Copy-Item tools/.cache/ctx/depth_anything_v2_w8a16.ctx.bin Assets/StreamingAssets/Models/
python tools/phase1/check-apk.py Builds/Android/WallDistanceDemo_QNN.apk
```
