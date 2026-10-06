using UnityEngine;

namespace WallDistance.AR
{
    /// <summary>
    /// Android's thermal status, 0 (none) to 6 (shutdown), sampled at most once a second. A JNI
    /// call per CSV row would cost more than the value is worth. -1 in the Editor and before
    /// Android 10, where the API does not exist.
    /// </summary>
    public static class ThermalStatus
    {
        static int _value = -1;
        static double _readAt = double.NegativeInfinity;

        public static int Current
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                double now = Time.realtimeSinceStartupAsDouble;
                if (now - _readAt < 1.0) return _value;
                _readAt = now;
                try
                {
                    using (var version = new AndroidJavaClass("android.os.Build$VERSION"))
                    {
                        if (version.GetStatic<int>("SDK_INT") < 29) return _value = -1;
                    }
                    using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                    using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                    using (var power = activity.Call<AndroidJavaObject>("getSystemService", "power"))
                        _value = power.Call<int>("getCurrentThermalStatus");
                }
                catch (System.Exception) { _value = -1; }
#endif
                return _value;
            }
        }
    }
}
