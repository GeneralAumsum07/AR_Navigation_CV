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
            config.allowDenseDetection = true;
            NewSessionId();
            Assisted = GetComponent<AssistedWallController>();
            if (Assisted == null) Assisted = gameObject.AddComponent<AssistedWallController>();
        }

        void OnEnable()
        {
            ARSession.stateChanged += OnArStateChanged;
        }

        void OnDisable()
        {
            ARSession.stateChanged -= OnArStateChanged;
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
            if (depthSource != null) depthSource.ResetFrames();
        }

        void NewSessionId()
        {
            SessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        void Update()
        {
            UpdateSessionState();

            if (candidateSource == null || arCamera == null)
            {
                State = WallDistanceSessionState.ArUnavailable;
                StateDetail = "service not wired: missing candidate source or camera";
                return;
            }

            candidateSource.Refresh();
            Assisted.Refresh();

            double now = Time.realtimeSinceStartupAsDouble;
            var input = new EngineInput
            {
                sessionId = SessionId,
                sessionTracking = State == WallDistanceSessionState.Tracking,
                now = now,
                cameraPose = new Pose(arCamera.transform.position, arCamera.transform.rotation),
                worldToClip = arCamera.projectionMatrix * arCamera.worldToCameraMatrix,
                crosshairRay = arCamera.ViewportPointToRay(new Vector3(crosshairViewport.x, crosshairViewport.y, 0f)),
                candidates = Assisted.Active ? Assisted.Candidates : candidateSource.Candidates,
                // Assisted mode waits for user selection; do not secretly fall back to depth.
                depth = !Assisted.Active && depthSource != null ? depthSource.Latest : null,
                denseDepth = !Assisted.Active && depthSource != null ? depthSource.LatestDense : null,
                depthSupported = depthSource != null && depthSource.DepthSupported,
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
