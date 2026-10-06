using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using WallDistance.Core;

namespace WallDistance.AR
{
    /// <summary>
    /// Feeds the depth network. On each camera frame it decides whether to start an inference
    /// (backend idle, rate allows it), turns the CPU image into the upright, letterboxed 518²
    /// RGB the model expects, and stamps it with the pose and intrinsics of THAT image.
    /// Collected results are raised as <see cref="FrameReady"/> on the main thread.
    ///
    /// Conversion is synchronous (plan deviation D2): ConvertAsync completes on a later frame,
    /// when the only pose available would belong to a different image.
    /// </summary>
    [DefaultExecutionOrder(-50)]
    public sealed class DepthInferenceScheduler : MonoBehaviour
    {
        public const int Size = 518;

        public ARSession session;
        public ARCameraManager cameraManager;
        public Camera arCamera;
        public FloorPlaneSource floorSource;

        public IDepthInference Backend { get; set; }
        public Func<IDepthInference> BackendFactory { get; set; }
        /// <summary>Set by the service on every session change; stamped on capture, never on collection.</summary>
        public string SessionId { get; set; }
        public InferenceRateGovernor Governor { get; } = new InferenceRateGovernor();
        public event Action<InverseDepthImage> FrameReady;

        public float CollectedHz { get; private set; }
        public double LastInferenceMs { get; private set; } = double.NaN;
        /// <summary>
        /// Main-thread milliseconds spent converting the CPU image and resampling it to 518²
        /// for the last prepared frame. Conversion is synchronous so the pose matches the image
        /// (deviation D2); this number is how the bench shows what that costs per frame.
        /// </summary>
        public double LastPrepareMs { get; private set; } = double.NaN;
        public string Status { get; private set; } = "waiting for camera";
        public int DumpRemaining { get; private set; }

        readonly ARCoreSensorPose _sensorPose = new ARCoreSensorPose();
        // Two targets: the one the pipeline is reading while the next inference fills the other.
        readonly InverseDepthImage[] _images = { new InverseDepthImage(Size, Size), new InverseDepthImage(Size, Size) };
        int _next;
        readonly byte[] _rgb = new byte[Size * Size * 3];
        NativeArray<byte> _converted;
        byte[] _sensorRgb = Array.Empty<byte>();

        // Bilinear lookup table for one (sensor size, quarter turns): inference pixel ->
        // top-left sensor pixel + weights. Rebuilt only when the sensor size or orientation changes.
        int _lutW, _lutH, _lutK = -1;
        int[] _lutIndex;
        float[] _lutFx, _lutFy;

        double _lastSubmit = double.NegativeInfinity, _lastDump = double.NegativeInfinity;
        int _collectedThisWindow;
        double _windowStart;
        InverseDepthImage _dumpPendingImage;
        string _dumpPendingDir;

        void Awake()
        {
            if (session == null) session = FindAnyObjectByType<ARSession>();
            if (cameraManager == null) cameraManager = FindAnyObjectByType<ARCameraManager>();
            if (arCamera == null && cameraManager != null) arCamera = cameraManager.GetComponent<Camera>();
            if (floorSource == null) floorSource = FindAnyObjectByType<FloorPlaneSource>();
        }

        void OnEnable()
        {
            if (Backend == null && BackendFactory != null) Backend = BackendFactory();
            if (cameraManager != null) cameraManager.frameReceived += OnCameraFrame;
        }

        void OnDisable()
        {
            if (cameraManager != null) cameraManager.frameReceived -= OnCameraFrame;
            // Disabling the host stops its coroutine too; the backend's task owner must still
            // release an initialization that finishes after this callback.
            Backend?.Dispose();
            Backend = null;
        }

        void OnDestroy()
        {
            if (_converted.IsCreated) _converted.Dispose();
            Backend?.Dispose();
        }

        /// <summary>Development builds: save the next <paramref name="frames"/> prepared inputs (Phase 1 data).</summary>
        public void RequestDump(int frames) => DumpRemaining = Mathf.Max(0, frames);

        void OnCameraFrame(ARCameraFrameEventArgs args)
        {
            if (ARSession.state != ARSessionState.SessionTracking) return;
            double now = Time.realtimeSinceStartupAsDouble;
            bool canInfer = Backend != null && Backend.IsAvailable && !Backend.IsBusy
                            && now - _lastSubmit >= Governor.MinIntervalSeconds;
            // Dumps are spaced 0.25 s apart so 20 frames cover several seconds of motion.
            bool wantDump = DumpRemaining > 0 && now - _lastDump >= 0.25;
            if (!canInfer && !wantDump) return;

            if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage image)) { Status = "no CPU image"; return; }
            using (image)
            {
                if (!cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intr)) { Status = "no intrinsics"; return; }
                if (!_sensorPose.TryGet(session, cameraManager, arCamera, out Pose sensorPose)) { Status = _sensorPose.Status; return; }
                // The pose must be this image's pose. 5 ms is well under one 30 fps frame (33 ms).
                if (Math.Abs(_sensorPose.FrameTimestamp - image.timestamp) > 0.005)
                {
                    Status = $"pose/image clock mismatch {(_sensorPose.FrameTimestamp - image.timestamp) * 1000:F1} ms";
                    return;
                }

