# Floor-free metric scale: design

- **Date:** 2026-10-07
- **Status:** Draft, awaiting Rachit's review
- **Amends:** `2026-10-06-fast-wall-detection-design.md` §3, §5.1, §5.2, §5.4, §6, §7, §8, §9
  (exact replacements in §9 of this document)
- **Scope:** `Assets/WallDistance` Core + AR assemblies. No native or model changes.
- **Device of record:** OnePlus 13R (no ToF sensor)

---

## 1. Understanding

**Stated by Rachit (2026-10-07):**

- "I want the app to work properly. Even for smooth floors."
- Accuracy target: **within ~10%** at typical corridor distances (e.g. 2.7–3.3 m at 3 m).
- Start-up: **a few seconds of walking** before readings appear is acceptable.
- Assuming a camera height is "cheating" (rejected as option E, 2026-10-07).

**Assumptions (correct these if wrong):**

- The phone is still hand-held while walking; the floor is in view in most frames when the
  phone points down the corridor (true in 80/80 frames of run 150956, by inspection).
- ARCore keeps tracking most of the time even where it finds no planes (it did for ~90% of
  run 150956).

## 2. Problem (evidence)

The 2026-10-06 design rests on one premise: **"The floor supplies metric scale"**, and the
floor comes from ARCore plane detection. On glossy corridor floors that source is absent.

| Run | Place | ARCore planes | Aligned frames | Valid aimed readings |
|---|---|---|---|---|
| 150956 (2026-10-07) | Grey glossy-tiled corridor, doors 10/12A | 0 for all 51 s | 20 / 1511 | 25 |
| 132228 (2026-10-07) | Pale glossy-tiled corridor | 0 | not re-counted | not re-counted |
| 34 field CSVs (2026-09) | Campus corridors | 0 in 33/34 | — | 0 in last 5 |

The existing no-floor fallback needs ≥ 200 raw-depth pixels with confidence ≥ 128 **in one
frame**. Run 150956 had a median of **35** such pixels per frame (≥ 10 in 65% of frames,
≥ 200 in 22%). The fallback therefore fails, although the clues it discards add up to
hundreds per second.

The A–D map fixes (continuity, see-through carving, range-aware association, side-segment
distance) were never exercised in that run: no floor, no learned walls.

## 3. Approach

**The depth network fixes the floor's shape by itself; only the camera height above the
floor needs metric clues, and those are pooled over seconds instead of demanded per frame.**

Why that works:

- Depth Anything V2 gives relative inverse depth `d`; metric depth is `1/z = s·d + t`.
  Write `z = k / (d + r)` with `k = 1/s`, `r = t/s`.
- For the wrong `r` the floor back-projects as a curved surface; only the right `r` makes it
  a flat, horizontal plane (gravity is known from the AR pose). So **flatness alone fixes
  `r`** and gives the camera height in network units, `h_rel`.
- The one remaining unknown is `k`, equivalently the metric camera height `H = k·h_rel`.
  `H` is a physical quantity that changes slowly, so sparse clues can be pooled over time.
  It is **measured, never assumed** (the 2026-10-06 rule stands).

**Spike evidence (throwaway, `scratchpad/selfalign.py`, 2026-10-07):** on the 14 dumped frames
that had an ARCore floor, the self-aligned depth (scaled by the true `H`) was compared with
the ARCore-floor-aligned depth on non-floor pixels:

- 9 frames with the floor visible over a range of depths: **0.8–7.4% median difference**
  (median ~5%).
- 1 frame (pale glossy corridor) differed by 28%. There the right wall was closer to
  vertical under the self-alignment (2.5° tilt) than under the ARCore fit (6.3°), so the
  ARCore fit is the likelier one to be wrong. One frame; weak evidence.
- 4 frames looked mostly at a wall with little or no floor: the shift is not identifiable
  there and must be rejected, not used (§4.1 identifiability gate).

Approaches considered:

