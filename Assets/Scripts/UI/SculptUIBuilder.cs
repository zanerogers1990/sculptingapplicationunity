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

        // ActiveColor/InactiveColor/PanelColor live on UIFactory, which already publishes
        // them - this file used to redeclare all three with identical values.
        // Distinct from UIFactory.ActiveColor above since mask-paint mode is orthogonal to brush selection
        // (which brush is "current" still matters for when you exit mask mode) - a different
        // color keeps the two kinds of highlight from reading as the same kind of state.
        private static readonly Color MaskActiveColor = new Color(0.95f, 0.65f, 0.15f);

        // Region tools (see RegionSelectTool). The hide gestures get their own teal rather than
        // sharing MaskActiveColor: hiding and masking are different kinds of state that happen
        // to share a gesture, and one armed-tool color for both would make it easy to draw a
        // box expecting one and get the other. The mask gestures DO share MaskActiveColor,
        // since they edit exactly what the Mask button edits.
        private static readonly Color RegionHideActiveColor = new Color(0.3f, 0.75f, 0.8f);
        // The marquee's tint while the drag would REMOVE (show/unmask) - the same red the
        // brushes already use for their negative/erase polarity.
        private static readonly Color RegionRemoveColor = new Color(1f, 0.3f, 0.3f);

        // Matches Unity's axis-handle/gizmo convention (X red, Y green, Z blue), and
        // MirrorController's own plane colors.
        private static readonly Color MirrorXColor = new Color(1f, 0.25f, 0.25f);
        private static readonly Color MirrorYColor = new Color(0.35f, 1f, 0.35f);
        private static readonly Color MirrorZColor = new Color(0.3f, 0.55f, 1f);

        // Symmetry Repair status line: the same green/grey pairing the extract status already
        // uses for "an action reported success" versus "here is what this section does".
        private static readonly Color SymmetryOkColor = new Color(0.55f, 0.85f, 0.55f);
        private static readonly Color SymmetryHintColor = new Color(0.65f, 0.65f, 0.7f);

        private Font _font;
        private Text _positiveToggleLabel;
        private Toggle _positiveToggle;
        private Text _accumulateToggleLabel;
        private Toggle _accumulateToggle;
        private Slider _accumulateStrengthSlider;
        private Text _frontFacingOnlyToggleLabel;
        private Toggle _frontFacingOnlyToggle;
        private Image _moveButtonImage;
        private Image _clayButtonImage;
        private Image _smoothButtonImage;
        private Image _creaseButtonImage;
        private Image _damButtonImage;
        private Image _inflateButtonImage;
        private Image _flattenButtonImage;
        private Image _poseButtonImage;
        private Image _maskButtonImage;
        private bool _lastShownMaskMode;

        // Box/lasso hide and mask (see BuildRegionSection). The tool owns all the state; these
        // are the controls whose highlight, enabled-ness and status text have to follow it.
        private RegionSelectTool _regionSelect;
        private Image _boxHideButtonImage, _lassoHideButtonImage, _boxMaskButtonImage, _lassoMaskButtonImage;
        private Button _showAllButton, _invertVisibleButton;
        private Text _regionStatusLabel;
        private RegionSelectMode _lastShownRegionMode = (RegionSelectMode)(-1);
        private string _lastShownRegionStatus = "\0"; // sentinel: never equal to a real value, so the first poll draws
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

        // Resynced every frame like _brushSizeSlider - RemeshResolution can now change from the
        // R-hold gauge as well as this slider, so leaving it un-polled would go stale the first
        // time someone used the hotkey.
        private Slider _remeshResolutionSlider;

        // Density readout for the R-hold remesh gauge (see RemeshDensityGrid) - anchored under
        // the world-space grid's screen projection, same "controller/tool owns the state, this
        // just follows it" idiom as the region status label above.
        private RemeshDensityGrid _densityGrid;
        private GameObject _densityLabelGO;
        private RectTransform _densityLabelRect;
        private Text _densityLabelText;
        private const float DensityLabelOffsetYPx = -28f;
        private int _lastShownDensity = -1;
        private Slider _brushSizeSlider;
        // Was previously created but never captured, so this slider went stale the moment a
        // hotkey (or the F-drag gauge below) changed controller.BrushStrength out from under
        // it - each brush remembers its own strength (SculptController._brushStrengthPerType),
        // so switching brushes silently changed the ACTUAL value while the panel kept showing
        // whatever the last brush had. Now resynced every frame alongside _brushSizeSlider.
        private Slider _brushStrengthSlider;
        private Text _polyCountLabel;
        private Text _exportStatusLabel;
        private int _lastShownTriCount = -1, _lastShownVertCount = -1;
        private Button _undoButton, _redoButton;
        private BrushType _lastShownBrush = (BrushType)(-1);

        // Mirroring is per-object (each object reflects through its own origin - see
        // MirrorController), but these toggles are built once and then point at whatever is
        // selected WHEN THEY ARE CLICKED. Without a resync they kept showing the state of
        // whichever object happened to be selected at build time, so after switching objects
        // the ticks were simply lying about the selection - and unticking one then wrote
        // "off" to an object that was already off while the plane you could actually see
        // (belonging to the previously-selected object) stayed up. Polled against
        // SelectionManager.SelectionVersion below, same once-per-frame idiom as the brush
        // buttons above.
        private Toggle _mirrorXToggle, _mirrorYToggle, _mirrorZToggle, _showPlanesToggle;
        private SelectionManager _selection;
        private int _lastShownSelectionVersion = -1;

        // Mirror X can now also flip from the X hotkey (see SculptController.HandleBrushSwitchKeys),
        // so the toggle needs the same per-frame value sync the brush buttons get, not just a
        // resync on selection change - see RefreshMirrorToggles.
        private bool _lastShownMirrorX, _lastShownMirrorY, _lastShownMirrorZ, _lastShownShowPlanes;
        private bool _mirrorTogglesShown;

        // Mask extract (see BuildExtractSection). The controller owns all the state; these are
        // just the controls whose enabled-ness and text have to follow it.
        // History depth control and its live cost readout - see BuildUI's undo row.
        private Text _historyLabel;
        private float _nextHistoryRefresh;

        private MaskExtractController _extract;
        private Button _extractAcceptButton, _extractCancelButton;

        // Symmetry repair (see BuildSymmetrySection). The status line is deliberately NOT polled
        // every frame like the extract one above: producing it means building a whole vertex
        // correspondence map (SymmetryOps.Status), which is O(vertex count) and would cost a
        // full pass over a multi-million-vertex sculpt on every single frame just to redraw a
        // line of text. It is written on demand instead - when Check Symmetry is pressed, and
        // after each repair, which are exactly the moments the number can have changed.
        private Image[] _symmetryAxisImages = new Image[3];
        private Text _symmetryStatusLabel;
        private Text _symPosToNegLabel, _symNegToPosLabel;
        private Text _symCutPosToNegLabel, _symCutNegToPosLabel;
        private int _lastShownSymmetryAxis = -1;
        private Text _extractStatusLabel;
        private bool _lastShownExtractPreviewing;
        private int _lastShownExtractTris = -1;
        private string _lastShownExtractError = "\0"; // sentinel: never equal to a real value, so the first poll always draws

        private static readonly BrushAlphaType[] AlphaTypes =
        {
            BrushAlphaType.SoftCircle, BrushAlphaType.Noise, BrushAlphaType.Bumps,
            BrushAlphaType.Ridges, BrushAlphaType.HardSquare
        };
        private readonly Image[] _alphaButtonImages = new Image[AlphaTypes.Length];

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

        // Inner strength-circle color - deliberately the same red as SculptController's own
        // NegativeColor (private to that class) rather than reusing BrushCursorColor: the ring
        // keeps its ordinary polarity/Smooth tint while adjusting strength (see
        // SculptController.UpdateBrushCursor), so the circle needs its own fixed "this is the
        // strength gesture" tint independent of whatever color the outer ring happens to be.
        private static readonly Color StrengthCircleColor = new Color(1f, 0.3f, 0.3f);
        // Kept translucent (rather than the fully-opaque disc a bare Color gives by default) so
        // it reads as a HUD overlay - the same reasoning as the halo's fixed 0.55 alpha below.
        private const float StrengthCircleBaseAlpha = 0.45f;

        private void Update()
        {
            if (controller == null || _cursorRingGO == null) return;

            // Panel's own Brush Size/Strength sliders stay in sync with S-drag/F-drag/scroll
            // adjustments made straight from the viewport - SetValueWithoutNotify avoids
            // feeding the change back into the controller through the slider's own
            // onValueChanged.
            if (_brushSizeSlider != null) _brushSizeSlider.SetValueWithoutNotify(controller.BrushRadius);
            if (_remeshResolutionSlider != null) _remeshResolutionSlider.SetValueWithoutNotify(controller.RemeshResolution);
            if (_brushStrengthSlider != null) _brushStrengthSlider.SetValueWithoutNotify(controller.BrushStrength);

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

                // Same per-brush memory for Front Facing Only (see
                // SculptController._brushFrontFacingOnly) - e.g. turning it on for Clay must not
                // leave it on the next time Move is picked up.
                if (_frontFacingOnlyToggle != null)
                {
                    _frontFacingOnlyToggle.SetIsOnWithoutNotify(controller.FrontFacingOnly);
                    _frontFacingOnlyToggleLabel.text = controller.FrontFacingOnly ? "Front Facing Only" : "Front Facing Only (Off)";
                }
            }

            if (_undoButton != null) _undoButton.interactable = controller.CanUndo;
            if (_redoButton != null) _redoButton.interactable = controller.CanRedo;
            RefreshHistoryLabel();

            RefreshMirrorToggles();
            RefreshSymmetryAxis();
            RefreshExtractStatus();
            RefreshRegionState();
            UpdateDensityLabel();

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

        /// Re-reads the Mirror toggles off the CURRENT selection whenever it (or one of the
        /// mirror axes itself, e.g. from the X hotkey) changes, so the panel describes the
        /// object the brushes are actually about to mirror through. Uses SetIsOnWithoutNotify
        /// for the same reason the polarity/accumulate resyncs above do: firing onChange here
        /// would just write the value straight back where it came from.
        private void RefreshMirrorToggles()
        {
            if (_mirrorXToggle == null) return;
            if (_selection == null) _selection = FindFirstObjectByType<SelectionManager>();
            if (_selection == null) return;

            MirrorController mirror = controller.Mirror;
            if (mirror == null) return;

            bool changed = !_mirrorTogglesShown || _selection.SelectionVersion != _lastShownSelectionVersion ||
                mirror.MirrorX != _lastShownMirrorX || mirror.MirrorY != _lastShownMirrorY ||
                mirror.MirrorZ != _lastShownMirrorZ || mirror.ShowPlanes != _lastShownShowPlanes;
            if (!changed) return;

            _mirrorTogglesShown = true;
            _lastShownSelectionVersion = _selection.SelectionVersion;
            _lastShownMirrorX = mirror.MirrorX;
            _lastShownMirrorY = mirror.MirrorY;
            _lastShownMirrorZ = mirror.MirrorZ;
            _lastShownShowPlanes = mirror.ShowPlanes;

            _mirrorXToggle.SetIsOnWithoutNotify(mirror.MirrorX);
            _mirrorYToggle.SetIsOnWithoutNotify(mirror.MirrorY);
            _mirrorZToggle.SetIsOnWithoutNotify(mirror.MirrorZ);
            _showPlanesToggle.SetIsOnWithoutNotify(mirror.ShowPlanes);
        }

        /// Null-guarded because controller.Mirror resolves through the live selection, which
        /// can be empty (every object deleted) between the panel being built and a click.
        private void SetMirrorAxis(int axis, bool on)
        {
            MirrorController mirror = controller.Mirror;
            if (mirror == null) return;
            switch (axis)
            {
                case 0: mirror.MirrorX = on; break;
                case 1: mirror.MirrorY = on; break;
                default: mirror.MirrorZ = on; break;
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
            // Destroys any leftover canvas from a previous build (e.g. SceneGraphUIBuilder's
            // Load Scene "Replace" flow re-running every panel's Start) before making a new
            // one - see UIFactory.DestroyStaleCanvas for why an un-destroyed previous canvas
            // would otherwise leave two stacked, overlapping copies of this panel.
            GameObject staleCanvas = GameObject.Find("SculptCanvas");
            if (staleCanvas != null) DestroyImmediate(staleCanvas);

            var canvasGO = new GameObject("SculptCanvas", typeof(RectTransform));
            // Root-level, not parented under this builder - see UIFactory.DestroyStaleCanvas
            // for why a runtime-created child of a scene object doesn't survive an Editor undo.
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

            var panelRoot = CreatePanel(canvasGO.transform);
            var panelRect = panelRoot.GetComponent<RectTransform>();
            panelRect.anchorMin = new Vector2(0, 1);
            panelRect.anchorMax = new Vector2(0, 1);
            panelRect.pivot = new Vector2(0, 1);
            // Docked flush to the top-left corner and fixed there - no longer draggable (see
            // UIFactory's now-removed DraggablePanel; the two remaining panels sit at opposite
            // screen edges and stay put, matching a normal sculpting app's toolbars).
            panelRect.anchoredPosition = Vector2.zero;
            panelRect.sizeDelta = new Vector2(270, 0);

            // This panel carries by far the most controls of any panel in the app (every brush
            // shaping foldout, mirror, export, remesh...). Capped at the full window height so
            // it never grows past the bottom of the screen; UIFactory.AddScrollingContent still
            // shrinks it back down to fit shorter content, same as the old ContentSizeFitter did.
            float maxHeight = Mathf.Max(300f, Screen.height);
            Transform content = UIFactory.AddScrollingContent(panelRect, maxHeight, new RectOffset(12, 12, 12, 12), 10f);
            var panel = content.gameObject;

            CreateLabel(panel.transform, "Sculpting Tools", 20, FontStyle.Bold);
            _polyCountLabel = CreateLabel(panel.transform, "Tris: - | Verts: -", 12, FontStyle.Normal);

            CreateLabel(panel.transform, "Brush Strength", 14, FontStyle.Normal);
            _brushStrengthSlider = CreateSlider(panel.transform, 0.01f, 1f, controller.BrushStrength, v => controller.BrushStrength = v);

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

            // Applies to both build-up paths, not just Accumulate-on - see
            // SculptController.accumulateStrength for why the label no longer says "Accumulate".
            CreateLabel(panel.transform, "Build-Up Strength", 14, FontStyle.Normal);
            _accumulateStrengthSlider = CreateSlider(panel.transform, 0.1f, 3f, controller.AccumulateStrength,
                v => controller.AccumulateStrength = v);

            // Shared across every brush (like Lazy Mouse, not per-brush like Accumulate above),
            // so it needs no resync in the brush-changed handler.
            CreateToggle(panel.transform, "Build Up on Hold", controller.BuildUpOnHold,
                v => controller.BuildUpOnHold = v, out _);

            // Clay-only (see SculptController.surfaceRelax remarks) - fixes hard creases/
            // pinching where two Clay lobes/strokes meet, without a full remesh. Labeled
            // "(Clay)" rather than shown/hidden per brush: it's a single shared field (not
            // per-brush like Accumulate), so it stays visible and just does nothing on the
            // other brushes - the label is there so that isn't a silent surprise.
            CreateLabel(panel.transform, "Surface Relax (Clay)", 14, FontStyle.Normal);
            CreateSlider(panel.transform, 0f, 1f, controller.SurfaceRelax, v => controller.SurfaceRelax = v);

            _frontFacingOnlyToggle = CreateToggle(panel.transform, "Front Facing Only", controller.FrontFacingOnly, v =>
            {
                controller.FrontFacingOnly = v;
                _frontFacingOnlyToggleLabel.text = v ? "Front Facing Only" : "Front Facing Only (Off)";
            }, out _frontFacingOnlyToggleLabel);

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
            var flattenButton = CreateButton(brushRow3.transform, "Flatten", () => SetBrushType(BrushType.Flatten));
            var poseButton = CreateButton(brushRow3.transform, "Pose", () => SetBrushType(BrushType.Pose));
            _inflateButtonImage = inflateButton.GetComponent<Image>();
            _flattenButtonImage = flattenButton.GetComponent<Image>();
            _poseButtonImage = poseButton.GetComponent<Image>();
            RefreshBrushButtons();

            // Collapsed by default, same reasoning as the other shaping foldouts below.
            Transform maskFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Masking", false);
            CreateLabel(maskFoldout, "Hardness (Soft <-> Hard)", 12, FontStyle.Normal);
            CreateSlider(maskFoldout, 0f, 1f, controller.MaskHardness, v => controller.MaskHardness = v);
            var maskActionRow = CreateRow(maskFoldout);
            CreateButton(maskActionRow.transform, "Invert Mask", () => controller.InvertMask());
            CreateButton(maskActionRow.transform, "Clear Mask", () =>
            {
                SculptableMesh target = SelectedMesh();
                if (target != null) target.ClearMask();
            });

            BuildRegionSection(panel.transform);

            // One shared setting for every brush (not per-brush like Accumulate), so no resync
            // is needed elsewhere in this file - nothing but this panel ever changes it, same as
            // MaskHardness above.
            Transform lazyMouseFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Lazy Mouse", false);
            CreateToggle(lazyMouseFoldout, "Lazy Mouse", controller.LazyMouseEnabled, v => controller.LazyMouseEnabled = v, out _);
            CreateLabel(lazyMouseFoldout, "Radius (px)", 12, FontStyle.Normal);
            CreateSlider(lazyMouseFoldout, 1f, 150f, controller.LazyMouseRadius, v => controller.LazyMouseRadius = v);
            CreateLabel(lazyMouseFoldout, "Smoothing (Springy <-> Taut)", 12, FontStyle.Normal);
            CreateSlider(lazyMouseFoldout, 0.05f, 1f, controller.LazyMouseStrength, v => controller.LazyMouseStrength = v);

            BuildExtractSection(panel.transform);

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

            // Collapsed by default, same reasoning as "Clay Shaping" above. One slider, because
            // Plane Offset is the only thing that distinguishes Flatten from its Fill/Scrape
            // siblings - everything else it needs (size, strength, polarity) is already in the
            // shared controls at the top of the panel. See SculptController.flattenPlaneOffset.
            Transform flattenFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Flatten Shaping", false);
            CreateLabel(flattenFoldout, "Plane Offset (Scrape <-> Fill)", 12, FontStyle.Normal);
            CreateSlider(flattenFoldout, -0.5f, 0.5f, controller.FlattenPlaneOffset,
                v => controller.FlattenPlaneOffset = v);

            // Collapsed by default, same reasoning as "Clay Shaping" above.
            Transform poseFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Pose Shaping", false);
            CreateLabel(poseFoldout, "Rigidity (Soft <-> Rigid)", 12, FontStyle.Normal);
            CreateSlider(poseFoldout, 0f, 1f, controller.PoseRigidity, v => controller.PoseRigidity = v);
            // Blender calls this same idea "Segments" - how many separate places along the limb
            // you can pivot from, instead of every click bending from the same single anchor.
            CreateLabel(poseFoldout, "Segments (Joints along the limb)", 12, FontStyle.Normal);
            CreateSlider(poseFoldout, 1f, 8f, controller.PoseSegments, v => controller.PoseSegments = Mathf.RoundToInt(v));

            // Collapsed by default, same reasoning as "Clay Shaping" above. Both controls are
            // inert without a stylus - CurrentPressure short-circuits to 1 when Pen.current is
            // null - but the section is always built rather than hidden on no-pen, since a
            // tablet can be plugged in after the UI is constructed.
            Transform pressureFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Stylus Pressure", false);
            CreateLabel(pressureFoldout, "Light-Touch Floor", 12, FontStyle.Normal);
            CreateSlider(pressureFoldout, 0f, 0.5f, controller.PressureFloor, v => controller.PressureFloor = v);
            CreateLabel(pressureFoldout, "Curve (Sensitive <-> Gradual)", 12, FontStyle.Normal);
            CreateSlider(pressureFoldout, 0.5f, 3f, controller.PressureCurve, v => controller.PressureCurve = v);

            // Read through a local that tolerates null rather than dereferencing
            // controller.Mirror three times: an exception thrown from anywhere in BuildUI
            // abandons the REST of the panel silently (everything below this point simply
            // never exists), which is a far worse failure than a couple of toggles starting
            // unticked. SculptController.Mirror now self-heals a missing MirrorController, so
            // this is belt-and-braces for the genuinely empty-scene case.
            MirrorController mirror = controller.Mirror;
            CreateLabel(panel.transform, "Mirror (Selected Object)", 14, FontStyle.Normal);
            _mirrorXToggle = CreateToggle(panel.transform, "Mirror X", mirror != null && mirror.MirrorX,
                v => SetMirrorAxis(0, v), out _, MirrorXColor);
            _mirrorYToggle = CreateToggle(panel.transform, "Mirror Y", mirror != null && mirror.MirrorY,
                v => SetMirrorAxis(1, v), out _, MirrorYColor);
            _mirrorZToggle = CreateToggle(panel.transform, "Mirror Z", mirror != null && mirror.MirrorZ,
                v => SetMirrorAxis(2, v), out _, MirrorZColor);
            // Applied scene-wide, not to the selection alone - see
            // MirrorController.SetShowPlanesForAll for why a per-object visibility toggle
            // reads as broken.
            _showPlanesToggle = CreateToggle(panel.transform, "Show Mirror Planes", mirror == null || mirror.ShowPlanes,
                MirrorController.SetShowPlanesForAll, out _);

            BuildSymmetrySection(panel.transform);

            CreateToggle(panel.transform, "Wireframe (Scene View)", controller.ShowWireframeGizmo,
                v => controller.ShowWireframeGizmo = v, out _);
            CreateToggle(panel.transform, "Log Ray Hits", controller.LogRayHits,
                v => controller.LogRayHits = v, out _);

            var undoRedoRow = CreateRow(panel.transform);
            _undoButton = CreateButton(undoRedoRow.transform, "Undo (Z)", () => controller.Undo());
            _redoButton = CreateButton(undoRedoRow.transform, "Redo (Shift+Z)", () => controller.Redo());

            // Undo depth is a setting rather than a constant because its cost is entirely
            // workload-dependent: brush strokes store only the vertices they touched, so hundreds
            // fit in a few MB, while a single high-resolution Remesh stores the whole mesh twice
            // over. The readout under the slider is the other half of that - EditHistory also
            // enforces a hard memory ceiling regardless of this number, and without seeing the
            // megabytes there is no way to tell which of the two limits you are actually against.
            CreateLabel(panel.transform, "Undo Steps", 14, FontStyle.Normal);
            CreateSlider(panel.transform, EditHistory.MinSteps, EditHistory.HardMaxSteps,
                controller.UndoSteps, v => controller.UndoSteps = Mathf.RoundToInt(v));
            // Populated here rather than left for the first Update: RefreshHistoryLabel is
            // throttled, and the panel gets rebuilt from scratch on a scene load, so an empty
            // initial string would leave the readout blank for up to half a second every time.
            _historyLabel = CreateLabel(panel.transform, SculptController.HistorySummary, 11, FontStyle.Italic);
            _nextHistoryRefresh = 0f;

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
            _remeshResolutionSlider = CreateSlider(panel.transform, 4f, 500f, controller.RemeshResolution,
                v => controller.RemeshResolution = Mathf.RoundToInt(v));
            CreateButton(panel.transform, "Remesh", () => controller.Remesh());

            CreateLabel(panel.transform,
                "Keys: 1 Move  2 Clay  3 Smooth  4 Crease  5 Dam Std\n6 Inflate  7 Flatten  8 Pose  M Toggle Mask Paint\nTap R: Remesh  Hold R + drag: adjust remesh density\nH Box/Lasso Hide  N Box/Lasso Mask (Esc cancels)\nZ Undo  Shift+Z Redo (not Ctrl+Z - that's the Editor's)\nHold S + drag, or Scroll over model: resize brush\nHold F + drag: adjust brush strength (red inner circle)\nLMB Sculpt/Mask | RMB or Ctrl+LMB Invert/Erase\nAlt+LMB Orbit | MMB Pan | Scroll Zoom | Ctrl+Alt+LMB Drag Zoom",
                11, FontStyle.Italic);

            // Built last so it sits on top of every other child in this canvas's sibling order
            // (Unity UI draws later siblings over earlier ones) - the brush cursor should never
            // be occluded by a panel, even for the one frame before _isOverUI would hide it.
            // The tether goes immediately BEFORE the ring, for the same reason in miniature: it
            // clears the panels, but the ring still draws over the line rather than the line
            // cutting across the ring's middle.
            _lazyTetherGO = CreateLazyMouseTether(canvasGO.transform);
            _cursorRingGO = CreateBrushCursor(canvasGO.transform);
            _actionToastGO = CreateActionToast(canvasGO.transform);
            _densityGrid = controller.DensityGrid; // triggers the self-install, see its remarks
            _densityLabelGO = CreateRemeshDensityLabel(canvasGO.transform);
            // Last of all: the marquee is drawn over the model AND over the panels, since a
            // drag that starts in the viewport can easily be dragged out across one. It never
            // competes with the brush ring for attention - a region gesture being armed is
            // exactly when the ring is suppressed (see SculptController.UpdateBrushCursor).
            _regionMarqueeGO = CreateRegionMarquee(canvasGO.transform);
            _regionCrosshairGO = CreateRegionCrosshair(canvasGO.transform);
        }

        // Throttled rather than refreshed every frame: EditHistory.TotalBytes walks every step
        // in both stacks, and this is a status line nobody is watching frame by frame.
        private const float HistoryRefreshInterval = 0.5f;

        private void RefreshHistoryLabel()
        {
            if (_historyLabel == null || Time.unscaledTime < _nextHistoryRefresh) return;
            _nextHistoryRefresh = Time.unscaledTime + HistoryRefreshInterval;
            _historyLabel.text = SculptController.HistorySummary;
        }

        private void SetBrushType(BrushType type)
        {
            controller.CurrentBrush = type;
            RefreshBrushButtons();
        }

        private void RefreshBrushButtons()
        {
            _moveButtonImage.color = controller.CurrentBrush == BrushType.Move ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _clayButtonImage.color = controller.CurrentBrush == BrushType.Clay ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _smoothButtonImage.color = controller.CurrentBrush == BrushType.Smooth ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _creaseButtonImage.color = controller.CurrentBrush == BrushType.Crease ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _damButtonImage.color = controller.CurrentBrush == BrushType.DamStandard ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _inflateButtonImage.color = controller.CurrentBrush == BrushType.Inflate ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _flattenButtonImage.color = controller.CurrentBrush == BrushType.Flatten ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _poseButtonImage.color = controller.CurrentBrush == BrushType.Pose ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _maskButtonImage.color = controller.IsMaskPaintMode ? MaskActiveColor : UIFactory.InactiveColor;
        }

        private void RefreshAlphaButtons()
        {
            for (int i = 0; i < AlphaTypes.Length; i++)
                _alphaButtonImages[i].color = controller.AlphaType == AlphaTypes[i] ? UIFactory.ActiveColor : UIFactory.InactiveColor;
        }

        // ------------------------------------------------------------------------- extract

        /// Mask extract (ZBrush-style): pull the masked region off the surface as a solid,
        /// separate object. Sits directly under Masking because the mask IS its input - the
        /// workflow is paint a mask, open this, dial it in against the live preview, Accept.
        ///
        /// Collapsed by default like every other shaping foldout. Every slider writes straight
        /// through to MaskExtractController, whose setters rebuild the preview themselves, so
        /// there's no refresh plumbing here - dragging a slider with no preview open is a plain
        /// value assignment.
        /// The correspondence-map repair tools. Separate from the Mirror toggles just above even
        /// though both are "symmetry", because they answer different questions: those toggles ask
        /// "reflect my strokes as I sculpt" and can have any combination of axes on at once,
        /// while these ask "are the two halves of this model actually the same, and make them so"
        /// - which is only meaningful about ONE plane at a time. See SculptController's
        /// symmetryAxis remarks.
        /// The object this panel's per-object buttons act on - the same primary selection the
        /// brushes target. Resolved per click rather than captured when the button is built:
        /// the selection changes from the Scene panel, from a viewport double-click and on a
        /// scene load, and a captured reference would quietly act on the wrong object (or a
        /// destroyed one) after any of those.
        private SculptableMesh SelectedMesh()
        {
            if (_selection == null) _selection = FindFirstObjectByType<SelectionManager>();
            return _selection != null ? _selection.PrimarySelection : null;
        }

        /// Box/lasso hide and mask (see RegionSelectTool). Sits directly under Masking because
        /// two of its four gestures edit exactly what that section edits - the other two edit
        /// visibility, which is the same idea pointed at a different piece of per-vertex state.
        ///
        /// The four gesture buttons are radio-style: clicking the armed one disarms it, so the
        /// panel can always get back to plain sculpting without reaching for a hotkey.
        private void BuildRegionSection(Transform panel)
        {
            _regionSelect = controller.RegionSelect;
            // Starts OPEN, unlike the shaping foldouts around it: those tune a brush you already
            // picked from a row of buttons that is always visible, whereas these gestures have
            // no other entry point in the panel at all - collapsed, the feature is invisible
            // unless you already know it exists. The panel scrolls, so the extra height costs
            // nothing but a little scrolling.
            Transform foldout = UIFactory.CreateFoldoutSection(panel, "Hide / Region Select", true);

            CreateLabel(foldout,
                "Drag a shape out from the cursor to hide or mask\nwhat it covers, front and back. RMB or Ctrl reverses\n(show/unmask), Shift acts OUTSIDE the shape, a click\nwith no drag resets, Esc cancels.",
                11, FontStyle.Italic);

            var hideRow = CreateRow(foldout);
            _boxHideButtonImage = CreateButton(hideRow.transform, "Box Hide (H)",
                () => ToggleRegionMode(RegionSelectMode.BoxHide)).GetComponent<Image>();
            _lassoHideButtonImage = CreateButton(hideRow.transform, "Lasso Hide",
                () => ToggleRegionMode(RegionSelectMode.LassoHide)).GetComponent<Image>();

            var maskRow = CreateRow(foldout);
            _boxMaskButtonImage = CreateButton(maskRow.transform, "Box Mask (N)",
                () => ToggleRegionMode(RegionSelectMode.BoxMask)).GetComponent<Image>();
            _lassoMaskButtonImage = CreateButton(maskRow.transform, "Lasso Mask",
                () => ToggleRegionMode(RegionSelectMode.LassoMask)).GetComponent<Image>();

            var actionRow = CreateRow(foldout);
            _showAllButton = CreateButton(actionRow.transform, "Show All", () =>
            {
                SculptableMesh target = SelectedMesh();
                if (target != null) target.ShowAllGeometry();
            });
            _invertVisibleButton = CreateButton(actionRow.transform, "Invert Visible", () =>
            {
                SculptableMesh target = SelectedMesh();
                if (target != null) target.InvertVisibleGeometry();
            });

            _regionStatusLabel = CreateLabel(foldout, "", 11, FontStyle.Italic);
            RefreshRegionButtons();
        }

        private void ToggleRegionMode(RegionSelectMode target)
        {
            if (_regionSelect == null) return;
            _regionSelect.Mode = _regionSelect.Mode == target ? RegionSelectMode.Off : target;
            RefreshRegionButtons();
        }

        private void RefreshRegionButtons()
        {
            if (_boxHideButtonImage == null || _regionSelect == null) return;
            RegionSelectMode m = _regionSelect.Mode;
            _boxHideButtonImage.color = m == RegionSelectMode.BoxHide ? RegionHideActiveColor : UIFactory.InactiveColor;
            _lassoHideButtonImage.color = m == RegionSelectMode.LassoHide ? RegionHideActiveColor : UIFactory.InactiveColor;
            _boxMaskButtonImage.color = m == RegionSelectMode.BoxMask ? MaskActiveColor : UIFactory.InactiveColor;
            _lassoMaskButtonImage.color = m == RegionSelectMode.LassoMask ? MaskActiveColor : UIFactory.InactiveColor;
        }

        /// Follows the tool once per frame: the mode can change from a hotkey (H/N, or any
        /// brush key leaving the mode) as well as from these buttons, and the status line is
        /// written by the tool itself when a gesture lands.
        private void RefreshRegionState()
        {
            if (_regionSelect == null) return;

            if (_regionSelect.Mode != _lastShownRegionMode)
            {
                _lastShownRegionMode = _regionSelect.Mode;
                RefreshRegionButtons();
            }

            if (_regionStatusLabel != null && _regionSelect.Status != _lastShownRegionStatus)
            {
                _lastShownRegionStatus = _regionSelect.Status;
                _regionStatusLabel.text = _lastShownRegionStatus;
            }

            // Both act on hidden geometry, so both are dead ends with nothing hidden - greying
            // them out says so before the click rather than after it does nothing.
            SculptableMesh target = SelectedMesh();
            bool anyHidden = target != null && target.AnyHidden;
            if (_showAllButton != null) _showAllButton.interactable = anyHidden;
            if (_invertVisibleButton != null) _invertVisibleButton.interactable = anyHidden;

            UpdateRegionMarquee();
            UpdateRegionCrosshair();
        }

        /// The armed-mode pointer. Tinted like the mode it belongs to (teal for hide, orange for
        /// mask) so the crosshair says WHICH gesture is armed, not merely that one is - the
        /// panel's highlighted button is the other half of that, and it can be scrolled out of
        /// sight or collapsed inside its foldout.
        private void UpdateRegionCrosshair()
        {
            if (_regionCrosshairGO == null) return;

            bool show = controller.ShowRegionCrosshair;
            if (_regionCrosshairGO.activeSelf != show) _regionCrosshairGO.SetActive(show);
            if (!show) return;

            _regionCrosshairRect.position = controller.RegionCrosshairScreenPosition;
            Color tint = _regionSelect != null && _regionSelect.IsHideMode ? RegionHideActiveColor : MaskActiveColor;
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

            Color tint = _regionSelect.DragRemoves ? RegionRemoveColor
                : _regionSelect.IsHideMode ? RegionHideActiveColor
                : MaskActiveColor;
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

        private void BuildSymmetrySection(Transform panel)
        {
            Transform foldout = UIFactory.CreateFoldoutSection(panel, "Symmetry Repair", false);

            CreateLabel(foldout, "Symmetry Plane", 12, FontStyle.Normal);
            var axisRow = CreateRow(foldout);
            for (int i = 0; i < 3; i++)
            {
                int axis = i; // captured per iteration, not shared across the three callbacks
                Button b = CreateButton(axisRow.transform, SymmetryOps.AxisName(axis),
                    () => controller.SymmetryAxis = axis);
                _symmetryAxisImages[i] = b.GetComponent<Image>();
            }

            // Match tolerance is exposed because the right value is a property of the MESH, not
            // of the app: an exactly-mirrored model pairs at any tolerance, while two halves that
            // have each been remeshed independently only pair once the window is wide enough to
            // span the difference in where the two tessellations put their vertices. Too wide is
            // self-limiting rather than destructive (see SymmetryOps.MaxToleranceScale), so this
            // is safe to let the user push.
            CreateLabel(foldout, "Match Tolerance (tight <-> loose)", 12, FontStyle.Normal);
            CreateSlider(foldout, SymmetryOps.MinToleranceScale, SymmetryOps.MaxToleranceScale,
                controller.SymmetryToleranceScale, v => controller.SymmetryToleranceScale = v);

            CreateButton(foldout, "Check Symmetry", () => SetSymmetryStatus(controller.SymmetryStatus(), Color.white));

            // Two rows, two different operations, and the split is the point. "Match Up" nudges
            // vertices onto their counterparts and needs the two halves to already correspond;
            // "Cut & Mirror" throws one side away and rebuilds it, and needs nothing at all. The
            // first is non-destructive and keeps mask/topology, so it stays the top row - but it
            // is also the one that can honestly do nothing on a model whose halves were built
            // separately, which is exactly when the row below is the answer.
            CreateLabel(foldout, "Match Up (keeps topology, needs matching halves)", 12, FontStyle.Normal);
            var mirrorRow = CreateRow(foldout);
            Button posToNeg = CreateButton(mirrorRow.transform, "+X to -X",
                () => SetSymmetryStatus(controller.MakeSymmetric(true), SymmetryOkColor));
            Button negToPos = CreateButton(mirrorRow.transform, "-X to +X",
                () => SetSymmetryStatus(controller.MakeSymmetric(false), SymmetryOkColor));
            _symPosToNegLabel = posToNeg.GetComponentInChildren<Text>();
            _symNegToPosLabel = negToPos.GetComponentInChildren<Text>();

            CreateLabel(foldout, "Cut & Mirror (rebuilds the far side, always works)", 12, FontStyle.Normal);
            var cutRow = CreateRow(foldout);
            Button cutPosToNeg = CreateButton(cutRow.transform, "+X to -X",
                () => SetSymmetryStatus(controller.MirrorAndWeld(true), SymmetryOkColor));
            Button cutNegToPos = CreateButton(cutRow.transform, "-X to +X",
                () => SetSymmetryStatus(controller.MirrorAndWeld(false), SymmetryOkColor));
            _symCutPosToNegLabel = cutPosToNeg.GetComponentInChildren<Text>();
            _symCutNegToPosLabel = cutNegToPos.GetComponentInChildren<Text>();

            CreateButton(foldout, "Symmetry Cleanup (Snap + Weld)",
                () => SetSymmetryStatus(controller.SymmetryCleanup(), SymmetryOkColor));

            _symmetryStatusLabel = CreateLabel(foldout,
                "Check Symmetry reports how many vertices pair across the plane. If most of them " +
                "don't pair, the halves were built separately - use Cut & Mirror.", 11, FontStyle.Italic);
            _symmetryStatusLabel.color = SymmetryHintColor;

            RefreshSymmetryAxis();
        }

        private void SetSymmetryStatus(string message, Color color)
        {
            if (_symmetryStatusLabel == null) return;
            _symmetryStatusLabel.text = message;
            _symmetryStatusLabel.color = color;
        }

        /// Keeps the axis buttons' highlight and the two mirror-direction labels in step with the
        /// chosen plane. Guarded on change rather than rewritten every frame, the same idiom
        /// RefreshBrushButtons uses - and unlike the extract status line this is cheap either
        /// way, it just has no reason to run when nothing moved.
        private void RefreshSymmetryAxis()
        {
            int axis = controller.SymmetryAxis;
            if (axis == _lastShownSymmetryAxis) return;
            _lastShownSymmetryAxis = axis;

            for (int i = 0; i < 3; i++)
            {
                if (_symmetryAxisImages[i] == null) continue;
                // Tinted with the axis's own gizmo colour rather than the generic UIFactory.ActiveColor, so
                // the selected plane matches the coloured quad MirrorController draws for it.
                _symmetryAxisImages[i].color = i != axis ? UIFactory.InactiveColor
                    : (i == 0 ? MirrorXColor : i == 1 ? MirrorYColor : MirrorZColor);
            }

            string name = SymmetryOps.AxisName(axis);
            if (_symPosToNegLabel != null) _symPosToNegLabel.text = $"+{name} to -{name}";
            if (_symNegToPosLabel != null) _symNegToPosLabel.text = $"-{name} to +{name}";
            if (_symCutPosToNegLabel != null) _symCutPosToNegLabel.text = $"+{name} to -{name}";
            if (_symCutNegToPosLabel != null) _symCutNegToPosLabel.text = $"-{name} to +{name}";
        }

        private void BuildExtractSection(Transform panel)
        {
            _extract = FindFirstObjectByType<MaskExtractController>();
            Transform foldout = UIFactory.CreateFoldoutSection(panel, "Extract (from Mask)", false);

            if (_extract == null)
            {
                CreateLabel(foldout, "No MaskExtractController in scene.", 11, FontStyle.Italic);
                return;
            }

            CreateLabel(foldout, "Thickness", 12, FontStyle.Normal);
            CreateSlider(foldout, 0.002f, 0.5f, _extract.ThicknessFraction, v => _extract.ThicknessFraction = v);

            CreateLabel(foldout, "Offset (sink <-> float)", 12, FontStyle.Normal);
            CreateSlider(foldout, -0.25f, 0.25f, _extract.OffsetFraction, v => _extract.OffsetFraction = v);

            CreateLabel(foldout, "Edge Falloff (slab <-> feathered)", 12, FontStyle.Normal);
            CreateSlider(foldout, 0f, 1f, _extract.FalloffAmount, v => _extract.FalloffAmount = v);

            CreateLabel(foldout, "Border Smoothing", 12, FontStyle.Normal);
            CreateSlider(foldout, 0f, 20f, _extract.BorderSmoothing, v => _extract.BorderSmoothing = Mathf.RoundToInt(v));

            CreateLabel(foldout, "Surface Smoothing", 12, FontStyle.Normal);
            CreateSlider(foldout, 0f, 20f, _extract.SurfaceSmoothing, v => _extract.SurfaceSmoothing = Mathf.RoundToInt(v));

            CreateLabel(foldout, "Shrinkwrap (inner face to body)", 12, FontStyle.Normal);
            CreateSlider(foldout, 0f, 1f, _extract.Shrinkwrap, v => _extract.Shrinkwrap = v);

            CreateLabel(foldout, "Mask Threshold", 12, FontStyle.Normal);
            CreateSlider(foldout, 0.05f, 0.95f, _extract.MaskThreshold, v => _extract.MaskThreshold = v);

            CreateToggle(foldout, "Extract Unmasked Instead", _extract.InvertRegion,
                v => _extract.InvertRegion = v, out _);

            CreateButton(foldout, "Preview Extract", () => _extract.BeginPreview());
            var acceptCancelRow = CreateRow(foldout);
            _extractAcceptButton = CreateButton(acceptCancelRow.transform, "Accept", () => _extract.Accept());
            _extractCancelButton = CreateButton(acceptCancelRow.transform, "Cancel", () => _extract.Cancel());

            _extractStatusLabel = CreateLabel(foldout, "", 11, FontStyle.Italic);
            RefreshExtractStatus();
        }

        /// Keeps the status line and the Accept/Cancel buttons in step with the controller's
        /// actual state - the preview can close itself without the UI touching it (the source
        /// gets deleted, the selection moves, a Remesh wipes the mask), so this is polled rather
        /// than pushed from the buttons. Only rewrites the label when something actually
        /// changed, same only-on-change idiom the brush buttons above already use.
        private void RefreshExtractStatus()
        {
            if (_extract == null || _extractStatusLabel == null) return;

            bool previewing = _extract.IsPreviewing;
            string error = _extract.Error;
            int tris = _extract.PreviewTriangleCount;

            if (previewing == _lastShownExtractPreviewing &&
                tris == _lastShownExtractTris &&
                error == _lastShownExtractError)
                return;

            _lastShownExtractPreviewing = previewing;
            _lastShownExtractTris = tris;
            _lastShownExtractError = error;

            // Accept needs actual geometry; Cancel only needs an open session - a session whose
            // mask was erased has nothing to commit but still very much needs closing. See
            // MaskExtractController.IsPreviewing.
            if (_extractAcceptButton != null) _extractAcceptButton.interactable = _extract.HasPreviewGeometry;
            if (_extractCancelButton != null) _extractCancelButton.interactable = previewing;

            if (!string.IsNullOrEmpty(error))
            {
                _extractStatusLabel.text = error;
                _extractStatusLabel.color = new Color(0.95f, 0.65f, 0.4f);
            }
            else if (previewing)
            {
                _extractStatusLabel.text = $"Preview: {tris:N0} tris. Accept to keep.";
                _extractStatusLabel.color = new Color(0.55f, 0.85f, 0.55f);
            }
            else
            {
                _extractStatusLabel.text = "Mask a region, then Preview. Accept makes it a new object.";
                _extractStatusLabel.color = new Color(0.65f, 0.65f, 0.7f);
            }
        }

        // Square icon button showing a live preview of a procedurally-generated brush alpha
        // (see BrushAlphaLibrary) instead of a text label - mirrors ZBrush's alpha palette.
        private Image CreateAlphaButton(Transform parent, BrushAlphaType type, Action onClick)
        {
            var go = new GameObject("AlphaButton_" + type, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = UIFactory.InactiveColor;
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
            go.GetComponent<Image>().color = UIFactory.PanelColor;
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
            img.color = UIFactory.InactiveColor;
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
            const int size = 128;
            const float thickness = 6f;
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

        /// Follows the world-space grid's screen projection every frame (see UpdateDensityLabel)
        /// rather than sitting at a fixed screen anchor like the action toast above - the grid
        /// itself moves and rescales with the selected object, so a fixed anchor would drift
        /// away from "underneath the grid" the moment the object wasn't dead-center on screen.
        private GameObject CreateRemeshDensityLabel(Transform canvasParent)
        {
            var go = new GameObject("RemeshDensityLabel", typeof(RectTransform));
            go.transform.SetParent(canvasParent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = rect.anchorMax = Vector2.zero;
            rect.pivot = new Vector2(0.5f, 1f); // top-center pivot: sits just below the anchor point
            rect.sizeDelta = new Vector2(160f, 28f);

            var text = go.AddComponent<Text>();
            text.font = _font;
            text.fontSize = 18;
            text.fontStyle = FontStyle.Bold;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.raycastTarget = false;

            _densityLabelRect = rect;
            _densityLabelText = text;
            go.SetActive(false);
            return go;
        }

        private void UpdateDensityLabel()
        {
            if (_densityLabelGO == null || _densityGrid == null) return;

            bool show = _densityGrid.IsVisible;
            if (_densityLabelGO.activeSelf != show) _densityLabelGO.SetActive(show);
            if (!show) return;

            Vector2 screen = _densityGrid.LabelScreenPosition;
            _densityLabelRect.position = new Vector3(screen.x, screen.y + DensityLabelOffsetYPx, 0f);

            // Only-on-change, same reasoning as the poly count label below - this runs every
            // frame the gauge is up, and a fresh concatenation for a value that hasn't moved
            // since last frame is wasted garbage.
            int density = controller.RemeshResolution;
            if (density != _lastShownDensity)
            {
                _lastShownDensity = density;
                _densityLabelText.text = "Density: " + density;
            }
        }
    }
}
