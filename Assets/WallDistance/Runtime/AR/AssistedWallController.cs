using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using WallDistance.Core;

namespace WallDistance.AR
{
    /// <summary>
    /// Honest fallback for textureless walls. ARCore estimates the floor and camera motion;
    /// the user identifies the wall base. No guessed depth or assumed camera height is used.
    /// The measured section is explicitly bounded by the two selected endpoints.
    /// </summary>
    public sealed class AssistedWallController : MonoBehaviour
    {
        public WallDistanceService service;
        ARPlaneManager _planes;
        ARRaycastManager _rays;
        ARAnchorManager _anchors;
        ARAnchor _first, _wall;
        ARPlane _floor;
        readonly List<ARRaycastHit> _hits = new List<ARRaycastHit>();
        readonly List<WallCandidate> _candidates = new List<WallCandidate>();
        readonly WallCandidate _candidate = new WallCandidate();
        readonly Vector2[] _boundary = new Vector2[4];
        LineRenderer _preview;
        float _width;
        string _session;
        int _generation;
        bool _targetValid;
        Vector3 _target;
        string _feedback;
        float _feedbackUntil;
        public bool Active { get; private set; }
        public bool HasWall => _wall != null && _wall.trackingState == TrackingState.Tracking;
        public string Instruction { get; private set; } = "Automatic mode: scan the room, floor and wall edges";
        public string CaptureLabel => _first == null ? "Set first base point" : "Set second base point";
        public bool CanCapture => Active && !HasWall && _targetValid;
        public IReadOnlyList<WallCandidate> Candidates => _candidates;

        void Awake()
        {
            if (service == null) service = GetComponent<WallDistanceService>();
            _planes = FindAnyObjectByType<ARPlaneManager>();
            _rays = FindAnyObjectByType<ARRaycastManager>();
            _anchors = FindAnyObjectByType<ARAnchorManager>();
            if (_anchors == null && _planes != null) _anchors = _planes.gameObject.AddComponent<ARAnchorManager>();
            var go = new GameObject("FloorSelectionPreview",typeof(LineRenderer));
            go.transform.SetParent(transform,false);
            _preview = go.GetComponent<LineRenderer>();
            _preview.material = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
            _preview.material.color = new Color(0.2f,0.9f,1f);
            _preview.widthMultiplier = 0.015f;
            _preview.useWorldSpace = true;
        }

        public void ToggleMode()
        {
            ClearSelection(); Active = !Active;
            if (service != null) service.Engine.Reset();
        }

        public void ClearSelection()
        {
            if (_anchors != null)
            {
                if (_first != null) _anchors.TryRemoveAnchor(_first);
                if (_wall != null) _anchors.TryRemoveAnchor(_wall);
            }
            _first = _wall = null; _floor = null; _targetValid = false;
            _candidates.Clear(); _generation++;
            if (_preview != null) _preview.positionCount = 0;
        }

        public void Refresh()
        {
            _candidates.Clear(); _targetValid = false;
            if (_session != service.SessionId) { ClearSelection(); _session = service.SessionId; }
            if (!Active) { Instruction = "Automatic: slowly scan the floor, wall edges and textured areas"; return; }
            if (service.State != WallDistanceSessionState.Tracking)
            { Instruction = "Tracking interrupted. Scan the floor and room slowly"; _preview.positionCount=0; return; }
            if (_wall != null)
            {
                if (!HasWall) { Instruction = "Selected wall anchor lost. Rescan or select again"; return; }
                var rot = _wall.transform.rotation;
                const float height = 3f;
                _boundary[0]=new Vector2(-_width/2,-height/2);
                _boundary[1]=new Vector2(_width/2,-height/2);
                _boundary[2]=new Vector2(_width/2,height/2);
                _boundary[3]=new Vector2(-_width/2,height/2);
                _candidate.Set("assisted-"+_generation,true,_wall.transform.position + rot*Vector3.forward*(height/2),rot,_boundary);
                _candidate.source = MeasurementSource.AssistedFloor;
                _candidates.Add(_candidate);
                Instruction = "Assisted wall selected. Aim between the base points; move to measure";
                _preview.positionCount=0;
                return;
            }
            FindFloorTarget();
            if (Time.unscaledTime < _feedbackUntil) { Instruction = _feedback; return; }
            if (_targetValid)
            {
                Instruction = _first == null
                    ? "Aim at the wall-floor junction. Set the first base point"
                    : "Aim along the SAME wall base, at least 40 cm away. Set the second point";
                Vector3 start = _first != null ? _first.transform.position : _target-Vector3.right*0.08f;
                _preview.positionCount=2;
                _preview.SetPosition(0,start+Vector3.up*0.015f);
                _preview.SetPosition(1,(_first != null ? _target : _target+Vector3.right*0.08f)+Vector3.up*0.015f);
            }
            else
            {
                Instruction = "Point down and scan a clear floor slowly until a cyan target appears";
                _preview.positionCount=0;
            }
        }

        void FindFloorTarget()
        {
            if (_rays == null || _planes == null) return;
            var camera = service.arCamera;
            Vector2 screen = camera.ViewportToScreenPoint(service.crosshairViewport);
            _hits.Clear();
            // Only actual detected polygon hits establish the floor. A second point may use
            // the same floor plane outside its polygon, since boundaries often end near walls.
            if (_first == null && _rays.Raycast(screen,_hits,TrackableType.PlaneWithinPolygon))
                foreach (var hit in _hits)
                {
                    var p = _planes.GetPlane(hit.trackableId);
                    if (p == null || p.alignment != PlaneAlignment.HorizontalUp || p.trackingState != TrackingState.Tracking) continue;
                    float height = camera.transform.position.y-hit.pose.position.y;
                    if (height < 0.4f || height > 2.5f || hit.distance > 4f) continue;
                    _floor=p; _target=hit.pose.position; _targetValid=true; return;
                }
            if (_first != null && _first.trackingState == TrackingState.Tracking && _floor != null)
            {
                Ray ray = camera.ViewportPointToRay(service.crosshairViewport);
                Vector3 up = _floor.transform.up;
                float denom = Vector3.Dot(ray.direction,up);
                if (denom > -0.15f) return;
                float t = Vector3.Dot(_first.transform.position-ray.origin,up)/denom;
                if (t <= 0 || t > 4f) return;
                _target=ray.GetPoint(t); _targetValid=true;
            }
        }

        public void Capture()
        {
            if (!CanCapture || _anchors == null || _floor == null) return;
            if (_first == null)
            {
                _first = _anchors.AttachAnchor(_floor,new Pose(_target,_floor.transform.rotation));
                if (_first == null) Feedback("Could not anchor floor point. Scan a wider floor area");
                return;
            }
            if (!AssistedWallGeometry.TryCreate(_first.transform.position,_target,_floor.transform.up,
                service.arCamera.transform.position,out var pose,out _width,out var reason))
            { Feedback(reason); return; }
            _wall = _anchors.AttachAnchor(_floor,pose);
            if (_wall == null) { Feedback("Could not anchor wall. Please rescan"); return; }
            _anchors.TryRemoveAnchor(_first); _first=null;
            service.Engine.Reset();
        }

        // Keep failed selections readable across per-frame UI updates.
        void Feedback(string message) { _feedback=message; _feedbackUntil=Time.unscaledTime+3f; Instruction=message; }

        void OnDestroy()
        {
            if (_preview != null && _preview.material != null) Destroy(_preview.material);
        }
    }
}
