using UnityEngine;
using UnityEngine.UI;

namespace Sculpting
{
    /// The viewport HUD that follows the pointer and the gestures: the 2D brush cursor ring (with
    /// its halo, centre dot, inner falloff ring and F-drag strength circle), the Lazy Mouse tether, the action toast
    /// (Undo/Redo/Save), the R-hold remesh density gauge, and the box/lasso region marquee and
    /// crosshair. Everything here only READS SculptController's presentation state, every frame.
    ///
    /// Split out of SculptUIBuilder, which used to build all of it as the last children of the
    /// Sculpting Tools panel's own canvas. That had a cost as well as a size: every one of these
    /// elements changes every frame the pointer moves, and a change to any graphic re-batches its
    /// whole canvas - so the panel's ~60 controls were re-batched every frame for a moving ring.
    /// This component draws on its own canvas instead, at sortingOrder 10: above both docked panels
    /// (0), below the radial menus (60), modals (100) and tooltips (1000). It has no raycaster, so
    /// it can never take a click.
    ///
    /// Added by SculptUIBuilder at startup (see SculptUIBuilder.EnsureHud) and handed the same
    /// SculptController.
    public class SculptHudOverlay : MonoBehaviour
    {
        private const string CanvasName = "SculptHudCanvas";

        private SculptController controller;
        private Font _font;

        /// Builds the HUD against `sculptController`. Idempotent: a second call rebuilds it.
        public void Init(SculptController sculptController)
        {
            controller = sculptController;
            _font = UIFactory.Font;
            _regionSelect = controller != null ? controller.RegionSelect : null;
            Build();
        }

        private void Build()
        {
            // Same destroy-by-name idiom as every other runtime-built canvas here (see
            // UIFactory.DestroyStaleCanvas) - a rebuild must not leave two stacked copies.
            GameObject stale = GameObject.Find(CanvasName);
            if (stale != null) DestroyImmediate(stale);

            // Root-level and ConstantPixelSize at scale 1, exactly like SculptCanvas: every element
            // below is positioned in raw screen pixels (RectTransform.position = a screen point).
            var canvasGO = new GameObject(CanvasName, typeof(RectTransform));
            var canvas = canvasGO.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 10;
            var scaler = canvasGO.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ConstantPixelSize;
            scaler.scaleFactor = 1f;

            // Same sibling order as before: the tether immediately BEFORE the ring, so the ring
            // draws over the line rather than the line cutting across the ring's middle; the
            // marquee and crosshair last of all.
            _lazyTetherGO = CreateLazyMouseTether(canvasGO.transform);
            _cursorRingGO = CreateBrushCursor(canvasGO.transform);
            _actionToastGO = CreateActionToast(canvasGO.transform);
            _densityGaugeGO = CreateRemeshDensityGauge(canvasGO.transform);
            _regionMarqueeGO = CreateRegionMarquee(canvasGO.transform);
            _regionCrosshairGO = CreateRegionCrosshair(canvasGO.transform);
        }

        private void OnDestroy()
        {
            GameObject canvas = GameObject.Find(CanvasName);
            if (canvas != null) Destroy(canvas);
        }

