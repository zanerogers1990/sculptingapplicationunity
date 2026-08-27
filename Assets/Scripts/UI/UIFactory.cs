using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sculpting
{
    /// Shared uGUI element factories for the panels built after SculptUIBuilder (Lighting,
    /// Post-Processing, Material) so each one doesn't re-implement the same slider/toggle/
    /// button boilerplate. Mirrors SculptUIBuilder's original look (same panel color, same
    /// constant-pixel-size Canvas approach) but is corner-parameterized so multiple panels
    /// can share the screen without overlapping.
    public static class UIFactory
    {
        public static readonly Color PanelColor = new Color(0.08f, 0.08f, 0.1f, 0.88f);
        public static readonly Color HeaderColor = new Color(0.16f, 0.16f, 0.2f, 0.95f);
        public static readonly Color ActiveColor = new Color(0.25f, 0.55f, 0.95f);
        public static readonly Color InactiveColor = new Color(0.2f, 0.2f, 0.22f);

        private static Font _font;
        public static Font Font => _font != null ? _font : (_font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

        /// Builds a standalone Canvas anchored to one corner of the screen with a padded,
        /// auto-sizing vertical panel inside it - the same structural pattern SculptUIBuilder
        /// uses for its own top-left panel, parameterized so callers can pick a different
        /// corner and horizontal-flow direction.
        ///
        /// The Canvas is created at the SCENE ROOT, deliberately not parented under the builder
        /// that asked for it. It's ScreenSpaceOverlay, so parenting never affected layout -
        /// but it did affect survival. This app runs inside the Unity Editor during
        /// development, where the Editor's own global Ctrl+Z can fire in Play mode (which is
        /// already why sculpt undo is bound to a bare Z - see SculptController.
        /// HandleUndoRedoKeys). An Editor undo that reverts a scene object takes its children
        /// with it, and a runtime-created child was never registered with the Editor's undo
        /// system, so Unity destroys it and logs "child GameObject ... was not registered into
        /// the undo system and became dangling during an undo operation". The panel simply
        /// vanished for the rest of the session - which is exactly how the Save/Load panel
        /// disappeared mid-session, taking saving and loading with it. A root object has no
        /// parent whose reversion can drag it down.
        public static Transform CreatePanelCanvas(string name, Vector2 anchor, Vector2 offset, float width)
        {
            DestroyStaleCanvas(name);
            var canvasGO = new GameObject(name, typeof(RectTransform));
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            canvasGO.AddComponent<GraphicRaycaster>();

            var panelGO = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGO.transform.SetParent(canvasGO.transform, false);
            panelGO.GetComponent<Image>().color = PanelColor;
            panelGO.AddComponent<DraggablePanel>();

            var rect = panelGO.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(width, 0);

            var layout = panelGO.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(12, 12, 12, 12);
            layout.spacing = 8;
            layout.childAlignment = TextAnchor.UpperLeft;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandHeight = false;
            layout.childForceExpandWidth = true;
            var fitter = panelGO.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            return panelGO.transform;
        }

        /// Every panel canvas this factory builds is created at the scene ROOT under a fixed
        /// name (see CreatePanelCanvas's remarks on why - Editor-undo survival). That means a
        /// rebuild (SaveLoadUIBuilder/SceneGraphUIBuilder's "Replace scene" flow re-running
        /// every panel's Start via SendMessage) used to leave the OLD root object behind as an
        /// orphan, since nothing ever destroyed it before the same-named replacement was
        /// created - two stacked, overlapping copies of the same panel. Called at the top of
        /// both panel-canvas factories so every rebuild is idempotent by construction rather
        /// than relying on each caller to track and destroy its own previous canvas.
        private static void DestroyStaleCanvas(string name)
        {
            GameObject stale = GameObject.Find(name);
            if (stale != null) UnityEngine.Object.DestroyImmediate(stale);
        }

        /// Same as CreatePanelCanvas, but the panel's content sits behind a scrollbar/viewport
        /// instead of growing the panel to whatever height its content needs. The panel still
        /// auto-sizes to its content below maxHeight (so a short panel isn't left with dead
        /// space) - it only starts scrolling once content would otherwise run past maxHeight.
        /// Returns the scrolling CONTENT transform, so callers build into it exactly like
        /// CreatePanelCanvas's return value - the scrolling machinery is invisible to them.
        public static Transform CreateScrollingPanelCanvas(string name, Vector2 anchor, Vector2 offset, float width, float maxHeight)
        {
            DestroyStaleCanvas(name);
            var canvasGO = new GameObject(name, typeof(RectTransform));
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            canvasGO.AddComponent<GraphicRaycaster>();

            var panelGO = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGO.transform.SetParent(canvasGO.transform, false);
            panelGO.GetComponent<Image>().color = PanelColor;
            panelGO.AddComponent<DraggablePanel>();

            var rect = panelGO.GetComponent<RectTransform>();
            rect.anchorMin = anchor;
            rect.anchorMax = anchor;
            rect.pivot = anchor;
            rect.anchoredPosition = offset;
            rect.sizeDelta = new Vector2(width, 0f); // height is driven by ScrollPanelHeightController below

            return AddScrollingContent(rect, maxHeight, new RectOffset(12, 12, 12, 12), 8f);
        }

        /// Wraps an already-positioned, already-sized panel RectTransform (background Image +
        /// DraggablePanel already attached, as every panel in this project has) with a
        /// Viewport/Content/Scrollbar/ScrollRect, and a ScrollPanelHeightController that keeps
        /// the panel's own height matched to its content up to maxHeight. Split out from
        /// CreateScrollingPanelCanvas so SculptUIBuilder - which builds its own panel by hand
        /// rather than going through CreatePanelCanvas - can add the same scrolling behavior to
        /// its existing panel instead of rebuilding it through this factory.
        public static Transform AddScrollingContent(RectTransform panelRect, float maxHeight, RectOffset padding, float spacing)
        {
            GameObject panelGO = panelRect.gameObject;

            var viewportGO = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            viewportGO.transform.SetParent(panelRect, false);
            var vpRect = viewportGO.GetComponent<RectTransform>();
            vpRect.anchorMin = Vector2.zero;
            vpRect.anchorMax = Vector2.one;
            vpRect.pivot = new Vector2(0f, 1f);
            vpRect.offsetMin = Vector2.zero;
            vpRect.offsetMax = new Vector2(-10f, 0f); // leaves room for the scrollbar strip on the right

            var contentGO = new GameObject("Content", typeof(RectTransform));
            contentGO.transform.SetParent(viewportGO.transform, false);
            var contentRect = contentGO.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0f, 1f);
            contentRect.anchoredPosition = Vector2.zero;

            var vlg = contentGO.AddComponent<VerticalLayoutGroup>();
            vlg.padding = padding;
            vlg.spacing = spacing;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            var fitter = contentGO.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

            var scrollbarGO = new GameObject("Scrollbar", typeof(RectTransform), typeof(Image), typeof(Scrollbar));
            scrollbarGO.transform.SetParent(panelRect, false);
            var sbRect = scrollbarGO.GetComponent<RectTransform>();
            sbRect.anchorMin = new Vector2(1f, 0f);
            sbRect.anchorMax = new Vector2(1f, 1f);
            sbRect.pivot = new Vector2(1f, 1f);
            sbRect.offsetMin = new Vector2(-8f, 2f);
            sbRect.offsetMax = new Vector2(-2f, -2f);
            scrollbarGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);
            var scrollbar = scrollbarGO.GetComponent<Scrollbar>();
            scrollbar.direction = Scrollbar.Direction.BottomToTop;

            var slideAreaGO = new GameObject("Sliding Area", typeof(RectTransform));
            slideAreaGO.transform.SetParent(scrollbarGO.transform, false);
            var slideAreaRect = slideAreaGO.GetComponent<RectTransform>();
            slideAreaRect.anchorMin = Vector2.zero;
            slideAreaRect.anchorMax = Vector2.one;
            slideAreaRect.offsetMin = new Vector2(1f, 1f);
            slideAreaRect.offsetMax = new Vector2(-1f, -1f);

            var handleGO = new GameObject("Handle", typeof(RectTransform), typeof(Image));
            handleGO.transform.SetParent(slideAreaGO.transform, false);
            handleGO.GetComponent<Image>().color = new Color(0.45f, 0.45f, 0.5f, 0.9f);
            var handleRect = handleGO.GetComponent<RectTransform>();
            handleRect.anchorMin = Vector2.zero;
            handleRect.anchorMax = Vector2.one;
            handleRect.sizeDelta = Vector2.zero;

            scrollbar.targetGraphic = handleGO.GetComponent<Image>();
            scrollbar.handleRect = handleRect;

            var scrollRect = panelGO.AddComponent<ScrollRect>();
            scrollRect.content = contentRect;
            scrollRect.viewport = vpRect;
            scrollRect.horizontal = false;
            scrollRect.vertical = true;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.verticalScrollbar = scrollbar;
            scrollRect.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHideAndExpandViewport;
            scrollRect.scrollSensitivity = 24f;

            var sizer = panelGO.AddComponent<ScrollPanelHeightController>();
            sizer.PanelRect = panelRect;
            sizer.ContentRect = contentRect;
            sizer.MaxHeight = maxHeight;

            return contentGO.transform;
        }

        /// Keeps a scrolling panel's outer height matched to its content's natural size, capped
        /// at MaxHeight - the scrolling equivalent of the plain ContentSizeFitter every other
        /// panel puts directly on itself, which can't cap. Foldouts opening/closing and lists
        /// like the scene object list rebuilding change content height continuously after the
        /// panel is first built, so this re-measures every frame rather than once at
        /// construction - the same cheap per-frame poll idiom SculptUIBuilder/
        /// SceneGraphUIBuilder already use for their own refresh checks.
        private sealed class ScrollPanelHeightController : MonoBehaviour
        {
            public RectTransform PanelRect;
            public RectTransform ContentRect;
            public float MaxHeight;

            private void LateUpdate()
            {
                if (PanelRect == null || ContentRect == null) return;
                LayoutRebuilder.ForceRebuildLayoutImmediate(ContentRect);
                float target = Mathf.Min(ContentRect.rect.height, MaxHeight);
                if (Mathf.Abs(PanelRect.sizeDelta.y - target) > 0.5f)
                    PanelRect.sizeDelta = new Vector2(PanelRect.sizeDelta.x, target);
            }
        }

        public static Text CreateLabel(Transform parent, string text, int fontSize = 14, FontStyle style = FontStyle.Normal)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = go.AddComponent<Text>();
            t.font = Font;
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

        public static GameObject CreateRow(Transform parent, float height = 22f)
        {
            var go = new GameObject("Row", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var hlg = go.AddComponent<HorizontalLayoutGroup>();
            hlg.spacing = 6;
            hlg.childForceExpandWidth = true;
            hlg.childForceExpandHeight = true;
            hlg.childControlWidth = true;
            hlg.childControlHeight = true;
            var le = go.AddComponent<LayoutElement>();
            le.preferredHeight = height;
            return go;
        }

        public static Slider CreateSlider(Transform parent, float min, float max, float defaultVal, Action<float> onChange)
        {
            var sliderGO = new GameObject("Slider", typeof(RectTransform));
            sliderGO.transform.SetParent(parent, false);
            sliderGO.AddComponent<LayoutElement>().preferredHeight = 18;
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
            handleRect.sizeDelta = new Vector2(12, 12);

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

        public static Button CreateButton(Transform parent, string label, Action onClick)
        {
            var go = new GameObject("Button_" + label, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = InactiveColor;
            go.AddComponent<LayoutElement>().preferredHeight = 28;
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
            text.font = Font;
            text.fontSize = 12;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = label;

            return btn;
        }

        public static Toggle CreateToggle(Transform parent, string label, bool defaultVal, Action<bool> onChange, Color? checkColor = null)
        {
            var go = new GameObject("Toggle_" + label, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.AddComponent<LayoutElement>().preferredHeight = 22;
            var toggle = go.AddComponent<Toggle>();

            var bgGO = new GameObject("Background", typeof(RectTransform), typeof(Image));
            bgGO.transform.SetParent(go.transform, false);
            var bgRect = bgGO.GetComponent<RectTransform>();
            bgRect.anchorMin = new Vector2(0, 0.5f);
            bgRect.anchorMax = new Vector2(0, 0.5f);
            bgRect.pivot = new Vector2(0, 0.5f);
            bgRect.sizeDelta = new Vector2(18, 18);
            bgGO.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.17f);

            var checkGO = new GameObject("Checkmark", typeof(RectTransform), typeof(Image));
            checkGO.transform.SetParent(bgGO.transform, false);
            var checkRect = checkGO.GetComponent<RectTransform>();
            checkRect.anchorMin = Vector2.zero;
            checkRect.anchorMax = Vector2.one;
            checkRect.sizeDelta = new Vector2(-5, -5);
            checkGO.GetComponent<Image>().color = checkColor ?? new Color(0.3f, 0.6f, 1f);

            toggle.targetGraphic = bgGO.GetComponent<Image>();
            toggle.graphic = checkGO.GetComponent<Image>();
            toggle.isOn = defaultVal;

            var textGO = new GameObject("Label", typeof(RectTransform));
            textGO.transform.SetParent(go.transform, false);
            var textRect = textGO.GetComponent<RectTransform>();
            textRect.anchorMin = new Vector2(0, 0);
            textRect.anchorMax = new Vector2(1, 1);
            textRect.offsetMin = new Vector2(25, 0);
            textRect.offsetMax = Vector2.zero;
            var text = textGO.AddComponent<Text>();
            text.font = Font;
            text.fontSize = 12;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.white;
            text.text = label;

            toggle.onValueChanged.AddListener(v => onChange(v));
            return toggle;
        }

        /// Handle to a color picker built by CreateColorPicker - lets callers whose color
        /// picker can represent different targets over time (e.g. LightingUIBuilder
        /// switching which light's sliders are shown) push a new value into the swatch,
        /// wheel knob and brightness slider without re-triggering onChange.
        public sealed class ColorPickerHandle
        {
            public readonly Image Swatch;
            private readonly Action<Color> _setWithoutNotify;

            public ColorPickerHandle(Image swatch, Action<Color> setWithoutNotify)
            {
                Swatch = swatch;
                _setWithoutNotify = setWithoutNotify;
            }

            public void SetValueWithoutNotify(Color color) => _setWithoutNotify(color);
        }

        /// Drag/click handler for the hue/saturation wheel - reports the picked point as
        /// polar coordinates (hue = angle, saturation = radius, both 0..1) rather than a
        /// Color so CreateColorPicker stays in charge of combining it with the separate
        /// brightness slider.
        private sealed class ColorWheelDrag : MonoBehaviour, IPointerDownHandler, IDragHandler
        {
            private RectTransform _rect;
            public Action<float, float> OnPick;

            public void Init(RectTransform rect) => _rect = rect;

            public void OnPointerDown(PointerEventData eventData) => HandlePointer(eventData);
            public void OnDrag(PointerEventData eventData) => HandlePointer(eventData);

            private void HandlePointer(PointerEventData eventData)
            {
                if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(_rect, eventData.position, eventData.pressEventCamera, out Vector2 local))
                    return;
                float radius = _rect.rect.width * 0.5f;
                Vector2 norm = local / radius;
                float dist = Mathf.Clamp01(norm.magnitude);
                float hue = Mathf.Repeat(Mathf.Atan2(norm.y, norm.x) / (Mathf.PI * 2f), 1f);
                OnPick?.Invoke(hue, dist);
            }
        }

        private static Texture2D _wheelTexture;
        private static Texture2D _knobTexture;

        // Procedural rather than an image asset (see BrushAlphaLibrary for the same
        // reasoning elsewhere in this project) - polar-mapped hue/saturation disc, angle =
        // hue, radius = saturation, value fixed at 1 (brightness is the separate slider
        // below the wheel). Cached across every color picker instance since it never changes.
        private static Texture2D GetWheelTexture()
        {
            if (_wheelTexture != null) return _wheelTexture;
            const int size = 128;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var pixels = new Color[size * size];
            float radius = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = (x + 0.5f - radius) / radius;
                    float dy = (y + 0.5f - radius) / radius;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);
                    if (dist > 1f)
                    {
                        pixels[y * size + x] = new Color(0, 0, 0, 0);
                        continue;
                    }
                    float hue = Mathf.Repeat(Mathf.Atan2(dy, dx) / (Mathf.PI * 2f), 1f);
                    Color c = Color.HSVToRGB(hue, dist, 1f);
                    c.a = dist > 0.96f ? Mathf.Lerp(1f, 0f, (dist - 0.96f) / 0.04f) : 1f;
                    pixels[y * size + x] = c;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            _wheelTexture = tex;
            return tex;
        }

        // Small filled circle with a dark ring border so the knob stays visible over both
        // light and dark parts of the wheel.
        private static Texture2D GetKnobTexture()
        {
            if (_knobTexture != null) return _knobTexture;
            const int size = 20;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var pixels = new Color[size * size];
            float radius = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(radius, radius)) / radius;
                    Color c = dist > 1f ? new Color(0, 0, 0, 0) : (dist > 0.72f ? Color.black : Color.white);
                    pixels[y * size + x] = c;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply();
            _knobTexture = tex;
            return tex;
        }

        /// A header row (label + color swatch), a draggable hue/saturation wheel, and a
        /// brightness slider beneath it - replaces the old plain R/G/B sliders with a
        /// standard HSV wheel+V picker.
        public static ColorPickerHandle CreateColorPicker(Transform parent, string label, Color defaultColor, Action<Color> onChange)
        {
            var headerRow = CreateRow(parent, 20f);
            CreateLabel(headerRow.transform, label, 13, FontStyle.Normal);

            var swatchGO = new GameObject("Swatch", typeof(RectTransform), typeof(Image));
            swatchGO.transform.SetParent(headerRow.transform, false);
            swatchGO.AddComponent<LayoutElement>().preferredWidth = 28;
            var swatch = swatchGO.GetComponent<Image>();
            Color c = defaultColor; c.a = 1f;
            swatch.color = c;

            const float wheelSize = 100f;
            var containerGO = new GameObject("WheelContainer", typeof(RectTransform));
            containerGO.transform.SetParent(parent, false);
            containerGO.AddComponent<LayoutElement>().preferredHeight = wheelSize + 6f;

            var wheelGO = new GameObject("Wheel", typeof(RectTransform), typeof(RawImage));
            wheelGO.transform.SetParent(containerGO.transform, false);
            var wheelRect = wheelGO.GetComponent<RectTransform>();
            wheelRect.anchorMin = wheelRect.anchorMax = wheelRect.pivot = new Vector2(0.5f, 0.5f);
            wheelRect.sizeDelta = new Vector2(wheelSize, wheelSize);
            wheelGO.GetComponent<RawImage>().texture = GetWheelTexture();

            var knobGO = new GameObject("Knob", typeof(RectTransform), typeof(RawImage));
            knobGO.transform.SetParent(wheelGO.transform, false);
            var knobRect = knobGO.GetComponent<RectTransform>();
            knobRect.anchorMin = knobRect.anchorMax = knobRect.pivot = new Vector2(0.5f, 0.5f);
            knobRect.sizeDelta = new Vector2(12, 12);
            knobGO.GetComponent<RawImage>().texture = GetKnobTexture();

            var drag = wheelGO.AddComponent<ColorWheelDrag>();
            drag.Init(wheelRect);

            Color.RGBToHSV(defaultColor, out float h, out float s, out float v);
            Slider valueSlider = null;

            void PositionKnob()
            {
                float angle = h * Mathf.PI * 2f;
                knobRect.anchoredPosition = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * (s * wheelSize * 0.5f);
            }

            void Push()
            {
                Color col = Color.HSVToRGB(h, s, v);
                col.a = 1f;
                swatch.color = col;
                onChange(col);
            }

            drag.OnPick = (hue, sat) =>
            {
                h = hue; s = sat;
                PositionKnob();
                Push();
            };

            valueSlider = CreateMiniSlider(parent, "V", v, val => { v = val; Push(); });
            PositionKnob();

            void SetWithoutNotify(Color color)
            {
                Color.RGBToHSV(color, out h, out s, out v);
                PositionKnob();
                valueSlider.SetValueWithoutNotify(v);
                Color sc = color; sc.a = 1f;
                swatch.color = sc;
            }

            return new ColorPickerHandle(swatch, SetWithoutNotify);
        }

        private static Slider CreateMiniSlider(Transform parent, string prefix, float defaultVal, Action<float> onChange)
        {
            var row = CreateRow(parent, 16f);
            var label = CreateLabel(row.transform, prefix, 11, FontStyle.Normal);
            label.GetComponent<LayoutElement>().preferredWidth = 14;
            label.GetComponent<LayoutElement>().flexibleWidth = 0;
            return CreateSlider(row.transform, 0f, 1f, defaultVal, onChange);
        }

        /// A clickable header that shows/hides a following content block - lets panels with
        /// many controls (Post-Processing, Lighting) stay collapsed to just their titles.
        public static Transform CreateFoldoutSection(Transform parent, string title, bool startOpen = true)
        {
            var headerGO = new GameObject("FoldoutHeader_" + title, typeof(RectTransform), typeof(Image));
            headerGO.transform.SetParent(parent, false);
            headerGO.GetComponent<Image>().color = HeaderColor;
            headerGO.AddComponent<LayoutElement>().preferredHeight = 24;
            var btn = headerGO.AddComponent<Button>();
            btn.targetGraphic = headerGO.GetComponent<Image>();

            var textGO = new GameObject("Text", typeof(RectTransform));
            textGO.transform.SetParent(headerGO.transform, false);
            var textRect = textGO.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(8, 0);
            textRect.offsetMax = Vector2.zero;
            var text = textGO.AddComponent<Text>();
            text.font = Font;
            text.fontSize = 13;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleLeft;
            text.color = Color.white;

            var contentGO = new GameObject("FoldoutContent_" + title, typeof(RectTransform));
            contentGO.transform.SetParent(parent, false);
            var vlg = contentGO.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 6;
            vlg.childAlignment = TextAnchor.UpperLeft;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandHeight = false;
            vlg.childForceExpandWidth = true;
            var fitter = contentGO.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            void SetOpen(bool open)
            {
                contentGO.SetActive(open);
                // Plain ASCII rather than Unicode triangle glyphs (e.g. U+25BE) - the
                // built-in LegacyRuntime.ttf font isn't guaranteed to include those, and a
                // missing glyph fails silently (blank box) rather than erroring.
                text.text = (open ? "v " : "> ") + title;
            }

            btn.onClick.AddListener(() => SetOpen(!contentGO.activeSelf));
            SetOpen(startOpen);

            return contentGO.transform;
        }

        /// One button in a modal (see ShowModal).
        public class ModalChoice
        {
            public readonly string Label;
            public readonly Action OnChosen;
            public ModalChoice(string label, Action onChosen) { Label = label; OnChosen = onChosen; }
        }

        /// Blocking overlay (dim backdrop + centred panel) offering a set of choices. Cancel is
        /// added automatically and always first, so no caller can ship a modal with no way out.
        /// The modal destroys itself before invoking the chosen action, so an action that opens
        /// another modal can't be hidden behind this one.
        ///
        /// Extracted from SceneGraphUIBuilder's Join confirmation once a second panel needed a
        /// prompt - a modal is exactly the kind of thing that drifts into two subtly different
        /// implementations if copied. `buildExtraContent` (optional) inserts controls between
        /// the message and the button row, which is what Join uses for its remesh toggle/slider.
        /// Returns the modal's root so a caller tracking it can dismiss it early. Root-level
        /// like CreatePanelCanvas, and for the same reason - see its remarks.
        public static GameObject ShowModal(string message,
                                           Action<Transform> buildExtraContent, params ModalChoice[] choices)
        {
            var canvasGO = new GameObject("ModalCanvas", typeof(RectTransform));
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above every panel canvas (which use the default 0) so the modal is never buried.
            canvas.sortingOrder = 100;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            canvasGO.AddComponent<GraphicRaycaster>();

            // Full-screen image, not just a dim: it also swallows clicks, so the panels behind
            // can't be operated while a decision is pending.
            var blockerGO = new GameObject("Blocker", typeof(RectTransform), typeof(Image));
            blockerGO.transform.SetParent(canvasGO.transform, false);
            var blockerRect = blockerGO.GetComponent<RectTransform>();
            blockerRect.anchorMin = Vector2.zero;
            blockerRect.anchorMax = Vector2.one;
            blockerRect.sizeDelta = Vector2.zero;
            blockerGO.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);

            var panelGO = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panelGO.transform.SetParent(canvasGO.transform, false);
            panelGO.GetComponent<Image>().color = PanelColor;
            var panelRect = panelGO.GetComponent<RectTransform>();
            panelRect.anchorMin = panelRect.anchorMax = panelRect.pivot = new Vector2(0.5f, 0.5f);
            panelRect.sizeDelta = new Vector2(320, 0);
            var layout = panelGO.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(16, 16, 16, 16);
            layout.spacing = 10;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            layout.childForceExpandWidth = true;
            var fitter = panelGO.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            CreateLabel(panelGO.transform, message, 13, FontStyle.Normal);
            buildExtraContent?.Invoke(panelGO.transform);

            // Choices get a row each rather than sharing one: their labels are full phrases
            // ("Add to current scene"), which would be unreadably squeezed side by side.
            // Deactivated before Destroy, deliberately. Destroy is deferred to end of frame, so
            // between the click and the actual destruction the overlay is still live: it keeps
            // swallowing input and stays on screen, and an action that opens a second modal
            // would briefly stack two. SetActive(false) makes dismissal take effect the instant
            // the button is pressed, with Destroy following to actually free it.
            void Dismiss()
            {
                canvasGO.SetActive(false);
                UnityEngine.Object.Destroy(canvasGO);
            }

            CreateButton(CreateRow(panelGO.transform, 30f).transform, "Cancel", Dismiss);

            foreach (ModalChoice choice in choices)
            {
                if (choice == null) continue;
                ModalChoice captured = choice; // avoid the closure capturing the loop variable
                CreateButton(CreateRow(panelGO.transform, 30f).transform, captured.Label, () =>
                {
                    Dismiss();
                    captured.OnChosen?.Invoke();
                });
            }

            return canvasGO;
        }

        /// Single-line text entry. `onSubmit` fires on Enter and on focus loss (Unity raises
        /// onEndEdit for both), NOT on every keystroke - a path field that fired per-character
        /// would try to resolve half-typed paths.
        ///
        /// Uses the legacy `InputField` rather than TMP_InputField to match every other control
        /// in this factory (all built on UnityEngine.UI + the built-in LegacyRuntime font), so
        /// the project keeps needing no TextMeshPro dependency or font asset.
        public static InputField CreateInputField(Transform parent, string initialText, Action<string> onSubmit)
        {
            var go = new GameObject("InputField", typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            go.GetComponent<Image>().color = new Color(0.15f, 0.15f, 0.17f);
            go.AddComponent<LayoutElement>().preferredHeight = 24;

            var textGO = new GameObject("Text", typeof(RectTransform));
            textGO.transform.SetParent(go.transform, false);
            var textRect = textGO.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            // Left/right inset so the caret and the first glyph aren't flush against the border.
            textRect.offsetMin = new Vector2(6, 0);
            textRect.offsetMax = new Vector2(-6, 0);

            var text = textGO.AddComponent<Text>();
            text.font = Font;
            text.fontSize = 11;
            text.color = Color.white;
            text.alignment = TextAnchor.MiddleLeft;
            // InputField requires a non-wrapping, single-line-capable Text; Overflow on both
            // axes keeps a long path scrolling horizontally instead of being clipped away.
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;

            var field = go.AddComponent<InputField>();
            field.textComponent = text;
            field.lineType = InputField.LineType.SingleLine;
            field.text = initialText ?? string.Empty;
            field.caretColor = Color.white;
            field.customCaretColor = true;
            field.selectionColor = new Color(0.25f, 0.55f, 0.95f, 0.6f);
            if (onSubmit != null) field.onEndEdit.AddListener(v => onSubmit(v));

            return field;
        }
    }
}
