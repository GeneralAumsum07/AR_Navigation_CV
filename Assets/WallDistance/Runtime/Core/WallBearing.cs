using UnityEngine;

namespace WallDistance.Core
{
    public static class WallBearing
    {
        public static float TowardWall(float cameraHeading, Vector3 cameraForward, Vector3 direction)
        {
            cameraForward.y=direction.y=0;
            if (float.IsNaN(cameraHeading) || cameraForward.sqrMagnitude<0.02f || direction.sqrMagnitude<0.0001f)
                return float.NaN;
            return Mathf.Repeat(cameraHeading+Vector3.SignedAngle(cameraForward,direction,Vector3.up),360);
        }
        public static string Format(float degrees)
        {
            if (float.IsNaN(degrees)) return "Compass unavailable";
            string[] points={"N","NE","E","SE","S","SW","W","NW"};
            int rounded=Mathf.RoundToInt(Mathf.Repeat(degrees,360))%360;
            return $"{rounded:000}° {points[Mathf.FloorToInt((rounded+22.5f)/45f)%8]}";
        }
    }
}