        private void Update()
        {
            if (controller == null || _cursorRingGO == null) return;

            // 2D ring cursor - position/size/tint follow the controller every frame it's shown
            // (see SculptController.UpdateBrushCursor, which also owns Cursor.visible).
            bool showCursor = controller.ShowBrushCursor;
            if (_cursorRingGO.activeSelf != showCursor) _cursorRingGO.SetActive(showCursor);
            if (showCursor)
            {
                _cursorRingRect.position = controller.BrushCursorScreenPosition;
                float diameter = controller.BrushCursorScreenDiameter;
                _cursorRingVisualRect.sizeDelta = new Vector2(diameter, diameter);
                _cursorHaloRect.sizeDelta = new Vector2(diameter, diameter) + Vector2.one * (CursorHaloExtraPx * 2f);

                // Smooth swaps in a dashed ring (still stretched to the live diameter above)
                // instead of a different color alone - see SculptController.BrushCursorDashed.
                _cursorRingImage.sprite = controller.BrushCursorDashed ? GetDashedRingSprite() : GetRingSprite();

                // Stroke-end pulse (see SculptController.BrushCursorFadeAlpha) multiplies
                // every layer's OWN base alpha rather than being baked into
                // BrushCursorColor - that color's alpha is always 1, so the halo (fixed
                // 0.55) and the tinted ring/dot (1) fade together in proportion instead of
                // the halo swallowing the multiplier at a different rate.
                float fade = controller.BrushCursorFadeAlpha;
                Color c = controller.BrushCursorColor;
                c.a *= fade;
                _cursorRingImage.color = c;
                _cursorDotImage.color = c;
                _cursorHaloImage.color = new Color(0f, 0f, 0f, 0.55f * fade);

                // Inner strength circle - only while holding F (see
                // SculptController.IsAdjustingStrength). 0 strength reads as a tiny dot
                // (floored at CursorDotSizePx so it never fully vanishes), scaling up
                // linearly until max strength exactly fills the outer ring - re-deriving
                // the diameter from the live outer `diameter` above (rather than caching a
                // pixel size) is what keeps it correct across brush-size changes too.
                bool showStrength = controller.IsAdjustingStrength;
                _cursorStrengthImage.enabled = showStrength;
                if (showStrength)
                {
                    float st01 = Mathf.InverseLerp(0.01f, 1f, controller.BrushStrength);
                    float strengthDiameter = Mathf.Max(CursorDotSizePx, diameter * st01);
                    _cursorStrengthRect.sizeDelta = new Vector2(strengthDiameter, strengthDiameter);
                    Color sc = StrengthCircleColor;
                    sc.a = StrengthCircleBaseAlpha * fade;
                    _cursorStrengthImage.color = sc;
                }

                // Inner falloff ring (see SculptController.BrushCursorFalloffRadius01) - the
                // ZBrush focal circle. Scaled against the outer ring's VISIBLE radius, not the
                // rect's: the ring sprite's band sits a little inside its square, and the two
                // circles are read against each other. Brightened while D scrubs it.
                float falloff01 = controller.BrushCursorFalloffRadius01;
                bool showFalloff = falloff01 >= 0f;
                if (_cursorFalloffRing.enabled != showFalloff) _cursorFalloffRing.enabled = showFalloff;
                if (showFalloff)
                {
                    bool adjusting = controller.IsAdjustingFocalShift;
                    float radius = Mathf.Max(FalloffRingMinRadiusPx, diameter * 0.5f * RingSpriteBandFraction * falloff01);
                    _cursorFalloffRing.Set(radius, adjusting ? 2f : 1.5f, controller.BrushCursorDashed);
                    Color fc = controller.BrushCursorColor;
                    fc.a *= (adjusting ? 1f : FalloffRingBaseAlpha) * fade;
                    _cursorFalloffRing.color = fc;
                }
            }

            // Lazy Mouse tether - the line from the ring back to the pointer, drawn only while
            // the stabilizer actually has the rope taut (see SculptController's tether remarks).
            if (_lazyTetherGO != null)
            {
                bool showTether = controller.LazyMouseTetherActive;
                if (_lazyTetherGO.activeSelf != showTether) _lazyTetherGO.SetActive(showTether);
                if (showTether)
                {
                    Vector2 from = controller.LazyMouseTetherFrom; // raw pointer
                    Vector2 to = controller.LazyMouseTetherTo;     // where the brush is working
                    Vector2 span = from - to;
                    float length = span.magnitude;

                    _lazyTetherLineRect.position = to;
                    _lazyTetherLineRect.sizeDelta = new Vector2(length, LazyTetherThicknessPx);
                    // Atan2 rather than Vector2.Angle: the latter is unsigned, so the line would
                    // mirror onto the wrong side of the ring for half of all cursor directions.
                    _lazyTetherLineRect.localEulerAngles =
                        new Vector3(0f, 0f, Mathf.Atan2(span.y, span.x) * Mathf.Rad2Deg);
                    _lazyTetherDotRect.position = from;

                    // Same tint as the ring (so polarity and Smooth's blue read consistently
                    // across both) at a fixed lower alpha, and multiplied by the same stroke-end
                    // fade so the whole cursor assembly pulses as one thing rather than the line
                    // outliving the ring it belongs to.
                    Color tc = controller.BrushCursorColor;
                    tc.a = LazyTetherAlpha * controller.BrushCursorFadeAlpha;
                    _lazyTetherLineImage.color = tc;
                    _lazyTetherDotImage.color = tc;
                }
            }

            // Action toast (Undo/Redo/Save/Save As) - see SculptController.ShowActionToast and
            // friends.
            if (_actionToastGO != null)
            {
                bool showToast = controller.ShowActionToast;
                if (_actionToastGO.activeSelf != showToast) _actionToastGO.SetActive(showToast);
                if (showToast)
                {
                    _actionToastLabel.text = controller.ActionToastText;
                    _actionToastLabel.color = new Color(1f, 1f, 1f, controller.ActionToastAlpha);
                    float lift = controller.ActionToastProgress01 * ActionToastRiseDistancePx;
                    _actionToastRect.anchoredPosition = new Vector2(0f, ActionToastBaseY + lift);
                }
            }

            RefreshRegionState();
            UpdateDensityGauge();
        }

