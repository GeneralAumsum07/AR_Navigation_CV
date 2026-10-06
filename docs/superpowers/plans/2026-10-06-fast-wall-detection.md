# Fast Wall Detection for Campus Corridors — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Detect corridor walls in well under a second, with as little error as possible. The method is floor-aligned monocular depth on the Snapdragon NPU, fused into a world-anchored wall map. From that map, publish aimed, nearest, left, right and corridor-width readings at frame rate.

**Architecture:**
- **Detection, ≤15 Hz.** Depth Anything V2 Small runs on the HTP NPU through a native QNN plugin. Its relative depth is scaled to metres by fitting it to the tracked ARCore floor. Vertical planes are found by RANSAC, and their base lines are snapped to image edges.
- **Map.** A `WallMap` fuses the detected walls with ARCore planes and keeps them anchored in the world.
- **Measurement, every frame.** The existing `WallMeasurementEngine` produces aimed/nearest from the map. The new `CorridorSideSelector` produces left/right/width.

**Tech Stack:**
- Unity 6000.3.5f1, AR Foundation / ARCore 6.6.2, C# (Core and AR assemblies), NUnit EditMode tests.
- Qualcomm AI Engine Direct (QAIRT 2.50.0.260828221209) on HTP v75 (SM8650); Android NDK r27c (Unity-bundled), CMake 3.22.1 + Ninja.
- Python 3.13 for offline analysis.

**Spec:** `docs/superpowers/specs/2026-10-06-fast-wall-detection-design.md`. Read it alongside this plan. Section numbers below (§n) refer to the spec.

## Execution status (2026-10-06)

- Tasks 0–20 were executed and committed as `2eebadf` through `b429e5b` (21 local commits, not pushed). Uncommitted review fixes sit on top of `b429e5b` in the working tree; they are not part of this plan.
- Physical device checks for Phase 0/1 (Tasks 0, 7, 17 Steps 7–9, 19 Step 5, 20 Step 9) are recorded as **deferred** in `docs/phase0-plane-triage.md` and `docs/phase1-runtime-spike.md`. The QAIRT SDK is not installed yet.
- Remaining: Tasks 21–23. Task 21's code steps can run now; its bench step, Task 22's field session and Task 23's decision depend on the SDK and the deferred device checks.

## Global Constraints

- Device of record: OnePlus 13R (CPH2691, Snapdragon 8 Gen 3 / SM8650, no ToF sensor). Android ARM64, IL2CPP, min SDK 25.
- Work on `main` only. Commit after every task. **Never push** unless Rachit asks in that turn.
- **No self-attribution in git:**
  - no `Co-Authored-By: Claude…` trailer;
  - no "Generated with Claude Code";
  - no tool credits.
  - Commit messages use ordinary engineering voice.
- Comment code heavily; comments explain *why*, not what.
- Existing integration surface stays backward compatible (§1, §5.5):
  - `WallDistanceService.Latest` / `Updated` keep `aimed` and `nearest`.
  - New snapshot fields are additive.
  - Enum values are **appended** only, so existing integers never change.
- Readings are NaN when invalid, never 0 (§5.4).
- Distances are metres. Poses are in the AR session frame, which is gravity-aligned (+Y up).
- Model: Depth Anything V2 Small, Qualcomm AI Hub release v0.63.0, QNN DLC, **w8a16** (22.3 ms on SM8650 per AI Hub), 518×518 input. Licence on the AI Hub card: MIT.
- Model weights are never committed (`*.dlc`, `*.bin`, `*.tflite`, `*.onnx` are git-ignored). They are fetched by `tools/fetch-models.ps1` and pinned in `tools/models.lock.json`.
- QNN runtime libraries must come from the same QAIRT build as the context binary (§5.3).
- Thresholds (§5.5, §6) — each is a config field:

  | Rule | Value |
  |---|---|
  | Side wall: no heading | within 20° of vertical |
  | Side wall: enter / exit angle | ≤ 30° to enter, > 40° to leave |
  | Side wall: extent margin | 1.0 m |
  | Side wall: freshness | 10 s |
  | Side wall: max distance | ≤ 6 m |
  | Corridor width | only when walls are parallel within 10° |
  | Floor alignment | ≥ 500 inliers and ≤ 3 % residual |
  | Raw-depth fallback | confidence ≥ 0.5, ≥ 200 samples |
  | Map association | ≤ 8°, ≤ 0.15 m |
  | Edge snap | ±12 px band, ≥ 60 % samples snapped |
  | Thermal step-down | > 60 ms for 5 s → 5 Hz |
  | Stale detection | after 1 s |
  | `CrossChecked` agreement | within 5 cm |
  | Nominal map-wall height | 2.4 m |

- No assumed camera height, ever (§6).
- CSV revision string is `floor-aligned-v1` (§7).
- EditMode tests need a running Unity Editor on this project. The test command is used verbatim in every task:

  ```
  unity command run_tests --mode editor --filter WallDistance --filter_type assembly --timeout 240 --no-banner
  ```

  It runs the whole `WallDistance.Tests.EditMode` assembly. Per-class filtering has not been verified on this machine, so every run covers every test, including the pre-existing ones.
- Android tool paths (Unity-bundled; `adb` is **not** on PATH):
  - `$AP = C:\Program Files\Unity\Hub\Editor\6000.3.5f1\Editor\Data\PlaybackEngines\AndroidPlayer`
  - adb: `$AP\SDK\platform-tools\adb.exe`
  - NDK: `$AP\NDK`
  - CMake / Ninja: `$AP\SDK\cmake\3.22.1\bin\`
- APK build command (from `Assets/WallDistance/README.md`):

  ```
  unity command build --target Android --outputPath Builds/Android/WallDistanceDemo.apk --options '["Development"]' --confirm true
  ```

## Review Focus

These are the five conditions the spec implies but no spec-listed test exercises, most likely to bite first. Each has a test in its owning task.

1. **Tiled or glossy floor with a grout line running parallel to a wall, 20 cm from its base.**
   - Expected: the base-edge snap stays on the true wall base, not the grout line.
   - Test: Task 11, `GroutLineParallelToWall_DoesNotCaptureSnap`.
2. **A person standing still in front of part of a side wall.**
   - Expected: that wall is still found at its true offset. The person may add a short spurious "wall" (a known limitation, §6), but must not replace or shift the real one.
   - Test: Task 10, `PersonInFrontOfRightWall_WallStillFound`.
3. **Tracking lost, then regained within the same session.**
   - Expected: while lost, side readings say `TrackingLost`. On recovery they come straight back from the retained map, with no blending against pre-loss values.
   - Test: Task 15, `TrackingLossThenRecovery_SidesResumeFromMapWithoutBlend`.
4. **App start with no floor plane and no confident raw depth.**
   - Expected: readings report `NoFloor` (the HUD prompts "point at the floor"). No wall is produced from an assumed camera height.
   - Test: Task 13, `NoFloorAndNoRawDepth_ReportsNoFloorAndAddsNoWalls`.
5. **Phone held level, close (~0.4 m) to a wall, so the wall base is below the image.**
   - Expected: the edge refiner declines and leaves the learned wall untouched, so it still yields a `LearnedEstimate`.
   - Test: Task 11, `BaseOutOfView_RefinerDeclinesAndLeavesObservationUnchanged`.

## Deviations from the spec (flagged for Rachit)

| # | Spec says | Plan does | Why |
|---|---|---|---|
| D1 | `libwalldepth.so` "loads the DLC" (§5.3) | Loads a **QNN context binary** generated on the PC from the pinned DLC (Task 7) | A context binary is QNN's deployment format for HTP. It skips on-device graph preparation, so init is faster and there are fewer moving parts. The DLC stays the pinned source artefact |
| D2 | Scheduler uses `XRCpuImage.ConvertAsync` (§5.3) | Synchronous `Convert` inside `frameReceived` | The pose must belong to the exact image. Async conversion completes on a later frame, when the pose has moved on. Cost is measured in Task 21 |
| D3 | C API `wd_init(modelPath, out err)` | `wd_init(contextPath, nativeLibDir, err, errLen)` | The HTP skeleton library is found through `ADSP_LIBRARY_PATH`, which must name the app's native-library directory. Only Java can supply that path |
| D4 | Side readings "inherit" source/quality (§5.5) | They inherit, **and** get `OutOfTestedRange` outside 0.5–3 m, like aimed/nearest | Otherwise the UI would show a 0.3 m side wall as "Edge confirmed" while aimed shows the same distance as out of range |
| D5 | Spec gives no recovery rule from 5 Hz | Recover to 15 Hz after 30 s of inferences ≤ 60 ms | Proposal; Rachit to confirm |
| D6 | Separate "bench scene" (§8) | The existing `WallMeasurement` scene with CSV logging | Same code path as field use; no scene to maintain |
| D7 | Phase 1 latency "through `libwalldepth.so`" (§9) | Phase 1 gates on Qualcomm's own `qnn-net-run`. `libwalldepth.so` latency is re-measured in Task 21 | Separates "is the model fast enough" from "is our plugin right" |
| D8 | Edge search band "±12 px" (§5.2) | ±12 px, **narrowed** to the image length of ±8 cm on the floor | Without the cap, a tile joint 10–20 cm from the wall falls inside 12 px at range and captures the "lowest edge" rule (Review Focus 1) |
| D9 | §5.4 lists `InferenceUnavailable` as a `FailureReason` | Reported once on the HUD and in the CSV header. Not stamped onto every reading | Non-Snapdragon phones would otherwise permanently show "ML unavailable" instead of the ARCore path's own reasons (§6: "`InferenceUnavailable` once") |
| D10 | Raw-depth disagreement on a learned wall | Noted in `qualityReason`, never demotes the label | The spec defines only the agreement case (`CrossChecked`). Raw depth is exactly what fails on plain walls (§2) |
| D11 | (§5.2 implies one wall-geometry convention) | `AssistedWallGeometry` is not refactored into a shared helper; `WallMap.Candidates` builds candidates with the same `LookRotation(up, normal)` convention | Keeps the assisted path untouched; the convention is pinned by the WallMap tests |
| D12 | ARCore planes and learned walls both feed the map (§5.2) | Once the map has any track, automatic mode measures against **map** candidates only; ARCore's own candidates are used only while the map is empty (Task 19) | One source of truth for aimed/nearest/left/right. Trade-off: map walls are rectangles of nominal 2.4 m height, not ARCore polygons |
| D13 | Field protocol references "taken from the camera position" (§8) | Tape references go in a sidecar `references.csv` per session, keyed by recording file name; no HUD text entry (Task 22) | The HUD has no text field (README TODO); paper sheet then typing up avoids entry errors on the phone. Proposal; Rachit to confirm |
| D14 | Spec gives no tolerance for "readings at 30 Hz" (§10) | Bench check passes at median `updRate` ≥ 28 Hz (Task 21) | A 30.0 Hz median is not achievable on a 30 fps camera with jitter. Proposal; Rachit to confirm |

## File structure

New Core files go in `Assets/WallDistance/Runtime/Core/` (pure C#, no AR Foundation):

| File | Responsibility |
|---|---|
| `DetectionConfig.cs` | Every detection threshold; nested in `MeasurementConfig`, so it is logged in the CSV header |
| `FloorPlane.cs` | Metric floor: height, projection, ray intersection, camera-height plausibility |
| `Horizontal.cs` | Project a vector onto the floor plane |
| `InverseDepthImage.cs` | Network output plus luma, intrinsics, pose and content rectangle; projection helpers |
| `InferenceGeometry.cs` | Sensor ↔ 518² inference-image mapping (quarter-turn rotation and letterbox); intrinsics and pose for the rotated camera |
| `InferenceRateGovernor.cs` | 15 Hz ↔ 5 Hz thermal step-down |
| `AlignmentResult.cs` | Output of floor alignment, plus `MetricSample` |
| `FloorAlignedDepth.cs` | Robust affine fit of network output to floor (or raw-depth) metric depth |
| `MetricSampleCollector.cs` | Confident raw-depth pixels → inference-image metric samples |
| `WallObservation.cs` | One detected vertical wall segment (base line, normal, extent, quality stats) |
| `VerticalPlaneExtractor.cs` | RANSAC vertical planes as 2D lines on the floor projection |
| `BaseEdgeRefiner.cs` | Snaps an observation's base line to the lowest strong image edge |
| `WallTrack.cs` | One fused, world-anchored wall in the map |
| `WallMap.cs` | Association, information-filter fusion, extents, ageing, cross-check, anchor deltas, candidates |
| `WallDetectionPipeline.cs` | Per-frame orchestration: align → extract → refine → map; failure reasons |
| `CorridorSideSelector.cs` | Left/right/width from the map (§5.5) |
| `ReadingText.cs` | User-facing text for qualities, failures and the `L | R | W` line |

New AR files go in `Assets/WallDistance/Runtime/AR/`:

| File | Responsibility |
|---|---|
| `FloorPlaneSource.cs` | Picks the floor `ARPlane` and exposes a `FloorPlane` |
| `IDepthInference.cs` | Backend interface, `InferenceRequest`, `NullDepthInference` |
| `DepthInferenceScheduler.cs` | CPU image → upright, letterboxed 518² RGB plus metadata; one request in flight; rate governor; frame dumps |
| `QnnDepthInference.cs` | P/Invoke wrapper over `libwalldepth.so` |
| `WallMapAnchoring.cs` | One `ARAnchor` per wall track; feeds anchor corrections back to the map |

Native and tools:

| Path | Responsibility |
|---|---|
| `native/walldepth/` | `CMakeLists.txt`, `include/walldepth.h`, `src/walldepth.cpp`, `build-android.ps1` |
| `Assets/WallDistance/Editor/QnnAndroidBuild.cs` | Plugin import settings plus Gradle patch that keeps native libraries extracted |
| `tools/models.lock.json`, `tools/fetch-models.ps1` | Pinned model download and verification |
| `tools/phase1/` | `dump_to_png.py`, `make_inputs.py`, `analyse.py`, `make-context.ps1` |
| `tools/bench/summarize_bench.py` (+ test) | 10-minute bench summary |
| `tools/field/analyse_field.py` (+ test) | Field benchmark error tables |
| `docs/phase0-plane-triage.md`, `docs/phase1-runtime-spike.md`, `docs/benchmarks/` | Phase records |

Modified files:

| File | Change |
|---|---|
| `Runtime/Core/WallReading.cs` | Enum values, `sourceChain`, snapshot sides, config fields |
| `Runtime/Core/WallCandidate.cs` | `crossChecked` |
| `Runtime/Core/WallMeasurementEngine.cs` | Learned-source quality, detection-failure reasons, side readings |
| `Runtime/AR/AssistedWallController.cs` | Shared camera-height bounds |
| `Runtime/AR/WallDistanceService.cs` | Wiring |
| `Runtime/AR/WallDistanceHud.cs` | `L | R | W` line, new texts, dump button |
| `Runtime/AR/MeasurementCsvRecorder.cs` | `floor-aligned-v1` columns |
| `.gitignore` | Native library outputs, tool cache |

Tests go in `Assets/WallDistance/Tests/EditMode/`: one `*Tests.cs` per Core unit, plus the `SyntheticCorridor.cs` fixture.

Unity writes a `.meta` file next to every new asset on import. Always `git add` the folder, so the `.meta` files are committed with their assets.

**Task order follows the spec's phases:**

| Phase | Tasks |
|---|---|
| Phase 0 | Task 0 |
| Primitives the Phase 1 dump needs | 1–5 |
| Phase 1 spike (gate) | 6–7 |
| Phase 2 Core algorithms | 8–16 |
| Phase 3 AR integration | 17–21 |
| Phase 4 | 22 |
| Conditional fallback | 23 |

---

### Task 0: Phase 0 — plane triage (manual, on device; GATE)

The field CSVs show 0 floor planes in 33/34 sessions (§2). This task decides whether the floor plane, which the whole design leans on, is available in Rachit's corridors.

**Files:**
- Create: `docs/phase0-plane-triage.md`

**Interfaces:**
- Consumes: nothing.
- Produces: a recorded decision that later tasks rely on: `floor-primary` (continue as planned) or `floor-less` (stop; re-review the spec with Rachit).

- [ ] **Step 1: Check what our app requests from ARCore**

  `Runtime/AR/ARWallCandidateSource.cs:34` sets `requestedDetectionMode = Horizontal | Vertical` at runtime, but the scene file serialises `m_DetectionMode: 2` (vertical only) on the `ARPlaneManager`.

  Run:

  ```bash
  grep -n "m_DetectionMode" Assets/Scenes/WallMeasurement.unity
  grep -n "requestedDetectionMode" Assets/WallDistance/Runtime/AR/*.cs
  ```

  Expected:
  - `m_DetectionMode: 2` in the scene.
  - The runtime override in `ARWallCandidateSource.cs`.

  Record both lines in the triage doc. If the runtime line is absent, our app only ever asked for vertical planes. That alone would explain zero floor planes.

- [ ] **Step 2: Build and install the stock AR Foundation "Plane Detection" sample**

  ```bash
  git clone https://github.com/Unity-Technologies/arfoundation-samples "$HOME/arfoundation-samples"
  git -C "$HOME/arfoundation-samples" branch -r
  ```

  1. Check out the branch whose `README.md` names AR Foundation 6.x (compare with `Packages/manifest.json` in this repo, which pins 6.6.2).
  2. Open that project in Unity 6000.3.5f1.
  3. Set the build scene list to the Plane Detection scene only.
  4. Build for Android, then install:

     ```bash
     "/c/Program Files/Unity/Hub/Editor/6000.3.5f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe" install -r <path-to-built-apk>
     ```

- [ ] **Step 3: Run the same corridor test with both apps**

  In the corridor used for the 2026-09-13/14 logs:
  - Hold the phone at chest height.
  - Sweep slowly across the floor for 30 s, three trials per app.
  - For our app, start CSV recording first. The `planesTotal` column counts all planes.

  Record for each trial:
  - seconds until the first horizontal plane appears;
  - floor type (painted, tiled, glossy);
  - lighting.

- [ ] **Step 4: Write the decision in `docs/phase0-plane-triage.md`**

  Use exactly this structure:

  ```markdown
  # Phase 0 — plane triage (OnePlus 13R)

  - Date: YYYY-MM-DD
  - Corridor / floor / lighting: …
  - Our app detection mode (scene / runtime): …

  | App | Trial | Floor plane found? | Seconds to first floor plane |
  |---|---|---|---|

  ## Decision
  - [ ] Stock app finds the floor, ours does not → fix our app first (record the fix), then `floor-primary`.
  - [ ] Both find the floor → `floor-primary`.
  - [ ] Stock app also finds nothing → `floor-less`: STOP. Re-review the spec with Rachit (§9).
  ```

- [ ] **Step 5: Commit**

  ```bash
  git add docs/phase0-plane-triage.md
  git commit -m "Record Phase 0 plane triage on the OnePlus 13R"
  ```

**Gate:**
- Continue only on `floor-primary`.
- On `floor-less`, stop executing this plan and tell Rachit.

---

### Task 1: Core API additions (enums, snapshot sides, config)

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/DetectionConfig.cs`
- Modify: `Assets/WallDistance/Runtime/Core/WallReading.cs`
- Modify: `Assets/WallDistance/Runtime/Core/WallCandidate.cs`
- Test: `Assets/WallDistance/Tests/EditMode/ApiCompatibilityTests.cs`

**Interfaces:**
- Produces (used by every later task):
  - `MeasurementKind.CorridorLeft = 2`, `CorridorRight = 3`
  - `MeasurementSource.LearnedDepth = 5`, `FloorEdge = 6`
  - `QualityLabel.LearnedEstimate = 7`, `EdgeConfirmed = 8`, `CrossChecked = 9`
  - `FailureReason.NoFloor = 6`, `AlignmentFailed = 7`, `InferenceUnavailable = 8`, `InferenceStale = 9`, `NoHeading = 10`, `NoWallOnSide = 11`
  - `WallReading.sourceChain` (string)
  - `WallDistanceSnapshot.left`, `.right` (`WallReading`), `.corridorWidthMeters` (float)
  - `WallCandidate.crossChecked` (bool)
  - `MeasurementConfig`:
    - side fields: `sideNoHeadingDeg`, `sideEnterAngleDeg`, `sideExitAngleDeg`, `sideExtentMarginMeters`, `sideMaxAgeSeconds`, `sideMaxDistanceMeters`, `corridorParallelToleranceDeg`
    - `crossCheckToleranceMeters`
    - `DetectionConfig detection`
  - `enum DepthParameterisation { AffineInverseDepth, AffineDepth }`
  - `DetectionConfig` with the fields shown in Step 3.

- [ ] **Step 1: Write the failing test**

  Create `Assets/WallDistance/Tests/EditMode/ApiCompatibilityTests.cs`:

  ```csharp
  using NUnit.Framework;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      /// <summary>
      /// Pins the integer value of every enum member. Scenes, CSV analysis and any consumer that
      /// stored an enum as an int depend on these never shifting, so new values are appended only.
      /// </summary>
      public class ApiCompatibilityTests
      {
          [Test]
          public void ExistingEnumValues_KeepTheirIntegers_NewOnesAreAppended()
          {
              Assert.AreEqual(0, (int)MeasurementKind.Aimed);
              Assert.AreEqual(1, (int)MeasurementKind.NearestObserved);
              Assert.AreEqual(2, (int)MeasurementKind.CorridorLeft);
              Assert.AreEqual(3, (int)MeasurementKind.CorridorRight);

              Assert.AreEqual(4, (int)MeasurementSource.AssistedFloor);
              Assert.AreEqual(5, (int)MeasurementSource.LearnedDepth);
              Assert.AreEqual(6, (int)MeasurementSource.FloorEdge);

              Assert.AreEqual(6, (int)QualityLabel.AssistedEstimate);
              Assert.AreEqual(7, (int)QualityLabel.LearnedEstimate);
              Assert.AreEqual(8, (int)QualityLabel.EdgeConfirmed);
              Assert.AreEqual(9, (int)QualityLabel.CrossChecked);

              Assert.AreEqual(5, (int)FailureReason.CandidateLost);
              Assert.AreEqual(6, (int)FailureReason.NoFloor);
              Assert.AreEqual(7, (int)FailureReason.AlignmentFailed);
              Assert.AreEqual(8, (int)FailureReason.InferenceUnavailable);
              Assert.AreEqual(9, (int)FailureReason.InferenceStale);
              Assert.AreEqual(10, (int)FailureReason.NoHeading);
              Assert.AreEqual(11, (int)FailureReason.NoWallOnSide);
          }

          [Test]
          public void Invalid_IsNaNNotZero_AndHasEmptySourceChain()
          {
              var r = WallReading.Invalid(MeasurementKind.CorridorLeft, FailureReason.NoWallOnSide, "s", 1.0, default);
              Assert.IsFalse(r.isValid);
              Assert.IsTrue(float.IsNaN(r.distanceMeters));
              Assert.IsTrue(float.IsNaN(r.rawDistanceMeters));
              Assert.AreEqual(MeasurementKind.CorridorLeft, r.kind);
              Assert.AreEqual("", r.sourceChain);
              Assert.AreEqual("NoWallOnSide", r.qualityReason);
          }

          [Test]
          public void SideDefaults_MatchSpecSection55()
          {
              var c = new MeasurementConfig();
              Assert.AreEqual(20f, c.sideNoHeadingDeg);
              Assert.AreEqual(30f, c.sideEnterAngleDeg);
              Assert.AreEqual(40f, c.sideExitAngleDeg);
              Assert.AreEqual(1.0f, c.sideExtentMarginMeters);
              Assert.AreEqual(10f, c.sideMaxAgeSeconds);
              Assert.AreEqual(6f, c.sideMaxDistanceMeters);
              Assert.AreEqual(10f, c.corridorParallelToleranceDeg);
              Assert.AreEqual(0.05f, c.crossCheckToleranceMeters);
          }

          [Test]
          public void DetectionDefaults_MatchSpecSections52And6()
          {
              var d = new MeasurementConfig().detection;
              Assert.IsNotNull(d);
              Assert.AreEqual(DepthParameterisation.AffineInverseDepth, d.parameterisation);
              Assert.AreEqual(500, d.minFloorInliers);
              Assert.AreEqual(0.03f, d.maxAlignResidual);
              Assert.AreEqual(200, d.minMetricSamples);
              Assert.AreEqual((byte)128, d.metricMinConfidence);
              Assert.AreEqual(4, d.maxPlanes);
              Assert.AreEqual(12, d.edgeBandPixels);
              Assert.AreEqual(0.6f, d.edgeMinSnapFraction);
              Assert.AreEqual(8f, d.associateMaxAngleDeg);
              Assert.AreEqual(0.15f, d.associateMaxOffsetMeters);
              Assert.AreEqual(2.4f, d.nominalWallHeightMeters);
              Assert.AreEqual(1f, d.staleAfterSeconds);
          }

          [Test]
          public void ConfigJson_IncludesDetectionBlock_SoCsvHeadersLogIt()
          {
              string json = JsonUtility.ToJson(new MeasurementConfig());
              StringAssert.Contains("\"detection\"", json);
              StringAssert.Contains("\"minFloorInliers\"", json);
              StringAssert.Contains("\"sideEnterAngleDeg\"", json);
          }

          [Test]
          public void Candidate_CrossCheckedDefaultsFalse()
          {
              Assert.IsFalse(new WallCandidate().crossChecked);
          }
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  Run: `unity command run_tests --mode editor --filter WallDistance --filter_type assembly --timeout 240 --no-banner`

  Expected: compilation fails, with errors such as `'MeasurementKind' does not contain a definition for 'CorridorLeft'` and `'DepthParameterisation' could not be found`.

- [ ] **Step 3: Create `DetectionConfig.cs`**

  ```csharp
  using System;
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// How the network output relates to metric depth. Depth Anything V2 is trained as relative
      /// inverse depth, but the quantised export may not preserve that; Phase 1 measures both on
      /// real frames and fixes this once (spec §6) - it is never switched at runtime.
      /// </summary>
      public enum DepthParameterisation
      {
          /// <summary>1/z = s·d + t.</summary>
          AffineInverseDepth,
          /// <summary>z = s·d + t.</summary>
          AffineDepth,
      }

      /// <summary>
      /// Every threshold of the learned-depth wall detector. Nested inside MeasurementConfig so the
      /// CSV header (JsonUtility dump) records the exact values a benchmark ran with. Defaults are
      /// the spec's starting points, not validated constants.
      /// </summary>
      [Serializable]
      public class DetectionConfig
      {
          [Header("Floor alignment (spec §5.2, §6)")]
          public DepthParameterisation parameterisation = DepthParameterisation.AffineInverseDepth;
          [Tooltip("Pixel stride when sampling floor pixels for the scale fit.")]
          public int alignStride = 4;
          public float floorMinDepthMeters = 0.3f;
          public float floorMaxDepthMeters = 10f;
          public int alignRansacIterations = 64;
          [Tooltip("A floor pixel is an inlier when its aligned depth is within this fraction of the floor-plane depth. " +
                   "Deliberately wider than maxAlignResidual so the median residual check below is meaningful.")]
          public float alignInlierRelative = 0.06f;
          [Tooltip("Fewer floor inliers than this discards the frame (spec §6).")]
          public int minFloorInliers = 500;
          [Tooltip("Median relative residual of the inliers above this discards the frame (spec §6: 3%).")]
          public float maxAlignResidual = 0.03f;
          [Tooltip("No-floor fallback: confident raw-depth samples needed (spec §6).")]
          public int minMetricSamples = 200;
          [Tooltip("Raw-depth confidence byte for the fallback; 128/255 is the spec's 0.5.")]
          public byte metricMinConfidence = 128;

          [Header("Vertical planes")]
          public int extractStride = 4;
          public float wallMinDepthMeters = 0.3f;
          public float wallMaxDepthMeters = 8f;
          [Tooltip("Points lower than this above the floor are floor clutter, not wall.")]
          public float floorClearanceMeters = 0.05f;
          public int maxPlanes = 4;
          public int planeRansacIterations = 128;
          public float planeInlierMeters = 0.08f;
          public int minPlaneInliers = 150;
          public float minHeightSpanMeters = 0.3f;
          public float minLengthMeters = 0.4f;
          [Tooltip("RANSAC pairs closer than this give unstable line directions.")]
          public float minPairSeparationMeters = 0.2f;

          [Header("Base edge")]
          public int edgeBandPixels = 12;
          [Tooltip("Search band is also capped to the image length of this floor distance (plan deviation D8).")]
          public float edgeMaxShiftMeters = 0.08f;
          [Tooltip("Minimum luma step (0-255) across the base line.")]
          public float edgeMinContrast = 12f;
          public float edgeSampleSpacingMeters = 0.1f;
          public int edgeMaxSamples = 64;
          public float edgeMinSnapFraction = 0.6f;
          public float edgeMaxRmsMeters = 0.02f;
          public float edgeMaxAngleChangeDeg = 10f;

          [Header("Wall map")]
          public float associateMaxAngleDeg = 8f;
          public float associateMaxOffsetMeters = 0.15f;
          public float associateMaxGapMeters = 0.5f;
          public float trackMaxAgeSeconds = 30f;
          public float minOffsetSigmaMeters = 0.01f;
          public float minAngleSigmaDeg = 0.5f;
          [Tooltip("Track information is capped at 1/σ² of this, so the map keeps adapting to anchor corrections.")]
          public float maxOffsetInfoSigmaMeters = 0.005f;
          public float maxAngleInfoSigmaDeg = 0.25f;
          [Tooltip("UI extent of map walls; a display height, not a measurement (spec §5.2).")]
          public float nominalWallHeightMeters = 2.4f;
          [Tooltip("A source counts as current for a track if it observed it within this window.")]
          public float sourceWindowSeconds = 2f;
          public float arPlaneObserveIntervalSeconds = 0.1f;

          [Header("Staleness")]
          public float staleAfterSeconds = 1f;
      }
  }
  ```

- [ ] **Step 4: Edit `WallReading.cs`**

  **4a. `MeasurementKind`.** After `NearestObserved,` add:

  ```csharp
          /// <summary>Horizontal perpendicular distance to the corridor wall left of the phone's heading (spec §5.5).</summary>
          CorridorLeft,
          /// <summary>Horizontal perpendicular distance to the corridor wall right of the phone's heading.</summary>
          CorridorRight,
  ```

  **4b. `MeasurementSource`.** Replace its summary `/// <summary>Where the number came from. Higher entries are more trustworthy.</summary>` with:

  ```csharp
      /// <summary>
      /// Where the number came from. Values are appended to keep stored integers stable, so the
      /// order is NOT a trust ranking - each value documents its own trust.
      /// </summary>
  ```

  After `AssistedFloor,` add:

  ```csharp
          /// <summary>Wall plane from floor-aligned monocular network depth only.</summary>
          LearnedDepth,
          /// <summary>Learned wall whose base line was snapped to image edges on the tracked floor. Geometric; ranks above LearnedDepth.</summary>
          FloorEdge,
  ```

  **4c. `QualityLabel`.** After `AssistedEstimate,` add:

  ```csharp
          /// <summary>Learned-depth wall only; scale comes from the floor fit.</summary>
          LearnedEstimate,
          /// <summary>Wall base found as an image edge on the tracked floor.</summary>
          EdgeConfirmed,
          /// <summary>Two independent sources (learned vs ARCore plane or raw depth) agree within crossCheckToleranceMeters.</summary>
          CrossChecked,
  ```

  **4d. `FailureReason`.** After `CandidateLost,` add:

  ```csharp
          /// <summary>No floor plane and too little confident raw depth to fix the depth scale.</summary>
          NoFloor,
          /// <summary>Floor fit rejected: too few floor inliers or residual above 3%.</summary>
          AlignmentFailed,
          /// <summary>ML depth backend could not start on this device.</summary>
          InferenceUnavailable,
          /// <summary>No detection for over 1 s and the map has no wall for this reading.</summary>
          InferenceStale,
          /// <summary>Phone points within 20° of straight up/down, so left/right are undefined.</summary>
          NoHeading,
          /// <summary>No map wall qualifies on this side (spec §5.5).</summary>
          NoWallOnSide,
  ```

  **4e. `WallReading`.** After the `qualityReason` field add:

  ```csharp
          /// <summary>Sources behind the number, e.g. "FloorEdge+ARPlane" (CSV aimed_source_chain). Empty when invalid.</summary>
          public string sourceChain;
  ```

  In `Invalid(...)`, after `qualityReason = reason.ToString(),` add:

  ```csharp
                  sourceChain = "",
  ```

  **4f. `WallDistanceSnapshot`.** Replace the struct with:

  ```csharp
      /// <summary>All readings for one frame plus session context.</summary>
      [Serializable]
      public struct WallDistanceSnapshot
      {
          public WallReading aimed;
          public WallReading nearest;
          /// <summary>Corridor wall to the left of the phone's floor heading (from the wall map).</summary>
          public WallReading left;
          /// <summary>Corridor wall to the right of the phone's floor heading (from the wall map).</summary>
          public WallReading right;
          /// <summary>left + right when both are valid and parallel within 10°; NaN otherwise. Always set by the engine.</summary>
          public float corridorWidthMeters;
          public bool sessionTracking;
          public bool depthSupported;
          public double timestamp;
      }
  ```

  **4g. `MeasurementConfig`.** After `minCandidateAreaSquareMeters` add:

  ```csharp

          [Header("Corridor sides (spec §5.5)")]
          [Tooltip("Within this many degrees of straight up/down there is no heading.")]
          public float sideNoHeadingDeg = 20f;
          [Tooltip("A wall becomes a side wall when its floor line is within this angle of the heading.")]
          public float sideEnterAngleDeg = 30f;
          [Tooltip("A current side wall stops being one above this angle (hysteresis).")]
          public float sideExitAngleDeg = 40f;
          public float sideExtentMarginMeters = 1.0f;
          public float sideMaxAgeSeconds = 10f;
          public float sideMaxDistanceMeters = 6f;
          public float corridorParallelToleranceDeg = 10f;

          [Header("Learned detection")]
          [Tooltip("Two sources agreeing within this distance earn CrossChecked.")]
          public float crossCheckToleranceMeters = 0.05f;
          public DetectionConfig detection = new DetectionConfig();
  ```

- [ ] **Step 5: Edit `WallCandidate.cs`**

  After the `source` field add:

  ```csharp

          /// <summary>
          /// Map walls only: a learned observation and an independent ARCore plane recently agreed
          /// within MeasurementConfig.crossCheckToleranceMeters. Always false for raw AR planes.
          /// </summary>
          public bool crossChecked;
  ```

- [ ] **Step 6: Run the tests to verify they pass**

  Run: `unity command run_tests --mode editor --filter WallDistance --filter_type assembly --timeout 240 --no-banner`

  Expected: PASS, both the 6 new tests and every pre-existing test.

- [ ] **Step 7: Commit**

  ```bash
  git add Assets/WallDistance/Runtime/Core Assets/WallDistance/Tests/EditMode
  git commit -m "Add corridor-side and learned-detection API: enums, snapshot sides, detection config"
  ```

---

### Task 2: `FloorPlane` and `Horizontal`

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/FloorPlane.cs`
- Create: `Assets/WallDistance/Runtime/Core/Horizontal.cs`
- Modify: `Assets/WallDistance/Runtime/AR/AssistedWallController.cs` (the `height < 0.4f || height > 2.5f` line, ~line 131)
- Test: `Assets/WallDistance/Tests/EditMode/FloorPlaneTests.cs`

**Interfaces:**
- Produces:
  - `readonly struct FloorPlane(Vector3 point, Vector3 up)`
    - fields `point`, `up` (unit; zero when invalid)
    - `bool IsValid`
    - `float HeightAbove(Vector3)`
    - `Vector3 Project(Vector3)`
    - `bool TryIntersect(Vector3 origin, Vector3 dir, out Vector3 hit, out float t)`
    - `bool PlausibleCameraHeight(Vector3 camera)`
    - `const float MinCameraHeight = 0.4f, MaxCameraHeight = 2.5f`
  - `static class Horizontal`
    - `bool TryDirection(Vector3 v, Vector3 up, out Vector3 dir, float minLength = 1e-3f)`

- [ ] **Step 1: Write the failing test**

  ```csharp
  using NUnit.Framework;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class FloorPlaneTests
      {
          static readonly FloorPlane Floor = new FloorPlane(new Vector3(0f, 0.1f, 0f), Vector3.up);

          [Test]
          public void HeightAbove_IsSignedDistanceAlongUp()
          {
              Assert.AreEqual(1.4f, Floor.HeightAbove(new Vector3(1f, 1.5f, 2f)), 1e-6f);
              Assert.AreEqual(-0.1f, Floor.HeightAbove(new Vector3(0f, 0f, 0f)), 1e-6f);
          }

          [Test]
          public void Project_DropsPointOntoFloor()
          {
              Vector3 p = Floor.Project(new Vector3(1f, 2f, 3f));
              Assert.AreEqual(new Vector3(1f, 0.1f, 3f), p);
          }

          [Test]
          public void TryIntersect_DescendingRay_HitsAtZDepth()
          {
              // dir has unit z, so t equals the Z-depth: the convention every depth image here uses.
              var f = new FloorPlane(Vector3.zero, Vector3.up);
              Assert.IsTrue(f.TryIntersect(new Vector3(0f, 1.4f, 0f), new Vector3(0f, -1f, 1f), out Vector3 hit, out float t));
              Assert.AreEqual(1.4f, t, 1e-5f);
              Assert.AreEqual(0f, hit.y, 1e-5f);
              Assert.AreEqual(1.4f, hit.z, 1e-5f);
          }

          [Test]
          public void TryIntersect_RejectsRisingParallelAndBelowFloorRays()
          {
              var f = new FloorPlane(Vector3.zero, Vector3.up);
              Assert.IsFalse(f.TryIntersect(new Vector3(0f, 1.4f, 0f), new Vector3(0f, 1f, 1f), out _, out _), "rising");
              Assert.IsFalse(f.TryIntersect(new Vector3(0f, 1.4f, 0f), new Vector3(0f, 0f, 1f), out _, out _), "parallel");
              Assert.IsFalse(f.TryIntersect(new Vector3(0f, -0.5f, 0f), new Vector3(0f, -1f, 1f), out _, out _), "origin below floor");
          }

          [Test]
          public void Default_IsInvalid_AndUpIsNormalised()
          {
              Assert.IsFalse(default(FloorPlane).IsValid);
              var f = new FloorPlane(Vector3.zero, new Vector3(0f, 2f, 0f));
              Assert.IsTrue(f.IsValid);
              Assert.AreEqual(1f, f.up.magnitude, 1e-6f);
          }

          [Test]
          public void PlausibleCameraHeight_MatchesAssistedModeBounds()
          {
              var f = new FloorPlane(Vector3.zero, Vector3.up);
              Assert.IsFalse(f.PlausibleCameraHeight(new Vector3(0f, 0.3f, 0f)));
              Assert.IsTrue(f.PlausibleCameraHeight(new Vector3(0f, 1.4f, 0f)));
              Assert.IsFalse(f.PlausibleCameraHeight(new Vector3(0f, 2.6f, 0f)));
          }

          [Test]
          public void Horizontal_TryDirection_FlattensAndRejectsVertical()
          {
              Assert.IsTrue(Horizontal.TryDirection(new Vector3(1f, 1f, 0f), Vector3.up, out Vector3 d));
              Assert.AreEqual(Vector3.right, d);
              Assert.IsFalse(Horizontal.TryDirection(Vector3.up * 3f, Vector3.up, out _));
          }
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  Run the test command. Expected: compilation error, `The type or namespace name 'FloorPlane' could not be found`.

- [ ] **Step 3: Implement `FloorPlane.cs`**

  ```csharp
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// The metric floor in session space. It is the only source of absolute scale for learned
      /// depth, so it is deliberately strict: an invalid plane is never replaced by an assumed
      /// camera height (spec §6).
      /// </summary>
      public readonly struct FloorPlane
      {
          /// <summary>Camera heights outside this band mean the "floor" is a table, a shelf or a mis-tracked plane.</summary>
          public const float MinCameraHeight = 0.4f, MaxCameraHeight = 2.5f;

          public readonly Vector3 point;
          /// <summary>Unit normal pointing away from the floor (gravity up). Zero for an invalid plane.</summary>
          public readonly Vector3 up;

          public FloorPlane(Vector3 point, Vector3 up)
          {
              this.point = point;
              this.up = up.sqrMagnitude > 1e-8f ? up.normalized : Vector3.zero;
          }

          /// <summary>default(FloorPlane) has a zero normal, so "no floor" needs no extra flag.</summary>
          public bool IsValid => up.sqrMagnitude > 0.5f;

          public float HeightAbove(Vector3 p) => Vector3.Dot(p - point, up);

          public Vector3 Project(Vector3 p) => p - up * HeightAbove(p);

          public bool PlausibleCameraHeight(Vector3 camera)
          {
              float h = HeightAbove(camera);
              return IsValid && h >= MinCameraHeight && h <= MaxCameraHeight;
          }

          /// <summary>
          /// Intersect a ray with the floor. Rays that do not descend (or barely descend, which
          /// would put the hit tens of metres away with huge error) are rejected.
          /// </summary>
          public bool TryIntersect(Vector3 origin, Vector3 dir, out Vector3 hit, out float t)
          {
              hit = default;
              t = 0f;
              if (!IsValid) return false;
              float denom = Vector3.Dot(dir, up);
              if (denom > -1e-4f) return false;
              t = -HeightAbove(origin) / denom;
              if (t <= 0f) return false;
              hit = origin + dir * t;
              return true;
          }
      }
  }
  ```

- [ ] **Step 4: Implement `Horizontal.cs`**

  ```csharp
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>Floor-plane projections shared by the extractor, the map and the side selector.</summary>
      public static class Horizontal
      {
          /// <summary>
          /// Project <paramref name="v"/> onto the plane perpendicular to <paramref name="up"/> and
          /// normalise. False when v is (nearly) parallel to up, where the direction is meaningless.
          /// </summary>
          public static bool TryDirection(Vector3 v, Vector3 up, out Vector3 dir, float minLength = 1e-3f)
          {
              Vector3 p = v - up * Vector3.Dot(v, up);
              float m = p.magnitude;
              if (m < minLength)
              {
                  dir = Vector3.zero;
                  return false;
              }
              dir = p / m;
              return true;
          }
      }
  }
  ```

- [ ] **Step 5: Use the shared bounds in `AssistedWallController.cs`**

  Replace:

  ```csharp
                      if (height < 0.4f || height > 2.5f || hit.distance > 4f) continue;
  ```

  with:

  ```csharp
                      // Same camera-height bounds as the learned floor, so both modes agree on what a floor is.
                      if (height < FloorPlane.MinCameraHeight || height > FloorPlane.MaxCameraHeight || hit.distance > 4f) continue;
  ```

  Confirm `using WallDistance.Core;` is already at the top of the file. Add it if missing.

- [ ] **Step 6: Run the tests to verify they pass**

  Run the test command. Expected: PASS (all tests).

- [ ] **Step 7: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Add FloorPlane and Horizontal helpers; share camera-height bounds with assisted mode"
  ```

---

### Task 3: `InverseDepthImage` and `InferenceGeometry`

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/InverseDepthImage.cs`
- Create: `Assets/WallDistance/Runtime/Core/InferenceGeometry.cs`
- Test: `Assets/WallDistance/Tests/EditMode/InferenceGeometryTests.cs`

**Interfaces:**
- Consumes: `DepthIntrinsics` (existing, `DepthFrame.cs`).
- Produces:
  - `sealed class InverseDepthImage(int width, int height)`
    - fields: `width`, `height`, `float[] values` (NaN-filled), `byte[] luma`, `DepthIntrinsics intrinsics`, `Pose cameraPose`, `double timestamp`, `RectInt content`, `double inferenceMilliseconds`
    - methods: `bool InContent(int u, int v)`, `Vector3 CameraRay(float u, float v)`, `Vector3 WorldRay(float u, float v)`, `Vector3 WorldPoint(float u, float v, float z)`, `bool TryProject(Vector3 world, out float u, out float v, out float z)`
  - `readonly struct InferenceGeometry`
    - `static Create(int sensorW, int sensorH, int quarterTurnsCW, int size = 518)`
    - fields: `sensorWidth`, `sensorHeight`, `quarterTurns`, `size`, `scale`, `offsetX`, `offsetY`, `RectInt content`
    - methods: `Vector2 SensorToInference(Vector2)`, `Vector2 InferenceToSensor(Vector2)`, `DepthIntrinsics Intrinsics(DepthIntrinsics sensorAtSensorResolution)`, `Pose CameraPose(Pose sensorPose)`
    - `static int ChooseQuarterTurns(Quaternion sensorRotation, Vector3 displayUp)`

- [ ] **Step 1: Write the failing test**

  ```csharp
  using NUnit.Framework;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class InferenceGeometryTests
      {
          static readonly DepthIntrinsics Sensor = new DepthIntrinsics
          {
              fx = 500f, fy = 505f, cx = 318.5f, cy = 241.2f, width = 640, height = 480,
          };
          static readonly Pose SensorPose = new Pose(new Vector3(0.3f, 1.4f, -0.2f), Quaternion.Euler(20f, 35f, 5f));

          /// <summary>
          /// The whole point of InferenceGeometry: mapping a sensor pixel into the 518² image must
          /// give the same pixel as projecting the world point through the rotated, letterboxed
          /// camera. If the rotation, intrinsics or pose disagree by one convention, this fails.
          /// </summary>
          [Test]
          public void SensorMapping_AgreesWithInferenceCamera_ForAllQuarterTurns(
              [Values(0, 1, 2, 3)] int k)
          {
              var g = InferenceGeometry.Create(640, 480, k);
              var intr = g.Intrinsics(Sensor);
              var camPose = g.CameraPose(SensorPose);
              foreach (float z in new[] { 1f, 3f })
              for (float x = -0.4f; x <= 0.4f; x += 0.2f)
              for (float y = -0.3f; y <= 0.3f; y += 0.15f)
              {
                  var camPoint = new Vector3(x * z, y * z, z);
                  Vector3 world = SensorPose.position + SensorPose.rotation * camPoint;
                  float su = Sensor.fx * camPoint.x / z + Sensor.cx;
                  float sv = Sensor.fy * -camPoint.y / z + Sensor.cy;
                  Vector2 mapped = g.SensorToInference(new Vector2(su, sv));

                  Vector3 c = Quaternion.Inverse(camPose.rotation) * (world - camPose.position);
                  float iu = intr.fx * c.x / c.z + intr.cx;
                  float iv = intr.fy * -c.y / c.z + intr.cy;
                  Assert.AreEqual(iu, mapped.x, 2e-3f, $"u, k={k}");
                  Assert.AreEqual(iv, mapped.y, 2e-3f, $"v, k={k}");
                  Assert.AreEqual(c.z, z, 1e-4f, "rotation about the optical axis keeps Z-depth");
              }
          }

          [Test]
          public void RoundTrip_SensorToInferenceToSensor([Values(0, 1, 2, 3)] int k)
          {
              var g = InferenceGeometry.Create(640, 480, k);
              var p = new Vector2(123.25f, 77.5f);
              Vector2 back = g.InferenceToSensor(g.SensorToInference(p));
              Assert.AreEqual(p.x, back.x, 1e-3f);
              Assert.AreEqual(p.y, back.y, 1e-3f);
          }

          [Test]
          public void PortraitLetterbox_ContentIsCentredAndFullHeight()
          {
              var g = InferenceGeometry.Create(640, 480, 1);
              Assert.AreEqual(518f / 640f, g.scale, 1e-6f);
              Assert.AreEqual(new RectInt(65, 0, 388, 518), g.content);
          }

          [Test]
          public void ChooseQuarterTurns_PicksTheTurnThatMakesTheImageUpright([Values(0, 1, 2, 3)] int expected)
          {
              // Display (upright) camera is the sensor camera turned by `expected` quarter turns.
              Quaternion display = Quaternion.Euler(10f, 30f, 0f);
              Quaternion sensor = display * Quaternion.Inverse(Quaternion.AngleAxis(90f * expected, Vector3.forward));
              Assert.AreEqual(expected, InferenceGeometry.ChooseQuarterTurns(sensor, display * Vector3.up));
          }

          [Test]
          public void InverseDepthImage_ProjectAndBackProject_RoundTrip()
          {
              var img = new InverseDepthImage(100, 80)
              {
                  intrinsics = new DepthIntrinsics { fx = 90f, fy = 90f, cx = 49.5f, cy = 39.5f, width = 100, height = 80 },
                  cameraPose = SensorPose,
                  content = new RectInt(10, 0, 80, 80),
              };
              Vector3 w = img.WorldPoint(30.25f, 20.75f, 2.5f);
              Assert.IsTrue(img.TryProject(w, out float u, out float v, out float z));
              Assert.AreEqual(30.25f, u, 1e-3f);
              Assert.AreEqual(20.75f, v, 1e-3f);
              Assert.AreEqual(2.5f, z, 1e-4f);
              Assert.IsTrue(float.IsNaN(img.values[0]), "values start as NaN, never 0");
              Assert.IsFalse(img.InContent(5, 5));
              Assert.IsTrue(img.InContent(10, 0));
              Assert.IsFalse(img.InContent(90, 0), "RectInt max is exclusive");
          }
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  Run the test command. Expected: compilation error, `'InferenceGeometry' could not be found`.

- [ ] **Step 3: Implement `InverseDepthImage.cs`**

  ```csharp
  using System;
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// One network output in the 518² inference image, with the camera that image belongs to.
      /// Same conventions as DepthFrame: pixel index = pixel-centre coordinate, v grows down,
      /// camera +X right / +Y up / +Z forward. <see cref="values"/> holds the raw network output d
      /// (relative inverse depth before alignment) and NaN in the letterbox padding.
      /// </summary>
      public sealed class InverseDepthImage
      {
          public readonly int width;
          public readonly int height;
          public readonly float[] values;
          /// <summary>Grey image at the same scale; the base-edge refiner snaps against it.</summary>
          public readonly byte[] luma;
          public DepthIntrinsics intrinsics;
          /// <summary>Pose of the (rotated, upright) inference camera when the image was captured.</summary>
          public Pose cameraPose;
          /// <summary>Seconds since app start when the camera image arrived.</summary>
          public double timestamp;
          /// <summary>Pixels that come from the camera; everything else is letterbox padding.</summary>
          public RectInt content;
          public double inferenceMilliseconds = double.NaN;

          public InverseDepthImage(int width, int height)
          {
              this.width = width;
              this.height = height;
              values = new float[width * height];
              luma = new byte[width * height];
              Array.Fill(values, float.NaN);
          }

          public bool InContent(int u, int v) => content.Contains(new Vector2Int(u, v));

          /// <summary>Camera-space ray with unit Z, so a distance along it IS the Z-depth.</summary>
          public Vector3 CameraRay(float u, float v) =>
              new Vector3((u - intrinsics.cx) / intrinsics.fx, -(v - intrinsics.cy) / intrinsics.fy, 1f);

          public Vector3 WorldRay(float u, float v) => cameraPose.rotation * CameraRay(u, v);

          public Vector3 WorldPoint(float u, float v, float z) => cameraPose.position + WorldRay(u, v) * z;

          public bool TryProject(Vector3 world, out float u, out float v, out float z)
          {
              Vector3 c = Quaternion.Inverse(cameraPose.rotation) * (world - cameraPose.position);
              z = c.z;
              u = v = 0f;
              if (z <= 1e-4f) return false;
              u = intrinsics.fx * c.x / z + intrinsics.cx;
              v = intrinsics.fy * -c.y / z + intrinsics.cy;
              return true;
          }
      }
  }
  ```

- [ ] **Step 4: Implement `InferenceGeometry.cs`**

  ```csharp
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// Maps the landscape camera sensor image into the square inference image: rotate by k
      /// quarter turns clockwise so the content is display-upright, then scale and letterbox to
      /// size². The network was trained on upright images, so feeding it a sideways corridor would
      /// cost accuracy. Every pixel, intrinsic and pose conversion lives here so the CPU resampler,
      /// the native input and the geometry can never disagree.
      ///
      /// Pixel convention: index = pixel-centre coordinate. Scaling is done on pixel EDGES
      /// ((p + 0.5)·scale − 0.5), which keeps both images' centres aligned.
      /// </summary>
      public readonly struct InferenceGeometry
      {
          public readonly int sensorWidth, sensorHeight, quarterTurns, size;
          public readonly float scale, offsetX, offsetY;
          public readonly RectInt content;

          InferenceGeometry(int w, int h, int k, int size)
          {
              sensorWidth = w;
              sensorHeight = h;
              quarterTurns = ((k % 4) + 4) % 4;
              this.size = size;
              int rw = quarterTurns % 2 == 0 ? w : h;
              int rh = quarterTurns % 2 == 0 ? h : w;
              scale = Mathf.Min((float)size / rw, (float)size / rh);
              float cw = rw * scale, ch = rh * scale;
              offsetX = (size - cw) * 0.5f;
              offsetY = (size - ch) * 0.5f;
              // Small epsilons absorb float error at exact fits (e.g. 518/640·640 = 518).
              int x0 = Mathf.CeilToInt(offsetX - 1e-3f), y0 = Mathf.CeilToInt(offsetY - 1e-3f);
              int x1 = Mathf.Min(size, Mathf.FloorToInt(offsetX + cw + 1e-3f));
              int y1 = Mathf.Min(size, Mathf.FloorToInt(offsetY + ch + 1e-3f));
              content = new RectInt(x0, y0, x1 - x0, y1 - y0);
          }

          public static InferenceGeometry Create(int sensorW, int sensorH, int quarterTurnsCW, int size = 518) =>
              new InferenceGeometry(sensorW, sensorH, quarterTurnsCW, size);

          public Vector2 SensorToInference(Vector2 s)
          {
              Vector2 r = Rotate(s);
              return new Vector2((r.x + 0.5f) * scale - 0.5f + offsetX, (r.y + 0.5f) * scale - 0.5f + offsetY);
          }

          public Vector2 InferenceToSensor(Vector2 p)
          {
              var r = new Vector2((p.x + 0.5f - offsetX) / scale - 0.5f, (p.y + 0.5f - offsetY) / scale - 0.5f);
              return Unrotate(r);
          }

          Vector2 Rotate(Vector2 s)
          {
              int W = sensorWidth, H = sensorHeight;
              switch (quarterTurns)
              {
                  case 1: return new Vector2(H - 1 - s.y, s.x);          // 90° clockwise
                  case 2: return new Vector2(W - 1 - s.x, H - 1 - s.y);
                  case 3: return new Vector2(s.y, W - 1 - s.x);          // 90° counter-clockwise
                  default: return s;
              }
          }

          Vector2 Unrotate(Vector2 r)
          {
              int W = sensorWidth, H = sensorHeight;
              switch (quarterTurns)
              {
                  case 1: return new Vector2(r.y, H - 1 - r.x);
                  case 2: return new Vector2(W - 1 - r.x, H - 1 - r.y);
                  case 3: return new Vector2(W - 1 - r.y, r.x);
                  default: return r;
              }
          }

          /// <summary>Intrinsics of the inference image. <paramref name="s"/> must be at sensor resolution.</summary>
          public DepthIntrinsics Intrinsics(DepthIntrinsics s)
          {
              int W = sensorWidth, H = sensorHeight;
              float fx, fy, cx, cy;
              switch (quarterTurns)
              {
                  // Derived from Rotate(): e.g. k=1 gives u' = H-1-v, so u' = fy·y/z + (H-1-cy).
                  case 1: fx = s.fy; fy = s.fx; cx = H - 1 - s.cy; cy = s.cx; break;
                  case 2: fx = s.fx; fy = s.fy; cx = W - 1 - s.cx; cy = H - 1 - s.cy; break;
                  case 3: fx = s.fy; fy = s.fx; cx = s.cy; cy = W - 1 - s.cx; break;
                  default: fx = s.fx; fy = s.fy; cx = s.cx; cy = s.cy; break;
              }
              return new DepthIntrinsics
              {
                  fx = fx * scale, fy = fy * scale,
                  cx = (cx + 0.5f) * scale - 0.5f + offsetX,
                  cy = (cy + 0.5f) * scale - 0.5f + offsetY,
                  width = size, height = size,
              };
          }

          /// <summary>
          /// Rotating the image k quarter turns clockwise equals rotating the camera about its optical
          /// axis so that its new +X is the old +Y (for k=1). AngleAxis(90, forward) maps right→up.
          /// </summary>
          public Pose CameraPose(Pose sensorPose) =>
              new Pose(sensorPose.position, sensorPose.rotation * Quaternion.AngleAxis(90f * quarterTurns, Vector3.forward));

          /// <summary>
          /// Pick the turn whose camera "up" best matches the display's up. Uses the display (not
          /// gravity) so the choice stays stable when the phone points straight at the floor.
          /// </summary>
          public static int ChooseQuarterTurns(Quaternion sensorRotation, Vector3 displayUp)
          {
              int best = 0;
              float bestDot = float.NegativeInfinity;
              for (int k = 0; k < 4; k++)
              {
                  Vector3 up = sensorRotation * (Quaternion.AngleAxis(90f * k, Vector3.forward) * Vector3.up);
                  float d = Vector3.Dot(up, displayUp);
                  if (d > bestDot) { bestDot = d; best = k; }
              }
              return best;
          }
      }
  }
  ```

- [ ] **Step 5: Run the tests to verify they pass**

  Run the test command. Expected: PASS.

  If `SensorMapping_AgreesWithInferenceCamera` fails only for k = 1 and 3, the sign of the AngleAxis turn is reversed. Re-derive from `Rotate()`, do not tune numbers.

- [ ] **Step 6: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Add InverseDepthImage and sensor-to-inference geometry (rotation, letterbox, intrinsics, pose)"
  ```

---

### Task 4: `InferenceRateGovernor`

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/InferenceRateGovernor.cs`
- Test: `Assets/WallDistance/Tests/EditMode/InferenceRateGovernorTests.cs`

**Interfaces:**
- Produces: `sealed class InferenceRateGovernor`
  - fields: `normalHz = 15`, `throttledHz = 5`, `slowMilliseconds = 60`, `slowForSeconds = 5`, `recoverAfterSeconds = 30`
  - `bool Throttled`, `double MinIntervalSeconds`, `string State` (`"normal"` / `"throttled"`), `int Transitions`
  - `void Record(double milliseconds, double now)`, `void Reset()`

- [ ] **Step 1: Write the failing test**

  ```csharp
  using NUnit.Framework;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class InferenceRateGovernorTests
      {
          [Test]
          public void StartsAt15Hz()
          {
              var g = new InferenceRateGovernor();
              Assert.IsFalse(g.Throttled);
              Assert.AreEqual(1.0 / 15.0, g.MinIntervalSeconds, 1e-9);
              Assert.AreEqual("normal", g.State);
          }

          [Test]
          public void SlowForFiveSeconds_DropsTo5Hz()
          {
              var g = new InferenceRateGovernor();
              for (int i = 0; i < 50; i++) g.Record(70, i * 0.1);   // t = 0 … 4.9
              Assert.IsFalse(g.Throttled, "4.9 s is not yet 5 s");
              g.Record(70, 5.0);
              Assert.IsTrue(g.Throttled);
              Assert.AreEqual(0.2, g.MinIntervalSeconds, 1e-9);
              Assert.AreEqual("throttled", g.State);
              Assert.AreEqual(1, g.Transitions);
          }

          [Test]
          public void OneFastInference_RestartsTheSlowClock()
          {
              var g = new InferenceRateGovernor();
              for (int i = 0; i <= 30; i++) g.Record(70, i * 0.1);   // 0 … 3.0
              g.Record(20, 3.1);
              for (int i = 32; i <= 70; i++) g.Record(70, i * 0.1);  // 3.2 … 7.0: only 3.8 s slow
              Assert.IsFalse(g.Throttled);
          }

          [Test]
          public void ThirtySecondsFast_RecoversTo15Hz()
          {
              var g = new InferenceRateGovernor();
              for (int i = 0; i <= 50; i++) g.Record(70, i * 0.1);
              Assert.IsTrue(g.Throttled);
              for (int i = 0; i < 150; i++) g.Record(20, 6.0 + i * 0.2);   // 6.0 … 35.8
              Assert.IsTrue(g.Throttled, "29.8 s fast is not yet 30 s");
              g.Record(20, 36.0);
              Assert.IsFalse(g.Throttled);
              Assert.AreEqual(2, g.Transitions);
          }
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  Expected: compilation error, `'InferenceRateGovernor' could not be found`.

- [ ] **Step 3: Implement**

  ```csharp
  namespace WallDistance.Core
  {
      /// <summary>
      /// Thermal step-down for the NPU + camera load (spec §6, §12). Sustained slow inferences are
      /// the observable symptom of throttling, so the rule keys on latency rather than on Android
      /// thermal APIs, which vary by vendor. The 30 s recovery rule is a plan proposal (D5).
      /// </summary>
      public sealed class InferenceRateGovernor
      {
          public float normalHz = 15f;
          public float throttledHz = 5f;
          public float slowMilliseconds = 60f;
          public float slowForSeconds = 5f;
          public float recoverAfterSeconds = 30f;

          public bool Throttled { get; private set; }
          public int Transitions { get; private set; }
          public double MinIntervalSeconds => 1.0 / (Throttled ? throttledHz : normalHz);
          public string State => Throttled ? "throttled" : "normal";

          double _slowSince = double.NaN;
          double _fastSince = double.NaN;

          public void Record(double milliseconds, double now)
          {
              // Tiny tolerance so float clocks such as 50·0.1 still count as 5 s.
              const double eps = 1e-9;
              if (milliseconds > slowMilliseconds)
              {
                  _fastSince = double.NaN;
                  if (double.IsNaN(_slowSince)) _slowSince = now;
                  if (!Throttled && now - _slowSince >= slowForSeconds - eps)
                  {
                      Throttled = true;
                      Transitions++;
                  }
              }
              else
              {
                  _slowSince = double.NaN;
                  if (!Throttled) return;
                  if (double.IsNaN(_fastSince)) _fastSince = now;
                  if (now - _fastSince >= recoverAfterSeconds - eps)
                  {
                      Throttled = false;
                      Transitions++;
                      _fastSince = double.NaN;
                  }
              }
          }

          public void Reset()
          {
              Throttled = false;
              _slowSince = _fastSince = double.NaN;
          }
      }
  }
  ```

- [ ] **Step 4: Run the tests to verify they pass**

  Expected: PASS.

- [ ] **Step 5: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Add inference rate governor for thermal step-down (15 Hz to 5 Hz)"
  ```

---

### Task 5: AR capture — floor source, inference interface, scheduler with frame dumps

This task makes the app capture correctly prepared 518² frames, with their exact camera, before any model runs. Phase 1 (Task 7) needs these dumps to test the model on real corridor frames.

**Files:**
- Create: `Assets/WallDistance/Runtime/AR/FloorPlaneSource.cs`
- Create: `Assets/WallDistance/Runtime/AR/IDepthInference.cs`
- Create: `Assets/WallDistance/Runtime/AR/DepthInferenceScheduler.cs`
- Modify: `Assets/WallDistance/Runtime/AR/WallDistanceService.cs` (Awake, Update)
- Modify: `Assets/WallDistance/Runtime/AR/WallDistanceHud.cs` (dump button)

**Interfaces:**
- Consumes:
  - `FloorPlane` (Task 2).
  - `InverseDepthImage`, `InferenceGeometry` (Task 3).
  - `InferenceRateGovernor` (Task 4).
  - `ARCoreSensorPose.TryGet(ARSession, ARCameraManager, Camera, out Pose)` and `.FrameTimestamp` (existing, `internal`, same assembly).
- Produces:
  - `struct InferenceRequest { byte[] rgb; int size; InverseDepthImage target; }`. `rgb` is HWC uint8 RGB, size×size×3.
  - `interface IDepthInference : IDisposable { bool IsAvailable; bool IsBusy; string Status; bool TryBegin(in InferenceRequest); bool TryCollect(out InverseDepthImage); }`
  - `sealed class NullDepthInference(string reason) : IDepthInference`
  - `FloorPlaneSource : MonoBehaviour`: `ARPlane Current`, `FloorPlane CurrentPlane`, `bool HasFloor`, `void Refresh()`
  - `DepthInferenceScheduler : MonoBehaviour`:
    - `IDepthInference Backend` (settable), `InferenceRateGovernor Governor`
    - `event Action<InverseDepthImage> FrameReady`
    - `float CollectedHz`, `double LastInferenceMs`, `string Status`
    - `int DumpRemaining`, `void RequestDump(int frames)`
    - `const int Size = 518`
  - `WallDistanceService.floorSource`, `.scheduler` (public fields)

There is no EditMode test: AR Foundation types cannot be built in the Core test assembly. This task is verified by compiling, then by the on-device dump in Step 7, which Task 7 consumes.

- [ ] **Step 1: Create `IDepthInference.cs`**

  ```csharp
  using System;
  using WallDistance.Core;

  namespace WallDistance.AR
  {
      /// <summary>One prepared network input. The backend writes its result into <see cref="target"/>.</summary>
      public struct InferenceRequest
      {
          /// <summary>size×size×3 RGB, row 0 = top, display-upright and letterboxed (InferenceGeometry).</summary>
          public byte[] rgb;
          public int size;
          /// <summary>Carries the camera for this exact image; returned by TryCollect once filled.</summary>
          public InverseDepthImage target;
      }

      /// <summary>
      /// The ML depth backend, kept behind an interface so LiteRT can replace QNN (spec §4)
      /// without touching the scheduler or the pipeline. At most one request is ever in flight.
      /// </summary>
      public interface IDepthInference : IDisposable
      {
          bool IsAvailable { get; }
          bool IsBusy { get; }
          /// <summary>Human-readable state, logged in the CSV header and shown on the HUD when unavailable.</summary>
          string Status { get; }
          /// <summary>Queue one inference. False when busy or unavailable; the caller drops the frame.</summary>
          bool TryBegin(in InferenceRequest request);
          /// <summary>True once, when the request started by TryBegin has finished.</summary>
          bool TryCollect(out InverseDepthImage image);
      }

      /// <summary>Backend for devices or builds without ML depth; keeps the ARCore path running.</summary>
      public sealed class NullDepthInference : IDepthInference
      {
          public NullDepthInference(string reason) { Status = reason; }
          public bool IsAvailable => false;
          public bool IsBusy => false;
          public string Status { get; }
          public bool TryBegin(in InferenceRequest request) => false;
          public bool TryCollect(out InverseDepthImage image) { image = null; return false; }
          public void Dispose() { }
      }
  }
  ```

- [ ] **Step 2: Create `FloorPlaneSource.cs`**

  ```csharp
  using UnityEngine;
  using UnityEngine.XR.ARFoundation;
  using UnityEngine.XR.ARSubsystems;
  using WallDistance.Core;

  namespace WallDistance.AR
  {
      /// <summary>
      /// Chooses THE floor: the largest tracked upward-facing plane whose height below the camera is
      /// plausible (same bounds as assisted mode). Sticky: once chosen, a floor is kept while it
      /// stays valid, so the depth scale does not jump between two similar planes.
      /// </summary>
      public sealed class FloorPlaneSource : MonoBehaviour
      {
          public ARPlaneManager planeManager;
          public Camera arCamera;

          public ARPlane Current { get; private set; }
          public FloorPlane CurrentPlane { get; private set; }
          public bool HasFloor => CurrentPlane.IsValid;

          void Awake()
          {
              if (planeManager == null) planeManager = FindAnyObjectByType<ARPlaneManager>();
              if (arCamera == null)
              {
                  var cm = FindAnyObjectByType<ARCameraManager>();
                  if (cm != null) arCamera = cm.GetComponent<Camera>();
              }
          }

          /// <summary>Call once per frame before anything reads CurrentPlane.</summary>
          public void Refresh()
          {
              if (planeManager == null || arCamera == null) { Clear(); return; }
              Vector3 cam = arCamera.transform.position;
              if (Current != null && IsUsable(Current, cam))
              {
                  // Re-read every frame: ARCore refines plane height as it sees more floor.
                  CurrentPlane = ToFloor(Current);
                  return;
              }
              ARPlane best = null;
              float bestArea = 0f;
              foreach (var p in planeManager.trackables)
              {
                  if (!IsUsable(p, cam)) continue;
                  float area = p.size.x * p.size.y;
                  if (area > bestArea) { bestArea = area; best = p; }
              }
              Current = best;
              CurrentPlane = best != null ? ToFloor(best) : default;
          }

          void Clear() { Current = null; CurrentPlane = default; }

          static FloorPlane ToFloor(ARPlane p) => new FloorPlane(p.center, p.normal);

          static bool IsUsable(ARPlane p, Vector3 cam) =>
              p != null && p.trackingState == TrackingState.Tracking && p.subsumedBy == null
              && p.alignment == PlaneAlignment.HorizontalUp && ToFloor(p).PlausibleCameraHeight(cam);
      }
  }
  ```

- [ ] **Step 3: Create `DepthInferenceScheduler.cs`**

  ```csharp
  using System;
  using System.Globalization;
  using System.IO;
  using System.Text;
  using Unity.Collections;
  using UnityEngine;
  using UnityEngine.XR.ARFoundation;
  using UnityEngine.XR.ARSubsystems;
  using WallDistance.Core;

  namespace WallDistance.AR
  {
      /// <summary>
      /// Feeds the depth network. On each camera frame it decides whether to start an inference
      /// (backend idle, rate allows it), turns the CPU image into the upright, letterboxed 518²
      /// RGB the model expects, and stamps it with the pose and intrinsics of THAT image.
      /// Collected results are raised as <see cref="FrameReady"/> on the main thread.
      ///
      /// Conversion is synchronous (plan deviation D2): ConvertAsync completes on a later frame,
      /// when the only pose available would belong to a different image.
      /// </summary>
      [DefaultExecutionOrder(-50)]
      public sealed class DepthInferenceScheduler : MonoBehaviour
      {
          public const int Size = 518;

          public ARSession session;
          public ARCameraManager cameraManager;
          public Camera arCamera;
          public FloorPlaneSource floorSource;

          public IDepthInference Backend { get; set; }
          public InferenceRateGovernor Governor { get; } = new InferenceRateGovernor();
          public event Action<InverseDepthImage> FrameReady;

          public float CollectedHz { get; private set; }
          public double LastInferenceMs { get; private set; } = double.NaN;
          public string Status { get; private set; } = "waiting for camera";
          public int DumpRemaining { get; private set; }

          readonly ARCoreSensorPose _sensorPose = new ARCoreSensorPose();
          // Two targets: the one the pipeline is reading while the next inference fills the other.
          readonly InverseDepthImage[] _images = { new InverseDepthImage(Size, Size), new InverseDepthImage(Size, Size) };
          int _next;
          readonly byte[] _rgb = new byte[Size * Size * 3];
          NativeArray<byte> _converted;
          byte[] _sensorRgb = Array.Empty<byte>();

          // Bilinear lookup table for one (sensor size, quarter turns): inference pixel ->
          // top-left sensor pixel + weights. Rebuilt only when the sensor size or orientation changes.
          int _lutW, _lutH, _lutK = -1;
          int[] _lutIndex;
          float[] _lutFx, _lutFy;

          double _lastSubmit = double.NegativeInfinity, _lastDump = double.NegativeInfinity;
          int _collectedThisWindow;
          double _windowStart;
          InverseDepthImage _dumpPendingImage;
          string _dumpPendingDir;

          void Awake()
          {
              if (session == null) session = FindAnyObjectByType<ARSession>();
              if (cameraManager == null) cameraManager = FindAnyObjectByType<ARCameraManager>();
              if (arCamera == null && cameraManager != null) arCamera = cameraManager.GetComponent<Camera>();
              if (floorSource == null) floorSource = FindAnyObjectByType<FloorPlaneSource>();
          }

          void OnEnable() { if (cameraManager != null) cameraManager.frameReceived += OnCameraFrame; }

          void OnDisable()
          {
              if (cameraManager != null) cameraManager.frameReceived -= OnCameraFrame;
          }

          void OnDestroy()
          {
              if (_converted.IsCreated) _converted.Dispose();
              Backend?.Dispose();
          }

          /// <summary>Development builds: save the next <paramref name="frames"/> prepared inputs (Phase 1 data).</summary>
          public void RequestDump(int frames) => DumpRemaining = Mathf.Max(0, frames);

          void OnCameraFrame(ARCameraFrameEventArgs args)
          {
              if (ARSession.state != ARSessionState.SessionTracking) return;
              double now = Time.realtimeSinceStartupAsDouble;
              bool canInfer = Backend != null && Backend.IsAvailable && !Backend.IsBusy
                              && now - _lastSubmit >= Governor.MinIntervalSeconds;
              // Dumps are spaced 0.25 s apart so 20 frames cover several seconds of motion.
              bool wantDump = DumpRemaining > 0 && now - _lastDump >= 0.25;
              if (!canInfer && !wantDump) return;

              if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) { Status = "no CPU image"; return; }
              using (image)
              {
                  if (!cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intr)) { Status = "no intrinsics"; return; }
                  if (!_sensorPose.TryGet(session, cameraManager, arCamera, out Pose sensorPose)) { Status = _sensorPose.Status; return; }
                  // The pose must be this image's pose. 5 ms is well under one 30 fps frame (33 ms).
                  if (Math.Abs(_sensorPose.FrameTimestamp - image.timestamp) > 0.005)
                  {
                      Status = $"pose/image clock mismatch {(_sensorPose.FrameTimestamp - image.timestamp) * 1000:F1} ms";
                      return;
                  }

                  var sensorIntr = new DepthIntrinsics
                  {
                      fx = intr.focalLength.x, fy = intr.focalLength.y,
                      cx = intr.principalPoint.x, cy = intr.principalPoint.y,
                      width = intr.resolution.x, height = intr.resolution.y,
                  }.ScaledTo(image.width, image.height);
                  int k = InferenceGeometry.ChooseQuarterTurns(sensorPose.rotation, arCamera.transform.up);
                  var geo = InferenceGeometry.Create(image.width, image.height, k, Size);

                  if (!ConvertToRgb(image)) return;
                  var target = _images[_next];
                  Resample(image.width, image.height, geo, target.luma);
                  target.intrinsics = geo.Intrinsics(sensorIntr);
                  target.cameraPose = geo.CameraPose(sensorPose);
                  target.timestamp = now;
                  target.content = geo.content;
                  target.inferenceMilliseconds = double.NaN;

                  string dumpDir = null;
                  if (wantDump)
                  {
                      dumpDir = WriteDump(target, geo, sensorIntr, image.timestamp);
                      _lastDump = now;
                      DumpRemaining--;
                  }

                  if (canInfer && Backend.TryBegin(new InferenceRequest { rgb = _rgb, size = Size, target = target }))
                  {
                      _lastSubmit = now;
                      _next ^= 1;
                      if (dumpDir != null) { _dumpPendingImage = target; _dumpPendingDir = dumpDir; }
                      Status = "running";
                  }
              }
          }

          void Update()
          {
              double now = Time.realtimeSinceStartupAsDouble;
              if (Backend != null && Backend.TryCollect(out InverseDepthImage img))
              {
                  LastInferenceMs = img.inferenceMilliseconds;
                  if (!double.IsNaN(img.inferenceMilliseconds)) Governor.Record(img.inferenceMilliseconds, now);
                  _collectedThisWindow++;
                  if (ReferenceEquals(img, _dumpPendingImage)) WriteOutput(img, _dumpPendingDir);
                  FrameReady?.Invoke(img);
              }
              if (now - _windowStart >= 1.0)
              {
                  CollectedHz = (float)(_collectedThisWindow / Math.Max(1e-3, now - _windowStart));
                  _collectedThisWindow = 0;
                  _windowStart = now;
              }
          }

          bool ConvertToRgb(XRCpuImage image)
          {
              // Transformation.None keeps the sensor's memory order: row 0 is the TOP of the image,
              // matching the "v grows down" convention. (Texture uploads need MirrorY; we do not.)
              var p = new XRCpuImage.ConversionParams(image, TextureFormat.RGB24, XRCpuImage.Transformation.None);
              int bytes = image.GetConvertedDataSize(p);
              if (!_converted.IsCreated || _converted.Length != bytes)
              {
                  if (_converted.IsCreated) _converted.Dispose();
                  _converted = new NativeArray<byte>(bytes, Allocator.Persistent);
                  _sensorRgb = new byte[bytes];
              }
              try { image.Convert(p, _converted); }
              catch (Exception e) { Status = "convert failed: " + e.Message; return false; }
              _converted.CopyTo(_sensorRgb);
              return true;
          }

          void Resample(int w, int h, InferenceGeometry geo, byte[] luma)
          {
              if (_lutIndex == null || _lutW != w || _lutH != h || _lutK != geo.quarterTurns) BuildLut(w, h, geo);
              int stride = w * 3;
              for (int i = 0; i < Size * Size; i++)
              {
                  int o = i * 3;
                  int s = _lutIndex[i];
                  if (s < 0)
                  {
                      // Letterbox padding is black; its network output is discarded (NaN) anyway.
                      _rgb[o] = _rgb[o + 1] = _rgb[o + 2] = 0;
                      luma[i] = 0;
                      continue;
                  }
                  float fx = _lutFx[i], fy = _lutFy[i];
                  for (int c = 0; c < 3; c++)
                  {
                      float top = _sensorRgb[s + c] + (_sensorRgb[s + 3 + c] - _sensorRgb[s + c]) * fx;
                      float bot = _sensorRgb[s + stride + c] + (_sensorRgb[s + stride + 3 + c] - _sensorRgb[s + stride + c]) * fx;
                      _rgb[o + c] = (byte)(top + (bot - top) * fy + 0.5f);
                  }
                  // Rec. 601 luma, integer weights; the edge refiner only needs relative contrast.
                  luma[i] = (byte)((77 * _rgb[o] + 150 * _rgb[o + 1] + 29 * _rgb[o + 2]) >> 8);
              }
          }

          void BuildLut(int w, int h, InferenceGeometry geo)
          {
              _lutW = w; _lutH = h; _lutK = geo.quarterTurns;
              _lutIndex = new int[Size * Size];
              _lutFx = new float[Size * Size];
              _lutFy = new float[Size * Size];
              for (int v = 0; v < Size; v++)
              for (int u = 0; u < Size; u++)
              {
                  int i = v * Size + u;
                  if (!geo.content.Contains(new Vector2Int(u, v))) { _lutIndex[i] = -1; continue; }
                  Vector2 s = geo.InferenceToSensor(new Vector2(u, v));
                  // Clamp so the 2×2 bilinear footprint never leaves the sensor image.
                  float sx = Mathf.Clamp(s.x, 0f, w - 1.001f), sy = Mathf.Clamp(s.y, 0f, h - 1.001f);
                  int x0 = (int)sx, y0 = (int)sy;
                  _lutIndex[i] = (y0 * w + x0) * 3;
                  _lutFx[i] = sx - x0;
                  _lutFy[i] = sy - y0;
              }
          }

          // ---------------------------------------------------------------- dumps (Phase 1 data)

          static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

          string WriteDump(InverseDepthImage img, InferenceGeometry geo, DepthIntrinsics sensorIntr, double imageTimestamp)
          {
              try
              {
                  string dir = Path.Combine(Application.persistentDataPath, "InferenceDumps",
                      "frame_" + imageTimestamp.ToString("F6", Inv).Replace('.', '_'));
                  Directory.CreateDirectory(dir);
                  File.WriteAllBytes(Path.Combine(dir, "input.rgb"), _rgb);
                  File.WriteAllBytes(Path.Combine(dir, "luma.u8"), img.luma);
                  var floor = floorSource != null ? floorSource.CurrentPlane : default;
                  var sb = new StringBuilder();
                  sb.Append('{');
                  Kv(sb, "size", Size); Kv(sb, "quarterTurns", geo.quarterTurns);
                  Kv(sb, "sensorWidth", geo.sensorWidth); Kv(sb, "sensorHeight", geo.sensorHeight);
                  Kv(sb, "imageTimestamp", imageTimestamp);
                  KvArr(sb, "sensorIntrinsics", sensorIntr.fx, sensorIntr.fy, sensorIntr.cx, sensorIntr.cy);
                  KvArr(sb, "intrinsics", img.intrinsics.fx, img.intrinsics.fy, img.intrinsics.cx, img.intrinsics.cy);
                  KvArr(sb, "content", img.content.x, img.content.y, img.content.width, img.content.height);
                  KvArr(sb, "cameraPosition", img.cameraPose.position.x, img.cameraPose.position.y, img.cameraPose.position.z);
                  KvArr(sb, "cameraRotationXYZW", img.cameraPose.rotation.x, img.cameraPose.rotation.y, img.cameraPose.rotation.z, img.cameraPose.rotation.w);
                  Kv(sb, "hasFloor", floor.IsValid ? 1 : 0);
                  KvArr(sb, "floorPoint", floor.point.x, floor.point.y, floor.point.z);
                  KvArr(sb, "floorUp", true, floor.up.x, floor.up.y, floor.up.z);
                  sb.Append('}');
                  File.WriteAllText(Path.Combine(dir, "meta.json"), sb.ToString());
                  return dir;
              }
              catch (Exception e) { Status = "dump failed: " + e.Message; return null; }
          }

          void WriteOutput(InverseDepthImage img, string dir)
          {
              _dumpPendingImage = null;
              if (dir == null) return;
              var bytes = new byte[img.values.Length * 4];
              Buffer.BlockCopy(img.values, 0, bytes, 0, bytes.Length);   // little-endian float32 on ARM64
              File.WriteAllBytes(Path.Combine(dir, "output.f32"), bytes);
          }

          static void Kv(StringBuilder sb, string k, double v) =>
              sb.Append('"').Append(k).Append("\":").Append(v.ToString("R", Inv)).Append(',');

          static void KvArr(StringBuilder sb, string k, params double[] v) => KvArr(sb, k, false, v);

          static void KvArr(StringBuilder sb, string k, bool last, params double[] v)
          {
              sb.Append('"').Append(k).Append("\":[");
              for (int i = 0; i < v.Length; i++) { if (i > 0) sb.Append(','); sb.Append(v[i].ToString("R", Inv)); }
              sb.Append(']');
              if (!last) sb.Append(',');
          }
      }
  }
  ```

- [ ] **Step 4: Wire the components into `WallDistanceService.cs`**

  **4a.** After `public ARDepthFrameSource depthSource;` add:

  ```csharp
          public FloorPlaneSource floorSource;
          public DepthInferenceScheduler scheduler;
  ```

  **4b.** In `Awake()`, after the `Assisted` lines, add:

  ```csharp
              // Learned-depth capture lives on the same XR Origin; created here so the scene needs no edits.
              if (floorSource == null) floorSource = GetComponent<FloorPlaneSource>();
              if (floorSource == null) floorSource = gameObject.AddComponent<FloorPlaneSource>();
              if (scheduler == null) scheduler = GetComponent<DepthInferenceScheduler>();
              if (scheduler == null) scheduler = gameObject.AddComponent<DepthInferenceScheduler>();
              scheduler.floorSource = floorSource;
              // Replaced by the QNN backend in Task 19; until then capture/dumps work without inference.
              if (scheduler.Backend == null) scheduler.Backend = new NullDepthInference("ML backend not built yet");
  ```

  **4c.** In `Update()`, after `Assisted.Refresh();`, add:

  ```csharp
              floorSource.Refresh();
  ```

- [ ] **Step 5: Add a Development-only dump button to `WallDistanceHud.cs`**

  At the end of `BuildUi()`, before the EventSystem block, add:

  ```csharp
              // Phase 1 data capture (spec §7 debug toggle). Development builds only, so field
              // users never fill their storage with dumps.
              if (Debug.isDebugBuild)
              {
                  MakeButton(canvasGo.transform, font, "Dump", new Vector2(0, 1160), new Vector2(600, 100),
                      () => { if (service.scheduler != null) service.scheduler.RequestDump(20); }, out _dumpText);
                  _dumpText.text = "Dump frames (20)";
              }
  ```

  Add `_dumpText` to the field list: change `Text _instruction, _modeText, _captureText;` to `Text _instruction, _modeText, _captureText, _dumpText;`.

  At the end of `OnUpdated`, add:

  ```csharp
              if (_dumpText != null && service.scheduler != null)
                  _dumpText.text = service.scheduler.DumpRemaining > 0 ? $"Dumping… {service.scheduler.DumpRemaining} left" : "Dump frames (20)";
  ```

- [ ] **Step 6: Compile and run the tests**

  Run: `unity command run_tests --mode editor --filter WallDistance --filter_type assembly --timeout 240 --no-banner`

  Expected: PASS (all existing and Task 1–4 tests). A compile error in the AR assembly also fails this run, so a green run proves the new AR code compiles.

- [ ] **Step 7: Capture a test dump on the device**

  1. Build the Development APK:

     ```
     unity command build --target Android --outputPath Builds/Android/WallDistanceDemo.apk --options '["Development"]' --confirm true
     ```

  2. Install it, then check that the dump folder exists after pressing the button once:

     ```bash
     ADB="/c/Program Files/Unity/Hub/Editor/6000.3.5f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
     "$ADB" install -r Builds/Android/WallDistanceDemo.apk
     ```

  3. In the app, scan the floor until tracking, press **Dump frames (20)**, and wait until the label returns to "Dump frames (20)".

  4. Pull the dumps:

     ```bash
     "$ADB" pull /sdcard/Android/data/com.arnav.walldistance/files/InferenceDumps Builds/dumps/
     ls Builds/dumps/InferenceDumps | head
     ```

  Expected:
  - 20 `frame_*` folders, each with `input.rgb` (804,972 bytes = 518·518·3), `luma.u8` (268,324 bytes) and `meta.json`.
  - No `output.f32` yet (no backend).
  - At least some frames have `"hasFloor":1`.

  The visual check that frames are upright happens in Task 7, Step 4.

- [ ] **Step 8: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Add floor source, depth-inference interface and scheduler with Phase 1 frame dumps"
  ```

  `Builds/dumps/` stays untracked: `Builds/**` is git-ignored except the CSV logs.

---

### Task 6: Pinned model download (`models.lock.json`, `fetch-models.ps1`)

**Files:**
- Create: `tools/models.lock.json`
- Create: `tools/fetch-models.ps1`
- Modify: `.gitignore`

**Interfaces:**
- Produces:
  - `tools/.cache/depth_anything_v2-qnn_dlc-w8a16/**/*.dlc`
  - `tools/.cache/depth_anything_v2-qnn_dlc-float/**/*.dlc`
  - recorded SHA-256 values in the lock file.
  - Task 7 converts these DLCs into context binaries.

- [ ] **Step 1: Write `tools/models.lock.json`**

  Empty hash strings mean "not recorded yet". The script refuses to use an unrecorded file unless it is run with `-Record`.

  ```json
  {
    "note": "Pinned model artefacts. Hashes are recorded on the first fetch with -Record and verified on every later fetch. Model weights are git-ignored.",
    "qairt": "2.50.0.260828221209",
    "models": [
      {
        "name": "depth_anything_v2-qnn_dlc-w8a16",
        "release": "qai-hub-models v0.63.0",
        "license": "MIT (Qualcomm AI Hub model card)",
        "url": "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/depth_anything_v2/releases/v0.63.0/depth_anything_v2-qnn_dlc-w8a16.zip",
        "zipSha256": "",
        "dlcSha256": ""
      },
      {
        "name": "depth_anything_v2-qnn_dlc-float",
        "release": "qai-hub-models v0.63.0",
        "license": "MIT (Qualcomm AI Hub model card)",
        "url": "https://qaihub-public-assets.s3.us-west-2.amazonaws.com/qai-hub-models/models/depth_anything_v2/releases/v0.63.0/depth_anything_v2-qnn_dlc-float.zip",
        "zipSha256": "",
        "dlcSha256": ""
      }
    ]
  }
  ```

- [ ] **Step 2: Write `tools/fetch-models.ps1`**

  Windows PowerShell 5.1 compatible: no `??`, no `&&`.

  ```powershell
  <#
  .SYNOPSIS
    Download the pinned depth models and verify their SHA-256 against tools/models.lock.json.
  .DESCRIPTION
    First run: pass -Record to write the hashes into the lock file (then commit it).
    Every later run verifies them and fails on any mismatch, so a silently changed upstream
    file can never reach a build.
  #>
  param(
      [switch]$Record
  )
  $ErrorActionPreference = 'Stop'
  $root = Split-Path -Parent $PSCommandPath
  $lockPath = Join-Path $root 'models.lock.json'
  $cache = Join-Path $root '.cache'
  New-Item -ItemType Directory -Force $cache | Out-Null
  $lock = Get-Content $lockPath -Raw | ConvertFrom-Json
  $changed = $false

  foreach ($m in $lock.models) {
      $zip = Join-Path $cache ($m.name + '.zip')
      if (-not (Test-Path $zip)) {
          Write-Host "Downloading $($m.url)"
          # TLS 1.2 is not the default in Windows PowerShell 5.1.
          [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
          Invoke-WebRequest -Uri $m.url -OutFile $zip -UseBasicParsing
      }
      $zipHash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
      if ([string]::IsNullOrEmpty($m.zipSha256)) {
          if (-not $Record) { throw "$($m.name): zip hash not recorded. Run once with -Record, review, and commit the lock file." }
          $m.zipSha256 = $zipHash; $changed = $true
      } elseif ($m.zipSha256 -ne $zipHash) {
          throw "$($m.name): zip SHA-256 mismatch (lock $($m.zipSha256), got $zipHash)"
      }

      $dir = Join-Path $cache $m.name
      if (-not (Test-Path $dir)) { Expand-Archive -Path $zip -DestinationPath $dir }
      # The archive layout is not documented, so find the DLC rather than assume a path.
      $dlcs = @(Get-ChildItem -Path $dir -Recurse -Filter '*.dlc')
      if ($dlcs.Count -ne 1) {
          Get-ChildItem -Path $dir -Recurse | ForEach-Object { Write-Host $_.FullName }
          throw "$($m.name): expected exactly one .dlc in the archive, found $($dlcs.Count) (listing above)"
      }
      $dlcHash = (Get-FileHash $dlcs[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
      if ([string]::IsNullOrEmpty($m.dlcSha256)) {
          if (-not $Record) { throw "$($m.name): DLC hash not recorded. Run once with -Record." }
          $m.dlcSha256 = $dlcHash; $changed = $true
      } elseif ($m.dlcSha256 -ne $dlcHash) {
          throw "$($m.name): DLC SHA-256 mismatch (lock $($m.dlcSha256), got $dlcHash)"
      }
      Write-Host "$($m.name): OK -> $($dlcs[0].FullName)"
  }

  if ($changed) {
      $lock | ConvertTo-Json -Depth 5 | Set-Content -Path $lockPath -Encoding UTF8
      Write-Host "Recorded hashes in $lockPath - review and commit it."
  }
  ```

- [ ] **Step 3: Ignore the cache**

  Append to `.gitignore`:

  ```gitignore

  # Model download cache (tools/fetch-models.ps1) and generated QNN context binaries.
  /tools/.cache/
  /Assets/StreamingAssets/Models/
  /Assets/StreamingAssets/Models.meta
  ```

- [ ] **Step 4: Run without `-Record` to verify it refuses**

  Run: `powershell -ExecutionPolicy Bypass -File tools/fetch-models.ps1`

  Expected: the downloads complete, then the script fails with `zip hash not recorded. Run once with -Record`.

- [ ] **Step 5: Record, then verify**

  ```
  powershell -ExecutionPolicy Bypass -File tools/fetch-models.ps1 -Record
  powershell -ExecutionPolicy Bypass -File tools/fetch-models.ps1
  ```

  Expected:
  - The first run prints `Recorded hashes`.
  - The second prints `depth_anything_v2-qnn_dlc-w8a16: OK -> …\*.dlc` and the float line, with no error.
  - `git diff tools/models.lock.json` shows four 64-hex-character hashes.

- [ ] **Step 6: Commit**

  ```bash
  git add tools/models.lock.json tools/fetch-models.ps1 .gitignore
  git commit -m "Pin Depth Anything V2 QNN DLCs with SHA-256 verified download"
  ```

---

### Task 7: Phase 1 — runtime spike (manual + scripts; GATE)

Answer two questions on the 13R before any Phase 2 code depends on them:

1. Is w8a16 fast enough? Gate: p50 ≤ 35 ms.
2. Is the output affine in inverse depth or in depth, measured against the tracked floor?

Plus the input/output facts the native plugin needs.

**Files:**
- Create: `tools/phase1/dump_to_png.py`, `tools/phase1/make_inputs.py`, `tools/phase1/analyse.py`, `tools/phase1/make-context.ps1`
- Create: `docs/phase1-runtime-spike.md`
- Possibly modify: `Assets/WallDistance/Runtime/Core/DetectionConfig.cs` and `Tests/EditMode/ApiCompatibilityTests.cs`, only if Phase 1 picks `AffineDepth` (Step 9)

**Interfaces:**
- Consumes:
  - the dumps from Task 5 (`input.rgb`, `luma.u8`, `meta.json`);
  - the DLCs from Task 6.
- Produces (recorded in `docs/phase1-runtime-spike.md`; Task 17 relies on them):
  - graph name; input/output tensor names, dims, dtypes and quantisation;
  - `Assets/StreamingAssets/Models/depth_anything_v2_w8a16.ctx.bin` (git-ignored);
  - the chosen `DepthParameterisation`.

- [ ] **Step 1: Install the SDK and Python dependency (Rachit: needs a Qualcomm login)**

  1. Download **Qualcomm AI Runtime (QAIRT) SDK 2.50.0.260828221209** from the Qualcomm software centre. The version must match `tools/models.lock.json` → `qairt`.
  2. Extract it, for example to `C:\qairt\2.50.0.260828221209`.
  3. Set the environment variable persistently:

     ```powershell
     [Environment]::SetEnvironmentVariable('QNN_SDK_ROOT', 'C:\qairt\2.50.0.260828221209', 'User')
     ```

     Open a new terminal afterwards.
  4. Install numpy:

     ```bash
     /c/msys64/ucrt64/bin/python -m pip install numpy
     ```

     If pip refuses (msys-managed Python), run `pacman -S mingw-w64-ucrt-x86_64-python-numpy` in an MSYS2 UCRT64 shell instead.

  Check:

  ```bash
  ls "$QNN_SDK_ROOT/bin" "$QNN_SDK_ROOT/lib"
  /c/msys64/ucrt64/bin/python -c "import numpy; print(numpy.__version__)"
  ```

  Expected:
  - `bin/` lists host folders (look for `x86_64-windows-msvc`) and `aarch64-android`.
  - `lib/` lists `aarch64-android` and `hexagon-v75`.
  - numpy prints a version.

  **If `bin/x86_64-windows-msvc` has no `qnn-context-binary-generator.exe`:** the Windows host may not support offline HTP context generation in this release. Use WSL2 (Ubuntu) with the SDK's `bin/x86_64-linux-clang` tools instead, and note it in the spike doc. Steps 2 and 3 are otherwise unchanged; run `make-context.ps1`'s commands by hand in WSL with the Linux paths.

- [ ] **Step 2: Read the licence and the model's tensor facts**

  1. Open the SDK's licence file:

     ```bash
     ls "$QNN_SDK_ROOT" | grep -i -E "licen|notice"
     ```

  2. Find whether `libQnnHtp.so`, `libQnnHtpV75Stub.so`, `libQnnHtpV75Skel.so` and `libQnnSystem.so` may be **redistributed inside an app**. Record the clause verbatim (file and section) in the spike doc. **If redistribution is not permitted, STOP and tell Rachit.** The LiteRT fallback (Task 23) then applies.
  3. Find the DLC inspection tool and dump the model's tensors:

     ```bash
     ls "$QNN_SDK_ROOT/bin/x86_64-windows-msvc" | grep -i dlc
     "$QNN_SDK_ROOT/bin/x86_64-windows-msvc/qairt-dlc-info.exe" -i "$(find tools/.cache/depth_anything_v2-qnn_dlc-w8a16 -name '*.dlc')" > Builds/phase1/dlc-info-w8a16.txt
     ```

     If the tool has a different name, use the `*dlc-info*` executable the `ls` lists.
  4. From `dlc-info-w8a16.txt`, record in the spike doc:
     - graph name;
     - input name, dims, dtype and quantisation (scale/offset);
     - the same for the output;
     - whether the input is NCHW `[1,3,518,518]` or NHWC `[1,518,518,3]`.

- [ ] **Step 3: Write `tools/phase1/make-context.ps1` and build the context binaries**

  ```powershell
  <#
  .SYNOPSIS
    Offline-compile a pinned QNN DLC into an HTP v75 (SM8650) context binary.
  .NOTES
    Flag names follow QAIRT's qnn-context-binary-generator. Run it with --help first and fix
    any flag that differs in this SDK build before trusting the output.
    soc_model 57 = SM8650 is from QAIRT's supported-SoC table (inferred; confirm in the SDK docs).
  #>
  param(
      [Parameter(Mandatory = $true)][string]$Variant,      # e.g. depth_anything_v2-qnn_dlc-w8a16
      [Parameter(Mandatory = $true)][string]$OutName,      # e.g. depth_anything_v2_w8a16
      [int]$SocModel = 57
  )
  $ErrorActionPreference = 'Stop'
  $sdk = $env:QNN_SDK_ROOT
  if (-not $sdk) { throw 'QNN_SDK_ROOT is not set' }
  $repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
  $dlc = @(Get-ChildItem -Path (Join-Path $repo "tools\.cache\$Variant") -Recurse -Filter '*.dlc')[0].FullName
  $bin = Join-Path $sdk 'bin\x86_64-windows-msvc'
  $lib = Join-Path $sdk 'lib\x86_64-windows-msvc'
  $out = Join-Path $repo 'tools\.cache\ctx'
  New-Item -ItemType Directory -Force $out | Out-Null

  # HTP backend extension config: which SoC/HTP architecture to compile for.
  $htpCfg = Join-Path $out 'htp_backend_ext_config.json'
  @"
  {
    "devices": [ { "soc_model": $SocModel, "dsp_arch": "v75" } ]
  }
  "@ | Set-Content -Path $htpCfg -Encoding ASCII
  $extCfg = Join-Path $out 'htp_ext.json'
  $extLib = (Join-Path $lib 'QnnHtpNetRunExtensions.dll') -replace '\\', '/'
  $htpCfgFwd = $htpCfg -replace '\\', '/'
  @"
  {
    "backend_extensions": { "shared_library_path": "$extLib", "config_file_path": "$htpCfgFwd" }
  }
  "@ | Set-Content -Path $extCfg -Encoding ASCII

  & (Join-Path $bin 'qnn-context-binary-generator.exe') `
      --backend (Join-Path $lib 'QnnHtp.dll') `
      --model (Join-Path $lib 'QnnModelDlc.dll') `
      --dlc_path $dlc `
      --binary_file "$OutName.ctx" `
      --output_dir $out `
      --config_file $extCfg
  if ($LASTEXITCODE -ne 0) { throw "context generation failed ($LASTEXITCODE)" }
  Get-ChildItem $out -Filter "$OutName.ctx*" | ForEach-Object { Write-Host $_.FullName $_.Length }
  ```

  Run, checking flags first:

  ```powershell
  & "$env:QNN_SDK_ROOT\bin\x86_64-windows-msvc\qnn-context-binary-generator.exe" --help
  powershell -ExecutionPolicy Bypass -File tools/phase1/make-context.ps1 -Variant depth_anything_v2-qnn_dlc-w8a16 -OutName depth_anything_v2_w8a16
  powershell -ExecutionPolicy Bypass -File tools/phase1/make-context.ps1 -Variant depth_anything_v2-qnn_dlc-float -OutName depth_anything_v2_float
  ```

  Expected: two files, `tools/.cache/ctx/depth_anything_v2_w8a16.ctx.bin` and `…_float.ctx.bin`, each tens of MB.

  If `--help` names a flag differently (`--dlc_path`, `--model`, `--binary_file`, `--config_file`), edit the script to the SDK's name and record the change in the spike doc.

- [ ] **Step 4: Write `tools/phase1/dump_to_png.py` and look at the frames**

  ```python
  """Convert Task 5 dumps (input.rgb, output.f32) to PNG for a visual check. Stdlib only."""
  import json
  import struct
  import sys
  import zlib
  from pathlib import Path

  SIZE = 518


  def write_png(path, width, height, rows, channels):
      """Minimal PNG writer: 8-bit greyscale (channels=1) or RGB (channels=3)."""
      raw = b"".join(b"\x00" + bytes(r) for r in rows)
      def chunk(tag, data):
          c = struct.pack(">I", len(data)) + tag + data
          return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
      colour = 0 if channels == 1 else 2
      png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, colour, 0, 0, 0))
      png += chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b"")
      Path(path).write_bytes(png)


  def convert(frame_dir):
      d = Path(frame_dir)
      rgb = (d / "input.rgb").read_bytes()
      write_png(d / "input.png", SIZE, SIZE, [rgb[y * SIZE * 3:(y + 1) * SIZE * 3] for y in range(SIZE)], 3)
      for name in ("output.f32", "out_w8a16.f32", "out_float.f32"):
          f = d / name
          if not f.exists():
              continue
          vals = struct.unpack("<%df" % (SIZE * SIZE), f.read_bytes())
          finite = [v for v in vals if v == v]
          lo, hi = min(finite), max(finite)
          scale = 255.0 / (hi - lo) if hi > lo else 0.0
          # Bright = large network value. If near surfaces are bright, the output is disparity-like.
          px = [0 if v != v else int((v - lo) * scale) for v in vals]
          write_png(d / (name + ".png"), SIZE, SIZE, [px[y * SIZE:(y + 1) * SIZE] for y in range(SIZE)], 1)
      meta = json.loads((d / "meta.json").read_text())
      print(d.name, "k=%d" % meta["quarterTurns"], "floor=%d" % meta["hasFloor"])


  if __name__ == "__main__":
      for frame in sorted(Path(sys.argv[1]).glob("frame_*")):
          convert(frame)
  ```

  Run:

  ```bash
  /c/msys64/ucrt64/bin/python tools/phase1/dump_to_png.py Builds/dumps/InferenceDumps
  ```

  Open several `input.png` files. Expected:
  - The corridor is **upright**: floor at the bottom, not mirrored (text on signs reads correctly).
  - There are black bars left and right.

  If the image is rotated or mirrored, fix `InferenceGeometry.ChooseQuarterTurns` or `ConvertToRgb` (Task 5) and re-dump before continuing. Every later number depends on this.

- [ ] **Step 5: Capture the real dataset**

  In at least two campus corridors:
  1. Press **Dump frames (20)** three times per corridor (≥ 120 frames total).
  2. Walk slowly with the floor and both walls in view.
  3. Pull the dumps as in Task 5, Step 7, into `Builds/dumps/`.

- [ ] **Step 6: Write `tools/phase1/make_inputs.py` and push to the device**

  ```python
  """Turn dumped input.rgb frames into qnn-net-run raw inputs plus input lists.

  The model takes float32 RGB in [0, 1] (normalisation happens inside the graph). The layout
  (NCHW or NHWC) comes from the DLC info recorded in Step 2 - pass it with --layout.
  """
  import argparse
  from pathlib import Path

  import numpy as np

  SIZE = 518

  ap = argparse.ArgumentParser()
  ap.add_argument("dumps")
  ap.add_argument("out")
  ap.add_argument("--layout", choices=["nchw", "nhwc"], required=True)
  ap.add_argument("--input-name", required=True, help="input tensor name from dlc-info")
  ap.add_argument("--device-dir", default="/data/local/tmp/qnn/inputs")
  ap.add_argument("--bench-repeats", type=int, default=5)
  a = ap.parse_args()

  out = Path(a.out)
  out.mkdir(parents=True, exist_ok=True)
  lines = []
  for frame in sorted(Path(a.dumps).glob("frame_*")):
      rgb = np.frombuffer((frame / "input.rgb").read_bytes(), dtype=np.uint8).reshape(SIZE, SIZE, 3)
      x = rgb.astype(np.float32) / 255.0
      if a.layout == "nchw":
          x = np.transpose(x, (2, 0, 1))
      name = frame.name + ".raw"
      x.astype("<f4").tofile(out / name)
      lines.append(f"{a.input_name}:={a.device_dir}/{name}")

  (out / "input_list.txt").write_text("\n".join(lines) + "\n")
  # Latency list: every frame repeated, so the profile holds hundreds of inferences.
  (out / "input_list_bench.txt").write_text("\n".join(lines * a.bench_repeats) + "\n")
  (out / "frames.txt").write_text("\n".join(l.split("/")[-1][:-4] for l in lines) + "\n")
  print(len(lines), "frames")
  ```

  Run, with the layout and input name from Step 2:

  ```bash
  PY=/c/msys64/ucrt64/bin/python
  $PY tools/phase1/make_inputs.py Builds/dumps/InferenceDumps Builds/phase1/inputs --layout nhwc --input-name image
  ADB="/c/Program Files/Unity/Hub/Editor/6000.3.5f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
  "$ADB" shell mkdir -p /data/local/tmp/qnn/inputs
  for f in libQnnHtp.so libQnnHtpV75Stub.so libQnnSystem.so; do "$ADB" push "$QNN_SDK_ROOT/lib/aarch64-android/$f" /data/local/tmp/qnn/; done
  "$ADB" push "$QNN_SDK_ROOT/lib/hexagon-v75/unsigned/libQnnHtpV75Skel.so" /data/local/tmp/qnn/
  "$ADB" push "$QNN_SDK_ROOT/bin/aarch64-android/qnn-net-run" /data/local/tmp/qnn/
  "$ADB" push tools/.cache/ctx/depth_anything_v2_w8a16.ctx.bin tools/.cache/ctx/depth_anything_v2_float.ctx.bin /data/local/tmp/qnn/
  "$ADB" push Builds/phase1/inputs/. /data/local/tmp/qnn/inputs/
  "$ADB" shell chmod +x /data/local/tmp/qnn/qnn-net-run
  ```

  (`--layout nhwc --input-name image` is an example: use the values recorded in Step 2.)

- [ ] **Step 7: Measure latency and collect outputs, for both variants**

  ```bash
  RUN='cd /data/local/tmp/qnn && export LD_LIBRARY_PATH=/data/local/tmp/qnn && export ADSP_LIBRARY_PATH="/data/local/tmp/qnn;/vendor/lib/rfsa/adsp;/vendor/dsp/cdsp;/system/lib/rfsa/adsp;/dsp"'
  for V in w8a16 float; do
    "$ADB" shell "$RUN && ./qnn-net-run --backend libQnnHtp.so --retrieve_context depth_anything_v2_$V.ctx.bin --input_list inputs/input_list_bench.txt --output_dir bench_$V --perf_profile burst --profiling_level basic"
    "$ADB" shell "$RUN && ./qnn-net-run --backend libQnnHtp.so --retrieve_context depth_anything_v2_$V.ctx.bin --input_list inputs/input_list.txt --output_dir out_$V"
    "$ADB" pull /data/local/tmp/qnn/bench_$V Builds/phase1/
    "$ADB" pull /data/local/tmp/qnn/out_$V Builds/phase1/
  done
  ls Builds/phase1/bench_w8a16 Builds/phase1/out_w8a16 | head
  ```

  Then view the profiles:

  ```bash
  "$QNN_SDK_ROOT/bin/x86_64-windows-msvc/qnn-profile-viewer.exe" --input_log "$(ls Builds/phase1/bench_w8a16/*profiling*.log | head -1)" > Builds/phase1/profile_w8a16.txt
  "$QNN_SDK_ROOT/bin/x86_64-windows-msvc/qnn-profile-viewer.exe" --input_log "$(ls Builds/phase1/bench_float/*profiling*.log | head -1)" > Builds/phase1/profile_float.txt
  ```

  Expected:
  - `out_*` holds one `Result_<i>/` folder per frame, each with one `.raw` output.
  - `profile_*.txt` reports execute times.

  If the viewer lists per-inference execute times, take their median as p50. If it reports only an average, record the average and say so in the spike doc: Task 21 re-measures a true p50 through our plugin (deviation D7).

- [ ] **Step 8: Write `tools/phase1/analyse.py` and decide the parameterisation**

  ```python
  """Phase 1: is the network output affine in inverse depth or in depth, against the floor?

  For each dumped frame with a tracked floor, compute every content pixel's metric floor depth
  from meta.json (floor plane + inference camera), robustly fit both models on floor-like pixels,
  and report the median relative depth residual of each. Lower wins (spec §6). Run for each
  variant (w8a16, float) to compare quantisation damage.
  """
  import argparse
  import json
  from pathlib import Path

  import numpy as np

  SIZE = 518
  rng = np.random.default_rng(1)


  def quat_rotate(q, v):
      """Rotate vectors v (N,3) by quaternion q = (x, y, z, w). Same formula Unity uses."""
      x, y, z, w = q
      u = np.array([x, y, z])
      t = 2.0 * np.cross(u, v)
      return v + w * t + np.cross(u, t)


  def floor_depth(meta):
      fx, fy, cx, cy = meta["intrinsics"]
      uu, vv = np.meshgrid(np.arange(SIZE, dtype=np.float64), np.arange(SIZE, dtype=np.float64))
      ray = np.stack([(uu - cx) / fx, -(vv - cy) / fy, np.ones_like(uu)], -1).reshape(-1, 3)
      d = quat_rotate(meta["cameraRotationXYZW"], ray)
      up = np.array(meta["floorUp"])
      h = np.dot(np.array(meta["cameraPosition"]) - np.array(meta["floorPoint"]), up)
      denom = d @ up
      z = np.full(SIZE * SIZE, np.nan)
      ok = denom < -1e-4
      z[ok] = -h / denom[ok]          # dir has unit camera z, so this is the Z-depth
      x0, y0, w, hgt = meta["content"]
      mask = np.zeros((SIZE, SIZE), bool)
      mask[int(y0):int(y0 + hgt), int(x0):int(x0 + w)] = True
      z[~mask.reshape(-1)] = np.nan
      z[(z < 0.3) | (z > 10)] = np.nan
      return z


  def fit(d, z, inverse, iters=200, inlier=0.06):
      y = 1.0 / z if inverse else z
      best = None
      for _ in range(iters):
          i, j = rng.choice(len(d), 2, replace=False)
          if abs(d[i] - d[j]) < 1e-6:
              continue
          s = (y[i] - y[j]) / (d[i] - d[j])
          t = y[i] - s * d[i]
          pred = s * d + t
          with np.errstate(divide="ignore", invalid="ignore"):
              zp = 1.0 / pred if inverse else pred
          rel = np.abs(zp - z) / z
          n = np.sum(rel < inlier)
          if best is None or n > best[0]:
              best = (n, s, t)
      if best is None:
          return np.nan, np.nan, np.nan, 0
      _, s, t = best
      for _ in range(2):   # least-squares refits on inliers, as FloorAlignedDepth does
          pred = s * d + t
          with np.errstate(divide="ignore", invalid="ignore"):
              zp = 1.0 / pred if inverse else pred
          inl = np.abs(zp - z) / z < inlier
          if inl.sum() < 2:
              break
          A = np.stack([d[inl], np.ones(inl.sum())], 1)
          s, t = np.linalg.lstsq(A, y[inl], rcond=None)[0]
      pred = s * d + t
      with np.errstate(divide="ignore", invalid="ignore"):
          zp = 1.0 / pred if inverse else pred
      rel = np.abs(zp - z) / z
      inl = rel < inlier
      return s, t, float(np.median(rel[inl])) if inl.any() else np.nan, int(inl.sum())


  def main():
      ap = argparse.ArgumentParser()
      ap.add_argument("dumps")
      ap.add_argument("outputs", help="qnn-net-run output dir (Result_<i>/...raw)")
      ap.add_argument("frames", help="frames.txt from make_inputs.py (maps Result_i to frame)")
      ap.add_argument("--layout", choices=["nchw", "nhwc"], default="nchw", help="output layout; any is fine for 1 channel")
      a = ap.parse_args()
      frames = Path(a.frames).read_text().split()
      rows = []
      for i, name in enumerate(frames):
          meta = json.loads((Path(a.dumps) / name / "meta.json").read_text())
          if not meta["hasFloor"]:
              continue
          raws = list((Path(a.outputs) / f"Result_{i}").glob("*.raw"))
          if len(raws) != 1:
              print(name, "missing output"); continue
          d = np.fromfile(raws[0], dtype="<f4")
          if d.size != SIZE * SIZE:
              print(name, "unexpected output size", d.size); continue
          z = floor_depth(meta)
          ok = np.isfinite(z) & np.isfinite(d)
          if ok.sum() < 500:
              continue
          idx = np.flatnonzero(ok)[::4]
          inv = fit(d[idx], z[idx], True)
          lin = fit(d[idx], z[idx], False)
          rows.append((name, inv, lin))
          print(f"{name}: inverse res={inv[2]:.4f} n={inv[3]}  depth res={lin[2]:.4f} n={lin[3]}  s_inv={inv[0]:+.4g}")
      if not rows:
          print("no usable frames"); return
      inv_med = np.nanmedian([r[1][2] for r in rows])
      lin_med = np.nanmedian([r[2][2] for r in rows])
      print(f"\nframes={len(rows)}  median residual: AffineInverseDepth={inv_med:.4f}  AffineDepth={lin_med:.4f}")
      print("winner:", "AffineInverseDepth" if inv_med <= lin_med else "AffineDepth")


  if __name__ == "__main__":
      main()
  ```

  Run for both variants:

  ```bash
  $PY tools/phase1/analyse.py Builds/dumps/InferenceDumps Builds/phase1/out_w8a16 Builds/phase1/inputs/frames.txt | tee Builds/phase1/analyse_w8a16.txt
  $PY tools/phase1/analyse.py Builds/dumps/InferenceDumps Builds/phase1/out_float Builds/phase1/inputs/frames.txt | tee Builds/phase1/analyse_float.txt
  ```

  Also copy each output next to its frame for the PNG check, then rerun `dump_to_png.py`. The Python loop below copies `Result_i/*.raw` to `<frame>/out_w8a16.f32`:

  ```bash
  $PY - <<'EOF'
  from pathlib import Path; import shutil
  frames = Path("Builds/phase1/inputs/frames.txt").read_text().split()
  for v in ("w8a16", "float"):
      for i, n in enumerate(frames):
          r = list(Path(f"Builds/phase1/out_{v}/Result_{i}").glob("*.raw"))
          if r: shutil.copy(r[0], Path("Builds/dumps/InferenceDumps") / n / f"out_{v}.f32")
  EOF
  $PY tools/phase1/dump_to_png.py Builds/dumps/InferenceDumps
  ```

  Expected:
  - Both runs print a `winner:` line and two medians.
  - The `out_w8a16.f32.png` images show the corridor structure (walls, floor gradient).

- [ ] **Step 9: Record the results in `docs/phase1-runtime-spike.md` and decide**

  ```markdown
  # Phase 1 — runtime spike (OnePlus 13R, QAIRT 2.50.0.260828221209)

  - Date:
  - Licence clause permitting redistribution of libQnn* (file, section, quote):
  - Context-binary generation: Windows host / WSL (and any flag changes vs make-context.ps1):

  ## Model tensors (from qairt-dlc-info)
  | | name | dims | dtype | quantisation (scale, offset) |
  |---|---|---|---|---|
  | graph | | | | |
  | input | | | | |
  | output | | | | |

  ## Latency (qnn-net-run, burst, HTP v75)
  | variant | inferences | p50 or average (say which) | max |
  |---|---|---|---|
  | w8a16 | | | |
  | float | | | |

  ## Output parameterisation (analyse.py, frames with a tracked floor)
  | variant | frames | median residual: inverse depth | median residual: depth | winner |
  |---|---|---|---|---|

  ## Decisions
  - Gate p50 ≤ 35 ms: PASS / FAIL (w8a16: … ms)
  - Parameterisation: AffineInverseDepth / AffineDepth
  - Variant to ship: w8a16 / float. **Proposal for Rachit**: ship float if the w8a16 median residual exceeds 1.5× the float residual (spec §12 says "materially worse"; 1.5× is this plan's proposed threshold).
  ```

  Then apply the decisions:
  - **Gate FAIL** (p50 > 35 ms for both variants): stop and go to Task 23.
  - **Parameterisation = AffineDepth:**
    - Change `DetectionConfig.parameterisation`'s default to `DepthParameterisation.AffineDepth`.
    - Change the assertion in `ApiCompatibilityTests.DetectionDefaults_MatchSpecSections52And6` to match.
    - Run the test command. Expected: PASS.
  - **Variant = float:** in Task 18, change `QnnDepthInference.DefaultModelFileName` to `"depth_anything_v2_float.ctx.bin"`. Task 19's service field defaults to that constant, so nothing else changes. Note it here so Task 18's implementer sees it.
  - Copy the chosen context binary into the app:

    ```bash
    mkdir -p Assets/StreamingAssets/Models
    cp tools/.cache/ctx/depth_anything_v2_w8a16.ctx.bin Assets/StreamingAssets/Models/
    ```

    Git-ignored by Task 6. Use the float file if chosen.

- [ ] **Step 10: Commit**

  ```bash
  git add tools/phase1 docs/phase1-runtime-spike.md Assets/WallDistance
  git commit -m "Record Phase 1 runtime spike: QNN HTP latency and output parameterisation"
  ```

**Gate:** continue to Phase 2 only when the p50 gate passes and a parameterisation is recorded.

---

### Task 8: `SyntheticCorridor` test fixture

All Phase 2 algorithm tests render frames from this fixture. It ray-casts a parameterised corridor and gives:
- the exact metric Z-depth;
- a grey image;
- a network-style output with a known affine transform, noise and mild nonlinearity (spec §8).

**Files:**
- Create: `Assets/WallDistance/Tests/EditMode/SyntheticCorridor.cs`
- Test: `Assets/WallDistance/Tests/EditMode/SyntheticCorridorTests.cs`

**Interfaces:**
- Consumes: `InverseDepthImage`, `FloorPlane`, `DepthIntrinsics`, `DepthParameterisation`.
- Produces (tests assembly only):
  - `sealed class SyntheticCorridor`
    - fields: `width = 2`, `height = 2.6`, `startZ = −6`, `endWallZ = 5.5`, `door`, `doorZ0 = 2`, `doorZ1 = 3`, `doorDepth = 0.3`, `doorHeight = 2.1`, `skirtingHeight = 0.1`, `person`, `personCentre = (0.6, 0, 3)`, `groutLinesX` (`List<float>`), `groutWidth = 0.02`
    - luma constants: `WallLuma` 200, `FloorLuma` 110, `SkirtingLuma` 60, `CeilingLuma` 230, `EndLuma` 180, `PersonLuma` 90, `GroutLuma` 70
    - `Rendered Render(Pose pose, int size = 518, float vFovDeg = 67, float contentAspect = 0.75f)`, where `Rendered { InverseDepthImage image; float[] depth; }`
    - `static InverseDepthImage ToNetworkOutput(Rendered r, float scale = 2, float shift = 0.05f, float relativeNoise = 0, float quadratic = 0, int seed = 1, DepthParameterisation parameterisation = AffineInverseDepth)`
    - `static Pose Camera(float x = 0.2f, float h = 1.4f, float z = 0, float pitchDown = 20, float yaw = 0, float roll = 0)`
    - `static readonly FloorPlane Floor` (y = 0, up)

  Geometry: the corridor runs along +Z, floor y = 0, left wall x = −1, right wall x = +1. The default camera is therefore 1.2 m from the left wall and 0.8 m from the right.

- [ ] **Step 1: Write the failing self-tests**

  ```csharp
  using NUnit.Framework;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      /// <summary>The fixture is the ground truth for every algorithm test, so it is tested first.</summary>
      public class SyntheticCorridorTests
      {
          [Test]
          public void FloorPixel_DepthMatchesAnalyticFloorIntersection()
          {
              var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
              int u = 259, v = 450, i = v * 518 + u;
              Assert.IsTrue(SyntheticCorridor.Floor.TryIntersect(r.image.cameraPose.position, r.image.WorldRay(u, v), out _, out float t));
              Assert.AreEqual(t, r.depth[i], 1e-4f);
              Assert.AreEqual(SyntheticCorridor.FloorLuma, r.image.luma[i]);
          }

          [Test]
          public void Padding_IsNaNDepthAndBlack()
          {
              var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
              Assert.AreEqual(new RectInt(65, 0, 388, 518), r.image.content, "matches the device's portrait letterbox");
              Assert.IsTrue(float.IsNaN(r.depth[0]));
              Assert.AreEqual(0, r.image.luma[0]);
          }

          [Test]
          public void FacingRightWall_CentreDepthIs0_8()
          {
              var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera(pitchDown: 0f, yaw: 90f));
              Assert.AreEqual(0.8f, r.depth[259 * 518 + 259], 1e-4f);
          }

          [Test]
          public void DoorRecess_IsDeeperThanWall()
          {
              var pose = SyntheticCorridor.Camera(z: 2.5f, pitchDown: 0f, yaw: -90f);
              var plain = new SyntheticCorridor().Render(pose);
              var withDoor = new SyntheticCorridor { door = true }.Render(pose);
              int c = 259 * 518 + 259;
              Assert.AreEqual(1.2f, plain.depth[c], 1e-4f);
              Assert.AreEqual(1.5f, withDoor.depth[c], 1e-4f);
          }

          [Test]
          public void Skirting_IsDarkerThanWall_AndGroutIsDrawn()
          {
              var corridor = new SyntheticCorridor();
              corridor.groutLinesX.Add(0.8f);
              var r = corridor.Render(SyntheticCorridor.Camera(pitchDown: 45f, yaw: 90f));
              Assert.AreEqual(SyntheticCorridor.SkirtingLuma, LumaAt(r, new Vector3(1f, 0.05f, 0.2f)));
              Assert.AreEqual(SyntheticCorridor.WallLuma, LumaAt(r, new Vector3(1f, 0.5f, 0.2f)));
              var g = corridor.Render(SyntheticCorridor.Camera());
              Assert.AreEqual(SyntheticCorridor.GroutLuma, LumaAt(g, new Vector3(0.8f, 0f, 2f)));
          }

          [Test]
          public void NetworkOutput_IsAffineInInverseDepth()
          {
              var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
              var img = SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f);
              int i = 450 * 518 + 259;
              Assert.AreEqual((1f / r.depth[i] - 0.05f) / 2f, img.values[i], 1e-6f);
              Assert.IsTrue(float.IsNaN(img.values[0]));
          }

          static byte LumaAt(SyntheticCorridor.Rendered r, Vector3 world)
          {
              Assert.IsTrue(r.image.TryProject(world, out float u, out float v, out _));
              int iu = Mathf.RoundToInt(u), iv = Mathf.RoundToInt(v);
              Assert.IsTrue(r.image.InContent(iu, iv), $"({iu},{iv}) outside content");
              return r.image.luma[iv * r.image.width + iu];
          }
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  Run: `unity command run_tests --mode editor --filter WallDistance --filter_type assembly --timeout 240 --no-banner`

  Expected: compilation error, `'SyntheticCorridor' could not be found`.

- [ ] **Step 3: Implement `SyntheticCorridor.cs`**

  ```csharp
  using System;
  using System.Collections.Generic;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      /// <summary>
      /// Ray-cast corridor renderer: exact metric depth plus a grey image, so detection code can be
      /// tested against ground truth instead of eyeballed on a phone. Axis-aligned quads only;
      /// that is all a corridor needs and keeps the truth easy to reason about.
      ///
      /// Layout: corridor along +Z, floor y = 0, walls at x = ±width/2, end wall at endWallZ
      /// facing -Z. An optional door recess sits in the LEFT wall; an optional standing person
      /// is a box in front of the RIGHT wall.
      /// </summary>
      sealed class SyntheticCorridor
      {
          public float width = 2f, height = 2.6f, startZ = -6f, endWallZ = 5.5f;
          public bool door;
          public float doorZ0 = 2f, doorZ1 = 3f, doorDepth = 0.3f, doorHeight = 2.1f;
          public float skirtingHeight = 0.1f;
          public bool person;
          public Vector3 personCentre = new Vector3(0.6f, 0f, 3f);
          public float personHalfX = 0.2f, personHalfZ = 0.15f, personHeight = 1.7f;
          public readonly List<float> groutLinesX = new List<float>();
          public float groutWidth = 0.02f;

          public const byte WallLuma = 200, FloorLuma = 110, SkirtingLuma = 60, CeilingLuma = 230,
              EndLuma = 180, PersonLuma = 90, GroutLuma = 70;

          public static readonly FloorPlane Floor = new FloorPlane(Vector3.zero, Vector3.up);

          public float LeftX => -width * 0.5f;
          public float RightX => width * 0.5f;

          public sealed class Rendered
          {
              public InverseDepthImage image;
              /// <summary>True metric Z-depth per pixel; NaN in padding or where nothing is hit.</summary>
              public float[] depth;
          }

          enum Kind { Wall, Floor, Ceiling, End, Person }

          /// <summary>Axis-aligned rectangle. (a, b) are the other two axes: x-plane → (y, z); y-plane → (x, z); z-plane → (x, y).</summary>
          struct Quad
          {
              public int axis;
              public float coord, aMin, aMax, bMin, bMax;
              public Kind kind;
              public bool hasDoorHole;
          }

          public static Pose Camera(float x = 0.2f, float h = 1.4f, float z = 0f, float pitchDown = 20f, float yaw = 0f, float roll = 0f) =>
              new Pose(new Vector3(x, h, z), Quaternion.Euler(pitchDown, yaw, roll));

          public Rendered Render(Pose pose, int size = 518, float vFovDeg = 67f, float contentAspect = 0.75f)
          {
              var img = new InverseDepthImage(size, size);
              float f = 0.5f * size / Mathf.Tan(vFovDeg * 0.5f * Mathf.Deg2Rad);
              float c = (size - 1) * 0.5f;
              img.intrinsics = new DepthIntrinsics { fx = f, fy = f, cx = c, cy = c, width = size, height = size };
              img.cameraPose = pose;
              // 0.75 of 518 rounds (to even) to 388 → x 65..452, the same rectangle as the device's portrait letterbox.
              int cw = Mathf.RoundToInt(size * contentAspect);
              img.content = new RectInt((size - cw) / 2, 0, cw, size);

              var depth = new float[size * size];
              Array.Fill(depth, float.NaN);
              var quads = Build();
              for (int v = 0; v < size; v++)
              for (int u = 0; u < size; u++)
              {
                  if (!img.InContent(u, v)) continue;
                  int i = v * size + u;
                  // WorldRay has unit camera-Z, so the hit distance t IS the Z-depth.
                  if (Cast(quads, pose.position, img.WorldRay(u, v), out float t, out byte luma))
                  {
                      depth[i] = t;
                      img.luma[i] = luma;
                  }
              }
              return new Rendered { image = img, depth = depth };
          }

          /// <summary>
          /// Network stand-in: d = (y − shift)/scale with y = 1/z (or z), plus an optional quadratic
          /// distortion and multiplicative Gaussian noise. Alignment should recover s = scale, t = shift.
          /// </summary>
          public static InverseDepthImage ToNetworkOutput(Rendered r, float scale = 2f, float shift = 0.05f,
              float relativeNoise = 0f, float quadratic = 0f, int seed = 1,
              DepthParameterisation parameterisation = DepthParameterisation.AffineInverseDepth)
          {
              var rng = new System.Random(seed);
              var values = r.image.values;
              for (int i = 0; i < values.Length; i++)
              {
                  float z = r.depth[i];
                  if (float.IsNaN(z)) { values[i] = float.NaN; continue; }
                  float y = parameterisation == DepthParameterisation.AffineInverseDepth ? 1f / z : z;
                  float d = (y - shift) / scale;
                  d += quadratic * d * d;
                  if (relativeNoise > 0f) d *= 1f + relativeNoise * Gaussian(rng);
                  values[i] = d;
              }
              return r.image;
          }

          static float Gaussian(System.Random rng)
          {
              // Box–Muller; 1 - NextDouble() keeps log() away from 0.
              double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
              return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
          }

          List<Quad> Build()
          {
              float recess = door ? doorDepth : 0f;
              float xMin = LeftX - recess, xMax = RightX;
              var q = new List<Quad>
              {
                  new Quad { axis = 1, coord = 0f, aMin = xMin, aMax = xMax, bMin = startZ, bMax = endWallZ, kind = Kind.Floor },
                  new Quad { axis = 1, coord = height, aMin = xMin, aMax = xMax, bMin = startZ, bMax = endWallZ, kind = Kind.Ceiling },
                  new Quad { axis = 0, coord = LeftX, aMin = 0f, aMax = height, bMin = startZ, bMax = endWallZ, kind = Kind.Wall, hasDoorHole = door },
                  new Quad { axis = 0, coord = RightX, aMin = 0f, aMax = height, bMin = startZ, bMax = endWallZ, kind = Kind.Wall },
                  new Quad { axis = 2, coord = endWallZ, aMin = xMin, aMax = xMax, bMin = 0f, bMax = height, kind = Kind.End },
                  new Quad { axis = 2, coord = startZ, aMin = xMin, aMax = xMax, bMin = 0f, bMax = height, kind = Kind.End },
              };
              if (door)
              {
                  float back = LeftX - doorDepth;
                  q.Add(new Quad { axis = 0, coord = back, aMin = 0f, aMax = doorHeight, bMin = doorZ0, bMax = doorZ1, kind = Kind.Wall });
                  q.Add(new Quad { axis = 2, coord = doorZ0, aMin = back, aMax = LeftX, bMin = 0f, bMax = doorHeight, kind = Kind.Wall });
                  q.Add(new Quad { axis = 2, coord = doorZ1, aMin = back, aMax = LeftX, bMin = 0f, bMax = doorHeight, kind = Kind.Wall });
              }
              if (person)
              {
                  Vector3 p = personCentre;
                  float x0 = p.x - personHalfX, x1 = p.x + personHalfX, z0 = p.z - personHalfZ, z1 = p.z + personHalfZ;
                  q.Add(new Quad { axis = 0, coord = x0, aMin = 0f, aMax = personHeight, bMin = z0, bMax = z1, kind = Kind.Person });
                  q.Add(new Quad { axis = 0, coord = x1, aMin = 0f, aMax = personHeight, bMin = z0, bMax = z1, kind = Kind.Person });
                  q.Add(new Quad { axis = 2, coord = z0, aMin = x0, aMax = x1, bMin = 0f, bMax = personHeight, kind = Kind.Person });
                  q.Add(new Quad { axis = 2, coord = z1, aMin = x0, aMax = x1, bMin = 0f, bMax = personHeight, kind = Kind.Person });
                  q.Add(new Quad { axis = 1, coord = personHeight, aMin = x0, aMax = x1, bMin = z0, bMax = z1, kind = Kind.Person });
              }
              return q;
          }

          bool Cast(List<Quad> quads, Vector3 o, Vector3 d, out float best, out byte luma)
          {
              best = float.PositiveInfinity;
              int hit = -1;
              Vector3 hitPoint = default;
              const float eps = 1e-5f;
              for (int i = 0; i < quads.Count; i++)
              {
                  var q = quads[i];
                  float dn = d[q.axis];
                  if (Mathf.Abs(dn) < 1e-9f) continue;
                  float t = (q.coord - o[q.axis]) / dn;
                  if (t <= eps || t >= best) continue;
                  Vector3 p = o + d * t;
                  float a, b;
                  switch (q.axis)
                  {
                      case 0: a = p.y; b = p.z; break;
                      case 1: a = p.x; b = p.z; break;
                      default: a = p.x; b = p.y; break;
                  }
                  if (a < q.aMin - eps || a > q.aMax + eps || b < q.bMin - eps || b > q.bMax + eps) continue;
                  if (q.hasDoorHole && p.z > doorZ0 && p.z < doorZ1 && p.y < doorHeight) continue;
                  best = t;
                  hit = i;
                  hitPoint = p;
              }
              luma = 0;
              if (hit < 0) return false;
              luma = Shade(quads[hit].kind, hitPoint);
              return true;
          }

          byte Shade(Kind kind, Vector3 p)
          {
              switch (kind)
              {
                  case Kind.Floor:
                      foreach (float gx in groutLinesX)
                          if (Mathf.Abs(p.x - gx) < groutWidth * 0.5f) return GroutLuma;
                      return FloorLuma;
                  case Kind.Wall: return p.y < skirtingHeight ? SkirtingLuma : WallLuma;
                  case Kind.End: return p.y < skirtingHeight ? SkirtingLuma : EndLuma;
                  case Kind.Ceiling: return CeilingLuma;
                  default: return PersonLuma;
              }
          }
      }
  }
  ```

- [ ] **Step 4: Run the tests to verify they pass**

  Run the test command. Expected: PASS.

- [ ] **Step 5: Commit**

  ```bash
  git add Assets/WallDistance/Tests/EditMode
  git commit -m "Add SyntheticCorridor ray-cast fixture for detection tests"
  ```

---

### Task 9: `FloorAlignedDepth` and `MetricSampleCollector`

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/AlignmentResult.cs`
- Create: `Assets/WallDistance/Runtime/Core/FloorAlignedDepth.cs`
- Create: `Assets/WallDistance/Runtime/Core/MetricSampleCollector.cs`
- Test: `Assets/WallDistance/Tests/EditMode/FloorAlignedDepthTests.cs`

**Interfaces:**
- Consumes:
  - `InverseDepthImage`, `FloorPlane`, `DetectionConfig`, `DepthParameterisation`, `FailureReason`.
  - `DepthFrame.IsUsable`, `.DepthAt(u, v)`, `.ConfidenceAt(u, v)`, `.Unproject(int u, int v, float depthMeters)` (existing).
- Produces:
  - `struct MetricSample { Vector2 pixel; float depthMeters; }`. `pixel` is in the inference image; `depthMeters` is Z-depth in the inference camera.
  - `sealed class AlignmentResult`
    - fields: `success`, `failure`, `reason`, `parameterisation`, `s`, `t`, `residual`, `inliers`, `candidates`, `usedMetricSamples`, `hasFloorMask`, `bool[] floorMask`
    - `float MetricDepth(float d)`
  - `sealed class FloorAlignedDepth(DetectionConfig)`
    - `AlignmentResult Align(InverseDepthImage, FloorPlane, IReadOnlyList<MetricSample>)`. Returns its own reused result object, overwritten by the next call.
  - `static class MetricSampleCollector`
    - `int Collect(DepthFrame raw, InverseDepthImage img, byte minConfidence, double now, double maxAgeSeconds, int stride, List<MetricSample> output)`

- [ ] **Step 1: Write the failing tests**

  ```csharp
  using System.Collections.Generic;
  using NUnit.Framework;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class FloorAlignedDepthTests
      {
          static AlignmentResult Align(InverseDepthImage img, FloorPlane floor, IReadOnlyList<MetricSample> metric = null, DetectionConfig cfg = null) =>
              new FloorAlignedDepth(cfg ?? new DetectionConfig()).Align(img, floor, metric);

          [Test]
          public void RecoversScaleAndShift_Within1Percent_UnderNoise()
          {
              var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
              var img = SyntheticCorridor.ToNetworkOutput(r, scale: 2f, shift: 0.05f, relativeNoise: 0.01f, seed: 3);
              var a = Align(img, SyntheticCorridor.Floor);
              Assert.IsTrue(a.success, a.reason);
              Assert.AreEqual(2f, a.s, 0.02f, "1% of s");
              // t has no natural scale of its own; 0.004 m⁻¹ is 1% of the floor's typical inverse depth (~0.4 m⁻¹).
              Assert.AreEqual(0.05f, a.t, 0.004f);
              Assert.LessOrEqual(a.residual, 0.03f);
              Assert.GreaterOrEqual(a.inliers, 500);
              Assert.IsFalse(a.usedMetricSamples);
          }

          [Test]
          public void ExactData_RecoversExactly()
          {
              var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera()), 1.5f, 0.1f);
              var a = Align(img, SyntheticCorridor.Floor);
              Assert.IsTrue(a.success, a.reason);
              Assert.AreEqual(1.5f, a.s, 1e-3f);
              Assert.AreEqual(0.1f, a.t, 1e-3f);
              Assert.AreEqual(1f / (1.5f * 0.2f + 0.1f), a.MetricDepth(0.2f), 1e-4f);
          }

          [Test]
          public void FewerThan500FloorPixels_IsAlignmentFailed()
          {
              // Pitched 35° up: the bottom of the view is above the horizon, so no floor is visible.
              var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera(pitchDown: -35f)));
              var a = Align(img, SyntheticCorridor.Floor);
              Assert.IsFalse(a.success);
              Assert.AreEqual(FailureReason.AlignmentFailed, a.failure);
          }

          [Test]
          public void NoFloorAndNoMetric_IsNoFloor()
          {
              var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera()));
              var a = Align(img, default);
              Assert.IsFalse(a.success);
              Assert.AreEqual(FailureReason.NoFloor, a.failure);
          }

          [Test]
          public void NoFloor_With300ConfidentMetricSamples_Aligns()
          {
              var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
              var img = SyntheticCorridor.ToNetworkOutput(r, 2f, 0.05f);
              var rng = new System.Random(5);
              var samples = new List<MetricSample>();
              while (samples.Count < 300)
              {
                  int u = rng.Next(65, 453), v = rng.Next(0, 518);
                  float z = r.depth[v * 518 + u];
                  if (!float.IsNaN(z)) samples.Add(new MetricSample { pixel = new Vector2(u, v), depthMeters = z });
              }
              var a = Align(img, default, samples);
              Assert.IsTrue(a.success, a.reason);
              Assert.IsTrue(a.usedMetricSamples);
              Assert.AreEqual(2f, a.s, 0.02f);
              Assert.IsFalse(a.hasFloorMask, "no floor plane, so no floor mask");
          }

          [Test]
          public void AffineDepthMode_RecoversDepthParameterisation()
          {
              var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
              var img = SyntheticCorridor.ToNetworkOutput(r, 2f, 0.05f, parameterisation: DepthParameterisation.AffineDepth);
              var a = Align(img, SyntheticCorridor.Floor, cfg: new DetectionConfig { parameterisation = DepthParameterisation.AffineDepth });
              Assert.IsTrue(a.success, a.reason);
              Assert.AreEqual(2f, a.s, 0.02f);
              Assert.AreEqual(0.05f, a.t, 0.01f);
          }

          [Test]
          public void FloorMask_CoversFloor_NotWalls()
          {
              var r = new SyntheticCorridor().Render(SyntheticCorridor.Camera());
              var img = SyntheticCorridor.ToNetworkOutput(r);
              var a = Align(img, SyntheticCorridor.Floor);
              Assert.IsTrue(a.success && a.hasFloorMask);
              int floor = 0, floorMasked = 0, wall = 0, wallMasked = 0;
              for (int v = 0; v < 518; v++)
              for (int u = 65; u < 453; u++)
              {
                  int i = v * 518 + u;
                  if (float.IsNaN(r.depth[i])) continue;
                  bool isFloor = SyntheticCorridor.Floor.TryIntersect(img.cameraPose.position, img.WorldRay(u, v), out _, out float t)
                                 && Mathf.Abs(t - r.depth[i]) < 1e-3f * t;
                  if (isFloor) { floor++; if (a.floorMask[i]) floorMasked++; }
                  else { wall++; if (a.floorMask[i]) wallMasked++; }
              }
              Assert.Greater((float)floorMasked / floor, 0.95f);
              // Wall pixels within ~8 cm of the base are within the 6% tolerance of the floor behind them.
              Assert.Less((float)wallMasked / wall, 0.10f);
          }

          // ------------------------------------------------------------ MetricSampleCollector

          static InverseDepthImage InferenceCamera(Pose pose) => new InverseDepthImage(518, 518)
          {
              intrinsics = new DepthIntrinsics { fx = 391f, fy = 391f, cx = 258.5f, cy = 258.5f, width = 518, height = 518 },
              cameraPose = pose,
              content = new RectInt(0, 0, 518, 518),
          };

          [Test]
          public void Collector_MapsConfidentRawDepthIntoInferenceImage()
          {
              var pose = new Pose(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
              var raw = Fx.DepthForPlane(pose, new Vector3(0f, 0f, 2f), Vector3.back, timestamp: 1.0, confidence: 200);
              var list = new List<MetricSample>();
              int n = MetricSampleCollector.Collect(raw, InferenceCamera(pose), 128, 1.05, 0.5, 2, list);
              Assert.Greater(n, 1000);
              Assert.AreEqual(n, list.Count);
              foreach (var s in list) Assert.AreEqual(2f, s.depthMeters, 0.002f);
          }

          [Test]
          public void Collector_RejectsLowConfidence_Stale_AndMissingConfidence()
          {
              var pose = new Pose(new Vector3(0f, 1.4f, 0f), Quaternion.identity);
              var img = InferenceCamera(pose);
              var list = new List<MetricSample>();
              var low = Fx.DepthForPlane(pose, new Vector3(0f, 0f, 2f), Vector3.back, 1.0, confidence: 100);
              Assert.AreEqual(0, MetricSampleCollector.Collect(low, img, 128, 1.05, 0.5, 2, list), "below 0.5 confidence");
              var ok = Fx.DepthForPlane(pose, new Vector3(0f, 0f, 2f), Vector3.back, 1.0, confidence: 200);
              Assert.AreEqual(0, MetricSampleCollector.Collect(ok, img, 128, 2.0, 0.5, 2, list), "stale");
              ok.confidence = null;
              Assert.AreEqual(0, MetricSampleCollector.Collect(ok, img, 128, 1.05, 0.5, 2, list), "no confidence image");
              Assert.AreEqual(0, MetricSampleCollector.Collect(null, img, 128, 1.05, 0.5, 2, list), "no frame");
          }
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  Run the test command. Expected: compilation error, `'FloorAlignedDepth' could not be found`.

- [ ] **Step 3: Implement `AlignmentResult.cs`**

  ```csharp
  using System;
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>A metric depth sample in inference-image space (from confident ARCore raw depth).</summary>
      public struct MetricSample
      {
          public Vector2 pixel;
          /// <summary>Z-depth along the inference camera's optical axis, metres.</summary>
          public float depthMeters;
      }

      /// <summary>
      /// Outcome of fitting the network output to metric depth. Kept even on failure (s, t and the
      /// residual are logged in the CSV), but only a successful result may be used to make walls.
      /// </summary>
      public sealed class AlignmentResult
      {
          public bool success;
          public FailureReason failure;
          public string reason = "";
          public DepthParameterisation parameterisation;
          public float s = float.NaN, t = float.NaN;
          /// <summary>Median relative depth error of the inliers (0.03 = 3%).</summary>
          public float residual = float.NaN;
          public int inliers, candidates;
          public bool usedMetricSamples;
          /// <summary>True when <see cref="floorMask"/> is meaningful (a floor plane was available).</summary>
          public bool hasFloorMask;
          /// <summary>Per inference pixel: aligned depth agrees with the floor plane, so it is floor, not wall.</summary>
          public bool[] floorMask = Array.Empty<bool>();

          /// <summary>Metric Z-depth for a network value; NaN where the model gives a non-physical depth.</summary>
          public float MetricDepth(float d) => Predict(parameterisation, s, t, d);

          internal static float Predict(DepthParameterisation p, float s, float t, float d)
          {
              if (float.IsNaN(d)) return float.NaN;
              float y = s * d + t;
              if (p == DepthParameterisation.AffineInverseDepth) return y > 1e-4f ? 1f / y : float.NaN;
              return y > 0f ? y : float.NaN;
          }

          internal void Reset(int pixels, DepthParameterisation p)
          {
              success = false;
              failure = FailureReason.None;
              reason = "";
              parameterisation = p;
              s = t = residual = float.NaN;
              inliers = candidates = 0;
              usedMetricSamples = false;
              hasFloorMask = false;
              if (floorMask.Length != pixels) floorMask = new bool[pixels];
              else Array.Clear(floorMask, 0, pixels);
          }
      }
  }
  ```

- [ ] **Step 4: Implement `FloorAlignedDepth.cs`**

  ```csharp
  using System;
  using System.Collections.Generic;
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// Gives the network's relative depth a metric scale by fitting it to the tracked floor: every
      /// floor pixel's true Z-depth is known from the floor plane and the camera pose, so the fit is
      /// a robust line fit (spec §5.2). Without a usable floor, confident ARCore raw-depth samples
      /// stand in (spec §6); with neither, the frame is refused - never an assumed camera height.
      /// </summary>
      public sealed class FloorAlignedDepth
      {
          readonly DetectionConfig _cfg;
          readonly List<float> _d = new List<float>(20000), _z = new List<float>(20000), _rel = new List<float>(20000);
          readonly List<int> _inl = new List<int>(20000);

          public AlignmentResult Result { get; } = new AlignmentResult();

          public FloorAlignedDepth(DetectionConfig cfg) { _cfg = cfg ?? new DetectionConfig(); }

          public AlignmentResult Align(InverseDepthImage img, FloorPlane floor, IReadOnlyList<MetricSample> metric)
          {
              var r = Result;
              r.Reset(img.width * img.height, _cfg.parameterisation);
              _d.Clear();
              _z.Clear();

              bool floorUsable = floor.IsValid && floor.PlausibleCameraHeight(img.cameraPose.position);
              int minInliers = _cfg.minFloorInliers;
              if (floorUsable) CollectFloorPairs(img, floor);

              if (_d.Count < _cfg.minFloorInliers)
              {
                  int floorPairs = _d.Count;
                  bool metricOk = metric != null && metric.Count >= _cfg.minMetricSamples;
                  if (metricOk)
                  {
                      _d.Clear();
                      _z.Clear();
                      CollectMetricPairs(img, metric);
                      r.usedMetricSamples = true;
                      minInliers = _cfg.minMetricSamples;
                  }
                  if (!metricOk || _d.Count < _cfg.minMetricSamples)
                  {
                      r.failure = floorUsable ? FailureReason.AlignmentFailed : FailureReason.NoFloor;
                      r.reason = floorUsable
                          ? $"only {floorPairs} floor pixels in view (need {_cfg.minFloorInliers})"
                          : "no floor plane and too few confident raw-depth samples";
                      return r;
                  }
              }
              r.candidates = _d.Count;

              if (!Ransac(out float s, out float t))
              {
                  r.failure = FailureReason.AlignmentFailed;
                  r.reason = "no consistent depth scale";
                  return r;
              }
              // Two least-squares refits on the inlier set tighten the RANSAC estimate.
              for (int k = 0; k < 2; k++)
              {
                  CountInliers(s, t);
                  if (_inl.Count < 2 || !LeastSquares(out s, out t)) break;
              }
              CountInliers(s, t);
              r.s = s;
              r.t = t;
              r.inliers = _inl.Count;
              r.residual = Median(_rel);

              if (r.inliers < minInliers)
              {
                  r.failure = FailureReason.AlignmentFailed;
                  r.reason = $"{r.inliers} inliers (need {minInliers})";
                  return r;
              }
              if (!(r.residual <= _cfg.maxAlignResidual))
              {
                  r.failure = FailureReason.AlignmentFailed;
                  r.reason = $"floor residual {r.residual * 100f:F1}% (max {_cfg.maxAlignResidual * 100f:F0}%)";
                  return r;
              }

              r.success = true;
              r.reason = r.usedMetricSamples ? "scaled from raw depth" : "scaled from floor";
              if (floorUsable) BuildMask(img, floor, r);
              return r;
          }

          void CollectFloorPairs(InverseDepthImage img, FloorPlane floor)
          {
              int step = Mathf.Max(1, _cfg.alignStride);
              Vector3 o = img.cameraPose.position;
              for (int v = img.content.yMin; v < img.content.yMax; v += step)
              for (int u = img.content.xMin; u < img.content.xMax; u += step)
              {
                  float d = img.values[v * img.width + u];
                  if (float.IsNaN(d)) continue;
                  // WorldRay has unit camera-Z, so t from the floor intersection is the Z-depth.
                  if (!floor.TryIntersect(o, img.WorldRay(u, v), out _, out float z)) continue;
                  if (z < _cfg.floorMinDepthMeters || z > _cfg.floorMaxDepthMeters) continue;
                  _d.Add(d);
                  _z.Add(z);
              }
          }

          void CollectMetricPairs(InverseDepthImage img, IReadOnlyList<MetricSample> metric)
          {
              for (int i = 0; i < metric.Count; i++)
              {
                  int u = Mathf.RoundToInt(metric[i].pixel.x), v = Mathf.RoundToInt(metric[i].pixel.y);
                  if (!img.InContent(u, v)) continue;
                  float d = img.values[v * img.width + u];
                  float z = metric[i].depthMeters;
                  if (float.IsNaN(d) || !(z >= _cfg.floorMinDepthMeters && z <= _cfg.floorMaxDepthMeters)) continue;
                  _d.Add(d);
                  _z.Add(z);
              }
          }

          float Target(float z) => _cfg.parameterisation == DepthParameterisation.AffineInverseDepth ? 1f / z : z;

          bool Ransac(out float bestS, out float bestT)
          {
              // Fixed seed: identical frames give identical fits, which keeps tests and replays deterministic.
              var rng = new System.Random(17);
              int n = _d.Count, bestCount = 0;
              bestS = bestT = float.NaN;
              // Score on at most ~4000 pairs; the refits below use all of them.
              int stride = Mathf.Max(1, n / 4000);
              for (int it = 0; it < _cfg.alignRansacIterations; it++)
              {
                  int i = rng.Next(n), j = rng.Next(n);
                  float dd = _d[i] - _d[j];
                  if (Mathf.Abs(dd) < 1e-6f) continue;
                  float s = (Target(_z[i]) - Target(_z[j])) / dd;
                  float t = Target(_z[i]) - s * _d[i];
                  // Larger network values must mean nearer in inverse-depth mode; a negative scale is a degenerate pair.
                  if (_cfg.parameterisation == DepthParameterisation.AffineInverseDepth && s <= 0f) continue;
                  int count = 0;
                  for (int k = 0; k < n; k += stride)
                  {
                      float zp = AlignmentResult.Predict(_cfg.parameterisation, s, t, _d[k]);
                      if (Mathf.Abs(zp - _z[k]) <= _cfg.alignInlierRelative * _z[k]) count++;
                  }
                  if (count > bestCount) { bestCount = count; bestS = s; bestT = t; }
              }
              return bestCount >= 2;
          }

          void CountInliers(float s, float t)
          {
              _inl.Clear();
              _rel.Clear();
              for (int k = 0; k < _d.Count; k++)
              {
                  float zp = AlignmentResult.Predict(_cfg.parameterisation, s, t, _d[k]);
                  float rel = Mathf.Abs(zp - _z[k]) / _z[k];
                  if (rel <= _cfg.alignInlierRelative) { _inl.Add(k); _rel.Add(rel); }
              }
          }

          bool LeastSquares(out float s, out float t)
          {
              // Ordinary least squares of target(z) on d over the current inliers.
              double sd = 0, sy = 0, sdd = 0, sdy = 0;
              int n = _inl.Count;
              for (int i = 0; i < n; i++)
              {
                  int k = _inl[i];
                  double d = _d[k], y = Target(_z[k]);
                  sd += d; sy += y; sdd += d * d; sdy += d * y;
              }
              double den = n * sdd - sd * sd;
              s = t = float.NaN;
              if (Math.Abs(den) < 1e-12) return false;
              s = (float)((n * sdy - sd * sy) / den);
              t = (float)((sy - s * sd) / n);
              return true;
          }

          static float Median(List<float> v)
          {
              if (v.Count == 0) return float.NaN;
              v.Sort();
              return v[v.Count / 2];
          }

          void BuildMask(InverseDepthImage img, FloorPlane floor, AlignmentResult r)
          {
              r.hasFloorMask = true;
              Vector3 o = img.cameraPose.position;
              for (int v = img.content.yMin; v < img.content.yMax; v++)
              for (int u = img.content.xMin; u < img.content.xMax; u++)
              {
                  int i = v * img.width + u;
                  float z = r.MetricDepth(img.values[i]);
                  if (float.IsNaN(z)) continue;
                  if (!floor.TryIntersect(o, img.WorldRay(u, v), out _, out float zf)) continue;
                  r.floorMask[i] = Mathf.Abs(z - zf) <= _cfg.alignInlierRelative * zf;
              }
          }
      }
  }
  ```

- [ ] **Step 5: Implement `MetricSampleCollector.cs`**

  ```csharp
  using System.Collections.Generic;
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// Turns confident ARCore raw-depth pixels into metric samples in the inference image: the
      /// no-floor fallback for alignment (spec §6). Samples are re-projected through the inference
      /// camera, so their depth is that camera's Z-depth, the quantity the aligner fits.
      /// </summary>
      public static class MetricSampleCollector
      {
          public static int Collect(DepthFrame raw, InverseDepthImage img, byte minConfidence, double now,
              double maxAgeSeconds, int stride, List<MetricSample> output)
          {
              output.Clear();
              if (raw == null || !raw.IsUsable || now - raw.timestamp > maxAgeSeconds) return 0;
              // Without a confidence image there is no way to tell a measured return from a guess,
              // and on plain walls most raw values are guesses (spec §2). Refuse rather than trust them.
              if (raw.confidence == null || raw.confidence.Length < raw.width * raw.height) return 0;
              int step = Mathf.Max(1, stride);
              for (int v = 0; v < raw.height; v += step)
              for (int u = 0; u < raw.width; u += step)
              {
                  if (raw.ConfidenceAt(u, v) < minConfidence) continue;
                  float z = raw.DepthAt(u, v);
                  if (float.IsNaN(z)) continue;
                  Vector3 world = raw.Unproject(u, v, z);
                  if (!img.TryProject(world, out float pu, out float pv, out float pz)) continue;
                  if (!img.InContent(Mathf.RoundToInt(pu), Mathf.RoundToInt(pv))) continue;
                  output.Add(new MetricSample { pixel = new Vector2(pu, pv), depthMeters = pz });
              }
              return output.Count;
          }
      }
  }
  ```

- [ ] **Step 6: Run the tests to verify they pass**

  Run the test command. Expected: PASS.

  If `FloorMask_CoversFloor_NotWalls` fails only on the wall fraction, print the fraction first. It must be a base-band effect (pixels within ~8 cm of the base), not a wrong mask. Do not loosen the floor fraction.

- [ ] **Step 7: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Add floor-aligned depth scaling with raw-depth fallback and floor mask"
  ```

---

### Task 10: `WallObservation` and `VerticalPlaneExtractor`

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/WallObservation.cs`
- Create: `Assets/WallDistance/Runtime/Core/VerticalPlaneExtractor.cs`
- Test: `Assets/WallDistance/Tests/EditMode/VerticalPlaneExtractorTests.cs`

**Interfaces:**
- Consumes:
  - `AlignmentResult` (`success`, `hasFloorMask`, `floorMask`, `MetricDepth`), `InverseDepthImage`, `FloorPlane`, `Horizontal`, `DetectionConfig` (Tasks 2–9).
  - `WallCandidate` (existing).
- Produces:
  - `sealed class WallObservation`
    - fields: `origin`, `normal`, `up`, `extentMin`, `extentMax`, `inliers`, `rms`, `source`, `edgeSnapFraction`, `timestamp`
    - members: `Vector3 direction`, `float Length`, `float SignedDistance(Vector3)`, `Vector3 PointAt(float along)`, `WallObservation Clone()`
    - `static WallObservation FromArPlane(WallCandidate c, Vector3 up, double now, float maxTiltDeg = 10f)`. Returns null when the plane is not vertical.
  - `sealed class VerticalPlaneExtractor(DetectionConfig)`
    - `int Extract(InverseDepthImage img, AlignmentResult align, FloorPlane floor, double now, List<WallObservation> output)`

  Conventions shared by every later task:
  - `origin` lies on the wall's **base line**, at floor height, at the midpoint of the extent.
  - `normal` is horizontal and points from the wall towards the camera that saw it.
  - `direction = Cross(normal, up)`.
  - The extent is `[extentMin, extentMax]` along `direction`, relative to `origin`.

  These match `AssistedWallGeometry`, where a candidate built with `LookRotation(up, normal)` has local X = `direction`.

- [ ] **Step 1: Write the failing tests**

  ```csharp
  using System.Collections.Generic;
  using NUnit.Framework;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class VerticalPlaneExtractorTests
      {
          static List<WallObservation> Run(SyntheticCorridor c, Pose pose, float noise = 0f, int seed = 1)
          {
              var cfg = new DetectionConfig();
              var img = SyntheticCorridor.ToNetworkOutput(c.Render(pose), relativeNoise: noise, seed: seed);
              var align = new FloorAlignedDepth(cfg).Align(img, SyntheticCorridor.Floor, null);
              Assert.IsTrue(align.success, align.reason);
              var list = new List<WallObservation>();
              new VerticalPlaneExtractor(cfg).Extract(img, align, SyntheticCorridor.Floor, 0.0, list);
              return list;
          }

          /// <summary>Some observation faces <paramref name="normal"/> within maxDeg and is <paramref name="distance"/> from the camera within tol.</summary>
          static void AssertWall(List<WallObservation> obs, Vector3 normal, float distance, Vector3 cam, float maxDeg, float tol)
          {
              string seen = "";
              foreach (var o in obs)
              {
                  float ang = Vector3.Angle(o.normal, normal), dist = o.SignedDistance(cam);
                  seen += $"[n={o.normal} d={dist:F3} ang={ang:F2} len={o.Length:F2}] ";
                  if (ang <= maxDeg && Mathf.Abs(dist - distance) <= tol) return;
              }
              Assert.Fail($"no wall with normal {normal} at {distance} m; saw {seen}");
          }

          [Test]
          public void CleanCorridor_FindsLeftRightAndEndWalls()
          {
              var pose = SyntheticCorridor.Camera();
              var obs = Run(new SyntheticCorridor(), pose);
              Assert.LessOrEqual(obs.Count, 4);
              AssertWall(obs, Vector3.right, 1.2f, pose.position, 1f, 0.02f);   // left wall faces +x
              AssertWall(obs, Vector3.left, 0.8f, pose.position, 1f, 0.02f);    // right wall faces -x
              AssertWall(obs, Vector3.back, 5.5f, pose.position, 1f, 0.02f);    // end wall faces -z
              foreach (var o in obs)
              {
                  Assert.AreEqual(0f, Vector3.Dot(o.normal, Vector3.up), 1e-4f, "normals are horizontal");
                  Assert.AreEqual(0f, o.origin.y, 1e-4f, "origin is on the base line");
                  Assert.AreEqual(MeasurementSource.LearnedDepth, o.source);
              }
          }

          [Test]
          public void NoisyCorridor_StillWithin5cmAnd3Degrees()
          {
              // 3% depth noise is a plan-chosen stand-in for network error; spec §8 fixes only the clean-data bound.
              var pose = SyntheticCorridor.Camera();
              var obs = Run(new SyntheticCorridor(), pose, noise: 0.03f, seed: 9);
              AssertWall(obs, Vector3.right, 1.2f, pose.position, 3f, 0.05f);
              AssertWall(obs, Vector3.left, 0.8f, pose.position, 3f, 0.05f);
          }

          [Test]
          public void PersonInFrontOfRightWall_WallStillFound()
          {
              // Review Focus 2: a person standing still may add a short spurious segment, but must not
              // replace or shift the real right wall.
              var pose = SyntheticCorridor.Camera();
              var obs = Run(new SyntheticCorridor { person = true }, pose);
              AssertWall(obs, Vector3.left, 0.8f, pose.position, 1f, 0.02f);
          }

          [Test]
          public void DoorRecess_MainLeftWallStillFound()
          {
              var pose = SyntheticCorridor.Camera();
              var obs = Run(new SyntheticCorridor { door = true }, pose);
              AssertWall(obs, Vector3.right, 1.2f, pose.position, 1f, 0.02f);
          }

          [Test]
          public void FailedAlignment_ProducesNothing()
          {
              var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(SyntheticCorridor.Camera()));
              var list = new List<WallObservation> { null };
              int n = new VerticalPlaneExtractor(new DetectionConfig()).Extract(img, new AlignmentResult(), SyntheticCorridor.Floor, 0.0, list);
              Assert.AreEqual(0, n);
              Assert.AreEqual(0, list.Count, "output is cleared");
          }

          [Test]
          public void Observation_DirectionAndExtentConventions()
          {
              var o = new WallObservation
              {
                  origin = new Vector3(1f, 0f, 3f), normal = Vector3.left, up = Vector3.up, extentMin = -1f, extentMax = 2f,
              };
              Assert.AreEqual(Vector3.back, o.direction, "Cross(normal, up)");
              Assert.AreEqual(3f, o.Length, 1e-6f);
              Assert.AreEqual(new Vector3(1f, 0f, 1f), o.PointAt(2f));
              Assert.AreEqual(0.8f, o.SignedDistance(new Vector3(0.2f, 1.4f, 0f)), 1e-6f);
              var c = o.Clone();
              c.origin = Vector3.zero;
              Assert.AreEqual(new Vector3(1f, 0f, 3f), o.origin, "Clone is a copy");
          }
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  Run the test command. Expected: compilation error, `'WallObservation' could not be found`.

- [ ] **Step 3: Implement `WallObservation.cs`**

  ```csharp
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// One vertical wall segment seen in one frame (or one ARCore plane snapshot), described by
      /// its base line on the floor. Base line rather than plane centre because the base is what
      /// the edge refiner can confirm and what "distance to the wall" means for navigation.
      /// </summary>
      public sealed class WallObservation
      {
          /// <summary>On the base line at floor height, at the middle of the observed extent.</summary>
          public Vector3 origin;
          /// <summary>Horizontal unit normal pointing from the wall toward the observing camera.</summary>
          public Vector3 normal;
          public Vector3 up = Vector3.up;
          /// <summary>Observed extent along <see cref="direction"/>, relative to origin.</summary>
          public float extentMin, extentMax;
          public int inliers;
          /// <summary>RMS distance of supporting points from the fitted line, metres.</summary>
          public float rms;
          public MeasurementSource source = MeasurementSource.LearnedDepth;
          public float edgeSnapFraction = float.NaN;
          public double timestamp;

          /// <summary>Along-wall unit vector; same convention as a candidate built with LookRotation(up, normal).</summary>
          public Vector3 direction => Vector3.Cross(normal, up);
          public float Length => extentMax - extentMin;
          public float SignedDistance(Vector3 p) => Vector3.Dot(p - origin, normal);
          public Vector3 PointAt(float along) => origin + direction * along;
          public WallObservation Clone() => (WallObservation)MemberwiseClone();

          /// <summary>
          /// An ARCore vertical plane as an observation, so the map can fuse and cross-check it.
          /// Null when the plane tilts more than <paramref name="maxTiltDeg"/> from vertical.
          /// </summary>
          public static WallObservation FromArPlane(WallCandidate c, Vector3 up, double now, float maxTiltDeg = 10f)
          {
              if (c == null || c.boundary.Count < 3) return null;
              if (Mathf.Abs(Vector3.Dot(c.normal, up)) > Mathf.Sin(maxTiltDeg * Mathf.Deg2Rad)) return null;
              if (!Horizontal.TryDirection(c.normal, up, out Vector3 n)) return null;
              var o = new WallObservation
              {
                  normal = n, up = up, timestamp = now, source = MeasurementSource.PlaneOnly,
                  // ARCore gives no per-plane error; 2 cm is a nominal figure so plane observations
                  // weigh about the same as one good learned frame in the map.
                  rms = 0.02f, inliers = 1,
              };
              float baseLevel = float.PositiveInfinity;
              for (int i = 0; i < c.boundary.Count; i++)
                  baseLevel = Mathf.Min(baseLevel, Vector3.Dot(c.LocalToWorld(c.boundary[i]), up));
              Vector3 p0 = c.position;
              o.origin = p0 + up * (baseLevel - Vector3.Dot(p0, up));
              Vector3 dir = o.direction;
              float aMin = float.PositiveInfinity, aMax = float.NegativeInfinity;
              for (int i = 0; i < c.boundary.Count; i++)
              {
                  float a = Vector3.Dot(c.LocalToWorld(c.boundary[i]) - o.origin, dir);
                  aMin = Mathf.Min(aMin, a);
                  aMax = Mathf.Max(aMax, a);
              }
              float mid = 0.5f * (aMin + aMax);
              o.origin += dir * mid;
              o.extentMin = aMin - mid;
              o.extentMax = aMax - mid;
              return o;
          }
      }
  }
  ```

- [ ] **Step 4: Implement `VerticalPlaneExtractor.cs`**

  ```csharp
  using System.Collections.Generic;
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// Finds vertical walls in floor-aligned depth. A vertical plane projects onto the floor as a
      /// LINE, so the 3D plane search reduces to robust 2D line fitting on the floor projection
      /// (spec §5.2): cheaper, and verticality is built in rather than checked afterwards.
      /// People walking through view are rejected by the same test (non-planar, short).
      /// </summary>
      public sealed class VerticalPlaneExtractor
      {
          readonly DetectionConfig _cfg;
          readonly List<Vector2> _p2 = new List<Vector2>(20000);
          readonly List<float> _h = new List<float>(20000);
          readonly List<Vector3> _world = new List<Vector3>(20000);
          readonly List<int> _active = new List<int>(20000), _inl = new List<int>(20000);
          readonly List<float> _tmp = new List<float>(20000);
          bool[] _taken = new bool[0];

          public VerticalPlaneExtractor(DetectionConfig cfg) { _cfg = cfg ?? new DetectionConfig(); }

          public int Extract(InverseDepthImage img, AlignmentResult align, FloorPlane floor, double now, List<WallObservation> output)
          {
              output.Clear();
              if (align == null || !align.success) return 0;

              Vector3 up = floor.IsValid ? floor.up : Vector3.up;
              // Floor-plane basis: e1 along the camera's horizontal heading, e2 to its side.
              if (!Horizontal.TryDirection(img.cameraPose.rotation * Vector3.forward, up, out Vector3 e1)
                  && !Horizontal.TryDirection(img.cameraPose.rotation * Vector3.up, up, out e1))
                  e1 = Vector3.forward;
              Vector3 e2 = Vector3.Cross(up, e1);
              float floorLevel = floor.IsValid ? Vector3.Dot(floor.point, up) : float.NaN;

              CollectPoints(img, align, floor, up, e1, e2, floorLevel);
              if (_taken.Length < _p2.Count) _taken = new bool[_p2.Count];
              System.Array.Clear(_taken, 0, _p2.Count);
              _active.Clear();
              for (int i = 0; i < _p2.Count; i++) _active.Add(i);

              var rng = new System.Random(7);
              int found = 0;
              for (int attempt = 0; attempt < _cfg.maxPlanes * 2 && found < _cfg.maxPlanes && _active.Count >= _cfg.minPlaneInliers; attempt++)
              {
                  if (!RansacLine(rng, out Vector2 n, out float c)) break;
                  float thr = _cfg.planeInlierMeters, rms = 0f;
                  for (int k = 0; k < 2; k++)
                  {
                      Collect(n, c, thr);
                      if (_inl.Count < 3) break;
                      FitLine(out n, out c, out rms);
                      // Tighten to the data's own spread, but never below 2 cm (network noise) or above the RANSAC band.
                      thr = Mathf.Clamp(2.5f * rms, 0.02f, _cfg.planeInlierMeters);
                  }
                  Collect(n, c, thr);
                  // Consume these points whether or not they pass: a rejected line (ceiling strip,
                  // clutter) must not be found again by the next attempt.
                  RemoveInliers();
                  if (_inl.Count < _cfg.minPlaneInliers) continue;

                  var obs = Build(n, c, rms, up, e1, e2, floor, floorLevel, img.cameraPose.position, now);
                  if (obs == null) continue;
                  output.Add(obs);
                  found++;
              }
              return output.Count;
          }

          void CollectPoints(InverseDepthImage img, AlignmentResult align, FloorPlane floor, Vector3 up, Vector3 e1, Vector3 e2, float floorLevel)
          {
              _p2.Clear(); _h.Clear(); _world.Clear();
              int step = Mathf.Max(1, _cfg.extractStride);
              for (int v = img.content.yMin; v < img.content.yMax; v += step)
              for (int u = img.content.xMin; u < img.content.xMax; u += step)
              {
                  int i = v * img.width + u;
                  if (align.hasFloorMask && align.floorMask[i]) continue;
                  float z = align.MetricDepth(img.values[i]);
                  if (!(z >= _cfg.wallMinDepthMeters && z <= _cfg.wallMaxDepthMeters)) continue;   // also drops NaN
                  Vector3 p = img.WorldPoint(u, v, z);
                  float h = Vector3.Dot(p, up);
                  if (floor.IsValid && h - floorLevel < _cfg.floorClearanceMeters) continue;
                  _p2.Add(new Vector2(Vector3.Dot(p, e1), Vector3.Dot(p, e2)));
                  _h.Add(h);
                  _world.Add(p);
              }
          }

          bool RansacLine(System.Random rng, out Vector2 bestN, out float bestC)
          {
              int cnt = _active.Count, best = 0;
              bestN = default; bestC = 0f;
              // Score on a subsample of ≤ 2000 points; the refits and the final count use all of them.
              int stride = Mathf.Max(1, cnt / 2000);
              for (int it = 0; it < _cfg.planeRansacIterations; it++)
              {
                  Vector2 a = _p2[_active[rng.Next(cnt)]], b = _p2[_active[rng.Next(cnt)]];
                  Vector2 ab = b - a;
                  float len = ab.magnitude;
                  if (len < _cfg.minPairSeparationMeters) continue;
                  var n = new Vector2(-ab.y, ab.x) / len;
                  float c = Vector2.Dot(n, a);
                  int count = 0;
                  for (int k = 0; k < cnt; k += stride)
                      if (Mathf.Abs(Vector2.Dot(n, _p2[_active[k]]) - c) <= _cfg.planeInlierMeters) count++;
                  if (count > best) { best = count; bestN = n; bestC = c; }
              }
              return best * stride >= _cfg.minPlaneInliers;
          }

          void Collect(Vector2 n, float c, float thr)
          {
              _inl.Clear();
              for (int k = 0; k < _active.Count; k++)
              {
                  int i = _active[k];
                  if (Mathf.Abs(Vector2.Dot(n, _p2[i]) - c) <= thr) _inl.Add(i);
              }
          }

          void FitLine(out Vector2 n, out float c, out float rms)
          {
              // Total least squares (PCA): the line direction is the principal axis of the inliers.
              double mx = 0, my = 0;
              foreach (int i in _inl) { mx += _p2[i].x; my += _p2[i].y; }
              mx /= _inl.Count; my /= _inl.Count;
              double sxx = 0, sxy = 0, syy = 0;
              foreach (int i in _inl)
              {
                  double dx = _p2[i].x - mx, dy = _p2[i].y - my;
                  sxx += dx * dx; sxy += dx * dy; syy += dy * dy;
              }
              double theta = 0.5 * System.Math.Atan2(2 * sxy, sxx - syy);
              n = new Vector2((float)-System.Math.Sin(theta), (float)System.Math.Cos(theta));
              c = (float)(n.x * mx + n.y * my);
              double ss = 0;
              foreach (int i in _inl) { double r = Vector2.Dot(n, _p2[i]) - c; ss += r * r; }
              rms = (float)System.Math.Sqrt(ss / _inl.Count);
          }

          void RemoveInliers()
          {
              foreach (int i in _inl) _taken[i] = true;
              int w = 0;
              for (int k = 0; k < _active.Count; k++)
                  if (!_taken[_active[k]]) _active[w++] = _active[k];
              _active.RemoveRange(w, _active.Count - w);
          }

          WallObservation Build(Vector2 n, float c, float rms, Vector3 up, Vector3 e1, Vector3 e2,
              FloorPlane floor, float floorLevel, Vector3 cam, double now)
          {
              // A wall must span real height (rejects ceiling strips and low clutter) and real length.
              _tmp.Clear();
              foreach (int i in _inl) _tmp.Add(_h[i]);
              float h05 = Percentile(_tmp, 0.05f), h95 = Percentile(_tmp, 0.95f), h02 = Percentile(_tmp, 0.02f);
              if (h95 - h05 < _cfg.minHeightSpanMeters) return null;

              Vector3 normal = (n.x * e1 + n.y * e2).normalized;
              double cx = 0, cy = 0;
              foreach (int i in _inl) { cx += _p2[i].x; cy += _p2[i].y; }
              Vector3 linePoint = e1 * (float)(cx / _inl.Count) + e2 * (float)(cy / _inl.Count);
              // Face the camera; the dot ignores height because the normal is horizontal.
              if (Vector3.Dot(cam - linePoint, normal) < 0f) normal = -normal;
              // With a floor, the base IS the floor. Without one (raw-depth scale), use the lowest wall points.
              float baseLevel = floor.IsValid ? floorLevel : h02;

              var obs = new WallObservation
              {
                  normal = normal, up = up, origin = linePoint + up * baseLevel,
                  inliers = _inl.Count, rms = rms, source = MeasurementSource.LearnedDepth, timestamp = now,
              };
              Vector3 dir = obs.direction;
              _tmp.Clear();
              foreach (int i in _inl) _tmp.Add(Vector3.Dot(_world[i] - obs.origin, dir));
              float aMin = Percentile(_tmp, 0.02f), aMax = Percentile(_tmp, 0.98f);
              if (aMax - aMin < _cfg.minLengthMeters) return null;
              float mid = 0.5f * (aMin + aMax);
              obs.origin += dir * mid;
              obs.extentMin = aMin - mid;
              obs.extentMax = aMax - mid;
              return obs;
          }

          static float Percentile(List<float> v, float q)
          {
              v.Sort();
              return v[Mathf.Clamp(Mathf.RoundToInt(q * (v.Count - 1)), 0, v.Count - 1)];
          }
      }
  }
  ```

  (`Percentile` sorts `_tmp` in place; each call site refills `_tmp` first, and the three height percentiles are taken from the same sorted list.)

- [ ] **Step 5: Run the tests to verify they pass**

  Run the test command. Expected: PASS.

  If a wall is missing, print the `seen` list from `AssertWall`'s failure message:
  - A wall with the right normal but a 5–30 cm offset means `baseLevel` or the origin construction is wrong.
  - A missing wall with many short segments means the RANSAC band is too tight for the stride.

- [ ] **Step 6: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Add vertical wall extraction as RANSAC lines on the floor projection"
  ```

---

### Task 11: `BaseEdgeRefiner`

Learned depth is smooth at the wall–floor corner, so its base line is soft. This task sharpens it: the base line is snapped to the strongest nearby image edge in the luma image (spec §5.2). The search band is capped at ±8 cm on the floor (deviation D8), so a tile joint further away cannot capture the snap (Review Focus 1). When the base is not visible, the refiner declines and leaves the observation untouched (Review Focus 5).

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/BaseEdgeRefiner.cs`
- Test: `Assets/WallDistance/Tests/EditMode/BaseEdgeRefinerTests.cs`

**Interfaces:**
- Consumes: `InverseDepthImage` (`luma`, `content`, `TryProject`, `WorldRay`, `InContent`), `FloorPlane`, `WallObservation`, `DetectionConfig` (edge fields).
- Produces:
  - `sealed class BaseEdgeRefiner(DetectionConfig)`
  - `bool TryRefine(InverseDepthImage img, FloorPlane floor, WallObservation obs, out WallObservation refined)`
    - On success, `refined` is a new object with `source = FloorEdge`, `edgeSnapFraction` set, `rms` = edge-line RMS and `inliers` = snapped samples.
    - On failure, `refined` is the same `obs` instance, unchanged.

- [ ] **Step 1: Write the failing tests**

  ```csharp
  using NUnit.Framework;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class BaseEdgeRefinerTests
      {
          /// <summary>
          /// The corridor's right wall (x = 1, facing -x) as a learned observation, shifted toward
          /// the camera by <paramref name="offset"/> and rotated by <paramref name="angleDeg"/>.
          /// Extent covers z 2..5, the part a camera at z = 0 pitched 30° down sees with its base.
          /// </summary>
          static WallObservation RightWall(float offset, float angleDeg = 0f) => new WallObservation
          {
              origin = new Vector3(1f - offset, 0f, 3.5f),
              normal = Quaternion.AngleAxis(angleDeg, Vector3.up) * Vector3.left,
              up = Vector3.up, extentMin = -1.5f, extentMax = 1.5f,
              inliers = 500, rms = 0.02f, source = MeasurementSource.LearnedDepth,
          };

          static InverseDepthImage Image(SyntheticCorridor c, Pose pose) => c.Render(pose).image;

          static void AssertOnTrueBase(WallObservation r)
          {
              // The true base is x = 1 along the whole extent.
              for (float a = -1.4f; a <= 1.41f; a += 0.7f)
                  Assert.AreEqual(1f, r.PointAt(a).x, 0.015f, $"base at along={a}");
              Assert.LessOrEqual(Vector3.Angle(r.normal, Vector3.left), 0.5f);
          }

          [Test]
          public void PerturbedBase_SnapsToTrueBase([Values(-0.05f, 0.05f)] float offset)
          {
              var img = Image(new SyntheticCorridor(), SyntheticCorridor.Camera(pitchDown: 30f));
              var obs = RightWall(offset, angleDeg: 1f);
              Assert.IsTrue(new BaseEdgeRefiner(new DetectionConfig()).TryRefine(img, SyntheticCorridor.Floor, obs, out var r));
              AssertOnTrueBase(r);
              Assert.AreEqual(MeasurementSource.FloorEdge, r.source);
              Assert.GreaterOrEqual(r.edgeSnapFraction, 0.6f);
              Assert.AreEqual(obs.extentMin, r.extentMin);
              Assert.AreEqual(obs.extentMax, r.extentMax);
              Assert.AreEqual(MeasurementSource.LearnedDepth, obs.source, "input is not modified");
          }

          [Test]
          public void GroutLineParallelToWall_DoesNotCaptureSnap()
          {
              // Review Focus 1: a dark grout line 20 cm from the wall base. The learned base is 6 cm
              // short of the wall, i.e. 14 cm from the grout. The ±8 cm band (D8) excludes the grout;
              // with a plain ±12 px band it would be inside the band at this range.
              var corridor = new SyntheticCorridor();
              corridor.groutLinesX.Add(0.80f);
              var img = Image(corridor, SyntheticCorridor.Camera(pitchDown: 30f));
              Assert.IsTrue(new BaseEdgeRefiner(new DetectionConfig()).TryRefine(img, SyntheticCorridor.Floor, RightWall(0.06f), out var r));
              AssertOnTrueBase(r);
          }

          [Test]
          public void BaseOutOfView_RefinerDeclinesAndLeavesObservationUnchanged()
          {
              // Review Focus 5: level phone 0.4 m from the right wall; the lowest visible wall point
              // is ~1.1 m up, so there is no base edge to snap to.
              var img = Image(new SyntheticCorridor(), SyntheticCorridor.Camera(x: 0.6f, pitchDown: 0f, yaw: 90f));
              var obs = new WallObservation
              {
                  origin = new Vector3(1f, 0f, 0f), normal = Vector3.left, up = Vector3.up,
                  extentMin = -0.3f, extentMax = 0.3f, inliers = 400, rms = 0.015f, source = MeasurementSource.LearnedDepth,
              };
              Assert.IsFalse(new BaseEdgeRefiner(new DetectionConfig()).TryRefine(img, SyntheticCorridor.Floor, obs, out var r));
              Assert.AreSame(obs, r);
              Assert.AreEqual(new Vector3(1f, 0f, 0f), obs.origin);
              Assert.AreEqual(MeasurementSource.LearnedDepth, obs.source);
              Assert.IsTrue(float.IsNaN(obs.edgeSnapFraction));
          }

          [Test]
          public void NoFloor_Declines()
          {
              var img = Image(new SyntheticCorridor(), SyntheticCorridor.Camera(pitchDown: 30f));
              var obs = RightWall(0.05f);
              Assert.IsFalse(new BaseEdgeRefiner(new DetectionConfig()).TryRefine(img, default, obs, out var r));
              Assert.AreSame(obs, r);
          }
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  Run: `unity command run_tests --mode editor --filter WallDistance --filter_type assembly --timeout 240 --no-banner`

  Expected: compilation error, `'BaseEdgeRefiner' could not be found`.

- [ ] **Step 3: Implement `BaseEdgeRefiner.cs`**

  ```csharp
  using System.Collections.Generic;
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// Snaps a learned wall's base line to the image edge where wall meets floor. The network's
      /// depth is smooth across that corner, so its base is soft; the image edge is sharp, and on
      /// the tracked floor it converts directly back to metres (spec §5.2).
      ///
      /// For each sample along the base line, take the luma profile across the line, inside a
      /// band capped at ±edgeMaxShiftMeters on the floor (deviation D8). Walk in from the FLOOR
      /// side and take the first strong local gradient maximum: the first edge met from the floor
      /// is the base, while skirting tops and posters sit above it.
      /// </summary>
      public sealed class BaseEdgeRefiner
      {
          readonly DetectionConfig _cfg;
          readonly List<Vector2> _pts = new List<Vector2>(64);   // (along, offset) in the observation's floor frame
          readonly List<int> _keep = new List<int>(64);
          float[] _profile = new float[32], _grad = new float[32];

          public BaseEdgeRefiner(DetectionConfig cfg) { _cfg = cfg ?? new DetectionConfig(); }

          public bool TryRefine(InverseDepthImage img, FloorPlane floor, WallObservation obs, out WallObservation refined)
          {
              refined = obs;
              // Without a floor there is no metric back-projection for the edge, so nothing to refine.
              if (!floor.IsValid || obs == null || obs.Length <= 0f) return false;

              Vector3 up = floor.up, dir = obs.direction, n = obs.normal;
              // Put the base line exactly on the floor; with a floor-aligned observation it already is.
              Vector3 o = floor.Project(obs.origin);

              int count = Mathf.Clamp(Mathf.FloorToInt(obs.Length / _cfg.edgeSampleSpacingMeters) + 1, 2, _cfg.edgeMaxSamples);
              _pts.Clear();
              for (int s = 0; s < count; s++)
              {
                  float along = Mathf.Lerp(obs.extentMin, obs.extentMax, s / (float)(count - 1));
                  if (TrySnap(img, floor, o + dir * along, dir, n, out Vector3 hit))
                      _pts.Add(new Vector2(Vector3.Dot(hit - o, dir), Vector3.Dot(hit - o, n)));
              }
              float snapFraction = _pts.Count / (float)count;
              if (_pts.Count < 5 || snapFraction < _cfg.edgeMinSnapFraction) return false;

              // Robust line through the snapped floor points: fit, drop > 3 cm, refit.
              FitLine(_pts, null, out Vector2 c, out Vector2 axis, out _);
              _keep.Clear();
              for (int i = 0; i < _pts.Count; i++)
                  if (Mathf.Abs(Cross2(axis, _pts[i] - c)) <= 0.03f) _keep.Add(i);
              if (_keep.Count < 5) return false;
              FitLine(_pts, _keep, out c, out axis, out float rms);
              if (rms > _cfg.edgeMaxRmsMeters) return false;

              // Keep the observation's sense of direction so extents stay meaningful.
              if (axis.x < 0f) axis = -axis;
              float angle = Mathf.Abs(Mathf.Atan2(axis.y, axis.x)) * Mathf.Rad2Deg;
              if (angle > _cfg.edgeMaxAngleChangeDeg) return false;

              Vector3 newDir = (dir * axis.x + n * axis.y).normalized;
              Vector3 linePoint = o + dir * c.x + n * c.y;
              var r = obs.Clone();
              // normal = Cross(up, direction) inverts direction = Cross(normal, up) for horizontal vectors.
              r.normal = Vector3.Cross(up, newDir).normalized;
              // New origin: the point on the refined line closest to the old origin, so extents carry over.
              r.origin = linePoint + newDir * Vector3.Dot(o - linePoint, newDir);
              r.source = MeasurementSource.FloorEdge;
              r.edgeSnapFraction = snapFraction;
              r.rms = rms;
              r.inliers = _keep.Count;
              refined = r;
              return true;
          }

          /// <summary>Find the base edge near floor point <paramref name="p"/>; return it on the floor.</summary>
          bool TrySnap(InverseDepthImage img, FloorPlane floor, Vector3 p, Vector3 dir, Vector3 n, out Vector3 hit)
          {
              hit = default;
              const float probe = 0.05f;
              if (!img.TryProject(p, out float pu, out float pv, out _)) return false;
              if (!img.TryProject(p + dir * probe, out float tu, out float tv, out _)) return false;
              if (!img.TryProject(p + n * _cfg.edgeMaxShiftMeters, out float fu, out float fv, out _)) return false;
              if (!img.TryProject(p - n * _cfg.edgeMaxShiftMeters, out float wu, out float wv, out _)) return false;

              var P = new Vector2(pu, pv);
              Vector2 tan = new Vector2(tu, tv) - P;
              if (tan.sqrMagnitude < 1e-6f) return false;
              tan.Normalize();
              // Image normal pointing to the floor side (toward the camera, in front of the wall).
              var nrm = new Vector2(-tan.y, tan.x);
              Vector2 floorSide = new Vector2(fu, fv) - P;
              if (Vector2.Dot(nrm, floorSide) < 0f) nrm = -nrm;

              // Band: the SHORTER of the two ±8 cm reaches, capped at edgeBandPixels. Taking the
              // shorter keeps the band inside 8 cm on both sides (D8).
              float reach = Mathf.Min(Mathf.Abs(Vector2.Dot(floorSide, nrm)), Mathf.Abs(Vector2.Dot(new Vector2(wu, wv) - P, nrm)));
              int band = Mathf.Clamp(Mathf.CeilToInt(reach), 2, _cfg.edgeBandPixels);
              int len = 2 * band + 1;
              if (_profile.Length < len) { _profile = new float[len]; _grad = new float[len]; }

              // Profile f(k), k = -band..band (index k + band), averaged over three tangent offsets
              // to suppress single-pixel noise along the edge.
              for (int k = -band; k <= band; k++)
              {
                  float sum = 0f;
                  for (int j = -1; j <= 1; j++)
                  {
                      if (!TryLuma(img, P + nrm * k + tan * j, out float l)) return false;
                      sum += l;
                  }
                  _profile[k + band] = sum / 3f;
              }
              // g(i) = f(i+1) - f(i) is the step between profile index i and i+1.
              for (int i = 0; i < len - 1; i++) _grad[i] = Mathf.Abs(_profile[i + 1] - _profile[i]);

              // Walk from the floor end toward the wall; the first strong local maximum is the base.
              int found = -1;
              for (int i = len - 2; i >= 0; i--)
              {
                  float g = _grad[i];
                  if (g < _cfg.edgeMinContrast) continue;
                  float prev = i + 1 <= len - 2 ? _grad[i + 1] : 0f, next = i - 1 >= 0 ? _grad[i - 1] : 0f;
                  if (g >= prev && g >= next) { found = i; break; }
              }
              if (found < 0) return false;

              // Parabolic sub-pixel peak.
              float gm = found - 1 >= 0 ? _grad[found - 1] : 0f, g0 = _grad[found], gp = found + 1 <= len - 2 ? _grad[found + 1] : 0f;
              float den = gm - 2f * g0 + gp;
              float delta = Mathf.Abs(den) > 1e-6f ? Mathf.Clamp(0.5f * (gm - gp) / den, -0.5f, 0.5f) : 0f;
              // The step between indices found and found+1 sits at offset (found - band) + 0.5.
              float offset = found - band + 0.5f + delta;
              Vector2 q = P + nrm * offset;
              return floor.TryIntersect(img.cameraPose.position, img.WorldRay(q.x, q.y), out hit, out _);
          }

          static bool TryLuma(InverseDepthImage img, Vector2 p, out float value)
          {
              value = 0f;
              int x0 = Mathf.FloorToInt(p.x), y0 = Mathf.FloorToInt(p.y);
              // The 2×2 bilinear footprint must lie inside the camera content; padding is not image.
              if (!img.InContent(x0, y0) || !img.InContent(x0 + 1, y0 + 1)) return false;
              float fx = p.x - x0, fy = p.y - y0;
              int i = y0 * img.width + x0;
              float top = img.luma[i] + (img.luma[i + 1] - img.luma[i]) * fx;
              float bot = img.luma[i + img.width] + (img.luma[i + img.width + 1] - img.luma[i + img.width]) * fx;
              value = top + (bot - top) * fy;
              return true;
          }

          static float Cross2(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

          /// <summary>Total-least-squares line: centroid c, unit axis, RMS perpendicular residual.</summary>
          static void FitLine(List<Vector2> pts, List<int> idx, out Vector2 c, out Vector2 axis, out float rms)
          {
              int n = idx?.Count ?? pts.Count;
              c = Vector2.zero;
              for (int i = 0; i < n; i++) c += pts[idx?[i] ?? i];
              c /= n;
              float sxx = 0f, sxy = 0f, syy = 0f;
              for (int i = 0; i < n; i++)
              {
                  Vector2 d = pts[idx?[i] ?? i] - c;
                  sxx += d.x * d.x; sxy += d.x * d.y; syy += d.y * d.y;
              }
              float theta = 0.5f * Mathf.Atan2(2f * sxy, sxx - syy);
              axis = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));
              float ss = 0f;
              for (int i = 0; i < n; i++) { float r = Cross2(axis, pts[idx?[i] ?? i] - c); ss += r * r; }
              rms = Mathf.Sqrt(ss / n);
          }
      }
  }
  ```

- [ ] **Step 4: Run the tests to verify they pass**

  Run the test command. Expected: PASS.

  If `PerturbedBase_SnapsToTrueBase` fails by a constant ~1 px of floor distance (≈ 5 mm near, 1.5 cm far), the half-pixel term in `offset` has the wrong sign. Check it on one sample by logging `_profile` before changing any threshold.

- [ ] **Step 5: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Add base-edge refinement with a band capped at 8 cm on the floor"
  ```

---

### Task 12: `WallTrack` and `WallMap`

The map turns many noisy per-frame observations into a few stable walls (spec §5.2). Each wall is fused with an information filter over two scalars, its offset along the normal and its yaw. A wall is kept while it is out of view, so left/right readings survive turning the phone. The map is anchored through ARCore anchors, so drift corrections move the walls too.

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/WallTrack.cs`
- Create: `Assets/WallDistance/Runtime/Core/WallMap.cs`
- Modify: `Assets/WallDistance/Tests/EditMode/TestFixtures.cs` (add `Fx.Obs`)
- Test: `Assets/WallDistance/Tests/EditMode/WallMapTests.cs`

**Interfaces:**
- Consumes: `WallObservation` (Task 10), `Horizontal`, `MeasurementConfig` (`crossCheckToleranceMeters`, `detection.*`), `WallCandidate`.
- Produces:
  - `sealed class WallTrack`
    - fields:
      - geometry: `id`, `origin`, `normal`, `up`, `extentMin`, `extentMax`
      - filter state: `offsetInfo`, `angleInfo` (1/σ², metres and radians)
      - bookkeeping: `observations`, `firstSeen`, `lastSeen`, `lastSource`, `lastFloorEdgeTime`, `lastLearnedTime`, `lastArPlaneTime`, `lastLearnedPoint`, `lastArPlanePoint`, `crossChecked`, `edgeSnapFraction`
    - members: `direction`, `Length`, `SignedDistance(Vector3)`, `PointAt(float)`, `MeasurementSource Source(double now, float window)`
  - `sealed class WallMap(MeasurementConfig)`
    - state: `IReadOnlyList<WallTrack> Tracks`, `string SessionId`
    - `void SetSession(string)`, `void Clear()`
    - `WallTrack Observe(WallObservation obs, double now)`
    - `int Prune(double now)` (returns the number removed)
    - `IReadOnlyList<WallCandidate> Candidates(double now)` (reused list)
    - `bool ApplyAnchorPose(string id, Pose anchor)`, `void ForgetAnchor(string id)`
  - Test helper `Fx.Obs(...)` (tests assembly).

  Candidates use the same geometry convention as `AssistedWallGeometry`:
  - `rotation = LookRotation(up, normal)`, so local X = `direction`, local Y = `normal`, local Z = `up`;
  - the polygon is `(extentMin, 0), (extentMax, 0), (extentMax, H), (extentMin, H)` with H = `nominalWallHeightMeters`.

  So `WallGeometry` and the engine treat map walls exactly like AR planes.

- [ ] **Step 1: Add the observation builder to `TestFixtures.cs`**

  Inside `static class Fx`, after `DepthForPlane`, add:

  ```csharp

          /// <summary>
          /// A wall observation whose base line runs along z from <paramref name="z0"/> to <paramref name="z1"/>
          /// at x = <paramref name="x"/>. angleDeg = 0 faces -x (a RIGHT wall of a corridor along +z);
          /// angleDeg = 180 faces +x (a LEFT wall). Other angles yaw the wall about its origin.
          /// </summary>
          public static WallObservation Obs(float x, double t, float z0 = 0f, float z1 = 3f, float angleDeg = 0f,
              MeasurementSource src = MeasurementSource.LearnedDepth, float rms = 0.01f, int inliers = 500, float baseY = 0f)
          {
              float half = 0.5f * (z1 - z0);
              return new WallObservation
              {
                  origin = new Vector3(x, baseY, 0.5f * (z0 + z1)),
                  normal = Quaternion.AngleAxis(angleDeg, Vector3.up) * Vector3.left,
                  up = Vector3.up, extentMin = -half, extentMax = half,
                  rms = rms, inliers = inliers, source = src, timestamp = t,
              };
          }
  ```

- [ ] **Step 2: Write the failing tests**

  ```csharp
  using NUnit.Framework;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class WallMapTests
      {
          static WallMap NewMap()
          {
              var m = new WallMap(new MeasurementConfig());
              m.SetSession("s1");
              return m;
          }

          [Test]
          public void RepeatedNoisyObservations_ConvergeAndCapInformation()
          {
              var map = NewMap();
              var rng = new System.Random(11);
              for (int i = 0; i < 30; i++)
              {
                  // Triangular noise (sum of two uniforms): up to ±1.7 cm and ±0.85°, σ ≈ 0.7 cm and 0.35°.
                  float dx = 0.01f * (float)(rng.NextDouble() + rng.NextDouble() - 1.0) * 1.7f;
                  float da = 0.5f * (float)(rng.NextDouble() + rng.NextDouble() - 1.0) * 1.7f;
                  map.Observe(Fx.Obs(1f + dx, i * 0.1, angleDeg: da), i * 0.1);
              }
              Assert.AreEqual(1, map.Tracks.Count);
              var w = map.Tracks[0];
              // Once capped (after 4 frames), each frame gets weight 0.2, so the estimate's spread is
              // a third of the input's: ~0.23 cm and ~0.12°. The bounds below are > 5σ.
              Assert.AreEqual(1f, w.origin.x, 0.015f);
              Assert.LessOrEqual(Vector3.Angle(w.normal, Vector3.left), 0.6f);
              Assert.AreEqual(1f / (0.005f * 0.005f), w.offsetInfo, 1f, "offset information is capped");
              Assert.AreEqual(30, w.observations);
          }

          [Test]
          public void Association_OffsetAngleAndGapRules()
          {
              var a = NewMap(); a.Observe(Fx.Obs(1f, 0), 0); a.Observe(Fx.Obs(1.1f, 0.1), 0.1);
              Assert.AreEqual(1, a.Tracks.Count, "10 cm apart: same wall");

              var b = NewMap(); b.Observe(Fx.Obs(1f, 0), 0); b.Observe(Fx.Obs(1.3f, 0.1), 0.1);
              Assert.AreEqual(2, b.Tracks.Count, "30 cm apart: a recess or another wall");

              var c = NewMap(); c.Observe(Fx.Obs(1f, 0), 0); c.Observe(Fx.Obs(1f, 0.1, angleDeg: 12f), 0.1);
              Assert.AreEqual(2, c.Tracks.Count, "12° apart: different wall");

              var d = NewMap(); d.Observe(Fx.Obs(1f, 0, 0f, 3f), 0); d.Observe(Fx.Obs(1f, 0.1, 4f, 6f), 0.1);
              Assert.AreEqual(2, d.Tracks.Count, "1 m along-wall gap: separate segments");

              var e = NewMap(); e.Observe(Fx.Obs(1f, 0, 0f, 3f), 0); e.Observe(Fx.Obs(1f, 0.1, 3.3f, 5f), 0.1);
              Assert.AreEqual(1, e.Tracks.Count, "30 cm gap: same wall");
              Assert.AreEqual(5f, e.Tracks[0].Length, 1e-3f, "extent is the union");
          }

          [Test]
          public void Prune_DropsTracksUnseenFor30Seconds()
          {
              var map = NewMap();
              map.Observe(Fx.Obs(1f, 0), 0);
              map.Observe(Fx.Obs(-1f, 20, angleDeg: 180f), 20);
              Assert.AreEqual(0, map.Prune(29.9));
              Assert.AreEqual(1, map.Prune(30.5));
              Assert.AreEqual(1, map.Tracks.Count);
              // Component check: NUnit compares Vector3 with exact Equals, and AngleAxis(180) leaves ~1e-7 residue.
              Assert.AreEqual(1f, map.Tracks[0].normal.x, 1e-5f, "the left wall survived");
          }

          [Test]
          public void NewSession_ClearsTheMap()
          {
              var map = NewMap();
              map.Observe(Fx.Obs(1f, 0), 0);
              map.SetSession("s1");
              Assert.AreEqual(1, map.Tracks.Count, "same session keeps walls");
              map.SetSession("s2");
              Assert.AreEqual(0, map.Tracks.Count);
          }

          [Test]
          public void Base_KeepsTheLowestObservedLevel()
          {
              var map = NewMap();
              map.Observe(Fx.Obs(1f, 0, baseY: 0.3f, src: MeasurementSource.PlaneOnly), 0);
              map.Observe(Fx.Obs(1f, 0.1, baseY: 0f), 0.1);
              map.Observe(Fx.Obs(1f, 0.2, baseY: 0.2f, src: MeasurementSource.PlaneOnly), 0.2);
              Assert.AreEqual(0f, map.Tracks[0].origin.y, 1e-5f);
          }

          [Test]
          public void CrossCheck_NeedsBothSourcesWithin5cmAnd2Seconds()
          {
              var pass = NewMap();
              pass.Observe(Fx.Obs(1f, 0), 0);
              pass.Observe(Fx.Obs(1.03f, 0.5, src: MeasurementSource.PlaneOnly), 0.5);
              Assert.IsTrue(pass.Candidates(1.0)[0].crossChecked, "3 cm apart");
              Assert.IsFalse(pass.Candidates(2.6)[0].crossChecked, "learned observation older than 2 s");

              var fail = NewMap();
              fail.Observe(Fx.Obs(1f, 0), 0);
              fail.Observe(Fx.Obs(1.08f, 0.5, src: MeasurementSource.PlaneOnly), 0.5);
              Assert.AreEqual(1, fail.Tracks.Count, "8 cm still associates (≤ 15 cm)");
              Assert.IsFalse(fail.Candidates(1.0)[0].crossChecked, "8 cm apart");
          }

          [Test]
          public void Source_PrefersEdgeThenLearnedThenPlane_WithinWindow()
          {
              var map = NewMap();
              var w = map.Observe(Fx.Obs(1f, 0, src: MeasurementSource.PlaneOnly), 0);
              Assert.AreEqual(MeasurementSource.PlaneOnly, w.Source(0.1, 2f));
              map.Observe(Fx.Obs(1f, 0.2), 0.2);
              Assert.AreEqual(MeasurementSource.LearnedDepth, w.Source(0.3, 2f));
              map.Observe(Fx.Obs(1f, 0.4, src: MeasurementSource.FloorEdge), 0.4);
              Assert.AreEqual(MeasurementSource.FloorEdge, w.Source(0.5, 2f));
              map.Observe(Fx.Obs(1f, 3.0, src: MeasurementSource.PlaneOnly), 3.0);
              Assert.AreEqual(MeasurementSource.PlaneOnly, w.Source(3.1, 2f), "edge and learned are older than 2 s");
              Assert.AreEqual(MeasurementSource.PlaneOnly, w.Source(20.0, 2f), "nothing recent: last source");
          }

          [Test]
          public void Candidate_MatchesTrackGeometry()
          {
              var map = NewMap();
              var w = map.Observe(Fx.Obs(1f, 0, 2f, 5f), 0);
              var c = map.Candidates(0.1)[0];
              Assert.AreEqual(w.id, c.id);
              Assert.IsTrue(c.isTracked);
              Assert.AreEqual(MeasurementSource.LearnedDepth, c.source);
              Assert.AreEqual(Vector3.left.x, c.normal.x, 1e-5f);
              Assert.AreEqual(0.8f, WallGeometry.PerpendicularDistance(c, new Vector3(0.2f, 1.4f, 3f)), 1e-4f);
              Assert.AreEqual(3f * 2.4f, c.Area(), 1e-3f, "length × nominal height");
              Assert.AreEqual(1f, c.LocalToWorld(c.boundary[0]).x, 1e-4f);
              Assert.AreEqual(0f, c.LocalToWorld(c.boundary[0]).y, 1e-4f, "polygon starts at the base");
          }

          [Test]
          public void AnchorPose_AppliesOnlyTheDeltaSinceTheLastCall()
          {
              var map = NewMap();
              var w = map.Observe(Fx.Obs(1f, 0), 0);
              var anchor = new Pose(new Vector3(1f, 0f, 1.5f), Quaternion.identity);
              Assert.IsTrue(map.ApplyAnchorPose(w.id, anchor), "first call stores the reference");
              Assert.AreEqual(1f, w.origin.x, 1e-5f);
              Assert.IsTrue(map.ApplyAnchorPose(w.id, new Pose(anchor.position + new Vector3(0.05f, 0f, 0f), Quaternion.identity)));
              Assert.AreEqual(1.05f, w.origin.x, 1e-5f, "ARCore moved the anchor 5 cm, so the wall moves 5 cm");
              Assert.IsFalse(map.ApplyAnchorPose("nope", anchor));
          }

          [Test]
          public void FromArPlane_VerticalPlaneBecomesPlaneOnlyObservation()
          {
              var c = Fx.WallAtZ(2f, halfWidth: 1f, halfHeight: 1f, yCenter: 1.2f);
              var o = WallObservation.FromArPlane(c, Vector3.up, 5.0);
              Assert.IsNotNull(o);
              Assert.AreEqual(MeasurementSource.PlaneOnly, o.source);
              Assert.AreEqual(0.2f, o.origin.y, 1e-4f, "base = lowest boundary point");
              Assert.AreEqual(2f, o.Length, 1e-4f);
              Assert.AreEqual(2f, o.SignedDistance(Vector3.zero), 1e-4f, "normal faces the camera at the origin");
              var floorLike = new WallCandidate();
              floorLike.Set("f", true, Vector3.zero, Quaternion.identity, new[] { Vector2.zero, Vector2.right, Vector2.up });
              Assert.IsNull(WallObservation.FromArPlane(floorLike, Vector3.up, 5.0));
          }
      }
  }
  ```

- [ ] **Step 3: Run the tests to verify they fail**

  Run the test command. Expected: compilation error, `'WallMap' could not be found`.

- [ ] **Step 4: Implement `WallTrack.cs`**

  ```csharp
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// One fused wall in the map. Geometry follows WallObservation: origin on the base line at
      /// the extent midpoint, horizontal normal toward where it was seen from, direction = Cross(normal, up).
      /// </summary>
      public sealed class WallTrack
      {
          public readonly string id;
          public Vector3 origin, normal, up = Vector3.up;
          public float extentMin, extentMax;
          /// <summary>Information (1/σ²) of the offset along the normal, 1/m².</summary>
          public float offsetInfo;
          /// <summary>Information (1/σ²) of the yaw about up, 1/rad².</summary>
          public float angleInfo;
          public int observations;
          public double firstSeen, lastSeen;
          public MeasurementSource lastSource;
          public double lastFloorEdgeTime = double.NaN, lastLearnedTime = double.NaN, lastArPlaneTime = double.NaN;
          /// <summary>Where the latest learned (or edge) and AR-plane observations put the base, near this track's origin.</summary>
          public Vector3 lastLearnedPoint, lastArPlanePoint;
          /// <summary>Learned and ARCore sources agreed recently; refreshed by WallMap.Candidates/Prune.</summary>
          public bool crossChecked;
          public float edgeSnapFraction = float.NaN;

          internal bool hasAnchorReference;
          internal Pose anchorReference;

          public WallTrack(string id) { this.id = id; }

          public Vector3 direction => Vector3.Cross(normal, up);
          public float Length => extentMax - extentMin;
          public float SignedDistance(Vector3 p) => Vector3.Dot(p - origin, normal);
          public Vector3 PointAt(float along) => origin + direction * along;

          /// <summary>
          /// The best source that observed this wall within <paramref name="window"/> seconds:
          /// edge-confirmed beats learned beats ARCore plane. Falls back to the last source when
          /// nothing is recent (the wall is being remembered, not seen).
          /// </summary>
          public MeasurementSource Source(double now, float window)
          {
              if (Recent(lastFloorEdgeTime, now, window)) return MeasurementSource.FloorEdge;
              if (Recent(lastLearnedTime, now, window)) return MeasurementSource.LearnedDepth;
              if (Recent(lastArPlaneTime, now, window)) return MeasurementSource.PlaneOnly;
              return lastSource;
          }

          internal static bool Recent(double t, double now, float window) => !double.IsNaN(t) && now - t <= window;
      }
  }
  ```

- [ ] **Step 5: Implement `WallMap.cs`**

  ```csharp
  using System.Collections.Generic;
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// World-anchored memory of walls (spec §5.2). Observations are associated with an existing
      /// track by angle, offset and along-wall gap, then fused with a two-scalar information
      /// filter (offset along the normal, yaw about up). Information is capped so a long-seen
      /// wall still follows anchor corrections and real changes, rather than freezing.
      /// </summary>
      public sealed class WallMap
      {
          readonly MeasurementConfig _cfg;
          readonly List<WallTrack> _tracks = new List<WallTrack>();
          readonly List<WallCandidate> _candidates = new List<WallCandidate>();
          readonly List<WallCandidate> _candidatePool = new List<WallCandidate>();
          readonly Vector2[] _poly = new Vector2[4];
          int _nextId;

          public IReadOnlyList<WallTrack> Tracks => _tracks;
          public string SessionId { get; private set; }

          public WallMap(MeasurementConfig cfg) { _cfg = cfg ?? new MeasurementConfig(); }

          DetectionConfig D => _cfg.detection;

          /// <summary>A new AR session is a new coordinate frame; walls from the old one are meaningless.</summary>
          public void SetSession(string sessionId)
          {
              if (sessionId == SessionId) return;
              SessionId = sessionId;
              Clear();
          }

          // Ids keep counting across sessions so a stale id can never alias a new wall.
          public void Clear() => _tracks.Clear();

          public WallTrack Observe(WallObservation obs, double now)
          {
              if (obs == null) return null;
              WallTrack best = null;
              float bestOffset = float.PositiveInfinity;
              foreach (var t in _tracks)
              {
                  if (Vector3.Angle(t.normal, obs.normal) > D.associateMaxAngleDeg) continue;
                  float off = Mathf.Abs(t.SignedDistance(obs.origin));
                  if (off > D.associateMaxOffsetMeters) continue;
                  if (AlongGap(t, obs) > D.associateMaxGapMeters) continue;
                  if (off < bestOffset) { bestOffset = off; best = t; }
              }
              if (best == null) best = Create(obs, now);
              else Fuse(best, obs);
              Record(best, obs, now);
              return best;
          }

          WallTrack Create(WallObservation obs, double now)
          {
              var t = new WallTrack("w" + _nextId++)
              {
                  origin = obs.origin, normal = obs.normal, up = obs.up,
                  extentMin = obs.extentMin, extentMax = obs.extentMax,
                  offsetInfo = Mathf.Min(OffsetInfo(obs), MaxOffsetInfo),
                  angleInfo = Mathf.Min(AngleInfo(obs), MaxAngleInfo),
                  firstSeen = now,
              };
              _tracks.Add(t);
              return t;
          }

          float MaxOffsetInfo => 1f / (D.maxOffsetInfoSigmaMeters * D.maxOffsetInfoSigmaMeters);
          float MaxAngleInfo { get { float s = D.maxAngleInfoSigmaDeg * Mathf.Deg2Rad; return 1f / (s * s); } }

          /// <summary>Offset information of one observation: σ² = rms²/N, floored at the minimum σ.</summary>
          float OffsetInfo(WallObservation o)
          {
              float var = o.rms * o.rms / Mathf.Max(1, o.inliers);
              return 1f / Mathf.Max(var, D.minOffsetSigmaMeters * D.minOffsetSigmaMeters);
          }

          /// <summary>
          /// Yaw information: a line fitted to N points spread over length L has slope variance
          /// ≈ 12·σ²/(N·L²). Floored, because points within a frame are correlated.
          /// </summary>
          float AngleInfo(WallObservation o)
          {
              float L = Mathf.Max(o.Length, 0.1f);
              float var = 12f * o.rms * o.rms / (Mathf.Max(1, o.inliers) * L * L);
              float min = D.minAngleSigmaDeg * Mathf.Deg2Rad;
              return 1f / Mathf.Max(var, min * min);
          }

          void Fuse(WallTrack t, WallObservation obs)
          {
              // Yaw first: rotate the track normal about up toward the observation's.
              float iObsA = AngleInfo(obs);
              float theta = Vector3.SignedAngle(t.normal, obs.normal, t.up) * Mathf.Deg2Rad;
              float wA = iObsA / (t.angleInfo + iObsA);
              t.normal = Quaternion.AngleAxis(wA * theta * Mathf.Rad2Deg, t.up) * t.normal;
              if (Horizontal.TryDirection(t.normal, t.up, out Vector3 n)) t.normal = n;
              t.angleInfo = Mathf.Min(t.angleInfo + iObsA, MaxAngleInfo);

              // Then offset: where the observation's line passes the track origin, along the new normal.
              float iObsD = OffsetInfo(obs);
              float delta = -obs.SignedDistance(t.origin) * Vector3.Dot(obs.normal, t.normal);
              float wD = iObsD / (t.offsetInfo + iObsD);
              t.origin += t.normal * (wD * delta);
              t.offsetInfo = Mathf.Min(t.offsetInfo + iObsD, MaxOffsetInfo);

              // Base: keep the lowest level seen (AR planes rarely reach the floor; learned bases do).
              float trackLevel = Vector3.Dot(t.origin, t.up), obsLevel = Vector3.Dot(obs.origin, t.up);
              if (obsLevel < trackLevel) t.origin += t.up * (obsLevel - trackLevel);

              // Extent: union, then re-centre the origin on it.
              Vector3 dir = t.direction;
              float a0 = Vector3.Dot(obs.PointAt(obs.extentMin) - t.origin, dir);
              float a1 = Vector3.Dot(obs.PointAt(obs.extentMax) - t.origin, dir);
              float lo = Mathf.Min(t.extentMin, Mathf.Min(a0, a1)), hi = Mathf.Max(t.extentMax, Mathf.Max(a0, a1));
              float mid = 0.5f * (lo + hi);
              t.origin += dir * mid;
              t.extentMin = lo - mid;
              t.extentMax = hi - mid;
          }

          void Record(WallTrack t, WallObservation obs, double now)
          {
              t.observations++;
              t.lastSeen = now;
              t.lastSource = obs.source;
              // Foot of the track origin on the observation's line, used for the cross-check.
              Vector3 foot = t.origin - obs.normal * obs.SignedDistance(t.origin);
              switch (obs.source)
              {
                  case MeasurementSource.FloorEdge:
                      t.lastFloorEdgeTime = now;
                      t.lastLearnedTime = now;
                      t.lastLearnedPoint = foot;
                      t.edgeSnapFraction = obs.edgeSnapFraction;
                      break;
                  case MeasurementSource.LearnedDepth:
                      t.lastLearnedTime = now;
                      t.lastLearnedPoint = foot;
                      break;
                  case MeasurementSource.PlaneOnly:
                      t.lastArPlaneTime = now;
                      t.lastArPlanePoint = foot;
                      break;
              }
          }

          /// <summary>Gap between the observation's extent and the track's, along the track (0 when they overlap).</summary>
          static float AlongGap(WallTrack t, WallObservation obs)
          {
              Vector3 dir = t.direction;
              float a0 = Vector3.Dot(obs.PointAt(obs.extentMin) - t.origin, dir);
              float a1 = Vector3.Dot(obs.PointAt(obs.extentMax) - t.origin, dir);
              float lo = Mathf.Min(a0, a1), hi = Mathf.Max(a0, a1);
              return Mathf.Max(0f, Mathf.Max(lo - t.extentMax, t.extentMin - hi));
          }

          void RefreshCrossChecks(double now)
          {
              foreach (var t in _tracks)
              {
                  // An independent ARCore plane and a learned observation both recent and within 5 cm.
                  t.crossChecked = WallTrack.Recent(t.lastLearnedTime, now, D.sourceWindowSeconds)
                                   && WallTrack.Recent(t.lastArPlaneTime, now, D.sourceWindowSeconds)
                                   && Mathf.Abs(Vector3.Dot(t.lastLearnedPoint - t.lastArPlanePoint, t.normal)) <= _cfg.crossCheckToleranceMeters;
              }
          }

          public int Prune(double now)
          {
              int removed = _tracks.RemoveAll(t => now - t.lastSeen > D.trackMaxAgeSeconds);
              RefreshCrossChecks(now);
              return removed;
          }

          /// <summary>
          /// Map walls as WallCandidates, so the existing engine measures them exactly like AR planes.
          /// The list and its objects are reused; copy anything you keep past the next call.
          /// </summary>
          public IReadOnlyList<WallCandidate> Candidates(double now)
          {
              RefreshCrossChecks(now);
              _candidates.Clear();
              while (_candidatePool.Count < _tracks.Count) _candidatePool.Add(new WallCandidate());
              float h = D.nominalWallHeightMeters;
              for (int i = 0; i < _tracks.Count; i++)
              {
                  var t = _tracks[i];
                  _poly[0] = new Vector2(t.extentMin, 0f);
                  _poly[1] = new Vector2(t.extentMax, 0f);
                  _poly[2] = new Vector2(t.extentMax, h);
                  _poly[3] = new Vector2(t.extentMin, h);
                  var c = _candidatePool[i];
                  // LookRotation(up, normal): local X = direction, local Y = normal, local Z = up.
                  c.Set(t.id, true, t.origin, Quaternion.LookRotation(t.up, t.normal), _poly);
                  c.source = t.Source(now, D.sourceWindowSeconds);
                  c.crossChecked = t.crossChecked;
                  _candidates.Add(c);
              }
              return _candidates;
          }

          /// <summary>
          /// Feed the tracked pose of this wall's ARAnchor. The first call stores a reference; later
          /// calls move the wall by the anchor's motion since the previous call (ARCore drift correction).
          /// </summary>
          public bool ApplyAnchorPose(string id, Pose anchor)
          {
              var t = Find(id);
              if (t == null) return false;
              if (t.hasAnchorReference)
              {
                  Quaternion dq = anchor.rotation * Quaternion.Inverse(t.anchorReference.rotation);
                  t.origin = anchor.position + dq * (t.origin - t.anchorReference.position);
                  // Keep the wall vertical even if the anchor's correction carries a tiny tilt.
                  if (Horizontal.TryDirection(dq * t.normal, t.up, out Vector3 n)) t.normal = n;
              }
              t.anchorReference = anchor;
              t.hasAnchorReference = true;
              return true;
          }

          public void ForgetAnchor(string id)
          {
              var t = Find(id);
              if (t != null) t.hasAnchorReference = false;
          }

          WallTrack Find(string id)
          {
              foreach (var t in _tracks) if (t.id == id) return t;
              return null;
          }
      }
  }
  ```

  Check the sign in `Fuse`: `obs.SignedDistance(t.origin)` is how far the track origin lies in front of the observed line. The observed line is therefore that far *behind* the track origin, along `obs.normal`, hence the minus.

- [ ] **Step 6: Run the tests to verify they pass**

  Run the test command. Expected: PASS.

  `FromArPlane_VerticalPlaneBecomesPlaneOnlyObservation` uses `Fx.WallAtZ`, whose normal faces −Z. Its base is `yCenter − halfHeight` = 0.2.

- [ ] **Step 7: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Add wall map: association, information-filter fusion, cross-check and anchor deltas"
  ```

---

### Task 13: `WallDetectionPipeline`

One call per inference frame: align → extract → refine → map. It also works out why there are no walls, for the readings and the HUD (spec §5.4).

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/WallDetectionPipeline.cs`
- Test: `Assets/WallDistance/Tests/EditMode/WallDetectionPipelineTests.cs`

**Interfaces:**
- Consumes: `FloorAlignedDepth`, `VerticalPlaneExtractor`, `BaseEdgeRefiner`, `WallMap`, `WallObservation.FromArPlane`, `MetricSample`, `MeasurementConfig`.
- Produces:
  - `sealed class WallDetectionPipeline(MeasurementConfig)`
  - properties:
    - `WallMap Map`, `AlignmentResult LastAlignment`
    - `int LastObservationCount`, `float LastEdgeSnapFraction` (NaN when nothing was refined)
    - `double LastProcessingMs`, `double LastDetectLatencyMs`, `double LastFrameTime` (NaN before the first frame)
    - `FailureReason LastFailure`
  - `void ProcessFrame(string sessionId, InverseDepthImage img, FloorPlane floor, IReadOnlyList<MetricSample> metric, double now)`
  - `void ObserveArPlanes(string sessionId, IReadOnlyList<WallCandidate> planes, Vector3 up, double now)`
  - `FailureReason DetectionFailure(double now, bool inferenceAvailable)`
    - Returns `InferenceUnavailable`, `InferenceStale`, `NoFloor`, `AlignmentFailed`, `NoWallInView` or `None`.
  - `void Reset()`

- [ ] **Step 1: Write the failing tests**

  ```csharp
  using System.Collections.Generic;
  using NUnit.Framework;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class WallDetectionPipelineTests
      {
          static InverseDepthImage Frame(Pose pose, double timestamp)
          {
              var img = SyntheticCorridor.ToNetworkOutput(new SyntheticCorridor().Render(pose));
              img.timestamp = timestamp;
              return img;
          }

          [Test]
          public void CorridorFrame_AddsWalls_WithRightWallAt0_8()
          {
              var p = new WallDetectionPipeline(new MeasurementConfig());
              var pose = SyntheticCorridor.Camera();
              p.ProcessFrame("s1", Frame(pose, 1.0), SyntheticCorridor.Floor, null, 1.03);
              Assert.AreEqual(FailureReason.None, p.LastFailure);
              Assert.GreaterOrEqual(p.Map.Tracks.Count, 3);
              Assert.AreEqual(30.0, p.LastDetectLatencyMs, 1e-6);
              bool found = false;
              foreach (var t in p.Map.Tracks)
                  if (Vector3.Angle(t.normal, Vector3.left) < 1f && Mathf.Abs(t.SignedDistance(pose.position) - 0.8f) <= 0.02f) found = true;
              Assert.IsTrue(found, "right wall at 0.8 m");
          }

          [Test]
          public void NoFloorAndNoRawDepth_ReportsNoFloorAndAddsNoWalls()
          {
              // Review Focus 4: at app start there is no floor plane and no confident raw depth.
              // No wall may be made from an assumed camera height.
              var p = new WallDetectionPipeline(new MeasurementConfig());
              p.ProcessFrame("s1", Frame(SyntheticCorridor.Camera(), 1.0), default, null, 1.03);
              Assert.AreEqual(FailureReason.NoFloor, p.LastFailure);
              Assert.AreEqual(0, p.Map.Tracks.Count);
              Assert.AreEqual(FailureReason.NoFloor, p.DetectionFailure(1.1, inferenceAvailable: true));
              p.ProcessFrame("s1", Frame(SyntheticCorridor.Camera(), 1.1), default, new List<MetricSample>(), 1.13);
              Assert.AreEqual(FailureReason.NoFloor, p.LastFailure, "an empty sample list is not a fallback");
          }

          [Test]
          public void DetectionFailure_StaleAndUnavailable()
          {
              var p = new WallDetectionPipeline(new MeasurementConfig());
              Assert.AreEqual(FailureReason.InferenceStale, p.DetectionFailure(0.0, true), "no frame yet");
              Assert.AreEqual(FailureReason.InferenceUnavailable, p.DetectionFailure(0.0, false));
              p.ProcessFrame("s1", Frame(SyntheticCorridor.Camera(), 1.0), SyntheticCorridor.Floor, null, 1.03);
              Assert.AreEqual(FailureReason.None, p.DetectionFailure(1.9, true));
              Assert.AreEqual(FailureReason.InferenceStale, p.DetectionFailure(2.1, true), "over 1 s since the last frame");
          }

          [Test]
          public void FailedFrame_KeepsExistingWalls()
          {
              var p = new WallDetectionPipeline(new MeasurementConfig());
              p.ProcessFrame("s1", Frame(SyntheticCorridor.Camera(), 1.0), SyntheticCorridor.Floor, null, 1.0);
              int walls = p.Map.Tracks.Count;
              p.ProcessFrame("s1", Frame(SyntheticCorridor.Camera(pitchDown: -35f), 1.1), SyntheticCorridor.Floor, null, 1.1);
              Assert.AreEqual(FailureReason.AlignmentFailed, p.LastFailure);
              Assert.AreEqual(walls, p.Map.Tracks.Count);
          }

          [Test]
          public void NewSession_ClearsTheMap_AndResetClearsEverything()
          {
              var p = new WallDetectionPipeline(new MeasurementConfig());
              p.ProcessFrame("s1", Frame(SyntheticCorridor.Camera(), 1.0), SyntheticCorridor.Floor, null, 1.0);
              p.ProcessFrame("s2", Frame(SyntheticCorridor.Camera(pitchDown: -35f), 1.1), SyntheticCorridor.Floor, null, 1.1);
              Assert.AreEqual(0, p.Map.Tracks.Count);
              p.Reset();
              Assert.IsTrue(double.IsNaN(p.LastFrameTime));
              Assert.AreEqual(FailureReason.InferenceStale, p.DetectionFailure(1.2, true));
          }

          [Test]
          public void ArPlanes_AreThrottledTo10Hz_AndOnlyTrackedVerticalLargeOnesCount()
          {
              var p = new WallDetectionPipeline(new MeasurementConfig());
              var near = Fx.WallAtZ(2f, 1f, 1f, "a", yCenter: 1f);
              var far = Fx.WallAtZ(4f, 1f, 1f, "b", yCenter: 1f);
              var tiny = Fx.WallAtZ(6f, 0.1f, 0.1f, "c", yCenter: 1f);
              var lost = Fx.WallAtZ(8f, 1f, 1f, "d", yCenter: 1f);
              lost.isTracked = false;
              p.ObserveArPlanes("s1", new[] { near }, Vector3.up, 0.0);
              p.ObserveArPlanes("s1", new[] { far }, Vector3.up, 0.05);
              Assert.AreEqual(1, p.Map.Tracks.Count, "second call within 0.1 s is skipped");
              p.ObserveArPlanes("s1", new[] { far, tiny, lost }, Vector3.up, 0.11);
              Assert.AreEqual(2, p.Map.Tracks.Count);
              Assert.AreEqual(MeasurementSource.PlaneOnly, p.Map.Tracks[1].lastSource);
          }
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  Run the test command. Expected: compilation error, `'WallDetectionPipeline' could not be found`.

- [ ] **Step 3: Implement `WallDetectionPipeline.cs`**

  ```csharp
  using System.Collections.Generic;
  using System.Diagnostics;
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// Per-inference-frame orchestration: floor alignment → vertical planes → base-edge refinement
      /// → wall map. Also folds ARCore's own vertical planes into the same map, so they can
      /// cross-check learned walls (spec §5.2). Pure C#: the AR layer feeds it frames and planes.
      /// </summary>
      public sealed class WallDetectionPipeline
      {
          readonly MeasurementConfig _cfg;
          readonly FloorAlignedDepth _aligner;
          readonly VerticalPlaneExtractor _extractor;
          readonly BaseEdgeRefiner _refiner;
          readonly List<WallObservation> _obs = new List<WallObservation>(8);
          readonly Stopwatch _watch = new Stopwatch();
          double _lastArObserve = double.NegativeInfinity;

          public WallMap Map { get; }
          public AlignmentResult LastAlignment { get; private set; }
          public int LastObservationCount { get; private set; }
          public float LastEdgeSnapFraction { get; private set; } = float.NaN;
          public double LastProcessingMs { get; private set; } = double.NaN;
          /// <summary>Camera-image-to-map latency of the last processed frame, ms.</summary>
          public double LastDetectLatencyMs { get; private set; } = double.NaN;
          /// <summary>Image timestamp of the last processed frame; NaN before the first.</summary>
          public double LastFrameTime { get; private set; } = double.NaN;
          public FailureReason LastFailure { get; private set; } = FailureReason.None;

          public WallDetectionPipeline(MeasurementConfig cfg)
          {
              _cfg = cfg ?? new MeasurementConfig();
              _aligner = new FloorAlignedDepth(_cfg.detection);
              _extractor = new VerticalPlaneExtractor(_cfg.detection);
              _refiner = new BaseEdgeRefiner(_cfg.detection);
              Map = new WallMap(_cfg);
          }

          public void ProcessFrame(string sessionId, InverseDepthImage img, FloorPlane floor, IReadOnlyList<MetricSample> metric, double now)
          {
              _watch.Restart();
              Map.SetSession(sessionId);
              LastFrameTime = img.timestamp;
              LastDetectLatencyMs = (now - img.timestamp) * 1000.0;

              var align = _aligner.Align(img, floor, metric);
              LastAlignment = align;
              LastObservationCount = 0;
              LastEdgeSnapFraction = float.NaN;
              if (!align.success)
              {
                  // Keep the map: a frame that cannot be scaled says nothing about walls already found.
                  LastFailure = align.failure;
                  LastProcessingMs = _watch.Elapsed.TotalMilliseconds;
                  return;
              }

              _extractor.Extract(img, align, floor, now, _obs);
              float snapSum = 0f;
              int snapped = 0;
              for (int i = 0; i < _obs.Count; i++)
              {
                  var o = _obs[i];
                  // Edges only refine walls scaled from the floor: without the floor plane there
                  // is no metric back-projection for the edge.
                  if (!align.usedMetricSamples && _refiner.TryRefine(img, floor, o, out var refined))
                  {
                      o = refined;
                      snapSum += refined.edgeSnapFraction;
                      snapped++;
                  }
                  Map.Observe(o, now);
              }
              LastObservationCount = _obs.Count;
              if (snapped > 0) LastEdgeSnapFraction = snapSum / snapped;
              LastFailure = _obs.Count == 0 ? FailureReason.NoWallInView : FailureReason.None;
              LastProcessingMs = _watch.Elapsed.TotalMilliseconds;
          }

          /// <summary>
          /// Fold ARCore vertical planes into the map at ≤ 10 Hz. Planes change slowly, and
          /// observing them every frame would let them outweigh the learned walls.
          /// </summary>
          public void ObserveArPlanes(string sessionId, IReadOnlyList<WallCandidate> planes, Vector3 up, double now)
          {
              if (planes == null || now - _lastArObserve < _cfg.detection.arPlaneObserveIntervalSeconds) return;
              _lastArObserve = now;
              Map.SetSession(sessionId);
              for (int i = 0; i < planes.Count; i++)
              {
                  var c = planes[i];
                  if (c == null || !c.isTracked || c.source != MeasurementSource.PlaneOnly) continue;
                  if (c.boundary.Count < 3 || c.Area() < _cfg.minCandidateAreaSquareMeters) continue;
                  var o = WallObservation.FromArPlane(c, up, now);
                  if (o != null) Map.Observe(o, now);
              }
          }

          /// <summary>
          /// Why learned detection is not producing walls right now; None when it is working.
          /// Unavailable is for the HUD and CSV only (deviation D9); the engine never stamps it on readings.
          /// </summary>
          public FailureReason DetectionFailure(double now, bool inferenceAvailable)
          {
              if (!inferenceAvailable) return FailureReason.InferenceUnavailable;
              if (double.IsNaN(LastFrameTime) || now - LastFrameTime > _cfg.detection.staleAfterSeconds)
                  return FailureReason.InferenceStale;
              return LastFailure;
          }

          public void Reset()
          {
              Map.Clear();
              LastAlignment = null;
              LastObservationCount = 0;
              LastEdgeSnapFraction = float.NaN;
              LastProcessingMs = LastDetectLatencyMs = LastFrameTime = double.NaN;
              LastFailure = FailureReason.None;
              _lastArObserve = double.NegativeInfinity;
          }
      }
  }
  ```

- [ ] **Step 4: Run the tests to verify they pass**

  Run the test command. Expected: PASS.

- [ ] **Step 5: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Add wall detection pipeline with detection failure reasons"
  ```

---

### Task 14: `CorridorSideSelector`

This task gives left, right and corridor width from the map (spec §5.5). A side wall is a remembered map wall whose floor line runs roughly along the phone's floor heading. Left/right is decided by which side of that heading the wall lies on. The distance is the horizontal perpendicular from the camera to the wall line. It is valid while the wall is out of view, which is the normal case: a side wall is at 90° to where the phone points.

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/CorridorSideSelector.cs`
- Test: `Assets/WallDistance/Tests/EditMode/CorridorSideSelectorTests.cs`

**Interfaces:**
- Consumes:
  - `WallTrack` (`id`, `SignedDistance`, `normal`, `direction`, `origin`, `extentMin/Max`, `lastSeen`, `observations`, `crossChecked`, `Source`), `WallMap` (tests).
  - `MeasurementFilter`, `Horizontal`, `MeasurementConfig` side fields (Task 1).
- Produces:
  - `sealed class CorridorSideSelector`
    - `void Reset()`
    - `void Measure(string sessionId, double now, Pose camera, Matrix4x4 worldToClip, Vector3 up, IReadOnlyList<WallTrack> walls, MeasurementConfig cfg, out WallReading left, out WallReading right, out float width)`
    - `static QualityLabel QualityFor(MeasurementSource source, bool crossChecked)`

  Readings have `kind = CorridorLeft/CorridorRight`, `candidateId` = track id, `surfaceNormal` pointing at the camera, and `surfacePoint` = foot of the perpendicular at camera height. `width` is NaN unless both are valid and parallel within `corridorParallelToleranceDeg`.

- [ ] **Step 1: Write the failing tests**

  ```csharp
  using System.Collections.Generic;
  using NUnit.Framework;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class CorridorSideSelectorTests
      {
          /// <summary>Corridor along +z: left wall x = leftX facing +x, right wall x = rightX facing -x, both observed at t.</summary>
          static WallMap Corridor(double t = 0, float leftX = -1f, float rightX = 1f, float z0 = -5f, float z1 = 10f)
          {
              var m = new WallMap(new MeasurementConfig());
              m.SetSession("s1");
              m.Observe(Fx.Obs(leftX, t, z0, z1, angleDeg: 180f), t);
              m.Observe(Fx.Obs(rightX, t, z0, z1), t);
              return m;
          }

          static Pose Cam(float x = 0.2f, float z = 0f, float yaw = 0f, float pitch = 10f) =>
              new Pose(new Vector3(x, 1.4f, z), Quaternion.Euler(pitch, yaw, 0f));

          /// <summary>Same projection*view a Unity camera builds (view space looks down -Z).</summary>
          static Matrix4x4 Clip(Pose p) =>
              Matrix4x4.Perspective(60f, 9f / 16f, 0.05f, 50f) * Matrix4x4.Scale(new Vector3(1f, 1f, -1f))
              * Matrix4x4.TRS(p.position, p.rotation, Vector3.one).inverse;

          static WallReading L, R;
          static float W;

          static void Measure(CorridorSideSelector s, IReadOnlyList<WallTrack> walls, Pose cam, double now = 0.1, MeasurementConfig cfg = null) =>
              s.Measure("s1", now, cam, Clip(cam), Vector3.up, walls, cfg ?? new MeasurementConfig(), out L, out R, out W);

          [Test]
          public void HeadingWithin20Degrees_GivesLeftRightAndWidth([Values(-20f, 0f, 20f)] float yaw)
          {
              Measure(new CorridorSideSelector(), Corridor().Tracks, Cam(yaw: yaw));
              Assert.IsTrue(L.isValid && R.isValid);
              Assert.AreEqual(MeasurementKind.CorridorLeft, L.kind);
              Assert.AreEqual(MeasurementKind.CorridorRight, R.kind);
              // Perpendicular, so independent of where the phone points.
              Assert.AreEqual(1.2f, L.distanceMeters, 1e-4f);
              Assert.AreEqual(0.8f, R.distanceMeters, 1e-4f);
              Assert.AreEqual(2.0f, W, 1e-4f);
              Assert.AreEqual(1f, R.surfaceNormal.x * -1f, 1e-4f, "right wall's normal faces the camera (-x)");
          }

          [Test]
          public void TurnedAround_SidesSwap()
          {
              var map = Corridor();
              Measure(new CorridorSideSelector(), map.Tracks, Cam(yaw: 180f));
              Assert.AreEqual(0.8f, L.distanceMeters, 1e-4f, "the x = +1 wall is now on the left");
              Assert.AreEqual(1.2f, R.distanceMeters, 1e-4f);
          }

          [Test]
          public void Hysteresis_EnterAt30_LeaveAbove40()
          {
              var map = Corridor();
              Measure(new CorridorSideSelector(), map.Tracks, Cam(yaw: 35f));
              Assert.AreEqual(FailureReason.NoWallOnSide, R.failure, "35° is too far off to become a side wall");

              var s = new CorridorSideSelector();
              Measure(s, map.Tracks, Cam(yaw: 0f));
              string id = R.candidateId;
              Measure(s, map.Tracks, Cam(yaw: 35f));
              Assert.IsTrue(R.isValid, "an existing side wall is kept up to 40°");
              Assert.AreEqual(id, R.candidateId);
              Measure(s, map.Tracks, Cam(yaw: 45f));
              Assert.IsFalse(R.isValid);
              Assert.AreEqual(FailureReason.NoWallOnSide, R.failure);
          }

          [Test]
          public void WalkingSway_KeepsTheSameWalls()
          {
              var map = Corridor();
              var s = new CorridorSideSelector();
              Measure(s, map.Tracks, Cam());
              string l = L.candidateId, r = R.candidateId;
              foreach (float yaw in new[] { 15f, -15f, 12f, -15f, 15f })
              {
                  Measure(s, map.Tracks, Cam(yaw: yaw));
                  Assert.AreEqual(l, L.candidateId);
                  Assert.AreEqual(r, R.candidateId);
              }
          }

          [Test]
          public void ExtentMargin_IsOneMetre()
          {
              var map = Corridor(z0: 3f, z1: 10f);
              Measure(new CorridorSideSelector(), map.Tracks, Cam(z: 1.9f));
              Assert.AreEqual(FailureReason.NoWallOnSide, R.failure, "1.1 m before the wall starts");
              Measure(new CorridorSideSelector(), map.Tracks, Cam(z: 2.1f));
              Assert.IsTrue(R.isValid, "0.9 m before the wall starts");
          }

          [Test]
          public void OldOrDistantWalls_AreIgnored()
          {
              var map = Corridor();
              Measure(new CorridorSideSelector(), map.Tracks, Cam(), now: 10.5);
              Assert.AreEqual(FailureReason.NoWallOnSide, R.failure, "last seen 10.5 s ago");
              Measure(new CorridorSideSelector(), map.Tracks, Cam(), now: 9.5);
              Assert.IsTrue(R.isValid);

              var wide = Corridor(rightX: 7.2f);
              Measure(new CorridorSideSelector(), wide.Tracks, Cam());
              Assert.IsTrue(L.isValid);
              Assert.AreEqual(FailureReason.NoWallOnSide, R.failure, "7 m away");
              Assert.IsTrue(float.IsNaN(W));
          }

          [Test]
          public void DoorRecess_NearestWallOnTheSideWins()
          {
              var map = Corridor();
              var main = map.Tracks[0];
              map.Observe(Fx.Obs(-1.3f, 0, 2f, 3f, angleDeg: 180f), 0);
              Assert.AreEqual(3, map.Tracks.Count, "the recess is its own wall");
              Measure(new CorridorSideSelector(), map.Tracks, Cam(z: 2.5f));
              Assert.AreEqual(main.id, L.candidateId);
              Assert.AreEqual(1.2f, L.distanceMeters, 1e-4f);
          }

          [Test]
          public void NonParallelWalls_HaveNoWidth()
          {
              var m = new WallMap(new MeasurementConfig());
              m.SetSession("s1");
              m.Observe(Fx.Obs(-1f, 0, -7.5f, 7.5f, angleDeg: 180f), 0);
              m.Observe(Fx.Obs(1f, 0, -7.5f, 7.5f, angleDeg: 15f), 0);
              Measure(new CorridorSideSelector(), m.Tracks, Cam());
              Assert.IsTrue(L.isValid && R.isValid, "15° is within the 30° entry angle");
              Assert.IsTrue(float.IsNaN(W), "but not parallel within 10°");
          }

          [Test]
          public void PhonePointingAtFloor_HasNoHeading()
          {
              var map = Corridor();
              Measure(new CorridorSideSelector(), map.Tracks, Cam(pitch: 75f));
              Assert.AreEqual(FailureReason.NoHeading, L.failure);
              Assert.AreEqual(FailureReason.NoHeading, R.failure);
              Assert.IsTrue(float.IsNaN(W));
              Measure(new CorridorSideSelector(), map.Tracks, Cam(pitch: 65f));
              Assert.IsTrue(L.isValid && R.isValid);
          }

          [Test]
          public void SwitchingToAnotherWall_DoesNotBlendDistances()
          {
              var cfg = new MeasurementConfig { filterTimeConstantSeconds = 1f };
              var map = Corridor();
              var s = new CorridorSideSelector();
              for (int i = 0; i < 5; i++) Measure(s, map.Tracks, Cam(x: 0f), 0.1 + 0.033 * i, cfg);
              Assert.AreEqual(1.0f, R.distanceMeters, 1e-4f);
              // The old right wall ages out (seen at 0, now 11 s); a new one 1.5 m away is seen at 11.
              var b = map.Observe(Fx.Obs(1.5f, 11.0, -5f, 10f), 11.0);
              Measure(s, map.Tracks, Cam(x: 0f), 11.0, cfg);
              Assert.AreEqual(b.id, R.candidateId);
              Assert.AreEqual(1.5f, R.distanceMeters, 1e-5f, "a new wall starts from its own raw distance");
          }

          [Test]
          public void OutOfViewWall_ReasonSaysWhenItWasLastSeen()
          {
              Measure(new CorridorSideSelector(), Corridor().Tracks, Cam(), now: 1.5);
              Assert.IsTrue(R.isValid, "out of view is still valid");
              StringAssert.Contains("last seen 1.5 s ago", R.qualityReason);
          }

          [Test]
          public void CloseSideWall_IsOutOfTestedRange()
          {
              Measure(new CorridorSideSelector(), Corridor().Tracks, Cam(x: 0.7f));
              Assert.IsTrue(R.isValid);
              Assert.AreEqual(QualityLabel.OutOfTestedRange, R.quality);
              StringAssert.StartsWith("0.30 m is outside", R.qualityReason);
          }

          [Test]
          public void Reading_InheritsSourceQualityAndChain()
          {
              Measure(new CorridorSideSelector(), Corridor().Tracks, Cam());
              Assert.AreEqual(MeasurementSource.LearnedDepth, R.source);
              Assert.AreEqual(QualityLabel.LearnedEstimate, R.quality);
              Assert.AreEqual("LearnedDepth", R.sourceChain);
          }

          [Test]
          public void QualityFor_MapsEverySource()
          {
              Assert.AreEqual(QualityLabel.CrossChecked, CorridorSideSelector.QualityFor(MeasurementSource.LearnedDepth, true));
              Assert.AreEqual(QualityLabel.EdgeConfirmed, CorridorSideSelector.QualityFor(MeasurementSource.FloorEdge, false));
              Assert.AreEqual(QualityLabel.LearnedEstimate, CorridorSideSelector.QualityFor(MeasurementSource.LearnedDepth, false));
              Assert.AreEqual(QualityLabel.PlaneEstimate, CorridorSideSelector.QualityFor(MeasurementSource.PlaneOnly, false));
              Assert.AreEqual(QualityLabel.AssistedEstimate, CorridorSideSelector.QualityFor(MeasurementSource.AssistedFloor, false));
              Assert.AreEqual(QualityLabel.DepthEstimate, CorridorSideSelector.QualityFor(MeasurementSource.DepthOnly, false));
              Assert.AreEqual(QualityLabel.DepthValidated, CorridorSideSelector.QualityFor(MeasurementSource.PlaneDepthValidated, false));
              Assert.AreEqual(QualityLabel.Unavailable, CorridorSideSelector.QualityFor(MeasurementSource.None, false));
          }
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  Run: `unity command run_tests --mode editor --filter WallDistance --filter_type assembly --timeout 240 --no-banner`

  Expected: compilation error, `'CorridorSideSelector' could not be found`.

- [ ] **Step 3: Implement `CorridorSideSelector.cs`**

  ```csharp
  using System.Collections.Generic;
  using System.Globalization;
  using UnityEngine;

  namespace WallDistance.Core
  {
      /// <summary>
      /// Left/right corridor walls and corridor width from the wall map (spec §5.5).
      ///
      /// "Side" is relative to the phone's FLOOR heading (its forward vector flattened onto the
      /// floor), not its screen: holding the phone at 45° still has a clear forward, while
      /// pointing it at the floor does not (NoHeading). Entry/exit angles differ (hysteresis),
      /// so walking sway does not flicker the choice. Each side has its own filter keyed by wall id.
      /// </summary>
      public sealed class CorridorSideSelector
      {
          static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

          readonly MeasurementFilter _leftFilter = new MeasurementFilter(), _rightFilter = new MeasurementFilter();
          string _leftId, _rightId;

          public void Reset()
          {
              _leftFilter.Reset();
              _rightFilter.Reset();
              _leftId = _rightId = null;
          }

          public void Measure(string sessionId, double now, Pose camera, Matrix4x4 worldToClip, Vector3 up,
              IReadOnlyList<WallTrack> walls, MeasurementConfig cfg, out WallReading left, out WallReading right, out float width)
          {
              width = float.NaN;
              Vector3 fwd = camera.rotation * Vector3.forward;
              if (Mathf.Abs(Vector3.Dot(fwd, up)) > Mathf.Cos(cfg.sideNoHeadingDeg * Mathf.Deg2Rad)
                  || !Horizontal.TryDirection(fwd, up, out Vector3 heading))
              {
                  // Pointing at the floor or ceiling: left and right are undefined. Forget the
                  // current walls so the next valid heading starts cleanly.
                  Reset();
                  left = WallReading.Invalid(MeasurementKind.CorridorLeft, FailureReason.NoHeading, sessionId, now, camera);
                  right = WallReading.Invalid(MeasurementKind.CorridorRight, FailureReason.NoHeading, sessionId, now, camera);
                  return;
              }

              WallTrack bestL = null, bestR = null;
              float dL = float.PositiveInfinity, dR = float.PositiveInfinity;
              if (walls != null)
              {
                  for (int i = 0; i < walls.Count; i++)
                  {
                      var w = walls[i];
                      if (now - w.lastSeen > cfg.sideMaxAgeSeconds) continue;
                      float signed = w.SignedDistance(camera.position);
                      float dist = Mathf.Abs(signed);
                      if (dist > cfg.sideMaxDistanceMeters || dist < 1e-4f) continue;
                      // Normal pointing at the camera; -toward points from the camera to the wall.
                      Vector3 toward = signed >= 0f ? w.normal : -w.normal;
                      bool isRight = Vector3.Dot(up, Vector3.Cross(heading, -toward)) > 0f;
                      // The current wall on this side may stay until the wider exit angle.
                      float limit = w.id == (isRight ? _rightId : _leftId) ? cfg.sideExitAngleDeg : cfg.sideEnterAngleDeg;
                      float cosLimit = Mathf.Cos(limit * Mathf.Deg2Rad);
                      if (Mathf.Abs(Vector3.Dot(w.direction, heading)) < cosLimit) continue;
                      // The camera must be alongside the wall (within its extent plus a margin).
                      float along = Vector3.Dot(camera.position - w.origin, w.direction);
                      if (along < w.extentMin - cfg.sideExtentMarginMeters || along > w.extentMax + cfg.sideExtentMarginMeters) continue;
                      if (isRight) { if (dist < dR) { dR = dist; bestR = w; } }
                      else if (dist < dL) { dL = dist; bestL = w; }
                  }
              }

              left = Read(sessionId, now, camera, worldToClip, bestL, dL, MeasurementKind.CorridorLeft, _leftFilter, cfg);
              right = Read(sessionId, now, camera, worldToClip, bestR, dR, MeasurementKind.CorridorRight, _rightFilter, cfg);
              _leftId = bestL?.id;
              _rightId = bestR?.id;

              if (left.isValid && right.isValid
                  && Mathf.Abs(Vector3.Dot(left.surfaceNormal, right.surfaceNormal)) >= Mathf.Cos(cfg.corridorParallelToleranceDeg * Mathf.Deg2Rad))
                  width = left.distanceMeters + right.distanceMeters;
          }

          static WallReading Read(string sessionId, double now, Pose camera, Matrix4x4 worldToClip, WallTrack w, float dist,
              MeasurementKind kind, MeasurementFilter filter, MeasurementConfig cfg)
          {
              if (w == null)
              {
                  filter.Reset();
                  return WallReading.Invalid(kind, FailureReason.NoWallOnSide, sessionId, now, camera);
              }
              Vector3 toward = w.SignedDistance(camera.position) >= 0f ? w.normal : -w.normal;
              // The filter resets itself when the wall id changes, so two walls are never blended.
              float filtered = filter.Update(sessionId, w.id, dist, now, cfg.filterTimeConstantSeconds);
              var source = w.Source(now, cfg.detection.sourceWindowSeconds);
              Vector3 foot = camera.position - toward * dist;
              var r = new WallReading
              {
                  isValid = true,
                  kind = kind,
                  distanceMeters = filtered,
                  rawDistanceMeters = dist,
                  source = source,
                  quality = QualityFor(source, w.crossChecked),
                  failure = FailureReason.None,
                  sessionId = sessionId,
                  candidateId = w.id,
                  timestamp = now,
                  cameraPose = camera,
                  surfacePoint = foot,
                  surfaceNormal = toward,
                  depthResidualMeters = float.NaN,
                  depthInlierFraction = float.NaN,
                  sourceChain = source + (w.crossChecked ? "+ARPlane" : ""),
                  // Side walls are usually out of view: say how old the evidence is.
                  qualityReason = InView(foot, worldToClip)
                      ? string.Format(Inv, "{0} observations", w.observations)
                      : string.Format(Inv, "out of view, last seen {0:F1} s ago", now - w.lastSeen),
              };
              // Plan deviation D4: the same tested-range rule as aimed/nearest, prepended to the reason.
              const float eps = 1e-4f;
              if (r.distanceMeters < cfg.minTestedRangeMeters - eps || r.distanceMeters > cfg.maxTestedRangeMeters + eps)
              {
                  r.quality = QualityLabel.OutOfTestedRange;
                  r.qualityReason = string.Format(Inv, "{0:F2} m is outside the {1}-{2} m validated range; {3}",
                      r.distanceMeters, cfg.minTestedRangeMeters, cfg.maxTestedRangeMeters, r.qualityReason);
              }
              return r;
          }

          static bool InView(Vector3 p, Matrix4x4 worldToClip)
          {
              Vector4 c = worldToClip * new Vector4(p.x, p.y, p.z, 1f);
              return c.w > 0f && Mathf.Abs(c.x) <= c.w && Mathf.Abs(c.y) <= c.w;
          }

          /// <summary>The quality label a reading from this source earns (spec §5.4).</summary>
          public static QualityLabel QualityFor(MeasurementSource source, bool crossChecked)
          {
              if (crossChecked) return QualityLabel.CrossChecked;
              switch (source)
              {
                  case MeasurementSource.FloorEdge: return QualityLabel.EdgeConfirmed;
                  case MeasurementSource.LearnedDepth: return QualityLabel.LearnedEstimate;
                  case MeasurementSource.PlaneOnly: return QualityLabel.PlaneEstimate;
                  case MeasurementSource.AssistedFloor: return QualityLabel.AssistedEstimate;
                  case MeasurementSource.DepthOnly: return QualityLabel.DepthEstimate;
                  case MeasurementSource.PlaneDepthValidated: return QualityLabel.DepthValidated;
                  default: return QualityLabel.Unavailable;
              }
          }
      }
  }
  ```

- [ ] **Step 4: Run the tests to verify they pass**

  Run the test command. Expected: PASS.

- [ ] **Step 5: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Add corridor side selector: left/right/width from the wall map with hysteresis"
  ```

---

### Task 15: Engine — learned quality, detection reasons, side readings

**Files:**
- Modify: `Assets/WallDistance/Runtime/Core/WallMeasurementEngine.cs`
- Test: `Assets/WallDistance/Tests/EditMode/LearnedEngineTests.cs`

**Interfaces:**
- Consumes: `CorridorSideSelector` (Task 14), `WallMap`/`WallTrack` (Task 12), `WallCandidate.crossChecked`, `MeasurementConfig.crossCheckToleranceMeters`.
- Produces:
  - `EngineInput.mapWalls` (`IReadOnlyList<WallTrack>`; null means no map, e.g. assisted mode)
  - `EngineInput.up` (`Vector3`; zero means `Vector3.up`)
  - `EngineInput.detectionFailure` (`FailureReason`)
  - `WallDistanceSnapshot.left/right/corridorWidthMeters` are always set: invalid readings and NaN width when not measurable.
  - Valid aimed/nearest readings always carry a non-empty `sourceChain`.

- [ ] **Step 1: Write the failing tests**

  ```csharp
  using System.Collections.Generic;
  using NUnit.Framework;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class LearnedEngineTests
      {
          Camera _cam;

          [SetUp]
          public void SetUp() => _cam = Fx.MakeCamera(new Vector3(0f, 1.4f, 0f), Quaternion.identity);

          [TearDown]
          public void TearDown() => Object.DestroyImmediate(_cam.gameObject);

          EngineInput In(IReadOnlyList<WallCandidate> candidates, double now, IReadOnlyList<WallTrack> map = null,
              DepthFrame depth = null, FailureReason detection = FailureReason.None, bool tracking = true)
          {
              var i = Fx.Input(_cam, candidates, now, depth, tracking: tracking);
              i.mapWalls = map;
              i.up = Vector3.up;
              i.detectionFailure = detection;
              return i;
          }

          static WallCandidate Learned(float z, MeasurementSource src = MeasurementSource.LearnedDepth, bool crossChecked = false)
          {
              var c = Fx.WallAtZ(z, yCenter: 1.4f);
              c.source = src;
              c.crossChecked = crossChecked;
              return c;
          }

          static WallMap Corridor(float leftX = -1f, float rightX = 1f)
          {
              var m = new WallMap(new MeasurementConfig());
              m.SetSession("s1");
              m.Observe(Fx.Obs(leftX, 0, -5f, 10f, angleDeg: 180f), 0);
              m.Observe(Fx.Obs(rightX, 0, -5f, 10f), 0);
              return m;
          }

          [Test]
          public void LearnedWall_IsLearnedEstimate_EdgeIsEdgeConfirmed_AgreementIsCrossChecked()
          {
              var e = new WallMeasurementEngine(new MeasurementConfig());
              var s = e.Update(In(new[] { Learned(2f) }, 1.0));
              Assert.AreEqual(QualityLabel.LearnedEstimate, s.aimed.quality);
              Assert.AreEqual(MeasurementSource.LearnedDepth, s.aimed.source);
              Assert.AreEqual("LearnedDepth", s.aimed.sourceChain);
              Assert.AreEqual(2f, s.aimed.distanceMeters, 1e-4f);

              s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Learned(2f, MeasurementSource.FloorEdge) }, 1.0));
              Assert.AreEqual(QualityLabel.EdgeConfirmed, s.aimed.quality);

              s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Learned(2f, crossChecked: true) }, 1.0));
              Assert.AreEqual(QualityLabel.CrossChecked, s.aimed.quality);
              Assert.AreEqual("LearnedDepth+ARPlane", s.aimed.sourceChain);
          }

          [Test]
          public void RawDepthAgreeing_CrossChecks_Disagreeing_OnlyAddsAReason()
          {
              var pose = new Pose(_cam.transform.position, _cam.transform.rotation);
              var agree = Fx.DepthForPlane(pose, new Vector3(0f, 0f, 2f), Vector3.back, 1.0);
              var s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Learned(2f) }, 1.0, depth: agree));
              Assert.AreEqual(QualityLabel.CrossChecked, s.aimed.quality);
              Assert.AreEqual("LearnedDepth+RawDepth", s.aimed.sourceChain);

              // Plan deviation D10: raw depth 30 cm off does not demote a learned wall.
              var off = Fx.DepthForPlane(pose, new Vector3(0f, 0f, 2f), Vector3.back, 1.0, biasMeters: 0.3f);
              s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Learned(2f) }, 1.0, depth: off));
              Assert.AreEqual(QualityLabel.LearnedEstimate, s.aimed.quality);
              StringAssert.Contains("raw depth", s.aimed.qualityReason);
          }

          [Test]
          public void LearnedWallBeyond3m_IsOutOfTestedRange()
          {
              var s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Learned(4f) }, 1.0));
              Assert.IsTrue(s.aimed.isValid);
              Assert.AreEqual(QualityLabel.OutOfTestedRange, s.aimed.quality);
              Assert.AreEqual(MeasurementSource.LearnedDepth, s.aimed.source);
          }

          [Test]
          public void DetectionFailure_ReplacesGenericNoWallReasons()
          {
              var e = new WallMeasurementEngine(new MeasurementConfig());
              var s = e.Update(In(new WallCandidate[0], 1.0, new List<WallTrack>(), detection: FailureReason.NoFloor));
              Assert.AreEqual(FailureReason.NoFloor, s.aimed.failure);
              Assert.AreEqual(FailureReason.NoFloor, s.nearest.failure);
              Assert.AreEqual(FailureReason.NoFloor, s.left.failure);
              Assert.AreEqual(FailureReason.NoFloor, s.right.failure);
          }

          [Test]
          public void DetectionFailure_NeverInvalidatesAValidReading_OrStampsUnavailable()
          {
              var s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Learned(2f) }, 1.0, detection: FailureReason.NoFloor));
              Assert.IsTrue(s.aimed.isValid, "walls already in the map are still measured");

              s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new WallCandidate[0], 1.0, detection: FailureReason.InferenceUnavailable));
              Assert.AreEqual(FailureReason.NoWallUnderCrosshair, s.aimed.failure, "deviation D9");
          }

          [Test]
          public void MapWalls_GiveSidesAndWidth()
          {
              var map = Corridor(rightX: 1.2f);
              var s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(map.Candidates(0.1), 0.1, map.Tracks));
              Assert.AreEqual(1.0f, s.left.distanceMeters, 1e-4f);
              Assert.AreEqual(1.2f, s.right.distanceMeters, 1e-4f);
              Assert.AreEqual(2.2f, s.corridorWidthMeters, 1e-4f);
          }

          [Test]
          public void TrackingLossThenRecovery_SidesResumeFromMapWithoutBlend()
          {
              // Review Focus 3. While tracking is lost, ARCore corrects the right wall's anchor by
              // 10 cm. On recovery the reading must be the corrected 1.1 m at once. A slow filter
              // (τ = 1 s) would show ~1.003 m if pre-loss history leaked through.
              var cfg = new MeasurementConfig { filterTimeConstantSeconds = 1f };
              var e = new WallMeasurementEngine(cfg);
              var map = Corridor();
              var right = map.Tracks[1];
              map.ApplyAnchorPose(right.id, new Pose(new Vector3(1f, 0f, 0f), Quaternion.identity));
              WallDistanceSnapshot s = default;
              for (int i = 0; i < 5; i++) s = e.Update(In(map.Candidates(0.1 + i * 0.033), 0.1 + i * 0.033, map.Tracks));
              Assert.AreEqual(1.0f, s.right.distanceMeters, 1e-4f);

              s = e.Update(In(map.Candidates(0.3), 0.3, map.Tracks, tracking: false));
              Assert.AreEqual(FailureReason.TrackingLost, s.left.failure);
              Assert.AreEqual(FailureReason.TrackingLost, s.right.failure);
              Assert.IsTrue(float.IsNaN(s.corridorWidthMeters));

              map.ApplyAnchorPose(right.id, new Pose(new Vector3(1.1f, 0f, 0f), Quaternion.identity));
              s = e.Update(In(map.Candidates(0.4), 0.4, map.Tracks));
              Assert.IsTrue(s.right.isValid, "the map was kept through the loss");
              Assert.AreEqual(1.1f, s.right.distanceMeters, 1e-4f);
              Assert.AreEqual(s.right.rawDistanceMeters, s.right.distanceMeters);
          }

          [Test]
          public void NoMap_SidesAreNoWallOnSide_WidthNaN()
          {
              var s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new WallCandidate[0], 1.0));
              Assert.AreEqual(FailureReason.NoWallOnSide, s.left.failure);
              Assert.AreEqual(MeasurementKind.CorridorRight, s.right.kind);
              Assert.IsTrue(float.IsNaN(s.corridorWidthMeters));
          }

          [Test]
          public void ArPlaneReading_GetsItsSourceAsChain()
          {
              var s = new WallMeasurementEngine(new MeasurementConfig()).Update(In(new[] { Fx.WallAtZ(2f, yCenter: 1.4f) }, 1.0));
              Assert.AreEqual(MeasurementSource.PlaneOnly, s.aimed.source);
              Assert.AreEqual("PlaneOnly", s.aimed.sourceChain);
          }
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  Run the test command. Expected: compilation error, `'EngineInput' does not contain a definition for 'mapWalls'`.

- [ ] **Step 3: Extend `EngineInput`**

  In `WallMeasurementEngine.cs`, after `public bool depthSupported;` in `EngineInput`, add:

  ```csharp
          /// <summary>Wall-map tracks for the left/right readings. Null when no map is in use (assisted mode).</summary>
          public IReadOnlyList<WallTrack> mapWalls;
          /// <summary>Gravity up in session space; left at zero it means Vector3.up.</summary>
          public Vector3 up;
          /// <summary>Why learned detection is not producing walls (WallDetectionPipeline.DetectionFailure); None when it is.</summary>
          public FailureReason detectionFailure;
  ```

- [ ] **Step 4: Add the side selector to the engine**

  **4a.** After `readonly DepthPlaneFitter _fitter = new DepthPlaneFitter();` add:

  ```csharp
          readonly CorridorSideSelector _sides = new CorridorSideSelector();
  ```

  **4b.** In `Reset()`, after `_nearestFilter.Reset();` add:

  ```csharp
              _sides.Reset();
  ```

  **4c.** In `Update`, replace the snapshot initialiser and everything after it with:

  ```csharp
              var snap = new WallDistanceSnapshot
              {
                  timestamp = input.now,
                  sessionTracking = input.sessionTracking,
                  depthSupported = input.depthSupported,
                  // NaN, not 0: a zero-width corridor is a value, "no width" is not.
                  corridorWidthMeters = float.NaN,
              };

              if (!input.sessionTracking)
              {
                  // Tracking loss invalidates immediately; the filters are cleared so the first
                  // reading after recovery is not blended with pre-loss history. The wall MAP is
                  // kept (it lives outside the engine), so sides come straight back on recovery.
                  _aimedFilter.Reset();
                  _nearestFilter.Reset();
                  _sides.Reset();
                  snap.aimed = WallReading.Invalid(MeasurementKind.Aimed, FailureReason.TrackingLost, input.sessionId, input.now, input.cameraPose);
                  snap.nearest = WallReading.Invalid(MeasurementKind.NearestObserved, FailureReason.TrackingLost, input.sessionId, input.now, input.cameraPose);
                  snap.left = WallReading.Invalid(MeasurementKind.CorridorLeft, FailureReason.TrackingLost, input.sessionId, input.now, input.cameraPose);
                  snap.right = WallReading.Invalid(MeasurementKind.CorridorRight, FailureReason.TrackingLost, input.sessionId, input.now, input.cameraPose);
                  return snap;
              }

              snap.aimed = MeasureAimed(input);
              snap.nearest = MeasureNearest(input);
              if (config.allowDenseDetection) MeasureNearestDepth(input, ref snap.nearest);
              MeasureSides(input, ref snap);

              Substitute(ref snap.aimed, input.detectionFailure);
              Substitute(ref snap.nearest, input.detectionFailure);
              Substitute(ref snap.left, input.detectionFailure);
              Substitute(ref snap.right, input.detectionFailure);
              FillSourceChain(ref snap.aimed);
              FillSourceChain(ref snap.nearest);
              return snap;
          }

          void MeasureSides(in EngineInput input, ref WallDistanceSnapshot snap)
          {
              if (input.mapWalls == null)
              {
                  _sides.Reset();
                  snap.left = WallReading.Invalid(MeasurementKind.CorridorLeft, FailureReason.NoWallOnSide, input.sessionId, input.now, input.cameraPose);
                  snap.right = WallReading.Invalid(MeasurementKind.CorridorRight, FailureReason.NoWallOnSide, input.sessionId, input.now, input.cameraPose);
                  snap.corridorWidthMeters = float.NaN;
                  return;
              }
              Vector3 up = input.up.sqrMagnitude > 0.5f ? input.up.normalized : Vector3.up;
              _sides.Measure(input.sessionId, input.now, input.cameraPose, input.worldToClip, up, input.mapWalls, config,
                  out snap.left, out snap.right, out snap.corridorWidthMeters);
          }

          /// <summary>
          /// A generic "no wall" is replaced by WHY learned detection has none: the user can act on
          /// "point at the floor", not on "aim at a wall". Valid readings are never touched, and
          /// InferenceUnavailable is never stamped on readings (deviation D9).
          /// </summary>
          static void Substitute(ref WallReading r, FailureReason detection)
          {
              if (r.isValid) return;
              if (detection != FailureReason.NoFloor && detection != FailureReason.AlignmentFailed && detection != FailureReason.InferenceStale) return;
              if (r.failure != FailureReason.NoWallUnderCrosshair && r.failure != FailureReason.NoWallInView && r.failure != FailureReason.NoWallOnSide) return;
              r.failure = detection;
              r.qualityReason = detection.ToString();
          }

          static void FillSourceChain(ref WallReading r)
          {
              if (r.isValid && string.IsNullOrEmpty(r.sourceChain)) r.sourceChain = r.source.ToString();
          }
  ```

  The replaced code is the old method tail: the `if (!input.sessionTracking)` block, the three `Measure…` lines, `return snap;` and the closing `}` of `Update`. The new block ends `Update` itself and adds the three helpers after it.

- [ ] **Step 5: Add the learned branch to `ApplyQuality`**

  In `ApplyQuality`, directly after the closing `}` of the `if (c.source == MeasurementSource.AssistedFloor)` block, add:

  ```csharp
              if (c.source == MeasurementSource.LearnedDepth || c.source == MeasurementSource.FloorEdge)
              {
                  ApplyLearnedQuality(ref r, c, input);
                  return;
              }
  ```

  Then add this method after `ApplyQuality`:

  ```csharp
          /// <summary>
          /// Map walls from learned depth. Raw depth can only PROMOTE them (to CrossChecked) when it
          /// agrees within the cross-check tolerance. Disagreement is noted in the reason, never a
          /// demotion (deviation D10): on plain walls raw depth is the unreliable party (spec §2).
          /// </summary>
          void ApplyLearnedQuality(ref WallReading r, WallCandidate c, in EngineInput input)
          {
              var v = _validator.Validate(c, input.depth, config, input.now);
              r.depthResidualMeters = v.medianResidualMeters;
              r.depthInlierFraction = v.inlierFraction;
              bool rawAgrees = v.depthUsed
                               && Mathf.Abs(v.medianResidualMeters) <= config.crossCheckToleranceMeters
                               && v.inlierFraction >= config.depthMinInlierFraction;
              r.source = c.source;
              r.sourceChain = c.source.ToString() + (c.crossChecked ? "+ARPlane" : "") + (rawAgrees ? "+RawDepth" : "");
              if (c.crossChecked || rawAgrees)
              {
                  r.quality = QualityLabel.CrossChecked;
                  r.qualityReason = rawAgrees
                      ? $"raw depth agrees within {Mathf.Abs(v.medianResidualMeters) * 100f:F0} cm"
                      : $"ARCore plane agrees within {config.crossCheckToleranceMeters * 100f:F0} cm";
              }
              else
              {
                  r.quality = c.source == MeasurementSource.FloorEdge ? QualityLabel.EdgeConfirmed : QualityLabel.LearnedEstimate;
                  r.qualityReason = v.depthUsed
                      ? $"raw depth differs by {Mathf.Abs(v.medianResidualMeters) * 100f:F0} cm (not used on plain walls)"
                      : c.source == MeasurementSource.FloorEdge ? "base snapped to the floor edge" : "learned depth scaled to the floor";
              }
              ApplyRangeLabel(ref r);
          }
  ```

- [ ] **Step 6: Run the tests to verify they pass**

  Run the test command. Expected: PASS, including every pre-existing `WallMeasurementEngineTests` test. Their inputs leave `mapWalls` null and `detectionFailure` at `None`, so aimed/nearest behave exactly as before.

- [ ] **Step 7: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Engine: learned-source quality, detection failure reasons, left/right/width readings"
  ```

---

### Task 16: `ReadingText` — user-facing strings

All reading text lives in Core so it can be tested and so the HUD and any future UI agree. This task also moves the HUD's two `Describe` methods into it and colours the new labels.

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/ReadingText.cs`
- Modify: `Assets/WallDistance/Runtime/AR/WallDistanceHud.cs` (`Render`, `QualityColor`, remove both `Describe` methods)
- Test: `Assets/WallDistance/Tests/EditMode/ReadingTextTests.cs`

**Interfaces:**
- Produces:
  - `static class ReadingText`
  - `string Quality(QualityLabel)`
  - `string Failure(FailureReason)`
  - `string Sides(WallReading left, WallReading right, float widthMeters)` → `"L 0.84 m | R 1.12 m | W 1.96 m"`, with `—` for anything invalid or NaN
  - `string SidesHint(WallReading left, WallReading right)` → `""` when both are valid

- [ ] **Step 1: Write the failing tests**

  ```csharp
  using NUnit.Framework;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class ReadingTextTests
      {
          static WallReading Valid(MeasurementKind k, float d) => new WallReading { isValid = true, kind = k, distanceMeters = d };
          static WallReading Bad(MeasurementKind k, FailureReason f) => WallReading.Invalid(k, f, "s", 0, default);

          [Test]
          public void Quality_ExistingTextUnchanged_NewLabelsNamed()
          {
              Assert.AreEqual("Depth validated", ReadingText.Quality(QualityLabel.DepthValidated));
              Assert.AreEqual("Plane estimate", ReadingText.Quality(QualityLabel.PlaneEstimate));
              Assert.AreEqual("Depth estimate (no plane)", ReadingText.Quality(QualityLabel.DepthEstimate));
              Assert.AreEqual("Assisted floor geometry", ReadingText.Quality(QualityLabel.AssistedEstimate));
              Assert.AreEqual("Unreliable", ReadingText.Quality(QualityLabel.Unreliable));
              Assert.AreEqual("Out of tested range", ReadingText.Quality(QualityLabel.OutOfTestedRange));
              Assert.AreEqual("Learned depth estimate", ReadingText.Quality(QualityLabel.LearnedEstimate));
              Assert.AreEqual("Edge confirmed", ReadingText.Quality(QualityLabel.EdgeConfirmed));
              Assert.AreEqual("Cross-checked", ReadingText.Quality(QualityLabel.CrossChecked));
              Assert.AreEqual("Unavailable", ReadingText.Quality(QualityLabel.Unavailable));
          }

          [Test]
          public void Failure_TellsTheUserWhatToDo()
          {
              Assert.AreEqual("aim at a wall", ReadingText.Failure(FailureReason.NoWallUnderCrosshair));
              Assert.AreEqual("scan slowly", ReadingText.Failure(FailureReason.NoWallInView));
              Assert.AreEqual("tracking lost", ReadingText.Failure(FailureReason.TrackingLost));
              Assert.AreEqual("wall lost", ReadingText.Failure(FailureReason.CandidateLost));
              Assert.AreEqual("AR not tracking", ReadingText.Failure(FailureReason.SessionNotTracking));
              Assert.AreEqual("point at the floor for a moment", ReadingText.Failure(FailureReason.NoFloor));
              Assert.AreEqual("show more floor", ReadingText.Failure(FailureReason.AlignmentFailed));
              Assert.AreEqual("ML depth unavailable", ReadingText.Failure(FailureReason.InferenceUnavailable));
              Assert.AreEqual("detecting…", ReadingText.Failure(FailureReason.InferenceStale));
              Assert.AreEqual("hold the phone more upright", ReadingText.Failure(FailureReason.NoHeading));
              Assert.AreEqual("no wall seen on this side", ReadingText.Failure(FailureReason.NoWallOnSide));
              Assert.AreEqual("not ready", ReadingText.Failure(FailureReason.None));
          }

          [Test]
          public void Sides_FormatsBothWallsAndWidth()
          {
              Assert.AreEqual("L 0.84 m | R 1.12 m | W 1.96 m",
                  ReadingText.Sides(Valid(MeasurementKind.CorridorLeft, 0.84f), Valid(MeasurementKind.CorridorRight, 1.12f), 1.96f));
              Assert.AreEqual("L — | R 1.12 m | W —",
                  ReadingText.Sides(Bad(MeasurementKind.CorridorLeft, FailureReason.NoWallOnSide), Valid(MeasurementKind.CorridorRight, 1.12f), float.NaN));
          }

          [Test]
          public void SidesHint_NamesTheMissingSide()
          {
              var l = Valid(MeasurementKind.CorridorLeft, 1f);
              var r = Valid(MeasurementKind.CorridorRight, 1f);
              Assert.AreEqual("", ReadingText.SidesHint(l, r));
              var noHeadingL = Bad(MeasurementKind.CorridorLeft, FailureReason.NoHeading);
              var noHeadingR = Bad(MeasurementKind.CorridorRight, FailureReason.NoHeading);
              Assert.AreEqual("hold the phone more upright", ReadingText.SidesHint(noHeadingL, noHeadingR));
              Assert.AreEqual("right: no wall seen on this side", ReadingText.SidesHint(l, Bad(MeasurementKind.CorridorRight, FailureReason.NoWallOnSide)));
              Assert.AreEqual("left: tracking lost · right: no wall seen on this side",
                  ReadingText.SidesHint(Bad(MeasurementKind.CorridorLeft, FailureReason.TrackingLost), Bad(MeasurementKind.CorridorRight, FailureReason.NoWallOnSide)));
          }
      }
  }
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  Run the test command. Expected: compilation error, `'ReadingText' does not exist`.

- [ ] **Step 3: Implement `ReadingText.cs`**

  ```csharp
  using System.Globalization;

  namespace WallDistance.Core
  {
      /// <summary>
      /// Every user-facing string for readings, in one tested place. Failure texts are
      /// instructions ("point at the floor"), not diagnoses, because the person holding the
      /// phone is the only one who can fix them.
      /// </summary>
      public static class ReadingText
      {
          static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

          public static string Quality(QualityLabel q)
          {
              switch (q)
              {
                  case QualityLabel.DepthValidated: return "Depth validated";
                  case QualityLabel.PlaneEstimate: return "Plane estimate";
                  case QualityLabel.DepthEstimate: return "Depth estimate (no plane)";
                  case QualityLabel.AssistedEstimate: return "Assisted floor geometry";
                  case QualityLabel.Unreliable: return "Unreliable";
                  case QualityLabel.OutOfTestedRange: return "Out of tested range";
                  case QualityLabel.LearnedEstimate: return "Learned depth estimate";
                  case QualityLabel.EdgeConfirmed: return "Edge confirmed";
                  case QualityLabel.CrossChecked: return "Cross-checked";
                  default: return "Unavailable";
              }
          }

          public static string Failure(FailureReason f)
          {
              switch (f)
              {
                  case FailureReason.NoWallUnderCrosshair: return "aim at a wall";
                  case FailureReason.NoWallInView: return "scan slowly";
                  case FailureReason.TrackingLost: return "tracking lost";
                  case FailureReason.CandidateLost: return "wall lost";
                  case FailureReason.SessionNotTracking: return "AR not tracking";
                  case FailureReason.NoFloor: return "point at the floor for a moment";
                  case FailureReason.AlignmentFailed: return "show more floor";
                  case FailureReason.InferenceUnavailable: return "ML depth unavailable";
                  case FailureReason.InferenceStale: return "detecting…";
                  case FailureReason.NoHeading: return "hold the phone more upright";
                  case FailureReason.NoWallOnSide: return "no wall seen on this side";
                  default: return "not ready";
              }
          }

          /// <summary>"L 0.84 m | R 1.12 m | W 1.96 m"; an em dash for anything not measured.</summary>
          public static string Sides(WallReading left, WallReading right, float widthMeters) =>
              "L " + Metres(left) + " | R " + Metres(right) + " | W " +
              (float.IsNaN(widthMeters) ? "—" : widthMeters.ToString("F2", Inv) + " m");

          /// <summary>What to do about missing sides; empty when both are measured.</summary>
          public static string SidesHint(WallReading left, WallReading right)
          {
              if (left.isValid && right.isValid) return "";
              if (!left.isValid && !right.isValid)
                  return left.failure == right.failure
                      ? Failure(left.failure)
                      : "left: " + Failure(left.failure) + " · right: " + Failure(right.failure);
              return !left.isValid ? "left: " + Failure(left.failure) : "right: " + Failure(right.failure);
          }

          static string Metres(WallReading r) => r.isValid && !float.IsNaN(r.distanceMeters)
              ? r.distanceMeters.ToString("F2", Inv) + " m"
              : "—";
      }
  }
  ```

- [ ] **Step 4: Use it in `WallDistanceHud.cs`**

  **4a.** In `Render`, replace `Describe(r.failure)` with `ReadingText.Failure(r.failure)`, and `Describe(r.quality)` with `ReadingText.Quality(r.quality)`.

  **4b.** Delete both `static string Describe(...)` methods.

  **4c.** Replace `QualityColor` with:

  ```csharp
          static Color QualityColor(QualityLabel q)
          {
              switch (q)
              {
                  // Green only where a second, independent check passed or the edge was confirmed.
                  case QualityLabel.DepthValidated: return ColorGood;
                  case QualityLabel.CrossChecked: return ColorGood;
                  case QualityLabel.EdgeConfirmed: return ColorGood;
                  case QualityLabel.LearnedEstimate: return ColorEstimate;
                  case QualityLabel.PlaneEstimate: return ColorEstimate;
                  case QualityLabel.DepthEstimate: return ColorEstimate;
                  case QualityLabel.AssistedEstimate: return new Color(0.3f,0.85f,1f);
                  case QualityLabel.OutOfTestedRange: return ColorEstimate;
                  default: return ColorBad;
              }
          }
  ```

  `EdgeConfirmed` is green because the base is a measured image edge on a tracked floor, which is an independent geometric check of the network. This is a proposal: if Rachit wants green reserved for `CrossChecked`, change that one line.

- [ ] **Step 5: Run the tests to verify they pass**

  Run the test command. Expected: PASS. A green run also proves the HUD still compiles.

- [ ] **Step 6: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Move reading text into Core and add strings for learned detection and sides"
  ```

---

### Task 17: Native plugin `libwalldepth.so` (QNN HTP, context binary)

The plugin loads the Phase 1 context binary once, runs it on its own thread, and gives C# a seven-call C API (deviation D3). Its input/output conversion is pure C++ in its own header, so it is unit-tested by itself. A self-test executable then checks the whole plugin against `qnn-net-run`'s output for the same frame. Both tests run on the phone via adb; only the NDK is needed, no host compiler.

> **Evidence vs inference.** The QNN type, function and enum names below come from the QAIRT SDK's public headers (`include/QNN/...`) and the HTP sample code, **recalled, not copied from the pinned 2.50 headers**. Step 5 compiles against the pinned headers and is the check. If a name differs, use the header's name and note the change in `docs/phase1-runtime-spike.md`.
>
> Two places are version-dependent on purpose: `QnnSystemContext_BinaryInfo_t` / `QnnSystemContext_GraphInfo_t` (handled for versions 1–3). If the pinned headers define no `_VERSION_3`, delete those two `case` lines. If the context reports a version that is not handled, `wd_init` fails with a message naming it; add a `case` for it.

**Files:**
- Create:
  - `native/walldepth/CMakeLists.txt`
  - `native/walldepth/include/walldepth.h`
  - `native/walldepth/src/tensor_convert.h`
  - `native/walldepth/src/walldepth.cpp`
  - `native/walldepth/test/convert_test.cpp`
  - `native/walldepth/test/selftest.cpp`
  - `native/walldepth/build-android.ps1`
  - `Assets/WallDistance/Editor/QnnAndroidBuild.cs`
- Modify: `.gitignore`

**Interfaces:**
- Consumes (Task 7):
  - `QNN_SDK_ROOT`;
  - the context binary in `Assets/StreamingAssets/Models/`;
  - the device copies of the Phase 1 inputs/outputs (`/data/local/tmp/qnn/...`);
  - `Builds/dumps/InferenceDumps`.
- Produces the C API (Task 18 P/Invokes it):
  - `int wd_init(const char* context_path, const char* native_lib_dir, char* err, int err_len)`: 0 = ok
  - `int wd_input_size(void)`: RGB bytes per submit, 518·518·3
  - `int wd_output_size(void)`: floats per result, 518·518
  - `int wd_submit(const uint8_t* rgb, int len)`: 1 = accepted, 0 = busy, −1 = failed
  - `int wd_poll(float* out, int len, double* inference_ms)`: 1 = copied, 0 = not ready, −1 = failed
  - `int wd_describe(char* buf, int len)`
  - `int wd_last_error(char* buf, int len)`
  - `void wd_shutdown(void)`
  - Staged libraries in `Assets/Plugins/Android/libs/arm64-v8a/`: `libwalldepth.so`, `libQnnHtp.so`, `libQnnHtpV75Stub.so`, `libQnnHtpV75Skel.so`, `libQnnSystem.so`. All are git-ignored.

- [ ] **Step 1: Write the conversion unit test (fails: header missing)**

  `native/walldepth/test/convert_test.cpp`:

  ```cpp
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
  ```

- [ ] **Step 2: Write the build files**

  `native/walldepth/CMakeLists.txt`:

  ```cmake
  # libwalldepth.so: QNN HTP depth inference for the Unity app (arm64-v8a only).
  # Configure through build-android.ps1, which supplies Unity's NDK toolchain and QNN_SDK_ROOT.
  cmake_minimum_required(VERSION 3.22)
  project(walldepth CXX)

  set(CMAKE_CXX_STANDARD 17)
  set(CMAKE_CXX_STANDARD_REQUIRED ON)

  if(NOT QNN_SDK_ROOT)
    message(FATAL_ERROR "Pass -DQNN_SDK_ROOT=<QAIRT SDK root> (see Task 7, Step 1)")
  endif()
  set(QNN_INCLUDE "${QNN_SDK_ROOT}/include/QNN")
  if(NOT EXISTS "${QNN_INCLUDE}/QnnInterface.h")
    message(FATAL_ERROR "QnnInterface.h not found under ${QNN_INCLUDE}")
  endif()

  # The QNN libraries are dlopen()ed at runtime, so nothing links against them here: the APK
  # carries the exact versions pinned in tools/models.lock.json.
  add_library(walldepth SHARED src/walldepth.cpp)
  target_include_directories(walldepth PUBLIC include PRIVATE src ${QNN_INCLUDE})
  target_compile_options(walldepth PRIVATE -Wall -Wextra -O2 -fvisibility=hidden)
  target_link_libraries(walldepth PRIVATE log dl)
  # 16 KB-aligned segments: required by Google Play for apps targeting Android 15+.
  target_link_options(walldepth PRIVATE -Wl,-z,max-page-size=16384 -Wl,--gc-sections)

  add_executable(wd_convert_test test/convert_test.cpp)
  target_include_directories(wd_convert_test PRIVATE src)

  add_executable(wd_selftest test/selftest.cpp)
  target_link_libraries(wd_selftest PRIVATE walldepth)
  ```

  `native/walldepth/build-android.ps1`:

  ```powershell
  <#
  .SYNOPSIS
    Build libwalldepth.so and its two test executables for arm64-v8a with Unity's bundled NDK
    and CMake, then stage the plugin plus the pinned QNN runtime libraries for the APK.
  #>
  param(
      [string]$UnityAndroid = 'C:\Program Files\Unity\Hub\Editor\6000.3.5f1\Editor\Data\PlaybackEngines\AndroidPlayer'
  )
  $ErrorActionPreference = 'Stop'
  $sdk = $env:QNN_SDK_ROOT
  if (-not $sdk) { throw 'QNN_SDK_ROOT is not set (Task 7, Step 1)' }
  $here = Split-Path -Parent $PSCommandPath
  $repo = Split-Path -Parent (Split-Path -Parent $here)
  $ndk = Join-Path $UnityAndroid 'NDK'
  $cmakeBin = Join-Path $UnityAndroid 'SDK\cmake\3.22.1\bin'
  $build = Join-Path $here 'build'
  function Fwd([string]$p) { $p -replace '\\', '/' }

  & (Join-Path $cmakeBin 'cmake.exe') -S $here -B $build -G Ninja `
      "-DCMAKE_MAKE_PROGRAM=$(Fwd (Join-Path $cmakeBin 'ninja.exe'))" `
      "-DCMAKE_TOOLCHAIN_FILE=$(Fwd (Join-Path $ndk 'build\cmake\android.toolchain.cmake'))" `
      -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-25 -DANDROID_STL=c++_static `
      -DCMAKE_BUILD_TYPE=Release "-DQNN_SDK_ROOT=$(Fwd $sdk)"
  if ($LASTEXITCODE -ne 0) { throw "cmake configure failed ($LASTEXITCODE)" }
  & (Join-Path $cmakeBin 'cmake.exe') --build $build
  if ($LASTEXITCODE -ne 0) { throw "cmake build failed ($LASTEXITCODE)" }

  # Unity treats Plugins/Android/libs/<abi>/*.so as Android plugins for that ABI by folder convention.
  $dest = Join-Path $repo 'Assets\Plugins\Android\libs\arm64-v8a'
  New-Item -ItemType Directory -Force $dest | Out-Null
  Copy-Item (Join-Path $build 'libwalldepth.so') $dest -Force
  foreach ($f in 'libQnnHtp.so', 'libQnnHtpV75Stub.so', 'libQnnSystem.so') {
      Copy-Item (Join-Path $sdk "lib\aarch64-android\$f") $dest -Force
  }
  # The skeleton runs on the Hexagon DSP; the "unsigned" build loads on production phones via
  # unsigned PD, the same file Task 7 used with qnn-net-run.
  Copy-Item (Join-Path $sdk 'lib\hexagon-v75\unsigned\libQnnHtpV75Skel.so') $dest -Force
  Get-ChildItem $dest -Filter *.so | ForEach-Object { '{0,-24} {1,12:N0} bytes' -f $_.Name, $_.Length }
  ```

  In `.gitignore`, under the "ML model weights" block, add:

  ```gitignore
  # QNN runtime libraries are not ours to commit (licence: Task 7, Step 2), and our own plugin
  # is a build output of native/walldepth.
  /Assets/Plugins/Android/libs/
  /Assets/Plugins/Android/libs.meta
  /native/walldepth/build/
  ```

- [ ] **Step 3: Run the conversion test to verify it fails**

  ```powershell
  powershell -ExecutionPolicy Bypass -File native/walldepth/build-android.ps1
  ```

  Expected: a compile error, `'tensor_convert.h' file not found` (and `walldepth.cpp` missing).

- [ ] **Step 4: Implement `tensor_convert.h` and the public header**

  `native/walldepth/src/tensor_convert.h`:

  ```cpp
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
  ```

  `native/walldepth/include/walldepth.h`:

  ```c
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
  ```

- [ ] **Step 5: Implement `walldepth.cpp`**

  ```cpp
  // libwalldepth.so: runs the pinned depth network on the Hexagon NPU through QNN.
  //
  // Load path (deviation D1): an offline-compiled HTP context binary, not a DLC. No on-device
  // graph preparation, so init is faster and has fewer failure points. Tensor metadata comes
  // from QnnSystem's binary-info parser and is copied out, so the binary (tens of MB) is freed
  // after load.
  #include "walldepth.h"
  #include "tensor_convert.h"

  #include <android/log.h>
  #include <dlfcn.h>

  #include <chrono>
  #include <condition_variable>
  #include <cstdarg>
  #include <cstdio>
  #include <cstdlib>
  #include <cstring>
  #include <fstream>
  #include <mutex>
  #include <string>
  #include <thread>
  #include <vector>

  #include "QnnInterface.h"
  #include "System/QnnSystemInterface.h"
  #include "HTP/QnnHtpDevice.h"
  #include "HTP/QnnHtpPerfInfrastructure.h"

  #define LOGI(...) __android_log_print(ANDROID_LOG_INFO, "walldepth", __VA_ARGS__)
  #define LOGE(...) __android_log_print(ANDROID_LOG_ERROR, "walldepth", __VA_ARGS__)

  namespace {

  constexpr int kSize = 518;

  typedef Qnn_ErrorHandle_t (*GetProvidersFn)(const QnnInterface_t*** providers, uint32_t* count);
  typedef Qnn_ErrorHandle_t (*GetSystemProvidersFn)(const QnnSystemInterface_t*** providers, uint32_t* count);

  // A graph tensor copied out of the binary's metadata. The QNN tensor view points into this
  // struct's own members, so an OwnedTensor must never be copied or moved after Build().
  struct OwnedTensor {
      uint32_t id = 0;
      std::string name;
      Qnn_TensorType_t type{};
      Qnn_TensorDataFormat_t dataFormat{};
      Qnn_DataType_t dataType{};
      Qnn_QuantizeParams_t quant{};     // scale/offset only (checked), so the copy owns no pointers
      std::vector<uint32_t> dims;
      std::vector<uint8_t> buffer;
      Qnn_Tensor_t tensor{};
      wd::DType dtype = wd::DType::F32;
      wd::Quant q;

      OwnedTensor() = default;
      OwnedTensor(const OwnedTensor&) = delete;
      OwnedTensor& operator=(const OwnedTensor&) = delete;

      size_t Count() const { size_t n = 1; for (uint32_t d : dims) n *= d; return n; }
  };

  enum class State { Idle, Pending, Done, Failed };

  struct Engine {
      void* htpLib = nullptr;
      void* sysLib = nullptr;
      QNN_INTERFACE_VER_TYPE qnn{};
      Qnn_LogHandle_t log = nullptr;
      Qnn_BackendHandle_t backend = nullptr;
      Qnn_DeviceHandle_t device = nullptr;
      Qnn_ContextHandle_t context = nullptr;
      Qnn_GraphHandle_t graph = nullptr;
      OwnedTensor input, output;
      wd::Layout layout = wd::Layout::NHWC;
      bool burst = false;
      std::string description;

      std::vector<uint8_t> staging;   // copy of the caller's RGB, so the caller may reuse its buffer
      std::vector<float> result;
      double lastMs = 0;

      std::thread worker;
      std::mutex m;
      std::condition_variable cv;
      State state = State::Idle;
      bool stop = false;
      std::string error;
  };

  Engine* g = nullptr;
  std::mutex g_life;          // serialises wd_init against wd_shutdown
  std::string g_lastError;    // init errors, before an Engine exists

  void CopyOut(const std::string& s, char* buf, int len)
  {
      if (!buf || len <= 0) return;
      std::snprintf(buf, static_cast<size_t>(len), "%s", s.c_str());
  }

  void QnnLog(const char* fmt, QnnLog_Level_t level, uint64_t /*timestamp*/, va_list args)
  {
      int prio = level == QNN_LOG_LEVEL_ERROR ? ANDROID_LOG_ERROR : level == QNN_LOG_LEVEL_WARN ? ANDROID_LOG_WARN : ANDROID_LOG_INFO;
      __android_log_vprint(prio, "walldepth-qnn", fmt, args);
  }

  // ---- version-tolerant accessors (see the evidence note in the plan) ----

  template <class F> bool VisitTensor(const Qnn_Tensor_t& t, F&& f)
  {
      switch (t.version) {
          case QNN_TENSOR_VERSION_1: f(t.v1); return true;
          case QNN_TENSOR_VERSION_2: f(t.v2); return true;
          default: return false;
      }
  }

  template <class F> bool VisitGraphs(const QnnSystemContext_BinaryInfo_t* info, F&& f)
  {
      switch (info->version) {
          case QNN_SYSTEM_CONTEXT_BINARY_INFO_VERSION_1: f(info->contextBinaryInfoV1.numGraphs, info->contextBinaryInfoV1.graphs); return true;
          case QNN_SYSTEM_CONTEXT_BINARY_INFO_VERSION_2: f(info->contextBinaryInfoV2.numGraphs, info->contextBinaryInfoV2.graphs); return true;
          case QNN_SYSTEM_CONTEXT_BINARY_INFO_VERSION_3: f(info->contextBinaryInfoV3.numGraphs, info->contextBinaryInfoV3.graphs); return true;
          default: return false;
      }
  }

  template <class F> bool VisitGraphInfo(const QnnSystemContext_GraphInfo_t& gi, F&& f)
  {
      switch (gi.version) {
          case QNN_SYSTEM_CONTEXT_GRAPH_INFO_VERSION_1: f(gi.graphInfoV1); return true;
          case QNN_SYSTEM_CONTEXT_GRAPH_INFO_VERSION_2: f(gi.graphInfoV2); return true;
          case QNN_SYSTEM_CONTEXT_GRAPH_INFO_VERSION_3: f(gi.graphInfoV3); return true;
          default: return false;
      }
  }

  bool CopyTensor(const Qnn_Tensor_t& src, OwnedTensor& dst, std::string& err)
  {
      bool known = VisitTensor(src, [&](const auto& t) {
          dst.id = t.id;
          dst.name = t.name ? t.name : "";
          dst.type = t.type;
          dst.dataFormat = t.dataFormat;
          dst.dataType = t.dataType;
          dst.quant = t.quantizeParams;
          dst.dims.assign(t.dimensions, t.dimensions + t.rank);
      });
      if (!known) { err = "unsupported Qnn_Tensor_t version " + std::to_string(src.version); return false; }

      switch (dst.dataType) {
          case QNN_DATATYPE_FLOAT_32: dst.dtype = wd::DType::F32; break;
          case QNN_DATATYPE_FLOAT_16: dst.dtype = wd::DType::F16; break;
          case QNN_DATATYPE_UFIXED_POINT_8: dst.dtype = wd::DType::U8; break;
          case QNN_DATATYPE_UFIXED_POINT_16: dst.dtype = wd::DType::U16; break;
          default:
              err = dst.name + ": unsupported data type " + std::to_string(static_cast<int>(dst.dataType));
              return false;
      }
      if (wd::IsQuantised(dst.dtype)) {
          // Per-channel encodings would need per-channel arrays; this model (Task 7) uses one scale.
          if (dst.quant.encodingDefinition != QNN_DEFINITION_DEFINED
              || dst.quant.quantizationEncoding != QNN_QUANTIZATION_ENCODING_SCALE_OFFSET) {
              err = dst.name + ": only per-tensor scale/offset quantisation is supported";
              return false;
          }
          dst.q.scale = dst.quant.scaleOffsetEncoding.scale;
          dst.q.offset = dst.quant.scaleOffsetEncoding.offset;
      }
      dst.buffer.assign(dst.Count() * wd::BytesPer(dst.dtype), 0);

      // Version 1 tensors are accepted by graphExecute in every SDK release.
      std::memset(&dst.tensor, 0, sizeof dst.tensor);
      dst.tensor.version = QNN_TENSOR_VERSION_1;
      Qnn_TensorV1_t& v = dst.tensor.v1;
      v.id = dst.id;
      v.name = dst.name.c_str();
      v.type = dst.type;
      v.dataFormat = dst.dataFormat;
      v.dataType = dst.dataType;
      v.quantizeParams = dst.quant;
      v.rank = static_cast<uint32_t>(dst.dims.size());
      v.dimensions = dst.dims.data();
      v.memType = QNN_TENSORMEMTYPE_RAW;
      v.clientBuf.data = dst.buffer.data();
      v.clientBuf.dataSize = static_cast<uint32_t>(dst.buffer.size());
      return true;
  }

  const char* TypeName(wd::DType t)
  {
      switch (t) { case wd::DType::F32: return "f32"; case wd::DType::F16: return "f16"; case wd::DType::U8: return "u8"; default: return "u16"; }
  }

  std::string Describe(const OwnedTensor& t)
  {
      std::string dims;
      for (size_t i = 0; i < t.dims.size(); ++i) dims += (i ? "x" : "") + std::to_string(t.dims[i]);
      char q[96] = "";
      if (wd::IsQuantised(t.dtype)) std::snprintf(q, sizeof q, " scale=%g offset=%d", t.q.scale, t.q.offset);
      return t.name + " [" + dims + "] " + TypeName(t.dtype) + q;
  }

  // Ask the HTP for its highest clocks and no sleep (QNN's documented "burst" settings), the
  // profile Phase 1 measured with qnn-net-run. Failure only costs speed, so it is not fatal.
  bool SetBurst(Engine& e)
  {
      QnnDevice_Infrastructure_t infra = nullptr;
      if (!e.qnn.deviceGetInfrastructure || e.qnn.deviceGetInfrastructure(&infra) != QNN_SUCCESS || !infra) return false;
      auto* htp = static_cast<QnnHtpDevice_Infrastructure_t*>(infra);
      if (htp->infraType != QNN_HTP_DEVICE_INFRASTRUCTURE_TYPE_PERF) return false;
      QnnHtpDevice_PerfInfrastructure_t& perf = htp->perfInfra;
      uint32_t id = 0;
      if (perf.createPowerConfigId(0, 0, &id) != QNN_SUCCESS) return false;

      QnnHtpPerfInfrastructure_PowerConfig_t c;
      std::memset(&c, 0, sizeof c);
      c.option = QNN_HTP_PERF_INFRASTRUCTURE_POWER_CONFIGOPTION_DCVS_V3;
      auto& d = c.dcvsV3Config;
      d.contextId = id;
      d.setDcvsEnable = 1;
      d.dcvsEnable = 0;
      d.powerMode = QNN_HTP_PERF_INFRASTRUCTURE_POWERMODE_PERFORMANCE_MODE;
      d.setSleepLatency = 1;
      d.sleepLatency = 40;
      d.setSleepDisable = 1;
      d.sleepDisable = 1;
      d.setBusParams = 1;
      d.busVoltageCornerMin = d.busVoltageCornerTarget = d.busVoltageCornerMax = DCVS_VOLTAGE_VCORNER_MAX_VOLTAGE_CORNER;
      d.setCoreParams = 1;
      d.coreVoltageCornerMin = d.coreVoltageCornerTarget = d.coreVoltageCornerMax = DCVS_VOLTAGE_VCORNER_MAX_VOLTAGE_CORNER;
      const QnnHtpPerfInfrastructure_PowerConfig_t* configs[] = {&c, nullptr};
      return perf.setPowerConfig(id, configs) == QNN_SUCCESS;
  }

  void WorkerLoop(Engine* e)
  {
      for (;;) {
          std::unique_lock<std::mutex> lk(e->m);
          e->cv.wait(lk, [&] { return e->stop || e->state == State::Pending; });
          if (e->stop) return;
          lk.unlock();

          // While Pending, wd_submit refuses new frames, so only this thread touches the buffers.
          auto t0 = std::chrono::steady_clock::now();
          bool ok = wd::PackInput(e->staging.data(), kSize, e->layout, e->input.dtype, e->input.q,
                                  e->input.buffer.data(), e->input.buffer.size());
          auto t1 = std::chrono::steady_clock::now();
          Qnn_ErrorHandle_t rc = ok ? e->qnn.graphExecute(e->graph, &e->input.tensor, 1, &e->output.tensor, 1, nullptr, nullptr)
                                    : QNN_SUCCESS;
          auto t2 = std::chrono::steady_clock::now();
          ok = ok && rc == QNN_SUCCESS
               && wd::UnpackOutput(e->output.buffer.data(), e->result.size(), e->output.dtype, e->output.q, e->result.data());
          double packMs = std::chrono::duration<double, std::milli>(t1 - t0).count();
          double execMs = std::chrono::duration<double, std::milli>(t2 - t1).count();

          lk.lock();
          if (ok) {
              // The reported time is the NPU execute only: that is what the rate governor and the
              // Phase 1 gate are about. Packing is logged separately.
              e->lastMs = execMs;
              e->state = State::Done;
              LOGI("inference %.1f ms (pack %.1f ms)", execMs, packMs);
          } else {
              e->error = "graphExecute failed: QNN error " + std::to_string(static_cast<unsigned long>(rc));
              e->state = State::Failed;
              LOGE("%s", e->error.c_str());
          }
      }
  }

  void FreeEngine(Engine* e)
  {
      if (e->worker.joinable()) {
          { std::lock_guard<std::mutex> lk(e->m); e->stop = true; }
          e->cv.notify_all();
          e->worker.join();
      }
      if (e->context && e->qnn.contextFree) e->qnn.contextFree(e->context, nullptr);
      if (e->device && e->qnn.deviceFree) e->qnn.deviceFree(e->device);
      if (e->backend && e->qnn.backendFree) e->qnn.backendFree(e->backend);
      if (e->log && e->qnn.logFree) e->qnn.logFree(e->log);
      if (e->sysLib) dlclose(e->sysLib);
      if (e->htpLib) dlclose(e->htpLib);
      delete e;
  }

  bool Init(Engine& e, const char* contextPath, const char* libDir, std::string& err)
  {
      // The HTP stub hands the skeleton library to the DSP; FastRPC looks for it on this path.
      std::string adsp = std::string(libDir) + ";/vendor/lib/rfsa/adsp;/vendor/dsp/cdsp;/system/lib/rfsa/adsp;/dsp";
      setenv("ADSP_LIBRARY_PATH", adsp.c_str(), 1);

      e.htpLib = dlopen("libQnnHtp.so", RTLD_NOW | RTLD_LOCAL);
      if (!e.htpLib) { err = std::string("dlopen libQnnHtp.so: ") + dlerror(); return false; }
      auto getProviders = reinterpret_cast<GetProvidersFn>(dlsym(e.htpLib, "QnnInterface_getProviders"));
      const QnnInterface_t** providers = nullptr;
      uint32_t n = 0;
      if (!getProviders || getProviders(&providers, &n) != QNN_SUCCESS || n == 0) { err = "QnnInterface_getProviders failed"; return false; }
      bool found = false;
      for (uint32_t i = 0; i < n && !found; ++i) {
          if (providers[i]->apiVersion.coreApiVersion.major == QNN_API_VERSION_MAJOR
              && providers[i]->apiVersion.coreApiVersion.minor >= QNN_API_VERSION_MINOR) {
              e.qnn = providers[i]->QNN_INTERFACE_VER_NAME;
              found = true;
          }
      }
      if (!found) { err = "libQnnHtp.so API version does not match the headers this plugin was built with"; return false; }

      e.sysLib = dlopen("libQnnSystem.so", RTLD_NOW | RTLD_LOCAL);
      if (!e.sysLib) { err = std::string("dlopen libQnnSystem.so: ") + dlerror(); return false; }
      auto getSys = reinterpret_cast<GetSystemProvidersFn>(dlsym(e.sysLib, "QnnSystemInterface_getProviders"));
      const QnnSystemInterface_t** sysProviders = nullptr;
      if (!getSys || getSys(&sysProviders, &n) != QNN_SUCCESS || n == 0) { err = "QnnSystemInterface_getProviders failed"; return false; }
      QNN_SYSTEM_INTERFACE_VER_TYPE sys{};
      found = false;
      for (uint32_t i = 0; i < n && !found; ++i) {
          if (sysProviders[i]->systemApiVersion.major == QNN_SYSTEM_API_VERSION_MAJOR
              && sysProviders[i]->systemApiVersion.minor >= QNN_SYSTEM_API_VERSION_MINOR) {
              sys = sysProviders[i]->QNN_SYSTEM_INTERFACE_VER_NAME;
              found = true;
          }
      }
      if (!found) { err = "libQnnSystem.so API version does not match the headers"; return false; }

      std::vector<uint8_t> blob;
      {
          std::ifstream f(contextPath, std::ios::binary | std::ios::ate);
          if (!f) { err = std::string("cannot open ") + contextPath; return false; }
          blob.resize(static_cast<size_t>(f.tellg()));
          f.seekg(0);
          if (!f.read(reinterpret_cast<char*>(blob.data()), static_cast<std::streamsize>(blob.size()))) { err = "cannot read the context binary"; return false; }
      }

      // Tensor metadata from the binary, copied out before the system context is freed.
      QnnSystemContext_Handle_t sysCtx = nullptr;
      if (sys.systemContextCreate(&sysCtx) != QNN_SUCCESS) { err = "systemContextCreate failed"; return false; }
      const QnnSystemContext_BinaryInfo_t* info = nullptr;
      Qnn_ContextBinarySize_t infoSize = 0;
      std::string graphName;
      bool metaOk = sys.systemContextGetBinaryInfo(sysCtx, blob.data(), blob.size(), &info, &infoSize) == QNN_SUCCESS && info;
      if (!metaOk) err = "systemContextGetBinaryInfo failed (is this a context binary for this SDK?)";
      if (metaOk && !VisitGraphs(info, [&](uint32_t numGraphs, const QnnSystemContext_GraphInfo_t* graphs) {
              if (numGraphs != 1) { err = "expected 1 graph, found " + std::to_string(numGraphs); metaOk = false; return; }
              if (!VisitGraphInfo(graphs[0], [&](const auto& gi) {
                      graphName = gi.graphName ? gi.graphName : "";
                      if (gi.numGraphInputs != 1 || gi.numGraphOutputs != 1) {
                          err = "expected 1 input and 1 output";
                          metaOk = false;
                          return;
                      }
                      metaOk = CopyTensor(gi.graphInputs[0], e.input, err) && CopyTensor(gi.graphOutputs[0], e.output, err);
                  })) {
                  err = "unsupported graph info version " + std::to_string(graphs[0].version);
                  metaOk = false;
              }
          })) {
          err = "unsupported binary info version " + std::to_string(info->version);
          metaOk = false;
      }
      sys.systemContextFree(sysCtx);
      if (!metaOk) return false;

      // Input must be 518x518x3 in NHWC or NCHW; output must hold one value per pixel.
      const auto& d = e.input.dims;
      if (d.size() == 4 && d[0] == 1 && d[1] == kSize && d[2] == kSize && d[3] == 3) e.layout = wd::Layout::NHWC;
      else if (d.size() == 4 && d[0] == 1 && d[1] == 3 && d[2] == kSize && d[3] == kSize) e.layout = wd::Layout::NCHW;
      else { err = "unexpected input shape: " + Describe(e.input); return false; }
      if (e.output.Count() != static_cast<size_t>(kSize) * kSize) { err = "unexpected output shape: " + Describe(e.output); return false; }

      if (e.qnn.logCreate) e.qnn.logCreate(QnnLog, QNN_LOG_LEVEL_WARN, &e.log);
      if (e.qnn.backendCreate(e.log, nullptr, &e.backend) != QNN_SUCCESS) { err = "backendCreate failed"; return false; }
      if (e.qnn.deviceCreate && e.qnn.deviceCreate(e.log, nullptr, &e.device) != QNN_SUCCESS) { err = "deviceCreate failed"; return false; }
      if (e.qnn.contextCreateFromBinary(e.backend, e.device, nullptr, blob.data(), blob.size(), &e.context, nullptr) != QNN_SUCCESS) {
          err = "contextCreateFromBinary failed (SDK/runtime version or SoC mismatch?)";
          return false;
      }
      if (e.qnn.graphRetrieve(e.context, graphName.c_str(), &e.graph) != QNN_SUCCESS) { err = "graphRetrieve failed for " + graphName; return false; }

      e.burst = SetBurst(e);
      if (!e.burst) LOGE("could not set HTP burst clocks; running at default clocks");
      e.staging.assign(static_cast<size_t>(kSize) * kSize * 3, 0);
      e.result.assign(e.output.Count(), 0.f);
      e.description = "graph " + graphName + "; in " + Describe(e.input) + (e.layout == wd::Layout::NHWC ? " NHWC" : " NCHW")
                      + "; out " + Describe(e.output) + "; burst " + (e.burst ? "on" : "off");
      LOGI("ready: %s", e.description.c_str());
      e.worker = std::thread(WorkerLoop, &e);
      return true;
  }

  }  // namespace

  extern "C" {

  int wd_init(const char* context_path, const char* native_lib_dir, char* err, int err_len)
  {
      std::lock_guard<std::mutex> life(g_life);
      if (g) return 0;
      if (!context_path || !native_lib_dir) { g_lastError = "null path"; CopyOut(g_lastError, err, err_len); return -1; }
      auto* e = new Engine();
      std::string msg;
      if (!Init(*e, context_path, native_lib_dir, msg)) {
          LOGE("init failed: %s", msg.c_str());
          g_lastError = msg;
          CopyOut(msg, err, err_len);
          FreeEngine(e);
          return -1;
      }
      g = e;
      return 0;
  }

  int wd_input_size(void) { return g ? static_cast<int>(g->staging.size()) : 0; }
  int wd_output_size(void) { return g ? static_cast<int>(g->result.size()) : 0; }

  int wd_submit(const uint8_t* rgb, int len)
  {
      Engine* e = g;
      if (!e) return -1;
      std::lock_guard<std::mutex> lk(e->m);
      if (e->state == State::Failed) return -1;
      if (e->state != State::Idle) return 0;
      if (!rgb || len != static_cast<int>(e->staging.size())) { e->error = "wd_submit: wrong input length"; e->state = State::Failed; return -1; }
      std::memcpy(e->staging.data(), rgb, static_cast<size_t>(len));
      e->state = State::Pending;
      e->cv.notify_one();
      return 1;
  }

  int wd_poll(float* out, int len, double* inference_ms)
  {
      Engine* e = g;
      if (!e) return -1;
      std::lock_guard<std::mutex> lk(e->m);
      if (e->state == State::Failed) return -1;
      if (e->state != State::Done) return 0;
      if (!out || len != static_cast<int>(e->result.size())) { e->error = "wd_poll: wrong output length"; e->state = State::Failed; return -1; }
      std::memcpy(out, e->result.data(), e->result.size() * sizeof(float));
      if (inference_ms) *inference_ms = e->lastMs;
      e->state = State::Idle;
      return 1;
  }

  int wd_describe(char* buf, int len)
  {
      std::string s = g ? g->description : "not initialised";
      CopyOut(s, buf, len);
      return static_cast<int>(s.size());
  }

  int wd_last_error(char* buf, int len)
  {
      std::string s;
      if (g) { std::lock_guard<std::mutex> lk(g->m); s = g->error; }
      if (s.empty()) s = g_lastError;
      CopyOut(s, buf, len);
      return static_cast<int>(s.size());
  }

  void wd_shutdown(void)
  {
      std::lock_guard<std::mutex> life(g_life);
      if (!g) return;
      FreeEngine(g);
      g = nullptr;
  }

  }  // extern "C"
  ```

- [ ] **Step 6: Write the plugin self-test**

  `native/walldepth/test/selftest.cpp`:

  ```cpp
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
  ```

- [ ] **Step 7: Build, then run both tests on the phone**

  ```powershell
  powershell -ExecutionPolicy Bypass -File native/walldepth/build-android.ps1
  ```

  Expected: the build succeeds and five `.so` files are listed. If a QNN name fails to compile, fix it to match the pinned header (see the evidence note) and record the change in the spike doc.

  ```bash
  ADB="/c/Program Files/Unity/Hub/Editor/6000.3.5f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
  B=native/walldepth/build
  "$ADB" push $B/wd_convert_test $B/wd_selftest $B/libwalldepth.so /data/local/tmp/qnn/
  "$ADB" shell "chmod +x /data/local/tmp/qnn/wd_convert_test /data/local/tmp/qnn/wd_selftest && /data/local/tmp/qnn/wd_convert_test"
  ```

  Expected: `convert_test: all passed`.

  Then run the self-test on frame 0 of the Phase 1 set. `frames.txt` line 1 names the frame; `Result_0` is qnn-net-run's output for it.

  ```bash
  F=$(head -1 Builds/phase1/inputs/frames.txt)
  "$ADB" push "Builds/dumps/InferenceDumps/$F/input.rgb" /data/local/tmp/qnn/selftest_input.rgb
  "$ADB" push "$(ls Builds/phase1/out_w8a16/Result_0/*.raw | head -1)" /data/local/tmp/qnn/selftest_expected.raw
  "$ADB" shell "cd /data/local/tmp/qnn && LD_LIBRARY_PATH=/data/local/tmp/qnn ./wd_selftest depth_anything_v2_w8a16.ctx.bin /data/local/tmp/qnn selftest_input.rgb selftest_expected.raw 100"
  ```

  Expected:
  - a description line (`graph … in … out … burst on`);
  - `p50` within a few ms of Phase 1's figure;
  - `selftest: PASS`.

  If the variant chosen in Task 7 is `float`, use `out_float` and `depth_anything_v2_float.ctx.bin`.

  If it FAILS with a large `max|diff|`, compare `wd_describe`'s input encoding with `make_inputs.py`, which feeds qnn-net-run floats in [0, 1]. The usual culprit is layout or scale.

- [ ] **Step 8: Check ELF alignment and record it**

  ```bash
  RE="/c/Program Files/Unity/Hub/Editor/6000.3.5f1/Editor/Data/PlaybackEngines/AndroidPlayer/NDK/toolchains/llvm/prebuilt/windows-x86_64/bin/llvm-readelf.exe"
  for f in Assets/Plugins/Android/libs/arm64-v8a/*.so; do echo "$f"; "$RE" -lW "$f" | grep LOAD | awk '{print "  align", $NF}'; done
  ```

  Expected: `libwalldepth.so` LOAD segments show `0x4000`.

  Record each QNN library's alignment in `docs/phase1-runtime-spike.md` under a new "## Packaging" heading. They are Qualcomm's builds, so 16 KB alignment is not ours to fix. If any shows `0x1000`, add one line: "Play upload for Android 15+ targets is blocked until QAIRT ships 16 KB-aligned libraries." Side-loading on the 13R is unaffected.

- [ ] **Step 9: Make Unity package the libraries correctly**

  `Assets/WallDistance/Editor/QnnAndroidBuild.cs`:

  ```csharp
  #if UNITY_ANDROID
  using System;
  using System.Collections.Generic;
  using System.IO;
  using UnityEditor;
  using UnityEditor.Android;
  using UnityEditor.Build;
  using UnityEditor.Build.Reporting;
  using UnityEngine;

  namespace WallDistance.EditorTools
  {
      /// <summary>
      /// Two build-time guarantees for ML depth:
      /// 1. The APK contains the plugin, the QNN runtime and a context binary. Missing files fail
      ///    the build, because an APK without them silently measures with ARCore planes only.
      ///    WALLDEPTH_ALLOW_MISSING=1 builds an ARCore-only APK on purpose.
      /// 2. Native libraries are extracted to disk at install. The DSP loads
      ///    libQnnHtpV75Skel.so through ADSP_LIBRARY_PATH from a real file path, which an
      ///    uncompressed-in-APK library does not have.
      /// </summary>
      public sealed class QnnAndroidBuild : IPreprocessBuildWithReport, IPostGenerateGradleAndroidProject
      {
          const string LibDir = "Assets/Plugins/Android/libs/arm64-v8a";
          const string ModelDir = "Assets/StreamingAssets/Models";
          const string Marker = "// walldepth: extract native libraries";
          static readonly string[] RequiredLibs =
              { "libwalldepth.so", "libQnnHtp.so", "libQnnHtpV75Stub.so", "libQnnHtpV75Skel.so", "libQnnSystem.so" };

          public int callbackOrder => 0;

          public void OnPreprocessBuild(BuildReport report)
          {
              if (report.summary.platform != BuildTarget.Android) return;
              var missing = new List<string>();
              foreach (var lib in RequiredLibs)
                  if (!File.Exists(Path.Combine(LibDir, lib))) missing.Add(lib);
              if (!Directory.Exists(ModelDir) || Directory.GetFiles(ModelDir, "*.ctx.bin").Length == 0)
                  missing.Add(ModelDir + "/*.ctx.bin");
              if (missing.Count == 0) return;

              string msg = "ML depth files missing: " + string.Join(", ", missing)
                           + ". Run native/walldepth/build-android.ps1 and copy the context binary (Task 7, Step 9),"
                           + " or set WALLDEPTH_ALLOW_MISSING=1 to build an ARCore-only APK.";
              if (Environment.GetEnvironmentVariable("WALLDEPTH_ALLOW_MISSING") == "1") Debug.LogWarning(msg);
              else throw new BuildFailedException(msg);
          }

          public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
          {
              // Packaging options belong to the application module ("launcher"), next to unityLibrary.
              string launcher = Path.GetFullPath(Path.Combine(unityLibraryPath, "..", "launcher"));
              string gradle = Path.Combine(launcher, "build.gradle");
              if (!File.Exists(gradle)) gradle = Path.Combine(launcher, "build.gradle.kts");
              if (!File.Exists(gradle)) throw new BuildFailedException("No launcher build.gradle(.kts) under " + launcher);
              if (File.ReadAllText(gradle).Contains(Marker)) return;
              // The same block is valid Groovy and Kotlin DSL; Gradle merges repeated android {} blocks.
              File.AppendAllText(gradle,
                  "\n" + Marker + "\nandroid {\n    packaging {\n        jniLibs {\n            useLegacyPackaging = true\n        }\n    }\n}\n");
          }
      }
  }
  #endif
  ```

  Build and confirm that the libraries are in the APK:

  ```bash
  unity command build --target Android --outputPath Builds/Android/WallDistanceDemo.apk --options '["Development"]' --confirm true
  unzip -l Builds/Android/WallDistanceDemo.apk | grep -E "lib/arm64-v8a/lib(walldepth|QnnHtp|QnnHtpV75Stub|QnnHtpV75Skel|QnnSystem)\.so|Models/.*ctx\.bin"
  ```

  Expected: six lines, five libraries plus the context binary.

  If `unzip` is missing in Git Bash, use the PowerShell equivalent:

  ```powershell
  Add-Type -A System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::OpenRead('Builds/Android/WallDistanceDemo.apk').Entries.FullName | Select-String 'walldepth|QnnHtp|QnnSystem|ctx.bin'
  ```

  Install the APK and confirm extraction:

  ```bash
  "$ADB" install -r Builds/Android/WallDistanceDemo.apk
  D=$("$ADB" shell dumpsys package com.arnav.walldistance | grep -m1 legacyNativeLibraryDir | sed 's/.*=//' | tr -d '\r')
  "$ADB" shell ls "$D/arm64"
  ```

  Expected: the five `.so` files are listed. That proves `useLegacyPackaging` took effect.

- [ ] **Step 10: Commit**

  ```bash
  git add native/walldepth .gitignore Assets/WallDistance/Editor docs/phase1-runtime-spike.md
  git commit -m "Add libwalldepth: QNN HTP context-binary runner with C API, unit test and on-device self-test"
  ```

  `git status` must not list any `.so` or `.ctx.bin` file. If it does, fix `.gitignore` before committing.

---

### Task 18: `QnnDepthInference` — the C# backend

**Files:**
- Modify: `Assets/WallDistance/Runtime/Core/InverseDepthImage.cs` (add `MaskOutsideContent`)
- Create: `Assets/WallDistance/Runtime/AR/QnnDepthInference.cs`
- Test: `Assets/WallDistance/Tests/EditMode/InverseDepthImageMaskTests.cs`
- Modify (plan text only): Task 7, Step 9 already names `DefaultModelFileName` as the place to switch to the float model.

**Interfaces:**
- Consumes:
  - `IDepthInference`, `InferenceRequest`, `DepthInferenceScheduler.Size` (Task 5);
  - the C API (Task 17).
- Produces:
  - `void InverseDepthImage.MaskOutsideContent()`
  - `sealed class QnnDepthInference : IDepthInference`
    - `const string DefaultModelFileName = "depth_anything_v2_w8a16.ctx.bin"`
    - `static QnnDepthInference Create(MonoBehaviour host, string modelFileName)`

  `Status` reads `"QNN HTP: <wd_describe>"` when ready. Otherwise it is one line saying why not, for example `"ML depth runs on the Android device only"` or `"model missing from the APK (…)"`.

- [ ] **Step 1: Write the failing test**

  ```csharp
  using NUnit.Framework;
  using UnityEngine;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class InverseDepthImageMaskTests
      {
          [Test]
          public void MaskOutsideContent_SetsLetterboxToNaN_KeepsContent()
          {
              var img = new InverseDepthImage(4, 3) { content = new RectInt(1, 0, 2, 3) };
              for (int i = 0; i < img.values.Length; i++) img.values[i] = 1f;
              img.MaskOutsideContent();
              for (int v = 0; v < 3; v++)
              for (int u = 0; u < 4; u++)
              {
                  float x = img.values[v * 4 + u];
                  if (u == 1 || u == 2) Assert.AreEqual(1f, x, $"content pixel ({u},{v}) kept");
                  else Assert.IsTrue(float.IsNaN(x), $"letterbox pixel ({u},{v}) masked");
              }
          }
      }
  }
  ```

- [ ] **Step 2: Run the test to verify it fails**

  Run: `unity command run_tests --mode editor --filter WallDistance --filter_type assembly --timeout 240 --no-banner`

  Expected: compilation error, `'InverseDepthImage' does not contain a definition for 'MaskOutsideContent'`.

- [ ] **Step 3: Add `MaskOutsideContent` to `InverseDepthImage`**

  After `InContent`, add:

  ```csharp
          /// <summary>
          /// NaN every value outside <see cref="content"/>. The network also "sees" the black letterbox
          /// bars and returns plausible-looking depth there; NaN makes any consumer that forgets the
          /// content check skip those pixels instead of fitting walls to them.
          /// </summary>
          public void MaskOutsideContent()
          {
              for (int v = 0; v < height; v++)
              {
                  int row = v * width;
                  bool rowInside = v >= content.yMin && v < content.yMax;
                  for (int u = 0; u < width; u++)
                      if (!rowInside || u < content.xMin || u >= content.xMax) values[row + u] = float.NaN;
              }
          }
  ```

  (`width`/`height` are the constructor's dimensions. If Task 3 named them differently, use those names.)

- [ ] **Step 4: Run the test to verify it passes**

  Run the test command. Expected: PASS.

- [ ] **Step 5: Implement `QnnDepthInference.cs`**

  ```csharp
  using System;
  using System.Collections;
  using System.IO;
  using System.Runtime.InteropServices;
  using System.Text;
  using System.Threading.Tasks;
  using UnityEngine;
  using UnityEngine.Networking;
  using WallDistance.Core;

  namespace WallDistance.AR
  {
      /// <summary>
      /// IDepthInference over libwalldepth.so (QNN on the Hexagon NPU).
      ///
      /// Setup is asynchronous so app start never blocks on it:
      /// 1. Copy the context binary out of the APK. Native code cannot read compressed APK
      ///    entries, and StreamingAssets on Android is inside the APK.
      /// 2. Read the extracted native-library directory from Java (deviation D3).
      /// 3. Load the model on a worker thread. Loading a context takes about a second.
      /// Until all three succeed, IsAvailable is false and Status says why; the ARCore path keeps
      /// measuring meanwhile.
      ///
      /// One request in flight. The result is written straight into the request's own
      /// InverseDepthImage, so there is no per-frame managed allocation.
      /// </summary>
      public sealed class QnnDepthInference : IDepthInference
      {
          public const string DefaultModelFileName = "depth_anything_v2_w8a16.ctx.bin";
          const string Lib = "walldepth";

          [DllImport(Lib)] static extern int wd_init([MarshalAs(UnmanagedType.LPStr)] string contextPath,
              [MarshalAs(UnmanagedType.LPStr)] string nativeLibDir, byte[] err, int errLen);
          [DllImport(Lib)] static extern int wd_input_size();
          [DllImport(Lib)] static extern int wd_output_size();
          [DllImport(Lib)] static extern int wd_submit(byte[] rgb, int len);
          [DllImport(Lib)] static extern int wd_poll(float[] output, int len, out double inferenceMs);
          [DllImport(Lib)] static extern int wd_describe(byte[] buf, int len);
          [DllImport(Lib)] static extern int wd_last_error(byte[] buf, int len);
          [DllImport(Lib)] static extern void wd_shutdown();

          volatile bool _available, _disposed;
          bool _initialised, _busy;
          InverseDepthImage _pending;
          readonly byte[] _msg = new byte[512];

          public bool IsAvailable => _available && !_disposed;
          public bool IsBusy => _busy;
          public string Status { get; private set; } = "starting";

          QnnDepthInference() { }

          /// <summary>Start setup on <paramref name="host"/>'s coroutine runner; returns immediately.</summary>
          public static QnnDepthInference Create(MonoBehaviour host, string modelFileName)
          {
              var q = new QnnDepthInference();
  #if UNITY_ANDROID && !UNITY_EDITOR
              host.StartCoroutine(q.Setup(string.IsNullOrEmpty(modelFileName) ? DefaultModelFileName : modelFileName));
  #else
              q.Status = "ML depth runs on the Android device only";
  #endif
              return q;
          }

          IEnumerator Setup(string modelFileName)
          {
              Status = "copying model";
              string dir = Path.Combine(Application.persistentDataPath, "Models");
              string dest = Path.Combine(dir, modelFileName);
              string stamp = dest + ".build";
              Directory.CreateDirectory(dir);
              // Copy once per installed build: buildGUID changes with every build, so a new model in a
              // new APK is always picked up, and app restarts do not re-copy tens of MB.
              bool fresh = File.Exists(dest) && File.Exists(stamp) && File.ReadAllText(stamp) == Application.buildGUID;
              if (!fresh)
              {
                  string src = Path.Combine(Application.streamingAssetsPath, "Models", modelFileName);
                  using (var req = UnityWebRequest.Get(src))
                  {
                      req.downloadHandler = new DownloadHandlerFile(dest) { removeFileOnAbort = true };
                      yield return req.SendWebRequest();
                      if (req.result != UnityWebRequest.Result.Success)
                      {
                          Status = $"model missing from the APK ({modelFileName}): {req.error}";
                          yield break;
                      }
                  }
                  File.WriteAllText(stamp, Application.buildGUID);
              }
              if (_disposed) yield break;

              string libDir;
              try { libDir = NativeLibraryDir(); }
              catch (Exception e) { Status = "cannot read nativeLibraryDir: " + e.Message; yield break; }

              Status = "loading model on the NPU";
              var err = new byte[512];
              // The worker owns err until it completes; this coroutine only reads it afterwards.
              var init = Task.Run(() => wd_init(dest, libDir, err, err.Length));
              while (!init.IsCompleted) yield return null;
              if (init.IsFaulted)
              {
                  // DllNotFoundException / EntryPointNotFoundException: the APK lacks libwalldepth.so.
                  Status = "native plugin failed to load: " + init.Exception.GetBaseException().Message;
                  yield break;
              }
              if (init.Result != 0) { Status = "QNN init failed: " + Decode(err); yield break; }
              _initialised = true;
              if (_disposed) { Shutdown(); yield break; }

              int size = DepthInferenceScheduler.Size;
              if (wd_input_size() != size * size * 3 || wd_output_size() != size * size)
              {
                  Status = $"model size mismatch: input {wd_input_size()} B, output {wd_output_size()} values";
                  Shutdown();
                  yield break;
              }
              wd_describe(_msg, _msg.Length);
              Status = "QNN HTP: " + Decode(_msg);
              _available = true;
          }

          public bool TryBegin(in InferenceRequest request)
          {
              if (!IsAvailable || _busy || request.target == null || request.rgb == null) return false;
              int rc = wd_submit(request.rgb, request.rgb.Length);
              if (rc == 1)
              {
                  _pending = request.target;
                  _busy = true;
                  return true;
              }
              if (rc < 0) Fail("submit");
              return false;
          }

          public bool TryCollect(out InverseDepthImage image)
          {
              image = null;
              if (!_busy) return false;
              var target = _pending;
              int rc = wd_poll(target.values, target.values.Length, out double ms);
              if (rc == 0) return false;
              _busy = false;
              _pending = null;
              if (rc < 0) { Fail("inference"); return false; }
              target.inferenceMilliseconds = ms;
              target.MaskOutsideContent();
              image = target;
              return true;
          }

          void Fail(string stage)
          {
              wd_last_error(_msg, _msg.Length);
              Status = $"QNN {stage} failed: {Decode(_msg)}";
              _available = false;
              Debug.LogError("[QnnDepthInference] " + Status);
          }

          public void Dispose()
          {
              // If init is still running, Setup sees _disposed when it finishes and shuts down then.
              _disposed = true;
              _available = false;
              if (_initialised) Shutdown();
          }

          void Shutdown()
          {
              _initialised = false;
              wd_shutdown();
          }

          static string Decode(byte[] buf)
          {
              int n = Array.IndexOf(buf, (byte)0);
              return Encoding.UTF8.GetString(buf, 0, n < 0 ? buf.Length : n);
          }

          static string NativeLibraryDir()
          {
              using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
              using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
              using (var info = activity.Call<AndroidJavaObject>("getApplicationInfo"))
                  return info.Get<string>("nativeLibraryDir");
          }
      }
  }
  ```

- [ ] **Step 6: Run the tests (compiles the AR assembly)**

  Run the test command. Expected: PASS. In the Editor, `Create` only sets `Status`, so nothing native is touched.

- [ ] **Step 7: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Add QnnDepthInference backend and mask letterbox pixels in inference output"
  ```

---

### Task 19: Service wiring — ML backend, pipeline, wall map, anchors

**Files:**
- Create: `Assets/WallDistance/Runtime/AR/WallMapAnchoring.cs`
- Modify: `Assets/WallDistance/Runtime/AR/WallDistanceService.cs`

**Interfaces:**
- Consumes:
  - `WallDetectionPipeline` (Task 13), `MetricSampleCollector` (Task 9), `WallMap.ApplyAnchorPose/ForgetAnchor` (Task 12);
  - `QnnDepthInference` (Task 18), `DepthInferenceScheduler.FrameReady`, `FloorPlaneSource` (Task 5);
  - `EngineInput.mapWalls/up/detectionFailure` (Task 15).
- Produces:
  - `WallDistanceService.Pipeline` (`WallDetectionPipeline`)
  - `WallDistanceService.modelFileName`
  - `WallDistanceService.InferenceAvailable` (`bool`)
  - `WallDistanceService.InferenceStatus` (`string`)
  - `sealed class WallMapAnchoring`: `WallMapAnchoring(Transform parent)`, `void Sync(WallMap map)`, `void Clear()`, `int Count`

There is no EditMode test for this task: it is AR wiring. It is verified by compiling, then on the device in Step 5.

- [ ] **Step 1: Create `WallMapAnchoring.cs`**

  ```csharp
  using System.Collections.Generic;
  using UnityEngine;
  using UnityEngine.XR.ARFoundation;
  using UnityEngine.XR.ARSubsystems;
  using WallDistance.Core;

  namespace WallDistance.AR
  {
      /// <summary>
      /// One ARAnchor per established wall track. ARCore corrects anchor poses as its map
      /// improves (loop closure, relocalisation); feeding those corrections to the WallMap keeps
      /// remembered walls aligned with the world. That matters most for side walls, which are
      /// measured from memory while out of view.
      ///
      /// Anchors are created by adding an ARAnchor component (AR Foundation's documented
      /// component route); this needs an ARAnchorManager on the XR Origin, which the service adds.
      /// </summary>
      public sealed class WallMapAnchoring
      {
          // Transient tracks (one noisy observation) are not worth an anchor.
          const int MinObservations = 3;

          readonly Transform _parent;
          readonly Dictionary<string, ARAnchor> _anchors = new Dictionary<string, ARAnchor>();
          readonly HashSet<string> _live = new HashSet<string>();
          readonly List<string> _dead = new List<string>();

          public WallMapAnchoring(Transform parent) { _parent = parent; }

          public int Count => _anchors.Count;

          public void Sync(WallMap map)
          {
              _live.Clear();
              foreach (var t in map.Tracks)
              {
                  _live.Add(t.id);
                  if (!_anchors.TryGetValue(t.id, out var anchor))
                  {
                      if (t.observations < MinObservations) continue;
                      var go = new GameObject("WallAnchor " + t.id);
                      go.transform.SetParent(_parent, false);
                      go.transform.SetPositionAndRotation(t.origin, Quaternion.LookRotation(t.up, t.normal));
                      _anchors[t.id] = go.AddComponent<ARAnchor>();
                      continue;
                  }
                  if (anchor == null)
                  {
                      // ARCore removed it; the next Sync makes a new one with a fresh reference.
                      _anchors.Remove(t.id);
                      map.ForgetAnchor(t.id);
                      continue;
                  }
                  // While an anchor is not tracking, its pose is stale. Skipping the frame keeps the
                  // reference, so the whole correction is applied once tracking returns.
                  if (anchor.trackingState == TrackingState.Tracking)
                      map.ApplyAnchorPose(t.id, new Pose(anchor.transform.position, anchor.transform.rotation));
              }

              // Tracks pruned or cleared by a session change: drop their anchors.
              _dead.Clear();
              foreach (var kv in _anchors) if (!_live.Contains(kv.Key)) _dead.Add(kv.Key);
              foreach (var id in _dead)
              {
                  if (_anchors[id] != null) Object.Destroy(_anchors[id].gameObject);
                  _anchors.Remove(id);
              }
          }

          public void Clear()
          {
              foreach (var a in _anchors.Values) if (a != null) Object.Destroy(a.gameObject);
              _anchors.Clear();
          }
      }
  }
  ```

- [ ] **Step 2: Add the fields and properties to the service**

  In `WallDistanceService.cs`:

  **2a.** After `public DepthInferenceScheduler scheduler;` add:

  ```csharp
          [Header("Learned depth")]
          [Tooltip("Context binary in StreamingAssets/Models (Task 7 decides w8a16 or float).")]
          public string modelFileName = QnnDepthInference.DefaultModelFileName;
  ```

  **2b.** After `public AssistedWallController Assisted { get; private set; }` add:

  ```csharp
          /// <summary>Learned wall detection and the wall map (left/right come from here).</summary>
          public WallDetectionPipeline Pipeline { get; private set; }
          public bool InferenceAvailable => scheduler != null && scheduler.Backend != null && scheduler.Backend.IsAvailable;
          public string InferenceStatus => scheduler != null && scheduler.Backend != null ? scheduler.Backend.Status : "no scheduler";

          WallMapAnchoring _anchoring;
          readonly List<MetricSample> _metric = new List<MetricSample>(4096);
          // Raw depth is ~160x90; every 2nd pixel gives up to ~3600 samples, well above the 200 minimum.
          const int MetricStride = 2;
  ```

- [ ] **Step 3: Wire up Awake, Start, OnEnable/OnDisable and NotifySessionReset**

  **3a.** In `Awake()`, after `Engine = new WallMeasurementEngine(config);` add:

  ```csharp
              Pipeline = new WallDetectionPipeline(config);
              // Anchors need an anchor manager on the XR Origin; the scene does not have one.
              if (GetComponent<ARAnchorManager>() == null) gameObject.AddComponent<ARAnchorManager>();
              _anchoring = new WallMapAnchoring(transform);
  ```

  **3b.** Replace the Task 5 line `if (scheduler.Backend == null) scheduler.Backend = new NullDepthInference("ML backend not built yet");` with:

  ```csharp
              // Real backend; until its async setup finishes it reports unavailable and the ARCore path runs.
              scheduler.Backend?.Dispose();
              scheduler.Backend = QnnDepthInference.Create(this, modelFileName);
  ```

  **3c.** Replace `OnEnable` and `OnDisable` with:

  ```csharp
          void OnEnable()
          {
              ARSession.stateChanged += OnArStateChanged;
              if (scheduler != null) scheduler.FrameReady += OnInferenceFrame;
          }

          void OnDisable()
          {
              ARSession.stateChanged -= OnArStateChanged;
              if (scheduler != null) scheduler.FrameReady -= OnInferenceFrame;
          }
  ```

  **3d.** In `NotifySessionReset()`, after `Engine.Reset();` add:

  ```csharp
              Pipeline.Reset();
              _anchoring.Clear();
  ```

- [ ] **Step 4: Process frames and feed the engine**

  **4a.** Add this method after `NotifySessionReset`:

  ```csharp
          /// <summary>
          /// One finished inference: align it to the floor (or to raw depth when there is no floor
          /// yet) and fold its walls into the map. Runs on the main thread from the scheduler's
          /// Update, so the map is never touched concurrently.
          /// </summary>
          void OnInferenceFrame(InverseDepthImage img)
          {
              if (State != WallDistanceSessionState.Tracking || Assisted.Active) return;
              double now = Time.realtimeSinceStartupAsDouble;
              FloorPlane floor = floorSource != null && floorSource.HasFloor ? floorSource.CurrentPlane : default;
              _metric.Clear();
              // Raw-depth samples are only the no-floor fallback (spec §6), so skip the work otherwise.
              if (!floor.IsValid && depthSource != null)
                  MetricSampleCollector.Collect(depthSource.Latest, img, config.detection.metricMinConfidence, now,
                      config.depthMaxAgeSeconds, MetricStride, _metric);
              Pipeline.ProcessFrame(SessionId, img, floor, _metric, now);
          }
  ```

  **4b.** In `Update()`, replace everything from `double now = Time.realtimeSinceStartupAsDouble;` up to and including `Latest = Engine.Update(input);` with:

  ```csharp
              double now = Time.realtimeSinceStartupAsDouble;
              bool tracking = State == WallDistanceSessionState.Tracking;
              bool automatic = !Assisted.Active;
              Vector3 up = floorSource != null && floorSource.HasFloor ? floorSource.CurrentPlane.up : Vector3.up;

              // A session change empties the map before anything reads it, not at the next inference.
              Pipeline.Map.SetSession(SessionId);
              if (tracking && automatic)
              {
                  Pipeline.ObserveArPlanes(SessionId, candidateSource.Candidates, up, now);
                  Pipeline.Map.Prune(now);
                  _anchoring.Sync(Pipeline.Map);
              }

              // Automatic mode measures against the map (learned walls plus ARCore planes folded in at
              // 10 Hz). Before the map has anything, ARCore's own candidates keep the old behaviour.
              IReadOnlyList<WallCandidate> candidates;
              if (!automatic) candidates = Assisted.Candidates;
              else if (Pipeline.Map.Tracks.Count > 0) candidates = Pipeline.Map.Candidates(now);
              else candidates = candidateSource.Candidates;

              var input = new EngineInput
              {
                  sessionId = SessionId,
                  sessionTracking = tracking,
                  now = now,
                  cameraPose = new Pose(arCamera.transform.position, arCamera.transform.rotation),
                  worldToClip = arCamera.projectionMatrix * arCamera.worldToCameraMatrix,
                  crosshairRay = arCamera.ViewportPointToRay(new Vector3(crosshairViewport.x, crosshairViewport.y, 0f)),
                  candidates = candidates,
                  // Assisted mode waits for user selection; do not secretly fall back to depth.
                  depth = automatic && depthSource != null ? depthSource.Latest : null,
                  denseDepth = automatic && depthSource != null ? depthSource.LatestDense : null,
                  depthSupported = depthSource != null && depthSource.DepthSupported,
                  // Assisted mode has no map: sides are NoWallOnSide there, and no ML reasons apply.
                  mapWalls = automatic ? Pipeline.Map.Tracks : null,
                  up = up,
                  detectionFailure = automatic ? Pipeline.DetectionFailure(now, InferenceAvailable) : FailureReason.None,
              };

              Latest = Engine.Update(input);
  ```

  A known trade-off of map candidates: a wall's shape becomes the map's rectangle (observed extent × nominal height) instead of ARCore's polygon. The aimed ray can therefore hit slightly above a short wall's real top. This is accepted: corridor walls are full height.

- [ ] **Step 5: Run the tests, then check on the device**

  Run the test command. Expected: PASS. This is a compile check of the AR assembly, plus all Core tests.

  Then build, install and watch the log:

  ```bash
  unity command build --target Android --outputPath Builds/Android/WallDistanceDemo.apk --options '["Development"]' --confirm true
  ADB="/c/Program Files/Unity/Hub/Editor/6000.3.5f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
  "$ADB" install -r Builds/Android/WallDistanceDemo.apk
  "$ADB" logcat -c
  "$ADB" logcat -s walldepth walldepth-qnn Unity | grep -E "walldepth|QnnDepthInference|Exception"
  ```

  Start the app and scan a corridor floor. Expected in the log:
  - `walldepth: ready: graph … burst on`;
  - then a stream of `inference NN.N ms` lines at roughly 15 per second;
  - no `QnnDepthInference` errors and no exceptions.

- [ ] **Step 6: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "Wire QNN backend, detection pipeline, wall map and anchors into the service"
  ```

---

### Task 20: HUD and CSV — sides, ML state and the new log columns

**Files:**
- Create: `Assets/WallDistance/Runtime/Core/CsvSchema.cs`
- Create: `Assets/WallDistance/Runtime/AR/ThermalStatus.cs`
- Modify: `Assets/WallDistance/Runtime/AR/WallDistanceHud.cs`
- Modify: `Assets/WallDistance/Runtime/AR/MeasurementCsvRecorder.cs`
- Test: `Assets/WallDistance/Tests/EditMode/CsvSchemaTests.cs`

**Interfaces:**
- Consumes: `ReadingText` (Task 16), service `Pipeline`/`InferenceAvailable`/`InferenceStatus`/`scheduler`/`floorSource` (Tasks 5, 19).
- Produces:
  - `static class CsvSchema`:
    - `const string Revision = "floor-aligned-v1"`
    - `string[] LearnedColumns`
    - `string[] ReadingColumns(string prefix, bool withReason)`
    - `string[] AppendedColumns()`
  - The CSV column names that Tasks 21–22's Python reads.
  - `static class ThermalStatus { static int Current }`: Android `PowerManager` thermal status 0–6, or −1 where unavailable.

- [ ] **Step 1: Write the failing test**

  ```csharp
  using NUnit.Framework;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class CsvSchemaTests
      {
          [Test]
          public void ReadingColumns_MirrorTheExistingAimedSet()
          {
              CollectionAssert.AreEqual(new[]
              {
                  "aimed_valid", "aimed_raw_m", "aimed_filtered_m", "aimed_source", "aimed_quality", "aimed_failure", "aimed_candidate",
                  "aimed_depthResidual_m", "aimed_depthInlier", "aimed_reason",
              }, CsvSchema.ReadingColumns("aimed", true));
              Assert.AreEqual(9, CsvSchema.ReadingColumns("nearest", false).Length);
          }

          [Test]
          public void AppendedColumns_SpecSection7ThenSidesThenWidth()
          {
              var cols = CsvSchema.AppendedColumns();
              Assert.AreEqual(12 + 10 + 10 + 1, cols.Length);
              CollectionAssert.AreEqual(new[]
              {
                  "infer_ms", "infer_hz", "align_s", "align_t", "align_residual", "floor_inliers",
                  "walls_in_map", "aimed_source_chain", "edge_snap_frac", "floor_h_m", "thermal_state", "detect_latency_ms",
              }, CsvSchema.LearnedColumns);
              Assert.AreEqual("left_valid", cols[12]);
              Assert.AreEqual("right_reason", cols[31]);
              Assert.AreEqual("corridor_width_m", cols[32]);
              Assert.AreEqual("floor-aligned-v1", CsvSchema.Revision);
          }
      }
  }
  ```

- [ ] **Step 2: Run the test to verify it fails**

  Run the test command. Expected: compilation error, `'CsvSchema' does not exist`.

- [ ] **Step 3: Implement `CsvSchema.cs`**

  ```csharp
  using System.Collections.Generic;

  namespace WallDistance.Core
  {
      /// <summary>
      /// Names of the CSV columns added for learned detection (spec §7), in file order. They live in
      /// Core so a test pins them: tools/bench and tools/field read these names, and a silent
      /// rename would break the benchmark analysis without failing any build.
      /// </summary>
      public static class CsvSchema
      {
          public const string Revision = "floor-aligned-v1";

          /// <summary>Appended directly after the existing last column, "depthError".</summary>
          public static readonly string[] LearnedColumns =
          {
              "infer_ms", "infer_hz", "align_s", "align_t", "align_residual", "floor_inliers",
              "walls_in_map", "aimed_source_chain", "edge_snap_frac", "floor_h_m", "thermal_state", "detect_latency_ms",
          };

          /// <summary>The per-reading set, the same shape as the existing aimed_/nearest_ columns.</summary>
          public static string[] ReadingColumns(string prefix, bool withReason)
          {
              var names = new List<string> { "valid", "raw_m", "filtered_m", "source", "quality", "failure", "candidate", "depthResidual_m", "depthInlier" };
              if (withReason) names.Add("reason");
              for (int i = 0; i < names.Count; i++) names[i] = prefix + "_" + names[i];
              return names.ToArray();
          }

          public static string[] AppendedColumns()
          {
              var all = new List<string>(LearnedColumns);
              all.AddRange(ReadingColumns("left", true));
              all.AddRange(ReadingColumns("right", true));
              all.Add("corridor_width_m");
              return all.ToArray();
          }
      }
  }
  ```

- [ ] **Step 4: Run the test to verify it passes**

  Run the test command. Expected: PASS.

- [ ] **Step 5: Create `ThermalStatus.cs`**

  ```csharp
  using UnityEngine;

  namespace WallDistance.AR
  {
      /// <summary>
      /// Android's thermal status, 0 (none) to 6 (shutdown), sampled at most once a second. A JNI
      /// call per CSV row would cost more than the value is worth. -1 in the Editor and before
      /// Android 10, where the API does not exist.
      /// </summary>
      public static class ThermalStatus
      {
          static int _value = -1;
          static double _readAt = double.NegativeInfinity;

          public static int Current
          {
              get
              {
  #if UNITY_ANDROID && !UNITY_EDITOR
                  double now = Time.realtimeSinceStartupAsDouble;
                  if (now - _readAt < 1.0) return _value;
                  _readAt = now;
                  try
                  {
                      using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                      {
                          if (version.GetStatic<int>("SDK_INT") < 29) return _value = -1;
                      }
                      using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                      using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                      using (var power = activity.Call<AndroidJavaObject>("getSystemService", "power"))
                          _value = power.Call<int>("getCurrentThermalStatus");
                  }
                  catch (System.Exception) { _value = -1; }
  #endif
                  return _value;
              }
          }
      }
  }
  ```

- [ ] **Step 6: HUD: side readings, ML state and stats**

  In `WallDistanceHud.cs`:

  **6a.** Change `Text _aimedText, _nearestText, _stateText, _statsText, _recordText;` to:

  ```csharp
          Text _aimedText, _nearestText, _stateText, _statsText, _recordText, _sidesText;
  ```

  **6b.** In `BuildUi()`, after the line `_statsText.rectTransform.anchoredPosition = new Vector2(0,530);` add:

  ```csharp
              // Between the stats line (530) and the mode buttons (360): spec §5.5's single line.
              _sidesText = MakeText(canvasGo.transform, "Sides", font, 34, new Vector2(0.5f, 0f), new Vector2(0, 460), new Vector2(1000, 60));
  ```

  **6c.** In `OnUpdated`, in the not-tracking branch, after `_crosshair.color = ColorMuted;` add:

  ```csharp
                  _sidesText.text = "L — | R — | W —";
                  _sidesText.color = ColorMuted;
  ```

  **6d.** In the tracking (`else`) branch, replace the line `_stateText.text = s.depthSupported ? "" : "Depth API unavailable on this device: plane estimates only";` with:

  ```csharp
                  // Learned depth state: shown while loading or failed, so "planes only" is never silent.
                  string ml = service.InferenceAvailable ? "" : "ML depth: " + service.InferenceStatus + " (ARCore planes only)";
                  string depthMsg = s.depthSupported ? "" : "Depth API unavailable on this device: plane estimates only";
                  _stateText.text = ml.Length > 0 && depthMsg.Length > 0 ? ml + "\n" + depthMsg : ml + depthMsg;
  ```

  and after `_crosshair.color = s.aimed.isValid ? QualityColor(s.aimed.quality) : ColorMuted;` add:

  ```csharp
                  _sidesText.text = ReadingText.Sides(s.left, s.right, s.corridorWidthMeters);
                  string hint = ReadingText.SidesHint(s.left, s.right);
                  if (hint.Length > 0) _sidesText.text += "  (" + hint + ")";
                  _sidesText.color = s.left.isValid && s.right.isValid ? Color.white : ColorMuted;
  ```

  **6e.** Replace the `_statsText.text = ...` statement with:

  ```csharp
              var sch = service.scheduler;
              _statsText.text = $"{(1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f)):F0} fps · {service.UpdateRate:F0} upd/s · " +
                                $"ML {(sch != null ? sch.CollectedHz : 0f):F0} Hz {(sch != null ? sch.LastInferenceMs : double.NaN):F0} ms · " +
                                $"{service.Pipeline.Map.Tracks.Count} walls · " +
                                $"{service.candidateSource?.TrackedVerticalPlaneCount ?? 0}/{service.candidateSource?.TotalPlaneCount ?? 0} planes";
  ```

  This drops the "max m²" figure to keep one line; the CSV still logs `largestVerticalArea_m2`.

- [ ] **Step 7: CSV: revision, inference note and new columns**

  In `MeasurementCsvRecorder.cs`:

  **7a.** In `WriteHeader()`, replace the first `WriteLine` with:

  ```csharp
              _writer.WriteLine("# WallDistance benchmark log; measurement revision=" + CsvSchema.Revision);
              // Spec §6: an unavailable ML backend is noted once here, not stamped on every reading.
              _writer.WriteLine("# inference=" + service.InferenceStatus.Replace('\n', ' '));
  ```

  **7b.** Replace the final argument line of the `string.Join` header call:

  ```csharp
                  "confidenceInfo", "sensorPoseInfo", "cameraTimestampS", "confidenceTimestampS", "mode", "depthError"));
  ```

  with:

  ```csharp
                  "confidenceInfo", "sensorPoseInfo", "cameraTimestampS", "confidenceTimestampS", "mode", "depthError")
                  + "," + string.Join(",", CsvSchema.AppendedColumns()));
  ```

  **7c.** In `OnUpdated`, replace:

  ```csharp
              Append(Quote(service.depthSource != null ? service.depthSource.LastError : ""), last:true);
  ```

  with:

  ```csharp
              Append(Quote(service.depthSource != null ? service.depthSource.LastError : ""));

              // --- learned detection (CsvSchema.LearnedColumns, same order) ---
              var sch = service.scheduler;
              var pipe = service.Pipeline;
              var al = pipe.LastAlignment;
              Append(sch != null ? sch.LastInferenceMs : double.NaN);
              Append(sch != null ? sch.CollectedHz : float.NaN);
              Append(al != null && al.success ? al.s : float.NaN);
              Append(al != null && al.success ? al.t : float.NaN);
              Append(al != null ? al.residual : float.NaN);
              Append(al != null ? al.inliers : -1);
              Append(pipe.Map.Tracks.Count);
              Append(Quote(s.aimed.sourceChain));
              Append(pipe.LastEdgeSnapFraction);
              Append(service.floorSource != null && service.floorSource.HasFloor
                  ? service.floorSource.CurrentPlane.HeightAbove(s.aimed.cameraPose.position) : float.NaN);
              Append(ThermalStatus.Current);
              Append(pipe.LastDetectLatencyMs);

              AppendReading(s.left, true);
              AppendReading(s.right, true);
              Append(s.corridorWidthMeters, last: true);
  ```

- [ ] **Step 8: Run the tests to verify they pass**

  Run the test command. Expected: PASS (all EditMode tests; the AR assembly compiles).

- [ ] **Step 9: Check on the device**

  Build and install as in Task 19, Step 5. Walk 10 m down a corridor, centred, phone upright, recording a CSV. Pull it:

  ```bash
  "$ADB" pull /sdcard/Android/data/com.arnav.walldistance/files/WallDistanceLogs Builds/logs/
  F=$(ls -t Builds/logs/WallDistanceLogs/*.csv | head -1)
  head -2 "$F"
  /c/msys64/ucrt64/bin/python -c "import csv,sys; r=list(csv.DictReader(l for l in open(sys.argv[1]) if not l.startswith('#'))); h=r[0].keys(); print(len(h), 'columns; last =', list(h)[-1]); print(sum(x['left_valid']=='1' for x in r), sum(x['right_valid']=='1' for x in r), 'of', len(r), 'rows have left/right')" "$F"
  ```

  Expected:
  - The header lines show `revision=floor-aligned-v1` and an `# inference=QNN HTP: …` line.
  - The last column is `corridor_width_m`.
  - After the first few seconds, most rows have both sides valid.
  - The HUD shows `L x.xx m | R x.xx m | W x.xx m` and `ML ~15 Hz`.

  These numbers are not yet accuracy evidence; that is Task 22.

- [ ] **Step 10: Commit**

  ```bash
  git add Assets/WallDistance
  git commit -m "HUD and CSV: left/right/width readings, ML depth state, floor-aligned-v1 log columns"
  ```

---

### Task 21: Phase 3 gate — prepare-time logging, bench summary, 10-minute bench

Spec §8 asks for a 10-minute on-device run that logs `infer_ms` p50/p95, end-to-end detection latency, frame rate and thermal state. §9 Phase 3's gate is "bench scene meets §10 rate targets". This task adds:
- the one missing measurement: the main-thread cost of synchronous frame conversion, which deviation D2 says is measured here;
- a tested summariser;
- the run itself.

**Precondition (check before Step 10, not before Step 1).** Steps 1–9 are code and need no phone. Step 10 needs an APK where ML depth actually runs, so first confirm:
- `docs/phase1-runtime-spike.md` records the Phase 1 gate (p50 ≤ 35 ms) as PASS, not DEFERRED;
- Task 17 Steps 7–9 and Task 19 Step 5 have been done on the phone. The log shows `walldepth: ready: … burst on`, and the HUD does not show "ML depth: … (ARCore planes only)".

If either is not true, do Steps 1–9, commit, and stop. Report to Rachit which precondition is missing; if it is the QNN SDK, Task 23 applies. A bench of an ARCore-only APK measures nothing this gate is about.

**Files:**
- Modify: `Assets/WallDistance/Runtime/AR/DepthInferenceScheduler.cs` (`LastPrepareMs`)
- Modify: `Assets/WallDistance/Runtime/Core/CsvSchema.cs` (`prep_ms`)
- Modify: `Assets/WallDistance/Runtime/AR/MeasurementCsvRecorder.cs` (row value)
- Modify: `Assets/WallDistance/Tests/EditMode/CsvSchemaTests.cs`
- Create: `tools/bench/summarize_bench.py`
- Create: `tools/bench/test_summarize_bench.py`
- Create (Step 11): `docs/benchmarks/<date>-bench.md`; the raw log stays in `Builds/logs/WallDistanceLogs/` (already allowed by `.gitignore`)

**Interfaces:**
- Consumes:
  - CSV columns from Task 20 (`CsvSchema.AppendedColumns()`), plus the existing `t`, `fps`, `updRate`.
  - `DepthInferenceScheduler` (Task 5).
- Produces:
  - `double DepthInferenceScheduler.LastPrepareMs` (NaN until the first prepared frame);
  - CSV column `prep_ms`, the last entry of `CsvSchema.LearnedColumns`.
  - Python module `tools/bench/summarize_bench.py`, imported by Task 22, with these functions:
    - `read_log(path) -> (list[str], list[dict])`
    - `num(value) -> float`
    - `percentile(values, q) -> float`
    - `distinct_runs(values) -> list[float]`
    - `window_means(times, values, seconds=60.0) -> list[float]`
    - `summarize(rows) -> dict`
    - `checks(summary) -> list[tuple]`
    - `main(argv) -> int`

- [ ] **Step 1: Update the schema test (fails: no `prep_ms` yet)**

  Replace the whole of `Assets/WallDistance/Tests/EditMode/CsvSchemaTests.cs` with:

  ```csharp
  using NUnit.Framework;
  using WallDistance.Core;

  namespace WallDistance.Tests
  {
      public class CsvSchemaTests
      {
          [Test]
          public void ReadingColumns_MirrorTheExistingAimedSet()
          {
              CollectionAssert.AreEqual(new[]
              {
                  "aimed_valid", "aimed_raw_m", "aimed_filtered_m", "aimed_source", "aimed_quality", "aimed_failure", "aimed_candidate",
                  "aimed_depthResidual_m", "aimed_depthInlier", "aimed_reason",
              }, CsvSchema.ReadingColumns("aimed", true));
              Assert.AreEqual(9, CsvSchema.ReadingColumns("nearest", false).Length);
          }

          [Test]
          public void AppendedColumns_SpecSection7ThenSidesThenWidth()
          {
              var cols = CsvSchema.AppendedColumns();
              Assert.AreEqual(13 + 10 + 10 + 1, cols.Length);
              CollectionAssert.AreEqual(new[]
              {
                  "infer_ms", "infer_hz", "align_s", "align_t", "align_residual", "floor_inliers",
                  "walls_in_map", "aimed_source_chain", "edge_snap_frac", "floor_h_m", "thermal_state", "detect_latency_ms",
                  "prep_ms",
              }, CsvSchema.LearnedColumns);
              Assert.AreEqual("left_valid", cols[13]);
              Assert.AreEqual("right_reason", cols[32]);
              Assert.AreEqual("corridor_width_m", cols[33]);
              Assert.AreEqual("floor-aligned-v1", CsvSchema.Revision);
          }
      }
  }
  ```

  `prep_ms` is appended at the end of the learned block, never inserted. The Python tools read columns by name, so appending is safe. The revision string stays `floor-aligned-v1`: no existing column changed meaning.

- [ ] **Step 2: Run the test to verify it fails**

  Run: `unity command run_tests --mode editor --filter WallDistance --filter_type assembly --timeout 240 --no-banner`

  Expected: FAIL in `AppendedColumns_SpecSection7ThenSidesThenWidth`: `Expected: 34  But was: 33`.

- [ ] **Step 3: Add the column, the timer and the row value**

  **3a.** In `CsvSchema.cs`, change the last line of `LearnedColumns` from:

  ```csharp
              "walls_in_map", "aimed_source_chain", "edge_snap_frac", "floor_h_m", "thermal_state", "detect_latency_ms",
  ```

  to:

  ```csharp
              "walls_in_map", "aimed_source_chain", "edge_snap_frac", "floor_h_m", "thermal_state", "detect_latency_ms",
              // Main-thread cost of the synchronous camera-image conversion (plan deviation D2).
              "prep_ms",
  ```

  **3b.** In `DepthInferenceScheduler.cs`, after `public double LastInferenceMs { get; private set; } = double.NaN;` add:

  ```csharp
          /// <summary>
          /// Main-thread milliseconds spent converting the CPU image and resampling it to 518²
          /// for the last prepared frame. Conversion is synchronous so the pose matches the image
          /// (deviation D2); this number is how the bench shows what that costs per frame.
          /// </summary>
          public double LastPrepareMs { get; private set; } = double.NaN;
  ```

  In `OnCameraFrame`, replace:

  ```csharp
                  if (!ConvertToRgb(image)) return;
                  var target = _images[_next];
                  Resample(image.width, image.height, geo, target.luma);
  ```

  with:

  ```csharp
                  // Stopwatch ticks, not Time: realtimeSinceStartup has too coarse a resolution on
                  // some Android builds for a few-millisecond interval.
                  long prepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                  if (!ConvertToRgb(image)) return;
                  var target = _images[_next];
                  Resample(image.width, image.height, geo, target.luma);
                  LastPrepareMs = (System.Diagnostics.Stopwatch.GetTimestamp() - prepStart) * 1000.0
                                  / System.Diagnostics.Stopwatch.Frequency;
  ```

  The type is written out in full because `using System.Diagnostics;` would make `Debug` ambiguous with `UnityEngine.Debug` in this file.

  **3c.** In `MeasurementCsvRecorder.cs`, after `Append(pipe.LastDetectLatencyMs);` add:

  ```csharp
              Append(sch != null ? sch.LastPrepareMs : double.NaN);
  ```

  (`sch` is the `service.scheduler` local that Task 20 declared a few lines above.)

- [ ] **Step 4: Run the tests to verify they pass**

  Run the test command. Expected: PASS (all EditMode tests; the AR assembly compiles).

- [ ] **Step 5: Write the failing summariser tests**

  `tools/bench/test_summarize_bench.py`:

  ```python
  """Tests for summarize_bench.py. Run: python -m unittest discover -s tools/bench -v"""
  import gzip
  import math
  import os
  import sys
  import tempfile
  import unittest

  sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
  import summarize_bench as sb  # noqa: E402

  COLUMNS = ["t", "fps", "updRate", "infer_ms", "infer_hz", "detect_latency_ms", "prep_ms", "thermal_state"]


  def healthy_rows(seconds=601.0, rate=30.0, slow_minute=None):
      """A synthetic log at `rate` rows/s. Inference values alternate 20/24 ms, changing every
      2 rows, the way the real CSV repeats the latest inference on every row until the next."""
      rows = []
      for i in range(int(seconds * rate)):
          t = i / rate
          hz = 4.0 if slow_minute is not None and slow_minute * 60 <= t < (slow_minute + 1) * 60 else 15.0
          rows.append({
              "t": repr(t), "fps": "30", "updRate": "30",
              "infer_ms": "20" if (i // 2) % 2 == 0 else "24",
              "infer_hz": repr(hz),
              "detect_latency_ms": "40" if (i // 2) % 2 == 0 else "45",
              "prep_ms": "3",
              "thermal_state": "1",
          })
      return rows


  def write_log(path, rows, gz=False):
      text = "# WallDistance benchmark log; measurement revision=floor-aligned-v1\n"
      text += "# inference=QNN HTP: test\n"
      text += ",".join(COLUMNS) + "\n"
      text += "".join(",".join(r.get(c, "") for c in COLUMNS) + "\n" for r in rows)
      opener = gzip.open if gz else open
      with opener(path, "wt", encoding="utf-8", newline="") as f:
          f.write(text)


  class HelperTests(unittest.TestCase):
      def test_num_reads_blank_as_nan(self):
          self.assertTrue(math.isnan(sb.num("")))
          self.assertTrue(math.isnan(sb.num(None)))
          self.assertEqual(sb.num("2.5"), 2.5)

      def test_percentile_is_nearest_rank_and_ignores_nan(self):
          self.assertEqual(sb.percentile([4, 1, 3, 2, math.nan], 50), 2)
          self.assertEqual(sb.percentile([1, 2, 3, 4], 95), 4)
          self.assertTrue(math.isnan(sb.percentile([math.nan], 50)))

      def test_distinct_runs_gives_one_sample_per_inference(self):
          self.assertEqual(sb.distinct_runs([22, 22, 22, 25, 25, math.nan, 25]), [22, 25, 25])

      def test_window_means_counts_a_minute_without_inference_as_zero(self):
          times = [float(t) for t in range(180)]
          values = [15.0 if t < 60 or t >= 120 else math.nan for t in range(180)]
          # 0..179 s holds two complete 60 s windows; the partial third is dropped.
          self.assertEqual(sb.window_means(times, values), [15.0, 0.0])

      def test_read_log_skips_comment_lines_and_reads_gzip(self):
          with tempfile.TemporaryDirectory() as d:
              for gz in (False, True):
                  path = os.path.join(d, "log.csv" + (".gz" if gz else ""))
                  write_log(path, healthy_rows(seconds=1.0), gz=gz)
                  header, rows = sb.read_log(path)
                  self.assertEqual(len(header), 2)
                  self.assertEqual(len(rows), 30)
                  self.assertEqual(rows[0]["fps"], "30")


  class SummaryTests(unittest.TestCase):
      def test_healthy_run_passes_every_check(self):
          s = sb.summarize(healthy_rows())
          self.assertAlmostEqual(s["duration_s"], 601.0, delta=0.1)
          self.assertEqual(s["infer_ms_p50"], 20)
          self.assertEqual(s["infer_ms_p95"], 24)
          self.assertEqual(s["detect_latency_ms_p95"], 45)
          self.assertEqual(s["prep_ms_p50"], 3)
          self.assertEqual(s["thermal_max"], 1)
          self.assertEqual(s["infer_hz_window_min"], 15.0)
          self.assertTrue(all(ok for _, ok, _, _ in sb.checks(s)))

      def test_one_slow_minute_fails_the_sustained_rate_check(self):
          s = sb.summarize(healthy_rows(slow_minute=3))
          self.assertEqual(s["infer_hz_window_min"], 4.0)
          self.assertAlmostEqual(s["reduced_rate_frac"], 60 / 601, places=2)
          failed = [label for label, ok, _, _ in sb.checks(s) if not ok]
          self.assertEqual(len(failed), 1)
          self.assertIn("10 Hz", failed[0])

      def test_short_run_fails_the_length_check(self):
          failed = [label for label, ok, _, _ in sb.checks(sb.summarize(healthy_rows(seconds=120.0))) if not ok]
          self.assertTrue(any("600" in label for label in failed))

      def test_cli_writes_markdown_and_returns_zero_on_pass(self):
          with tempfile.TemporaryDirectory() as d:
              log, out = os.path.join(d, "bench.csv"), os.path.join(d, "bench.md")
              write_log(log, healthy_rows())
              self.assertEqual(sb.main([log, "--markdown", out]), 0)
              with open(out, encoding="utf-8") as f:
                  text = f.read()
              self.assertIn("PASS", text)
              self.assertNotIn("FAIL", text)
              self.assertIn("revision=floor-aligned-v1", text)


  if __name__ == "__main__":
      unittest.main()
  ```

- [ ] **Step 6: Run the tests to verify they fail**

  ```bash
  /c/msys64/ucrt64/bin/python -m unittest discover -s tools/bench -v
  ```

  Expected: `ModuleNotFoundError: No module named 'summarize_bench'`.

- [ ] **Step 7: Implement `summarize_bench.py`**

  `tools/bench/summarize_bench.py`:

  ```python
  """Summarise a 10-minute on-device bench log against the spec's rate targets.

  Spec §8 ("On-device bench scene") asks for infer_ms p50/p95, end-to-end detection latency,
  frame rate and thermal state over 10 minutes; §10 proposes the rate targets checked here.

  Usage:
      python tools/bench/summarize_bench.py <walldist_*.csv[.gz]> [--markdown out.md]
  Exit code 0 when every check passes, 1 otherwise (2 for a bad file).

  Standard library only, so it runs on any Python 3.9+ without installing anything.
  """
  import argparse
  import csv
  import gzip
  import math
  import os
  import sys


  def _open_text(path):
      if path.endswith(".gz"):
          return gzip.open(path, "rt", encoding="utf-8", newline="")
      return open(path, encoding="utf-8", newline="")


  def read_log(path):
      """Return (comment header lines, data rows as dicts). Comment lines start with '#'
      (revision, inference status, device, condition, config) and precede the column row."""
      header, body = [], []
      with _open_text(path) as f:
          for line in f:
              if line.startswith("#"):
                  header.append(line.rstrip("\r\n"))
              else:
                  body.append(line)
      return header, list(csv.DictReader(body))


  def num(value):
      """CSV cell -> float. The recorder writes NaN as an empty cell, so blank means 'no value'."""
      try:
          return float(value)
      except (TypeError, ValueError):
          return math.nan


  def _finite(values):
      return [v for v in values if not math.isnan(v)]


  def percentile(values, q):
      """Nearest-rank percentile of the finite values (NaN when there are none). Nearest rank
      always returns a value that was actually observed, which keeps small samples honest."""
      xs = sorted(_finite(values))
      if not xs:
          return math.nan
      k = max(1, math.ceil(q / 100.0 * len(xs)))
      return xs[k - 1]


  def distinct_runs(values):
      """Collapse runs of the same value into one sample. The CSV repeats the latest inference
      result on every ~30 Hz row until the next one arrives, so raw rows would weight each
      inference by how long it stayed latest; slow inferences would count more than fast ones."""
      out, prev = [], None
      for v in values:
          if math.isnan(v):
              prev = None
              continue
          if v != prev:
              out.append(v)
          prev = v
      return out


  def window_means(times, values, seconds=60.0):
      """Mean of `values` in each complete `seconds` window from the first row. A window with no
      finite value scores 0: a minute without any inference is a minute without detection."""
      if not times:
          return []
      t0 = times[0]
      n = int((times[-1] - t0) // seconds)
      sums, counts = [0.0] * n, [0] * n
      for t, v in zip(times, values):
          k = int((t - t0) // seconds)
          if k < n and not math.isnan(v):
              sums[k] += v
              counts[k] += 1
      return [s / c if c else 0.0 for s, c in zip(sums, counts)]


  def _col(rows, name):
      return [num(r.get(name)) for r in rows]


  def summarize(rows):
      t = _col(rows, "t")
      s = {
          "rows": len(rows),
          "duration_s": (t[-1] - t[0]) if len(t) > 1 else 0.0,
          "fps_p50": percentile(_col(rows, "fps"), 50),
          "upd_p50": percentile(_col(rows, "updRate"), 50),
      }
      for name in ("infer_ms", "detect_latency_ms", "prep_ms"):
          samples = distinct_runs(_col(rows, name))
          s[name + "_n"] = len(samples)
          s[name + "_p50"] = percentile(samples, 50)
          s[name + "_p95"] = percentile(samples, 95)
      hz = _col(rows, "infer_hz")
      windows = window_means(t, hz)
      s["infer_hz_p50"] = percentile(hz, 50)
      s["infer_hz_window_min"] = min(windows) if windows else math.nan
      # Inferred, not logged: the governor's 5 Hz step-down shows up as infer_hz near 5, so
      # anything under 7.5 Hz (halfway to the 10 Hz target) is counted as reduced-rate time.
      finite_hz = _finite(hz)
      s["reduced_rate_frac"] = sum(1 for v in finite_hz if v < 7.5) / len(finite_hz) if finite_hz else math.nan
      thermal = [v for v in _col(rows, "thermal_state") if not math.isnan(v) and v >= 0]
      s["thermal_max"] = max(thermal) if thermal else math.nan
      s["thermal_last"] = thermal[-1] if thermal else math.nan
      return s


  # (summary key, label, comparison, threshold, where the threshold comes from)
  TARGETS = [
      ("duration_s", "run length >= 600 s", ">=", 600.0, "spec §8 (10-minute run)"),
      ("upd_p50", "readings at ~30 Hz (median updRate >= 28)", ">=", 28.0,
       "spec §10; the 28 Hz tolerance is this plan's proposal"),
      ("infer_hz_window_min", "detection >= 10 Hz in every minute", ">=", 10.0, "spec §10 (sustained for 10 min)"),
      ("infer_ms_p50", "NPU inference p50 <= 35 ms", "<=", 35.0, "spec §9 Phase 1 gate, re-measured in-app (D7)"),
  ]


  def checks(summary):
      """[(label, passed, value, source)]. A NaN value fails: no data is not a pass."""
      out = []
      for key, label, op, threshold, source in TARGETS:
          v = summary[key]
          ok = not math.isnan(v) and (v >= threshold if op == ">=" else v <= threshold)
          out.append((label, ok, v, source))
      return out


  def _fmt(v):
      return "—" if isinstance(v, float) and math.isnan(v) else (f"{v:.1f}" if isinstance(v, float) else str(v))


  def render(path, header, summary, results):
      lines = [f"# Bench summary: `{os.path.basename(path)}`", "", "## Log header", ""]
      # The config line is one long JSON object; it stays in the CSV, not in the summary.
      lines += [f"    {h}" for h in header if not h.startswith("# config")]
      lines += ["", "## Measurements", "", "| metric | value |", "|---|---|"]
      for key in ("rows", "duration_s", "fps_p50", "upd_p50",
                  "infer_ms_n", "infer_ms_p50", "infer_ms_p95",
                  "detect_latency_ms_p50", "detect_latency_ms_p95",
                  "prep_ms_p50", "prep_ms_p95",
                  "infer_hz_p50", "infer_hz_window_min", "reduced_rate_frac",
                  "thermal_max", "thermal_last"):
          lines.append(f"| {key} | {_fmt(summary[key])} |")
      lines += ["", "`reduced_rate_frac` is inferred from `infer_hz < 7.5`, not logged directly.",
                "`thermal_*` is Android's PowerManager status, 0 (none) to 6 (shutdown); blank before Android 10.",
                "", "## Checks", "", "| check | result | value | threshold source |", "|---|---|---|---|"]
      for label, ok, v, source in results:
          lines.append(f"| {label} | {'PASS' if ok else 'FAIL'} | {_fmt(v)} | {source} |")
      return "\n".join(lines) + "\n"


  def main(argv=None):
      ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
      ap.add_argument("log")
      ap.add_argument("--markdown", help="also write the summary to this file")
      args = ap.parse_args(argv)
      # The report uses ≤, § and —; a Windows console (cp1252) cannot print them and would raise.
      # Replace them on the console only; the --markdown file is always written as UTF-8.
      if hasattr(sys.stdout, "reconfigure"):
          sys.stdout.reconfigure(errors="replace")
      try:
          header, rows = read_log(args.log)
      except OSError as e:
          print(f"cannot read {args.log}: {e}", file=sys.stderr)
          return 2
      if not rows or "infer_ms" not in rows[0]:
          print("no rows, or not a floor-aligned-v1 log (no infer_ms column)", file=sys.stderr)
          return 2
      summary = summarize(rows)
      results = checks(summary)
      text = render(args.log, header, summary, results)
      print(text)
      if args.markdown:
          with open(args.markdown, "w", encoding="utf-8") as f:
              f.write(text)
      return 0 if all(ok for _, ok, _, _ in results) else 1


  if __name__ == "__main__":
      sys.exit(main())
  ```

- [ ] **Step 8: Run the tests to verify they pass**

  ```bash
  /c/msys64/ucrt64/bin/python -m unittest discover -s tools/bench -v
  ```

  Expected: 9 tests, `OK`.

- [ ] **Step 9: Commit**

  ```bash
  git add Assets/WallDistance tools/bench
  git commit -m "Log frame-prepare time and add the 10-minute bench summariser"
  ```

- [ ] **Step 10: Run the 10-minute bench on the phone**

  Check the precondition at the top of this task first. Then:

  - Build and install the Development APK as in Task 19, Step 5.
  - Conditions: battery ≥ 50 %, **not charging** (charging adds heat), phone at room temperature (not just out of a pocket or a sunny window). Note the ambient temperature if a thermometer is handy.
  - In a corridor: track the floor (HUD prompt gone), press **● Record CSV**, then walk the corridor back and forth at normal pace with the phone upright, for **at least 10 minutes 30 seconds**. Stop recording.
  - Pull the log:

  ```bash
  ADB="/c/Program Files/Unity/Hub/Editor/6000.3.5f1/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb.exe"
  "$ADB" pull /sdcard/Android/data/com.arnav.walldistance/files/WallDistanceLogs Builds/logs/
  F=$(ls -t Builds/logs/WallDistanceLogs/*.csv | head -1); echo "$F"
  ```

- [ ] **Step 11: Summarise, record and commit**

  ```bash
  D=$(date +%Y-%m-%d)
  mkdir -p docs/benchmarks
  /c/msys64/ucrt64/bin/python tools/bench/summarize_bench.py "$F" --markdown "docs/benchmarks/$D-bench.md"; echo "exit $?"
  ```

  Under the generated tables, add a short "Conditions" section by hand: ambient temperature, battery start/end %, the corridor, and anything unusual (calls, notifications, the app backgrounded).

  Then answer D2's question in one line, from `prep_ms_p95`:
  - If `prep_ms_p95` ≤ 8 ms (a quarter of a 33 ms frame), write "D2 holds: synchronous conversion is affordable".
  - Otherwise, record the number and flag D2 for Rachit. Do not switch to async conversion in this task: async would break the pose/image pairing D2 exists for.

  ```bash
  git add "docs/benchmarks/$D-bench.md" "$F"
  git commit -m "Record 10-minute on-device bench against the Phase 3 rate targets"
  ```

  **Gate (spec §9, Phase 3):** continue to Task 22 when every check is PASS.
  - **When a check fails:** do not tune anything in this task. The §10 targets are proposals, so report the failing line and its value to Rachit and wait.
  - **Thermal:** if `thermal_max` ≥ 3 (severe), say so too, even if the rate checks pass.

---

### Task 22: Phase 4 — field protocol and error analysis

Spec §8's field protocol and §10's error targets, made repeatable. Each trial is one CSV recording. A hand-filled references file maps each recording to its tape-measured distances. A tested script joins the two and produces the report §8 asks for:
- time to first valid reading;
- median and P95 absolute error by source and distance;
- valid fraction;
- update rate.

**How references are attached (deviation D13, flagged for Rachit).** The README notes that the HUD has no text field for `reference_m` / `conditionTag`. Typing three numbers into a phone between trials, while holding a tape, is also where entry errors come from. So this plan keeps the references in a sidecar CSV, written on a paper sheet during the session and typed up afterwards. Each row names the recording file; the recorder's `walldist_<yyyyMMdd_HHmmss>.csv` names sort in recording order, which matches the sheet's order. The HUD stays unchanged.

**Precondition:** Task 21's gate passed. Steps 1–5 (code) can be done any time.

**Files:**
- Create: `tools/field/analyse_field.py`
- Create: `tools/field/test_analyse_field.py`
- Create: `tools/field/references_template.csv`
- Create: `docs/benchmarks/field-protocol.md`
- Create (Step 7): `docs/benchmarks/field/<date>/references.csv`, `docs/benchmarks/field/<date>/report.md`; raw logs in `Builds/logs/WallDistanceLogs/`

**Interfaces:**
- Consumes:
  - `read_log`, `num`, `percentile` from `tools/bench/summarize_bench.py` (Task 21);
  - CSV columns `t`, `updRate`, `cam_x`, `cam_z`, `cam_qx`…`cam_qw`, `<r>_valid`, `<r>_raw_m`, `<r>_filtered_m`, `<r>_source` for r in aimed/left/right, and `corridor_width_m`.
- Produces:
  - `analyse_field.py`:
    - `band(meters) -> str`
    - `forward_y(row) -> float`
    - `load_references(path) -> list[dict]`
    - `analyse_file(rows, ref) -> dict`
    - `aggregate(results) -> dict`
    - `render(aggregate) -> str`
    - `main(argv) -> int`
  - The references file format:

    ```
    file,site,condition,point,trial,motion,ref_aimed_m,ref_left_m,ref_right_m,ref_width_m,notes
    ```

    `motion` is `standing` or `walking`. A blank reference means "not measured".

- [ ] **Step 1: Write the failing tests**

  `tools/field/test_analyse_field.py`:

  ```python
  """Tests for analyse_field.py. Run: python -m unittest discover -s tools/field -v"""
  import math
  import os
  import sys
  import tempfile
  import unittest

  sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
  import analyse_field as af  # noqa: E402

  # Camera quaternions (x, y, z, w). Unity: +x rotation tilts the forward axis (+z) downwards.
  LEVEL = ("0", "0", "0", "1")
  DOWN_60 = (repr(math.sin(math.radians(30))), "0", "0", repr(math.cos(math.radians(30))))
  READINGS = ("aimed", "left", "right")


  def row(t, q=LEVEL, z=0.0, width="", **readings):
      """readings: name=(valid, raw, filtered, source); missing readings are invalid."""
      r = {"t": repr(t), "updRate": "30", "cam_x": "0", "cam_y": "1.4", "cam_z": repr(z),
           "cam_qx": q[0], "cam_qy": q[1], "cam_qz": q[2], "cam_qw": q[3], "corridor_width_m": width}
      for name in READINGS:
          valid, raw, filt, src = readings.get(name, (0, "", "", "None"))
          r.update({f"{name}_valid": str(valid), f"{name}_raw_m": raw, f"{name}_filtered_m": filt, f"{name}_source": src})
      return r


  def ref(**kw):
      base = {"file": "a.csv", "site": "S", "condition": "plain", "point": "P1", "trial": "1", "motion": "standing",
              "ref_aimed_m": "", "ref_left_m": "", "ref_right_m": "", "ref_width_m": "", "notes": ""}
      base.update({k: str(v) for k, v in kw.items()})
      return base


  class HelperTests(unittest.TestCase):
      def test_band_edges(self):
          self.assertEqual(af.band(0.3), "<0.5")
          self.assertEqual(af.band(0.5), "0.5-1")
          self.assertEqual(af.band(2.99), "2-3")
          self.assertEqual(af.band(3.0), ">=3")

      def test_forward_y_level_and_pitched_down(self):
          self.assertAlmostEqual(af.forward_y(row(0)), 0.0)
          self.assertAlmostEqual(af.forward_y(row(0, q=DOWN_60)), -math.sin(math.radians(60)), places=6)


  class StandingTrialTests(unittest.TestCase):
      def test_acquisition_runs_from_raise_to_first_valid_and_errors_use_reference(self):
          rows = [row(i / 30, q=DOWN_60) for i in range(30)]                       # 0-1 s: at the floor
          rows += [row(1 + i / 30) for i in range(9)]                               # raised, not yet valid
          rows += [row(1.3 + i / 30, aimed=(1, "2.03", "2.01", "FloorEdge")) for i in range(60)]
          res = af.analyse_file(rows, ref(ref_aimed_m=2.0))
          self.assertAlmostEqual(res["acquisition_s"]["aimed"], 0.3, places=6)
          errs = [s for s in res["samples"] if s["reading"] == "aimed"]
          self.assertEqual(len(errs), 60)
          self.assertEqual({(s["source"], s["band"]) for s in errs}, {("FloorEdge", "2-3")})
          self.assertAlmostEqual(errs[0]["err_raw"], 0.03, places=6)
          self.assertAlmostEqual(errs[0]["err_filtered"], 0.01, places=6)

      def test_valid_before_the_raise_counts_as_zero_acquisition(self):
          rows = [row(i / 30, q=DOWN_60, aimed=(1, "1.0", "1.0", "LearnedDepth")) for i in range(10)]
          rows += [row(1 + i / 30, aimed=(1, "1.0", "1.0", "LearnedDepth")) for i in range(10)]
          self.assertEqual(af.analyse_file(rows, ref(ref_aimed_m=1.0))["acquisition_s"]["aimed"], 0.0)

      def test_invalid_rows_lower_valid_fraction_and_never_become_errors(self):
          rows = []
          for i in range(40):
              ok = i % 2 == 0
              rows.append(row(i / 30, aimed=(1, "1.5", "1.5", "FloorEdge") if ok else (0, "", "", "None")))
          res = af.analyse_file(rows, ref(ref_aimed_m=1.5))
          self.assertAlmostEqual(res["valid_frac"]["aimed"], 0.5)
          self.assertEqual(len([s for s in res["samples"] if s["reading"] == "aimed"]), 20)

      def test_unmeasured_reference_produces_no_error_samples(self):
          rows = [row(i / 30, left=(1, "0.9", "0.9", "FloorEdge")) for i in range(10)]
          res = af.analyse_file(rows, ref(ref_aimed_m=2.0))         # ref_left_m blank
          self.assertEqual([s for s in res["samples"] if s["reading"] == "left"], [])

      def test_width_reference_defaults_to_left_plus_right(self):
          rows = [row(i / 30, width="2.45") for i in range(10)]
          res = af.analyse_file(rows, ref(ref_left_m=1.2, ref_right_m=1.3))
          w = [s for s in res["samples"] if s["reading"] == "width"]
          self.assertEqual(len(w), 10)
          self.assertAlmostEqual(w[0]["err_raw"], 0.05, places=6)


  class WalkingTrialTests(unittest.TestCase):
      def test_both_sides_fraction_ignores_the_first_two_metres(self):
          rows = []
          for i in range(300):                                          # 10 s at 1 m/s along +z
              t = i / 30
              ok = t >= 1.5 and not (5.0 <= t < 5.5)
              sides = {"left": (1, "1.1", "1.1", "FloorEdge"), "right": (1, "1.2", "1.2", "FloorEdge")} if ok else {}
              rows.append(row(t, z=t, **sides))
          res = af.analyse_file(rows, ref(motion="walking"))
          # After 2 m (t >= 2 s) there are 240 rows, of which 15 (5.0-5.5 s) lack both sides.
          self.assertAlmostEqual(res["both_sides_frac"], 225 / 240, places=6)
          self.assertIsNone(res["acquisition_s"])


  class EndToEndTests(unittest.TestCase):
      def _write(self, d, name, rows):
          cols = list(rows[0].keys())
          with open(os.path.join(d, name), "w", encoding="utf-8", newline="") as f:
              f.write("# WallDistance benchmark log; measurement revision=floor-aligned-v1\n")
              f.write(",".join(cols) + "\n")
              for r in rows:
                  f.write(",".join(r[c] for c in cols) + "\n")

      def _refs(self, d, refs):
          path = os.path.join(d, "references.csv")
          cols = list(refs[0].keys())
          with open(path, "w", encoding="utf-8", newline="") as f:
              f.write(",".join(cols) + "\n")
              for r in refs:
                  f.write(",".join(r[c] for c in cols) + "\n")
          return path

      def test_main_writes_report(self):
          with tempfile.TemporaryDirectory() as d:
              rows = [row(i / 30, aimed=(1, "2.04", "2.02", "FloorEdge")) for i in range(30)]
              self._write(d, "a.csv", rows)
              out = os.path.join(d, "report.md")
              self.assertEqual(af.main([d, self._refs(d, [ref(ref_aimed_m=2.0)]), "--markdown", out]), 0)
              with open(out, encoding="utf-8") as f:
                  text = f.read()
              self.assertIn("| aimed | FloorEdge | 2-3 | 30 |", text)

      def test_main_refuses_missing_log_files(self):
          with tempfile.TemporaryDirectory() as d:
              self.assertEqual(af.main([d, self._refs(d, [ref(file="missing.csv")])]), 2)


  if __name__ == "__main__":
      unittest.main()
  ```

- [ ] **Step 2: Run the tests to verify they fail**

  ```bash
  /c/msys64/ucrt64/bin/python -m unittest discover -s tools/field -v
  ```

  Expected: `ModuleNotFoundError: No module named 'analyse_field'`.

- [ ] **Step 3: Implement `analyse_field.py`**

  `tools/field/analyse_field.py`:

  ```python
  """Field benchmark analysis (spec §8 field protocol, §10 proposed targets).

  Usage:
      python tools/field/analyse_field.py <logs_dir> <references.csv> [--markdown report.md]

  Each row of references.csv names one recording in logs_dir and its tape-measured distances,
  taken from the camera position (protocol: docs/benchmarks/field-protocol.md).
  Exit code 0 on success, 2 when inputs are missing or unreadable. Targets are reported,
  not enforced: they are proposals for Rachit to confirm (spec §10).
  """
  import argparse
  import csv
  import math
  import os
  import sys

  sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "bench"))
  from summarize_bench import num, percentile, read_log  # noqa: E402

  READINGS = ("aimed", "left", "right")
  BANDS = [(0.0, 0.5, "<0.5"), (0.5, 1.0, "0.5-1"), (1.0, 1.5, "1-1.5"), (1.5, 2.0, "1.5-2"),
           (2.0, 3.0, "2-3"), (3.0, math.inf, ">=3")]
  # A camera within 20° of level counts as "raised to the wall". Same angle as the side-wall
  # no-heading rule, used here for the opposite purpose: knowing when the tester has aimed.
  LEVEL_SIN = math.sin(math.radians(20))
  # Spec §10: left/right valid fraction is judged after the first 2 m of a walk.
  WALK_WARMUP_M = 2.0


  def band(meters):
      for lo, hi, name in BANDS:
          if lo <= meters < hi:
              return name
      return "<0.5"


  def forward_y(row):
      """Vertical component of the camera's forward axis from the logged quaternion.
      Rotating (0,0,1) by q=(x,y,z,w) gives forward.y = 2(yz - wx)."""
      x, y, z, w = (num(row.get(k)) for k in ("cam_qx", "cam_qy", "cam_qz", "cam_qw"))
      return 2.0 * (y * z - w * x)


  def _valid(row, name):
      return row.get(f"{name}_valid") in ("1", "True", "true")


  def _ref(ref, key):
      return num(ref.get(key))


  def load_references(path):
      with open(path, encoding="utf-8", newline="") as f:
          return [r for r in csv.DictReader(f) if (r.get("file") or "").strip()]


  def analyse_file(rows, ref):
      """One recording. Invalid readings lower the valid fraction; they never become an error
      value (a missing reading is not a zero-distance reading)."""
      t = [num(r.get("t")) for r in rows]
      refs = {"aimed": _ref(ref, "ref_aimed_m"), "left": _ref(ref, "ref_left_m"), "right": _ref(ref, "ref_right_m")}
      ref_width = _ref(ref, "ref_width_m")
      if math.isnan(ref_width) and not math.isnan(refs["left"]) and not math.isnan(refs["right"]):
          # Both lateral distances are taped from the same camera position, so they sum to the width there.
          ref_width = refs["left"] + refs["right"]
      walking = (ref.get("motion") or "").strip() == "walking"

      samples = []
      for r in rows:
          for name in READINGS:
              if math.isnan(refs[name]) or not _valid(r, name):
                  continue
              raw, filt = num(r.get(f"{name}_raw_m")), num(r.get(f"{name}_filtered_m"))
              if math.isnan(raw):
                  continue
              samples.append({"reading": name, "source": r.get(f"{name}_source") or "None", "band": band(refs[name]),
                              "err_raw": abs(raw - refs[name]),
                              "err_filtered": abs(filt - refs[name]) if not math.isnan(filt) else math.nan})
          width = num(r.get("corridor_width_m"))
          if not math.isnan(ref_width) and not math.isnan(width):
              samples.append({"reading": "width", "source": "width", "band": band(ref_width),
                              "err_raw": abs(width - ref_width), "err_filtered": abs(width - ref_width)})

      result = {"file": ref.get("file"), "motion": "walking" if walking else "standing", "samples": samples,
                "upd_p50": percentile([num(r.get("updRate")) for r in rows], 50),
                "valid_frac": {}, "acquisition_s": None, "both_sides_frac": math.nan}
      for name in READINGS:
          valid = sum(1 for r in rows if _valid(r, name))
          result["valid_frac"][name] = valid / len(rows) if rows else math.nan

      if walking:
          # Path length on the floor plane, so turning on the spot does not count as distance.
          dist, prev, after = 0.0, None, []
          for r in rows:
              p = (num(r.get("cam_x")), num(r.get("cam_z")))
              if prev is not None and not any(math.isnan(v) for v in p + prev):
                  dist += math.hypot(p[0] - prev[0], p[1] - prev[1])
              prev = p
              if dist >= WALK_WARMUP_M:
                  after.append(_valid(r, "left") and _valid(r, "right"))
          result["both_sides_frac"] = sum(after) / len(after) if after else math.nan
      else:
          # Acquisition proxy (inferred, see the protocol): the clock starts at the first level
          # row, i.e. when the tester has raised the phone from the floor to the wall.
          start = next((t[i] for i, r in enumerate(rows) if abs(forward_y(r)) < LEVEL_SIN), math.nan)
          result["acquisition_s"] = {}
          for name in READINGS:
              first = next((t[i] for i, r in enumerate(rows) if _valid(r, name)), math.nan)
              result["acquisition_s"][name] = math.nan if math.isnan(start) or math.isnan(first) else max(0.0, first - start)
      return result


  def aggregate(results):
      errors = {}
      for res in results:
          for s in res["samples"]:
              errors.setdefault((s["reading"], s["source"], s["band"]), []).append(s)
      acq = {name: [res["acquisition_s"][name] for res in results
                    if res["acquisition_s"] is not None and not math.isnan(res["acquisition_s"][name])]
             for name in READINGS}
      standing = [r for r in results if r["motion"] == "standing"]
      return {
          "errors": {k: {"n": len(v),
                         "raw_p50": percentile([s["err_raw"] for s in v], 50),
                         "raw_p95": percentile([s["err_raw"] for s in v], 95),
                         "filt_p50": percentile([s["err_filtered"] for s in v], 50),
                         "filt_p95": percentile([s["err_filtered"] for s in v], 95)}
                     for k, v in sorted(errors.items())},
          "acquisition": {name: {"n": len(v), "p50": percentile(v, 50), "p95": percentile(v, 95)} for name, v in acq.items()},
          "valid_frac": {name: percentile([r["valid_frac"][name] for r in standing], 50) for name in READINGS},
          "both_sides_frac": [r["both_sides_frac"] for r in results if r["motion"] == "walking"],
          "upd_p50": percentile([r["upd_p50"] for r in results], 50),
          "files": len(results),
      }


  def _cm(v):
      return "—" if math.isnan(v) else f"{v * 100:.1f}"


  def _s(v):
      return "—" if math.isnan(v) else f"{v:.2f}"


  def render(agg):
      lines = [f"# Field benchmark report ({agg['files']} recordings)", "",
               "## Absolute error (cm) by reading, source and reference distance", "",
               "| reading | source | band (m) | n | raw p50 | raw p95 | filtered p50 | filtered p95 |",
               "|---|---|---|---|---|---|---|---|"]
      for (reading, source, b), e in agg["errors"].items():
          lines.append(f"| {reading} | {source} | {b} | {e['n']} | {_cm(e['raw_p50'])} | {_cm(e['raw_p95'])} | "
                       f"{_cm(e['filt_p50'])} | {_cm(e['filt_p95'])} |")
      lines += ["", "n counts rows (~30 per second), not trials; rows within one trial are correlated.", "",
                "## Acquisition (s, standing trials)", "",
                "Inferred proxy: from the first row with the camera within 20° of level (phone raised) "
                "to the first valid reading. Spec §10 proposal: median ≤ 0.5 s, P95 ≤ 1.5 s.", "",
                "| reading | trials | p50 | p95 |", "|---|---|---|---|"]
      for name, a in agg["acquisition"].items():
          lines.append(f"| {name} | {a['n']} | {_s(a['p50'])} | {_s(a['p95'])} |")
      lines += ["", "## Valid fraction", "",
                "| measure | value |", "|---|---|"]
      for name, v in agg["valid_frac"].items():
          lines.append(f"| {name}, standing (median over trials) | {_s(v)} |")
      for i, v in enumerate(agg["both_sides_frac"], 1):
          lines.append(f"| both sides after first 2 m, walk {i} (spec §10 proposal ≥ 0.90) | {_s(v)} |")
      lines += [f"| update rate, median of trial medians (Hz) | {_s(agg['upd_p50'])} |", ""]
      return "\n".join(lines)


  def main(argv=None):
      ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
      ap.add_argument("logs_dir")
      ap.add_argument("references")
      ap.add_argument("--markdown", help="also write the report to this file")
      args = ap.parse_args(argv)
      # The report uses ≤, § and —; a Windows console (cp1252) cannot print them and would raise.
      # Replace them on the console only; the --markdown file is always written as UTF-8.
      if hasattr(sys.stdout, "reconfigure"):
          sys.stdout.reconfigure(errors="replace")
      try:
          refs = load_references(args.references)
      except OSError as e:
          print(f"cannot read {args.references}: {e}", file=sys.stderr)
          return 2
      missing = [r["file"] for r in refs if not os.path.isfile(os.path.join(args.logs_dir, r["file"]))]
      if missing or not refs:
          print("missing log files: " + (", ".join(missing) if missing else "references.csv has no rows"), file=sys.stderr)
          return 2
      results = []
      for r in refs:
          _, rows = read_log(os.path.join(args.logs_dir, r["file"]))
          results.append(analyse_file(rows, r))
      text = render(aggregate(results))
      print(text)
      if args.markdown:
          with open(args.markdown, "w", encoding="utf-8") as f:
              f.write(text)
      return 0


  if __name__ == "__main__":
      sys.exit(main())
  ```

- [ ] **Step 4: Run the tests to verify they pass**

  ```bash
  /c/msys64/ucrt64/bin/python -m unittest discover -s tools/field -v
  /c/msys64/ucrt64/bin/python -m unittest discover -s tools/bench -v
  ```

  Expected: field 10 tests `OK`, and bench still 9 tests `OK`.

- [ ] **Step 5: Write the protocol and the references template, then commit**

  `tools/field/references_template.csv`:

  ```csv
  file,site,condition,point,trial,motion,ref_aimed_m,ref_left_m,ref_right_m,ref_width_m,notes
  walldist_20261010_101500.csv,library-corridor-1,plain-paint,P1,1,standing,2.00,1.20,1.35,,example row: delete
  walldist_20261010_103000.csv,library-corridor-1,plain-paint,walk-centre,1,walking,,,,2.55,example row: delete
  ```

  `docs/benchmarks/field-protocol.md`:

  ````markdown
  # Field benchmark protocol (spec §8)

  One recording per trial. References go on a paper sheet during the session and into
  `docs/benchmarks/field/<date>/references.csv` afterwards (format:
  `tools/field/references_template.csv`). The recorder names files
  `walldist_<yyyyMMdd_HHmmss>.csv`, so files sorted by name are in sheet order.

  ## Equipment
  - OnePlus 13R with the Development APK; battery ≥ 50 %, not charging.
  - Tape measure and a carpenter's square; masking tape to mark points on the floor.
  - The paper sheet: one line per trial with point, trial, motion, the three tape readings and notes.

  ## Every trial
  1. Force-stop the app and relaunch it, so each trial starts with an empty wall map
     ("independently initialised", spec §8). Wait until the floor prompt disappears.
  2. Stand at the marked point. Hold the phone at chest height pointing at the **floor**
     (about 60° down). Press **● Record CSV**.
  3. Raise the phone to level, aimed at the wall under test. The analysis starts the
     acquisition clock at this moment (first row within 20° of level).
  4. Hold still for 10 s, then press **■ Stop**.
  5. Tape from the camera position, perpendicular to each wall: the aimed wall, the left wall
     and the right wall. Write the three numbers on the sheet; leave a blank where a wall is
     absent (junction, doorway gap).

  ## Points (corridors first)
  - Side walls at 0.5 / 1 / 1.5 / 2 / 3 m: stand facing along the corridor, aim at the end
    wall or down the corridor; the side readings are the ones under test.
  - End wall at 1, 2, 3, 4, 5 m: aim at it.
  - Lateral positions: centre line, and 0.5 m off-centre towards each wall.
  - Include one junction and one doorway gap longer than 1 m (expected: that side `NoWallOnSide`).

  ## Walking trials
  - Tape the corridor width once at a few places; enter the median as `ref_width_m`.
  - Relaunch, track the floor, press Record, walk the straight corridor at normal pace on the
    centre line, phone upright, for at least 15 m. Stop. `motion = walking`.
  - Repeat 0.5 m off-centre.

  ## Conditions
  Plain painted walls; glossy tiled floors; skirting boards; doors; dim lighting; people
  walking past. Write the condition in the `condition` column with short fixed words
  (`plain-paint`, `glossy-floor`, `skirting`, `doors`, `dim`, `people`).

  ## Trials
  Three per point and condition.

  ## Analysis
  ```bash
  /c/msys64/ucrt64/bin/python tools/field/analyse_field.py Builds/logs/WallDistanceLogs \
      docs/benchmarks/field/<date>/references.csv --markdown docs/benchmarks/field/<date>/report.md
  ```
  ````

  ```bash
  git add tools/field docs/benchmarks/field-protocol.md
  git commit -m "Add field benchmark protocol and error analysis with references sidecar"
  ```

- [ ] **Step 6: Run the field session on the phone**

  Follow `docs/benchmarks/field-protocol.md`. Then pull the logs and type up the sheet:

  ```bash
  "$ADB" pull /sdcard/Android/data/com.arnav.walldistance/files/WallDistanceLogs Builds/logs/
  D=<session date, yyyy-mm-dd>
  mkdir -p docs/benchmarks/field/$D
  cp tools/field/references_template.csv docs/benchmarks/field/$D/references.csv
  ```

  Replace the two example rows with one row per trial from the sheet. Match recordings to sheet lines by sorting the file names, then check that the count matches the number of sheet lines before typing references in.

- [ ] **Step 7: Analyse, record and commit**

  ```bash
  /c/msys64/ucrt64/bin/python tools/field/analyse_field.py Builds/logs/WallDistanceLogs \
      docs/benchmarks/field/$D/references.csv --markdown docs/benchmarks/field/$D/report.md; echo "exit $?"
  ```

  Add a "Conditions and observations" section to `report.md` by hand: lighting, floor finish, anything that went wrong in a trial (and that trial's `file`).

  Commit the references, the report and **only the logs named in references.csv**:

  ```bash
  git add docs/benchmarks/field/$D
  tail -n +2 docs/benchmarks/field/$D/references.csv | cut -d, -f1 | sed 's#^#Builds/logs/WallDistanceLogs/#' | xargs git add
  git commit -m "Record field benchmark $D: errors by source and distance, acquisition, side validity"
  ```

  **Gate (spec §9, Phase 4):** "§10 targets reviewed with Rachit". Send Rachit the report's path and stop. Tuning thresholds against these numbers is a new piece of work for Rachit to scope; it is not part of this plan.

---

### Task 23: LiteRT fallback trigger (decision gate, no code)

Spec §4: "if QNN packaging blocks progress for more than 3 working days in Phase 1, switch to LiteRT with its Qualcomm AI Engine Direct accelerator … the C# interface (§5.3) does not change."

**Evidence so far** (from `docs/phase1-runtime-spike.md`, as committed):
- The QAIRT SDK is not installed, and `QNN_SDK_ROOT` is unset.
- An anonymous request to the official SDK download endpoint returned HTTP 403.
- The licence clause for redistributing `libQnn*` is TBD.
- The QNN C++ in `walldepth.cpp` has therefore not been compiled against the pinned headers.

So this gate may already be live. The plan does not decide it: the 3-day clock and whether the blocker counts are Rachit's call.

**Files:**
- Modify: `docs/phase1-runtime-spike.md` (a "Fallback decision" section)

- [ ] **Step 1: Establish the facts and write them down**

  Add this section to `docs/phase1-runtime-spike.md`, filling in each line from evidence:

  ```markdown
  ## Fallback decision (spec §4)

  - First day QNN packaging blocked progress: <date, and what blocked it, e.g. "SDK download HTTP 403">
  - Working days blocked so far: <n>
  - Blockers still open: <SDK access / licence clause / header compile / DSP skeleton load / other>
  - Unblocking actions taken: <e.g. Qualcomm ID sign-in requested, licence read>
  - Decision (Rachit): <stay on QNN | switch to LiteRT> — date
  ```

  Fill in only what can be established. Where a line cannot be, write `TBD —` followed by the specific question. For example: "TBD — has Rachit been able to sign in to Qualcomm Software Center with a Qualcomm ID?"

- [ ] **Step 2: Ask Rachit for the decision**

  Send the section's path and its filled-in lines to Rachit, with this question: "QNN has been blocked for <n> working days by <blocker>; the spec's threshold is 3. Stay on QNN or switch to LiteRT?" Then wait.

- [ ] **Step 3: Act on the answer**

  - **Stay on QNN:** record the decision line, commit, and continue with the blocked task.

    ```bash
    git add docs/phase1-runtime-spike.md
    git commit -m "Record Phase 1 fallback decision: stay on QNN"
    ```

  - **Switch to LiteRT:** record the decision line, commit (message `"Record Phase 1 fallback decision: switch to LiteRT"`), and **stop executing this plan.** The LiteRT backend needs its own short plan (`superpowers:writing-plans`). That plan must cover:
    - a new `IDepthInference` implementation;
    - LiteRT's Qualcomm accelerator packaging;
    - model conversion and pinning;
    - re-running Task 7's latency gate, the Task 17 self-test equivalent, then Tasks 21–22.

    `IDepthInference`, the scheduler and everything in Core stay unchanged; that is the point of §5.3's interface.
