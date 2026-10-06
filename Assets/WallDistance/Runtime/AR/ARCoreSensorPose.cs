using System;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace WallDistance.AR
{
    /// <summary>
    /// Match CPU intrinsics to ARCore's physical camera orientation. The Unity rendering
    /// camera is display-oriented (90 degrees different in portrait). Native pointers use
    /// Unity's documented UnityXRNativeSession/Frame version-1 wrappers, never guessed offsets.
    /// </summary>
    internal sealed class ARCoreSensorPose
    {
        [StructLayout(LayoutKind.Sequential)]
        struct NativePointer { public int version; public IntPtr pointer; }
        readonly float[] _sensor = new float[7], _display = new float[7];
        public string Status { get; private set; } = "Waiting for physical camera pose";
        public double FrameTimestamp { get; private set; } = double.NaN;

        public bool TryGet(ARSession session, ARCameraManager manager, Camera camera, out Pose result)
        {
            result = default;
#if UNITY_ANDROID && !UNITY_EDITOR
            if (session == null || session.subsystem == null || manager == null || manager.subsystem == null) return false;
            IntPtr nativeSession = session.subsystem.nativePtr;
            if (nativeSession == IntPtr.Zero) return false;
            var sp = Marshal.PtrToStructure<NativePointer>(nativeSession);
            if (sp.version != 1 || sp.pointer == IntPtr.Zero) { Status="Unsupported session pointer version"; return false; }
            var parameters = new XRCameraParams { zNear=camera.nearClipPlane,zFar=camera.farClipPlane,
                screenWidth=Screen.width,screenHeight=Screen.height,screenOrientation=Screen.orientation };
            if (!manager.subsystem.TryGetLatestFrame(parameters,out var frame) || frame.nativePtr == IntPtr.Zero) return false;
            var fp = Marshal.PtrToStructure<NativePointer>(frame.nativePtr);
            if (fp.version != 1 || fp.pointer == IntPtr.Zero) { Status="Unsupported frame pointer version"; return false; }
            if (!frame.TryGetTimestamp(out long ns)) return false;
            FrameTimestamp=ns*1e-9;
            IntPtr nativeCamera=IntPtr.Zero, sensorPose=IntPtr.Zero, displayPose=IntPtr.Zero;
            try
            {
                ArFrame_acquireCamera(sp.pointer,fp.pointer,out nativeCamera);
                if (nativeCamera == IntPtr.Zero) return false;
                ArCamera_getTrackingState(sp.pointer,nativeCamera,out int tracking);
                if (tracking != 0) { Status="Native camera is not tracking"; return false; }
                ArPose_create(sp.pointer,IntPtr.Zero,out sensorPose);
                ArPose_create(sp.pointer,IntPtr.Zero,out displayPose);
                if(sensorPose==IntPtr.Zero || displayPose==IntPtr.Zero) return false;
                ArCamera_getPose(sp.pointer,nativeCamera,sensorPose);
                ArCamera_getDisplayOrientedPose(sp.pointer,nativeCamera,displayPose);
                ArPose_getPoseRaw(sp.pointer,sensorPose,_sensor);
                ArPose_getPoseRaw(sp.pointer,displayPose,_display);
                var sq=UnityRotation(_sensor); var dq=UnityRotation(_display);
                var relative=Quaternion.Inverse(dq)*sq;
                result = new Pose(camera.transform.position + camera.transform.rotation *
                    (Quaternion.Inverse(dq)*(UnityPosition(_sensor)-UnityPosition(_display))),
                    camera.transform.rotation*relative);
                Status=$"Physical sensor pose; display rotation {Quaternion.Angle(Quaternion.identity,relative):F0} deg";
                return true;
            }
            catch (Exception e) { Status=e.GetType().Name+": "+e.Message; return false; }
            finally
            {
                if(sensorPose!=IntPtr.Zero) ArPose_destroy(sensorPose);
                if(displayPose!=IntPtr.Zero) ArPose_destroy(displayPose);
                if(nativeCamera!=IntPtr.Zero) ArCamera_release(nativeCamera);
            }
#else
            Status="Physical ARCore pose available on Android device only";
            return false;
#endif
        }
        // Reflect OpenGL's right-handed -Z-forward world into Unity's left-handed +Z-forward
        // convention. Quaternion vector components are pseudovectors under this reflection.
        static Quaternion UnityRotation(float[] p) => new Quaternion(-p[0],-p[1],p[2],p[3]);
        static Vector3 UnityPosition(float[] p) => new Vector3(p[4],p[5],-p[6]);
        const string Lib="arcore_sdk_c";
        [DllImport(Lib)] static extern void ArFrame_acquireCamera(IntPtr s,IntPtr f,out IntPtr c);
        [DllImport(Lib)] static extern void ArCamera_release(IntPtr c);
        [DllImport(Lib)] static extern void ArCamera_getTrackingState(IntPtr s,IntPtr c,out int state);
        [DllImport(Lib)] static extern void ArPose_create(IntPtr s,IntPtr raw,out IntPtr pose);
        [DllImport(Lib)] static extern void ArPose_destroy(IntPtr p);
        [DllImport(Lib)] static extern void ArCamera_getPose(IntPtr s,IntPtr c,IntPtr p);
        [DllImport(Lib)] static extern void ArCamera_getDisplayOrientedPose(IntPtr s,IntPtr c,IntPtr p);
        [DllImport(Lib)] static extern void ArPose_getPoseRaw(IntPtr s,IntPtr p,[Out] float[] raw);
    }
}
