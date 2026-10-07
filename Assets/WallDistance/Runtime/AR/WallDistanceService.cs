using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using WallDistance.Core;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace WallDistance.AR
{
    /// <summary>High-level state the UI shows and integrators can branch on.</summary>
    public enum WallDistanceSessionState
    {
        Initializing,
        CameraPermissionDenied,
        ArUnsupported,
        ArNeedsInstall,
        ArUnavailable,
        TrackingLost,
        Tracking,
    }

    /// <summary>
    /// The integration surface for the rest of the campus-navigation app.
    ///
    /// - <see cref="Latest"/> is the most recent snapshot (aimed + nearest readings).
    /// - <see cref="Updated"/> fires once per measurement update.
    /// - Distances are metres; poses are in the AR SESSION frame, not campus-map coordinates.
    ///   A session reset changes <see cref="WallReading.sessionId"/> and the frame identity, so
    ///   anything that fuses these readings with the mini-GPS must key on sessionId.
    ///
    /// Attach to the XR Origin alongside ARPlaneManager, ARRaycastManager and AROcclusionManager.
    /// </summary>
    public sealed class WallDistanceService : MonoBehaviour
    {
        [Header("Wiring (auto-resolved when left empty)")]
        public ARSession session;
        public Camera arCamera;
        public ARWallCandidateSource candidateSource;
        public ARDepthFrameSource depthSource;
        public FloorPlaneSource floorSource;
        public DepthInferenceScheduler scheduler;
        [Header("Learned depth")]
        [Tooltip("Context binary in StreamingAssets/Models (Task 7 decides w8a16 or float).")]
        public string modelFileName = QnnDepthInference.DefaultModelFileName;

        [Header("Measurement")]
        public MeasurementConfig config = new MeasurementConfig();
        [Tooltip("Viewport point the 'aimed' reading measures through. (0.5, 0.5) is the screen centre.")]
        public Vector2 crosshairViewport = new Vector2(0.5f, 0.5f);

        public WallMeasurementEngine Engine { get; private set; }
        public WallDistanceSnapshot Latest { get; private set; }
        public WallDistanceSessionState State { get; private set; } = WallDistanceSessionState.Initializing;
        public string StateDetail { get; private set; } = "";
        public string SessionId { get; private set; }
        public AssistedWallController Assisted { get; private set; }
        /// <summary>Learned wall detection and the wall map (left/right come from here).</summary>
        public WallDetectionPipeline Pipeline { get; private set; }
        public bool InferenceAvailable => scheduler != null && scheduler.Backend != null && scheduler.Backend.IsAvailable;
        public string InferenceStatus => scheduler != null && scheduler.Backend != null ? scheduler.Backend.Status : "no scheduler";

        WallMapAnchoring _anchoring;
        PoseJumpGuard _poseGuard;
        /// <summary>Camera pose jumps this session (see <see cref="PoseJumpGuard"/>); CSV diagnostic.</summary>
        public int PoseJumps => _poseGuard != null ? _poseGuard.Jumps : 0;
        readonly MapContinuityGuard _continuity = new MapContinuityGuard();
        /// <summary>Wall-map clears caused by lost tracking or pose jumps this session; CSV diagnostic.</summary>
        public int MapClears => _continuity.Clears;
        readonly List<MetricSample> _metric = new List<MetricSample>(4096);
        // Raw depth is ~160x90 = 14 400 pixels; every one is read. On glossy corridors only ~35 of
        // them are confident (run 150956), and each is a camera-height clue for the derived floor.
        const int MetricStride = 1;
        FloorResolver _floorResolver;
        /// <summary>Chooses ARCore's floor or a derived one for each inference frame (floor-free design).</summary>
        public FloorResolver FloorResolver => _floorResolver;
        /// <summary>The floor the last inference frame was scaled against; invalid when there was none.</summary>
        public FloorPlane ActiveFloor { get; private set; }

        /// <summary>Raised after every measurement update with the fresh snapshot.</summary>
        public event Action<WallDistanceSnapshot> Updated;

        /// <summary>Measurement updates per second over the last second (for the benchmark log).</summary>
        public float UpdateRate { get; private set; }
        int _updatesThisWindow;
        double _windowStart;

        ARSessionState _lastArState = ARSessionState.None;

        void Awake()
        {
            if (session == null) session = FindAnyObjectByType<ARSession>();
            if (candidateSource == null) candidateSource = FindAnyObjectByType<ARWallCandidateSource>();
            if (depthSource == null) depthSource = FindAnyObjectByType<ARDepthFrameSource>();
            if (arCamera == null)
            {
                var cm = FindAnyObjectByType<ARCameraManager>();
                if (cm != null) arCamera = cm.GetComponent<Camera>();
            }
            Engine = new WallMeasurementEngine(config);
            // Both inference and AR-plane paths correct the retained map BEFORE fusing new-world data.
            Pipeline = new WallDetectionPipeline(config, map => _anchoring?.Sync(map));
            // Anchors need an anchor manager on the XR Origin; the scene does not have one.
            if (GetComponent<ARAnchorManager>() == null) gameObject.AddComponent<ARAnchorManager>();
            _anchoring = new WallMapAnchoring(transform);
            config.allowDenseDetection = true;
            // Before NewSessionId, which resets it.
            _poseGuard = new PoseJumpGuard(config);
            // Before NewSessionId, which resets it.
            _floorResolver = new FloorResolver(config.detection);
            NewSessionId();
            Assisted = GetComponent<AssistedWallController>();
            if (Assisted == null) Assisted = gameObject.AddComponent<AssistedWallController>();
            // Learned-depth capture lives on the same XR Origin; created here so the scene needs no edits.
            if (floorSource == null) floorSource = GetComponent<FloorPlaneSource>();
            if (floorSource == null) floorSource = gameObject.AddComponent<FloorPlaneSource>();
            floorSource.Selector = new FloorSelector(config);
            if (scheduler == null) scheduler = GetComponent<DepthInferenceScheduler>();
            if (scheduler == null) scheduler = gameObject.AddComponent<DepthInferenceScheduler>();
            scheduler.floorSource = floorSource;
            scheduler.SessionId = SessionId;
            scheduler.BackendFactory = () => QnnDepthInference.Create(this, modelFileName);
            // Real backend; until its async setup finishes it reports unavailable and the ARCore path runs.
            scheduler.Backend?.Dispose();
            scheduler.Backend = scheduler.BackendFactory();
        }

        void OnEnable()
        {
            ARSession.stateChanged += OnArStateChanged;
            if (scheduler != null)
            {
                if (scheduler.Backend == null && scheduler.BackendFactory != null) scheduler.Backend = scheduler.BackendFactory();
                scheduler.FrameReady += OnInferenceFrame;
            }
        }

        void OnDisable()
        {
            ARSession.stateChanged -= OnArStateChanged;
            if (scheduler != null)
            {
                scheduler.FrameReady -= OnInferenceFrame;
                scheduler.Backend?.Dispose();
                scheduler.Backend = null;
            }
        }

        void OnArStateChanged(ARSessionStateChangedEventArgs args)
        {
            // Leaving and re-entering a tracking session means a new coordinate frame.
            bool wasTracking = _lastArState == ARSessionState.SessionTracking;
            bool nowTracking = args.state == ARSessionState.SessionTracking;
            if (!wasTracking && nowTracking && _lastArState < ARSessionState.SessionInitializing)
                NewSessionId();
            _lastArState = args.state;
        }

        /// <summary>Call after ARSession.Reset() so readings are stamped with a fresh frame identity.</summary>
        public void NotifySessionReset()
        {
            NewSessionId();
            Engine.Reset();
            Pipeline.Reset();
            _anchoring.Clear();
            if (depthSource != null) depthSource.ResetFrames();
        }

        /// <summary>
        /// One finished inference: align it to the floor (or to raw depth when there is no floor
        /// yet) and fold its walls into the map. Runs on the main thread from the scheduler's
        /// Update, so the map is never touched concurrently.
        /// </summary>
        void OnInferenceFrame(InverseDepthImage img)
        {
            UpdateSessionState();
            if (img == null || img.sessionId != SessionId) return;
            if (State != WallDistanceSessionState.Tracking || Assisted.Active) return;
            // Captured before tracking (re)started: posed in a frame the map no longer trusts.
            if (!_continuity.Accepts(img.timestamp)) return;
            double now = Time.realtimeSinceStartupAsDouble;
            if (!img.IsCurrent(SessionId, now, config.detection.staleAfterSeconds))
            {
                // Keep the pipeline's dropped-frame counter, but never collect clues or replace
                // ActiveFloor from a capture whose geometry the pipeline will refuse.
                Pipeline.ProcessFrame(SessionId, img, default, null, now);
                return;
            }
            FloorPlane arFloor = floorSource != null && floorSource.HasFloor ? floorSource.CurrentPlane : default;
            _metric.Clear();
            // Without an ARCore floor, confident raw depth is both the derived floor's camera-height
            // clue and the old per-frame fallback (≥ 200 samples). With one, neither is needed.
            if (!arFloor.IsValid && depthSource != null)
                MetricSampleCollector.Collect(depthSource.Latest, img, config.detection.metricMinConfidence, now,
                    config.depthMaxAgeSeconds, MetricStride, _metric);
            FloorPlane floor = _floorResolver.Resolve(img, arFloor, _metric, now);
            ActiveFloor = floor;
            Pipeline.ProcessFrame(SessionId, img, floor, _metric, now);
        }

        void NewSessionId()
        {
            SessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
            Pipeline?.SetSession(SessionId);
            _anchoring?.Clear();
            if (scheduler != null) scheduler.SessionId = SessionId;
            // A new session is a new coordinate frame: floor levels and the last camera position
            // from the old one would read as a jump in the new one.
            _poseGuard?.Reset();
            _continuity.Reset();
            // A floor height from the old coordinate frame means nothing in the new one.
            _floorResolver?.Reset();
            ActiveFloor = default;
            if (floorSource != null) floorSource.ResetFloor();
        }

        void Update()
        {
            // Observed before the state is derived, so a jump this frame already withholds this frame.
            if (arCamera != null) _poseGuard.Observe(arCamera.transform.position, Time.realtimeSinceStartupAsDouble);
            UpdateSessionState();
            // Losing tracking (or a pose jump, which UpdateSessionState reports as lost) leaves the
            // mapped walls in a frame the returning camera may not share. Rebuilding takes about a
            // second at ~10 inferences/s; a misplaced wall otherwise lingers for up to 30 s.
            if (_continuity.Update(State == WallDistanceSessionState.Tracking, Time.realtimeSinceStartupAsDouble))
            {
                Pipeline.Map.Clear();
                _anchoring?.Clear();
                // The floor's world height went with the old frame; the camera's height above it did not.
                _floorResolver.MarkDiscontinuity();
            }

            if (candidateSource == null || arCamera == null)
            {
                State = WallDistanceSessionState.ArUnavailable;
                StateDetail = "service not wired: missing candidate source or camera";
                return;
            }

            candidateSource.Refresh();
            Assisted.Refresh();
            floorSource.Refresh();

            double now = Time.realtimeSinceStartupAsDouble;
            bool tracking = State == WallDistanceSessionState.Tracking;
            bool automatic = !Assisted.Active;
            Vector3 up = floorSource != null && floorSource.HasFloor ? floorSource.CurrentPlane.up : Vector3.up;

            // A session change empties the map before anything reads it, not at the next inference.
            Pipeline.SetSession(SessionId);
            if (tracking && automatic)
            {
                Pipeline.ObserveArPlanes(SessionId, candidateSource.Candidates, up, now);
                Pipeline.Map.Prune(now);
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
                detectionFailure = automatic
                    ? _floorResolver.Estimator.Explain(Pipeline.DetectionFailure(now, InferenceAvailable), now)
                    : FailureReason.None,
            };

            Latest = Engine.Update(input);

            _updatesThisWindow++;
            if (now - _windowStart >= 1.0)
            {
                UpdateRate = (float)(_updatesThisWindow / (now - _windowStart));
                _updatesThisWindow = 0;
                _windowStart = now;
            }

            Updated?.Invoke(Latest);
        }

        void UpdateSessionState()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
            {
                State = WallDistanceSessionState.CameraPermissionDenied;
                StateDetail = "Camera permission is required. Enable it in Settings > Apps.";
                return;
            }
#endif
            switch (ARSession.state)
            {
                case ARSessionState.None:
                case ARSessionState.CheckingAvailability:
                    State = WallDistanceSessionState.Initializing;
                    StateDetail = "Checking AR availability…";
                    break;
                case ARSessionState.Unsupported:
                    State = WallDistanceSessionState.ArUnsupported;
                    StateDetail = "This device does not support ARCore.";
                    break;
                case ARSessionState.NeedsInstall:
                case ARSessionState.Installing:
                    State = WallDistanceSessionState.ArNeedsInstall;
                    StateDetail = "Google Play Services for AR must be installed/updated.";
                    break;
                case ARSessionState.Ready:
                case ARSessionState.SessionInitializing:
                    State = WallDistanceSessionState.Initializing;
                    StateDetail = "Starting AR session… move the phone slowly.";
                    break;
                case ARSessionState.SessionTracking:
                    State = WallDistanceSessionState.Tracking;
                    StateDetail = "";
                    break;
                default:
                    State = WallDistanceSessionState.TrackingLost;
                    StateDetail = DescribeNotTracking(ARSession.notTrackingReason);
                    break;
            }
            // ARCore can say "Tracking" while its pose leaps metres in a frame. Treating that as
            // lost tracking reuses the existing safe path: readings invalid, filters cleared, and
            // no inference frame or AR plane enters the wall map with a pose from the wrong frame.
            if (State == WallDistanceSessionState.Tracking && _poseGuard != null
                && !_poseGuard.IsReliable(Time.realtimeSinceStartupAsDouble))
            {
                State = WallDistanceSessionState.TrackingLost;
                StateDetail = "Position jumped: tracking unreliable. Move slowly.";
            }
        }

        static string DescribeNotTracking(NotTrackingReason reason)
        {
            switch (reason)
            {
                case NotTrackingReason.Initializing: return "Initializing tracking… scan slowly.";
                case NotTrackingReason.Relocalizing: return "Relocalizing… return to a scanned area.";
                case NotTrackingReason.InsufficientLight: return "Too dark for tracking.";
                case NotTrackingReason.InsufficientFeatures: return "Not enough visual features. Aim at textured surfaces.";
                case NotTrackingReason.ExcessiveMotion: return "Moving too fast. Slow down.";
                case NotTrackingReason.CameraUnavailable: return "Camera unavailable.";
                case NotTrackingReason.Unsupported: return "Tracking unsupported on this device.";
                default: return "Tracking lost.";
            }
        }
    }
}
