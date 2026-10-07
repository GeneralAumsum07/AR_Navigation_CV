using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using WallDistance.Core;

namespace WallDistance.AR
{
    /// <summary>
    /// Writes one CSV row per measurement update while recording. Each file starts with a
    /// header block that dumps the <see cref="MeasurementConfig"/> thresholds so a benchmark
    /// session is reproducible. Files land in Application.persistentDataPath/WallDistanceLogs
    /// (on Android: /sdcard/Android/data/&lt;package&gt;/files/WallDistanceLogs, pull with adb).
    ///
    /// The reference distance is whatever the tester typed in (metres) - it is stored on every
    /// row so a trial can be sliced later without matching timestamps by hand.
    /// </summary>
    public sealed class MeasurementCsvRecorder : MonoBehaviour
    {
        public WallDistanceService service;
        [Tooltip("Manually entered tape-measure distance (metres) for the current trial. Set via SetReferenceDistance().")]
        public float referenceDistanceMeters = float.NaN;
        [Tooltip("Free-text condition tag, e.g. 'painted-1.5m-30deg-trial2'.")]
        public string conditionTag = "";

        StreamWriter _writer;
        readonly StringBuilder _sb = new StringBuilder(512);
        public bool IsRecording => _writer != null;
        public int RowsWritten { get; private set; }
        public string CurrentFilePath { get; private set; }

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        void Awake()
        {
            if (service == null) service = FindAnyObjectByType<WallDistanceService>();
        }

        void OnEnable() { if (service != null) service.Updated += OnUpdated; }
        void OnDisable()
        {
            if (service != null) service.Updated -= OnUpdated;
            Stop();
        }

        public void SetReferenceDistance(float meters) => referenceDistanceMeters = meters;

        public void Toggle()
        {
            if (IsRecording) Stop(); else StartRecording();
        }

