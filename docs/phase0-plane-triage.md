# Phase 0 — plane triage (OnePlus 13R)

- Date: 2026-10-06
- Corridor / floor / lighting: TBD — record the corridor used for the September 13/14 logs, its floor material and lighting during the six trials.
- Our app detection mode (scene / runtime): `Vertical` / `Horizontal | Vertical`.
  - `Assets/Scenes/WallMeasurement.unity:202`: `m_DetectionMode: 2`.
  - `Assets/WallDistance/Runtime/AR/ARWallCandidateSource.cs:34`: `_planes.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;` (inside `Awake`).
  - Live Editor inspection confirmed `Assets/Scenes/WallMeasurement.unity` is open; outside Play Mode the plane manager reports `Vertical`. This confirms the serialized setting, not the Android runtime result.

| App | Trial | Floor plane found? | Seconds to first floor plane |
|---|---|---|---|
| Our app | 1 | TBD — physical trial pending | TBD |
| Our app | 2 | TBD — physical trial pending | TBD |
| Our app | 3 | TBD — physical trial pending | TBD |
| Stock Plane Detection sample | 1 | TBD — physical trial pending | TBD |
| Stock Plane Detection sample | 2 | TBD — physical trial pending | TBD |
| Stock Plane Detection sample | 3 | TBD — physical trial pending | TBD |

## Decision

- [ ] Stock app finds the floor, ours does not → fix our app first (record the fix), then `floor-primary`.
- [ ] Both find the floor → `floor-primary`.
- [ ] Stock app also finds nothing → `floor-less`: STOP. Re-review the spec with Rachit (§9).

**Gate status: physical verification deferred by Rachit.** No floor-primary or floor-less conclusion can be drawn yet. No Android device appeared in `adb devices -l` during preparation. Rachit subsequently explicitly authorized implementation without physical tests; the implementation proceeds with floor availability unverified.

## Preparation and verification

- Baseline verification: `unity command run_tests --mode editor --filter WallDistance --filter_type assembly --timeout 240 --no-banner` passed all 71 tests, with 0 failures or skipped tests.
- Stock repository: `C:/Users/Rachit/arfoundation-samples`, upstream `Unity-Technologies/arfoundation-samples`, branch `6.3`, commit `d7c67fb4e06d1652bf4c8d9a41d5b0d41baafa81` (local branch `phase0-unity-6.3`). Its README selects branch `6.3` for Unity 6000.3 LTS. It pins AR Foundation / ARCore 6.3.5; our app pins 6.6.2. Record that version difference when interpreting the trials.
- Branch selection: upstream `6.6` was inspected first and pins AR Foundation 6.6.2, but requires Unity 6000.6.0b10 and URP 17.6.0. Using upstream `6.3` keeps the stock sample compatible with the installed Unity 6000.3.5f1 without downgrading its packages.
- Selected stock scene: `Assets/Scenes/PlaneDetection/PlaneDetectionMode/PlaneDetectionMode.unity`; serialized detection mode is `-1` (all modes).
- Local sample-only build entry point: `Assets/Editor/Phase0PlaneDetectionBuild.cs` sets the build list to that single scene and builds an Android development APK. It does not modify this project's runtime or scene.
- Sample build status: blocked before compilation. Both the initial Unity CLI launch and a direct Unity 6000.3.5f1 batch launch exited 1 with Package Manager failing to update the manifest: `The "path" argument must be of type string. Received undefined`. The direct retry rules out the CLI launch as the sole cause. Licensing resolved successfully despite an earlier access-token warning. No APK was produced. Logs: `C:/Users/Rachit/arfoundation-samples/Logs/build-Android-1791290210429.log` and `C:/Users/Rachit/arfoundation-samples/Logs/phase0-direct-build.log`. Root cause of the Package Manager failure remains unestablished.
- Installation and physical trials: pending. Connect the OnePlus 13R with USB debugging, install the sample APK once built, and do three 30-second floor sweeps per app at chest height. Record whether a horizontal plane appeared and its first-appearance time. Start CSV recording before each trial in our app.
- Plan completeness: the six supplied plan files contain Tasks 0–20. Tasks 21–23 are referenced but their steps are absent; their plan text is needed before those tasks can be executed strictly.