        // Box/lasso hide and mask (see RegionSelectTool) - the panel buttons that used to arm
        // these moved to the shift-Space radial menu (RegionRadialMenuUIBuilder). This field is
        // all that's left here: UpdateRegionMarquee/UpdateRegionCrosshair below still need it to
        // draw the drag marquee and armed-mode crosshair, which are unrelated to the panel.
        private RegionSelectTool _regionSelect;

        private GameObject _regionMarqueeGO;

        private RegionMarqueeGraphic _regionMarquee;

        // The crosshair shown in place of the brush ring while a region mode is armed (see
        // SculptController.ShowRegionCrosshair). Four arms with a gap at the centre rather than
        // two crossing lines: the gap leaves the exact point you are aiming at unobscured, which
        // is the whole reason a marquee tool draws a crosshair instead of a dot.
        private GameObject _regionCrosshairGO;

        private RectTransform _regionCrosshairRect;

        private readonly Image[] _regionCrosshairArms = new Image[4];

        private const float CrosshairArmLengthPx = 9f;

        private const float CrosshairGapPx = 3f;

        private const float CrosshairThicknessPx = 1.5f;

        // HUD gauge for the R-hold remesh gesture - a label + slider frozen at the screen
        // position R went down at (same anchor idiom as the S/F gauges' frozen ring), same
        // "controller/tool owns the state, this just follows it" idiom as the region status
        // label above.
        private GameObject _densityGaugeGO;

        private RectTransform _densityGaugeRect;

        private Text _densityGaugeLabel;

        private Slider _densityGaugeSlider;

        private const float DensityGaugeOffsetYPx = 40f;

        private int _lastShownDensity = -1;

        // ZBrush/Blender-style 2D ring cursor (see SculptController.ShowBrushCursor and
        // friends) - a halo (dark, slightly larger, for contrast against any background), a
        // tinted ring at the actual brush diameter (brush size - S to resize, or scroll), a
        // small fixed-size center dot for precision, and an inner filled circle that only
        // appears while holding F (SculptController.IsAdjustingStrength), scaled from a tiny
        // dot up to the full ring diameter to show BrushStrength - see Update(). No standalone
        // popup gauges anymore for either value: the ring itself IS the size readout, and the
        // inner circle IS the strength readout. Replaces the old world-space BrushPreview
        // sphere entirely; the OS cursor (Cursor.visible) is toggled opposite this by
        // SculptController itself, not here.
        private GameObject _cursorRingGO;

        private RectTransform _cursorRingRect;

        private Image _cursorHaloImage, _cursorRingImage, _cursorDotImage, _cursorStrengthImage;

        private RectTransform _cursorHaloRect, _cursorRingVisualRect, _cursorStrengthRect;

        private const float CursorHaloExtraPx = 3f;

        private const float CursorDotSizePx = 4f;

        // The inner falloff ring - see Update(). Drawn fainter than the outer ring it sits in,
        // so size stays the first thing the cursor says; floored so it never collapses onto the
        // centre dot.
        private CircleOutlineGraphic _cursorFalloffRing;

        private const float FalloffRingBaseAlpha = 0.6f;

        private const float FalloffRingMinRadiusPx = 4f;

        // Where GetRingSprite's band is centred, as a fraction of the sprite's half-width.
        private const float RingSpriteBandFraction = (RingSpriteSize * 0.5f - RingSpriteThickness * 0.5f - 1f) / (RingSpriteSize * 0.5f);

