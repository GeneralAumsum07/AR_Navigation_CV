using System.Globalization;
using UnityEngine;
using UnityEngine.UI;
using WallDistance.Core;

namespace WallDistance.AR
{
    /// <summary>
    /// Demo HUD. Builds its own Canvas at runtime so the scene contains no fragile hand-wired UI,
    /// and is purely a consumer of <see cref="WallDistanceService"/> - nothing here feeds back
    /// into measurement.
    ///
    /// Layout (reference 1080x2400): the main view carries only what someone walking a corridor
    /// reads - an alert line that appears only when something is wrong, the crosshair with the
    /// aimed distance under it, and at the bottom the left/right/width line and the nearest wall
    /// with its compass bearing. Everything for testers (stats, inference status, CSV recording,
    /// frame dumps) lives in a drawer behind the "Debug" button, closed by default, so it can
    /// never cover the crosshair again (the old Dump button sat right on top of it).
    ///
    /// Assisted Floor mode has no UI any more (removed at Rachit's request): without a button its
    /// controller is never activated, so automatic detection is the only mode.
    /// </summary>
    public sealed class WallDistanceHud : MonoBehaviour
    {
        public WallDistanceService service;
        public MeasurementCsvRecorder recorder;

        // Main view.
        Text _alertText, _aimedText, _aimedDetail, _sidesText, _sidesHint, _nearestText, _recIndicator;
        Image _crosshair;
        // Debug drawer.
        GameObject _drawer;
        Text _statsText, _recordText, _dumpText;
        WallCompass _compass;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly Color ColorGood = new Color(0.35f, 0.95f, 0.45f);
        static readonly Color ColorEstimate = new Color(1f, 0.85f, 0.3f);
        static readonly Color ColorBad = new Color(1f, 0.4f, 0.35f);
        static readonly Color ColorMuted = new Color(0.8f, 0.8f, 0.8f);
        static readonly Color ColorButton = new Color(0.03f, 0.15f, 0.22f, 0.9f);

        void Awake()
        {
            if (service == null) service = FindAnyObjectByType<WallDistanceService>();
            if (recorder == null) recorder = FindAnyObjectByType<MeasurementCsvRecorder>();
            BuildUi();
            _compass = gameObject.AddComponent<WallCompass>();
        }

        void OnEnable() { if (service != null) service.Updated += OnUpdated; }
        void OnDisable() { if (service != null) service.Updated -= OnUpdated; }

        void OnUpdated(WallDistanceSnapshot s)
        {
            // A recording keeps running with the drawer closed, so its state must stay visible.
            _recIndicator.enabled = recorder != null && recorder.IsRecording;
            if (_drawer.activeSelf) UpdateDrawer();

            // Session-level state first: if AR is not tracking nothing else is meaningful.
            if (service.State != WallDistanceSessionState.Tracking)
            {
                ShowAlert(service.StateDetail, ColorBad);
                _aimedText.text = "—";
                _aimedText.color = ColorMuted;
                _aimedDetail.text = "";
                _crosshair.color = ColorMuted;
                _sidesText.text = ReadingText.Sides(default, default, float.NaN);
                _sidesText.color = ColorMuted;
                _sidesHint.text = "";
                _nearestText.text = "";
                return;
            }

            // Degraded-mode warnings only. The full ML status string is a tester's concern and
            // lives in the drawer; here the user only needs to know the numbers are planes-only.
            string alert = "";
            if (!service.InferenceAvailable) alert = "ML depth not running: ARCore planes only";
            if (!s.depthSupported) alert += (alert.Length > 0 ? "\n" : "") + "Depth API unavailable: plane estimates only";
            ShowAlert(alert, ColorEstimate);

            RenderAimed(s.aimed);

            _sidesText.text = ReadingText.Sides(s.left, s.right, s.corridorWidthMeters);
            _sidesText.color = s.left.isValid && s.right.isValid ? Color.white : ColorMuted;
            _sidesHint.text = ReadingText.SidesHint(s.left, s.right);

            if (s.nearest.isValid)
            {
                _nearestText.text = $"Nearest wall {s.nearest.distanceMeters.ToString("F2", Inv)} m · {_compass.Describe(s.nearest)}";
                _nearestText.color = QualityColor(s.nearest.quality);
            }
            else
            {
                _nearestText.text = "Nearest wall — (" + ReadingText.Failure(s.nearest.failure) + ")";
                _nearestText.color = ColorMuted;
            }
        }

