# Fast wall detection implementation status — 2026-10-07

The device-independent implementation steps in Tasks 0–22 are complete on `main`, with local commits after each task. QAIRT 2.51 native compilation, context generation and APK packaging are now verified. Rachit explicitly deferred physical tests; device gates remain unmeasured. Nothing has been pushed.

## Completed code and checks

- Tasks 1–20: floor alignment, depth capture, inference interfaces, wall extraction/refinement, map fusion/anchors, side readings, QNN source integration, HUD and CSV.
- One final review pass fixed eight Important findings: session/age rejection, asynchronous native ownership, anchor correction order, opening preservation, edge-snap metric limits, independent depth cross-checks, complete CPU timing, and the context generator. Non-finite native self-test output now fails validation.
- Task 21: synchronous frame preparation timing (`prep_ms`) and the bench summariser. The changed schema test failed before implementation; all 187 EditMode tests then passed. The actual recorder's UTF-8 BOM exposed a parser failure; an added regression failed before the shared reader was fixed. All ten Python bench tests now pass, including plain/gzip BOM input, and the actual Editor recording parses correctly.
- Task 22: field analysis, references sidecar template and field protocol. The tests failed before implementation; all ten field tests and the ten bench tests pass.
- Final Editor lifecycle smoke: service disable/re-enable succeeds, ML-unavailable status is explicit, no new console errors. Recording produced 16,402 rows with 93 matching columns, `prep_ms` in the schema, and unavailable preparation/width values blank. This is an Editor integration check, not physical evidence.
- Offline native converter/self-test controls and the PowerShell context-generator contract test pass. Their fake runtime/generator fixtures establish offline behavior only.
- The completed SDK archive was extracted to `C:/qairt/2.51.0.260929`; its actual build is 2.51.0.260929181845. All native targets compile/link against its real headers. Both model variants produce readable contexts with the expected tensor metadata, SoC 57 and HTP v75. The w8a16 context and runtime libraries are staged from that same SDK.
- SDK integration controls reproduced and fixed Windows C++ linking, SDK version enforcement, host DLL lookup, Android vendor RPC access, native-byte preservation and model-copy request disposal. The latest Unity suite passes **190/190**; the SDK contract test and native offline controls pass. Earlier Python analysis results remain **20/20**.
- Final APK verification passes **17/17**: SDK identity, exact hashes of all five native libraries and the model context, extraction flags, optional `libcdsprpc.so` access and unchanged SDK licence/notice payloads. `tools/phase1/check-apk.py` checks the actual artifact.

## Deferred gates and decisions

- Physical Tasks 0, 7, 17, 19, 20, 21 and 22 are deferred under Rachit's instruction. No accuracy, acquisition-time, thermal or NPU rate results have been fabricated. No bench/field measurement report has been generated from synthetic data.
- Rachit's authorised QAIRT 2.51 revision is implemented and pinned. The missing SDK, licence-reading, header-compilation, context-generation and APK-packaging blockers are closed. Physical NPU loading/execution, native self-test and rate/accuracy gates remain deferred; see `phase1-runtime-spike.md` for preserved generator diagnostics and the exact evidence.
- The default w8a16 variant and inverse-depth parameterisation remain provisional until the deferred device measurements compare them.
- Task 23's fallback facts and Rachit's decision to stay on QNN using 2.51 are recorded in `phase1-runtime-spike.md`. Only one blocked working day is established; an earlier start date is TBD. No LiteRT backend switch has been made.
- Threshold tuning after the field report is outside this plan.

## Measurement limits

`detect_latency_ms` includes image-arrival-to-map CPU processing. Sensor timestamp delay is not measured, so this is not a verified sensor-to-display latency. `prep_ms` separately records synchronous preparation cost. The supplied bench summariser collapses repeated values into runs; consecutive inferences with exactly equal timings cannot be distinguished without an inference sequence column. Its counts are therefore proxies rather than exact inference counts.

The field acquisition clock is inferred from phone orientation. Error sample counts represent correlated CSV rows, as the report states, rather than independent trials. Neither offline tool establishes physical accuracy.

The review's Minor suggestion to log inference status transitions after CSV recording begins is deferred. The CSV header records the status at recording start; operators must also check the HUD and `walldepth: ready` log before an NPU benchmark.

## Android build and workspace

The final Development IL2CPP/ARM64 ARCore-only validation APK built successfully with zero build errors (478,810 ms). It is `Builds/Android/WallDistanceDemo_ARCoreOnly.apk`, 101,651,392 bytes. The ordinary QNN packaging guard previously rejected missing native/context files as intended; the ARCore-only APK uses an explicit temporary build opt-out and does not establish ML operation.

The subsequent full QNN Development IL2CPP/ARM64 APK is **`Builds/Android/WallDistanceDemo_QNN.apk`**, 111,022,864 bytes. Final build `build_7c5ecdd9087f` succeeded in 109,879 ms with zero errors and the missing-dependency opt-out unset. Its native libraries and model context pass exact payload verification. This establishes packaging, not on-device NPU operation.

Unity warns that `libQnnHtpV75Skel.so` is not 16 KB-aligned. Inspection identifies it as a Hexagon DSP executable with the SDK's 4 KB alignment, rather than an ARM64 CPU library. The ARM64 plugin/SDK libraries have 16 KB LOAD alignment; the skeleton is preserved unchanged and extracted to disk. Actual DSP loading remains deferred. Other build warnings concern the optional Pipeline player configuration and diagnostic symbol detail.

The build opt-out was restored to unset, Play Mode stopped and `runInBackground` restored to false. Automatic build additions to PlayerSettings preloaded assets were removed through the live Unity API, preserving the original asset. Automatic package-lock changes were restored. Both `/.superpowers/` and `/docs/superpowers/` are in `.gitignore`; already tracked plans/specs remain tracked.

## Prepared commands

The SDK is ready. Reproduction commands and compatibility evidence are in `docs/phase1-runtime-spike.md`. Validate the deferred device gates before using physical benchmarks as performance/accuracy evidence.

```powershell
python -m unittest discover -s tools/bench -v
python -m unittest discover -s tools/field -v
python tools/phase1/check-apk.py Builds/Android/WallDistanceDemo_QNN.apk
python tools/bench/summarize_bench.py <device-log.csv> --markdown <bench-report.md>
python tools/field/analyse_field.py Builds/logs/WallDistanceLogs <references.csv> --markdown <field-report.md>
```

The repeatable field steps are in `docs/benchmarks/field-protocol.md`. Example references in `tools/field/references_template.csv` must be replaced with actual tape measurements.
