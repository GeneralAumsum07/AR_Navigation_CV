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
