using System;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using WallDistance.Core;

namespace WallDistance.AR
{
    /// <summary>
    /// Copies the RAW environment depth image (and its confidence image, when the device offers
    /// one) into a managed <see cref="DepthFrame"/> every time ARCore delivers a new one.
    /// Raw rather than smoothed: temporal smoothing hides exactly the frame-to-frame
    /// disagreement we use to flag unreliable readings.
    ///
    /// Depth images on ARCore are small (typically 160x90 or 160x120) so the per-frame copy is
    /// a few tens of kilobytes - cheap enough to do on the main thread.
    /// </summary>
    [RequireComponent(typeof(AROcclusionManager))]
    public sealed class ARDepthFrameSource : MonoBehaviour
    {
        [Tooltip("Camera whose pose is stamped onto each depth frame (the XR Origin camera).")]
        public Camera arCamera;
        [Tooltip("Needed for intrinsics; usually on the same GameObject as the AR camera.")]
        public ARCameraManager cameraManager;

        AROcclusionManager _occlusion;
        readonly DepthFrame _frame = new DepthFrame();
        // Dense depth is retained for diagnostics only: smoothness does not establish scale.
        // Raw measurements can also be wrong; confidence and geometry checks remain necessary.
        readonly DepthFrame _dense = new DepthFrame();
        bool _hasDense;
        bool _hasFrame;
        ARSession _session;
        readonly ARCoreSensorPose _sensorPose = new ARCoreSensorPose();
        public string SensorPoseInfo => _sensorPose.Status;
        public string ConfidenceInfo { get; private set; } = "";
        public double ConfidenceTimestamp { get; private set; } = double.NaN;
        public double CameraTimestamp => _sensorPose.FrameTimestamp;

        /// <summary>True when the provider reports environment depth as supported at runtime.</summary>
        public bool DepthSupported => _occlusion != null && _occlusion.descriptor != null
                                     && _occlusion.descriptor.environmentDepthImageSupported == Supported.Supported;

        /// <summary>Whether a confidence image is available on this device.</summary>
        public bool ConfidenceSupported => _occlusion != null && _occlusion.descriptor != null
                                          && _occlusion.descriptor.environmentDepthConfidenceImageSupported == Supported.Supported;

        /// <summary>Latest depth frame or null if none received yet. May be stale; engine checks age.</summary>
        public DepthFrame Latest => _hasFrame ? _frame : null;
        /// <summary>Latest DENSE environment depth frame (no confidence image), or null.</summary>
        public DepthFrame LatestDense => _hasDense ? _dense : null;
        /// <summary>Diagnostics: non-zero fraction of the latest dense frame.</summary>
        public float DenseValidFraction { get; private set; } = float.NaN;
        /// <summary>Diagnostics: raw ushort values of the dense frame - centre pixel and non-zero min/max, plus a hex sample.</summary>
        public string DenseValueInfo { get; private set; } = "";
        public string RawValueInfo { get; private set; } = "";

        /// <summary>Diagnostics: fraction of depth pixels that are non-zero in the latest frame.</summary>
        public float ValidDepthFraction { get; private set; } = float.NaN;
        /// <summary>Diagnostics: mean confidence byte over the latest frame (NaN if no confidence image).</summary>
        public float MeanConfidence { get; private set; } = float.NaN;
        /// <summary>Diagnostics: depth image resolution and format as reported by ARCore.</summary>
        public string DepthFormatInfo { get; private set; } = "";
        public string LastError { get; private set; }

        void Awake()
        {
            _occlusion = GetComponent<AROcclusionManager>();
            _session = FindAnyObjectByType<ARSession>();
            // Ask for depth even though we do not render occlusion; Medium keeps the CPU image
            // flowing at a rate that stays inside the 0.5 s staleness window on mid-range phones.
            _occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Medium;
            // Keep smoothing enabled for the dense diagnostic stream. Identical sampled
            // values alone do not prove a frozen image; provider timestamps are logged now.
            _occlusion.environmentDepthTemporalSmoothingRequested = true;
            if (cameraManager == null) cameraManager = FindAnyObjectByType<ARCameraManager>();
            if (arCamera == null && cameraManager != null) arCamera = cameraManager.GetComponent<Camera>();
        }

        void OnEnable() { _occlusion.frameReceived += OnFrame; }
        void OnDisable() { _occlusion.frameReceived -= OnFrame; ResetFrames(); }

        public void ResetFrames()
        {
            _hasFrame = _hasDense = false;
            _frame.providerTimestamp = _dense.providerTimestamp = double.NaN;
            ValidDepthFraction = DenseValidFraction = MeanConfidence = float.NaN;
            RawValueInfo = DenseValueInfo = "";
        }