        private const int RingSpriteSize = 128;

        private const float RingSpriteThickness = 6f;

        // Lazy Mouse tether (see SculptController.LazyMouseTetherActive) - a thin line from the
        // ring, which sits where the brush is actually working, back to a small dot at the raw
        // pointer, exactly the affordance ZBrush and Nomad draw for their own stabilizers. Two
        // independent children rather than a positioned parent with local offsets: the line is
        // rotated and stretched while the dot is neither, so sharing a parent transform would
        // mean undoing the parent's rotation on the dot every frame.
        //
        // Deliberately understated - thin, translucent, no halo. It is a running readout of a
        // gap the user is already looking straight at, and anything heavier would compete with
        // the ring for attention exactly when they are concentrating on a stroke.
        private GameObject _lazyTetherGO;

        private RectTransform _lazyTetherLineRect, _lazyTetherDotRect;

        private Image _lazyTetherLineImage, _lazyTetherDotImage;

        private const float LazyTetherThicknessPx = 1.5f;

        private const float LazyTetherDotSizePx = 5f;

        private const float LazyTetherAlpha = 0.5f;

        // Action toast (Undo/Redo/Save/Save As - see SculptController.ShowActionToast and
        // friends) - a short-lived text popup, independent of the brush cursor above, anchored
        // bottom-center of the whole screen (clear of both docked side panels regardless of
        // window width) rather than trying to reproduce any one exact spot in the viewport.
        private GameObject _actionToastGO;

        private RectTransform _actionToastRect;

        private Text _actionToastLabel;

        private const float ActionToastBaseY = 60f;

        private const float ActionToastRiseDistancePx = 24f;

        // Inner strength-circle color - deliberately the same red as SculptController's own
        // NegativeColor (private to that class) rather than reusing BrushCursorColor: the ring
        // keeps its ordinary polarity/Smooth tint while adjusting strength (see
        // SculptController.UpdateBrushCursor), so the circle needs its own fixed "this is the
        // strength gesture" tint independent of whatever color the outer ring happens to be.
        private static readonly Color StrengthCircleColor = new Color(1f, 0.3f, 0.3f);

        // Kept translucent (rather than the fully-opaque disc a bare Color gives by default) so
        // it reads as a HUD overlay - the same reasoning as the halo's fixed 0.55 alpha below.
        private const float StrengthCircleBaseAlpha = 0.45f;

        /// Follows the tool once per frame: the drag marquee and armed-mode crosshair are the
        /// only things left here that depend on RegionSelectTool state - arming a mode itself now
        /// happens from the shift-Space radial menu (RegionRadialMenuUIBuilder), not this panel.
        private void RefreshRegionState()
        {
            if (_regionSelect == null) return;
            UpdateRegionMarquee();
            UpdateRegionCrosshair();
        }

        /// The armed-mode pointer. Tinted like the mode it belongs to (teal for hide, orange for
        /// mask, red for trim) so the crosshair says WHICH gesture is armed, not merely that one
        /// is - the panel's highlighted button is the other half of that, and it can be scrolled
        /// out of sight or collapsed inside its foldout. It matters most for trim, where the
        /// crosshair is the only thing on screen saying the next drag deletes geometry.
        private void UpdateRegionCrosshair()
        {
            if (_regionCrosshairGO == null) return;

            bool show = controller.ShowRegionCrosshair;
            if (_regionCrosshairGO.activeSelf != show) _regionCrosshairGO.SetActive(show);
            if (!show) return;

            _regionCrosshairRect.position = controller.RegionCrosshairScreenPosition;
            Color tint = _regionSelect == null ? UIFactory.MaskActiveColor
                       : _regionSelect.IsTrimMode ? UIFactory.RegionTrimActiveColor
                       : _regionSelect.IsHideMode ? UIFactory.RegionHideActiveColor
                       : UIFactory.MaskActiveColor;
            for (int i = 0; i < _regionCrosshairArms.Length; i++)
                if (_regionCrosshairArms[i] != null) _regionCrosshairArms[i].color = tint;
        }

