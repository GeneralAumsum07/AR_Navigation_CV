using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using WallDistance.Core;

namespace WallDistance.AR
{
    /// <summary>
    /// Adapts ARPlaneManager output into provider-neutral <see cref="WallCandidate"/>s.
    /// Only VERTICAL, currently-tracked, not-subsumed planes are exposed; everything else the
    /// engine never sees. Instances are pooled per trackable id so no per-frame allocation.
    /// </summary>
    [RequireComponent(typeof(ARPlaneManager))]
    public sealed class ARWallCandidateSource : MonoBehaviour
    {
        ARPlaneManager _planes;
        readonly Dictionary<TrackableId, WallCandidate> _pool = new Dictionary<TrackableId, WallCandidate>();
        readonly List<WallCandidate> _current = new List<WallCandidate>();
        readonly List<Vector2> _scratch = new List<Vector2>(64);

        public IReadOnlyList<WallCandidate> Candidates => _current;
        public int TrackedVerticalPlaneCount { get; private set; }
        /// <summary>Every plane ARCore currently reports, any alignment - diagnostic for "is detection running at all".</summary>
        public int TotalPlaneCount { get; private set; }
        /// <summary>Largest vertical polygon area (m^2) this frame - diagnostic against the eligibility threshold.</summary>
        public float LargestVerticalAreaSqM { get; private set; }

        void Awake()
        {
            _planes = GetComponent<ARPlaneManager>();
            // Floors support assisted calibration; only vertical planes enter this adapter's
            // automatic wall list. Horizontal detection does not make the floor a wall.
            _planes.requestedDetectionMode = PlaneDetectionMode.Horizontal | PlaneDetectionMode.Vertical;
        }

        /// <summary>Refresh the candidate list from the live plane set. Call once per frame before measuring.</summary>
        public void Refresh()
        {
            _current.Clear();
            TrackedVerticalPlaneCount = 0;
            TotalPlaneCount = 0;
            LargestVerticalAreaSqM = 0f;
            foreach (var plane in _planes.trackables)
            {
                TotalPlaneCount++;
                if (plane.alignment != PlaneAlignment.Vertical) continue;
                // A subsumed plane was merged into a larger one; measuring against both would
                // double-count the same wall with slightly different geometry.
                if (plane.subsumedBy != null) continue;

                bool tracked = plane.trackingState == TrackingState.Tracking;
                if (tracked) TrackedVerticalPlaneCount++;

                if (!_pool.TryGetValue(plane.trackableId, out var cand))
                {
                    cand = new WallCandidate();
                    _pool[plane.trackableId] = cand;
                }

                _scratch.Clear();
                var boundary = plane.boundary;
                for (int i = 0; i < boundary.Length; i++) _scratch.Add(boundary[i]);

                cand.Set(plane.trackableId.ToString(), tracked, plane.transform.position, plane.transform.rotation, _scratch);
                _current.Add(cand);
                LargestVerticalAreaSqM = Mathf.Max(LargestVerticalAreaSqM, cand.Area());
            }

            // Drop pool entries for planes that no longer exist so ids are never reused stale.
            if (_pool.Count > _current.Count * 2 + 8)
            {
                var alive = new HashSet<string>();
                foreach (var c in _current) alive.Add(c.id);
                var dead = new List<TrackableId>();
                foreach (var kv in _pool) if (!alive.Contains(kv.Key.ToString())) dead.Add(kv.Key);
                foreach (var k in dead) _pool.Remove(k);
            }
        }

        /// <summary>Find the live ARPlane for a candidate id (for outline rendering). May be null.</summary>
        public ARPlane FindPlane(string candidateId)
        {
            if (string.IsNullOrEmpty(candidateId)) return null;
            foreach (var plane in _planes.trackables)
                if (plane.trackableId.ToString() == candidateId) return plane;
            return null;
        }
    }
}
