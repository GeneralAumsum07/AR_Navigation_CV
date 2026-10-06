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