                var sensorIntr = new DepthIntrinsics
                {
                    fx = intr.focalLength.x, fy = intr.focalLength.y,
                    cx = intr.principalPoint.x, cy = intr.principalPoint.y,
                    width = intr.resolution.x, height = intr.resolution.y,
                }.ScaledTo(image.width, image.height);
                int k = InferenceGeometry.ChooseQuarterTurns(sensorPose.rotation, arCamera.transform.up);
                var geo = InferenceGeometry.Create(image.width, image.height, k, Size);

                // Stopwatch ticks, not Time: realtimeSinceStartup has too coarse a resolution on
                // some Android builds for a few-millisecond interval.
                long prepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (!ConvertToRgb(image)) return;
                var target = _images[_next];
                Resample(image.width, image.height, geo, target.luma);
                LastPrepareMs = (System.Diagnostics.Stopwatch.GetTimestamp() - prepStart) * 1000.0
                                / System.Diagnostics.Stopwatch.Frequency;
                target.intrinsics = geo.Intrinsics(sensorIntr);
                target.cameraPose = geo.CameraPose(sensorPose);
                  target.sessionId = SessionId;
                target.timestamp = now;
                target.content = geo.content;
                target.inferenceMilliseconds = double.NaN;

                string dumpDir = null;
                if (wantDump)
                {
                    dumpDir = WriteDump(target, geo, sensorIntr, image.timestamp);
                    _lastDump = now;
                    DumpRemaining--;
                }