        void ShowAlert(string text, Color color)
        {
            // Hidden rather than blank so its outline box never darkens the camera view.
            _alertText.enabled = !string.IsNullOrEmpty(text);
            _alertText.text = text ?? "";
            _alertText.color = color;
        }

        void RenderAimed(WallReading r)
        {
            if (!r.isValid)
            {
                _aimedText.text = "—";
                _aimedText.color = ColorMuted;
                _aimedDetail.text = ReadingText.Failure(r.failure);
                // A failed depth fit should tell the tester why there is no number; otherwise absent
                // raw support looks indistinguishable from a UI failure.
                if (!string.IsNullOrEmpty(r.qualityReason) && r.qualityReason != r.failure.ToString())
                    _aimedDetail.text += " · " + r.qualityReason;
                _aimedDetail.color = ColorMuted;
                _crosshair.color = ColorMuted;
                return;
            }
            Color c = QualityColor(r.quality);
            _aimedText.text = r.distanceMeters.ToString("F2", Inv) + " m";
            _aimedText.color = c;
            _aimedDetail.text = ReadingText.Quality(r.quality) + " · " + _compass.Describe(r);
            _aimedDetail.color = c;
            _crosshair.color = c;
        }

        void UpdateDrawer()
        {
            var sch = service.scheduler;
            var pipe = service.Pipeline;
            var cs = service.candidateSource;
            // The same three signals the CSV logs (sched_status, infer_status, frames_dropped), so a
            // tester can see a silent stall live instead of only in a recording afterwards.
            _statsText.text =
                $"{(1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f)):F0} fps · {service.UpdateRate:F0} upd/s · " +
                $"ML {(sch != null ? sch.CollectedHz : 0f):F0} Hz {(sch != null ? sch.LastInferenceMs : double.NaN):F0} ms\n" +
                $"{pipe.Map.Tracks.Count} walls · {cs?.TrackedVerticalPlaneCount ?? 0}/{cs?.TotalPlaneCount ?? 0} planes · " +
                $"{pipe.FramesDropped} results dropped\n" +
                $"ML: {service.InferenceStatus}\n" +
                $"Scheduler: {(sch != null ? sch.Status : "none")}";
            if (recorder != null)
                _recordText.text = recorder.IsRecording ? $"■ Stop ({recorder.RowsWritten} rows)" : "● Record CSV";
            if (_dumpText != null && sch != null)
                _dumpText.text = sch.DumpRemaining > 0 ? $"Dumping… {sch.DumpRemaining} left" : "Dump frames (20)";
        }

        void ToggleDrawer()
        {
            _drawer.SetActive(!_drawer.activeSelf);
            if (_drawer.activeSelf) UpdateDrawer();
        }

        static Color QualityColor(QualityLabel q)
        {
            switch (q)
            {
                // Green only where a second, independent check passed or the edge was confirmed.
                case QualityLabel.DepthValidated: return ColorGood;
                case QualityLabel.CrossChecked: return ColorGood;
                case QualityLabel.EdgeConfirmed: return ColorGood;
                case QualityLabel.LearnedEstimate: return ColorEstimate;
                case QualityLabel.PlaneEstimate: return ColorEstimate;
                case QualityLabel.DepthEstimate: return ColorEstimate;
                case QualityLabel.AssistedEstimate: return new Color(0.3f, 0.85f, 1f);
                case QualityLabel.OutOfTestedRange: return ColorEstimate;
                default: return ColorBad;
            }
        }

        // ------------------------------------------------------------------ UI construction

