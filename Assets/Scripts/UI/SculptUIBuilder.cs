using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Sculpting
{
    /// Builds the left-hand Sculpt panel purely from code at runtime and wires it to a
    /// SculptController. Pinned at the top: poly count, Undo/Redo and the tool search box. Below,
    /// one UICategory each: Brushes, Brush Settings (only what applies to the brush in hand),
    /// Stroke, Masking, Symmetry, Remesh & Topology, Preferences and Keyboard Shortcuts.
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
        private bool _lastShownMaskMode;

        /// Every brush in the grid, in hotkey order - the label, the key that picks it, and what
        /// it does. The grid, the Brush Settings title and the Brushes summary all read from here.
        private static readonly (BrushType Type, string Name, string Key, string Tooltip)[] Brushes =
        {
            (BrushType.Move, "Move", "1", "Drags the surface around under the brush, like pushing clay."),
            (BrushType.Clay, "Clay", "2", "Builds up rounded volume, like adding a lump of clay."),
            (BrushType.Smooth, "Smooth", "3", "Averages the surface to remove bumps and noise. Hold Shift with any brush to smooth."),
            (BrushType.Crease, "Crease", "4", "Pinches in a sharp groove, like a fingernail crease."),
            (BrushType.Inflate, "Inflate", "5", "Pushes the surface outward along its normal, like inflating a balloon."),
            (BrushType.Flatten, "Flatten", "6", "Flattens the surface to a plane - Plane Offset in Brush Settings picks Fill or Scrape."),
            (BrushType.Pose, "Pose", "7", "Bends a limb-like region about a joint, keeping its volume."),
            (BrushType.Standard, "Standard", "8", "Raises the surface along its average normal - ZBrush's default brush."),
            (BrushType.Layer, "Layer", "9", "Raises the surface by one even height that crossing your own stroke never doubles."),
            (BrushType.SnakeHook, "Snakehook", "0", "Pulls the surface out after the cursor - drag out horns, tentacles and fingers."),
        };

        private readonly System.Collections.Generic.Dictionary<BrushType, Image> _brushButtonImages =
            new System.Collections.Generic.Dictionary<BrushType, Image>();
        // The grid's Mask button and the Masking category's "Mask Paint" button - one state.
        private readonly System.Collections.Generic.List<Image> _maskButtonImages = new System.Collections.Generic.List<Image>();

        // Brush Settings shows only what applies to the brush in hand: one group per brush that
        // has settings of its own, swapped in whenever the brush changes (see RefreshBrushContext).
        private UICategory _brushSettingsCategory;
        private readonly System.Collections.Generic.Dictionary<BrushType, GameObject> _brushContextGroups =
            new System.Collections.Generic.Dictionary<BrushType, GameObject>();
        private GameObject _maskContextGroup;
        private Text _noBrushSettingsLabel;

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
        // Two: one in the Masking category, one in Brush Settings while Mask Paint is on.
        private readonly System.Collections.Generic.List<Slider> _maskHardnessSliders = new System.Collections.Generic.List<Slider>();
        private Text _polyCountLabel;
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

        // Local / World symmetry space (see SymmetrySpace), synced like the rest of this section.
        private readonly Image[] _symmetrySpaceImages = new Image[2];
        private int _lastShownSymmetrySpace = -1;

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
            _lastShownSymmetrySpace = -1;
            _lastShownSymmetryAxis = -1;
            _lastShownExtractPreviewing = false;
            _lastShownExtractTris = -1;
            _lastShownExtractError = "\0";
            _nextHistoryRefresh = 0f;
            _quadTargetShown = -1;
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
            foreach (Slider s in _maskHardnessSliders) if (s != null) s.SetValueWithoutNotify(controller.MaskHardness);

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
                    _polyCountLabel.text = FormatCount(tris) + " tris  " + FormatCount(verts) + " verts";
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
                !mirror.RadialCustomAxis.Equals(_lastShownRadialCustomAxis) ||
                (int)mirror.Space != _lastShownSymmetrySpace;
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

            _lastShownSymmetrySpace = (int)mirror.Space;
            for (int i = 0; i < _symmetrySpaceImages.Length; i++)
                if (_symmetrySpaceImages[i] != null)
                    _symmetrySpaceImages[i].color = i == _lastShownSymmetrySpace ? UIFactory.ActiveColor : UIFactory.InactiveColor;
        }

        private void SetSymmetrySpace(SymmetrySpace space)
        {
            MirrorController mirror = controller.Mirror;
            if (mirror != null) mirror.Space = space;
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
            // In World space the radial axis is a world direction (see SymmetrySpace).
            Vector3 local = mirror.Space == SymmetrySpace.World
                ? cam.transform.forward
                : mirror.transform.InverseTransformDirection(cam.transform.forward);
            if (local.sqrMagnitude < 1e-12f) return;
            mirror.RadialCustomAxis = local.normalized;
            mirror.RadialAxisChoice = RadialAxis.Custom;
        }

        private void BuildRadialSection(Transform panel, MirrorController mirror)
        {
            CreateLabel(panel, "Radial Symmetry", 13, FontStyle.Bold);
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
                    i < 3 ? $"Repeats strokes around the {names[i]} axis - the object's own, or the world's in World space."
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

        // Category accents - each group of the panel has its own colour, on its header stripe and
        // summary, so the panel reads as a handful of labelled blocks rather than one long list.
        private static readonly Color BrushesAccent = new Color(0.3f, 0.6f, 1f);
        private static readonly Color BrushSettingsAccent = new Color(0.35f, 0.82f, 0.9f);
        private static readonly Color StrokeAccent = new Color(0.66f, 0.52f, 1f);
        private static readonly Color SymmetryAccent = new Color(0.95f, 0.45f, 0.62f);
        private static readonly Color RemeshAccent = new Color(0.45f, 0.85f, 0.5f);
        private static readonly Color PrefsAccent = new Color(0.62f, 0.62f, 0.68f);
        private static readonly Color ShortcutsAccent = new Color(0.9f, 0.8f, 0.4f);

        private void BuildUI()
        {
            // Destroys any leftover canvas from a previous build (e.g. SceneGraphUIBuilder's
            // Load Scene "Replace" flow re-running every panel's Start) before making a new
            // one - see UIFactory.DestroyStaleCanvas for why an un-destroyed previous canvas
            // would otherwise leave two stacked, overlapping copies of this panel.
            GameObject staleCanvas = GameObject.Find("SculptCanvas");
            if (staleCanvas != null) DestroyImmediate(staleCanvas);

            _brushButtonImages.Clear();
            _maskButtonImages.Clear();
            _maskHardnessSliders.Clear();
            _brushContextGroups.Clear();

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

            // Capped at the full window height so it never grows past the bottom of the screen;
            // UIFactory.AddScrollingContent still shrinks it back down to fit shorter content.
            Transform content = UIFactory.AddScrollingContent(panelRect, new RectOffset(8, 8, 10, 10), 6f);
            var panel = content.gameObject;

            // ------------------------------------------------ always visible: title, undo, search
            var titleRow = UIFactory.CreateRow(panel.transform, 26f);
            CreateLabel(titleRow.transform, "Sculpt", 20, FontStyle.Bold);
            _polyCountLabel = CreateLabel(titleRow.transform, "Tris: -", 11, FontStyle.Normal);
            _polyCountLabel.alignment = TextAnchor.MiddleRight;
            _polyCountLabel.color = UIFactory.StatusHintColor;

            var undoRedoRow = UIFactory.CreateRow(panel.transform, 28f);
            _undoButton = CreateButton(undoRedoRow.transform, "Undo", () => controller.Undo(), "Undoes the last action.", "Z");
            _redoButton = CreateButton(undoRedoRow.transform, "Redo", () => controller.Redo(), "Redoes the last undone action.", "Shift+Z");

            UICategorySearch.Create(panel.transform);

            BuildBrushesCategory(panel.transform);
            BuildBrushSettingsCategory(panel.transform);
            BuildStrokeCategory(panel.transform);
            BuildMaskingCategory(panel.transform);
            BuildSymmetryCategory(panel.transform);
            BuildRemeshCategory(panel.transform);
            BuildPreferencesCategory(panel.transform);
            BuildShortcutsCategory(panel.transform);

            RefreshBrushButtons();
        }

        // ----------------------------------------------------------------------- brushes

        private void BuildBrushesCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "sculpt.brushes", "Brushes", BrushesAccent, true,
                () => controller.IsMaskPaintMode ? "Mask" : BrushName(controller.CurrentBrush),
                "Pick a brush and set its size and strength.");
            category.Keywords = "brush tool size strength radius focal hardness softness add subtract invert";
            Transform c = category.Content;

            // Three to a row, in hotkey order; the key is printed in each button's corner.
            GameObject row = null;
            for (int i = 0; i < Brushes.Length; i++)
            {
                if (i % 3 == 0) row = CreateRow(c);
                BrushType type = Brushes[i].Type;
                Button b = CreateButton(row.transform, Brushes[i].Name, () => SetBrushType(type),
                    Brushes[i].Tooltip, Brushes[i].Key);
                _brushButtonImages[type] = b.GetComponent<Image>();
            }
            Button mask = CreateButton(row.transform, "Mask", ToggleMaskPaint,
                "Paints a mask that protects areas from later brush strokes. Ctrl or right-drag erases it. Settings in Masking.", "M");
            _maskButtonImages.Add(mask.GetComponent<Image>());

            _brushSizeSlider = CreateLabeledSlider(c, "Size", controller.BrushSizeMin, controller.BrushSizeMax,
                controller.BrushSize, v => controller.BrushSize = v,
                "Radius of the brush - in screen pixels, or in world units with Screen-Space Size off. Also: hold S and drag, or scroll over the model.",
                v => controller.ScreenSpaceBrushSize ? $"{v:0} px" : v.ToString("0.###"));
            _brushStrengthSlider = CreateLabeledSlider(c, "Strength", 0.01f, 1f, controller.BrushStrength,
                v => controller.BrushStrength = v,
                "How much effect each pass of the brush has. Also: hold F and drag (the red inner circle).",
                v => $"{v * 100f:0}%");
            // ZBrush's -100..100 scale; the controller keeps -1..1. See BrushFalloff.Shift.
            _focalShiftSlider = CreateLabeledSlider(c, "Focal Shift (Hard - Soft)", -100f, 100f, controller.FocalShift * 100f,
                v => controller.FocalShift = v / 100f,
                "Where the brush's falloff happens, shown by the cursor's inner ring (where the brush is at half strength). Negative is harder, positive softer. Each brush keeps its own. Also: hold D and drag.",
                v => v.ToString("0"));

            _positiveToggle = CreateToggle(c, "Positive (Add)", controller.IsPositive, v =>
            {
                controller.IsPositive = v;
                _positiveToggleLabel.text = v ? "Positive (Add)" : "Negative (Subtract)";
            }, out _positiveToggleLabel, tooltip: "Whether the brush adds or removes material. Hold Ctrl (or use the right mouse button) to invert for one stroke.");
            CreateToggle(c, "Screen-Space Size", controller.ScreenSpaceBrushSize,
                v => controller.ScreenSpaceBrushSize = v, out _,
                tooltip: "On (ZBrush's Draw Size): the brush stays the same size on screen as you zoom. Off: the brush has a fixed size on the model.");
        }

        private static string BrushName(BrushType type)
        {
            foreach (var b in Brushes) if (b.Type == type) return b.Name;
            return type.ToString();
        }

        private void ToggleMaskPaint()
        {
            controller.IsMaskPaintMode = !controller.IsMaskPaintMode;
            RefreshBrushButtons();
        }

        // ---------------------------------------------------------------- brush settings

        /// Settings that belong to the brush in hand. The shared per-brush ones (Accumulate,
        /// Front Facing Only, the falloff curve) always show; below them sits a group for the
        /// current brush only - Clay's tip and alpha, Crease's pinch, Pose's rigidity... - so
        /// nobody has to work out which of a dozen sliders affects what they're holding.
        private void BuildBrushSettingsCategory(Transform panel)
        {
            _brushSettingsCategory = UICategory.Create(panel, "sculpt.brushSettings", "Brush Settings", BrushSettingsAccent, true,
                tooltip: "Settings for the brush you're holding - each brush remembers its own.");
            _brushSettingsCategory.Keywords = "accumulate build up front facing backface falloff curve clay crease flatten layer move pose alpha";
            Transform c = _brushSettingsCategory.Content;

            _accumulateToggle = CreateToggle(c, "Accumulate", controller.Accumulate, v =>
            {
                controller.Accumulate = v;
                _accumulateToggleLabel.text = v ? "Accumulate" : "Accumulate (Off)";
            }, out _accumulateToggleLabel, tooltip: "Lets repeated passes over the same spot keep building up effect, instead of capping out after the first pass.");
            // Applies to both build-up paths, not just Accumulate-on - see
            // SculptController.accumulateStrength for why the label no longer says "Accumulate".
            _accumulateStrengthSlider = CreateLabeledSlider(c, "Build-Up Strength", 0.1f, 3f, controller.AccumulateStrength,
                v => controller.AccumulateStrength = v, "How fast repeated passes build up effect.");
            _frontFacingOnlyToggle = CreateToggle(c, "Front Facing Only", controller.FrontFacingOnly, v =>
            {
                controller.FrontFacingOnly = v;
                _frontFacingOnlyToggleLabel.text = v ? "Front Facing Only" : "Front Facing Only (Off)";
            }, out _frontFacingOnlyToggleLabel, tooltip: "Only affects surface facing the camera - stops a brush from also sculpting the far side of thin geometry.");

            // --- Clay
            Transform clay = CreateGroup(c, BrushType.Clay);
            CreateLabeledSlider(clay, "Clay Depth", 0.1f, 1.5f, controller.ClayHeightFactor, v => controller.ClayHeightFactor = v,
                "How much volume the Clay brush builds up per pass.");
            CreateLabeledSlider(clay, "Tip Shape (Square - Round)", 0f, 1f, controller.ClayTipRoundness, v => controller.ClayTipRoundness = v,
                "Shape of the Clay brush's footprint - low is a square stamp, high is a round one.");
            // Low = flat-topped strip with a hard rim; high = soft-shouldered pad. See
            // SculptController.clayEdgeSoftness.
            CreateLabeledSlider(clay, "Tip Softness (Flat - Domed)", 0.05f, 1f, controller.ClayEdgeSoftness, v => controller.ClayEdgeSoftness = v,
                "Edge profile of the Clay brush - low is a flat-topped pad with a hard rim, high is a soft dome.");
            // Clay-only (see SculptController.surfaceRelax remarks) - fixes hard creases/pinching
            // where two Clay lobes/strokes meet, without a full remesh.
            CreateLabeledSlider(clay, "Surface Relax", 0f, 1f, controller.SurfaceRelax, v => controller.SurfaceRelax = v,
                "Smooths out hard creases and pinching where Clay strokes meet.");

            Transform alpha = UIFactory.CreateFoldoutSection(clay, "Alpha (Stamp Shape)", false);
            CreateToggle(alpha, "Use Alpha", controller.UseAlpha, v => controller.UseAlpha = v, out _,
                tooltip: "Stamps the selected alpha texture's shape into the Clay brush instead of a plain round tip.");
            var alphaRow = CreateRow(alpha);
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
            CreateLabeledSlider(alpha, "Alpha Rotation", 0f, 360f, controller.AlphaRotation, v => controller.AlphaRotation = v,
                "Rotates the alpha shape.", v => v.ToString("0"));
            CreateLabeledSlider(alpha, "Alpha Scale", 0.3f, 3f, controller.AlphaScale, v => controller.AlphaScale = v,
                "Resizes the alpha shape relative to the brush.");
            CreateToggle(alpha, "Invert Alpha", controller.InvertAlpha, v => controller.InvertAlpha = v, out _,
                tooltip: "Flips the alpha's light/dark areas, inverting where it stamps strongest.");

            // --- Crease
            Transform crease = CreateGroup(c, BrushType.Crease);
            CreateLabeledSlider(crease, "Pinch", 0f, 1f, controller.CreasePinch, v => controller.CreasePinch = v,
                "How sharply the Crease brush pulls geometry together into the groove.");
            CreateLabeledSlider(crease, "Depth", 0.05f, 1f, controller.CreaseDepthFactor, v => controller.CreaseDepthFactor = v,
                "How deep the Crease brush's groove cuts.");

            // --- Flatten: Plane Offset is the only thing that distinguishes it from its Fill/Scrape
            // siblings. See SculptController.flattenPlaneOffset.
            Transform flatten = CreateGroup(c, BrushType.Flatten);
            CreateLabeledSlider(flatten, "Plane Offset (Scrape - Fill)", -0.5f, 0.5f, controller.FlattenPlaneOffset,
                v => controller.FlattenPlaneOffset = v,
                "Where the flatten plane sits relative to the surface - negative scrapes material away, positive fills up to the plane.");

            // --- Layer
            Transform layer = CreateGroup(c, BrushType.Layer);
            CreateLabeledSlider(layer, "Layer Height", 0.02f, 1f, controller.LayerHeight, v => controller.LayerHeight = v,
                "How far one Layer stroke raises the surface, as a fraction of the brush size.");

            // --- Move
            Transform move = CreateGroup(c, BrushType.Move);
            CreateToggle(move, "Connected Only", controller.MoveConnectedOnly, v => controller.MoveConnectedOnly = v, out _,
                tooltip: "Move only grabs surface connected to the point under the cursor (ZBrush's Move Topological) - moving one finger leaves its neighbour alone.");

            // --- Pose
            Transform pose = CreateGroup(c, BrushType.Pose);
            CreateLabeledSlider(pose, "Rigidity (Soft - Rigid)", 0f, 1f, controller.PoseRigidity, v => controller.PoseRigidity = v,
                "How stiffly the bent region holds its shape - low is soft and droopy, high is rigid.");
            // Blender calls this same idea "Segments" - how many separate places along the limb
            // you can pivot from, instead of every click bending from the same single anchor.
            Slider segments = CreateLabeledSlider(pose, "Segments (Joints)", 1f, 8f, controller.PoseSegments,
                v => controller.PoseSegments = Mathf.RoundToInt(v),
                "Number of separate joints along the limb you can pivot from, instead of one single anchor.", v => v.ToString("0"));
            segments.wholeNumbers = true;

            // --- Mask paint
            _maskContextGroup = CreateGroupObject(c);
            _maskHardnessSliders.Add(CreateLabeledSlider(_maskContextGroup.transform, "Mask Hardness (Soft - Hard)", 0f, 1f,
                controller.MaskHardness, v => controller.MaskHardness = v,
                "Sharpness of the mask edge - low fades gradually, high cuts off sharply. Also: hold D and drag while mask painting."));
            CreateLabel(_maskContextGroup.transform, "More mask tools are in Masking below.", 11, FontStyle.Italic).color = UIFactory.StatusHintColor;

            _noBrushSettingsLabel = CreateLabel(c, "This brush has no extra settings.", 11, FontStyle.Italic);
            _noBrushSettingsLabel.color = UIFactory.StatusHintColor;

            BuildFalloffSection(c);
            RefreshBrushContext();
        }

        /// A vertical block inside Brush Settings shown only while `brush` is the current one.
        private Transform CreateGroup(Transform parent, BrushType brush)
        {
            GameObject group = CreateGroupObject(parent);
            _brushContextGroups[brush] = group;
            return group.transform;
        }

        private static GameObject CreateGroupObject(Transform parent)
        {
            var go = new GameObject("Group", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var vlg = go.AddComponent<VerticalLayoutGroup>();
            vlg.spacing = 7;
            vlg.padding = new RectOffset(0, 0, 4, 0);
            vlg.childControlWidth = true;
            vlg.childControlHeight = true;
            vlg.childForceExpandWidth = true;
            vlg.childForceExpandHeight = false;
            return go;
        }

        /// Shows the group for the brush in hand, and names the category after it.
        private void RefreshBrushContext()
        {
            if (_brushSettingsCategory == null) return;
            bool mask = controller.IsMaskPaintMode;
            bool any = mask;
            foreach (var pair in _brushContextGroups)
            {
                bool show = !mask && pair.Key == controller.CurrentBrush;
                if (pair.Value.activeSelf != show) pair.Value.SetActive(show);
                any |= show;
            }
            if (_maskContextGroup != null && _maskContextGroup.activeSelf != mask) _maskContextGroup.SetActive(mask);
            if (_noBrushSettingsLabel != null) _noBrushSettingsLabel.gameObject.SetActive(!any);
            _brushSettingsCategory.SetTitle((mask ? "Mask" : BrushName(controller.CurrentBrush)) + " Settings");
        }

        // ------------------------------------------------------------------------ stroke

        private void BuildStrokeCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "sculpt.stroke", "Stroke", StrokeAccent, false,
                () => controller.LazyMouseEnabled ? "Lazy Mouse" : string.Empty,
                "How strokes are drawn - steadying, build-up and stylus pressure. Shared by every brush.");
            category.Keywords = "lazy mouse steady smooth stroke tether pressure pen tablet stylus hold";
            Transform c = category.Content;

            // Shared across every brush (like Lazy Mouse, not per-brush like Accumulate).
            CreateToggle(c, "Build Up on Hold", controller.BuildUpOnHold,
                v => controller.BuildUpOnHold = v, out _, tooltip: "Keeps applying the brush while the mouse button is held still, not just while it's moving.");

            CreateToggle(c, "Lazy Mouse", controller.LazyMouseEnabled, v => controller.LazyMouseEnabled = v, out _,
                tooltip: "Smooths the brush's path with a trailing tether instead of following the raw cursor - steadies shaky strokes.");
            CreateLabeledSlider(c, "Lazy Radius", 1f, 150f, controller.LazyMouseRadius, v => controller.LazyMouseRadius = v,
                "How far the cursor must pull ahead of the brush before the tether starts dragging it along.", v => $"{v:0} px");
            CreateLabeledSlider(c, "Lazy Smoothing", 0.05f, 1f, controller.LazyMouseStrength, v => controller.LazyMouseStrength = v,
                "How tightly the brush follows the tether - low is loose and springy, high is taut and direct.");

            // Both controls are inert without a stylus - CurrentPressure short-circuits to 1 when
            // Pen.current is null - but always built, since a tablet can be plugged in later.
            Transform pressure = UIFactory.CreateFoldoutSection(c, "Stylus Pressure", false);
            CreateLabeledSlider(pressure, "Light-Touch Floor", 0f, 0.5f, controller.PressureFloor, v => controller.PressureFloor = v,
                "Minimum effect a stylus stroke has, even at the very lightest touch.");
            CreateLabeledSlider(pressure, "Curve (Sensitive - Gradual)", 0.5f, 3f, controller.PressureCurve, v => controller.PressureCurve = v,
                "How quickly effect ramps up with pressure - low is sensitive to a light touch, high needs more pressure before it responds.");
        }

        // ----------------------------------------------------------------------- masking

        private void BuildMaskingCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "sculpt.masking", "Masking", UIFactory.MaskActiveColor, false,
                () => controller.IsMaskPaintMode ? "Painting" : string.Empty,
                "Protect parts of the model from brushes, and pull masked areas off as new objects.");
            category.Keywords = "mask protect isolate freeze extract shell invert blur sharpen grow shrink";
            Transform c = category.Content;

            CreateLabel(c, "Paint with the Mask brush (M), or drag a box/lasso with N.", 11, FontStyle.Italic).color = UIFactory.StatusHintColor;
            Button paint = CreateButton(c, "Mask Paint", ToggleMaskPaint, "Turns mask painting on or off.", "M");
            _maskButtonImages.Add(paint.GetComponent<Image>());

            _maskHardnessSliders.Add(CreateLabeledSlider(c, "Hardness (Soft - Hard)", 0f, 1f, controller.MaskHardness,
                v => controller.MaskHardness = v,
                "Sharpness of the mask edge - low fades gradually, high cuts off sharply. Also: hold D and drag while mask painting."));

            var actionRow = CreateRow(c);
            CreateButton(actionRow.transform, "Invert", () => controller.InvertMask(), "Swaps masked and unmasked areas.");
            CreateButton(actionRow.transform, "Clear", () =>
            {
                SculptableMesh target = SelectedMesh();
                if (target != null) target.ClearMask();
            }, "Removes the mask from the selected object entirely.");
            var filterRow = CreateRow(c);
            CreateButton(filterRow.transform, "Blur", () => FilterSelectedMask(SculptableMesh.MaskFilter.Blur, 2), "Softens the mask's edge.");
            CreateButton(filterRow.transform, "Sharpen", () => FilterSelectedMask(SculptableMesh.MaskFilter.Sharpen, 2),
                "Tightens a soft mask edge toward a hard one.");
            var growRow = CreateRow(c);
            CreateButton(growRow.transform, "Grow", () => FilterSelectedMask(SculptableMesh.MaskFilter.Grow, 2),
                "Expands the mask outward by a couple of edge rings.");
            CreateButton(growRow.transform, "Shrink", () => FilterSelectedMask(SculptableMesh.MaskFilter.Shrink, 2),
                "Pulls the mask inward by a couple of edge rings.");

            // The mask IS extract's input - paint a mask, open this, dial it in, Accept.
            BuildExtractSection(c);
        }

        // ---------------------------------------------------------------------- symmetry

        private void BuildSymmetryCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "sculpt.symmetry", "Symmetry", SymmetryAccent, true,
                SymmetrySummary, "Sculpt both sides at once, and repair a model's symmetry. Applies to the selected object.");
            category.Keywords = "mirror symmetry radial repair cut weld plane axis x y z guides";
            Transform c = category.Content;

            // Read through a local that tolerates null rather than dereferencing controller.Mirror
            // repeatedly: an exception thrown from anywhere in BuildUI abandons the REST of the
            // panel silently. SculptController.Mirror self-heals a missing MirrorController, so
            // this is belt-and-braces for the genuinely empty-scene case.
            MirrorController mirror = controller.Mirror;

            // Nomad's Local/World: where the planes and the radial axis sit.
            CreateLabel(c, "Symmetry space", 12, FontStyle.Normal);
            var spaceRow = CreateRow(c);
            _symmetrySpaceImages[0] = CreateButton(spaceRow.transform, "Local", () => SetSymmetrySpace(SymmetrySpace.Local),
                "Symmetry through the object's own centre along its own axes - it moves and turns with the object.")
                .GetComponent<Image>();
            _symmetrySpaceImages[1] = CreateButton(spaceRow.transform, "World", () => SetSymmetrySpace(SymmetrySpace.World),
                "Symmetry through the world origin along the world axes - it stays put while the object moves through it. " +
                "Pairs with Mirror in the Scene panel: parts placed either side of the world origin sculpt symmetrically.")
                .GetComponent<Image>();
            int space = mirror != null ? (int)mirror.Space : 0;
            for (int i = 0; i < 2; i++)
                _symmetrySpaceImages[i].color = i == space ? UIFactory.ActiveColor : UIFactory.InactiveColor;

            CreateLabel(c, "Mirror strokes across", 12, FontStyle.Normal);
            var mirrorRow = UIFactory.CreateRow(c, 24f);
            _mirrorXToggle = CreateToggle(mirrorRow.transform, "X", mirror != null && mirror.MirrorX,
                v => SetMirrorAxis(0, v), out _, MirrorXColor, "Sculpts both sides of the X axis at once. Hotkey: X.");
            _mirrorYToggle = CreateToggle(mirrorRow.transform, "Y", mirror != null && mirror.MirrorY,
                v => SetMirrorAxis(1, v), out _, MirrorYColor, "Sculpts both sides of the Y axis at once.");
            _mirrorZToggle = CreateToggle(mirrorRow.transform, "Z", mirror != null && mirror.MirrorZ,
                v => SetMirrorAxis(2, v), out _, MirrorZColor, "Sculpts both sides of the Z axis at once.");

            BuildRadialSection(c, mirror);
            // Applied scene-wide, not to the selection alone - see
            // MirrorController.SetShowPlanesForAll for why a per-object visibility toggle reads
            // as broken.
            _showPlanesToggle = CreateToggle(c, "Show Symmetry Guides", mirror == null || mirror.ShowPlanes,
                MirrorController.SetShowPlanesForAll, out _, tooltip: "Draws the active mirror plane(s) and radial axis in the viewport.");

            BuildSymmetrySection(c);
        }

        private string SymmetrySummary()
        {
            MirrorController m = controller.Mirror;
            if (m == null) return string.Empty;
            string s = (m.MirrorX ? "X" : string.Empty) + (m.MirrorY ? "Y" : string.Empty) + (m.MirrorZ ? "Z" : string.Empty);
            if (m.Radial) s += (s.Length > 0 ? " + " : string.Empty) + "Radial " + m.RadialCount;
            if (s.Length > 0 && m.Space == SymmetrySpace.World) s += " (World)";
            return s.Length > 0 ? s : "Off";
        }

        // ------------------------------------------------------------------------ remesh

        private void BuildRemeshCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "sculpt.remesh", "Remesh & Topology", RemeshAccent, false,
                () => FormatCount(controller.TriangleCount) + " tris",
                "Rebuild the selected mesh with clean, even topology.");
            category.Keywords = "remesh voxel quad retopology topology resolution density polygons";
            Transform c = category.Content;

            CreateLabel(c, "Voxel Remesh", 13, FontStyle.Bold);
            _remeshResolutionSlider = CreateLabeledSlider(c, "Resolution", 4f, SculptController.MaxRemeshResolution, controller.RemeshResolution,
                v => controller.RemeshResolution = Mathf.RoundToInt(v),
                "Voxel density used by Remesh - higher captures finer detail but is slower. Also: hold R and drag.", v => v.ToString("0"));
            CreateButton(c, "Remesh", () => controller.Remesh(),
                "Rebuilds the mesh on a clean, evenly-spaced grid at the resolution above - fixes stretched/uneven topology from sculpting. Capped at a 10M-triangle budget, so compact models stop gaining detail before slender ones do.",
                "R");

            CreateLabel(c, "Quad Remesh", 13, FontStyle.Bold);
            _quadTargetLabel = CreateLabel(c, "Quad Remesh Target", 12, FontStyle.Normal);
            _quadTargetSlider = CreateSlider(c,
                Mathf.Log10(SculptController.MinQuadRemeshTarget), Mathf.Log10(SculptController.MaxQuadRemeshTarget),
                Mathf.Log10(controller.QuadRemeshTarget),
                v => controller.QuadRemeshTarget = RoundQuadTarget(Mathf.Pow(10f, v)),
                "How many quads Quad Remesh aims for - the result lands within a few percent.");
            var quadTargetRow = CreateRow(c);
            CreateButton(quadTargetRow.transform, "Half", () => controller.QuadRemeshTarget /= 2, "Halves the quad target.");
            CreateButton(quadTargetRow.transform, "Double", () => controller.QuadRemeshTarget *= 2, "Doubles the quad target.");
            CreateButton(c, "Quad Remesh", () => controller.QuadRemesh(),
                "Rebuilds the mesh as evenly-sized quads following the shape, aiming for the target above. Export OBJ then writes the quads. Sculpting keeps the quads; any other topology change (Remesh, Trim, Boolean) turns them back into plain triangles.");
            _remeshReportLabel = CreateLabel(c, "", 11, FontStyle.Italic);
        }

        private static string FormatCount(int n) =>
            n >= 1000000 ? (n / 1000000f).ToString("0.0") + "M" : n >= 1000 ? (n / 1000f).ToString("0") + "k" : n.ToString();

        // ------------------------------------------------------------------- preferences

        private void BuildPreferencesCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "sculpt.prefs", "Preferences", PrefsAccent, false,
                tooltip: "Undo history size and debugging aids.");
            category.Keywords = "undo history steps memory settings debug wireframe log";
            Transform c = category.Content;

            // Undo depth is a setting rather than a constant because its cost is entirely
            // workload-dependent: brush strokes store only the vertices they touched, while a
            // single high-resolution Remesh stores the whole mesh twice over. The readout under
            // the slider is the other half of that - EditHistory also enforces a hard memory
            // ceiling regardless of this number.
            Slider steps = CreateLabeledSlider(c, "Undo Steps", EditHistory.MinSteps, EditHistory.HardMaxSteps,
                controller.UndoSteps, v => controller.UndoSteps = Mathf.RoundToInt(v),
                "How many past actions the undo history keeps, up to a hard memory ceiling regardless of this number.", v => v.ToString("0"));
            steps.wholeNumbers = true;
            // Populated here rather than left for the first Update: RefreshHistoryLabel is
            // throttled, and the panel gets rebuilt from scratch on a scene load.
            _historyLabel = CreateLabel(c, SculptController.HistorySummary, 11, FontStyle.Italic);
            _historyLabel.color = UIFactory.StatusHintColor;
            _nextHistoryRefresh = 0f;

            CreateToggle(c, "Wireframe (Scene View)", controller.ShowWireframeGizmo,
                v => controller.ShowWireframeGizmo = v, out _, tooltip: "Draws the mesh's wireframe in the Editor's Scene view (not the Game view).");
            CreateToggle(c, "Log Ray Hits", controller.LogRayHits,
                v => controller.LogRayHits = v, out _, tooltip: "Prints brush raycast hits to the console - a debugging aid.");
        }

        // --------------------------------------------------------------------- shortcuts

        private static readonly Color ShortcutKeyColor = new Color(0.95f, 0.85f, 0.5f);

        private void BuildShortcutsCategory(Transform panel)
        {
            UICategory category = UICategory.Create(panel, "sculpt.shortcuts", "Keyboard Shortcuts", ShortcutsAccent, false,
                tooltip: "Every hotkey in one place.");
            category.Keywords = "keys hotkeys shortcuts keyboard help controls mouse";
            Transform c = category.Content;

            ShortcutHeading(c, "Brushes");
            Shortcut(c, "1 - 0", "Pick a brush");
            Shortcut(c, "M", "Mask paint on / off");
            Shortcut(c, "Hold Shift", "Smooth, while held");
            Shortcut(c, "Hold Space", "Radial brush menu");
            Shortcut(c, "Shift+Space", "Radial region menu");

            ShortcutHeading(c, "Adjust the brush");
            Shortcut(c, "S + drag", "Size (or scroll)");
            Shortcut(c, "F + drag", "Strength");
            Shortcut(c, "D + drag", "Focal shift");

            ShortcutHeading(c, "Sculpt");
            Shortcut(c, "Left drag", "Sculpt or paint mask");
            Shortcut(c, "Right / Ctrl", "Invert or erase");
            Shortcut(c, "X", "Mirror X on / off");

            ShortcutHeading(c, "Regions (box / lasso)");
            Shortcut(c, "H", "Hide");
            Shortcut(c, "N", "Mask");
            Shortcut(c, "T", "Trim");
            Shortcut(c, "Esc", "Cancel a region drag");

            ShortcutHeading(c, "Mesh & history");
            Shortcut(c, "Tap R", "Remesh");
            Shortcut(c, "R + drag", "Remesh density");
            Shortcut(c, "Z", "Undo");
            Shortcut(c, "Shift+Z", "Redo");
            Shortcut(c, "Delete", "Delete selected object");

            ShortcutHeading(c, "View");
            Shortcut(c, "Alt + left", "Orbit");
            Shortcut(c, "Middle drag", "Pan");
            Shortcut(c, "Scroll", "Zoom");
            Shortcut(c, "Ctrl+Alt+left", "Drag zoom");
            Shortcut(c, "O", "Turntable spin");
            Shortcut(c, "Tab", "Hide all panels");
            Shortcut(c, "F11", "Fullscreen");
        }

        private void ShortcutHeading(Transform parent, string text)
        {
            Text t = CreateLabel(parent, text, 12, FontStyle.Bold);
            t.color = ShortcutsAccent;
        }

        private void Shortcut(Transform parent, string key, string action)
        {
            GameObject row = UIFactory.CreateRow(parent, 16f);
            Text k = CreateLabel(row.transform, key, 11, FontStyle.Bold);
            k.color = ShortcutKeyColor;
            var kl = k.GetComponent<LayoutElement>();
            kl.preferredWidth = 88;
            kl.flexibleWidth = 0;
            Text a = CreateLabel(row.transform, action, 11, FontStyle.Normal);
            a.GetComponent<LayoutElement>().flexibleWidth = 1;
            k.horizontalOverflow = a.horizontalOverflow = HorizontalWrapMode.Overflow;
            // HorizontalLayoutGroup's force-expand would split the row 50/50; widths decide instead.
            row.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = false;
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

        /// Clicking a brush also leaves mask painting: in mask mode the brush grid still shows
        /// which brush you'll return to, so a click that only changed that would look dead.
        private void SetBrushType(BrushType type)
        {
            controller.CurrentBrush = type;
            if (controller.IsMaskPaintMode) controller.IsMaskPaintMode = false;
            RefreshBrushButtons();
        }

        private void RefreshBrushButtons()
        {
            foreach (var pair in _brushButtonImages)
                if (pair.Value != null)
                    pair.Value.color = controller.CurrentBrush == pair.Key ? UIFactory.ActiveColor : UIFactory.InactiveColor;
            foreach (Image image in _maskButtonImages)
                if (image != null) image.color = controller.IsMaskPaintMode ? UIFactory.MaskActiveColor : UIFactory.InactiveColor;
            RefreshBrushContext();
        }

        /// A caption, a live value readout on the right, and the slider under them. The readout
        /// follows the slider itself (see UIFactory.AddValueReadout), so hotkey drags that move
        /// a polled slider update it too.
        private Slider CreateLabeledSlider(Transform parent, string label, float min, float max, float value,
                                           Action<float> onChange, string tooltip, Func<float, string> format = null)
        {
            GameObject row = UIFactory.CreateRow(parent, 16f);
            row.GetComponent<HorizontalLayoutGroup>().childForceExpandWidth = false;
            Text caption = CreateLabel(row.transform, label, 12, FontStyle.Normal);
            caption.GetComponent<LayoutElement>().flexibleWidth = 1;
            TooltipSystem.Attach(caption.gameObject, tooltip);
            Text readout = CreateLabel(row.transform, string.Empty, 11, FontStyle.Normal);
            readout.alignment = TextAnchor.MiddleRight;
            readout.color = UIFactory.StatusHintColor;
            readout.raycastTarget = false;
            var readoutLayout = readout.GetComponent<LayoutElement>();
            readoutLayout.preferredWidth = 46;
            readoutLayout.flexibleWidth = 0;

            Slider slider = CreateSlider(parent, min, max, value, onChange, tooltip);
            UIFactory.AddValueReadout(slider, readout, format ?? (v => v.ToString("0.00")));
            return slider;
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

        /// `hotkey` (optional) is printed small in the button's top-right corner, so the grid
        /// teaches its own shortcuts.
        private Button CreateButton(Transform parent, string label, Action onClick, string tooltip = null, string hotkey = null)
        {
            var go = new GameObject("Button_" + label, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = UIFactory.InactiveColor;
            go.AddComponent<LayoutElement>().preferredHeight = 32;
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => onClick());
            UIFactory.AddHoverGlow(go);


            var textGO = new GameObject("Text", typeof(RectTransform));
            textGO.transform.SetParent(go.transform, false);
            var textRect = textGO.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.sizeDelta = Vector2.zero;
            var text = textGO.AddComponent<Text>();
            text.font = _font;
            text.fontSize = 13;
            text.alignment = TextAnchor.MiddleCenter;
            text.color = Color.white;
            text.text = label;

            // After the label, so GetComponentInChildren<Text>() still finds the caption first.
            if (!string.IsNullOrEmpty(hotkey))
            {
                var keyGO = new GameObject("Hotkey", typeof(RectTransform));
                keyGO.transform.SetParent(go.transform, false);
                var keyRect = keyGO.GetComponent<RectTransform>();
                keyRect.anchorMin = Vector2.zero;
                keyRect.anchorMax = Vector2.one;
                keyRect.offsetMin = new Vector2(2, 1);
                keyRect.offsetMax = new Vector2(-3, -1);
                var key = keyGO.AddComponent<Text>();
                key.font = _font;
                key.fontSize = 9;
                key.alignment = TextAnchor.UpperRight;
                key.color = new Color(1f, 1f, 1f, 0.45f);
                key.text = hotkey;
                key.raycastTarget = false;
                tooltip = string.IsNullOrEmpty(tooltip) ? "Hotkey: " + hotkey : tooltip + " Hotkey: " + hotkey + ".";
            }

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