        void OnFrame(AROcclusionFrameEventArgs args)
        {
            if (ARSession.state != ARSessionState.SessionTracking) { ResetFrames(); return; }
            CaptureRaw();
            // Acquire independently: a missing raw image must not suppress diagnostics.
            CaptureDense();
        }

        void CaptureRaw()
        {
            if (!DepthSupported) return;
            if (!_occlusion.TryAcquireRawEnvironmentDepthCpuImage(out XRCpuImage depth))
            {
                LastError = "raw depth image unavailable";
                return;
            }
            using (depth)
            {
                if (!depth.valid || depth.planeCount < 1)
                {
                    LastError = "depth image invalid";
                    return;
                }
                if (!cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intr))
                {
                    LastError = "intrinsics unavailable";
                    return;
                }

                // Check before copying: an old buffer must retain both its original geometry
                // and its first-observed time, so downstream staleness checks remain meaningful.
                if (!double.IsNaN(_frame.providerTimestamp) && depth.timestamp <= _frame.providerTimestamp) return;
                if (!_sensorPose.TryGet(_session,cameraManager,arCamera,out var sensorPose))
                { LastError=_sensorPose.Status; return; }
                // Do not bind delayed depth to the current pose. Raw frames are ~10 Hz on
                // some devices; allow one such interval, and log the clocks independently.
                if (Math.Abs(_sensorPose.FrameTimestamp-depth.timestamp)>0.12)
                { LastError="Depth and camera timestamps differ by over 120 ms"; return; }

                int w = depth.width, h = depth.height;
                int n = w * h;
                if (_frame.depthMillimeters == null || _frame.depthMillimeters.Length != n)
                {
                    _frame.depthMillimeters = new ushort[n];
                    _frame.confidence = null;
                }

                var plane = depth.GetPlane(0);
                if (!CopyDepth(plane, depth.format, w, h, _frame.depthMillimeters))
                {
                    LastError = $"unsupported depth format {depth.format}";
                    return;
                }

                // Confidence is optional; without it every sample is treated as fully confident
                // and only the geometric inlier test filters bad returns.
                _frame.confidence = null;
                if (ConfidenceSupported && _occlusion.TryAcquireEnvironmentDepthConfidenceCpuImage(out XRCpuImage conf))
                {
                    using (conf)
                    {
                        ConfidenceTimestamp=conf.timestamp;
                        var cp = conf.planeCount > 0 ? conf.GetPlane(0) : default;
                        ConfidenceInfo=$"{conf.format} {conf.width}x{conf.height} row={cp.rowStride} pixel={cp.pixelStride} dt={conf.timestamp-depth.timestamp:F6}";
                        if (conf.valid && conf.format == XRCpuImage.Format.OneComponent8 && conf.width == w && conf.height == h
                            && conf.planeCount >= 1 && Math.Abs(conf.timestamp-depth.timestamp)<0.002)
                        {
                            if (_frame.confidence == null || _frame.confidence.Length != n) _frame.confidence = new byte[n];
                            CopyBytes(conf.GetPlane(0), w, h, _frame.confidence);
                            int max=0, reliable=0;
                            for(int i=0;i<n;i++) { max=Math.Max(max,_frame.confidence[i]); if(_frame.confidence[i]>=128) reliable++; }
                            ConfidenceInfo += $" max={max} reliable={reliable}/{n}";
                        }
                    }
                }

                // Frame-level diagnostics: separates "sparse raw depth" from "confidence gate" when
                // the fitter reports every patch pixel rejected.
                int nonZero = 0; long confSum = 0;
                for (int i = 0; i < n; i++)
                {
                    if (_frame.depthMillimeters[i] != 0) nonZero++;
                    if (_frame.confidence != null) confSum += _frame.confidence[i];
                }
                ValidDepthFraction = (float)nonZero / n;
                MeanConfidence = _frame.confidence != null ? (float)confSum / n : float.NaN;
                DepthFormatInfo = $"{w}x{h} {depth.format}";
                RawValueInfo = Describe(_frame.depthMillimeters, w, h);

                // Intrinsics are expressed in the colour CPU image resolution; rescale them to the
                // depth grid. ARCore guarantees the depth image shares the colour image's field
                // of view and orientation, so a pure per-axis scale is correct.
                var baseIntr = new DepthIntrinsics
                {
                    fx = intr.focalLength.x, fy = intr.focalLength.y,
                    cx = intr.principalPoint.x, cy = intr.principalPoint.y,
                    width = intr.resolution.x, height = intr.resolution.y,
                };
                _frame.intrinsics = baseIntr.ScaledTo(w, h);
                _frame.width = w;
                _frame.height = h;
                // Receipt-time pose is an approximation, not a synchronized acquisition pose.
                // Preserve it when the provider returns the same image again.
                if (!_frame.TryStamp(depth.timestamp, Time.realtimeSinceStartupAsDouble,
                    sensorPose)) return;
                _hasFrame = true;
                LastError = null;
            }

        }