        void BuildUi()
        {
            var canvasGo = new GameObject("WallDistanceCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasGo.transform.SetParent(transform, false);
            var canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1080, 2400);
            scaler.matchWidthOrHeight = 0.5f;
            var root = canvasGo.transform;

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var top = new Vector2(0.5f, 1f);
            var centre = new Vector2(0.5f, 0.5f);
            var bottom = new Vector2(0.5f, 0f);

            // Top row, 110 px down to clear the status bar and the punch-hole camera.
            _recIndicator = MakeText(root, "RecIndicator", font, 34, new Vector2(0f, 1f), new Vector2(120, -110), new Vector2(200, 70));
            _recIndicator.text = "● REC";
            _recIndicator.color = ColorBad;
            _recIndicator.enabled = false;
            MakeButton(root, font, "DebugToggle", new Vector2(1f, 1f), new Vector2(-130, -110), new Vector2(200, 90),
                ToggleDrawer, out var debugLabel);
            debugLabel.text = "Debug";
            // Below the top row so a two-line alert never runs under the Debug button.
            _alertText = MakeText(root, "Alert", font, 36, top, new Vector2(0, -230), new Vector2(1000, 130));
            _alertText.enabled = false;

            // Crosshair at the exact screen centre: the aimed reading measures through this point.
            var chGo = new GameObject("Crosshair", typeof(Image));
            chGo.transform.SetParent(root, false);
            _crosshair = chGo.GetComponent<Image>();
            _crosshair.color = ColorMuted;
            _crosshair.raycastTarget = false;
            var chRt = _crosshair.rectTransform;
            chRt.anchorMin = chRt.anchorMax = centre;
            chRt.sizeDelta = new Vector2(16, 16);
            // Aimed distance just below the crosshair, where the eye already is.
            _aimedText = MakeText(root, "Aimed", font, 64, centre, new Vector2(0, -110), new Vector2(1000, 90));
            _aimedDetail = MakeText(root, "AimedDetail", font, 30, centre, new Vector2(0, -180), new Vector2(1000, 60));

            // Bottom block, above the gesture bar: sides are the corridor reading, so they are largest.
            _sidesText = MakeText(root, "Sides", font, 50, bottom, new Vector2(0, 420), new Vector2(1040, 80));
            _sidesHint = MakeText(root, "SidesHint", font, 30, bottom, new Vector2(0, 355), new Vector2(1000, 50));
            _sidesHint.color = ColorMuted;
            _nearestText = MakeText(root, "Nearest", font, 34, bottom, new Vector2(0, 250), new Vector2(1000, 100));

            BuildDrawer(root, font);

            // An EventSystem is required for the buttons; create one only if the scene lacks it.
            if (FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                var es = new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem));
                es.transform.SetParent(transform, false);
#if ENABLE_INPUT_SYSTEM
                es.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
#else
                es.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
#endif
            }
        }

        void BuildDrawer(Transform root, Font font)
        {
            // Spans y 310-850 from the top: below the alert line (165-295), so the two never
            // overlap, and well clear of the centre crosshair (y 1200).
            _drawer = new GameObject("DebugDrawer", typeof(Image));
            _drawer.transform.SetParent(root, false);
            _drawer.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.7f);
            var rt = _drawer.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 1f);
            rt.anchoredPosition = new Vector2(0, -580);
            rt.sizeDelta = new Vector2(1020, 540);
            var panel = _drawer.transform;

            _statsText = MakeText(panel, "Stats", font, 28, new Vector2(0.5f, 1f), new Vector2(0, -130), new Vector2(970, 230));
            _statsText.alignment = TextAnchor.UpperLeft;
            _statsText.color = ColorMuted;

            // Record is available in every build (the field protocol needs it); frame dumps only
            // in development builds, so field users never fill their storage with them.
            MakeButton(panel, font, "RecordButton", new Vector2(0.5f, 0f), new Vector2(-250, 90), new Vector2(470, 110),
                () => recorder?.Toggle(), out _recordText);
            _recordText.text = "● Record CSV";
            if (Debug.isDebugBuild)
            {
                MakeButton(panel, font, "Dump", new Vector2(0.5f, 0f), new Vector2(250, 90), new Vector2(470, 110),
                    () => { if (service.scheduler != null) service.scheduler.RequestDump(20); }, out _dumpText);
                _dumpText.text = "Dump frames (20)";
            }
            _drawer.SetActive(false);
        }

        static Text MakeText(Transform parent, string name, Font font, int size, Vector2 anchor, Vector2 pos, Vector2 sizeDelta)
        {
            var go = new GameObject(name, typeof(Text), typeof(Outline));
            go.transform.SetParent(parent, false);
            var t = go.GetComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Wrap;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.color = Color.white;
            // Labels must never swallow taps meant for the buttons beneath or around them.
            t.raycastTarget = false;
            go.GetComponent<Outline>().effectColor = new Color(0, 0, 0, 0.9f);
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = sizeDelta;
            return t;
        }

        static Button MakeButton(Transform parent, Font font, string name, Vector2 anchor, Vector2 pos, Vector2 size,
            UnityEngine.Events.UnityAction action, out Text label)
        {
            var go = new GameObject(name, typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = ColorButton;
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            var button = go.GetComponent<Button>();
            button.onClick.AddListener(action);
            label = MakeText(go.transform, "Label", font, 32, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            return button;
        }
    }
}