| Approach | Glossy floor | Cost | Verdict |
|---|---|---|---|
| **Self-aligned floor + pooled camera height (chosen)** | Works when floor is in view and some confident depth exists over seconds | ~CPU only | **Chosen** |
| Second, metric depth network (e.g. DA-V2 Metric Indoor) | Works | Second NPU model on a phone already at thermal state 3; ~6% AbsRel per frame; QNN export unverified | Rejected for now |
| Fit `s, t` per frame to ARCore sparse points | Needs dozens of good points every frame | CPU | Rejected: corridors do not supply them per frame |

## 4. Design

### 4.1 `FloorSelfAligner` (Core, new)

Finds the shift `r` and the relative camera height `h_rel` of one inference frame, with no
ARCore floor.

- **Input:** `InverseDepthImage` (values, content rect, intrinsics, camera pose). World up is
  `Vector3.up` (AR Foundation session space is gravity-aligned).
- **Candidates:** pixels on a `selfAlignStride` grid (4) inside `content`, finite `d`, whose
  world ray descends by more than `selfAlignMinDescentDeg` (3°) below horizontal.
- **Model:** for a trial `r`, `z_rel = 1/(d + r)` and each candidate's height relative to the
  camera is `y = z_rel · (ray · up)` (ray with unit camera-Z, as `InverseDepthImage.WorldRay`).
- **Search:** `r` ranges over `(−min d, ∞)`. Coarse pass over 64 log-spaced values of
  `r + min d` from `1e-4·range(d)` to `1e3·range(d)`, then a 21-point linear grid between
  the neighbours of the best coarse value. (A grid, not golden-section search: the score is
  an integer count, piecewise constant in `r`, so a bracketing search can stall on a plateau.)
- Only `AffineInverseDepth` (the configured parameterisation) is supported; any other
  parameterisation fails the frame with a reason.
- **Score for a trial `r`:** seed the floor height `y0` as the median of the lowest 60% of
  candidate heights; inliers are `|y − y0| < selfAlignHeightTolerance·|y0|` (0.03); re-take
  `y0` as the inlier median and recount. Score = inlier count. Relative tolerance makes the
  score independent of the unknown scale.
- **Output (`SelfAlignment`):** `success`, `r`, `hRel = −y0`, `inliers`, `residual` (median
  `|y − y0|/|y0|` of inliers), `depthRatio`, `reason`.
- **Gates (all must pass, else `success = false`):**
  - `inliers ≥ selfAlignMinInliers` (500);
  - `residual ≤ maxAlignResidual` (existing 3%);
  - **identifiability:** the inliers' `z_rel` span a ratio `≥ selfAlignMinDepthRatio` (2.0).
    A floor seen over a narrow depth band cannot fix `r` (the four close-up frames above);
  - `r` not at either end of the search range.
- Pure C#, allocation-free after warm-up; target ≤ 5 ms p95 on the 13R, logged.

### 4.2 Height clues

One clue per inference frame, at most:

| Clue | When | Value | Weight |
|---|---|---|---|
| ARCore floor | `FloorSelector.Current` valid | `H = floor.HeightAbove(camera)` | `heightPlaneWeight` (200) |
| Raw depth | self-alignment succeeded and ≥ `minClueSamples` (10) confident samples | per sample `H_i = (z_metric / z_rel(pixel))·hRel`; clue = median of `H_i` | sample count, capped at 200 |

- Raw-depth samples come from the existing `MetricSampleCollector` with confidence ≥ 128
  (unchanged), but **stride 1** for clue collection (160×90 = 14 400 checks; the current
  stride 2 discards three quarters of an already scarce supply).
- A raw-depth clue is rejected when its samples disagree: MAD/median of `H_i` >
  `maxClueSpread` (0.15).
- Any clue outside the plausible handheld band (`FloorPlane.MinCameraHeight`–`MaxCameraHeight`,
  0.8–2.2 m) is rejected.

