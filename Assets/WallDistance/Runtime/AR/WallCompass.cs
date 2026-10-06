using UnityEngine;
using WallDistance.Core;

namespace WallDistance.AR
{
    public sealed class WallCompass : MonoBehaviour
    {
        AndroidJavaObject _sensor;
        void OnEnable()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try {
                using (var player=new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity=player.GetStatic<AndroidJavaObject>("currentActivity"))
                    _sensor=new AndroidJavaObject("com.arnav.walldistance.WallCompass",activity);
            } catch (System.Exception e) { Debug.LogWarning("Compass unavailable: "+e.Message); }
#endif
        }
        void OnDisable() { if (_sensor!=null) { _sensor.Call("stop"); _sensor.Dispose(); _sensor=null; } }
        public string Describe(WallReading wall)
        {
            if (!wall.isValid) return "";
            float heading=_sensor!=null ? _sensor.Call<float>("getHeading") : float.NaN;
            float bearing=WallBearing.TowardWall(heading,wall.cameraPose.rotation*Vector3.forward,
                wall.surfacePoint-wall.cameraPose.position);
            return WallBearing.Format(bearing)+(float.IsNaN(bearing) ? " — move phone in figure 8" : " magnetic");
        }
    }
}
