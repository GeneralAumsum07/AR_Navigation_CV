# Phase 1 — runtime spike (OnePlus 13R, QAIRT 2.50.0.260828221209)

- Date: 2026-10-06
- Physical tests: explicitly deferred by Rachit; implementation may proceed without them.
- Licence clause permitting redistribution of libQnn* (file, section, quote): TBD — inspect the pinned SDK's licence before staging its libraries. No Qualcomm runtime libraries have been copied or redistributed.
- SDK: not installed; `QNN_SDK_ROOT` is unset and `C:/qairt/2.50.0.260828221209` is absent. An anonymous request to the official software-center SDK download endpoint returned HTTP 403. Obtain the pinned SDK through Qualcomm Software Center.
- Context-binary generation: pending SDK installation. `make-context.ps1` follows the plan's Windows-host flags; verify them against the installed generator's `--help`. SoC model 57 remains the plan's inference, to confirm in SDK documentation.

## Model tensors

Evidence below comes from `metadata.json` in the downloaded v0.63.0 archive, not from a DLC inspection tool. The archive records the exact pinned QAIRT build.

| | name | dims | dtype | quantisation (scale, zero point) |
|---|---|---|---|---|
| graph | TBD — inspect DLC/context with SDK | TBD | TBD | TBD |
| w8a16 input | image | [1, 518, 518, 3] (NHWC) | uint16 | 0.00002101432801282499, 9979 |
| w8a16 output | depth_estimates | [1, 518, 518, 1] | uint16 | 0.0002007981966016814, 0 |

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
- Context binary and NPU packaging: pending the pinned SDK, licence review and context generation. The normal Android build guard must report missing dependencies; an explicitly allowed ARCore-only build does not prove NPU operation.

## Offline verification

- Python 3.13.11 and NumPy 2.4.6 are available.
- All three Python scripts compile and both analysis/input CLIs expose their documented options.
- Physical frame orientation, image/pose timestamp matching, latency and model-output parameterisation remain unverified.

## Packaging

- QNN header compatibility, native library LOAD alignment, DSP skeleton extraction and self-test against qnn-net-run: TBD — build with the pinned SDK and then run the deferred device checks.
