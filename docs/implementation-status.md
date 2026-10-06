# Fast wall detection implementation status — 2026-10-06

The device-independent implementation steps in Tasks 0–22 are complete on `main`, with local commits after each task. Rachit explicitly deferred physical tests; device gates remain unmeasured. SDK-dependent native compilation and context generation remain blocked. Nothing has been pushed.

## Completed code and checks

- Tasks 1–20: floor alignment, depth capture, inference interfaces, wall extraction/refinement, map fusion/anchors, side readings, QNN source integration, HUD and CSV.
- One final review pass fixed eight Important findings: session/age rejection, asynchronous native ownership, anchor correction order, opening preservation, edge-snap metric limits, independent depth cross-checks, complete CPU timing, and the context generator. Non-finite native self-test output now fails validation.
- Task 21: synchronous frame preparation timing (`prep_ms`) and the bench summariser. The changed schema test failed before implementation; all 187 EditMode tests then passed. The actual recorder's UTF-8 BOM exposed a parser failure; an added regression failed before the shared reader was fixed. All ten Python bench tests now pass, including plain/gzip BOM input, and the actual Editor recording parses correctly.
- Task 22: field analysis, references sidecar template and field protocol. The tests failed before implementation; all ten field tests and the ten bench tests pass.
- Final Editor lifecycle smoke: service disable/re-enable succeeds, ML-unavailable status is explicit, no new console errors. Recording produced 16,402 rows with 93 matching columns, `prep_ms` in the schema, and unavailable preparation/width values blank. This is an Editor integration check, not physical evidence.
- Offline native converter/self-test controls and the PowerShell context-generator contract test pass. Their fake runtime/generator fixtures establish offline behavior only.

## Deferred gates and decisions

- Physical Tasks 0, 7, 17, 19, 20, 21 and 22 are deferred under Rachit's instruction. No accuracy, acquisition-time, thermal or NPU rate results have been fabricated. No bench/field measurement report has been generated from synthetic data.
- The original pinned QAIRT SDK is absent. Rachit authorised QAIRT 2.51 with compatibility verification; its archive is still downloading in `C:/Users/Rachit/Downloads`. Context generation, redistribution licence review, actual SDK-header compilation and full QNN packaging are unresolved; see `phase1-runtime-spike.md`.
- The default w8a16 variant and inverse-depth parameterisation remain provisional until the deferred device measurements compare them.
- Task 23's fallback facts and Rachit's decision to stay on QNN using 2.51 are recorded in `phase1-runtime-spike.md`. Only one blocked working day is established; an earlier start date is TBD. No LiteRT backend switch has been made.
- Threshold tuning after the field report is outside this plan.

## Measurement limits

`detect_latency_ms` includes image-arrival-to-map CPU processing. Sensor timestamp delay is not measured, so this is not a verified sensor-to-display latency. `prep_ms` separately records synchronous preparation cost. The supplied bench summariser collapses repeated values into runs; consecutive inferences with exactly equal timings cannot be distinguished without an inference sequence column. Its counts are therefore proxies rather than exact inference counts.

The field acquisition clock is inferred from phone orientation. Error sample counts represent correlated CSV rows, as the report states, rather than independent trials. Neither offline tool establishes physical accuracy.

The review's Minor suggestion to log inference status transitions after CSV recording begins is deferred. The CSV header records the status at recording start; operators must also check the HUD and `walldepth: ready` log before an NPU benchmark.

## Android build and workspace

The final Development IL2CPP/ARM64 ARCore-only validation APK built successfully with zero build errors (478,810 ms). It is `Builds/Android/WallDistanceDemo_ARCoreOnly.apk`, 101,651,392 bytes. The ordinary QNN packaging guard previously rejected missing native/context files as intended; the ARCore-only APK uses an explicit temporary build opt-out and does not establish ML operation.

The build opt-out was restored to unset, Play Mode stopped and `runInBackground` restored to false. Automatic build additions to PlayerSettings preloaded assets were removed through the live Unity API, preserving the original asset. Automatic package-lock changes were restored. Both `/.superpowers/` and `/docs/superpowers/` are in `.gitignore`; already tracked plans/specs remain tracked.

## Prepared commands

After the authorised 2.51 download completes, follow `docs/phase1-runtime-spike.md` for SDK compatibility verification and the context/native build scripts, then validate the deferred device gates before benchmarking.

```powershell
python -m unittest discover -s tools/bench -v
python -m unittest discover -s tools/field -v
python tools/bench/summarize_bench.py <device-log.csv> --markdown <bench-report.md>
python tools/field/analyse_field.py Builds/logs/WallDistanceLogs <references.csv> --markdown <field-report.md>
```

The repeatable field steps are in `docs/benchmarks/field-protocol.md`. Example references in `tools/field/references_template.csv` must be replaced with actual tape measurements.