        private GameObject CreateRegionCrosshair(Transform canvasParent)
        {
            var go = new GameObject("RegionCrosshair", typeof(RectTransform));
            go.transform.SetParent(canvasParent, false);
            _regionCrosshairRect = go.GetComponent<RectTransform>();
            _regionCrosshairRect.anchorMin = _regionCrosshairRect.anchorMax = Vector2.zero;
            _regionCrosshairRect.pivot = new Vector2(0.5f, 0.5f);
            _regionCrosshairRect.sizeDelta = Vector2.zero;

            float offset = CrosshairGapPx + CrosshairArmLengthPx * 0.5f;
            var arms = new[]
            {
                (new Vector2(offset, 0f), new Vector2(CrosshairArmLengthPx, CrosshairThicknessPx)),
                (new Vector2(-offset, 0f), new Vector2(CrosshairArmLengthPx, CrosshairThicknessPx)),
                (new Vector2(0f, offset), new Vector2(CrosshairThicknessPx, CrosshairArmLengthPx)),
                (new Vector2(0f, -offset), new Vector2(CrosshairThicknessPx, CrosshairArmLengthPx)),
            };
            for (int i = 0; i < arms.Length; i++)
            {
                var armGO = new GameObject("Arm" + i, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                armGO.transform.SetParent(go.transform, false);
                var armRect = armGO.GetComponent<RectTransform>();
                armRect.anchorMin = armRect.anchorMax = new Vector2(0.5f, 0.5f);
                armRect.anchoredPosition = arms[i].Item1;
                armRect.sizeDelta = arms[i].Item2;
                // No sprite: an Image with a null sprite draws a flat filled rect, which is all
                // a 1.5px arm is - same as the Lazy Mouse tether line.
                _regionCrosshairArms[i] = armGO.GetComponent<Image>();
                _regionCrosshairArms[i].raycastTarget = false;
            }

            go.SetActive(false);
            return go;
        }

        /// Feeds the marquee overlay this frame's shape and tint. Tint carries the two
        /// modifiers, so what the release will do is legible mid-drag rather than only after:
        /// red for a reversing drag (RMB/Ctrl), and washed toward white while Shift has it
        /// acting on everything outside the shape.
        private void UpdateRegionMarquee()
        {
            if (_regionMarqueeGO == null) return;

            bool show = _regionSelect.IsDragging;
            if (_regionMarqueeGO.activeSelf != show) _regionMarqueeGO.SetActive(show);
            if (!show) return;

            // Trim keeps its own red whichever way the modifiers point: for hide and mask, red
            // means "this drag takes something away", and for trim that is true of BOTH
            // directions - only which side goes changes.
            Color tint = _regionSelect.IsTrimMode ? UIFactory.RegionTrimActiveColor
                : _regionSelect.DragRemoves ? UIFactory.RegionRemoveColor
                : _regionSelect.IsHideMode ? UIFactory.RegionHideActiveColor
                : UIFactory.MaskActiveColor;
            if (_regionSelect.DragActsOnOutside) tint = Color.Lerp(tint, Color.white, 0.4f);
            _regionMarquee.color = tint;

            if (_regionSelect.IsLassoMode) _regionMarquee.SetPath(_regionSelect.LassoPoints);
            else _regionMarquee.SetBox(_regionSelect.DragRect);
        }

        // Stretched over the whole canvas with a (0,0) pivot so its local coordinate space IS
        // screen pixels - see RegionMarqueeGraphic, which draws the tool's screen-space points
        // straight into it with no conversion.
        private GameObject CreateRegionMarquee(Transform canvasParent)
        {
            // CanvasRenderer listed explicitly rather than left to RequireComponent: a Graphic
            // built through the GameObject constructor does not inherit the base class's
            // attribute, and one without a renderer draws nothing at all while reporting no
            // error (see RegionMarqueeGraphic's remarks).
            var go = new GameObject("RegionMarquee", typeof(RectTransform), typeof(CanvasRenderer), typeof(RegionMarqueeGraphic));
            go.transform.SetParent(canvasParent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = Vector2.zero;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;

            _regionMarquee = go.GetComponent<RegionMarqueeGraphic>();
            _regionMarquee.raycastTarget = false;
            go.SetActive(false);
            return go;
        }

        // Halo (dark, slightly larger) + tinted ring at the live brush diameter + a small
        // fixed-size center dot - all centered on one positioning parent so Update() only has
        // to move/resize that one RectTransform's children, not juggle three independent
        // positions. raycastTarget is off on all three: this sits on top of everything in
        // sibling order (see BuildUI), and a hit-testable cursor would make itself count as
        // "over UI" the instant it appeared under the mouse, fighting the very check
        // (SculptController._isOverUI) that decides whether to show it at all.
        // The Lazy Mouse tether - see the field remarks. The line's pivot is its LEFT edge
        // (0, 0.5) so Update() can anchor it at the ring, point it at the cursor with a single
        // Z rotation, and set its length as plain width; a centred pivot would need the
        // midpoint computed every frame as well. No sprite on the line at all: an Image with a
        // null sprite draws a flat filled rect, which is exactly what a 1.5px line is.
        private GameObject CreateLazyMouseTether(Transform canvasParent)
        {
            var go = new GameObject("LazyMouseTether", typeof(RectTransform));
            go.transform.SetParent(canvasParent, false);
            var root = go.GetComponent<RectTransform>();
            root.anchorMin = root.anchorMax = Vector2.zero;
            root.sizeDelta = Vector2.zero;

            var lineGO = new GameObject("Line", typeof(RectTransform), typeof(Image));
            lineGO.transform.SetParent(go.transform, false);
            _lazyTetherLineRect = lineGO.GetComponent<RectTransform>();
            _lazyTetherLineRect.anchorMin = _lazyTetherLineRect.anchorMax = Vector2.zero;
            _lazyTetherLineRect.pivot = new Vector2(0f, 0.5f);
            _lazyTetherLineImage = lineGO.GetComponent<Image>();
            _lazyTetherLineImage.raycastTarget = false;

            var dotGO = new GameObject("PointerDot", typeof(RectTransform), typeof(Image));
            dotGO.transform.SetParent(go.transform, false);
            _lazyTetherDotRect = dotGO.GetComponent<RectTransform>();
            _lazyTetherDotRect.anchorMin = _lazyTetherDotRect.anchorMax = Vector2.zero;
            _lazyTetherDotRect.pivot = new Vector2(0.5f, 0.5f);
            _lazyTetherDotRect.sizeDelta = new Vector2(LazyTetherDotSizePx, LazyTetherDotSizePx);
            _lazyTetherDotImage = dotGO.GetComponent<Image>();
            _lazyTetherDotImage.sprite = GetDotSprite();
            _lazyTetherDotImage.raycastTarget = false;

            go.SetActive(false);
            return go;
        }

        private GameObject CreateBrushCursor(Transform canvasParent)
        {
            var go = new GameObject("BrushCursorRing", typeof(RectTransform));
            go.transform.SetParent(canvasParent, false);
            _cursorRingRect = go.GetComponent<RectTransform>();
            _cursorRingRect.anchorMin = _cursorRingRect.anchorMax = new Vector2(0f, 0f);
            _cursorRingRect.pivot = new Vector2(0.5f, 0.5f);
            _cursorRingRect.sizeDelta = Vector2.zero;

            var haloGO = new GameObject("Halo", typeof(RectTransform), typeof(Image));
            haloGO.transform.SetParent(go.transform, false);
            _cursorHaloRect = haloGO.GetComponent<RectTransform>();
            _cursorHaloRect.anchorMin = _cursorHaloRect.anchorMax = new Vector2(0.5f, 0.5f);
            _cursorHaloImage = haloGO.GetComponent<Image>();
            _cursorHaloImage.sprite = GetRingSprite();
            _cursorHaloImage.color = new Color(0f, 0f, 0f, 0.55f);
            _cursorHaloImage.raycastTarget = false;

            var ringGO = new GameObject("Ring", typeof(RectTransform), typeof(Image));
            ringGO.transform.SetParent(go.transform, false);
            _cursorRingVisualRect = ringGO.GetComponent<RectTransform>();
            _cursorRingVisualRect.anchorMin = _cursorRingVisualRect.anchorMax = new Vector2(0.5f, 0.5f);
            _cursorRingImage = ringGO.GetComponent<Image>();
            _cursorRingImage.sprite = GetRingSprite();
            _cursorRingImage.raycastTarget = false;

            // Inner strength circle, between the ring and the center dot in sibling order so
            // the precision dot always stays visible on top even when strength is near max and
            // this circle nearly fills the ring. Reuses GetDotSprite (a generic filled-circle
            // texture, not exclusive to the small dot below) since Image.type Simple stretches
            // it to whatever sizeDelta Update() sets, same as the dot does at its own fixed
            // size. Starts disabled (Update() only enables it while IsAdjustingStrength).
            var strengthGO = new GameObject("StrengthCircle", typeof(RectTransform), typeof(Image));
            strengthGO.transform.SetParent(go.transform, false);
            _cursorStrengthRect = strengthGO.GetComponent<RectTransform>();
            _cursorStrengthRect.anchorMin = _cursorStrengthRect.anchorMax = new Vector2(0.5f, 0.5f);
            _cursorStrengthImage = strengthGO.GetComponent<Image>();
            _cursorStrengthImage.sprite = GetDotSprite();
            _cursorStrengthImage.raycastTarget = false;
            _cursorStrengthImage.enabled = false;

            // Inner falloff ring, above the strength disc so it stays readable through it while
            // F is held, and under the centre dot. Zero-size rect: the graphic draws around its
            // pivot in pixels.
            var falloffGO = new GameObject("FalloffRing", typeof(RectTransform), typeof(CanvasRenderer), typeof(CircleOutlineGraphic));
            falloffGO.transform.SetParent(go.transform, false);
            var falloffRect = falloffGO.GetComponent<RectTransform>();
            falloffRect.anchorMin = falloffRect.anchorMax = new Vector2(0.5f, 0.5f);
            falloffRect.sizeDelta = Vector2.zero;
            _cursorFalloffRing = falloffGO.GetComponent<CircleOutlineGraphic>();
            _cursorFalloffRing.raycastTarget = false;

            var dotGO = new GameObject("Dot", typeof(RectTransform), typeof(Image));
            dotGO.transform.SetParent(go.transform, false);
            var dotRect = dotGO.GetComponent<RectTransform>();
            dotRect.anchorMin = dotRect.anchorMax = new Vector2(0.5f, 0.5f);
            dotRect.sizeDelta = new Vector2(CursorDotSizePx, CursorDotSizePx);
            _cursorDotImage = dotGO.GetComponent<Image>();
            _cursorDotImage.sprite = GetDotSprite();
            _cursorDotImage.raycastTarget = false;

            go.SetActive(false);
            return go;
        }

        private static Sprite _ringSprite;

        private static Sprite _dotSprite;

        // Procedural ring texture: an antialiased circular band near the edge of a 128x128
        // square, alpha elsewhere zero. Generated once and cached - Image.type Simple stretches
        // it to whatever sizeDelta Update() sets, so one texture serves every brush radius.
        private static Sprite GetRingSprite()
        {
            if (_ringSprite != null) return _ringSprite;
            const int size = RingSpriteSize;
            const float thickness = RingSpriteThickness;
            float outerR = size * 0.5f - thickness * 0.5f - 1f;
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float distFromRing = Mathf.Abs(d - outerR);
                    float alpha = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(thickness * 0.5f - 1f, thickness * 0.5f + 1f, distFromRing));
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            _ringSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return _ringSprite;
        }

        private static Sprite _dashedRingSprite;

        // Same ring band as GetRingSprite, further cut into evenly-spaced segments by angle -
        // Smooth's cursor style (see SculptController.BrushCursorDashed). A hard on/off cut
        // (no antialiasing across the segment edges) rather than smoothing them too: at the
        // cursor's typical on-screen size the crisp edge reads as a dash, not a rendering seam.
        private static Sprite GetDashedRingSprite()
        {
            if (_dashedRingSprite != null) return _dashedRingSprite;
            const int size = 128;
            const float thickness = 6f;
            const int dashCount = 14;
            const float dashOnFraction = 0.6f;
            float outerR = size * 0.5f - thickness * 0.5f - 1f;
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    Vector2 p = new Vector2(x + 0.5f, y + 0.5f) - center;
                    float distFromRing = Mathf.Abs(p.magnitude - outerR);
                    float ringAlpha = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(thickness * 0.5f - 1f, thickness * 0.5f + 1f, distFromRing));

                    float angle01 = Mathf.Repeat(Mathf.Atan2(p.y, p.x) / (Mathf.PI * 2f), 1f);
                    float dashPhase = Mathf.Repeat(angle01 * dashCount, 1f);
                    float dashAlpha = dashPhase < dashOnFraction ? 1f : 0f;

                    pixels[y * size + x] = new Color(1f, 1f, 1f, ringAlpha * dashAlpha);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            _dashedRingSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return _dashedRingSprite;
        }

