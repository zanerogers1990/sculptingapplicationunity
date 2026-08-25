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
        public static Transform CreatePanelCanvas(Transform parent, string name, Vector2 anchor, Vector2 offset, float width)
        {
            var canvasGO = new GameObject(name, typeof(RectTransform));
            canvasGO.transform.SetParent(parent, false);
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
    }
}
