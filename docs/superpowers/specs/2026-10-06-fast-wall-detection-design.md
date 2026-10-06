# Fast wall detection for campus corridors: design

- **Date:** 2026-10-06
- **Status:** Draft, awaiting review
- **Scope:** `Assets/WallDistance` (Core + AR assemblies) plus a new native Android plugin
- **Device of record:** OnePlus 13R (CPH2691, Snapdragon 8 Gen 3 / SM8650, no ToF sensor)

---

## 1. Understanding

**Stated by Rachit (2026-10-06):**

- Usage is mostly **campus corridors**.
- Goal: walls **detected quickly, with as little error as possible**. No fixed error limit.
- No limit on app size growth.
- ML runtime: LiteRT or Qualcomm QNN, **whichever is faster**.
- Work happens on `main` only.

**Assumptions (correct these if wrong):**

- The integration surface stays the same: `WallDistanceService.Latest` / `Updated` with
  *aimed* and *nearest observed* readings in the AR session frame.
- The phone is hand-held at roughly chest height while walking. Camera height is not fixed;
  it is measured from the floor plane every frame.
- Corridors are Manhattan-like: straight walls meeting at right angles, a flat floor, and
  wall bases visible along much of their length.
  Doors, notice boards, skirting boards and people are normal clutter.

## 2. Problem (evidence)

Source: the 34 field CSVs in `Builds/logs/WallDistanceLogs/` (2026-09-13/14).

| Observation | Value |
|---|---|
| ARCore planes, any orientation | 0 in 33/34 sessions, including a 7.7 m walk |
| Raw depth confidence, frame mean | ~1–5 of 255; gate is 128 |
| Smoothed depth vs truth | 7.98 m reported at a ~1 m wall; 36 m in the latest session |
| Valid aimed readings | 0 in the last 5 sessions |

Every current source depends on visible texture or sideways motion. Plain painted
corridor walls give neither.

- ARCore states that featureless surfaces such as white walls produce imprecise depth
  unless the device has a ToF sensor. The 13R has none.
- Plane detection needs feature-point clusters.
- Zero **floor** planes is *not* explained by plain walls. It may be an app or setup
  fault. Phase 0 settles this before any other work (§9).

## 3. Approach

**The floor supplies metric scale; a monocular depth network supplies wall shape; image
edges sharpen the wall base. Walls are detected a few times per second, anchored in the
world, and their distance is recomputed from the tracked camera pose every frame.**

Approaches considered:

| Approach | Plain walls | Latency | Error | Verdict |
|---|---|---|---|---|
| ARCore planes / raw depth (current) | Fails | Seconds, or never | Good when present | Kept as a cross-check source |
| Metric monocular depth alone | Works | ~22 ms | AbsRel ~6% (≈18 cm at 3 m) | Rejected: scale error too large |
| Separate wall/floor segmentation net + floor geometry | Works | 2 nets | ~1–4 cm (geometry) | Rejected for v1: second model; skirting boards confuse "wall" labels |
| **Relative depth net + floor-aligned scale + edge refinement (chosen)** | Works | ~22 ms + ~10 ms CPU | Geometry-limited when the base edge is found; scale-limited otherwise | **Chosen** |

Why the chosen approach should be accurate (calculated, not yet measured):

- **Base-edge path.** For a wall-base point at distance *d*, seen from camera height *h*,
  the error is `(h² + d²)/h` per radian of image angle. At inference resolution
  (~0.13°/px) and h = 1.4 m that is ≈1.9 cm/px at 3 m and ≈0.5 cm/px at 1 m.
  A 1 cm camera-height error adds `d/h` cm, so ≈2 cm at 3 m.
- **Scale path.** Every visible floor pixel has a known metric depth from the floor plane.
  A typical frame therefore has thousands of reference points for the scale fit, versus
  the handful of ARCore feature points other systems use.

## 4. Runtime decision

| Runtime (Snapdragon 8 Gen 3, Qualcomm AI Hub) | Precision | Latency |
|---|---|---|
| **QNN (DLC), HTP NPU** | **w8a16** | **22.3 ms** |
| QNN (DLC), HTP NPU | float | 30.7 ms |
| LiteRT (TFLite), NPU | float | 30.9 ms |

**Decision:** Qualcomm AI Engine Direct (QNN) on the HTP NPU.

- Model: Depth Anything V2 Small, the `w8a16` DLC from Qualcomm AI Hub.
- Input: 518×518. Licence on the AI Hub card: MIT.
- Fallback: if QNN packaging blocks progress for more than 3 working days in Phase 1,
  switch to LiteRT with its Qualcomm AI Engine Direct accelerator. It uses the same NPU at
  ~31 ms, and the C# interface (§5.3) does not change.