        /// <summary>Copy the dense environment depth image; shares pose/intrinsics logic with the raw path.</summary>
        void CaptureDense()
        {
            // Explicit smoothed image, used solely to compare provider outputs in diagnostics.
            if (!_occlusion.TryAcquireSmoothedEnvironmentDepthCpuImage(out XRCpuImage depth)) return;
            using (depth)
            {
                if (!depth.valid || depth.planeCount < 1) return;
                if (!cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intr)) return;
                if (!double.IsNaN(_dense.providerTimestamp) && depth.timestamp <= _dense.providerTimestamp) return;
                int w = depth.width, h = depth.height, n = w * h;
                if (_dense.depthMillimeters == null || _dense.depthMillimeters.Length != n)
                    _dense.depthMillimeters = new ushort[n];
                if (!CopyDepth(depth.GetPlane(0), depth.format, w, h, _dense.depthMillimeters)) return;
                _dense.confidence = null; // dense depth has no confidence channel; geometry gates it instead
                int nonZero = 0;
                for (int i = 0; i < n; i++) if (_dense.depthMillimeters[i] != 0) nonZero++;
                DenseValidFraction = (float)nonZero / n;
                DenseValueInfo = Describe(_dense.depthMillimeters, w, h);
                var baseIntr = new DepthIntrinsics
                {
                    fx = intr.focalLength.x, fy = intr.focalLength.y,
                    cx = intr.principalPoint.x, cy = intr.principalPoint.y,
                    width = intr.resolution.x, height = intr.resolution.y,
                };
                _dense.intrinsics = baseIntr.ScaledTo(w, h);
                _dense.width = w; _dense.height = h;
                if (!_dense.TryStamp(depth.timestamp, Time.realtimeSinceStartupAsDouble,
                    new Pose(arCamera.transform.position, arCamera.transform.rotation))) return;
                _hasDense = true;
            }
        }

        /// <summary>Centre pixel, non-zero min/max, and a hex sample of 5 pixels across the middle row.</summary>
        static string Describe(ushort[] px, int w, int h)
        {
            int min = int.MaxValue, max = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i] == 0) continue;
                if (px[i] < min) min = px[i];
                if (px[i] > max) max = px[i];
            }
            int c = (h / 2) * w + w / 2;
            var sb = new System.Text.StringBuilder();
            sb.Append("centre=").Append(px[c]).Append(" min=").Append(min == int.MaxValue ? 0 : min).Append(" max=").Append(max).Append(" hex=");
            for (int k = 0; k < 5; k++) sb.Append(px[(h / 2) * w + (w * (k + 1)) / 6].ToString("X4")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        static bool CopyDepth(XRCpuImage.Plane plane, XRCpuImage.Format format, int w, int h, ushort[] dst)
        {
            var data = plane.data;
            int rowStride = plane.rowStride;
            int pixelStride = plane.pixelStride;
            switch (format)
            {
                case XRCpuImage.Format.DepthUint16:
                    for (int v = 0; v < h; v++)
                    {
                        int row = v * rowStride;
                        for (int u = 0; u < w; u++)
                        {
                            int i = row + u * pixelStride;
                            dst[v * w + u] = (ushort)(data[i] | (data[i + 1] << 8));
                        }
                    }
                    return true;
                case XRCpuImage.Format.DepthFloat32:
                    // Float32 depth is metres; store as millimetres to share one code path.
                    for (int v = 0; v < h; v++)
                    {
                        int row = v * rowStride;
                        for (int u = 0; u < w; u++)
                        {
                            int i = row + u * pixelStride;
                            float m = BitConverter.ToSingle(new[] { data[i], data[i + 1], data[i + 2], data[i + 3] }, 0);
                            dst[v * w + u] = (ushort)Mathf.Clamp(Mathf.RoundToInt(m * 1000f), 0, ushort.MaxValue);
                        }
                    }
                    return true;
                default:
                    return false;
            }
        }

        static void CopyBytes(XRCpuImage.Plane plane, int w, int h, byte[] dst)
        {
            var data = plane.data;
            int rowStride = plane.rowStride;
            int pixelStride = plane.pixelStride;
            for (int v = 0; v < h; v++)
            {
                int row = v * rowStride;
                for (int u = 0; u < w; u++) dst[v * w + u] = data[row + u * pixelStride];
            }
        }
    }
}
