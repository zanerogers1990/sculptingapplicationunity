using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Sculpting
{
    /// Second pie menu, alongside RadialMenuUIBuilder: holding Shift+Space
    /// (SculptController.HandleRegionRadialMenuKey) pops up a ring of the box/lasso region tools
    /// (Box/Lasso Hide, Box/Lasso Mask, Box/Lasso Trim) plus Show All and Invert Visible - the
    /// gesture buttons that used to live in the left panel's "Hide / Trim / Region Select"
    /// foldout (see SculptUIBuilder, which now only keeps the drag marquee/crosshair overlay
    /// that section also owned). Clicking a Box/Lasso wedge arms that RegionSelectMode exactly
    /// like the old buttons did (click the armed one again to disarm); clicking Show All/Invert
    /// Visible fires immediately. Either way the menu closes right away, same as the brush menu.
    ///
    /// Same construction as RadialMenuUIBuilder (own root Canvas, procedural SDF line-art icons,
    /// hover/active color lerp) but deliberately self-contained rather than sharing code with it -
    /// see that class's remarks for why (no icon asset infra exists anywhere in this project, and
    /// this app already tolerates the same rasterization approach living independently in more
    /// than one file, e.g. SculptUIBuilder's cursor ring/dot textures).
    public class RegionRadialMenuUIBuilder : MonoBehaviour
    {
        public SculptController controller;

        private const float RingRadius = 92f;
        private const float WedgeDiameter = 56f;
        private const float WedgeRadius = WedgeDiameter * 0.5f;
        private const float IconSize = 30f;
        private const float LabelGap = 10f;

        private const float OpenCloseSpeed = 9f;
        private const float ScaleLerpSpeed = 14f;
        private const float ColorLerpSpeed = 12f;
        private const float HoverScale = 1.18f;

        // No slider panel below (these tools have no continuous parameter to scrub), so unlike
        // RadialMenuUIBuilder the clamp margin is the same on every side.
        private const float HalfExtent = RingRadius + WedgeRadius + 14f;

        private static readonly Color HoverColor = new Color(0.4f, 0.75f, 1f);

        // Same three colors SculptUIBuilder tints the drag marquee/crosshair with (see its own
        // remarks) - duplicated rather than shared across files for the same reason the icon
        // rasterizer below is: this project has no shared UI-constants file, and every *UIBuilder
        // is already self-contained. Kept numerically identical on purpose, so a wedge's icon
        // tint matches the crosshair color the moment that tool is armed.
        private static readonly Color RegionHideActiveColor = new Color(0.3f, 0.75f, 0.8f);
        private static readonly Color MaskActiveColor = new Color(0.95f, 0.65f, 0.15f);
        private static readonly Color RegionTrimActiveColor = new Color(0.95f, 0.35f, 0.3f);

        private readonly struct RegionToolDef
        {
            public readonly string Label;
            public readonly string Tooltip;
            public readonly RegionSelectMode? Mode; // null => an immediate action, not an armable mode
            public readonly Action<RegionRadialMenuUIBuilder> Invoke; // only used when Mode is null
            public readonly Func<Sprite> Icon;
            public readonly Color Tint;

            private RegionToolDef(string label, string tooltip, RegionSelectMode? mode,
                Action<RegionRadialMenuUIBuilder> invoke, Func<Sprite> icon, Color tint)
            {
                Label = label; Tooltip = tooltip; Mode = mode; Invoke = invoke; Icon = icon; Tint = tint;
            }

            public static RegionToolDef ForMode(string label, string tooltip, RegionSelectMode mode, Func<Sprite> icon, Color tint) =>
                new RegionToolDef(label, tooltip, mode, null, icon, tint);

            public static RegionToolDef ForAction(string label, string tooltip, Action<RegionRadialMenuUIBuilder> invoke, Func<Sprite> icon, Color tint) =>
                new RegionToolDef(label, tooltip, null, invoke, icon, tint);
        }

        // Ordered to match the old panel's row grouping (hide/mask/trim/actions) so each pair
        // lands adjacent going clockwise from the top - see BuildWedge's angle formula.
        private static readonly RegionToolDef[] ToolDefs =
        {
            RegionToolDef.ForMode("Box Hide (H)", "Drag a box to hide the geometry it covers.",
                RegionSelectMode.BoxHide, GetBoxIcon, RegionHideActiveColor),
            RegionToolDef.ForMode("Lasso Hide", "Draw a freeform outline to hide the geometry it covers.",
                RegionSelectMode.LassoHide, GetLassoIcon, RegionHideActiveColor),
            RegionToolDef.ForMode("Box Mask (N)", "Drag a box to mask the geometry it covers.",
                RegionSelectMode.BoxMask, GetBoxDashedIcon, MaskActiveColor),
            RegionToolDef.ForMode("Lasso Mask", "Draw a freeform outline to mask the geometry it covers.",
                RegionSelectMode.LassoMask, GetLassoDashedIcon, MaskActiveColor),
            RegionToolDef.ForMode("Box Trim (T)",
                "Drag a box to CUT AWAY the geometry it covers, closing the hole behind it. RMB or Ctrl keeps the covered part instead. Undoable.",
                RegionSelectMode.BoxTrim, GetBoxTrimIcon, RegionTrimActiveColor),
            RegionToolDef.ForMode("Lasso Trim",
                "Draw a freeform outline to CUT AWAY the geometry it covers, closing the hole behind it. RMB or Ctrl keeps the covered part instead. Undoable.",
                RegionSelectMode.LassoTrim, GetLassoTrimIcon, RegionTrimActiveColor),
            RegionToolDef.ForAction("Show All", "Un-hides all geometry on the selected object.",
                self => { SculptableMesh t = self.SelectedMesh(); if (t != null) t.ShowAllGeometry(); }, GetShowAllIcon, Color.white),
            RegionToolDef.ForAction("Invert Visible", "Swaps hidden and visible geometry on the selected object.",
                self => { SculptableMesh t = self.SelectedMesh(); if (t != null) t.InvertVisibleGeometry(); }, GetInvertIcon, Color.white),
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
            public RegionToolDef Def;
        }

        private GameObject _rootGO;
        private RectTransform _rootRect;
        private CanvasGroup _canvasGroup;
        private Text _centerLabel;
        private float _openness;
        private readonly Wedge[] _wedges = new Wedge[ToolDefs.Length];
        private SelectionManager _selection;

        private void Start()
        {
            if (controller == null) controller = FindFirstObjectByType<SculptController>();
            BuildUI();
        }

        private void Update()
        {
            if (controller == null || _rootRect == null) return;

            bool open = controller.IsRegionRadialMenuOpen;
            _openness = Mathf.MoveTowards(_openness, open ? 1f : 0f, Time.deltaTime * OpenCloseSpeed);
            bool shouldShow = _openness > 0.001f;
            if (_rootGO.activeSelf != shouldShow) _rootGO.SetActive(shouldShow);
            if (!shouldShow) return;

            if (open)
            {
                Vector2 pos = controller.RegionRadialMenuScreenPosition;
                pos.x = Mathf.Clamp(pos.x, HalfExtent, Mathf.Max(HalfExtent, Screen.width - HalfExtent));
                pos.y = Mathf.Clamp(pos.y, HalfExtent, Mathf.Max(HalfExtent, Screen.height - HalfExtent));
                _rootRect.position = pos;
            }

            _rootRect.localScale = Vector3.one * Mathf.Lerp(0.85f, 1f, _openness);
            _canvasGroup.alpha = _openness;
            _canvasGroup.blocksRaycasts = open;
            _canvasGroup.interactable = open;

            RegionSelectTool region = controller.RegionSelect;
            string hoveredLabel = null;
            for (int i = 0; i < _wedges.Length; i++)
            {
                Wedge w = _wedges[i];
                bool hovered = open && w.Hover.Hovered;
                if (hovered) hoveredLabel = w.Def.Label;

                bool isActive = w.Def.Mode.HasValue && region != null && region.Mode == w.Def.Mode.Value;

                Color targetColor = hovered ? HoverColor : (isActive ? UIFactory.ActiveColor : UIFactory.InactiveColor);
                float targetScale = hovered ? HoverScale : 1f;
                w.Background.color = Color.Lerp(w.Background.color, targetColor, Time.deltaTime * ColorLerpSpeed);
                w.Visual.localScale = Vector3.Lerp(w.Visual.localScale, Vector3.one * targetScale, Time.deltaTime * ScaleLerpSpeed);
            }
            _centerLabel.text = hoveredLabel ?? "";
        }

        /// Resolved per click rather than captured - same reasoning as SculptUIBuilder's own
        /// SelectedMesh: the selection can change between menu opens and a captured reference
        /// would act on a stale or destroyed object.
        private SculptableMesh SelectedMesh()
        {
            if (_selection == null) _selection = FindFirstObjectByType<SelectionManager>();
            return _selection != null ? _selection.PrimarySelection : null;
        }

        private void SelectTool(int index)
        {
            RegionToolDef def = ToolDefs[index];
            if (def.Mode.HasValue)
            {
                RegionSelectTool region = controller.RegionSelect;
                if (region != null) region.Mode = region.Mode == def.Mode.Value ? RegionSelectMode.Off : def.Mode.Value;
            }
            else
            {
                def.Invoke?.Invoke(this);
            }
            controller.CloseRegionRadialMenu();
        }

        private void BuildUI()
        {
            GameObject stale = GameObject.Find("RegionRadialMenuCanvas");
            if (stale != null) DestroyImmediate(stale);

            var canvasGO = new GameObject("RegionRadialMenuCanvas", typeof(RectTransform));
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 60; // same layer as RadialMenuUIBuilder - the two never show at once
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;
            canvasGO.AddComponent<GraphicRaycaster>();

            _rootGO = new GameObject("MenuRoot", typeof(RectTransform));
            _rootGO.transform.SetParent(canvasGO.transform, false);
            _rootRect = _rootGO.GetComponent<RectTransform>();
            _rootRect.anchorMin = _rootRect.anchorMax = new Vector2(0f, 0f);
            _rootRect.pivot = new Vector2(0.5f, 0.5f);
            _rootRect.sizeDelta = Vector2.zero;
            _canvasGroup = _rootGO.AddComponent<CanvasGroup>();

            BuildCenterLabel();
            for (int i = 0; i < ToolDefs.Length; i++) BuildWedge(i);

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
            RegionToolDef def = ToolDefs[index];
            float angle = 90f - index * (360f / ToolDefs.Length);
            Vector2 offset = new Vector2(Mathf.Cos(angle * Mathf.Deg2Rad), Mathf.Sin(angle * Mathf.Deg2Rad)) * RingRadius;

            var wedgeGO = new GameObject("Wedge_" + def.Label, typeof(RectTransform));
            wedgeGO.transform.SetParent(_rootGO.transform, false);
            var wedgeRect = wedgeGO.GetComponent<RectTransform>();
            wedgeRect.anchorMin = wedgeRect.anchorMax = new Vector2(0.5f, 0.5f);
            wedgeRect.anchoredPosition = offset;
            wedgeRect.sizeDelta = Vector2.zero;

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
            btn.transition = Selectable.Transition.None;
            int captured = index;
            btn.onClick.AddListener(() => SelectTool(captured));

            var hover = visualGO.AddComponent<WedgeHoverState>();

            var iconGO = new GameObject("Icon", typeof(RectTransform), typeof(Image));
            iconGO.transform.SetParent(visualGO.transform, false);
            var iconRect = iconGO.GetComponent<RectTransform>();
            iconRect.anchorMin = iconRect.anchorMax = new Vector2(0.5f, 0.5f);
            iconRect.sizeDelta = new Vector2(IconSize, IconSize);
            var iconImage = iconGO.GetComponent<Image>();
            iconImage.sprite = def.Icon();
            iconImage.color = def.Tint;
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

        // ------------------------------------------------------------- procedural tool icons
        //
        // Same SDF line-art approach as RadialMenuUIBuilder (see its remarks) - duplicated here
        // rather than shared, since that class keeps every one of these helpers private and this
        // one needs a couple of extras (a dashed-polyline stroke, for the Mask wedges' marching-
        // ants look) that the brush menu never needed.

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

        // Walks a polyline by ARC LENGTH (not by vertex) so the dash/gap rhythm stays even
        // regardless of how densely the source points are sampled - a coarse box (4 corners) and
        // a fine lasso loop (28 points) both come out with the same visual dash pitch.
        private static List<Seg> DashPolyline(IList<Vector2> pts, float dashLen, float gapLen)
        {
            var result = new List<Seg>();
            float period = dashLen + gapLen;
            float cursor = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 a = pts[i];
                Vector2 b = pts[(i + 1) % pts.Count];
                float segLen = Vector2.Distance(a, b);
                float t0 = 0f;
                while (t0 < segLen)
                {
                    float phase = cursor % period;
                    bool on = phase < dashLen;
                    float remainInPhase = (on ? dashLen : period) - phase;
                    float step = Mathf.Min(remainInPhase, segLen - t0);
                    if (on)
                    {
                        Vector2 pa = Vector2.Lerp(a, b, t0 / segLen);
                        Vector2 pb = Vector2.Lerp(a, b, (t0 + step) / segLen);
                        result.Add(new Seg(pa, pb));
                    }
                    t0 += step;
                    cursor += step;
                }
            }
            return result;
        }

        private static List<Seg> ClosedLoopSegs(IList<Vector2> pts)
        {
            var segs = new List<Seg>(pts.Count);
            for (int i = 0; i < pts.Count; i++) segs.Add(new Seg(pts[i], pts[(i + 1) % pts.Count]));
            return segs;
        }

        // Plain rectangle - the "Box" half of every Box/Lasso pair.
        private static readonly Vector2[] BoxCorners =
        {
            new Vector2(-11f, -11f), new Vector2(11f, -11f), new Vector2(11f, 11f), new Vector2(-11f, 11f),
        };

        // Irregular closed loop - the "Lasso" half of every pair. A hand-drawn selection is never
        // a perfect circle, so the radius wobbles at two different frequencies rather than one
        // (a single sine would just read as a lumpy ellipse).
        private static List<Vector2> BuildLassoLoopPoints()
        {
            var pts = new List<Vector2>();
            const int steps = 28;
            for (int i = 0; i < steps; i++)
            {
                float ang = i / (float)steps * Mathf.PI * 2f;
                float r = 11f + Mathf.Sin(ang * 3f + 0.7f) * 2f + Mathf.Cos(ang * 5f) * 1f;
                pts.Add(new Vector2(Mathf.Cos(ang), Mathf.Sin(ang)) * r);
            }
            return pts;
        }

        private static readonly List<Vector2> LassoLoopPoints = BuildLassoLoopPoints();

        private static readonly List<Seg> BoxOutlineSegs = ClosedLoopSegs(BoxCorners);
        private static readonly List<Seg> LassoOutlineSegs = ClosedLoopSegs(LassoLoopPoints);
        private static readonly List<Seg> BoxDashedSegs = DashPolyline(BoxCorners, 4f, 3f);
        private static readonly List<Seg> LassoDashedSegs = DashPolyline(LassoLoopPoints, 4f, 3f);

        // A jagged "cut here" mark layered over the plain outline - distinguishes Trim (which
        // deletes geometry) from Hide (plain outline, reversible) the same way the panel used to
        // separate them by color alone; here the shape carries it too.
        private static readonly List<Seg> CutMarkSegs = new List<Seg>
        {
            new Seg(new Vector2(-10f, -3f), new Vector2(-3f, 4f)),
            new Seg(new Vector2(-3f, 4f), new Vector2(3f, -4f)),
            new Seg(new Vector2(3f, -4f), new Vector2(10f, 3f)),
        };

        // Open eye (Show All) - two parabolic arcs sharing their endpoints at (+-13, 0), which
        // closes the lens shape with no separate "seam" segment needed.
        private static List<Seg> BuildEyeSegs()
        {
            var segs = new List<Seg>();
            const int steps = 12;
            Vector2 prevTop = default, prevBot = default;
            for (int i = 0; i <= steps; i++)
            {
                float x = Mathf.Lerp(-13f, 13f, i / (float)steps);
                float envelope = 1f - (x / 13f) * (x / 13f);
                Vector2 top = new Vector2(x, 7f * envelope);
                Vector2 bot = new Vector2(x, -4.5f * envelope);
                if (i > 0) { segs.Add(new Seg(prevTop, top)); segs.Add(new Seg(prevBot, bot)); }
                prevTop = top; prevBot = bot;
            }
            return segs;
        }

        private static readonly List<Seg> EyeSegs = BuildEyeSegs();

        private static Sprite _circleSprite, _boxIcon, _lassoIcon, _boxDashedIcon, _lassoDashedIcon;
        private static Sprite _boxTrimIcon, _lassoTrimIcon, _showAllIcon, _invertIcon;

        private static Sprite GetCircleSprite() => _circleSprite ??= Rasterize(p => CircleFill(p, Vector2.zero, 30f));
        private static Sprite GetBoxIcon() => _boxIcon ??= Rasterize(p => SegsStroke(p, BoxOutlineSegs, 2.3f));
        private static Sprite GetLassoIcon() => _lassoIcon ??= Rasterize(p => SegsStroke(p, LassoOutlineSegs, 2.3f));
        private static Sprite GetBoxDashedIcon() => _boxDashedIcon ??= Rasterize(p => SegsStroke(p, BoxDashedSegs, 2f));
        private static Sprite GetLassoDashedIcon() => _lassoDashedIcon ??= Rasterize(p => SegsStroke(p, LassoDashedSegs, 2f));
        private static Sprite GetBoxTrimIcon() => _boxTrimIcon ??= Rasterize(p =>
            Mathf.Max(SegsStroke(p, BoxOutlineSegs, 2.3f), SegsStroke(p, CutMarkSegs, 2.2f)));
        private static Sprite GetLassoTrimIcon() => _lassoTrimIcon ??= Rasterize(p =>
            Mathf.Max(SegsStroke(p, LassoOutlineSegs, 2.3f), SegsStroke(p, CutMarkSegs, 2.2f)));
        private static Sprite GetShowAllIcon() => _showAllIcon ??= Rasterize(p =>
            Mathf.Max(SegsStroke(p, EyeSegs, 2.2f), CircleFill(p, Vector2.zero, 3.5f)));
        private static Sprite GetInvertIcon() => _invertIcon ??= Rasterize(p =>
            Mathf.Max(CircleStroke(p, Vector2.zero, 13f, 2.4f), p.x <= 0f ? CircleFill(p, Vector2.zero, 13f) : 0f));
    }
}
