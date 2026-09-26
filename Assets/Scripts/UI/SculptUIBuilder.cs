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

        // ActiveColor/InactiveColor/PanelColor - and the mask/region tool colours - live on
        // UIFactory, shared with the HUD and the radial menus.

        // Matches Unity's axis-handle/gizmo convention (X red, Y green, Z blue), and
        // MirrorController's own plane colors.
        private static readonly Color MirrorXColor = new Color(1f, 0.25f, 0.25f);
        private static readonly Color MirrorYColor = new Color(0.35f, 1f, 0.35f);
        private static readonly Color MirrorZColor = new Color(0.3f, 0.55f, 1f);

        private Font _font;
        private Text _positiveToggleLabel;
        private Toggle _positiveToggle;
        private Text _accumulateToggleLabel;
        private Toggle _accumulateToggle;
        private Slider _accumulateStrengthSlider;
        private Text _frontFacingOnlyToggleLabel;
        private Toggle _frontFacingOnlyToggle;
        private Toggle _customFalloffToggle;
        private Image _moveButtonImage;
        private Image _clayButtonImage;
        private Image _smoothButtonImage;
        private Image _creaseButtonImage;
        private Image _inflateButtonImage;
        private Image _flattenButtonImage;
        private Image _poseButtonImage;
        private Image _standardButtonImage;
        private Image _layerButtonImage;
        private Image _snakeHookButtonImage;
        private Image _maskButtonImage;
        private bool _lastShownMaskMode;

        // Resynced every frame like _brushSizeSlider - RemeshResolution can now change from the
        // R-hold gauge as well as this slider, so leaving it un-polled would go stale the first
        // time someone used the hotkey.
        private Slider _remeshResolutionSlider;
        private Slider _brushSizeSlider;
        // Was previously created but never captured, so this slider went stale the moment a
        // hotkey (or the F-drag gauge below) changed controller.BrushStrength out from under
        // it - each brush remembers its own strength (SculptController._brushStrengthPerType),
        // so switching brushes silently changed the ACTUAL value while the panel kept showing
        // whatever the last brush had. Now resynced every frame alongside _brushSizeSlider.
        private Slider _brushStrengthSlider;
        // Per brush and D-draggable, so polled like the strength slider. Mask Hardness too: in
        // mask mode the D-drag edits it instead.
        private Slider _focalShiftSlider;
        private Slider _maskHardnessSlider;
        private Text _polyCountLabel;
        private Text _exportStatusLabel;
        // Polled, not set from the button: Remesh also fires from the R hotkey.
        private Text _remeshReportLabel;
        // Quad Remesh target: a log-scale slider (100 to 200k is too wide for a linear one) plus
        // a readout, both resynced per frame because Half/Double change the value too.
        private Slider _quadTargetSlider;
        private Text _quadTargetLabel;
        private int _quadTargetShown = -1;
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

        // Radial symmetry controls (see BuildRadialSection), synced the same way as the mirror
        // toggles: per-object settings, so a selection change has to repaint them.
        private static readonly Color RadialCustomColor = new Color(1f, 0.8f, 0.25f);
        private Toggle _radialToggle;
        private Slider _radialCountSlider;
        private Text _radialCountLabel;
        private readonly Image[] _radialAxisImages = new Image[4];
        private GameObject _radialCustomRow;
        private InputField _radialCustomField;
        private bool _lastShownRadial;
        private int _lastShownRadialCount = -1;
        private int _lastShownRadialAxis = -1;
        private Vector3 _lastShownRadialCustomAxis;

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
            EnsureHud();
            TooltipSystem.EnsureToggleBuilt();
        }

        /// The viewport HUD (brush cursor, tether, toast, density gauge, region marquee) - its own
        /// component and canvas, see SculptHudOverlay. Added here, on this GameObject, since the
        /// scene cannot carry wired references; built once, not on a scene-load rebuild, as it
        /// holds no scene state of its own.
        private void EnsureHud()
        {
            var hud = GetComponent<SculptHudOverlay>();
            if (hud == null) hud = gameObject.AddComponent<SculptHudOverlay>();
            hud.Init(controller);
        }

        /// Rebuilds the panel from the controller's CURRENT values - what a "Replace scene" load
        /// needs, since it restores brush settings wholesale (see SceneGraphUIBuilder.
        /// RebuildOtherPanels, the caller).
        ///
        /// That used to be a SendMessage("Start") at this whole GameObject, with two problems:
        /// it re-ran every OTHER component's Start on it too (the view gizmo and both radial
        /// menus rebuilt themselves for nothing), and it left the change-detection caches below
        /// holding values from before the load - so any widget whose watched value happened to
        /// match (the symmetry axis tint and labels, the poly count, the extract status) kept
        /// showing the default it was just rebuilt with.
        public void RebuildForLoadedScene()
        {
            ResetShownCaches();
            BuildUI();
        }

        /// Puts every "last shown" cache back to its never-drawn value, so the next Update
        /// refreshes each widget unconditionally.
        private void ResetShownCaches()
        {
            _lastShownBrush = (BrushType)(-1);
            _lastShownMaskMode = false;
            _lastShownTriCount = -1;
            _lastShownVertCount = -1;
            _lastShownSelectionVersion = -1;
            _lastShownMirrorX = _lastShownMirrorY = _lastShownMirrorZ = _lastShownShowPlanes = false;
            _mirrorTogglesShown = false;
            _lastShownRadial = false;
            _lastShownRadialCount = -1;
            _lastShownRadialAxis = -1;
            _lastShownSymmetryAxis = -1;
            _lastShownExtractPreviewing = false;
            _lastShownExtractTris = -1;
            _lastShownExtractError = "\0";
            _nextHistoryRefresh = 0f;
        }

        private void Update()
        {
            // Not built yet (BuildUI creates the size slider unconditionally).
            if (controller == null || _brushSizeSlider == null) return;

            // Panel's own Brush Size/Strength sliders stay in sync with S-drag/F-drag/scroll
            // adjustments made straight from the viewport - SetValueWithoutNotify avoids
            // feeding the change back into the controller through the slider's own
            // onValueChanged.
            if (_brushSizeSlider != null)
            {
                // The range follows the unit - pixels or world units - which the toggle below can
                // switch at any time. Changed without notify: see SetRangeAndValueWithoutNotify.
                UIFactory.SetRangeAndValueWithoutNotify(_brushSizeSlider,
                    controller.BrushSizeMin, controller.BrushSizeMax, controller.BrushSize);
            }
            if (_remeshResolutionSlider != null) _remeshResolutionSlider.SetValueWithoutNotify(controller.RemeshResolution);
            if (_quadTargetSlider != null && _quadTargetShown != controller.QuadRemeshTarget)
            {
                _quadTargetShown = controller.QuadRemeshTarget;
                _quadTargetSlider.SetValueWithoutNotify(Mathf.Log10(_quadTargetShown));
                _quadTargetLabel.text = $"Quad Remesh Target: {_quadTargetShown:N0} quads";
            }
            // Reference compare: the report string is only replaced when a Remesh runs.
            if (_remeshReportLabel != null && !ReferenceEquals(_remeshReportLabel.text, controller.LastRemeshReport))
                _remeshReportLabel.text = controller.LastRemeshReport;
            if (_brushStrengthSlider != null) _brushStrengthSlider.SetValueWithoutNotify(controller.BrushStrength);
            if (_focalShiftSlider != null) _focalShiftSlider.SetValueWithoutNotify(controller.FocalShift * 100f);
            if (_maskHardnessSlider != null) _maskHardnessSlider.SetValueWithoutNotify(controller.MaskHardness);

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
                // Falloff curves are per brush too (see SculptController._falloffCurves).
                if (_customFalloffToggle != null) _customFalloffToggle.SetIsOnWithoutNotify(controller.CustomFalloff);

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
                mirror.MirrorZ != _lastShownMirrorZ || mirror.ShowPlanes != _lastShownShowPlanes ||
                mirror.Radial != _lastShownRadial || mirror.RadialCount != _lastShownRadialCount ||
                (int)mirror.RadialAxisChoice != _lastShownRadialAxis ||
                !mirror.RadialCustomAxis.Equals(_lastShownRadialCustomAxis);
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
            RefreshRadialControls(mirror);
        }

        private void RefreshRadialControls(MirrorController mirror)
        {
            _lastShownRadial = mirror.Radial;
            _lastShownRadialCount = mirror.RadialCount;
            _lastShownRadialAxis = (int)mirror.RadialAxisChoice;
            _lastShownRadialCustomAxis = mirror.RadialCustomAxis;
            if (_radialToggle == null) return;

            _radialToggle.SetIsOnWithoutNotify(mirror.Radial);
            _radialCountSlider.SetValueWithoutNotify(mirror.RadialCount);
            _radialCountLabel.text = RadialCountText(mirror.RadialCount);

            int axis = (int)mirror.RadialAxisChoice;
            for (int i = 0; i < _radialAxisImages.Length; i++)
            {
                if (_radialAxisImages[i] == null) continue;
                // Tinted with the colour MirrorController draws the radial guide in for that axis.
                _radialAxisImages[i].color = i != axis ? UIFactory.InactiveColor
                    : i == 0 ? MirrorXColor : i == 1 ? MirrorYColor : i == 2 ? MirrorZColor : RadialCustomColor;
            }

            bool custom = mirror.RadialAxisChoice == RadialAxis.Custom;
            if (_radialCustomRow.activeSelf != custom) _radialCustomRow.SetActive(custom);
            if (custom && !_radialCustomField.isFocused) _radialCustomField.SetTextWithoutNotify(FormatAxis(mirror.RadialCustomAxis));
        }

        private static string RadialCountText(int count) => $"Radial Repeats: {count}";

        private static string FormatAxis(Vector3 axis) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.###}, {1:0.###}, {2:0.###}", axis.x, axis.y, axis.z);

        /// "x, y, z" (commas or spaces). Null when it does not read as a usable direction.
        private static Vector3? ParseAxis(string text)
        {
            string[] parts = text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return null;
            var values = new float[3];
            for (int i = 0; i < 3; i++)
                if (!float.TryParse(parts[i], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out values[i])) return null;
            var axis = new Vector3(values[0], values[1], values[2]);
            return axis.sqrMagnitude > 1e-12f ? axis.normalized : (Vector3?)null;
        }

        // The setters below are null-guarded for the same reason SetMirrorAxis is.
        private void SetRadial(bool on)
        {
            MirrorController mirror = controller.Mirror;
            if (mirror != null) mirror.Radial = on;
        }

        private void SetRadialCount(float value)
        {
            MirrorController mirror = controller.Mirror;
            if (mirror == null) return;
            mirror.RadialCount = Mathf.RoundToInt(value);
            if (_radialCountLabel != null) _radialCountLabel.text = RadialCountText(mirror.RadialCount);
        }

        private void SetRadialAxis(RadialAxis axis)
        {
            MirrorController mirror = controller.Mirror;
            if (mirror != null) mirror.RadialAxisChoice = axis;
        }

        private void SetRadialCustomAxis(string text)
        {
            MirrorController mirror = controller.Mirror;
            if (mirror == null) return;
            Vector3? axis = ParseAxis(text);
            // An unreadable entry snaps back to the current axis rather than zeroing it.
            if (axis.HasValue) mirror.RadialCustomAxis = axis.Value;
            _radialCustomField.SetTextWithoutNotify(FormatAxis(mirror.RadialCustomAxis));
        }

        /// Aims the custom axis straight down the view, in the object's own local space - so, seen
        /// from here, the repeats land evenly spaced around the object's pivot on screen.
        private void SetRadialAxisFromView()
        {
            MirrorController mirror = controller.Mirror;
            Camera cam = Camera.main;
            if (mirror == null || cam == null) return;
            Vector3 local = mirror.transform.InverseTransformDirection(cam.transform.forward);
            if (local.sqrMagnitude < 1e-12f) return;
            mirror.RadialCustomAxis = local.normalized;
            mirror.RadialAxisChoice = RadialAxis.Custom;
        }

        private void BuildRadialSection(Transform panel, MirrorController mirror)
        {
            CreateLabel(panel, "Radial Symmetry (Selected Object)", 14, FontStyle.Normal);
            _radialToggle = CreateToggle(panel, "Radial", mirror != null && mirror.Radial, SetRadial, out _,
                tooltip: "Repeats every stroke evenly around the axis below, through the object's pivot. Combines with the mirror axes. Lathe objects are built around Y.");

            int count = mirror != null ? mirror.RadialCount : 8;
            _radialCountLabel = CreateLabel(panel, RadialCountText(count), 12, FontStyle.Normal);
            _radialCountSlider = CreateSlider(panel, SymmetryGroup.MinRadialCount, SymmetryGroup.MaxRadialCount, count,
                SetRadialCount, "How many times each stroke is repeated around the axis.");
            _radialCountSlider.wholeNumbers = true;

            CreateLabel(panel, "Radial Axis", 12, FontStyle.Normal);
            var axisRow = CreateRow(panel);
            string[] names = { "X", "Y", "Z", "Custom" };
            for (int i = 0; i < names.Length; i++)
            {
                var axis = (RadialAxis)i; // captured per iteration, not shared across the callbacks
                Button b = CreateButton(axisRow.transform, names[i], () => SetRadialAxis(axis),
                    i < 3 ? $"Repeats strokes around the object's local {names[i]} axis."
                          : "Repeats strokes around an axis you set below - typed in, or taken from the view.");
                _radialAxisImages[i] = b.GetComponent<Image>();
            }

            _radialCustomRow = CreateRow(panel);
            _radialCustomField = UIFactory.CreateInputField(_radialCustomRow.transform,
                FormatAxis(mirror != null ? mirror.RadialCustomAxis : Vector3.up), SetRadialCustomAxis);
            _radialCustomField.GetComponent<LayoutElement>().flexibleWidth = 2f; // the field gets the room, not the button
            CreateButton(_radialCustomRow.transform, "From View", SetRadialAxisFromView,
                "Sets the custom axis to point straight down the current view, through the object's pivot.");
            _radialCustomRow.SetActive(mirror != null && mirror.RadialAxisChoice == RadialAxis.Custom);
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
            // Constant pixel size, not Unity's own "Scale With Screen Size" - that scales every
            // element uniformly (fonts, buttons, spacing) and could blow the panel up or shrink/
            // shift it unpredictably in an unconventional or narrow docked Game view. The panel
            // still adapts to the window's own size, just via UIFactory.ResponsivePanelWidth /
            // ScrollPanelHeightController instead - only the panel's own width and height track
            // the screen, not the size of everything inside it.
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
            Transform content = UIFactory.AddScrollingContent(panelRect, new RectOffset(12, 12, 12, 12), 10f);
            var panel = content.gameObject;

            CreateLabel(panel.transform, "Sculpting Tools", 20, FontStyle.Bold);
            _polyCountLabel = CreateLabel(panel.transform, "Tris: - | Verts: -", 12, FontStyle.Normal);

            CreateLabel(panel.transform, "Brush Strength", 14, FontStyle.Normal);
            _brushStrengthSlider = CreateSlider(panel.transform, 0.01f, 1f, controller.BrushStrength, v => controller.BrushStrength = v,
                "How much effect each pass of the brush has. Also adjustable by holding F and dragging.");

            CreateLabel(panel.transform, "Brush Size", 14, FontStyle.Normal);
            _brushSizeSlider = CreateSlider(panel.transform, controller.BrushSizeMin, controller.BrushSizeMax,
                controller.BrushSize, v => controller.BrushSize = v,
                "Radius of the brush - in screen pixels, or in world units with Screen-Space Size off. Also adjustable by holding S and dragging, or scrolling over the model.");
            CreateToggle(panel.transform, "Screen-Space Size", controller.ScreenSpaceBrushSize,
                v => controller.ScreenSpaceBrushSize = v, out _,
                tooltip: "On (ZBrush's Draw Size): the brush stays the same size on screen as you zoom. Off: the brush has a fixed size on the model.");

            // ZBrush's -100..100 scale; the controller keeps -1..1. See BrushFalloff.Shift.
            CreateLabel(panel.transform, "Focal Shift (Hard <-> Soft)", 14, FontStyle.Normal);
            _focalShiftSlider = CreateSlider(panel.transform, -100f, 100f, controller.FocalShift * 100f,
                v => controller.FocalShift = v / 100f,
                "Where the brush's falloff happens, shown by the cursor's inner ring (where the brush is at half strength). Negative is harder, positive softer. Each brush keeps its own. Also adjustable by holding D and dragging.");

            _positiveToggle = CreateToggle(panel.transform, "Positive (Add)", controller.IsPositive, v =>
            {
                controller.IsPositive = v;
                _positiveToggleLabel.text = v ? "Positive (Add)" : "Negative (Subtract)";
            }, out _positiveToggleLabel, tooltip: "Whether the brush adds or removes material. Hold Ctrl (or use the right mouse button) to invert for one stroke.");

            _accumulateToggle = CreateToggle(panel.transform, "Accumulate", controller.Accumulate, v =>
            {
                controller.Accumulate = v;
                _accumulateToggleLabel.text = v ? "Accumulate" : "Accumulate (Off)";
            }, out _accumulateToggleLabel, tooltip: "Lets repeated passes over the same spot keep building up effect, instead of capping out after the first pass.");

            // Applies to both build-up paths, not just Accumulate-on - see
            // SculptController.accumulateStrength for why the label no longer says "Accumulate".
            CreateLabel(panel.transform, "Build-Up Strength", 14, FontStyle.Normal);
            _accumulateStrengthSlider = CreateSlider(panel.transform, 0.1f, 3f, controller.AccumulateStrength,
                v => controller.AccumulateStrength = v, "How fast repeated passes build up effect.");

            // Shared across every brush (like Lazy Mouse, not per-brush like Accumulate above),
            // so it needs no resync in the brush-changed handler.
            CreateToggle(panel.transform, "Build Up on Hold", controller.BuildUpOnHold,
                v => controller.BuildUpOnHold = v, out _, tooltip: "Keeps applying the brush while the mouse button is held still, not just while it's moving.");

            // Clay-only (see SculptController.surfaceRelax remarks) - fixes hard creases/
            // pinching where two Clay lobes/strokes meet, without a full remesh. Labeled
            // "(Clay)" rather than shown/hidden per brush: it's a single shared field (not
            // per-brush like Accumulate), so it stays visible and just does nothing on the
            // other brushes - the label is there so that isn't a silent surprise.
            CreateLabel(panel.transform, "Surface Relax (Clay)", 14, FontStyle.Normal);
            CreateSlider(panel.transform, 0f, 1f, controller.SurfaceRelax, v => controller.SurfaceRelax = v,
                "Smooths out hard creases and pinching where Clay strokes meet. Only affects the Clay brush.");

            _frontFacingOnlyToggle = CreateToggle(panel.transform, "Front Facing Only", controller.FrontFacingOnly, v =>
            {
                controller.FrontFacingOnly = v;
                _frontFacingOnlyToggleLabel.text = v ? "Front Facing Only" : "Front Facing Only (Off)";
            }, out _frontFacingOnlyToggleLabel, tooltip: "Only affects surface facing the camera - stops a brush from also sculpting the far side of thin geometry.");

            var brushRow = CreateRow(panel.transform);
            var moveButton = CreateButton(brushRow.transform, "Move", () => SetBrushType(BrushType.Move), "Drags the surface around under the brush, like pushing clay.");
            var clayButton = CreateButton(brushRow.transform, "Clay", () => SetBrushType(BrushType.Clay), "Builds up rounded volume, like adding a lump of clay.");
            var smoothButton = CreateButton(brushRow.transform, "Smooth", () => SetBrushType(BrushType.Smooth), "Averages the surface to remove bumps and noise.");
            _moveButtonImage = moveButton.GetComponent<Image>();
            _clayButtonImage = clayButton.GetComponent<Image>();
            _smoothButtonImage = smoothButton.GetComponent<Image>();

            // Second row - the panel is sized for 3 buttons per row (see CreateRow/panel
            // width), so Crease/Mask get their own row rather than squeezing 5 in.
            var brushRow2 = CreateRow(panel.transform);
            var creaseButton = CreateButton(brushRow2.transform, "Crease", () => SetBrushType(BrushType.Crease), "Pinches in a sharp groove, like a fingernail crease.");
            var maskButton = CreateButton(brushRow2.transform, "Mask", () => controller.IsMaskPaintMode = !controller.IsMaskPaintMode,
                "Paints a mask that protects or isolates areas from later brush strokes.");
            _creaseButtonImage = creaseButton.GetComponent<Image>();
            _maskButtonImage = maskButton.GetComponent<Image>();

            // Third row - Inflate joins Crease/Mask's group of "not one of the
            // first three" brushes, same reasoning as brushRow2 above for why it doesn't
            // squeeze into an existing row.
            var brushRow3 = CreateRow(panel.transform);
            var inflateButton = CreateButton(brushRow3.transform, "Inflate", () => SetBrushType(BrushType.Inflate), "Pushes the surface outward along its normal, like inflating a balloon.");
            var flattenButton = CreateButton(brushRow3.transform, "Flatten", () => SetBrushType(BrushType.Flatten), "Flattens the surface to a plane - see Plane Offset below for Fill vs Scrape.");
            var poseButton = CreateButton(brushRow3.transform, "Pose", () => SetBrushType(BrushType.Pose), "Bends a limb-like region about a joint, keeping its volume.");
            _inflateButtonImage = inflateButton.GetComponent<Image>();
            _flattenButtonImage = flattenButton.GetComponent<Image>();
            _poseButtonImage = poseButton.GetComponent<Image>();

            var brushRow4 = CreateRow(panel.transform);
            var standardButton = CreateButton(brushRow4.transform, "Standard", () => SetBrushType(BrushType.Standard),
                "Raises the surface along its average normal - ZBrush's default brush.");
            var layerButton = CreateButton(brushRow4.transform, "Layer", () => SetBrushType(BrushType.Layer),
                "Raises the surface by one even height that crossing your own stroke never doubles - see Layer Height below.");
            var snakeHookButton = CreateButton(brushRow4.transform, "Snakehook", () => SetBrushType(BrushType.SnakeHook),
                "Pulls the surface out after the cursor - drag out horns, tentacles and fingers.");
            _standardButtonImage = standardButton.GetComponent<Image>();
            _layerButtonImage = layerButton.GetComponent<Image>();
            _snakeHookButtonImage = snakeHookButton.GetComponent<Image>();
            RefreshBrushButtons();

            // Collapsed by default, same reasoning as the other shaping foldouts below.
            Transform maskFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Masking", false);
            CreateLabel(maskFoldout, "Hardness (Soft <-> Hard)", 12, FontStyle.Normal);
            _maskHardnessSlider = CreateSlider(maskFoldout, 0f, 1f, controller.MaskHardness, v => controller.MaskHardness = v,
                "Sharpness of the mask edge - low fades gradually, high cuts off sharply. Also adjustable by holding D and dragging while mask painting.");
            var maskActionRow = CreateRow(maskFoldout);
            CreateButton(maskActionRow.transform, "Invert Mask", () => controller.InvertMask(), "Swaps masked and unmasked areas.");
            CreateButton(maskActionRow.transform, "Clear Mask", () =>
            {
                SculptableMesh target = SelectedMesh();
                if (target != null) target.ClearMask();
            }, "Removes the mask from the selected object entirely.");
            var maskFilterRow = CreateRow(maskFoldout);
            CreateButton(maskFilterRow.transform, "Blur", () => FilterSelectedMask(SculptableMesh.MaskFilter.Blur, 2),
                "Softens the mask's edge.");
            CreateButton(maskFilterRow.transform, "Sharpen", () => FilterSelectedMask(SculptableMesh.MaskFilter.Sharpen, 2),
                "Tightens a soft mask edge toward a hard one.");
            var maskGrowRow = CreateRow(maskFoldout);
            CreateButton(maskGrowRow.transform, "Grow", () => FilterSelectedMask(SculptableMesh.MaskFilter.Grow, 2),
                "Expands the mask outward by a couple of edge rings.");
            CreateButton(maskGrowRow.transform, "Shrink", () => FilterSelectedMask(SculptableMesh.MaskFilter.Shrink, 2),
                "Pulls the mask inward by a couple of edge rings.");

            // One shared setting for every brush (not per-brush like Accumulate), so no resync
            // is needed elsewhere in this file - nothing but this panel ever changes it, same as
            // MaskHardness above.
            Transform lazyMouseFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Lazy Mouse", false);
            CreateToggle(lazyMouseFoldout, "Lazy Mouse", controller.LazyMouseEnabled, v => controller.LazyMouseEnabled = v, out _,
                tooltip: "Smooths the brush's path with a trailing tether instead of following the raw cursor - steadies shaky strokes.");
            CreateLabel(lazyMouseFoldout, "Radius (px)", 12, FontStyle.Normal);
            CreateSlider(lazyMouseFoldout, 1f, 150f, controller.LazyMouseRadius, v => controller.LazyMouseRadius = v,
                "How far the cursor must pull ahead of the brush before the tether starts dragging it along.");
            CreateLabel(lazyMouseFoldout, "Smoothing (Springy <-> Taut)", 12, FontStyle.Normal);
            CreateSlider(lazyMouseFoldout, 0.05f, 1f, controller.LazyMouseStrength, v => controller.LazyMouseStrength = v,
                "How tightly the brush follows the tether - low is loose and springy, high is taut and direct.");

            BuildExtractSection(panel.transform);

            // Collapsed by default (see UIFactory.CreateFoldoutSection) - with this section
            // expanded, the top-left panel's height was tall enough to run into the
            // Material panel anchored at the bottom-left corner.
            Transform clayFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Clay Shaping", false);
            CreateLabel(clayFoldout, "Clay Depth", 12, FontStyle.Normal);
            CreateSlider(clayFoldout, 0.1f, 1.5f, controller.ClayHeightFactor, v => controller.ClayHeightFactor = v,
                "How much volume the Clay brush builds up per pass.");

            CreateLabel(clayFoldout, "Tip Shape (Square <-> Round)", 12, FontStyle.Normal);
            CreateSlider(clayFoldout, 0f, 1f, controller.ClayTipRoundness, v => controller.ClayTipRoundness = v,
                "Shape of the Clay brush's footprint - low is a square stamp, high is a round one.");

            // Low = flat-topped strip with a hard rim; high = soft-shouldered pad. See
            // SculptController.clayEdgeSoftness.
            CreateLabel(clayFoldout, "Tip Softness (Flat <-> Domed)", 12, FontStyle.Normal);
            CreateSlider(clayFoldout, 0.05f, 1f, controller.ClayEdgeSoftness, v => controller.ClayEdgeSoftness = v,
                "Edge profile of the Clay brush - low is a flat-topped pad with a hard rim, high is a soft dome.");

            CreateToggle(clayFoldout, "Use Alpha", controller.UseAlpha, v => controller.UseAlpha = v, out _,
                tooltip: "Stamps the selected alpha texture's shape into the Clay brush instead of a plain round tip.");

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
                TooltipSystem.Attach(alphaButton.gameObject, "Selects this alpha shape for the Clay brush.");
            }
            RefreshAlphaButtons();

            CreateLabel(clayFoldout, "Alpha Rotation", 12, FontStyle.Normal);
            CreateSlider(clayFoldout, 0f, 360f, controller.AlphaRotation, v => controller.AlphaRotation = v,
                "Rotates the alpha shape.");
            CreateLabel(clayFoldout, "Alpha Scale", 12, FontStyle.Normal);
            CreateSlider(clayFoldout, 0.3f, 3f, controller.AlphaScale, v => controller.AlphaScale = v,
                "Resizes the alpha shape relative to the brush.");
            CreateToggle(clayFoldout, "Invert Alpha", controller.InvertAlpha, v => controller.InvertAlpha = v, out _,
                tooltip: "Flips the alpha's light/dark areas, inverting where it stamps strongest.");

            // Collapsed by default, same reasoning as "Clay Shaping" above.
            Transform creaseFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Crease Shaping", false);
            CreateLabel(creaseFoldout, "Pinch", 12, FontStyle.Normal);
            CreateSlider(creaseFoldout, 0f, 1f, controller.CreasePinch, v => controller.CreasePinch = v,
                "How sharply the Crease brush pulls geometry together into the groove.");
            CreateLabel(creaseFoldout, "Depth", 12, FontStyle.Normal);
            CreateSlider(creaseFoldout, 0.05f, 1f, controller.CreaseDepthFactor, v => controller.CreaseDepthFactor = v,
                "How deep the Crease brush's groove cuts.");

            // Collapsed by default, same reasoning as "Clay Shaping" above. One slider, because
            // Plane Offset is the only thing that distinguishes Flatten from its Fill/Scrape
            // siblings - everything else it needs (size, strength, polarity) is already in the
            // shared controls at the top of the panel. See SculptController.flattenPlaneOffset.
            Transform flattenFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Flatten Shaping", false);
            CreateLabel(flattenFoldout, "Plane Offset (Scrape <-> Fill)", 12, FontStyle.Normal);
            CreateSlider(flattenFoldout, -0.5f, 0.5f, controller.FlattenPlaneOffset,
                v => controller.FlattenPlaneOffset = v,
                "Where the flatten plane sits relative to the surface - negative scrapes material away, positive fills up to the plane.");

            BuildFalloffSection(panel.transform);

            Transform layerFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Layer / Move", false);
            CreateLabel(layerFoldout, "Layer Height", 12, FontStyle.Normal);
            CreateSlider(layerFoldout, 0.02f, 1f, controller.LayerHeight, v => controller.LayerHeight = v,
                "How far one Layer stroke raises the surface, as a fraction of the brush size.");
            CreateToggle(layerFoldout, "Move: Connected Only", controller.MoveConnectedOnly, v => controller.MoveConnectedOnly = v, out _,
                tooltip: "Move only grabs surface connected to the point under the cursor (ZBrush's Move Topological) - moving one finger leaves its neighbour alone.");

            // Collapsed by default, same reasoning as "Clay Shaping" above.
            Transform poseFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Pose Shaping", false);
            CreateLabel(poseFoldout, "Rigidity (Soft <-> Rigid)", 12, FontStyle.Normal);
            CreateSlider(poseFoldout, 0f, 1f, controller.PoseRigidity, v => controller.PoseRigidity = v,
                "How stiffly the bent region holds its shape - low is soft and droopy, high is rigid.");
            // Blender calls this same idea "Segments" - how many separate places along the limb
            // you can pivot from, instead of every click bending from the same single anchor.
            CreateLabel(poseFoldout, "Segments (Joints along the limb)", 12, FontStyle.Normal);
            CreateSlider(poseFoldout, 1f, 8f, controller.PoseSegments, v => controller.PoseSegments = Mathf.RoundToInt(v),
                "Number of separate joints along the limb you can pivot from, instead of one single anchor.");

            // Collapsed by default, same reasoning as "Clay Shaping" above. Both controls are
            // inert without a stylus - CurrentPressure short-circuits to 1 when Pen.current is
            // null - but the section is always built rather than hidden on no-pen, since a
            // tablet can be plugged in after the UI is constructed.
            Transform pressureFoldout = UIFactory.CreateFoldoutSection(panel.transform, "Stylus Pressure", false);
            CreateLabel(pressureFoldout, "Light-Touch Floor", 12, FontStyle.Normal);
            CreateSlider(pressureFoldout, 0f, 0.5f, controller.PressureFloor, v => controller.PressureFloor = v,
                "Minimum effect a stylus stroke has, even at the very lightest touch.");
            CreateLabel(pressureFoldout, "Curve (Sensitive <-> Gradual)", 12, FontStyle.Normal);
            CreateSlider(pressureFoldout, 0.5f, 3f, controller.PressureCurve, v => controller.PressureCurve = v,
                "How quickly effect ramps up with pressure - low is sensitive to a light touch, high needs more pressure before it responds.");

            // Read through a local that tolerates null rather than dereferencing
            // controller.Mirror three times: an exception thrown from anywhere in BuildUI
            // abandons the REST of the panel silently (everything below this point simply
            // never exists), which is a far worse failure than a couple of toggles starting
            // unticked. SculptController.Mirror now self-heals a missing MirrorController, so
            // this is belt-and-braces for the genuinely empty-scene case.
            MirrorController mirror = controller.Mirror;
            CreateLabel(panel.transform, "Mirror (Selected Object)", 14, FontStyle.Normal);
            _mirrorXToggle = CreateToggle(panel.transform, "Mirror X", mirror != null && mirror.MirrorX,
                v => SetMirrorAxis(0, v), out _, MirrorXColor, "Sculpts both sides of the X axis at once.");
            _mirrorYToggle = CreateToggle(panel.transform, "Mirror Y", mirror != null && mirror.MirrorY,
                v => SetMirrorAxis(1, v), out _, MirrorYColor, "Sculpts both sides of the Y axis at once.");
            _mirrorZToggle = CreateToggle(panel.transform, "Mirror Z", mirror != null && mirror.MirrorZ,
                v => SetMirrorAxis(2, v), out _, MirrorZColor, "Sculpts both sides of the Z axis at once.");
            // Applied scene-wide, not to the selection alone - see
            // MirrorController.SetShowPlanesForAll for why a per-object visibility toggle
            // reads as broken.
            BuildRadialSection(panel.transform, mirror);
            _showPlanesToggle = CreateToggle(panel.transform, "Show Symmetry Guides", mirror == null || mirror.ShowPlanes,
                MirrorController.SetShowPlanesForAll, out _, tooltip: "Draws the active mirror plane(s) and radial axis in the viewport.");

            BuildSymmetrySection(panel.transform);

            CreateToggle(panel.transform, "Wireframe (Scene View)", controller.ShowWireframeGizmo,
                v => controller.ShowWireframeGizmo = v, out _, tooltip: "Draws the mesh's wireframe in the Editor's Scene view (not the Game view).");
            CreateToggle(panel.transform, "Log Ray Hits", controller.LogRayHits,
                v => controller.LogRayHits = v, out _, tooltip: "Prints brush raycast hits to the console - a debugging aid.");

            var undoRedoRow = CreateRow(panel.transform);
            _undoButton = CreateButton(undoRedoRow.transform, "Undo (Z)", () => controller.Undo(), "Undoes the last sculpting action.");
            _redoButton = CreateButton(undoRedoRow.transform, "Redo (Shift+Z)", () => controller.Redo(), "Redoes the last undone action.");

            // Undo depth is a setting rather than a constant because its cost is entirely
            // workload-dependent: brush strokes store only the vertices they touched, so hundreds
            // fit in a few MB, while a single high-resolution Remesh stores the whole mesh twice
            // over. The readout under the slider is the other half of that - EditHistory also
            // enforces a hard memory ceiling regardless of this number, and without seeing the
            // megabytes there is no way to tell which of the two limits you are actually against.
            CreateLabel(panel.transform, "Undo Steps", 14, FontStyle.Normal);
            CreateSlider(panel.transform, EditHistory.MinSteps, EditHistory.HardMaxSteps,
                controller.UndoSteps, v => controller.UndoSteps = Mathf.RoundToInt(v),
                "How many past actions the undo history keeps, up to a hard memory ceiling regardless of this number.");
            // Populated here rather than left for the first Update: RefreshHistoryLabel is
            // throttled, and the panel gets rebuilt from scratch on a scene load, so an empty
            // initial string would leave the readout blank for up to half a second every time.
            _historyLabel = CreateLabel(panel.transform, SculptController.HistorySummary, 11, FontStyle.Italic);
            _nextHistoryRefresh = 0f;

            CreateButton(panel.transform, "Reset Mesh", () => controller.ResetMesh(), "Resets the selected object back to its original unsculpted shape.");

            CreateLabel(panel.transform, "Export", 14, FontStyle.Normal);
            CreateButton(panel.transform, "Export OBJ", () =>
            {
                string path = controller.Export(out bool cancelled);
                _exportStatusLabel.text = path != null ? "Saved to " + path
                    : cancelled ? "Export cancelled"
                    : "Export failed - no mesh yet";
            }, "Saves the selected mesh as an OBJ file - opens a dialog to pick the folder and name.");
            _exportStatusLabel = CreateLabel(panel.transform, "", 11, FontStyle.Italic);

            CreateLabel(panel.transform, "Remesh Resolution", 14, FontStyle.Normal);
            _remeshResolutionSlider = CreateSlider(panel.transform, 4f, SculptController.MaxRemeshResolution, controller.RemeshResolution,
                v => controller.RemeshResolution = Mathf.RoundToInt(v),
                "Voxel density used by Remesh - higher captures finer detail but is slower. Also adjustable by holding R and dragging.");
            CreateButton(panel.transform, "Remesh", () => controller.Remesh(),
                "Rebuilds the mesh on a clean, evenly-spaced grid at the resolution above - fixes stretched/uneven topology from sculpting. Also bound to tapping R. Capped at a 10M-triangle budget, so compact models stop gaining detail before slender ones do.");

            _quadTargetLabel = CreateLabel(panel.transform, "Quad Remesh Target", 14, FontStyle.Normal);
            _quadTargetSlider = CreateSlider(panel.transform,
                Mathf.Log10(SculptController.MinQuadRemeshTarget), Mathf.Log10(SculptController.MaxQuadRemeshTarget),
                Mathf.Log10(controller.QuadRemeshTarget),
                v => controller.QuadRemeshTarget = RoundQuadTarget(Mathf.Pow(10f, v)),
                "How many quads Quad Remesh aims for - the result lands within a few percent.");
            var quadTargetRow = CreateRow(panel.transform);
            CreateButton(quadTargetRow.transform, "Half", () => controller.QuadRemeshTarget /= 2, "Halves the quad target.");
            CreateButton(quadTargetRow.transform, "Double", () => controller.QuadRemeshTarget *= 2, "Doubles the quad target.");
            CreateButton(panel.transform, "Quad Remesh", () => controller.QuadRemesh(),
                "Rebuilds the mesh as evenly-sized quads following the shape, aiming for the target above. Export OBJ then writes the quads. Sculpting keeps the quads; any other topology change (Remesh, Trim, Boolean) turns them back into plain triangles.");
            _remeshReportLabel = CreateLabel(panel.transform, "", 11, FontStyle.Italic);

            CreateLabel(panel.transform,
                "Keys: 1 Move  2 Clay  3 Smooth  4 Crease\n5 Inflate  6 Flatten  7 Pose  8 Standard\n9 Layer  0 Snakehook  M Toggle Mask Paint\nHold Space: radial tool menu (Move/Clay/Smooth/Crease/\nMask/Inflate/Flatten + Strength/Size sliders)\nTap R: Remesh  Hold R + drag: adjust remesh density\nH Box/Lasso Hide  N Box/Lasso Mask  T Box/Lasso Trim\n(Esc cancels a region drag)\nZ Undo  Shift+Z Redo (not Ctrl+Z - that's the Editor's)\nHold S + drag, or Scroll over model: resize brush\nHold F + drag: adjust brush strength (red inner circle)\nHold D + drag: Focal Shift (inner ring; mask hardness in Mask mode)\nLMB Sculpt/Mask | RMB or Ctrl+LMB Invert/Erase\nAlt+LMB Orbit | MMB Pan | Scroll Zoom | Ctrl+Alt+LMB Drag Zoom",
                11, FontStyle.Italic);

        }

        /// Two significant figures, so the slider lands on 4,700 rather than 4,683.
        private static int RoundQuadTarget(float value)
        {
            int magnitude = (int)Mathf.Pow(10f, Mathf.Max(0f, Mathf.Floor(Mathf.Log10(Mathf.Max(value, 1f))) - 1f));
            return Mathf.RoundToInt(value / magnitude) * magnitude;
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
            _inflateButtonImage.color = controller.CurrentBrush == BrushType.Inflate ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _flattenButtonImage.color = controller.CurrentBrush == BrushType.Flatten ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _poseButtonImage.color = controller.CurrentBrush == BrushType.Pose ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _standardButtonImage.color = controller.CurrentBrush == BrushType.Standard ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _layerButtonImage.color = controller.CurrentBrush == BrushType.Layer ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _snakeHookButtonImage.color = controller.CurrentBrush == BrushType.SnakeHook ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            _maskButtonImage.color = controller.IsMaskPaintMode ? UIFactory.MaskActiveColor : UIFactory.InactiveColor;
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

        /// ZBrush's brush Curve: a per-brush falloff drawn by hand. Off, the brush keeps its own
        /// built-in falloff (shown faintly in the graph); on, the curve replaces it.
        private void BuildFalloffSection(Transform parent)
        {
            Transform foldout = UIFactory.CreateFoldoutSection(parent, "Falloff Curve", false);
            _customFalloffToggle = CreateToggle(foldout, "Custom Falloff (this brush)", controller.CustomFalloff,
                v => controller.CustomFalloff = v, out _,
                tooltip: "Replace this brush's falloff with the curve below. Each brush keeps its own curve.");

            var graphGO = new GameObject("FalloffCurve", typeof(RectTransform), typeof(CanvasRenderer), typeof(FalloffCurveGraphic));
            graphGO.transform.SetParent(foldout, false);
            graphGO.AddComponent<LayoutElement>().preferredHeight = 110f;
            var graph = graphGO.GetComponent<FalloffCurveGraphic>();
            graph.Source = () => controller.CurrentFalloffCurve;
            graph.FocalShiftSource = () => controller.IsMaskPaintMode ? 0f : controller.FocalShift;
            graph.raycastTarget = true;

            CreateLabel(foldout, "Left: brush centre   Right: edge\nDrag points - click to add - right-click to remove", 10, FontStyle.Italic);
            var presetRow = CreateRow(foldout);
            foreach (FalloffPreset preset in (FalloffPreset[])System.Enum.GetValues(typeof(FalloffPreset)))
            {
                FalloffPreset p = preset;
                CreateButton(presetRow.transform, p.ToString(), () =>
                {
                    controller.SetFalloffPreset(p);
                    _customFalloffToggle.SetIsOnWithoutNotify(true);
                }, "Start this brush's curve from the " + p + " shape.");
            }
        }

        private void FilterSelectedMask(SculptableMesh.MaskFilter filter, int steps)
        {
            SculptableMesh target = SelectedMesh();
            if (target != null) target.FilterMask(filter, steps);
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
                    () => controller.SymmetryAxis = axis, "Selects this axis as the symmetry plane for the operations below.");
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
                controller.SymmetryToleranceScale, v => controller.SymmetryToleranceScale = v,
                "How far apart two vertices can be and still count as a mirrored pair - widen this if halves built separately aren't matching up.");

            CreateButton(foldout, "Check Symmetry", () => SetSymmetryStatus(controller.SymmetryStatus(), Color.white),
                "Reports how many vertices pair up across the symmetry plane, without changing anything.");

            // Two rows, two different operations, and the split is the point. "Match Up" nudges
            // vertices onto their counterparts and needs the two halves to already correspond;
            // "Cut & Mirror" throws one side away and rebuilds it, and needs nothing at all. The
            // first is non-destructive and keeps mask/topology, so it stays the top row - but it
            // is also the one that can honestly do nothing on a model whose halves were built
            // separately, which is exactly when the row below is the answer.
            CreateLabel(foldout, "Match Up (keeps topology, needs matching halves)", 12, FontStyle.Normal);
            var mirrorRow = CreateRow(foldout);
            Button posToNeg = CreateButton(mirrorRow.transform, "+X to -X",
                () => SetSymmetryStatus(controller.MakeSymmetric(true), UIFactory.StatusOkColor),
                "Nudges the + side's paired vertices to match the - side, keeping topology and mask.");
            Button negToPos = CreateButton(mirrorRow.transform, "-X to +X",
                () => SetSymmetryStatus(controller.MakeSymmetric(false), UIFactory.StatusOkColor),
                "Nudges the - side's paired vertices to match the + side, keeping topology and mask.");
            _symPosToNegLabel = posToNeg.GetComponentInChildren<Text>();
            _symNegToPosLabel = negToPos.GetComponentInChildren<Text>();

            CreateLabel(foldout, "Cut & Mirror (rebuilds the far side, always works)", 12, FontStyle.Normal);
            var cutRow = CreateRow(foldout);
            Button cutPosToNeg = CreateButton(cutRow.transform, "+X to -X",
                () => SetSymmetryStatus(controller.MirrorAndWeld(true), UIFactory.StatusOkColor),
                "Deletes the - side and rebuilds it as an exact mirror of the + side.");
            Button cutNegToPos = CreateButton(cutRow.transform, "-X to +X",
                () => SetSymmetryStatus(controller.MirrorAndWeld(false), UIFactory.StatusOkColor),
                "Deletes the + side and rebuilds it as an exact mirror of the - side.");
            _symCutPosToNegLabel = cutPosToNeg.GetComponentInChildren<Text>();
            _symCutNegToPosLabel = cutNegToPos.GetComponentInChildren<Text>();

            CreateButton(foldout, "Symmetry Cleanup (Snap + Weld)",
                () => SetSymmetryStatus(controller.SymmetryCleanup(), UIFactory.StatusOkColor),
                "Snaps near-matching vertices onto the plane and welds seams along it, without moving either side wholesale.");

            _symmetryStatusLabel = CreateLabel(foldout,
                "Check Symmetry reports how many vertices pair across the plane. If most of them " +
                "don't pair, the halves were built separately - use Cut & Mirror.", 11, FontStyle.Italic);
            _symmetryStatusLabel.color = UIFactory.StatusHintColor;

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
            CreateSlider(foldout, 0.002f, 0.5f, _extract.ThicknessFraction, v => _extract.ThicknessFraction = v,
                "How thick the extracted shell is, as a fraction of the object's size.");

            CreateLabel(foldout, "Offset (sink <-> float)", 12, FontStyle.Normal);
            CreateSlider(foldout, -0.25f, 0.25f, _extract.OffsetFraction, v => _extract.OffsetFraction = v,
                "Moves the extracted shell in or out relative to the masked surface.");

            CreateLabel(foldout, "Edge Falloff (slab <-> feathered)", 12, FontStyle.Normal);
            CreateSlider(foldout, 0f, 1f, _extract.FalloffAmount, v => _extract.FalloffAmount = v,
                "How the shell thins toward its border - 0 is a flat slab edge, higher feathers it thinner.");

            CreateLabel(foldout, "Border Smoothing", 12, FontStyle.Normal);
            CreateSlider(foldout, 0f, 20f, _extract.BorderSmoothing, v => _extract.BorderSmoothing = Mathf.RoundToInt(v),
                "Smoothing passes applied to the extracted shell's border.");

            CreateLabel(foldout, "Surface Smoothing", 12, FontStyle.Normal);
            CreateSlider(foldout, 0f, 20f, _extract.SurfaceSmoothing, v => _extract.SurfaceSmoothing = Mathf.RoundToInt(v),
                "Smoothing passes applied across the whole extracted surface.");

            CreateLabel(foldout, "Shrinkwrap (inner face to body)", 12, FontStyle.Normal);
            CreateSlider(foldout, 0f, 1f, _extract.Shrinkwrap, v => _extract.Shrinkwrap = v,
                "Pulls the shell's inner face back onto the original body's surface.");

            CreateLabel(foldout, "Mask Threshold", 12, FontStyle.Normal);
            CreateSlider(foldout, 0.05f, 0.95f, _extract.MaskThreshold, v => _extract.MaskThreshold = v,
                "How strongly a vertex must be masked to be included in the extraction.");

            CreateToggle(foldout, "Extract Unmasked Instead", _extract.InvertRegion,
                v => _extract.InvertRegion = v, out _, tooltip: "Extracts the UNmasked area instead of the masked one.");

            CreateButton(foldout, "Preview Extract", () => _extract.BeginPreview(), "Shows a live preview of the extracted shell using the settings above.");
            var acceptCancelRow = CreateRow(foldout);
            _extractAcceptButton = CreateButton(acceptCancelRow.transform, "Accept", () => _extract.Accept(), "Turns the preview into a real, separate object.");
            _extractCancelButton = CreateButton(acceptCancelRow.transform, "Cancel", () => _extract.Cancel(), "Discards the preview without creating anything.");

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
                _extractStatusLabel.color = UIFactory.StatusWarnColor;
            }
            else if (previewing)
            {
                _extractStatusLabel.text = $"Preview: {tris:N0} tris. Accept to keep.";
                _extractStatusLabel.color = UIFactory.StatusOkColor;
            }
            else
            {
                _extractStatusLabel.text = "Mask a region, then Preview. Accept makes it a new object.";
                _extractStatusLabel.color = UIFactory.StatusHintColor;
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

        // The panel's compact 20px slider - see UIFactory.CreateCompactSlider, shared with the HUD.
        private Slider CreateSlider(Transform parent, float min, float max, float defaultVal, Action<float> onChange, string tooltip = null) =>
            UIFactory.CreateCompactSlider(parent, min, max, defaultVal, onChange, tooltip);

        private Button CreateButton(Transform parent, string label, Action onClick, string tooltip = null)
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

            TooltipSystem.Attach(go, tooltip);
            return btn;
        }

        private Toggle CreateToggle(Transform parent, string label, bool defaultVal, Action<bool> onChange,
            out Text labelText, Color? checkColor = null, string tooltip = null)
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

            TooltipSystem.Attach(go, tooltip);
            return toggle;
        }
    }
}