        // Small filled-circle texture for the center dot, same antialiasing approach as the
        // ring above.
        private static Sprite GetDotSprite()
        {
            if (_dotSprite != null) return _dotSprite;
            const int size = 32;
            float radius = size * 0.5f - 1f;
            Vector2 center = new Vector2(size * 0.5f, size * 0.5f);

            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                    float alpha = 1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(radius - 1f, radius + 1f, d));
                    pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply();

            _dotSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
            return _dotSprite;
        }

        // Bottom-center of the whole screen (not the brush cursor's canvas position) - clear of
        // both docked side panels regardless of window width, and readable without competing
        // with the ring cursor up near the mouse. raycastTarget off, same reasoning as the ring
        // cursor's own children: this sits on top in sibling order and must never itself count
        // as "over UI".
        private GameObject CreateActionToast(Transform canvasParent)
        {
            var go = new GameObject("ActionToastLabel", typeof(RectTransform));
            go.transform.SetParent(canvasParent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0f);
            rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, ActionToastBaseY);
            rect.sizeDelta = new Vector2(320f, 40f);

            var text = go.AddComponent<Text>();
            text.font = _font;
            text.fontSize = 22;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;

            _actionToastRect = rect;
            _actionToastLabel = text;
            go.SetActive(false);
            return go;
        }

        /// Frozen at the screen position R went down at (see SculptController's
        /// RemeshDensityAnchorScreenPosition) rather than following a world-space object the way
        /// the old grid preview did - same "grows/shrinks in place" idiom the S/F gauges already
        /// use for their own frozen ring, just as a label+slider instead of a ring.
        private GameObject CreateRemeshDensityGauge(Transform canvasParent)
        {
            var go = new GameObject("RemeshDensityGauge", typeof(RectTransform));
            go.transform.SetParent(canvasParent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 0f); // bottom-center pivot: sits just above the anchor point
            rect.sizeDelta = new Vector2(200f, 52f);
            _densityGaugeRect = rect;

            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 4f;
            vlg.childAlignment = TextAnchor.LowerCenter;
            vlg.childControlHeight = true;
            vlg.childControlWidth = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;

            var textGO = new GameObject("Label", typeof(RectTransform));
            textGO.transform.SetParent(go.transform, false);
            textGO.AddComponent<LayoutElement>().preferredHeight = 24;
            _densityGaugeLabel = textGO.AddComponent<Text>();
            _densityGaugeLabel.font = _font;
            _densityGaugeLabel.fontSize = 18;
            _densityGaugeLabel.fontStyle = FontStyle.Bold;
            _densityGaugeLabel.alignment = TextAnchor.MiddleCenter;
            _densityGaugeLabel.color = Color.white;
            _densityGaugeLabel.raycastTarget = false;

            _densityGaugeSlider = UIFactory.CreateCompactSlider(go.transform, 4f, SculptController.MaxRemeshResolution, controller.RemeshResolution, _ => { });
            // Read-only readout, not a control - the value comes from the R-drag's own
            // horizontal mouse delta (HandleRemeshDensityKey), and letting this also be
            // click-dragged would fight that same frame's delta-driven value.
            _densityGaugeSlider.interactable = false;

            go.SetActive(false);
            return go;
        }

        private void UpdateDensityGauge()
        {
            if (_densityGaugeGO == null) return;

            bool show = controller.IsAdjustingRemeshDensity;
            if (_densityGaugeGO.activeSelf != show) _densityGaugeGO.SetActive(show);
            if (!show) return;

            Vector2 anchor = controller.RemeshDensityAnchorScreenPosition;
            _densityGaugeRect.position = new Vector3(anchor.x, anchor.y + DensityGaugeOffsetYPx, 0f);

            int density = controller.RemeshResolution;
            _densityGaugeSlider.SetValueWithoutNotify(density);
            // Only-on-change, same reasoning as the poly count label elsewhere - this runs every
            // frame the gauge is up, and a fresh concatenation for a value that hasn't moved
            // since last frame is wasted garbage.
            if (density != _lastShownDensity)
            {
                _lastShownDensity = density;
                _densityGaugeLabel.text = "Density: " + density;
            }
        }
    }
}