- **Non-Snapdragon phones:** the ML layer reports `InferenceUnavailable`, and the ARCore
  sources keep working unchanged. A GPU backend for other phones is out of scope (§11).

Rejected: Unity Sentis has no NPU path on Android. Depth Anything V3 Small is ~45 ms on
the same chip, about 2× slower, with no corridor-specific benefit shown.

## 5. Architecture

### 5.1 Data flow

```
ARCameraManager.frameReceived (30 Hz)
   │  XRCpuImage (YUV) + intrinsics + sensor pose @ image timestamp
   ▼
DepthInferenceScheduler (AR)  ── skips frames while one inference is in flight (≤15 Hz)
   │  upright RGB 518×518 letterboxed + Y plane at the same scale
   ▼
QnnDepthPlugin (native .so, worker thread) ── relative inverse depth 518×518, ~22 ms
   ▼
FloorAlignedDepth (Core)  ◄── FloorPlane (from ARCore horizontal plane)
   │  scale s, shift t, floor mask, residual
   │  + optional confident raw-depth samples
   ▼
VerticalPlaneExtractor (Core) ── RANSAC vertical planes on non-floor pixels
   ▼
BaseEdgeRefiner (Core) ── snaps each plane's floor line to image edges
   ▼
WallMap (Core, world-anchored) ◄── ARCore vertical planes (as observations)
   │  fused walls with extents, covariance, source history
   ▼
WallMeasurementEngine (existing) ── aimed + nearest, every frame from the current pose
   ▼
WallDistanceService.Latest / Updated (unchanged API)
```

Two clocks run independently:

- **Detection** (≤15 Hz) updates the `WallMap`.
- **Measurement** (30 Hz, every `Update`) recomputes distances from the current camera
  pose against the walls in the map.

"Quickly" therefore means one detection, ~40 ms after the wall enters view; after that,
readings arrive at full frame rate.

### 5.2 Core components (pure C#, no AR Foundation; unit-testable)

| Unit | Responsibility | Input → output |
|---|---|---|
| `FloorPlane` | Metric floor in session space; gives camera height | point + up (gravity-aligned) |
| `InverseDepthImage` | Network output with its own camera | `float[] w×h`, `DepthIntrinsics` (inference-image space), `Pose`, timestamp |
| `FloorAlignedDepth` | Robust affine fit `1/z = s·d + t` using floor pixels; floor-plane inliers become the floor mask | image + `FloorPlane` + optional metric samples → `AlignmentResult {s, t, floorMask, inliers, residual, reason}` |
| `VerticalPlaneExtractor` | Back-projects non-floor pixels with aligned depth. Finds up to 4 vertical planes by RANSAC on the floor-plane projection (2D line fits), checks verticality against gravity, records each segment's extent along the floor | aligned depth → `List<WallObservation>` |
| `BaseEdgeRefiner` | Projects each observation's floor line into the image. Searches a ±12 px band for the **lowest** strong, collinear intensity edge (avoids the top of a skirting board). Re-fits the floor line from snapped points | `WallObservation` + Y plane + `FloorPlane` → refined `WallObservation` (`FloorEdge` when ≥60% of samples snap) |
| `WallMap` | World-anchored wall tracks. Associates by normal (≤8°) and offset (≤15 cm). Fuses with an information filter on (normal angle, offset); unions extents; ages out unseen walls; resets on session change. Emits `WallCandidate`s with a nominal 2.4 m height, which is a UI extent, not a measurement | observations + ARCore vertical planes → `IReadOnlyList<WallCandidate>` |

Existing code that is reused:

- `WallMeasurementEngine`: aimed/nearest selection, polygon raycast, filtering.
- `WallGeometry`, `WallCandidate`, `MeasurementFilter`.
- `AssistedWallGeometry`: its floor-line-to-vertical-plane construction becomes the
  shared helper behind both `VerticalPlaneExtractor` and `BaseEdgeRefiner`.

`DepthPlaneFitter` and `DepthValidator` remain as the ARCore raw-depth path.

### 5.3 AR / native components