### 4.3 `CameraHeightEstimator` (Core, new)

Turns clues into a floor the pipeline can use.

- **State:** the floor's world height `floorY`, stored per clue as `camera.y − H`.
  Storing the floor, not `H`, lets the user raise or lower the phone: ARCore's short-term
  vertical motion is metric and accurate.
- **Window:** clues from the last `heightWindowSeconds` (5 s). Estimate = weighted median of
  `floorY`; spread = weighted median absolute deviation.
- **Ready** when the window holds ≥ `minHeightWeight` (100) total weight, from ≥ 5 clues
  spanning ≥ `minHeightSpanSeconds` (2 s), with spread ≤ `maxHeightSpreadMeters` (0.07,
  half the ~14 cm that 10% of a 1.4 m height allows). An ARCore-floor clue alone satisfies
  the weight rule but still needs the 2 s span.
- **Hold:** once ready, the last good estimate is held for up to `heightHoldSeconds` (20 s)
  while the window has **too little evidence** (weight, clue count or span below the
  thresholds). Enough evidence that **disagrees** (spread > 7 cm) is not overridden by the
  hold: not ready. Too little evidence → hold; contradicting evidence → recalibrate.
- **Tracking discontinuity** (the existing `MapContinuityGuard` clear): ARCore may have moved
  the world frame (run 150956 had camera-Y steps of −0.5, −0.8, +0.46, −1.46 m). Before the
  clear the estimator records `H_carry = camera.y − floorY`; when tracking resumes,
  `floorY = camera.y − H_carry`, all old clues are dropped, and the estimate stays ready
  under the hold rule. This avoids a multi-second "calibrating" after every jump.
- **Output:** `bool TryFloor(Vector3 camera, double now, out FloorPlane floor)` returning
  `FloorPlane(new Vector3(camera.x, floorY, camera.z), Vector3.up)`, plus diagnostics
  (`Ready`, `HeightMeters`, `SpreadMeters`, `WindowWeight`, `Source`).

### 4.4 Integration (`WallDistanceService.OnInferenceFrame`)

```
ARCore floor valid?  ── yes ─► estimator.AddPlaneClue(H); floor = ARCore floor       (unchanged path)
        │ no
        ▼
collect raw samples (stride 1) ─► selfAlign = aligner.Align(img)
        │ success ─► estimator.AddDepthClue(samples, selfAlign, img)
        ▼
estimator.TryFloor(...)  ── ready ─► floor = derived floor (FloorSource = Derived)
        │ not ready
        ▼
existing raw-depth fallback (≥200 samples) or FailureReason.Calibrating
```

- The derived floor is a normal `FloorPlane`; `FloorAlignedDepth`, extraction, the map and
  every A–D fix run unchanged downstream.
- The ARCore floor always wins when present, and also feeds the estimator so it is warm when
  the plane disappears.
- Readings from a derived floor keep their existing sources (`LearnedDepth`, `FloorEdge`);
  the floor source is a separate diagnostic.

### 4.5 Readings, HUD, logging

- New `FailureReason.Calibrating` (appended last; existing values keep their numbers): the
  estimator has clues or self-aligned frames but is not ready. HUD: "Calibrating — keep
  walking". `NoFloor` remains for "no self-alignment and no clues".
- New CSV learned columns: `floor_source` (`ARPlane` / `Derived` / `None`), `height_m`,
  `height_spread_m`, `height_weight`, `selfalign_ok`, `selfalign_ms`, `clue_samples`.

## 5. Failure handling

| Condition | Behaviour |
|---|---|
| Floor not in view, or seen over too narrow a depth band | Self-alignment fails for that frame; the held estimate is still used if ready |
| No confident raw depth for > 20 s and no ARCore floor | Back to `Calibrating`; never a guessed height |
| Clues disagree (spread > 7 cm) | Not ready; `Calibrating` |
| Glossy-floor reflections make the network see depth "below" the floor | Seed uses the lowest 60%; risk measured in replay and field (§8). Open risk |
| Pose jump / tracking loss | Map cleared (existing); floor re-placed under the camera with the carried height |
| ARCore vertical drift between jumps | Bounded by the 5 s window while clues flow; unbounded during a 20 s hold. Field-measured (§7) |

