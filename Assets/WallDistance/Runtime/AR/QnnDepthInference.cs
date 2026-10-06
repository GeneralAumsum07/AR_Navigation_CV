using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using WallDistance.Core;

namespace WallDistance.AR
{
    /// <summary>
    /// IDepthInference over libwalldepth.so (QNN on the Hexagon NPU).
    ///
    /// Setup is asynchronous so app start never blocks on it:
    /// 1. Copy the context binary out of the APK. Native code cannot read compressed APK
    ///    entries, and StreamingAssets on Android is inside the APK.
    /// 2. Read the extracted native-library directory from Java (deviation D3).
    /// 3. Load the model on a worker thread. Loading a context takes about a second.
    /// Until all three succeed, IsAvailable is false and Status says why; the ARCore path keeps
    /// measuring meanwhile.
    ///
    /// One request in flight. The result is written straight into the request's own
    /// InverseDepthImage, so there is no per-frame managed allocation.
    /// </summary>
    public sealed class QnnDepthInference : IDepthInference
    {
        public const string DefaultModelFileName = "depth_anything_v2_w8a16.ctx.bin";
        const string Lib = "walldepth";

        [DllImport(Lib)] static extern int wd_init([MarshalAs(UnmanagedType.LPStr)] string contextPath,
            [MarshalAs(UnmanagedType.LPStr)] string nativeLibDir, byte[] err, int errLen);
        [DllImport(Lib)] static extern int wd_input_size();
        [DllImport(Lib)] static extern int wd_output_size();
        [DllImport(Lib)] static extern int wd_submit(byte[] rgb, int len);
        [DllImport(Lib)] static extern int wd_poll(float[] output, int len, out double inferenceMs);
        [DllImport(Lib)] static extern int wd_describe(byte[] buf, int len);
        [DllImport(Lib)] static extern int wd_last_error(byte[] buf, int len);
        [DllImport(Lib)] static extern void wd_shutdown();

        volatile bool _available, _disposed;
        bool _initialised, _busy;
        InverseDepthImage _pending;
        readonly byte[] _msg = new byte[512];

        public bool IsAvailable => _available && !_disposed;
        public bool IsBusy => _busy;
        public string Status { get; private set; } = "starting";

        QnnDepthInference() { }

        /// <summary>Start setup on <paramref name="host"/>'s coroutine runner; returns immediately.</summary>
        public static QnnDepthInference Create(MonoBehaviour host, string modelFileName)
        {
            var q = new QnnDepthInference();
#if UNITY_ANDROID && !UNITY_EDITOR
            host.StartCoroutine(q.Setup(string.IsNullOrEmpty(modelFileName) ? DefaultModelFileName : modelFileName));
#else
            q.Status = "ML depth runs on the Android device only";
#endif
            return q;
        }

        IEnumerator Setup(string modelFileName)
        {
            Status = "copying model";
            string dir = Path.Combine(Application.persistentDataPath, "Models");
            string dest = Path.Combine(dir, modelFileName);
            string stamp = dest + ".build";
            Directory.CreateDirectory(dir);
            // Copy once per installed build: buildGUID changes with every build, so a new model in a
            // new APK is always picked up, and app restarts do not re-copy tens of MB.
            bool fresh = File.Exists(dest) && File.Exists(stamp) && File.ReadAllText(stamp) == Application.buildGUID;
            if (!fresh)
            {
                string src = Path.Combine(Application.streamingAssetsPath, "Models", modelFileName);
                using (var req = UnityWebRequest.Get(src))
                {
                    req.downloadHandler = new DownloadHandlerFile(dest) { removeFileOnAbort = true };
                    yield return req.SendWebRequest();
                    if (req.result != UnityWebRequest.Result.Success)
                    {
                        Status = $"model missing from the APK ({modelFileName}): {req.error}";
                        yield break;
                    }
                }
                File.WriteAllText(stamp, Application.buildGUID);
            }
            if (_disposed) yield break;

            string libDir;
            try { libDir = NativeLibraryDir(); }
            catch (Exception e) { Status = "cannot read nativeLibraryDir: " + e.Message; yield break; }

            Status = "loading model on the NPU";
            var err = new byte[512];
            // The worker owns err until it completes; this coroutine only reads it afterwards.
            var init = Task.Run(() => wd_init(dest, libDir, err, err.Length));
            while (!init.IsCompleted) yield return null;
            if (init.IsFaulted)
            {
                // DllNotFoundException / EntryPointNotFoundException: the APK lacks libwalldepth.so.
                Status = "native plugin failed to load: " + init.Exception.GetBaseException().Message;
                yield break;
            }
            if (init.Result != 0) { Status = "QNN init failed: " + Decode(err); yield break; }
            _initialised = true;
            if (_disposed) { Shutdown(); yield break; }

            int size = DepthInferenceScheduler.Size;
            if (wd_input_size() != size * size * 3 || wd_output_size() != size * size)
            {
                Status = $"model size mismatch: input {wd_input_size()} B, output {wd_output_size()} values";
                Shutdown();
                yield break;
            }
            wd_describe(_msg, _msg.Length);
            Status = "QNN HTP: " + Decode(_msg);
            _available = true;
        }

        public bool TryBegin(in InferenceRequest request)
        {
            if (!IsAvailable || _busy || request.target == null || request.rgb == null) return false;
            int rc = wd_submit(request.rgb, request.rgb.Length);
            if (rc == 1)
            {
                _pending = request.target;
                _busy = true;
                return true;
            }
            if (rc < 0) Fail("submit");
            return false;
        }

        public bool TryCollect(out InverseDepthImage image)
        {
            image = null;
            if (!_busy) return false;
            var target = _pending;
            int rc = wd_poll(target.values, target.values.Length, out double ms);
            if (rc == 0) return false;
            _busy = false;
            _pending = null;
            if (rc < 0) { Fail("inference"); return false; }
            target.inferenceMilliseconds = ms;
            target.MaskOutsideContent();
            image = target;
            return true;
        }

        void Fail(string stage)
        {
            wd_last_error(_msg, _msg.Length);
            Status = $"QNN {stage} failed: {Decode(_msg)}";
            _available = false;
            Debug.LogError("[QnnDepthInference] " + Status);
        }

        public void Dispose()
        {
            // If init is still running, Setup sees _disposed when it finishes and shuts down then.
            _disposed = true;
            _available = false;
            if (_initialised) Shutdown();
        }

        void Shutdown()
        {
            _initialised = false;
            wd_shutdown();
        }

        static string Decode(byte[] buf)
        {
            int n = Array.IndexOf(buf, (byte)0);
            return Encoding.UTF8.GetString(buf, 0, n < 0 ? buf.Length : n);
        }

        static string NativeLibraryDir()
        {
            using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
            using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
            using (var info = activity.Call<AndroidJavaObject>("getApplicationInfo"))
                return info.Get<string>("nativeLibraryDir");
        }
    }
}
