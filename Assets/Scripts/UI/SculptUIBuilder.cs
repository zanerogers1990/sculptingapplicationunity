using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Sculpting
{
    /// Builds the entire sculpting HUD (Canvas, EventSystem, sliders, toggle, brush buttons,
    /// reset button) purely from code at runtime and wires it to a SculptController.
    /// Avoids hand-authoring prefabs while keeping every control's behavior in one place.
    public class SculptUIBuilder : MonoBehaviour
    {
        public SculptController controller;

        private static readonly Color ActiveColor = new Color(0.25f, 0.55f, 0.95f);
        private static readonly Color InactiveColor = new Color(0.2f, 0.2f, 0.22f);
        // Distinct from ActiveColor since mask-paint mode is orthogonal to brush selection
        // (which brush is "current" still matters for when you exit mask mode) - a different
        // color keeps the two kinds of highlight from reading as the same kind of state.
        private static readonly Color MaskActiveColor = new Color(0.95f, 0.65f, 0.15f);
        private static readonly Color PanelColor = new Color(0.08f, 0.08f, 0.1f, 0.88f);

        // Matches Unity's axis-handle/gizmo convention (X red, Y green, Z blue), and
        // MirrorController's own plane colors.
        private static readonly Color MirrorXColor = new Color(1f, 0.25f, 0.25f);
        private static readonly Color MirrorYColor = new Color(0.35f, 1f, 0.35f);
        private static readonly Color MirrorZColor = new Color(0.3f, 0.55f, 1f);

        private const float ResizeGaugeWidth = 160f;

        private Font _font;
        private Text _positiveToggleLabel;
        private Toggle _positiveToggle;
        private Text _accumulateToggleLabel;
        private Toggle _accumulateToggle;
        private Slider _accumulateStrengthSlider;
        private Image _moveButtonImage;
        private Image _clayButtonImage;
        private Image _smoothButtonImage;
        private Image _creaseButtonImage;
        private Image _damButtonImage;
        private Image _inflateButtonImage;
        private Image _maskButtonImage;
        private bool _lastShownMaskMode;
        private Slider _brushSizeSlider;
        private Text _polyCountLabel;
        private Text _exportStatusLabel;
        private int _lastShownTriCount = -1, _lastShownVertCount = -1;
        private Button _undoButton, _redoButton;
        private BrushType _lastShownBrush = (BrushType)(-1);

        private static readonly BrushAlphaType[] AlphaTypes =
        {
            BrushAlphaType.SoftCircle, BrushAlphaType.Noise, BrushAlphaType.Bumps,
            BrushAlphaType.Ridges, BrushAlphaType.HardSquare
        };
        private readonly Image[] _alphaButtonImages = new Image[AlphaTypes.Length];

        // ZBrush/Blender-style popup gauge shown while SculptController.IsResizingBrush is
        // true, positioned at the screen point where the S-drag started (see Update()).
        private GameObject _resizeGaugeGO;
        private RectTransform _resizeGaugeRect;
        private RectTransform _resizeGaugeFillRect;

        // Start(), not Awake(): BuildUI() reads controller.Mirror.MirrorX, which now resolves
        // through SelectionManager.PrimarySelection (see SculptController.Mirror) instead of a
        // GetComponent on this same GameObject. That needs every SculptableMesh's OnEnable
        // (where it registers itself - see SelectionManager) to have already run, and Unity
        // only guarantees ALL objects' Awake+OnEnable are complete before ANY object's Start -
        // building the UI from Awake() risked racing that registration on scene load.
        private void Start()
        {
            if (controller == null) controller = FindFirstObjectByType<SculptController>();
            _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            EnsureEventSystem();
            BuildUI();
        }

        private void Update()
        {
            if (controller == null || _resizeGaugeGO == null) return;

            bool show = controller.IsResizingBrush;
            if (_resizeGaugeGO.activeSelf != show) _resizeGaugeGO.SetActive(show);

            // Keep the panel's own Brush Size slider in sync while the popup gauge drives
            // BrushRadius directly - SetValueWithoutNotify avoids feeding the change back
            // into controller.BrushRadius through the slider's own onValueChanged.
            if (show)
            {
                _resizeGaugeRect.position = controller.ResizeAnchorScreenPosition + new Vector2(0f, 50f);
                float t01 = Mathf.InverseLerp(SculptController.MinBrushRadius, SculptController.MaxBrushRadius, controller.BrushRadius);
                _resizeGaugeFillRect.sizeDelta = new Vector2(ResizeGaugeWidth * t01, _resizeGaugeFillRect.sizeDelta.y);
            }

            if (_brushSizeSlider != null) _brushSizeSlider.SetValueWithoutNotify(controller.BrushRadius);

            // Brush switches can now come from the keyboard outside of SetBrushType (hotkeys
            // 1-5, and holding Shift to temporarily switch to Smooth), so the highlighted
            // button needs a per-frame sync rather than only refreshing on a UI click - cheap
            // either way (a handful of color assignments), but only-on-change avoids touching
            // five Image components every single frame for no reason.
            if (controller.CurrentBrush != _lastShownBrush || controller.IsMaskPaintMode != _lastShownMaskMode)
            {
                _lastShownBrush = controller.CurrentBrush;
                _lastShownMaskMode = controller.IsMaskPaintMode;
                RefreshBrushButtons();

                // Each brush remembers its own polarity (see SculptController._brushPolarity),
                // so switching brushes can silently change controller.IsPositive out from under
                // this toggle - resync its visual state without re-firing onChange (which would
                // just feed the same value straight back into controller.IsPositive).
                if (_positiveToggle != null)
                {
                    _positiveToggle.SetIsOnWithoutNotify(controller.IsPositive);
                    _positiveToggleLabel.text = controller.IsPositive ? "Positive (Add)" : "Negative (Subtract)";
                }

                // Same per-brush memory for Accumulate (see SculptController._brushAccumulate).
                if (_accumulateToggle != null)
                {
                    _accumulateToggle.SetIsOnWithoutNotify(controller.Accumulate);
                    _accumulateToggleLabel.text = controller.Accumulate ? "Accumulate" : "Accumulate (Off)";
                }

                // Same per-brush memory for Accumulate Strength (see
                // SculptController._accumulateStrengthPerType).
                if (_accumulateStrengthSlider != null)
                    _accumulateStrengthSlider.SetValueWithoutNotify(controller.AccumulateStrength);
            }

            if (_undoButton != null) _undoButton.interactable = controller.CanUndo;
            if (_redoButton != null) _redoButton.interactable = controller.CanRedo;

            if (_polyCountLabel != null)
            {
                int tris = controller.TriangleCount, verts = controller.VertexCount;
                if (tris != _lastShownTriCount || verts != _lastShownVertCount)
                {
                    _lastShownTriCount = tris;
                    _lastShownVertCount = verts;
                    _polyCountLabel.text = "Tris: " + tris.ToString("N0") + " | Verts: " + verts.ToString("N0");
                }
            }
        }

        private static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<EventSystem>() != null) return;
            var go = new GameObject("EventSystem");
            go.AddComponent<EventSystem>();
            go.AddComponent<InputSystemUIInputModule>();
        }

        private void BuildUI()
        {
            var canvasGO = new GameObject("SculptCanvas", typeof(RectTransform));
            canvasGO.transform.SetParent(transform, false);
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Constant pixel size keeps the panel a fixed, predictable size in the top-left
            // corner regardless of the Game view's resolution/aspect - "Scale With Screen
            // Size" could blow the panel up or shrink/shift it unpredictably in an
            // unconventional or narrow docked Game view.
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            canvasGO.AddComponent<GraphicRaycaster>();

            var panel = CreatePanel(canvasGO.transform);
            panel.AddComponent<DraggablePanel>();
            var panelRect = panel.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0, 1);
            panelRect.anchorMax = new Vector2(0, 1);
            panelRect.pivot = new Vector2(0, 1);
            panelRect.anchoredPosition = new Vector2(12, -12);
            panelRect.sizeDelta = new Vector2(270, 0);

            var layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.spacing = 10;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            var fitter = panel.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            CreateLabel(panel.transform, "Sculpting Tools", 20, FontStyle.Bold);
            _polyCountLabel = CreateLabel(panel.transform, "Tris: - | Verts: -", 12, FontStyle.Normal);

            CreateLabel(panel.transform, "Brush Strength", 14, FontStyle.Normal);
            CreateSlider(panel.transform, 0.01f, 1f, controller.BrushStrength, v => controller.BrushStrength = v);

            CreateLabel(panel.transform, "Brush Size", 14, FontStyle.Normal);
            _brushSizeSlider = CreateSlider(panel.transform, SculptController.MinBrushRadius, SculptController.MaxBrushRadius,
                controller.BrushRadius, v => controller.BrushRadius = v);

            _positiveToggle = CreateToggle(panel.transform, "Positive (Add)", controller.IsPositive, v =>
            {
                controller.IsPositive = v;
                _positiveToggleLabel.text = v ? "Positive (Add)" : "Negative (Subtract)";
            }, out _positiveToggleLabel);

            _accumulateToggle = CreateToggle(panel.transform, "Accumulate", controller.Accumulate, v =>
            {
                controller.Accumulate = v;
                _accumulateToggleLabel.text = v ? "Accumulate" : "Accumulate (Off)";
            }, out _accumulateToggleLabel);

            CreateLabel(panel.transform, "Accumulate Strength", 14, FontStyle.Normal);
            _accumulateStrengthSlider = CreateSlider(panel.transform, 0.1f, 3f, controller.AccumulateStrength,
                v => controller.AccumulateStrength = v);

            var brushRow = CreateRow(panel.transform);
            var moveButton = CreateButton(brushRow.transform, "Move", () => SetBrushType(BrushType.Move));
            var clayButton = CreateButton(brushRow.transform, "Clay", () => SetBrushType(BrushType.Clay));
            var smoothButton = CreateButton(brushRow.transform, "Smooth", () => SetBrushType(BrushType.Smooth));
            _moveButtonImage = moveButton.GetComponent<Image>();
            _clayButtonImage = clayButton.GetComponent<Image>();
            _smoothButtonImage = smoothButton.GetComponent<Image>();

            // Second row - the panel is sized for 3 buttons per row (see CreateRow/panel
            // width), so Crease/Dam Standard get their own row rather than squeezing 5 in.
            var brushRow2 = CreateRow(panel.transform);
            var creaseButton = CreateButton(brushRow2.transform, "Crease", () => SetBrushType(BrushType.Crease));
            var damButton = CreateButton(brushRow2.transform, "Dam Std", () => SetBrushType(BrushType.DamStandard));
            var maskButton = CreateButton(brushRow2.transform, "Mask", () => controller.IsMaskPaintMode = !controller.IsMaskPaintMode);
            _creaseButtonImage = creaseButton.GetComponent<Image>();
            _damButtonImage = damButton.GetComponent<Image>();
            _maskButtonImage = maskButton.GetComponent<Image>();

            // Third row - Inflate joins Crease/Dam Standard/Mask's group of "not one of the
            // first three" brushes, same reasoning as brushRow2 above for why it doesn't
            // squeeze into an existing row.
            var brushRow3 = CreateRow(panel.transform);
            var inflateButton = CreateButton(brushRow3.transform, "Inflate", () => SetBrushType(BrushType.Inflate));
            _inflateButtonImage = inflateButton.GetComponent<Image>();
            RefreshBrushButtons();

            // Collapsed by default, same reasoning as the other shaping foldouts below.
            Transform maskFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Masking", false);
            CreateLabel(maskFoldout, "Hardness (Soft <-> Hard)", 12, FontStyle.Normal);
            CreateSlider(maskFoldout, 0f, 1f, controller.MaskHardness, v => controller.MaskHardness = v);
            CreateButton(maskFoldout, "Invert Mask", () => controller.InvertMask());

            // Collapsed by default (see UIFactory.CreateFoldoutSection) - with this section
            // expanded, the top-left panel's height was tall enough to run into the
            // Material panel anchored at the bottom-left corner.
            Transform clayFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Clay Shaping", false);
            CreateLabel(clayFoldout, "Clay Depth", 12, FontStyle.Normal);
            CreateSlider(clayFoldout, 0.1f, 1.5f, controller.ClayHeightFactor, v => controller.ClayHeightFactor = v);

            CreateLabel(clayFoldout, "Tip Shape (Square <-> Round)", 12, FontStyle.Normal);
            CreateSlider(clayFoldout, 0f, 1f, controller.ClayTipRoundness, v => controller.ClayTipRoundness = v);

            // Low = flat-topped strip with a hard rim; high = soft-shouldered pad. See
            // SculptController.clayEdgeSoftness.
            CreateLabel(clayFoldout, "Tip Softness (Flat <-> Domed)", 12, FontStyle.Normal);
            CreateSlider(clayFoldout, 0.05f, 1f, controller.ClayEdgeSoftness, v => controller.ClayEdgeSoftness = v);

            CreateToggle(clayFoldout, "Use Alpha", controller.UseAlpha, v => controller.UseAlpha = v, out _);

            var alphaRow = CreateRow(clayFoldout);
            for (int i = 0; i < AlphaTypes.Length; i++)
            {
                BrushAlphaType type = AlphaTypes[i];
                var alphaButton = CreateAlphaButton(alphaRow.transform, type, () =>
                {
                    controller.AlphaType = type;
                    RefreshAlphaButtons();
                });
                _alphaButtonImages[i] = alphaButton;
            }
            RefreshAlphaButtons();

            CreateLabel(clayFoldout, "Alpha Rotation", 12, FontStyle.Normal);
            CreateSlider(clayFoldout, 0f, 360f, controller.AlphaRotation, v => controller.AlphaRotation = v);
            CreateLabel(clayFoldout, "Alpha Scale", 12, FontStyle.Normal);
            CreateSlider(clayFoldout, 0.3f, 3f, controller.AlphaScale, v => controller.AlphaScale = v);
            CreateToggle(clayFoldout, "Invert Alpha", controller.InvertAlpha, v => controller.InvertAlpha = v, out _);

            // Collapsed by default, same reasoning as "Clay Shaping" above.
            Transform creaseFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Crease Shaping", false);
            CreateLabel(creaseFoldout, "Pinch", 12, FontStyle.Normal);
            CreateSlider(creaseFoldout, 0f, 1f, controller.CreasePinch, v => controller.CreasePinch = v);
            CreateLabel(creaseFoldout, "Depth", 12, FontStyle.Normal);
            CreateSlider(creaseFoldout, 0.05f, 1f, controller.CreaseDepthFactor, v => controller.CreaseDepthFactor = v);
            CreateLabel(creaseFoldout, "Dam Standard Lip Height", 12, FontStyle.Normal);
            CreateSlider(creaseFoldout, 0f, 1f, controller.DamLipHeight, v => controller.DamLipHeight = v);

            // Collapsed by default, same reasoning as "Clay Shaping" above. Both controls are
            // inert without a stylus - CurrentPressure short-circuits to 1 when Pen.current is
            // null - but the section is always built rather than hidden on no-pen, since a
            // tablet can be plugged in after the UI is constructed.
            Transform pressureFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Stylus Pressure", false);
            CreateLabel(pressureFoldout, "Light-Touch Floor", 12, FontStyle.Normal);
            CreateSlider(pressureFoldout, 0f, 0.5f, controller.PressureFloor, v => controller.PressureFloor = v);
            CreateLabel(pressureFoldout, "Curve (Sensitive <-> Gradual)", 12, FontStyle.Normal);
            CreateSlider(pressureFoldout, 0.5f, 3f, controller.PressureCurve, v => controller.PressureCurve = v);

            CreateLabel(panel.transform, "Mirror (Local Axes)", 14, FontStyle.Normal);
            CreateToggle(panel.transform, "Mirror X", controller.Mirror.MirrorX,
                v => controller.Mirror.MirrorX = v, out _, MirrorXColor);
            CreateToggle(panel.transform, "Mirror Y", controller.Mirror.MirrorY,
                v => controller.Mirror.MirrorY = v, out _, MirrorYColor);
            CreateToggle(panel.transform, "Mirror Z", controller.Mirror.MirrorZ,
                v => controller.Mirror.MirrorZ = v, out _, MirrorZColor);
            CreateToggle(panel.transform, "Show Mirror Planes", controller.Mirror.ShowPlanes,
                v => controller.Mirror.ShowPlanes = v, out _);

            CreateToggle(panel.transform, "Wireframe (Scene View)", controller.ShowWireframeGizmo,
                v => controller.ShowWireframeGizmo = v, out _);
            CreateToggle(panel.transform, "Log Ray Hits", controller.LogRayHits,
                v => controller.LogRayHits = v, out _);

            var undoRedoRow = CreateRow(panel.transform);
            _undoButton = CreateButton(undoRedoRow.transform, "Undo (Z)", () => controller.Undo());
            _redoButton = CreateButton(undoRedoRow.transform, "Redo (Shift+Z)", () => controller.Redo());

            CreateButton(panel.transform, "Reset Mesh", () => controller.ResetMesh());

            CreateLabel(panel.transform, "Export", 14, FontStyle.Normal);
            CreateButton(panel.transform, "Export OBJ", () =>
            {
                string path = controller.Export();
                _exportStatusLabel.text = path != null
                    ? "Saved to Desktop/SculptExports/" + System.IO.Path.GetFileName(path)
                    : "Export failed - no mesh yet";
            });
            _exportStatusLabel = CreateLabel(panel.transform, "", 11, FontStyle.Italic);

            CreateLabel(panel.transform, "Remesh Resolution", 14, FontStyle.Normal);
            CreateSlider(panel.transform, 4f, 500f, controller.RemeshResolution,
                v => controller.RemeshResolution = Mathf.RoundToInt(v));
            CreateButton(panel.transform, "Remesh", () => controller.Remesh());

            CreateLabel(panel.transform,
                "Keys: 1 Move  2 Clay  3 Smooth  4 Crease  5 Dam Std  6 Inflate\nM Toggle Mask Paint  R Remesh\nZ Undo  Shift+Z Redo (not Ctrl+Z - that's the Editor's)\nHold S + drag, or Scroll over model: resize brush\nLMB Sculpt/Mask | RMB or Ctrl+LMB Invert/Erase\nAlt+LMB Orbit | MMB Pan | Scroll Zoom | Ctrl+Alt+LMB Drag Zoom",
                11, FontStyle.Italic);

            _resizeGaugeGO = CreateResizeGauge(canvasGO.transform);
        }

        private void SetBrushType(BrushType type)
        {
            controller.CurrentBrush = type;
            RefreshBrushButtons();
        }

        private void RefreshBrushButtons()
        {
            _moveButtonImage.color = controller.CurrentBrush == BrushType.Move ? ActiveColor : InactiveColor;
            _clayButtonImage.color = controller.CurrentBrush == BrushType.Clay ? ActiveColor : InactiveColor;
            _smoothButtonImage.color = controller.CurrentBrush == BrushType.Smooth ? ActiveColor : InactiveColor;
            _creaseButtonImage.color = controller.CurrentBrush == BrushType.Crease ? ActiveColor : InactiveColor;
            _damButtonImage.color = controller.CurrentBrush == BrushType.DamStandard ? ActiveColor : InactiveColor;
            _inflateButtonImage.color = controller.CurrentBrush == BrushType.Inflate ? ActiveColor : InactiveColor;
            _maskButtonImage.color = controller.IsMaskPaintMode ? MaskActiveColor : InactiveColor;
        }

        private void RefreshAlphaButtons()
        {
            for (int i = 0; i < AlphaTypes.Length; i++)
                _alphaButtonImages[i].color = controller.AlphaType == AlphaTypes[i] ? ActiveColor : InactiveColor;
        }

        // Square icon button showing a live preview of a procedurally-generated brush alpha
        // (see BrushAlphaLibrary) instead of a text label - mirrors ZBrush's alpha palette.
        private Image CreateAlphaButton(Transform parent, BrushAlphaType type, Action onClick)
        {
            var go = new GameObject("AlphaButton_" + type, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = InactiveColor;
            go.AddComponent<LayoutElement>().preferredHeight = 34;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick());

            Texture2D preview = BrushAlphaLibrary.Get(type).Preview;
            var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGO.transform.SetParent(go.transform, false);
            var iconRect = iconGO.GetComponent<RectTransform>();
            iconRect.anchorMin = new Vector2(0.12f, 0.12f);
            iconRect.anchorMax = new Vector2(0.88f, 0.88f);
            iconRect.sizeDelta = Vector2.zero;
            iconGO.GetComponent<Image>().sprite = Sprite.Create(preview, new Rect(0, 0, preview.width, preview.height), new Vector2(0.5f, 0.5f));

            return img;
        }

        // ---------------------------------------------------------------- element factories

        private static GameObject CreatePanel(Transform parent)
        {
            var go = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = PanelColor;
            return go;
        }

        private static GameObject CreateRow(Transform parent)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 8;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = 34;
            return go;
        }

        private Text CreateLabel(Transform parent, string text, int fontSize, FontStyle style)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = _font;
            t.fontSize = fontSize;
            t.fontStyle = style;
            t.color = Color.white;
            t.text = text;
            t.alignment = TextAnchor.MiddleLeft;
            int lineCount = 1;
            foreach (char c in text) if (c == '\n') lineCount++;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = lineCount * (fontSize + 4) + 4;
            return t;
        }

        private Slider CreateSlider(Transform parent, float min, float max, float defaultVal, Action<float> onChange)
        {
            var sliderGO = new GameObject("Slider", typeof(RectTransform));
            sliderGO.transform.SetParent(parent, false);
            sliderGO.AddComponent<LayoutElement>().preferredHeight = 20;
            var slider = sliderGO.AddComponent<Slider>();

            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(sliderGO.transform, false);
            var bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.25f);
            bgRect.anchorMax = new Vector2(1, 0.75f);
            bgRect.sizeDelta = Vector2.zero;
            bgGO.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.17f);

            var fillAreaGO = new GameObject("Fill Area", typeof(RectTransform));
            fillAreaGO.transform.SetParent(sliderGO.transform, false);
            var fillAreaRect = fillAreaGO.GetComponent<RectTransform>();
            fillAreaRect.anchorMin = new Vector2(0, 0.25f);
            fillAreaRect.anchorMax = new Vector2(1, 0.75f);
            fillAreaRect.offsetMin = new Vector2(5, 0);
            fillAreaRect.offsetMax = new Vector2(-5, 0);

            var fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(fillAreaGO.transform, false);
            fillGO.GetComponent<Image>().color = new Color(0.3f, 0.6f, 1f);
            var fillRect = fillGO.GetComponent<RectTransform>();
            fillRect.anchorMin = new Vector2(0, 0);
            fillRect.anchorMax = new Vector2(0, 1);
            fillRect.sizeDelta = new Vector2(10, 0);

            var handleAreaGO = new GameObject("Handle Slide Area", typeof(RectTransform));
            handleAreaGO.transform.SetParent(sliderGO.transform, false);
            var handleAreaRect = handleAreaGO.GetComponent<RectTransform>();
            handleAreaRect.anchorMin = new Vector2(0, 0);
            handleAreaRect.anchorMax = new Vector2(1, 1);
            handleAreaRect.offsetMin = new Vector2(10, 0);
            handleAreaRect.offsetMax = new Vector2(-10, 0);

            var handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleGO.transform.SetParent(handleAreaGO.transform, false);
            handleGO.GetComponent<Image>().color = Color.white;
            var handleRect = handleGO.GetComponent<RectTransform>();
            handleRect.sizeDelta = new Vector2(14, 14);

            slider.targetGraphic = handleGO.GetComponent<Image>();
            slider.fillRect = fillRect;
            slider.handleRect = handleRect;
            slider.direction = Slider.Direction.LeftToRight;
            slider.minValue = min;
            slider.maxValue = max;
            slider.value = defaultVal;
            slider.onValueChanged.AddListener(v => onChange(v));

            return slider;
        }

        private Button CreateButton(Transform parent, string label, Action onClick)
        {
            var go = new GameObject("Button_" + label, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = InactiveColor;
            go.AddComponent<LayoutElement>().preferredHeight = 32;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick());

            var textGO = new GameObject("Text", typeof(RectTransform));
            textGO.transform.SetParent(go.transform, false);
            var textRect = textGO.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            var text = textGO.AddComponent<Text>();
            text.font = _font;
            text.fontSize = 14;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = label;

            return btn;
        }

        private Toggle CreateToggle(Transform parent, string label, bool defaultVal, Action<bool> onChange,
            out Text labelText, Color? checkColor = null)
        {
            var go = new GameObject("Toggle_" + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.AddComponent<LayoutElement>().preferredHeight = 24;
            var toggle = go.AddComponent<Toggle>();

            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(go.transform, false);
            var bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.5f);
            bgRect.anchorMax = new Vector2(0, 0.5f);
            bgRect.pivot = new Vector2(0, 0.5f);
            bgRect.sizeDelta = new Vector2(20, 20);
            bgGO.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.17f);

            var checkGO = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
            checkGO.transform.SetParent(bgGO.transform, false);
            var checkRect = checkGO.GetComponent<RectTransform>();
            checkRect.anchorMin = Vector2.zero;
            checkRect.anchorMax = Vector2.one;
            checkRect.sizeDelta = new Vector2(-6, -6);
            checkGO.GetComponent<Image>().color = checkColor ?? new Color(0.3f, 0.6f, 1f);

            toggle.targetGraphic = bgGO.GetComponent<Image>();
            toggle.graphic = checkGO.GetComponent<Image>();
            toggle.isOn = defaultVal;

            var textGO = new GameObject("Label", typeof(RectTransform));
            textGO.transform.SetParent(go.transform, false);
            var textRect = textGO.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0, 0);
            textRect.anchorMax = new Vector2(1, 1);
            textRect.offsetMin = new Vector2(28, 0);
            textRect.offsetMax = Vector2.zero;
            var text = textGO.AddComponent<Text>();
            text.font = _font;
            text.fontSize = 13;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.white;
            text.text = label;
            labelText = text;

            toggle.onValueChanged.AddListener(v => onChange(v));

            return toggle;
        }

        // Small floating track+fill bar, not an interactive Slider - Update() drives its
        // fill width directly from controller.BrushRadius while resizing, and positions it
        // at the screen point the S-drag started from.
        private GameObject CreateResizeGauge(Transform canvasParent)
        {
            var go = new GameObject("BrushResizeGauge", typeof(RectTransform));
            go.transform.SetParent(canvasParent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(ResizeGaugeWidth, 12);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 0f);

            var trackGO = new GameObject("Track", typeof(RectTransform), typeof(Image));
            trackGO.transform.SetParent(go.transform, false);
            var trackRect = trackGO.GetComponent<RectTransform>();
            trackRect.anchorMin = Vector2.zero;
            trackRect.anchorMax = Vector2.one;
            trackRect.sizeDelta = Vector2.zero;
            trackGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);

            var fillGO = new GameObject("Fill", typeof(RectTransform), typeof(Image));
            fillGO.transform.SetParent(go.transform, false);
            _resizeGaugeFillRect = fillGO.GetComponent<RectTransform>();
            _resizeGaugeFillRect.anchorMin = new Vector2(0f, 0f);
            _resizeGaugeFillRect.anchorMax = new Vector2(0f, 1f);
            _resizeGaugeFillRect.pivot = new Vector2(0f, 0.5f);
            _resizeGaugeFillRect.anchoredPosition = Vector2.zero;
            fillGO.GetComponent<Image>().color = new Color(0.2f, 1f, 0.4f);

            _resizeGaugeRect = rect;
            go.SetActive(false);
            return go;
        }
    }
}