        // Not named Start: Unity calls any MonoBehaviour method named Start on launch, which
        // used to begin a recording every time the app opened (and made the HUD's Record
        // button stop it instead). RecorderLifecycleTests guards against the name returning.
        public void StartRecording()
        {
            if (IsRecording) return;
            string dir = Path.Combine(Application.persistentDataPath, "WallDistanceLogs");
            Directory.CreateDirectory(dir);
            CurrentFilePath = Path.Combine(dir, $"walldist_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            _writer = new StreamWriter(CurrentFilePath, false, Encoding.UTF8);
            RowsWritten = 0;
            WriteHeader();
        }

        public void Stop()
        {
            if (_writer == null) return;
            _writer.Flush();
            _writer.Dispose();
            _writer = null;
        }

        void WriteHeader()
        {
            var c = service.config;
            _writer.WriteLine("# WallDistance benchmark log; measurement revision=" + CsvSchema.Revision);
            // Spec §6: an unavailable ML backend is noted once here, not stamped on every reading.
            _writer.WriteLine("# inference=" + service.InferenceStatus.Replace('\n', ' '));
            _writer.WriteLine($"# device={SystemInfo.deviceModel} os={SystemInfo.operatingSystem} unity={Application.unityVersion}");
            _writer.WriteLine($"# condition={conditionTag}");
            _writer.WriteLine("# config: " + JsonUtility.ToJson(c));
            _writer.WriteLine(string.Join(",",
                "t", "session", "arState", "trackingState", "fps", "updRate", "depthSupported", "depthAgeS", "depthValidFrac", "depthMeanConf", "denseValidFrac", "depthFormat", "rawValues", "denseValues", "planesTotal", "planesVerticalTracked", "largestVerticalArea_m2", "reference_m",
                "aimed_valid", "aimed_raw_m", "aimed_filtered_m", "aimed_source", "aimed_quality", "aimed_failure", "aimed_candidate",
                "aimed_depthResidual_m", "aimed_depthInlier", "aimed_reason",
                "nearest_valid", "nearest_raw_m", "nearest_filtered_m", "nearest_source", "nearest_quality", "nearest_failure", "nearest_candidate",
                "nearest_depthResidual_m", "nearest_depthInlier",
                "cam_x", "cam_y", "cam_z", "cam_qx", "cam_qy", "cam_qz", "cam_qw",
                "aimed_pt_x", "aimed_pt_y", "aimed_pt_z", "aimed_n_x", "aimed_n_y", "aimed_n_z",
                "rawProviderTimestampS", "denseProviderTimestampS", "denseAgeS",
                "confidenceInfo", "sensorPoseInfo", "cameraTimestampS", "confidenceTimestampS", "mode", "depthError")
                + "," + string.Join(",", CsvSchema.AppendedColumns()));
        }

        void OnUpdated(WallDistanceSnapshot s)
        {
            if (_writer == null) return;
            _sb.Clear();
            var depth = service.depthSource != null ? service.depthSource.Latest : null;
            double depthAge = depth != null ? s.timestamp - depth.timestamp : double.NaN;

            Append(s.timestamp); Append(s.aimed.sessionId ?? service.SessionId);
            Append(UnityEngine.XR.ARFoundation.ARSession.state.ToString());
            Append(service.State.ToString());
            Append(1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f));
            Append(service.UpdateRate);
            Append(s.depthSupported ? 1 : 0);
            Append(depthAge);
            Append(service.depthSource != null ? service.depthSource.ValidDepthFraction : float.NaN);
            Append(service.depthSource != null ? service.depthSource.MeanConfidence : float.NaN);
            Append(service.depthSource != null ? service.depthSource.DenseValidFraction : float.NaN);
            Append(service.depthSource != null ? service.depthSource.DepthFormatInfo : "");
            Append(service.depthSource != null ? "\"" + service.depthSource.RawValueInfo + "\"" : "");
            Append(service.depthSource != null ? "\"" + service.depthSource.DenseValueInfo + "\"" : "");
            // Plane-detection diagnostics: separates "ARCore found nothing" from "found a wall we rejected".
            var cs = service.candidateSource;
            Append(cs != null ? cs.TotalPlaneCount : -1);
            Append(cs != null ? cs.TrackedVerticalPlaneCount : -1);
            Append(cs != null ? cs.LargestVerticalAreaSqM : float.NaN);
            Append(referenceDistanceMeters);

            AppendReading(s.aimed, true);
            AppendReading(s.nearest, false);

            var p = s.aimed.cameraPose;
            Append(p.position.x); Append(p.position.y); Append(p.position.z);
            Append(p.rotation.x); Append(p.rotation.y); Append(p.rotation.z); Append(p.rotation.w);
            Append(s.aimed.surfacePoint.x); Append(s.aimed.surfacePoint.y); Append(s.aimed.surfacePoint.z);
            Append(s.aimed.surfaceNormal.x); Append(s.aimed.surfaceNormal.y); Append(s.aimed.surfaceNormal.z);
            var dense = service.depthSource != null ? service.depthSource.LatestDense : null;
            Append(depth != null ? depth.providerTimestamp : double.NaN);
            Append(dense != null ? dense.providerTimestamp : double.NaN);
            Append(dense != null ? s.timestamp - dense.timestamp : double.NaN);
            Append(Quote(service.depthSource != null ? service.depthSource.ConfidenceInfo : ""));
            Append(Quote(service.depthSource != null ? service.depthSource.SensorPoseInfo : ""));
            Append(service.depthSource != null ? service.depthSource.CameraTimestamp : double.NaN);
            Append(service.depthSource != null ? service.depthSource.ConfidenceTimestamp : double.NaN);
            Append(service.Assisted.Active ? "AssistedFloor" : "Automatic");
            Append(Quote(service.depthSource != null ? service.depthSource.LastError : ""));

            // --- learned detection (CsvSchema.LearnedColumns, same order) ---
            var sch = service.scheduler;
            var pipe = service.Pipeline;
            var al = pipe.LastAlignment;
            Append(sch != null ? sch.LastInferenceMs : double.NaN);
            Append(sch != null ? sch.CollectedHz : float.NaN);
            Append(al != null && al.success ? al.s : float.NaN);
            Append(al != null && al.success ? al.t : float.NaN);
            Append(al != null ? al.residual : float.NaN);
            Append(al != null ? al.inliers : -1);
            Append(pipe.Map.Tracks.Count);
            Append(Quote(s.aimed.sourceChain));
            Append(pipe.LastEdgeSnapFraction);
            Append(service.floorSource != null && service.floorSource.HasFloor
                ? service.floorSource.CurrentPlane.HeightAbove(s.aimed.cameraPose.position) : float.NaN);
            Append(ThermalStatus.Current);
            Append(pipe.LastDetectLatencyMs);
            Append(sch != null ? sch.LastPrepareMs : double.NaN);
            // Status strings are free text (the QNN description contains commas), so they are quoted.
            // sched_status is overwritten with "running" on the next successful submit, so a
            // refusal shows only in the rows sampled while it lasted; a run of such rows is the signal.
            Append(Quote(sch != null ? sch.Status : "no scheduler"));
            Append(Quote(service.InferenceStatus.Replace('\n', ' ')));
            Append(pipe.FramesDropped);
            Append(service.PoseJumps);
            Append(service.floorSource != null ? service.floorSource.Selector.Jumps : -1);
            Append(service.MapClears);
            Append(pipe.WallsCarved);

            AppendReading(s.left, true);
            AppendReading(s.right, true);
            Append(s.corridorWidthMeters, last: true);

            _writer.WriteLine(_sb.ToString());
            RowsWritten++;
            if (RowsWritten % 60 == 0) _writer.Flush();
        }

        void AppendReading(WallReading r, bool withReason)
        {
            Append(r.isValid ? 1 : 0);
            Append(r.rawDistanceMeters);
            Append(r.distanceMeters);
            Append(r.source.ToString());
            Append(r.quality.ToString());
            Append(r.failure.ToString());
            Append(r.candidateId ?? "");
            Append(r.depthResidualMeters);
            Append(r.depthInlierFraction);
            if (withReason) Append(Quote(r.qualityReason));
        }

        void Append(double v, bool last = false) { _sb.Append(double.IsNaN(v) ? "" : v.ToString("R", Inv)); if (!last) _sb.Append(','); }
        void Append(float v, bool last = false) { _sb.Append(float.IsNaN(v) ? "" : v.ToString("R", Inv)); if (!last) _sb.Append(','); }
        void Append(int v, bool last = false) { _sb.Append(v.ToString(Inv)); if (!last) _sb.Append(','); }
        void Append(string v, bool last = false) { _sb.Append(v); if (!last) _sb.Append(','); }

        static string Quote(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }
    }
}