                if (canInfer && Backend.TryBegin(new InferenceRequest { rgb = _rgb, size = Size, target = target }))
                {
                    _lastSubmit = now;
                    _next ^= 1;
                    if (dumpDir != null) { _dumpPendingImage = target; _dumpPendingDir = dumpDir; }
                    Status = "running";
                }
            }
        }

        void Update()
        {
            double now = Time.realtimeSinceStartupAsDouble;
            if (Backend != null && Backend.TryCollect(out InverseDepthImage img))
            {
                LastInferenceMs = img.inferenceMilliseconds;
                if (!double.IsNaN(img.inferenceMilliseconds)) Governor.Record(img.inferenceMilliseconds, now);
                _collectedThisWindow++;
                if (ReferenceEquals(img, _dumpPendingImage)) WriteOutput(img, _dumpPendingDir);
                FrameReady?.Invoke(img);
            }
            if (now - _windowStart >= 1.0)
            {
                CollectedHz = (float)(_collectedThisWindow / Math.Max(1e-3, now - _windowStart));
                _collectedThisWindow = 0;
                _windowStart = now;
            }
        }

        bool ConvertToRgb(XRCpuImage image)
        {
            // Transformation.None keeps the sensor's memory order: row 0 is the TOP of the image,
            // matching the "v grows down" convention. (Texture uploads need MirrorY; we do not.)
            var p = new XRCpuImage.ConversionParams(image, TextureFormat.RGB24, XRCpuImage.Transformation.None);
            int bytes = image.GetConvertedDataSize(p);
            if (!_converted.IsCreated || _converted.Length != bytes)
            {
                if (_converted.IsCreated) _converted.Dispose();
                _converted = new NativeArray<byte>(bytes, Allocator.Persistent);
                _sensorRgb = new byte[bytes];
            }
            try { image.Convert(p, _converted); }
            catch (Exception e) { Status = "convert failed: " + e.Message; return false; }
            _converted.CopyTo(_sensorRgb);
            return true;
        }

        void Resample(int w, int h, InferenceGeometry geo, byte[] luma)
        {
            if (_lutIndex == null || _lutW != w || _lutH != h || _lutK != geo.quarterTurns) BuildLut(w, h, geo);
            int stride = w * 3;
            for (int i = 0; i < Size * Size; i++)
            {
                int o = i * 3;
                int s = _lutIndex[i];
                if (s < 0)
                {
                    // Letterbox padding is black; its network output is discarded (NaN) anyway.
                    _rgb[o] = _rgb[o + 1] = _rgb[o + 2] = 0;
                    luma[i] = 0;
                    continue;
                }
                float fx = _lutFx[i], fy = _lutFy[i];
                for (int c = 0; c < 3; c++)
                {
                    float top = _sensorRgb[s + c] + (_sensorRgb[s + 3 + c] - _sensorRgb[s + c]) * fx;
                    float bot = _sensorRgb[s + stride + c] + (_sensorRgb[s + stride + 3 + c] - _sensorRgb[s + stride + c]) * fx;
                    _rgb[o + c] = (byte)(top + (bot - top) * fy + 0.5f);
                }
                // Rec. 601 luma, integer weights; the edge refiner only needs relative contrast.
                luma[i] = (byte)((77 * _rgb[o] + 150 * _rgb[o + 1] + 29 * _rgb[o + 2]) >> 8);
            }
        }

        void BuildLut(int w, int h, InferenceGeometry geo)
        {
            _lutW = w; _lutH = h; _lutK = geo.quarterTurns;
            _lutIndex = new int[Size * Size];
            _lutFx = new float[Size * Size];
            _lutFy = new float[Size * Size];
            for (int v = 0; v < Size; v++)
            for (int u = 0; u < Size; u++)
            {
                int i = v * Size + u;
                if (!geo.content.Contains(new Vector2Int(u, v))) { _lutIndex[i] = -1; continue; }
                Vector2 s = geo.InferenceToSensor(new Vector2(u, v));
                // Clamp so the 2×2 bilinear footprint never leaves the sensor image.
                float sx = Mathf.Clamp(s.x, 0f, w - 1.001f), sy = Mathf.Clamp(s.y, 0f, h - 1.001f);
                int x0 = (int)sx, y0 = (int)sy;
                _lutIndex[i] = (y0 * w + x0) * 3;
                _lutFx[i] = sx - x0;
                _lutFy[i] = sy - y0;
            }
        }

        // ---------------------------------------------------------------- dumps (Phase 1 data)

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        string WriteDump(InverseDepthImage img, InferenceGeometry geo, DepthIntrinsics sensorIntr, double imageTimestamp)
        {
            try
            {
                string dir = Path.Combine(Application.persistentDataPath, "InferenceDumps",
                    "frame_" + imageTimestamp.ToString("F6", Inv).Replace('.', '_'));
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, "input.rgb"), _rgb);
                File.WriteAllBytes(Path.Combine(dir, "luma.u8"), img.luma);
                var floor = floorSource != null ? floorSource.CurrentPlane : default;
                var sb = new StringBuilder();
                sb.Append('{');
                Kv(sb, "size", Size); Kv(sb, "quarterTurns", geo.quarterTurns);
                Kv(sb, "sensorWidth", geo.sensorWidth); Kv(sb, "sensorHeight", geo.sensorHeight);
                Kv(sb, "imageTimestamp", imageTimestamp);
                KvArr(sb, "sensorIntrinsics", sensorIntr.fx, sensorIntr.fy, sensorIntr.cx, sensorIntr.cy);
                KvArr(sb, "intrinsics", img.intrinsics.fx, img.intrinsics.fy, img.intrinsics.cx, img.intrinsics.cy);
                KvArr(sb, "content", img.content.x, img.content.y, img.content.width, img.content.height);
                KvArr(sb, "cameraPosition", img.cameraPose.position.x, img.cameraPose.position.y, img.cameraPose.position.z);
                KvArr(sb, "cameraRotationXYZW", img.cameraPose.rotation.x, img.cameraPose.rotation.y, img.cameraPose.rotation.z, img.cameraPose.rotation.w);
                Kv(sb, "hasFloor", floor.IsValid ? 1 : 0);
                KvArr(sb, "floorPoint", floor.point.x, floor.point.y, floor.point.z);
                KvArr(sb, "floorUp", true, floor.up.x, floor.up.y, floor.up.z);
                sb.Append('}');
                File.WriteAllText(Path.Combine(dir, "meta.json"), sb.ToString());
                return dir;
            }
            catch (Exception e) { Status = "dump failed: " + e.Message; return null; }
        }

        void WriteOutput(InverseDepthImage img, string dir)
        {
            _dumpPendingImage = null;
            if (dir == null) return;
            var bytes = new byte[img.values.Length * 4];
            Buffer.BlockCopy(img.values, 0, bytes, 0, bytes.Length);   // little-endian float32 on ARM64
            File.WriteAllBytes(Path.Combine(dir, "output.f32"), bytes);
        }

        static void Kv(StringBuilder sb, string k, double v) =>
            sb.Append('"').Append(k).Append("\":").Append(v.ToString("R", Inv)).Append(',');

        static void KvArr(StringBuilder sb, string k, params double[] v) => KvArr(sb, k, false, v);

        static void KvArr(StringBuilder sb, string k, bool last, params double[] v)
        {
            sb.Append('"').Append(k).Append("\":[");
            for (int i = 0; i < v.Length; i++) { if (i > 0) sb.Append(','); sb.Append(v[i].ToString("R", Inv)); }
            sb.Append(']');
            if (!last) sb.Append(',');
        }
    }
}