## 6. Testing

**EditMode (synthetic, `SyntheticCorridor`):**

| Unit | Test |
|---|---|
| `FloorSelfAligner` | Recovers `r` (depth within 1%) under an unknown affine transform + noise; camera pitched 10°/30°; rejects a wall-only view (identifiability); rejects too few inliers |
| `FloorSelfAligner` | Scale invariance: multiplying `d` by a constant leaves the recovered metric depth unchanged |
| Clue extraction | Synthetic raw samples at known metric depth give `H` within 2%; disagreeing samples (spread > 15%) give no clue |
| `CameraHeightEstimator` | Not ready before 2 s / 100 weight; ready after; weighted median rejects 30% outliers; raising the camera 0.3 m keeps `floorY`; hold expiry after 20 s; carry across a discontinuity; implausible clues rejected |
| Pipeline | Synthetic corridor with **no** ARCore floor and sparse metric samples → after warm-up, side walls within 5% |
| CSV | Schema columns and order |

**Offline replay:** run the C# `FloorSelfAligner` over the real dumps (as a temporary probe,
not committed) and confirm it matches the Python spike on the 9 identifiable frames.

**Field (pass criteria, for Rachit to confirm):** in the grey glossy corridor of run 150956,
tape-measured side and end walls at 1, 2 and 3 m, three trials each:

- median aimed error ≤ 10% at each distance;
- first valid reading ≤ 5 s after starting to walk, in ≥ 4 of 5 starts;
- no reading gap > 2 s while a wall is in view and ARCore is tracking.

## 7. Out of scope (v1)

- ARCore feature points as a third clue. Added only if the field run spends > 20% of
  walking time in `Calibrating` for lack of clues.
- Thermal mitigation beyond the existing 5 Hz drop. Run 150956 reached thermal state 3; its
  effect on ARCore tracking is not established.
- Fixing ARCore's own pose jumps.

## 8. Risks

| Risk | Mitigation |
|---|---|
| Confident raw-depth pixels are biased on glossy surfaces | Weighted median + spread gate; field check against tape |
| Reflections break floor flatness | Lowest-60% seed; field check; fall back to `Calibrating` |
| Hold period hides drift | 20 s cap; `height_m` logged so drift is visible in CSVs |
| 5 ms budget exceeded on device | Stride is a config value; logged as `selfalign_ms` |

## 9. Amendments to the 2026-10-06 spec

Applied in the first implementation task:

- **§3 Approach**, first sentence becomes: "The floor supplies metric scale — from ARCore
  when it finds one, otherwise from a floor the depth network fixes by flatness, scaled by a
  camera height pooled from sparse metric clues (2026-10-07 floor-free design)."
- **§5.1** data flow: `FloorPlane (from ARCore horizontal plane, or derived — FloorSelfAligner + CameraHeightEstimator)`.
- **§5.2** table: add `FloorSelfAligner` and `CameraHeightEstimator` rows (§4.1, §4.3 here).
- **§5.4**: add `FailureReason.Calibrating`.
- **§6** "No floor plane yet" row becomes: "Derived floor once the camera-height estimate is
  ready (floor-free design §4); until then the raw-depth fallback if ≥ 200 samples, else
  `Calibrating` / `NoFloor`. No assumed camera height, ever."
- **§7**: add the §4.5 columns.
- **§9 Phase 0** result recorded: in the 2026-10-07 corridors ARCore found no floor; per the
  Phase 0 rule the floor-less path becomes primary and this spec was re-reviewed. The stock
  AR Foundation plane sample was **not** run in these corridors.
