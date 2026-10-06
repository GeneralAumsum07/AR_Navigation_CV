# WallDistance — AR wall-distance module

## Current deliverable — September 16: detection + compass

`Builds/Android/WallDetectionCompass.apk` restores the dense-depth wall fitting used by
the earlier demo. Automatic mode is the default; no assisted selection is required.
Green marks the aimed wall. Cyan marks the closest detected wall or sampled wall-like
patch in the current view. A 5-by-3 depth grid supplements tracked AR planes for nearest
selection. These are geometric detections, not semantic recognition of walls vs furniture.

Bearings such as **264° W magnetic** point from the camera toward the selected surface.
They use Android's magnetically referenced rotation-vector sensor and the wall's relative
AR direction. Stale/unreliable compass samples show unavailable, not a fabricated heading.
Indoor metal can disturb compass readings. No location permission or true-north correction
is required. [Android sensor axes](https://developer.android.com/reference/android/hardware/SensorManager#getRotationMatrixFromVector(float[],%20float[])).

Distance accuracy is intentionally unchanged/unverified. The closest ranking inherits
depth errors and is limited to detected surfaces currently in view. Dense depth is again
enabled explicitly by the demo service (`allowDenseDetection`); the earlier raw-only
diagnostic build below is historical. 71 synthetic tests pass; device validation of this
APK is pending installation by the user.

Reports two distances from the phone camera to detected walls, in metres, with an explicit
quality label on every reading:

| Reading | Meaning |
|---|---|
| **Aimed wall** | Perpendicular distance from the camera to the vertical plane under the crosshair. Not the ray length — it does not change when you turn the phone. |
| **Nearest observed wall** | Shortest distance to any tracked vertical plane polygon currently inside the camera view. Walls behind you cannot be ruled out. |

Target: ±10 cm over 0.5–3 m on ordinary opaque indoor walls. **This is a target, not a
verified figure** until the OnePlus 13R field benchmark below has been run.

## Layout

```
Assets/WallDistance/
  Runtime/Core/   WallDistance.Core   pure C#, no AR dependency (geometry, depth validation, filter, engine)
  Runtime/AR/     WallDistance.AR     AR Foundation adapters + WallDistanceService + demo HUD + CSV recorder
  Editor/         WallDistance.Editor headless build entry point
  Tests/EditMode/ 66 tests over Core with synthetic walls, cameras and depth images
Assets/Scenes/WallMeasurement.unity   demo scene (the only scene in Build Settings)
```

`Core` never references AR Foundation, so the measurement logic can be unit-tested and reused
with a different provider (iOS/ARKit) by writing another adapter pair
(`ARWallCandidateSource`, `ARDepthFrameSource`).

## How a reading is produced

### September 14 diagnostic build: sensor-pose-assisted-v3

Automatic mode still requires tracked wall geometry or confidence-filtered raw depth.
Dense depth is diagnostic only: the recorded 1 m sweep produced approximately 8 m,
so accepting that stream as a measurement is not justified. CPU depth reconstruction
now uses ARCore's physical camera orientation relative to the display camera, with
camera/depth timestamp separation limited to 120 ms. This fixes an orientation mismatch;
it does not establish the sensor's distance accuracy. CSVs include native-pose status,
confidence format/stride/timestamp, and capture errors for device verification.

**Assisted Floor** provides a separate unmarked-wall workflow. Scan a clear floor,
then select two points at the same wall-floor junction, at least 40 cm apart. The first
point must hit a tracked floor polygon; the second uses that floor plane. An AR anchor
holds the selected vertical section as the phone moves. This requires a visible wall
base and user selection; it is not automatic wall recognition. Do not select a bed,
tabletop, or furniture edge in place of the wall base. The displayed section extends
3 m upward for aiming; that height is a UI extent, not a measured wall height.

The cyan **Assisted floor geometry** quality label distinguishes this estimate from
depth-validated geometry. Reset selection when changing walls. Tracking loss suppresses
measurements. Verify at measured camera-to-wall distances of 0.5, 1, 2, and 3 m before
claiming accuracy; synthetic tests establish geometry correctness only.

### Automatic processing

1. `ARWallCandidateSource` copies every **vertical, tracked, non-subsumed** `ARPlane` into a
   provider-neutral `WallCandidate` (pose + bounded boundary polygon).
2. `WallMeasurementEngine`:
   - **Aimed**: ray from the crosshair, first candidate whose *polygon* (not infinite plane) it
     hits; report the perpendicular distance to that plane.
   - **Nearest observed**: for each candidate intersecting the view frustum, shortest distance to
     the *bounded polygon* (edges included, so a wall is never extended through a doorway).
   - Both are smoothed by a time-based exponential filter (τ = 0.25 s) that resets whenever the
     candidate id or session id changes.
3. `DepthValidator` samples a 12×12 grid of **raw** depth pixels, ray-casts each onto the
   candidate polygon, reconstructs the observed 3D point from the depth value, and computes the
   signed residual to the plane. Depth is used to **validate** the plane, not averaged into it.
4. **Depth-only fallback (aimed reading only).** When no AR plane polygon is under the crosshair,
   `DepthPlaneFitter` least-squares-fits a plane to the 21×21 raw depth patch around the
   crosshair (two outlier-rejection refits, RMS ≤ 2 cm, normal within 20° of vertical) and
   reports its perpendicular distance as `source = DepthOnly`, `quality = DepthEstimate`.
   Added after field testing on 2026-09-13: on plain painted walls at close range ARCore
   produced **zero planes** for entire sessions while depth streamed at 30 Hz. This deviates
   from the original plan (planes as the only baseline) and is pending Astra's review. A real
   plane under the crosshair always takes precedence. *Nearest observed* has no depth fallback.

Quality labels (heuristic — thresholds in `MeasurementConfig`, logged into every CSV):

| Label | Condition |
|---|---|
| `DepthValidated` | ≥12 valid samples, \|median residual\| ≤ 10 cm, ≥60 % inliers within 5 cm |
| `PlaneEstimate` | No usable depth (unsupported, stale > 0.5 s, too few samples) |
| `DepthEstimate` | No AR plane; plane fitted to the raw depth patch under the crosshair. Single-source, un-cross-checked — trust below `PlaneEstimate`, and log it separately in benchmarks |
| `Unreliable` | Depth used but disagrees (median > 10 cm or < 60 % inliers) — do not trust |
| `OutOfTestedRange` | Reading outside 0.5–3 m |
| `Unavailable` | `isValid == false`; see `failure` |

Invalid readings carry `distanceMeters = NaN`, never 0.

## Integrating into the navigation app

```csharp
using WallDistance.AR;
using WallDistance.Core;

var svc = FindAnyObjectByType<WallDistanceService>();
svc.Updated += snap =>
{
    if (snap.aimed.isValid && snap.aimed.quality == QualityLabel.DepthValidated)
        Fuse(snap.aimed.distanceMeters, snap.aimed.surfaceNormal, snap.aimed.sessionId);
};
// or poll: var latest = svc.Latest;
```

Every `WallReading` carries: `isValid`, `distanceMeters` (filtered), `rawDistanceMeters`,
`kind`, `source`, `quality`, `failure`, `sessionId`, `candidateId`, `timestamp`, `cameraPose`,
`surfacePoint`, `surfaceNormal`, `depthResidualMeters`, `depthInlierFraction`, `qualityReason`.

**Coordinate frame:** all poses/points are in the AR *session* frame, not campus-map
coordinates. The frame is arbitrary per session; a session reset changes `sessionId`. Any fusion
with the mini-GPS must key on `sessionId` and treat the two frames as unrelated. Distances are invariant under rigid frame transforms; normals must be rotated into the destination frame before fusion.

**Prefab/scene reuse:** drop `AR Session`, `XR Origin` (with the managers shown in the demo
scene) and the `WallDistance` GameObject into your scene. `WallDistanceService` auto-resolves
its references if left empty.

**Camera pose gotcha:** the AR camera's `TrackedPoseDriver` must bind
`<HandheldARInputDevice>/devicePosition` and `/deviceRotation` in addition to the
`<XRHMD>/centerEye*` defaults - ARCore on a phone is a *handheld* device, not an HMD. With only
the HMD bindings the session tracks and depth flows, but the Unity camera never moves and every
reading is `NoWallInView`. The CSV `cam_*` columns staying constant is the tell-tale.

## Building

Via the CLI against the open Editor:

```bash
unity command build --target Android --outputPath Builds/Android/WallDistanceDemo.apk --options '["Development"]' --confirm true
unity command build_status
```

Headless:

```bash
unity build . --target Android --execute-method WallDistance.EditorTools.WallDistanceBuild.BuildAndroid
```

Tests:

```bash
unity command run_tests --mode editor --filter WallDistance --filter_type assembly
```

Project settings applied: Android, ARM64, IL2CPP, min SDK 25, OpenGLES3 + Vulkan, portrait;
XR Plug-in Management → ARCore loader; ARCore *required*, Depth *optional*; URP
`ARBackgroundRendererFeature` on both renderers. AR Foundation / ARCore XR Plugin **6.6.2**.

## Field benchmark (OnePlus 13R)

The CSV recorder (● Record button) writes
`Android/data/com.arnav.walldistance/files/WallDistanceLogs/walldist_<timestamp>.csv`.
Pull with `adb pull /sdcard/Android/data/com.arnav.walldistance/files/WallDistanceLogs`.

Columns include raw and filtered distances for both readings, source, quality, failure reason,
depth residual and inlier fraction, depth image age, fps, update rate, tracking state, camera pose,
surface point/normal, and `reference_m` (set `MeasurementCsvRecorder.referenceDistanceMeters` /
`conditionTag` before pressing Record — a text field for this is a TODO in the HUD).

Protocol (from the plan):
- Distances 0.5, 1, 1.5, 2, 3 m × angles 0°, 30°, 60° × 3 independently initialised trials.
- Reference from the **camera position** with tape + square. Record 10 s after acquisition.
- Painted and textured walls first, then stress: blank wall, dim light, glass, reflective,
  doors, corners, passing people, walking.
- Report median and P95 absolute error, valid-reading availability, acquisition time, filter lag;
  raw and filtered separately. Acceptance: P95 ≤ 10 cm and ≥ 90 % valid on ordinary walls;
  ≥ 30 fps, ≥ 10 updates/s over 10 min.

Conditions that fail stay documented as limitations.

## Known limitations / not in scope

### Sweep regression correction (2026-09-13)

The 23:13 sweep at approximately 1 m logged zero AR planes. Dense depth reported
7981 mm at the centre and the dense-only fitter accepted approximately 7.98 m;
the raw centre had no data. This demonstrates that a smooth fitted surface does
not establish a correct absolute distance. The dense-only fallback is removed:
the aimed fallback now requires raw depth with confidence, followed by the existing
sample-count, residual and verticality checks. Dense images remain diagnostic only.
An unavailable reading is expected when these checks cannot establish a measurement.

Provider timestamps are logged separately from local arrival times. Repeated or
out-of-order images no longer renew the frame age or camera pose. This is a
first-observation freshness bound, not synchronized sensor-to-camera pose recovery.
Camera/sensor orientation and timestamp-to-pose alignment still require physical
validation. No calibration factor was applied to force the known 1 m test distance.

The regression suite covers unsupported dense depth, raw/dense disagreement,
missing confidence and repeated provider timestamps. The original synthetic tests
do not certify the phone's physical accuracy. CSV headers identify this revision as
`raw-confidence-v2`; use those recordings for subsequent testing.

- No mini-GPS fusion or campus-position correction (next phase).
- Android only; iOS needs an ARKit adapter pair.
- Vertical planes are *candidates*; a cupboard front or whiteboard is a "wall" to this module.
- Depth acquisition pose is approximated by the camera pose at first receipt; sensor/display orientation and acquisition timing are not yet physically validated.
- Devices without the ARCore Depth API get `PlaneEstimate` only.
