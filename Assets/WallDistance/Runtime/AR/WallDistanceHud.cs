using UnityEngine;
using UnityEngine.UI;
using WallDistance.Core;

namespace WallDistance.AR
{
    /// <summary>
    /// Demo HUD: both distances, quality states, crosshair, and a record toggle. Builds its own
    /// Canvas at runtime so the scene contains no fragile hand-wired UI. Purely a consumer of
    /// <see cref="WallDistanceService"/> - nothing here feeds back into measurement.
    /// </summary>
    public sealed class WallDistanceHud : MonoBehaviour
    {
        public WallDistanceService service;
        public MeasurementCsvRecorder recorder;

        Text _aimedText, _nearestText, _stateText, _statsText, _recordText;
        Image _crosshair;
        Button _recordButton;
        Text _instruction, _modeText, _captureText, _dumpText;
        Button _captureButton;
        WallCompass _compass;

        static readonly Color ColorGood = new Color(0.35f, 0.95f, 0.45f);
        static readonly Color ColorEstimate = new Color(1f, 0.85f, 0.3f);
        static readonly Color ColorBad = new Color(1f, 0.4f, 0.35f);
        static readonly Color ColorMuted = new Color(0.8f, 0.8f, 0.8f);

        void Awake()
        {
            if (service == null) service = FindAnyObjectByType<WallDistanceService>();
            if (recorder == null) recorder = FindAnyObjectByType<MeasurementCsvRecorder>();
            BuildUi();
            _compass=gameObject.AddComponent<WallCompass>();
        }

        void OnEnable() { if (service != null) service.Updated += OnUpdated; }
        void OnDisable() { if (service != null) service.Updated -= OnUpdated; }

        void OnUpdated(WallDistanceSnapshot s)
        {
            if (service.Assisted != null)
            {
                _instruction.text = service.Assisted.Instruction;
                _modeText.text = service.Assisted.Active ? "Switch to Automatic" : "Use Assisted Floor";
                _captureButton.gameObject.SetActive(service.Assisted.Active && !service.Assisted.HasWall);
                _captureButton.interactable = service.Assisted.CanCapture;
                _captureText.text = service.Assisted.CaptureLabel;
            }
            // Session-level state first: if AR is not tracking nothing else is meaningful.
            if (service.State != WallDistanceSessionState.Tracking)
            {
                _stateText.text = service.StateDetail;
                _stateText.color = ColorBad;
                _aimedText.text = "Aimed wall: —";
                _nearestText.text = "Nearest observed wall: —";
                _aimedText.color = _nearestText.color = ColorMuted;
                _crosshair.color = ColorMuted;
            }
            else
            {
                _stateText.text = s.depthSupported ? "" : "Depth API unavailable on this device: plane estimates only";
                _stateText.color = ColorEstimate;
                Render(_aimedText, "Aimed wall", s.aimed);
                Render(_nearestText, "Nearest observed wall", s.nearest);
                if (s.aimed.isValid) _aimedText.text += "\n"+_compass.Describe(s.aimed);
                if (s.nearest.isValid) _nearestText.text += "\n"+_compass.Describe(s.nearest);
                if (!service.Assisted.Active) _instruction.text="Scan slowly. Green: aimed wall · Cyan: closest detected\nDistances and closest ranking are approximate";
                _crosshair.color = s.aimed.isValid ? QualityColor(s.aimed.quality) : ColorMuted;
            }

            _statsText.text = $"{(1f / Mathf.Max(Time.unscaledDeltaTime, 1e-4f)):F0} fps · {service.UpdateRate:F0} upd/s · " +
                              $"{service.candidateSource?.TrackedVerticalPlaneCount ?? 0}/{service.candidateSource?.TotalPlaneCount ?? 0} walls/planes · max {service.candidateSource?.LargestVerticalAreaSqM ?? 0f:F2} m²";
            if (recorder != null)
                _recordText.text = recorder.IsRecording ? $"■ Stop ({recorder.RowsWritten} rows)" : "● Record CSV";
            if (_dumpText != null && service.scheduler != null)
                _dumpText.text = service.scheduler.DumpRemaining > 0 ? $"Dumping… {service.scheduler.DumpRemaining} left" : "Dump frames (20)";
        }

