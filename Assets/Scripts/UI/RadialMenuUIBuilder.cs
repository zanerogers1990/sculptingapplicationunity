using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sculpting
{
    /// ZBrush/Blender-style pie menu: holding Space (SculptController.HandleRadialMenuKey) pops
    /// up a ring of tool icons centered on wherever the cursor was when Space went down: Move,
    /// Clay, Smooth, Crease, Mask, Inflate, Flatten, plus a Strength/Size slider pair underneath
    /// so both can be scrubbed without leaving the menu. Hovering a wedge highlights it, and
    /// clicking one applies it (CurrentBrush for the six real brushes, IsMaskPaintMode's toggle
    /// for Mask - there is no BrushType.Mask, see SculptController.IsMaskPaintMode) and calls
    /// CloseRadialMenu immediately rather than waiting for Space to also come up. Releasing
    /// Space with nothing clicked just cancels - both paths clear the same controller flag, so
    /// this class only ever has to poll IsRadialMenuOpen.
    ///
    /// Built purely from code at runtime, same as SculptUIBuilder/UIFactory - its own root-level
    /// Canvas (see DestroyStaleCanvas reasoning on UIFactory) rather than living inside
    /// SculptCanvas, so its sorting order can sit above every docked panel without disturbing
    /// theirs.
    public class RadialMenuUIBuilder : MonoBehaviour
    {
        public SculptController controller;

        private const float RingRadius = 92f;
        private const float WedgeDiameter = 56f;
        private const float WedgeRadius = WedgeDiameter * 0.5f;
        private const float IconSize = 30f;
        private const float LabelGap = 10f;
        private const float SliderPanelWidth = 170f;
        private const float SliderPanelGap = 18f;
        // Only used to keep the menu clear of the screen edge - doesn't need to track the
        // slider panel's real (auto-fit) height exactly, just not undershoot it.
        private const float EstimatedSliderPanelHeight = 100f;

        private const float OpenCloseSpeed = 9f; // ~110ms to fully open/close
        private const float ScaleLerpSpeed = 14f;
        private const float ColorLerpSpeed = 12f;
        private const float HoverScale = 1.18f;

        private const float HalfExtentX = RingRadius + WedgeRadius + 14f;
        private const float HalfExtentTop = RingRadius + WedgeRadius + 14f;
        private const float HalfExtentBottom = RingRadius + WedgeRadius + SliderPanelGap + EstimatedSliderPanelHeight + 10f;

        private static readonly Color HoverColor = new Color(0.4f, 0.75f, 1f);

        private readonly struct ToolDef
        {
            public readonly string Label;
            public readonly string Tooltip;
            public readonly BrushType? Brush; // null => the Mask toggle, not a BrushType (see IsMaskPaintMode)
            public readonly Func<Sprite> Icon;

            public ToolDef(string label, string tooltip, BrushType? brush, Func<Sprite> icon)
            {
                Label = label; Tooltip = tooltip; Brush = brush; Icon = icon;
            }
        }

        private static readonly ToolDef[] ToolDefs =
        {
            new ToolDef("Move", "Drags the surface around under the brush, like pushing clay.", BrushType.Move, GetMoveIcon),
            new ToolDef("Clay", "Builds up rounded volume, like adding a lump of clay.", BrushType.Clay, GetClayIcon),
            new ToolDef("Smooth", "Averages the surface to remove bumps and noise.", BrushType.Smooth, GetSmoothIcon),
            new ToolDef("Crease", "Pinches in a sharp groove, like a fingernail crease.", BrushType.Crease, GetCreaseIcon),
            new ToolDef("Mask", "Paints a mask that protects or isolates areas from later brush strokes.", null, GetMaskIcon),
            new ToolDef("Inflate", "Pushes the surface outward along its normal, like inflating a balloon.", BrushType.Inflate, GetInflateIcon),
            new ToolDef("Flatten", "Flattens the surface to a plane.", BrushType.Flatten, GetFlattenIcon),
        };

        private sealed class WedgeHoverState : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
        {
            public bool Hovered { get; private set; }
            public void OnPointerEnter(PointerEventData eventData) => Hovered = true;
            public void OnPointerExit(PointerEventData eventData) => Hovered = false;
        }

        private sealed class Wedge
        {
            public RectTransform Visual;
            public Image Background;
            public WedgeHoverState Hover;
            public ToolDef Def;
        }

        private GameObject _rootGO;
        private RectTransform _rootRect;
        private CanvasGroup _canvasGroup;
        private Text _centerLabel;
        private Slider _strengthSlider;
        private Slider _sizeSlider;
        private float _openness;
        private readonly Wedge[] _wedges = new Wedge[ToolDefs.Length];

        private void Start()
        {
            if (controller == null) controller = FindFirstObjectByType<SculptController>();
            BuildUI();
        }

        private void Update()
        {
            if (controller == null || _rootRect == null) return;

            bool open = controller.IsRadialMenuOpen;
            _openness = Mathf.MoveTowards(_openness, open ? 1f : 0f, Time.deltaTime * OpenCloseSpeed);
            bool shouldShow = _openness > 0.001f;
            if (_rootGO.activeSelf != shouldShow) _rootGO.SetActive(shouldShow);
            if (!shouldShow) return;

            // Repositioned every frame it's open (not just once) so a window resize mid-hold
            // doesn't leave the clamp stale - cheap either way.
            if (open)
            {
                Vector2 pos = controller.RadialMenuScreenPosition;
                pos.x = Mathf.Clamp(pos.x, HalfExtentX, Mathf.Max(HalfExtentX, Screen.width - HalfExtentX));
                pos.y = Mathf.Clamp(pos.y, HalfExtentBottom, Mathf.Max(HalfExtentBottom, Screen.height - HalfExtentTop));
                _rootRect.position = pos;
            }

            _rootRect.localScale = Vector3.one * Mathf.Lerp(0.85f, 1f, _openness);
            _canvasGroup.alpha = _openness;
            // Stops blocking (and stops registering as "over UI" for SculptController's own
            // _isOverUI check) the instant Space comes up or a wedge is clicked, rather than
            // waiting out the fade - see CloseRadialMenu's remarks on why closing is immediate.
            _canvasGroup.blocksRaycasts = open;
            _canvasGroup.interactable = open;

            // Panel's own Strength/Size sliders can also change these from outside (F/S drag,
            // scroll-resize) - same SetValueWithoutNotify resync SculptUIBuilder.Update does.
            if (_strengthSlider != null) _strengthSlider.SetValueWithoutNotify(controller.BrushStrength);
            if (_sizeSlider != null)
            {
                UIFactory.SetRangeAndValueWithoutNotify(_sizeSlider,
                    controller.BrushSizeMin, controller.BrushSizeMax, controller.BrushSize);
            }

            string hoveredLabel = null;
            for (int i = 0; i < _wedges.Length; i++)
            {
                Wedge w = _wedges[i];
                bool hovered = open && w.Hover.Hovered;
                if (hovered) hoveredLabel = w.Def.Label;

                bool isActive = w.Def.Brush.HasValue
                    ? (!controller.IsMaskPaintMode && controller.CurrentBrush == w.Def.Brush.Value)
                    : controller.IsMaskPaintMode;

                Color targetColor = hovered ? HoverColor : (isActive ? UIFactory.ActiveColor : UIFactory.InactiveColor);
                float targetScale = hovered ? HoverScale : 1f;
                w.Background.color = Color.Lerp(w.Background.color, targetColor, Time.deltaTime * ColorLerpSpeed);
                w.Visual.localScale = Vector3.Lerp(w.Visual.localScale, Vector3.one * targetScale, Time.deltaTime * ScaleLerpSpeed);
            }
            _centerLabel.text = hoveredLabel ?? "";
        }

        private void SelectTool(int index)
        {
            ToolDef def = ToolDefs[index];
            if (def.Brush.HasValue) controller.CurrentBrush = def.Brush.Value;
            else controller.IsMaskPaintMode = !controller.IsMaskPaintMode;
            controller.CloseRadialMenu();
        }

        private void BuildUI()
        {
            // Idempotent rebuild, same reasoning as UIFactory.DestroyStaleCanvas - a scene
            // reload flow (SceneGraphUIBuilder's Replace) re-runs every builder's Start.
            GameObject stale = GameObject.Find("RadialMenuCanvas");
            if (stale != null) DestroyImmediate(stale);

            var canvasGO = new GameObject("RadialMenuCanvas", typeof(RectTransform));
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            // Above every docked panel canvas (default sortingOrder 0) so the menu is never
            // buried under them; below UIFactory.ShowModal's blocking overlay (100).
            canvas.sortingOrder = 60;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            canvasGO.AddComponent<GraphicRaycaster>();

            _rootGO = new GameObject("MenuRoot", typeof(RectTransform));
            _rootGO.transform.SetParent(canvasGO.transform, false);
            _rootRect = _rootGO.GetComponent<RectTransform>();
            // Anchored/pivoted like SculptUIBuilder's brush cursor ring - a screen-pixel
            // position can be assigned straight to RectTransform.position on an Overlay canvas.
            _rootRect.anchorMin = _rootRect.anchorMax = new Vector2(0f, 0f);
            _rootRect.pivot = new Vector2(0.5f, 0.5f);
            _rootRect.sizeDelta = Vector2.zero;
            _canvasGroup = _rootGO.AddComponent<CanvasGroup>();

            BuildCenterLabel();
            for (int i = 0; i < ToolDefs.Length; i++) BuildWedge(i);
            BuildSliderPanel();

            _rootGO.SetActive(false);
        }

        private void BuildCenterLabel()
        {
            var go = new GameObject("CenterLabel", typeof(RectTransform));
            go.transform.SetParent(_rootGO.transform, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(140f, 24f);
            _centerLabel = go.AddComponent<Text>();
            _centerLabel.font = UIFactory.Font;
            _centerLabel.fontSize = 13;
            _centerLabel.fontStyle = FontStyle.Bold;
            _centerLabel.alignment = TextAnchor.MiddleCenter;
            _centerLabel.color = Color.white;
            _centerLabel.raycastTarget = false;
        }

        private void BuildWedge(int index)
        {
            ToolDef def = ToolDefs[index];
            // Clockwise from the top (90 degrees, standard math convention) - CCW would put
            // Clay left-of-top instead of right-of-top, which reads backwards against every
            // clock-face/pie-menu convention users already know.
            float angle = 90f - index * (360f / ToolDefs.Length);
            Vector2 offset = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)) * RingRadius;

            var wedgeGO = new GameObject("Wedge_" + def.Label, typeof(RectTransform));
            wedgeGO.transform.SetParent(_rootGO.transform, false);
            var wedgeRect = wedgeGO.GetComponent<RectTransform>();
            wedgeRect.anchorMin = wedgeRect.anchorMax = new Vector2(0.5f, 0.5f);
            wedgeRect.anchoredPosition = offset;
            wedgeRect.sizeDelta = Vector2.zero;

            // Hover scale/color animate on this inner "Visual" rect, not the outer wedgeGO -
            // keeps the label below (and the wedge's ring position) from scaling along with it.
            var visualGO = new GameObject("Visual", typeof(RectTransform), typeof(Image));
            visualGO.transform.SetParent(wedgeGO.transform, false);
            var visualRect = visualGO.GetComponent<RectTransform>();
            visualRect.anchorMin = visualRect.anchorMax = new Vector2(0.5f, 0.5f);
            visualRect.sizeDelta = new Vector2(WedgeDiameter, WedgeDiameter);
            var bgImage = visualGO.GetComponent<Image>();
            bgImage.sprite = GetCircleSprite();
            bgImage.color = UIFactory.InactiveColor;

            var btn = visualGO.AddComponent<Button>();
            btn.targetGraphic = bgImage;
            // Manual per-frame color/scale in Update() owns all the feedback here - Button's
            // own ColorTint transition would otherwise multiply on top of it and fight the lerp.
            btn.transition = Selectable.Transition.None;
            int captured = index; // avoid the closure capturing the loop variable
            btn.onClick.AddListener(() => SelectTool(captured));

            var hover = visualGO.AddComponent<WedgeHoverState>();

            var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGO.transform.SetParent(visualGO.transform, false);
            var iconRect = iconGO.GetComponent<RectTransform>();
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.sizeDelta = new Vector2(IconSize, IconSize);
            var iconImage = iconGO.GetComponent<Image>();
            iconImage.sprite = def.Icon();
            iconImage.color = Color.white;
            iconImage.raycastTarget = false;

            var labelGO = new GameObject("Label", typeof(RectTransform));
            labelGO.transform.SetParent(wedgeGO.transform, false);
            var labelRect = labelGO.GetComponent<RectTransform>();
            labelRect.anchorMin = labelRect.anchorMax = new Vector2(0.5f, 0.5f);
            labelRect.anchoredPosition = new Vector2(0f, -(WedgeRadius + LabelGap));
            labelRect.sizeDelta = new Vector2(84f, 16f);
            var labelText = labelGO.AddComponent<Text>();
            labelText.font = UIFactory.Font;
            labelText.fontSize = 11;
            labelText.alignment = TextAnchor.MiddleCenter;
            labelText.color = new Color(1f, 1f, 1f, 0.85f);
            labelText.text = def.Label;
            labelText.raycastTarget = false;

            TooltipSystem.Attach(visualGO, def.Tooltip);

            _wedges[index] = new Wedge { Visual = visualRect, Background = bgImage, Hover = hover, Def = def };
        }

        private void BuildSliderPanel()
        {
            var panelGO = new GameObject("SliderPanel", typeof(RectTransform));
            panelGO.transform.SetParent(_rootGO.transform, false);
            var panelRect = panelGO.GetComponent<RectTransform>();
            panelRect.anchorMin = panelRect.anchorMax = new Vector2(0.5f, 0.5f);
            panelRect.pivot = new Vector2(0.5f, 1f);
            panelRect.anchoredPosition = new Vector2(0f, -(RingRadius + WedgeRadius + SliderPanelGap));
            panelRect.sizeDelta = new Vector2(SliderPanelWidth, 0f);

            var vlg = panelGO.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 2f;
            vlg.childAlignment = TextAnchor.UpperCenter;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            var fitter = panelGO.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

            UIFactory.CreateLabel(panelGO.transform, "Strength", 12, FontStyle.Normal);
            _strengthSlider = UIFactory.CreateSlider(panelGO.transform, 0.01f, 1f, controller.BrushStrength,
                v => controller.BrushStrength = v,
                "Brush Strength - also adjustable by holding F and dragging, or the side panel.");

            UIFactory.CreateLabel(panelGO.transform, "Size", 12, FontStyle.Normal);
            _sizeSlider = UIFactory.CreateSlider(panelGO.transform, controller.BrushSizeMin, controller.BrushSizeMax,
                controller.BrushSize, v => controller.BrushSize = v,
                "Brush Size - also adjustable by holding S and dragging, scrolling over the model, or the side panel.");
        }

        // ------------------------------------------------------------- procedural tool icons
        //
        // No icon assets exist anywhere in this project (see UIFactory's own color wheel/knob
        // textures for the same reasoning) - every glyph below is a tiny signed-distance shape
        // rasterized once into a cached Texture2D/Sprite, antialiased the same way
        // SculptUIBuilder's ring/dot cursor sprites are (SmoothStep across a 2px band).

        private readonly struct Seg
        {
            public readonly Vector2 A, B;
            public Seg(Vector2 a, Vector2 b) { A = a; B = b; }
        }

        private static float SegDist(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 0.0001f));
            return Vector2.Distance(p, a + ab * t);
        }

        private static float StrokeAlphaAt(float dist, float thickness) =>
            1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(thickness - 1f, thickness + 1f, dist));

        private static float FillAlphaAt(float signedDist) =>
            1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-1f, 1f, signedDist));

        private static float SegsStroke(Vector2 p, IList<Seg> segs, float thickness)
        {
            float best = float.MaxValue;
            for (int i = 0; i < segs.Count; i++) best = Mathf.Min(best, SegDist(p, segs[i].A, segs[i].B));
            return StrokeAlphaAt(best, thickness);
        }

        private static float CircleStroke(Vector2 p, Vector2 c, float r, float thickness) =>
            StrokeAlphaAt(Mathf.Abs(Vector2.Distance(p, c) - r), thickness);

        private static float CircleFill(Vector2 p, Vector2 c, float r) => FillAlphaAt(Vector2.Distance(p, c) - r);

        private static float BoxFill(Vector2 p, Vector2 c, Vector2 half)
        {
            Vector2 d = new Vector2(Mathf.Abs(p.x - c.x) - half.x, Mathf.Abs(p.y - c.y) - half.y);
            float outside = new Vector2(Mathf.Max(d.x, 0f), Mathf.Max(d.y, 0f)).magnitude;
            float inside = Mathf.Min(Mathf.Max(d.x, d.y), 0f);
            return FillAlphaAt(outside + inside);
        }

        private static float DashedRingAlpha(Vector2 p, float radius, float thickness, int dashCount, float onFraction)
        {
            float ringAlpha = StrokeAlphaAt(Mathf.Abs(p.magnitude - radius), thickness);
            float angle01 = Mathf.Repeat(Mathf.Atan2(p.y, p.x) / (Mathf.PI * 2f), 1f);
            float dashPhase = Mathf.Repeat(angle01 * dashCount, 1f);
            return dashPhase < onFraction ? ringAlpha : 0f;
        }

        private static void AddArrow(List<Seg> segs, Vector2 dir, float innerR, float outerR, float headLen, float headHalfWidth)
        {
            Vector2 tail = dir * innerR;
            Vector2 tip = dir * outerR;
            segs.Add(new Seg(tail, tip));
            Vector2 back = -dir * headLen;
            Vector2 perp = new Vector2(-dir.y, dir.x) * headHalfWidth;
            segs.Add(new Seg(tip, tip + back + perp));
            segs.Add(new Seg(tip, tip + back - perp));
        }

        private static List<Seg> BuildArrowCross(int count, float innerR, float outerR, float headLen, float headHalfWidth)
        {
            var segs = new List<Seg>();
            for (int i = 0; i < count; i++)
            {
                float ang = i * (360f / count);
                Vector2 dir = new Vector2(Mathf.Cos(ang * Mathf.Deg2Rad), Mathf.Sin(ang * Mathf.Deg2Rad));
                AddArrow(segs, dir, innerR, outerR, headLen, headHalfWidth);
            }
            return segs;
        }

        // Sampled sine curve, not a real curve primitive - short enough polylines that the
        // per-segment SegDist above reads as smooth at this icon's display size.
        private static List<Seg> BuildSmoothWaves()
        {
            var segs = new List<Seg>();
            foreach (float y0 in new[] { -9f, 0f, 9f })
            {
                Vector2? prev = null;
                for (int i = 0; i <= 16; i++)
                {
                    float t = i / 16f;
                    float x = Mathf.Lerp(-14f, 14f, t);
                    float y = y0 + Mathf.Sin(t * Mathf.PI * 2f) * 3f;
                    Vector2 p = new Vector2(x, y);
                    if (prev.HasValue) segs.Add(new Seg(prev.Value, p));
                    prev = p;
                }
            }
            return segs;
        }

        private static readonly List<Seg> MoveSegs = BuildArrowCross(4, 3f, 17f, 6f, 4.5f);
        private static readonly List<Seg> InflateArrowSegs = BuildArrowCross(4, 11f, 18f, 5f, 4f);
        private static readonly List<Seg> SmoothSegs = BuildSmoothWaves();
        private static readonly List<Seg> CreaseSegs = new List<Seg>
        {
            new Seg(new Vector2(-11f, 7f), new Vector2(0f, -9f)),
            new Seg(new Vector2(0f, -9f), new Vector2(11f, 7f)),
        };
        private static readonly List<Seg> FlattenArrowSegs = new List<Seg>
        {
            new Seg(new Vector2(0f, 14f), new Vector2(0f, -2f)),
            new Seg(new Vector2(0f, -2f), new Vector2(-5f, 4f)),
            new Seg(new Vector2(0f, -2f), new Vector2(5f, 4f)),
        };

        private static Sprite Rasterize(Func<Vector2, float> alphaAt, int size = 64)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var pixels = new Color32[size * size];
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f) - center;
                    float a = Mathf.Clamp01(alphaAt(p));
                    pixels[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private static Sprite _circleSprite, _moveIcon, _clayIcon, _smoothIcon, _creaseIcon, _maskIcon, _inflateIcon, _flattenIcon;

        private static Sprite GetCircleSprite() => _circleSprite ??= Rasterize(p => CircleFill(p, Vector2.zero, 30f));
        private static Sprite GetMoveIcon() => _moveIcon ??= Rasterize(p => SegsStroke(p, MoveSegs, 2.4f));
        private static Sprite GetClayIcon() => _clayIcon ??= Rasterize(p =>
            Mathf.Max(CircleFill(p, new Vector2(-4f, -3f), 11f), CircleFill(p, new Vector2(5f, 4f), 8f)));
        private static Sprite GetSmoothIcon() => _smoothIcon ??= Rasterize(p => SegsStroke(p, SmoothSegs, 2.2f));
        private static Sprite GetCreaseIcon() => _creaseIcon ??= Rasterize(p => SegsStroke(p, CreaseSegs, 2.6f));
        private static Sprite GetMaskIcon() => _maskIcon ??= Rasterize(p => DashedRingAlpha(p, 13f, 3f, 10, 0.55f));
        private static Sprite GetInflateIcon() => _inflateIcon ??= Rasterize(p =>
            Mathf.Max(CircleStroke(p, Vector2.zero, 10f, 2.2f), SegsStroke(p, InflateArrowSegs, 2.2f)));
        private static Sprite GetFlattenIcon() => _flattenIcon ??= Rasterize(p =>
            Mathf.Max(BoxFill(p, new Vector2(0f, -6f), new Vector2(13f, 2.5f)), SegsStroke(p, FlattenArrowSegs, 2.4f)));
    }
}
