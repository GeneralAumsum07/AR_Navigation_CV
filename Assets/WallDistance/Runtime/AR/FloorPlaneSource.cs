using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using WallDistance.Core;

namespace WallDistance.AR
{
    /// <summary>
    /// Feeds ARCore's upward-facing planes to <see cref="FloorSelector"/>, which picks the lowest
    /// plausible level and withholds the floor while it jumps. Selection logic lives in Core so
    /// it is tested without a device; this adapter only translates ARPlanes.
    /// </summary>
    public sealed class FloorPlaneSource : MonoBehaviour
    {
        public ARPlaneManager planeManager;
        public Camera arCamera;

        // Replaced by the service so the selector uses the same (serialized) config as everything else.
        public FloorSelector Selector { get; set; } = new FloorSelector(new MeasurementConfig());
        public FloorPlane CurrentPlane => Selector.Current;
        public bool HasFloor => CurrentPlane.IsValid;

        readonly List<FloorCandidate> _candidates = new List<FloorCandidate>(8);

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
            _candidates.Clear();
            if (planeManager != null && arCamera != null)
            {
                foreach (var p in planeManager.trackables)
                {
                    // Re-read every frame: ARCore refines plane height as it sees more floor, and
                    // the selector judges whether that refinement is plausible.
                    if (p.trackingState != TrackingState.Tracking || p.subsumedBy != null
                        || p.alignment != PlaneAlignment.HorizontalUp) continue;
                    _candidates.Add(new FloorCandidate(p.trackableId.ToString(), p.center, p.normal, p.size.x * p.size.y));
                }
            }
            Vector3 cam = arCamera != null ? arCamera.transform.position : Vector3.zero;
            Selector.Update(_candidates, cam, Time.realtimeSinceStartupAsDouble);
        }

        /// <summary>A new AR session is a new coordinate frame: old floor levels mean nothing in it.</summary>
        public void ResetFloor() => Selector.Reset();
    }
}