        static void Render(Text t, string label, WallReading r)
        {
            if (!r.isValid)
            {
                t.text = $"{label}: — ({ReadingText.Failure(r.failure)})";
                // A failed depth fit should tell the tester why there is no number.
                // Otherwise absent raw support looks indistinguishable from a UI failure.
                if (r.kind == MeasurementKind.Aimed && !string.IsNullOrEmpty(r.qualityReason)
                    && r.qualityReason != r.failure.ToString())
                    t.text += "\n" + r.qualityReason;
                t.color = ColorMuted;
                return;
            }
            t.text = $"{label}: {r.distanceMeters:F2} m  [{ReadingText.Quality(r.quality)}]";
            t.color = QualityColor(r.quality);
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
                case QualityLabel.AssistedEstimate: return new Color(0.3f,0.85f,1f);
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

            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            _stateText = MakeText(canvasGo.transform, "State", font, 40, new Vector2(0.5f, 1f), new Vector2(0, -120), new Vector2(1000, 120));
            _aimedText = MakeText(canvasGo.transform, "Aimed", font, 48, new Vector2(0.5f, 0f), new Vector2(0, 520), new Vector2(1000, 80));
            _nearestText = MakeText(canvasGo.transform, "Nearest", font, 48, new Vector2(0.5f, 0f), new Vector2(0, 430), new Vector2(1000, 80));
            _statsText = MakeText(canvasGo.transform, "Stats", font, 30, new Vector2(0.5f, 0f), new Vector2(0, 360), new Vector2(1000, 50));
            _statsText.color = ColorMuted;
            _instruction = MakeText(canvasGo.transform,"Instructions",font,36,new Vector2(0.5f,1f),
                new Vector2(0,-270),new Vector2(960,170));
            _aimedText.rectTransform.anchoredPosition = new Vector2(0,760);
            _aimedText.fontSize = 38;
            _nearestText.rectTransform.anchoredPosition = new Vector2(0,610);
            _nearestText.fontSize = 36;
            _statsText.rectTransform.anchoredPosition = new Vector2(0,530);
            MakeButton(canvasGo.transform,font,"Mode",new Vector2(-250,360),new Vector2(470,100),
                () => service.Assisted.ToggleMode(),out _modeText);
            MakeButton(canvasGo.transform,font,"Reselect",new Vector2(250,360),new Vector2(470,100),
                () => service.Assisted.ClearSelection(),out var resetLabel);
            resetLabel.text = "Reselect wall";
            _captureButton = MakeButton(canvasGo.transform,font,"CaptureBase",new Vector2(0,1030),new Vector2(800,110),
                () => service.Assisted.Capture(),out _captureText);

            // Crosshair: a small ring drawn as a square outline is good enough for a demo.
            var chGo = new GameObject("Crosshair", typeof(Image));
            chGo.transform.SetParent(canvasGo.transform, false);
            _crosshair = chGo.GetComponent<Image>();
            _crosshair.color = ColorMuted;
            var chRt = _crosshair.rectTransform;
            chRt.anchorMin = chRt.anchorMax = new Vector2(0.5f, 0.5f);
            chRt.sizeDelta = new Vector2(12, 12);

            var btnGo = new GameObject("RecordButton", typeof(Image), typeof(Button));
            btnGo.transform.SetParent(canvasGo.transform, false);
            btnGo.GetComponent<Image>().color = new Color(0, 0, 0, 0.55f);
            var btnRt = btnGo.GetComponent<RectTransform>();
            btnRt.anchorMin = btnRt.anchorMax = new Vector2(0.5f, 0f);
            btnRt.anchoredPosition = new Vector2(0, 200);
            btnRt.sizeDelta = new Vector2(520, 110);
            _recordButton = btnGo.GetComponent<Button>();
            _recordButton.onClick.AddListener(() => recorder?.Toggle());
            _recordText = MakeText(btnGo.transform, "Label", font, 40, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(520, 110));
            _recordText.text = "● Record CSV";

            // Phase 1 data capture (spec §7 debug toggle). Development builds only, so field
            // users never fill their storage with dumps.
            if (Debug.isDebugBuild)
            {
                MakeButton(canvasGo.transform, font, "Dump", new Vector2(0, 1160), new Vector2(600, 100),
                    () => { if (service.scheduler != null) service.scheduler.RequestDump(20); }, out _dumpText);
                _dumpText.text = "Dump frames (20)";
            }

            // An EventSystem is required for the button; create one only if the scene lacks it.
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
            go.GetComponent<Outline>().effectColor = new Color(0, 0, 0, 0.9f);
            var rt = t.rectTransform;
            rt.anchorMin = rt.anchorMax = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = sizeDelta;
            return t;
        }

        static Button MakeButton(Transform parent, Font font, string name, Vector2 pos, Vector2 size,
            UnityEngine.Events.UnityAction action, out Text label)
        {
            var go = new GameObject(name,typeof(Image),typeof(Button));
            go.transform.SetParent(parent,false);
            go.GetComponent<Image>().color = new Color(0.03f,0.15f,0.22f,0.9f);
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin=rt.anchorMax=new Vector2(0.5f,0); rt.anchoredPosition=pos; rt.sizeDelta=size;
            var button=go.GetComponent<Button>(); button.onClick.AddListener(action);
            label=MakeText(go.transform,"Label",font,32,new Vector2(0.5f,0.5f),Vector2.zero,size);
            label.raycastTarget=false;
            return button;
        }
    }
}
