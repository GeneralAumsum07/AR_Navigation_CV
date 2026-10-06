using UnityEngine;
using UnityEngine.XR.ARFoundation;
using WallDistance.Core;

namespace WallDistance.AR
{
    /// <summary>
    /// Draws the selected (aimed) wall's polygon outline and the perpendicular measurement line
    /// from the camera to the wall. Two LineRenderers, world space, updated each measurement.
    /// </summary>
    public sealed class WallOutlineRenderer : MonoBehaviour
    {
        public WallDistanceService service;
        [Tooltip("Line width in metres.")]
        public float lineWidth = 0.01f;

        LineRenderer _outline;
        LineRenderer _perpendicular;
        LineRenderer _nearest;
        readonly Vector3[] _scratch = new Vector3[2];

        void Awake()
        {
            if (service == null) service = FindAnyObjectByType<WallDistanceService>();
            _outline = MakeLine("SelectedWallOutline", new Color(0.35f, 0.95f, 0.45f), true);
            _perpendicular = MakeLine("PerpendicularLine", new Color(0.3f, 0.7f, 1f), false);
            _nearest = MakeLine("ClosestWall", Color.cyan, true);
        }

        void OnEnable() { if (service != null) service.Updated += OnUpdated; }
        void OnDisable() { if (service != null) service.Updated -= OnUpdated; }

        void OnUpdated(WallDistanceSnapshot s)
        {
            var aimed = s.aimed;
            DrawNearest(s.nearest);
            if (!aimed.isValid)
            {
                _outline.positionCount = 0;
                _perpendicular.positionCount = 0;
                return;
            }

            var plane = service.candidateSource.FindPlane(aimed.candidateId);
            if (aimed.source == MeasurementSource.AssistedFloor && service.Assisted.Candidates.Count > 0)
            {
                var c = service.Assisted.Candidates[0];
                _outline.positionCount = c.boundary.Count;
                for (int i=0;i<c.boundary.Count;i++) _outline.SetPosition(i,c.LocalToWorld(c.boundary[i]));
            }
            else if (plane != null)
            {
                var b = plane.boundary;
                _outline.positionCount = b.Length;
                for (int i = 0; i < b.Length; i++)
                    _outline.SetPosition(i, plane.transform.TransformPoint(new Vector3(b[i].x, 0f, b[i].y)));
            }
            else
            {
                // Depth-only fit has no polygon: show a 30 cm square on the fitted plane at the foot
                // of the perpendicular so the user can see what surface the number refers to.
                Vector3 n = aimed.surfaceNormal;
                Vector3 right = Vector3.Normalize(Vector3.Cross(Vector3.up, n));
                Vector3 up = Vector3.Cross(n, right);
                const float h = 0.15f;
                _outline.positionCount = 4;
                _outline.SetPosition(0, aimed.surfacePoint + (-right - up) * h);
                _outline.SetPosition(1, aimed.surfacePoint + ( right - up) * h);
                _outline.SetPosition(2, aimed.surfacePoint + ( right + up) * h);
                _outline.SetPosition(3, aimed.surfacePoint + (-right + up) * h);
            }

            // Perpendicular: from the camera to the foot of the perpendicular on the plane. Drawn
            // slightly below the camera so it is visible instead of starting at the lens.
            _scratch[0] = aimed.cameraPose.position + aimed.cameraPose.rotation * new Vector3(0f, -0.05f, 0.05f);
            _scratch[1] = aimed.surfacePoint;
            _perpendicular.positionCount = 2;
            _perpendicular.SetPositions(_scratch);
            _perpendicular.startColor = _perpendicular.endColor =
                aimed.quality == QualityLabel.Unreliable ? new Color(1f, 0.4f, 0.35f) : new Color(0.3f, 0.7f, 1f);
        }

        void DrawNearest(WallReading wall)
        {
            if (!wall.isValid) { _nearest.positionCount=0; return; }
            var plane=service.candidateSource.FindPlane(wall.candidateId);
            if (plane!=null) {
                _nearest.positionCount=plane.boundary.Length;
                for(int i=0;i<plane.boundary.Length;i++) _nearest.SetPosition(i,
                    plane.transform.TransformPoint(new Vector3(plane.boundary[i].x,0,plane.boundary[i].y)));
                return;
            }
            // This square marks an observed patch; it is not a claimed wall boundary.
            var right=Vector3.Cross(Vector3.up,wall.surfaceNormal).normalized*0.22f;
            var up=Vector3.Cross(wall.surfaceNormal,right).normalized*0.22f;
            _nearest.positionCount=4;
            _nearest.SetPosition(0,wall.surfacePoint-right-up);
            _nearest.SetPosition(1,wall.surfacePoint+right-up);
            _nearest.SetPosition(2,wall.surfacePoint+right+up);
            _nearest.SetPosition(3,wall.surfacePoint-right+up);
        }

        LineRenderer MakeLine(string name, Color color, bool loop)
        {
            var go = new GameObject(name, typeof(LineRenderer));
            go.transform.SetParent(transform, false);
            var lr = go.GetComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.loop = loop;
            lr.widthMultiplier = lineWidth;
            lr.positionCount = 0;
            lr.material = new Material(Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default"));
            lr.startColor = lr.endColor = color;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            return lr;
        }
    }
}