| Unit | Responsibility |
|---|---|
| `IDepthInference` (AR) | `bool TryBegin(in InferenceRequest)`, `bool TryCollect(out InverseDepthImage)`, `string Status`. Hides the backend so LiteRT can replace QNN without touching anything else |
| `QnnDepthInference` (AR) | P/Invoke wrapper over the native plugin. Owns its output buffers; no per-frame managed allocation |
| `DepthInferenceScheduler` (AR) | Takes the CPU image at `frameReceived`. Converts YUV→RGB at reduced size via `XRCpuImage.ConvertAsync`, rotates to display-upright and letterboxes to 518×518. Stamps the request with the pose (`ARCoreSensorPose`) and intrinsics for that image, rotated and scaled into inference-image space. Never queues more than one request |
| `FloorPlaneSource` (AR) | Picks the largest tracked horizontal plane below the camera; exposes `FloorPlane`. Reuses `AssistedWallController`'s floor logic |
| `libwalldepth.so` (native, NDK C++) | Loads the DLC through the QNN HTP backend once. Runs on its own thread. C API: `wd_init(modelPath, out err)`, `wd_submit(rgb, w, h)`, `wd_poll(outBuf) → status`, `wd_shutdown()`. Logs per-inference latency |

Model delivery:

- `tools/fetch-models.ps1` downloads the pinned DLC revision and checks its SHA-256.
- The file goes into `Assets/StreamingAssets/Models/`; model weights are git-ignored.
- On first launch the app copies it to `persistentDataPath`, because native code cannot
  read compressed APK entries.
- The QNN runtime libraries (`libQnnHtp*.so`, `libQnnSystem.so`) must match the SDK
  version the DLC was built with. Both versions are pinned in `tools/models.lock.json`.

### 5.4 Readings, sources and quality

New `MeasurementSource` values:

- `LearnedDepth`: wall plane from floor-aligned network depth only.
- `FloorEdge`: the same wall with its base snapped to image edges. Geometric; ranks above
  `LearnedDepth`.

New `QualityLabel` values:

- `LearnedEstimate`
- `EdgeConfirmed`
- `CrossChecked`: two independent sources agree within 5 cm (FloorEdge or LearnedDepth
  vs. an ARCore plane or raw-depth fit).

New `FailureReason` values:

- `NoFloor`
- `AlignmentFailed`
- `InferenceUnavailable`
- `InferenceStale`: no detection completed for over 1 s **and** the `WallMap` holds no
  wall that satisfies the reading (aimed: none under the crosshair; nearest: none in
  view). Walls already in the map keep producing valid readings from the tracked pose.

Existing sources and labels are unchanged. Readings remain NaN when invalid, never 0.

## 6. Failure handling

| Condition | Behaviour |
|---|---|
| No floor plane yet | Alignment uses confident raw-depth samples (≥0.5) if ≥200 are present. Otherwise `NoFloor`; the HUD prompts "point at the floor for a moment". No assumed camera height, ever |
| Alignment residual > 3% of depth, or < 500 floor inliers | Frame discarded (`AlignmentFailed`); the `WallMap` keeps its previous walls |
| Network output is not affine in inverse depth (checked in Phase 1) | Aligner switches to an affine fit in depth. Chosen once from Phase 1 data; not decided at runtime |
| Plugin init failure / non-Snapdragon / model missing | `InferenceUnavailable` once; the ARCore path continues; a CSV note is written |
| Tracking lost | Existing behaviour: invalidate readings, reset filters, keep the `WallMap` only within the same session id |
| Thermal throttling (inference > 60 ms for 5 s) | Scheduler drops to 5 Hz; logged |
| People walking through view | Rejected by RANSAC: non-planar and non-vertical. A person standing still is accepted as a "wall" — known limitation, same as today's furniture caveat |

## 7. Logging

Bump the CSV revision to `floor-aligned-v1`. Add these columns:

- `infer_ms`, `infer_hz`, `align_s`, `align_t`, `align_residual`, `floor_inliers`
- `walls_in_map`, `aimed_source_chain` (e.g. `FloorEdge+ARPlane`), `edge_snap_frac`
- `floor_h_m`, `thermal_state`

On demand only (debug toggle), dump one inference input/output pair. This lets the aligner
be replayed off-device with real frames, which the SceneView prior work lacked.

## 8. Testing

**EditMode (Core, synthetic):** a `SyntheticCorridor` fixture renders exact inverse depth
and a grey image for a parameterised corridor:

- width, camera height, yaw/pitch/roll, end wall, door recesses, skirting board, a person
  occluder.
- Then: an unknown affine transform, Gaussian noise, and a mild nonlinearity to stand in
  for network error.

| Unit | Test |
|---|---|
| `FloorAlignedDepth` | Recovers s and t within 1% under noise; rejects frames with < 500 floor inliers |
| `VerticalPlaneExtractor` | Finds left, right and end walls with offset ≤ 2 cm and normal ≤ 1° on clean data |
| `BaseEdgeRefiner` | Snaps to the base, not the skirting-board top |
| `WallMap` | Convergence, association, ageing, and session reset |

**On-device bench scene:**

- 10-minute run logging `infer_ms` p50/p95, end-to-end detection latency, frame rate and
  thermal state.

**Field protocol** (extends the README benchmark, corridors first):

- Side walls at 0.5 / 1 / 1.5 / 2 / 3 m; an end wall at 1–5 m.
- Standing and walking at normal pace.
- Painted plain walls; glossy tiled floors; skirting boards; doors; dim lighting; people.
- Three trials each, with the tape reference taken from the camera position.
- Report: time-to-first-valid reading; median and P95 absolute error by source and distance;
  valid fraction; update rate.

## 9. Phases

| Phase | Output | Gate to continue |
|---|---|---|
| **0. Plane triage** | Stock AR Foundation "Plane Detection" sample on the 13R, in the same corridor | Stock app finds the floor → if ours doesn't, fix our app first. Stock app also finds nothing → floor-less fallback (§6) becomes primary; re-review this spec |
| **1. Runtime spike** (throwaway) | DLC running on the 13R through `libwalldepth.so`; latency logged; 20 frames dumped | p50 ≤ 35 ms on device; output parameterisation confirmed (inverse vs. depth) against the floor plane |
| **2. Core algorithms** | §5.2 units + synthetic tests | All EditMode tests green, existing tests included |
| **3. AR integration** | Scheduler, plugin wrapper, `FloorPlaneSource`, new sources/labels, CSV columns, HUD prompts | Builds; bench scene meets §10 rate targets |
| **4. Field benchmark & tuning** | §8 protocol results committed as CSV + summary | §10 targets reviewed with Rachit |

## 10. Proposed targets (for Rachit to confirm)

"Fast and as little error as possible" made measurable. These are proposals, not
commitments.

- **Acquisition:** median ≤ 0.5 s, P95 ≤ 1.5 s from a wall entering view to its first valid
  reading, once the floor is tracked.
- **Rates:** readings at 30 Hz; detection ≥ 10 Hz sustained for 10 min.
- **Error:** reported per source. Initial aim is median ≤ 5 cm on corridor side walls at
  1–3 m (`FloorEdge`), then minimised further in Phase 4.

## 11. Out of scope

- A dedicated **left-wall / right-wall corridor reading** (centring aid). The `WallMap`
  makes this cheap; it needs its own small spec if wanted.
- Mini-GPS / campus-map fusion; iOS; ceilings.
- Semantic "wall vs. furniture" classification.
- An ML backend for non-Snapdragon phones.

## 12. Risks

| Risk | Mitigation |
|---|---|
| Glossy corridor floors defeat ARCore floor detection | Raw-depth scale fallback (§6); Phase 0 tests on the actual floor |
| QNN runtime/SDK version mismatch, or redistribution terms for the `libQnn*` libraries | Pin both versions; check the SDK licence in Phase 1 before shipping; LiteRT fallback (§4) |
| w8a16 quantisation distorts depth non-affinely | Phase 1 compares w8a16 vs float on the dumped frames; ship float (31 ms) if the residual is materially worse |
| Skirting boards / dark kick-plates bias the edge snap | "Lowest collinear edge" rule; synthetic test; specific field condition |
| Wall base out of view when close (portrait, phone held level, ≲2 m) | `LearnedDepth` path still works; `WallMap` keeps walls seen earlier while approaching |
| Thermal load from sustained NPU + camera | 15 Hz cap; adaptive drop to 5 Hz; logged |

## 13. References

- ARCore Depth API — https://developers.google.com/ar/develop/depth
- ARCore Raw Depth codelab — https://codelabs.developers.google.com/codelabs/arcore-rawdepthapi
- ARCore supported devices (13R: Depth API) — https://developers.google.com/ar/devices
- Qualcomm AI Hub, Depth-Anything-V2 — https://huggingface.co/qualcomm/Depth-Anything-V2
- Qualcomm AI Hub, Depth-Anything-V3 — https://huggingface.co/qualcomm/Depth-Anything-V3
- LiteRT Qualcomm AI Engine Direct — https://developers.google.com/edge/litert/next/qualcomm
- SceneView ML depth (affine scale fit on ARCore samples) — https://github.com/sceneview/sceneview/pull/4271
- PTC-Depth, Bayesian scale for foundation depth — https://arxiv.org/abs/2604.01791
