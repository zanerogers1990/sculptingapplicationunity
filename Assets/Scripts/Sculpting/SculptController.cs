using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Raycasts from the camera into the sculptable mesh and deforms vertices under the
    /// cursor with the Move, Clay, or Smooth brush. Clay eases vertices toward a plateau along
    /// the hit normal - left mouse with the current positive/negative setting, right mouse
    /// inverted (same convention as most sculpting apps). Move instead grabs whatever the
    /// brush is touching on mouse-down and drags it 1:1 with the cursor along a plane facing
    /// the camera, tracked via screen-space delta rather than a live raycast each frame - so
    /// the grabbed region keeps following the cursor even once it drifts off the mesh's
    /// silhouette. Smooth relaxes vertices toward their mesh-neighbor average (see
    /// SculptableMesh.GetNeighborAverage). 1/2/3 switch brushes; holding S resizes the brush
    /// (drag horizontally) instead of sculpting, shown via a ZBrush-style popup gauge (see
    /// SculptUIBuilder). When MirrorController has any axis enabled, every brush application
    /// is repeated at each mirrored local-space position (see MirrorController.GetMirrorSigns)
    /// so strokes land symmetrically.
    /// No longer [RequireComponent]d on SculptableMesh/MirrorController - this component now
    /// lives once on a persistent object (SceneSystems) and follows whichever object is
    /// selected (see Target/SyncSelectionTarget) instead of hardcoding a single sculpted mesh.
    ///
    /// Split across partial files by concern:
    ///   SculptController.cs           serialized settings, public API, lifecycle, selection sync,
    ///                                 and the brush dispatch in HandleSculptInput
    ///   SculptController.Brushes.cs   each brush's input handler, stroke pacing and apply paths
    ///   SculptController.Jobs.cs      Burst jobs, their shared math, and the native scratch
    ///   SculptController.Input.cs     hotkeys, gauges, pen pressure, Lazy Mouse, the brush cursor
    ///   SculptController.Settings.cs  settings save/load, Reset/Remesh, symmetry repair, Export
    public partial class SculptController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Camera cam;
        // Synced from Target every frame a selection change is detected (SyncSelectionTarget) -
        // every brush handler below still reads these two fields directly, unchanged, so the
        // ~60 existing call sites across this file didn't need touching one by one.
        [SerializeField] private SculptableMesh sculptableMesh;
        [SerializeField] private MirrorController mirrorController;

        [Header("Brush Settings")]
        [SerializeField, Range(0.01f, 1f)] private float brushStrength = 0.1f;
        [SerializeField, Range(0.05f, 2f)] private float brushRadius = 0.5f;
        [SerializeField] private BrushType currentBrush = BrushType.Move;
        [SerializeField] private bool isPositive = true;
        [SerializeField] private bool accumulate = true;
        // Multiplies the build-up rate of BOTH paths - the Accumulate-on rate
        // (EffectiveBrushStrengthAccumulate) and the Accumulate-off ease-toward-a-plateau rate
        // (EffectiveBrushStrengthPlateau). Lets a stroke build up faster or slower than
        // brushStrength alone would give, without touching brushStrength itself. Shown as
        // "Build-Up Strength" rather than "Accumulate Strength": it used to apply only to the
        // Accumulate-on path, which made it a dead control on every brush that defaults to
        // Accumulate off (Crease, Smooth, Move) while still looking like the strength knob.
        [SerializeField, Range(0.1f, 3f)] private float accumulateStrength = 1f;
        // Rejects every backfacing vertex from this brush's footprint (own mesh normal facing
        // away from the camera) before any falloff/mask weighting - lets a stroke reach into a
        // tight fold or across a thin fin (an ear, a finger gap) without also dragging the far
        // side along with it. Per-brush like Accumulate (see _brushFrontFacingOnly), off by
        // default everywhere: it's a precision aid for tricky geometry, not something that
        // should silently thin out an ordinary broad stroke.
        [SerializeField] private bool frontFacingOnly = false;
        // When ON, a brush held motionless keeps deforming (the ZBrush/Blender "airbrush" feel).
        // When OFF - the default - a stroke's build-up is paced by how far the cursor TRAVELS
        // rather than how long it is held, so stopping stops, and slowing down to place a
        // careful crease no longer buries it under extra material. See AccumulateSpeedFactor.
        // Shared across brushes rather than per-brush (like Lazy Mouse, unlike Accumulate): it
        // is a statement about how strokes should feel, not about one brush's behaviour.
        [SerializeField] private bool buildUpOnHold = false;

        // Curvature-gated relaxation folded into Clay's own dab, right after its own
        // displacement (see ApplySurfaceRelaxLocal) - without this, two Clay lobes/strokes
        // growing toward each other stretch the triangles between them thinner and thinner
        // (nothing ever relaxes that area back into shape) until they fold into a hard
        // crease/pinch instead of a smooth bridge. 0 disables it entirely.
        //
        // Clay-only: user-reported this is the one brush that actually needs it (bridging
        // between separately-built volumes is a Clay-specific problem) - Crease/Dam Standard/
        // Inflate/Flatten don't call ApplySurfaceRelaxLocal at all any more, so this field has
        // no effect on them regardless of value. Was shared across every brush at first (like
        // Build Up on Hold); narrowed to Clay-only once it became clear the OTHER brushes'
        // own sharp, deliberate output (Crease's groove in particular) was getting fought by
        // the very same pass meant only to fix accidental seams.
        [SerializeField, Range(0f, 1f)] private float surfaceRelax = 0.22f;

        // How concentrated the Pose brush's root->tip weight ramp is (see
        // SculptableMesh.SelectPose's rampT remarks) - 0 spreads the transition from 0 to full
        // weight across the ENTIRE root-to-click distance (soft, "rubber hose" bend: most of a
        // long-reach chain only ever gets partial rotation weight, which reads as weak even for
        // a large drag), 1 compresses it into a thin band right at the anchor so nearly
        // everything past that moves with full weight (a rigid hinge, most of the reached limb
        // actually follows the drag). Defaults above the old effective behavior (0, unexposed) -
        // user-reported the un-tunable default felt weak on a long reach even with a big drag.
        [SerializeField, Range(0f, 1f)] private float poseRigidity = 0.6f;

        // How many discrete joints the true anchor->true tip distance is divided into (see
        // SculptableMesh.SelectPose's segmentIndex remarks) - Blender's own Pose brush calls
        // this same idea "Segments". Without it, every click anywhere along a limb pivots from
        // the SAME single point (the true mask boundary) - user-reported this read as the tool
        // "snapping to one area" rather than feeling like there were multiple places to grab
        // from. Each segment boundary becomes its own local root: a click inside segment 2 (say)
        // pivots from the boundary between segments 1 and 2, not all the way back at the true
        // anchor, and everything below that boundary stays completely fixed for that gesture.
        [SerializeField, Range(1, 8)] private int poseSegments = 4;

        // Remembers each brush's own polarity across switches this session (ZBrush/Blender-
        // style per-tool state), instead of one flag shared by every brush regardless of which
        // is selected. Crease/Dam Standard default to negative (carve) since that's what most
        // people expect the first time they pick either up - they read as "indent" tools, unlike
        // Clay/Inflate which read as "add" tools by default. Indexed by BrushType; kept in sync
        // with `isPositive` by the CurrentBrush/IsPositive setters below.
        private readonly bool[] _brushPolarity = CreateDefaultBrushPolarity();

        private static bool[] CreateDefaultBrushPolarity()
        {
            var polarity = new bool[Enum.GetValues(typeof(BrushType)).Length];
            for (int i = 0; i < polarity.Length; i++) polarity[i] = true;
            polarity[(int)BrushType.Crease] = false;
            polarity[(int)BrushType.DamStandard] = false;
            return polarity;
        }

        // Same per-brush-memory pattern as _brushPolarity, for whether holding the brush in
        // place keeps deepening its effect indefinitely (ON - ZBrush/Blender-style continuous
        // accumulation) or converges to a single dab's worth and stops (OFF). Clay/Inflate/Dam
        // Standard default ON (they read as "keep building" tools); Move/Smooth/Crease default
        // OFF (Move/Smooth don't have this concept at all - see their apply code, which never
        // reads this flag, same as they never read isPositive; Crease's existing single-dab-cap
        // behavior IS the desired OFF default, unchanged).
        private readonly bool[] _brushAccumulate = CreateDefaultBrushAccumulate();

        private static bool[] CreateDefaultBrushAccumulate()
        {
            var accum = new bool[Enum.GetValues(typeof(BrushType)).Length];
            accum[(int)BrushType.Clay] = true;
            accum[(int)BrushType.Inflate] = true;
            accum[(int)BrushType.DamStandard] = true;
            return accum;
        }

        // Same per-brush-memory pattern, for accumulateStrength.
        private readonly float[] _accumulateStrengthPerType = CreateDefaultAccumulateStrength();

        private static float[] CreateDefaultAccumulateStrength()
        {
            var arr = new float[Enum.GetValues(typeof(BrushType)).Length];
            for (int i = 0; i < arr.Length; i++) arr[i] = 1f; // matches accumulateStrength field's default above
            return arr;
        }

        // Same per-brush-memory pattern as _brushAccumulate, for Front Facing Only - every brush
        // starts OFF (see frontFacingOnly's own remarks) and only diverges once the user turns it
        // on while that particular brush is selected, so e.g. enabling it for Clay doesn't also
        // silently turn it on the next time Move is picked up.
        private readonly bool[] _brushFrontFacingOnly = new bool[Enum.GetValues(typeof(BrushType)).Length];

        // Same per-brush-memory pattern as _brushPolarity/_brushAccumulate, for Brush Strength -
        // previously one value shared across every brush, so tuning Crease's strength while Clay
        // was selected would silently carry over the next time Clay was picked back up. Every
        // brush starts at the same default (matching the brushStrength field's own serialized
        // default below) and only diverges once the user actually changes it while that brush is
        // selected - kept in sync by the CurrentBrush/BrushStrength setters below.
        //
        // Brush RADIUS deliberately does NOT get this treatment: it is one value shared by every
        // brush, so a size dialled in on Clay is still the size you get after switching to Move
        // or Crease. Size is how big the thing you are working on is; strength is how hard a
        // particular brush should hit, which is a property of the brush. Per-brush radius was
        // tried first and felt like the tool resizing itself behind your back mid-sculpt.
        private readonly float[] _brushStrengthPerType = CreateDefaultBrushStrength();

        private static float[] CreateDefaultBrushStrength()
        {
            var arr = new float[Enum.GetValues(typeof(BrushType)).Length];
            for (int i = 0; i < arr.Length; i++) arr[i] = 0.1f; // matches brushStrength field's default below
            return arr;
        }

        [Header("Lazy Mouse")]
        // ZBrush/Nomad-style stroke stabilizer. While painting, the brush doesn't chase the
        // raw cursor 1:1 - it trails behind on an imaginary taut "rope" of lazyMouseRadius
        // screen pixels, only moving once the cursor has pulled that rope taut, and then only
        // directly toward the cursor (see GetStrokeScreenPosition). Small hand tremor/mouse
        // jitter stays within the rope's radius and never reaches the brush at all, which is
        // what makes long strokes come out smooth/straight instead of wobbly - at the cost of
        // a small lag behind fast cursor motion. Off by default (matches ZBrush's own default)
        // since it changes stroke feel enough that it shouldn't surprise a user who hasn't
        // asked for it.
        [SerializeField] private bool lazyMouseEnabled = false;
        [SerializeField, Range(1f, 150f)] private float lazyMouseRadius = 25f;
        // Fraction of the rope's excess length (dist - radius) closed per frame once taut. 1 =
        // classic ZBrush feel (the rope stays exactly taut every frame); lower values add extra
        // spring-like lag on top of the radius itself, for an even smoother/syrupier trail.
        [SerializeField, Range(0.05f, 1f)] private float lazyMouseStrength = 1f;

        [Header("Clay Brush")]
        // Peak plateau depth as a fraction of brushRadius. Default halved (was 0.6) at the same
        // time the plateau stopped being flat-topped-at-full-height across the whole footprint
        // (see ClayDisplacementJob's Height * weight) - 0.6 was tuned when every vertex in the
        // footprint converged to it, so keeping it would have left the new falloff-shaped dome
        // just as tall at the center as the old mesa was everywhere.
        [SerializeField, Range(0.1f, 1.5f)] private float clayHeightFactor = 0.3f;
        // 1 = fully round (today's plain radial falloff, unchanged), 0 = square/flat-topped
        // tip (ZBrush/Nomad "Square" profile) - blended per-vertex in ClayFalloff's t01 input
        // (see ClayWeightJob/ApplyClayBrushLocalManaged), not a separate code path, so it
        // collapses to exactly the original math at the default value.
        [SerializeField, Range(0f, 1f)] private float clayTipRoundness = 1f;
        [SerializeField] private bool useAlpha;
        [SerializeField] private BrushAlphaType alphaType = BrushAlphaType.SoftCircle;
        [SerializeField, Range(0f, 360f)] private float alphaRotation;
        [SerializeField, Range(0.3f, 3f)] private float alphaScale = 1f;
        [SerializeField] private bool invertAlpha;

        [Header("Crease / Dam Standard Brush")]
        [SerializeField, Range(0f, 1f)] private float creasePinch = 0.6f;
        [SerializeField, Range(0.05f, 1f)] private float creaseDepthFactor = 0.35f;
        [SerializeField, Range(0f, 1f)] private float damLipHeight = 0.25f;

        [Header("Flatten Brush")]
        // Where the flatten plane sits relative to the surface it was averaged from, as a
        // fraction of brushRadius along the plane normal. 0 is a plain flatten (the plane goes
        // exactly through the footprint's own average height). Positive lifts the plane above
        // the surface so low ground is filled in while high ground is barely touched - the
        // ZBrush "Fill"/Nomad "Flatten (fill)" feel; negative sinks it so only the high points
        // are shaved off - the Scrape/Polish end of the same family. One control rather than
        // three separate brushes, since Flatten/Fill/Scrape differ ONLY by where this plane sits.
        [SerializeField, Range(-0.5f, 0.5f)] private float flattenPlaneOffset = 0f;

        [Header("Masking")]
        // 0 = smoothstep across the whole radius (soft, gradual edges), 1 = full weight
        // everywhere inside the radius with a hard cutoff (immediate, opaque) - see
        // SculptableMesh.PaintMask's hardness remarks.
        [SerializeField, Range(0f, 1f)] private float maskHardness = 0.5f;

        [Header("Remesh Settings")]
        [SerializeField, Range(4, 500)] private int remeshResolution = 24;

        [Header("Symmetry Repair")]
        // Which plane the correspondence-map tools work across. Deliberately its own setting
        // rather than being read off MirrorController's three toggles: those are independent and
        // any combination can be on at once (X+Y mirrors a stroke into four quadrants), whereas
        // pairing vertices is a question about ONE plane - there is no such thing as a vertex's
        // counterpart across "X and Y at the same time". Defaults to X, the bilateral axis
        // essentially every character or creature is symmetric about.
        [SerializeField, Range(0, 2)] private int symmetryAxis = SymmetryMap.AxisX;
        [SerializeField, Range(SymmetryOps.MinToleranceScale, SymmetryOps.MaxToleranceScale)]
        private float symmetryToleranceScale = 1f;

        [Header("Debug")]
        [SerializeField] private bool showWireframeGizmo = false;
        [SerializeField] private bool logRayHits = false;

        // Multithreads Inflate/Crease/DamStandard/Clay/Smooth's per-candidate math via Unity
        // Jobs+Burst instead of a plain C# loop - mirrors how Blender/ZBrush get real-time perf
        // at high polycount (CPU spatial acceleration + multithreading), not GPU compute for the
        // brush math itself - a GPU brush was ruled out because hit-testing the deformed surface
        // would need an async GPU readback, adding a frame or two of latency to every dab. Only
        // matters for large-radius brushes on dense meshes (thousands+ vertices per footprint);
        // ordinary strokes are already fast enough via the footprint scoping alone. Exposed as a
        // toggle for A/B profiling - each brush keeps its original plain-C# method (suffixed
        // ...Managed) as both the small-footprint fast path and a correctness reference.
        [SerializeField] private bool useBurstJobs = true;

        // Fraction of the brush radius given over to Clay's edge taper (see ClayFalloff) - the
        // rest of the footprint sits at full weight. 1 tapers across the whole radius (a round
        // dome); small values keep a near-flat top with a narrow band of falloff right at the
        // edge, which is what lets a dragged stroke lay an even flat-topped strip ZBrush/Blender
        // Clay Buildup style rather than a ridge that's tallest along its centerline.
        //
        // Promoted from a 0.3 const to a serialized/UI-exposed field: at 0.3 the profile sits at
        // FULL weight across the inner 70% of the radius, which - now that weight also scales
        // the plateau's height, not just how fast a vertex gets there - reads as a cookie-cutter
        // mesa with near-vertical walls rather than a brush. 0.6 keeps the flat-strip character
        // while giving the footprint a shoulder to blend on.
        [SerializeField, Range(0.05f, 1f)] private float clayEdgeSoftness = 0.6f;
        // How far a light vs. a firm pen touch narrows/widens Clay's own footprint, on top of
        // CurrentPressure already scaling how MUCH clay a dab deposits (EffectiveBrushStrength).
        // Real clay responds to a lighter touch by contacting less of the surface, not just by
        // depositing less of it - dragged softly it leaves a narrow smear, pressed harder it
        // spreads wider and flatter. 0 leaves the footprint at exactly brushRadius regardless of
        // pressure (which is also what a mouse - no pen, CurrentPressure always 1 - already sees,
        // so this is opt-in and changes nothing for mouse users). See EffectiveClayRadius.
        [SerializeField, Range(0f, 1f)] private float clayPressureRadiusInfluence = 0.3f;
        // Companion to the above: how much a lighter touch also softens the footprint's edge
        // (raises clayEdgeSoftness) instead of just shrinking it - a light touch feathers off,
        // a firm one presses a thicker, more solid-edged pad. Same CurrentPressure==1 no-op for
        // mouse users. See EffectiveClayEdgeSoftness.
        [SerializeField, Range(0f, 1f)] private float clayPressureSoftnessInfluence = 0.5f;

        [Header("Stylus Pressure")]
        // Strength floor at zero pressure. Was a 0.35 const alongside the old sqrt response -
        // between them, a 10% press already produced 56% of full strength, so most of the
        // stylus's usable travel was spent above half power and light work was impossible.
        [SerializeField, Range(0f, 0.5f)] private float pressureFloor = 0.12f;
        // Exponent applied to smoothed pressure. 1 is linear; >1 spends more of the stylus's
        // travel in the light end (finer control on delicate passes); <1 front-loads it. The
        // old response was a hard-coded sqrt, i.e. 0.5 - which has INFINITE slope at zero, so
        // the response was steepest exactly where the sensor is noisiest and where the user
        // most wants fine control. That is what read as oversensitive; the fix is an exponent
        // on the other side of 1, not a smaller floor alone.
        [SerializeField, Range(0.5f, 3f)] private float pressureCurve = 1.6f;

        // Was 0.05 - too coarse once the camera is zoomed in close (CameraOrbitController's
        // minDistance is 0.5) for fine detail work: the smallest available brush still covered
        // a visibly large patch of the zoomed-in surface. 0.01 matches the floor
        // RebuildSpatialIndex/QueryNear already clamp their own cell size to, so the rest of the
        // brush pipeline was already exercised at this scale.
        public const float MinBrushRadius = 0.01f;
        // Was 2 - too tight once brush size also had to reach across large masked regions (the
        // Pose brush's reach is driven by this same value - see SculptableMesh.SelectPose's
        // effectiveReach), on top of ordinary sculpting on a bigger-than-default figure. See
        // ResizeSensitivity above, scaled to match.
        public const float MaxBrushRadius = 8f;

        private bool _isHovering;
        private Vector3 _hoverPoint;
        private Vector3 _hoverNormal;
        private bool _previewPositive;
        private bool _isOverUI;

        // Short-lived text popup independent of the brush cursor above, since it needs to be
        // readable even when nothing is selected (undoing a ZSphere convert, say - see the
        // remarks on Undo/Redo below) and shouldn't disappear the instant the mouse moves off
        // the model. Originally Undo/Redo-only (see TriggerUndoRedoFeedback); now shared with
        // Save/Save As (see TriggerActionToast) since both are the same "confirm a one-shot
        // action actually happened" need - a mesh flash makes sense for Undo/Redo (something on
        // the model itself changed) but not for a save, so TriggerActionToast is the toast-only
        // half of TriggerUndoRedoFeedback, callable on its own.
        private string _actionToastText;
        private float _actionToastTimer;
        private const float ActionToastDuration = 0.8f;
        private const float ActionToastFadeDuration = 0.3f;

        // Very brief, neutral (not brush-polarity-colored) flash across the sculpted surface on
        // Undo/Redo - see TriggerUndoRedoFeedback. Deliberately much shorter than
        // SelectionFlashEffect's own default (0.35s, blue) used for the double-click object-pick
        // confirmation: that one is announcing "you just changed WHAT you're working on" and
        // wants to be noticed, this one is just a wordless "yes, the surface actually changed"
        // beat alongside the toast text doing the actual explaining.
        private const float UndoFlashDuration = 0.1f;
        private static readonly Color UndoFlashColor = new Color(1f, 1f, 1f, 0.5f);

        // Toggled by tapping M - see HandleMaskPaintInput. A persistent mode switch (like the
        // 1-5 brush hotkeys) rather than a held modifier (like Shift-to-Smooth), since painting
        // a mask is typically its own multi-stroke pass, not a quick one-off tweak mid-sculpt.
        private bool _isMaskPaintMode;

        private void OnDestroy() => ReleaseNativeResources();

        // Writes through to _brushStrengthPerType immediately (not just on brush switch - see
        // CurrentBrush) so a live slider drag is remembered even if the user never switches
        // brushes again this session.
        public float BrushStrength
        {
            get => brushStrength;
            set
            {
                brushStrength = Mathf.Clamp(value, 0.01f, 1f);
                _brushStrengthPerType[(int)currentBrush] = brushStrength;
            }
        }

        // One value for every brush, unlike BrushStrength above - see _brushStrengthPerType.
        public float BrushRadius
        {
            get => brushRadius;
            set => brushRadius = Mathf.Clamp(value, MinBrushRadius, MaxBrushRadius);
        }

        // Action toast (Undo/Redo/Save/Save As) - see TriggerActionToast/_actionToastTimer.
        public bool ShowActionToast => _actionToastTimer > 0f;
        public string ActionToastText => _actionToastText;
        public float ActionToastAlpha => _actionToastTimer <= 0f
            ? 0f
            : (_actionToastTimer >= ActionToastFadeDuration ? 1f : _actionToastTimer / ActionToastFadeDuration);
        // 0 the instant it appears, 1 the instant it's gone - drives a gentle upward drift
        // (SculptUIBuilder) so it reads as "popping up", not just fading in place.
        public float ActionToastProgress01 => 1f - Mathf.Clamp01(_actionToastTimer / ActionToastDuration);

        public bool IsMaskPaintMode
        {
            get => _isMaskPaintMode;
            set
            {
                if (_isMaskPaintMode == value) return;
                _isMaskPaintMode = value;
                if (_isMaskPaintMode)
                {
                    EndActiveDrags(); // don't leave a grab mid-drag while painting mask
                    // Mask painting and the box/lasso region gestures both want the same click -
                    // see RegionSelectTool.Mode's setter, which disarms this one in the same way.
                    if (RegionSelect != null) RegionSelect.Mode = RegionSelectMode.Off;
                }
            }
        }

        public BrushType CurrentBrush
        {
            get => currentBrush;
            set
            {
                if (currentBrush != value)
                {
                    // Picking a brush is a statement about what the next click should do, and while
                    // mask paint mode is armed the answer is "paint mask" no matter which brush is
                    // highlighted. Leaving it on meant selecting Move and then dragging painted a
                    // mask instead of moving anything, with the panel showing Move the whole time -
                    // there is nothing in the UI that says the click is going somewhere else.
                    // Mask painting is a MODE, so the way out of it is to pick a tool, exactly as
                    // the box/lasso region gestures already disarm each other (see
                    // IsMaskPaintMode's setter and RegionSelectTool.Mode's).
                    //
                    // Deliberately not reached by the Shift-to-Smooth override, which is a held
                    // modifier rather than a choice of tool - see HandleShiftSmoothOverride, which
                    // skips itself entirely while masking.
                    IsMaskPaintMode = false;

                    EndActiveDrags();
                    _lastCarveStrokeLocal = null;
                    _lastClayStrokeLocal = null;
                    _brushPolarity[(int)currentBrush] = isPositive;
                    _brushAccumulate[(int)currentBrush] = accumulate;
                    _accumulateStrengthPerType[(int)currentBrush] = accumulateStrength;
                    _brushStrengthPerType[(int)currentBrush] = brushStrength;
                    _brushFrontFacingOnly[(int)currentBrush] = frontFacingOnly;
                    currentBrush = value;
                    isPositive = _brushPolarity[(int)currentBrush];
                    accumulate = _brushAccumulate[(int)currentBrush];
                    accumulateStrength = _accumulateStrengthPerType[(int)currentBrush];
                    brushStrength = _brushStrengthPerType[(int)currentBrush];
                    frontFacingOnly = _brushFrontFacingOnly[(int)currentBrush];
                    // brushRadius deliberately carries across the switch untouched.
                    TriggerActionToast(BrushDisplayName(currentBrush));
                }
            }
        }

        /// Human-readable brush name for the switch-brush toast above (see TriggerActionToast) -
        /// BrushType.DamStandard has no space, everything else matches its enum name as-is.
        private static string BrushDisplayName(BrushType type) =>
            type == BrushType.DamStandard ? "Dam Standard" : type.ToString();
        public bool IsPositive
        {
            get => isPositive;
            set
            {
                isPositive = value;
                _brushPolarity[(int)currentBrush] = value;
            }
        }
        public bool Accumulate
        {
            get => accumulate;
            set
            {
                accumulate = value;
                _brushAccumulate[(int)currentBrush] = value;
            }
        }
        public float AccumulateStrength
        {
            get => accumulateStrength;
            set
            {
                accumulateStrength = Mathf.Clamp(value, 0.1f, 3f);
                _accumulateStrengthPerType[(int)currentBrush] = accumulateStrength;
            }
        }
        public bool FrontFacingOnly
        {
            get => frontFacingOnly;
            set
            {
                frontFacingOnly = value;
                _brushFrontFacingOnly[(int)currentBrush] = value;
            }
        }
        public float ClayHeightFactor { get => clayHeightFactor; set => clayHeightFactor = Mathf.Clamp(value, 0.1f, 1.5f); }
        public float ClayTipRoundness { get => clayTipRoundness; set => clayTipRoundness = Mathf.Clamp01(value); }
        // Clamped away from 0 rather than to it - ClayFalloff divides by this.
        public float ClayEdgeSoftness { get => clayEdgeSoftness; set => clayEdgeSoftness = Mathf.Clamp(value, 0.05f, 1f); }
        public float ClayPressureRadiusInfluence { get => clayPressureRadiusInfluence; set => clayPressureRadiusInfluence = Mathf.Clamp01(value); }
        public float ClayPressureSoftnessInfluence { get => clayPressureSoftnessInfluence; set => clayPressureSoftnessInfluence = Mathf.Clamp01(value); }
        public float PressureFloor { get => pressureFloor; set => pressureFloor = Mathf.Clamp(value, 0f, 0.5f); }
        public float PressureCurve { get => pressureCurve; set => pressureCurve = Mathf.Clamp(value, 0.5f, 3f); }
        public float CreasePinch { get => creasePinch; set => creasePinch = Mathf.Clamp01(value); }
        public float CreaseDepthFactor { get => creaseDepthFactor; set => creaseDepthFactor = Mathf.Clamp(value, 0.05f, 1f); }
        public float DamLipHeight { get => damLipHeight; set => damLipHeight = Mathf.Clamp01(value); }
        public float FlattenPlaneOffset { get => flattenPlaneOffset; set => flattenPlaneOffset = Mathf.Clamp(value, -0.5f, 0.5f); }
        public float MaskHardness { get => maskHardness; set => maskHardness = Mathf.Clamp01(value); }
        public bool UseAlpha { get => useAlpha; set => useAlpha = value; }
        public BrushAlphaType AlphaType { get => alphaType; set => alphaType = value; }
        public float AlphaRotation { get => alphaRotation; set => alphaRotation = Mathf.Repeat(value, 360f); }
        public float AlphaScale { get => alphaScale; set => alphaScale = Mathf.Clamp(value, 0.3f, 3f); }
        public bool InvertAlpha { get => invertAlpha; set => invertAlpha = value; }
        public bool ShowWireframeGizmo { get => showWireframeGizmo; set => showWireframeGizmo = value; }
        public bool LogRayHits { get => logRayHits; set => logRayHits = value; }
        public bool UseBurstJobs { get => useBurstJobs; set => useBurstJobs = value; }
        /// Highest remesh density the UI offers. Raised from 500 once the extraction stopped
        /// allocating whole-lattice arrays: at 500 the old pipeline needed about 2.8 GB of live
        /// grid to produce 2.7M triangles, and the same cubic growth made anything past that
        /// unreachable rather than merely slow. The sparse extraction's memory follows the
        /// SURFACE instead, so 1024 - roughly 6-7 million triangles on a compact model - costs a
        /// few hundred megabytes. Kept below MeshRemesher.MaxResolution so the structural limit
        /// stays a backstop and not the thing users bump into.
        public const int MaxRemeshResolution = 1024;

        public int RemeshResolution { get => remeshResolution; set => remeshResolution = Mathf.Clamp(value, 4, MaxRemeshResolution); }

        // One value shared by every brush (unlike BrushStrength) - lazy mouse is an input-
        // smoothing behavior, not a property of any particular brush's effect.
        public bool LazyMouseEnabled { get => lazyMouseEnabled; set => lazyMouseEnabled = value; }
        public bool BuildUpOnHold { get => buildUpOnHold; set => buildUpOnHold = value; }
        public float SurfaceRelax { get => surfaceRelax; set => surfaceRelax = Mathf.Clamp01(value); }
        public float PoseRigidity { get => poseRigidity; set => poseRigidity = Mathf.Clamp01(value); }
        public int PoseSegments { get => poseSegments; set => poseSegments = Mathf.Clamp(value, 1, 8); }
        public float LazyMouseRadius { get => lazyMouseRadius; set => lazyMouseRadius = Mathf.Clamp(value, 1f, 150f); }
        public float LazyMouseStrength { get => lazyMouseStrength; set => lazyMouseStrength = Mathf.Clamp(value, 0.05f, 1f); }

        public int SymmetryAxis { get => symmetryAxis; set => symmetryAxis = Mathf.Clamp(value, 0, 2); }
        public float SymmetryToleranceScale
        {
            get => symmetryToleranceScale;
            set => symmetryToleranceScale = Mathf.Clamp(value, SymmetryOps.MinToleranceScale, SymmetryOps.MaxToleranceScale);
        }

        // GetIndexCount/vertexCount rather than .triangles/.vertices - those copy the whole
        // index/vertex buffer on every access, which would be a real cost read every frame by
        // the UI's poly-count display at multi-million-triangle mesh sizes.
        public int TriangleCount => sculptableMesh != null && sculptableMesh.Mesh != null
            ? (int)sculptableMesh.Mesh.GetIndexCount(0) / 3 : 0;
        public int VertexCount => sculptableMesh != null && sculptableMesh.Mesh != null
            ? sculptableMesh.Mesh.vertexCount : 0;

        // Routed through EditHistory rather than straight at the selected object's own stack.
        // The old form undid whatever the SELECTION had last done, so undoing after clicking a
        // different object in the scene panel reversed something you did minutes ago on that
        // object instead of the thing you just did - and it could not reach edits that are not
        // about one object's vertices at all, like skinning a ZSphere rig into a new mesh. Note
        // these no longer require a selection: there is plenty worth undoing when nothing is
        // selected (that ZSphere convert, for one).
        public bool CanUndo => EditHistory.CanUndo;
        public bool CanRedo => EditHistory.CanRedo;

        public void Undo()
        {
            EndActiveDrags();
            if (!EditHistory.CanUndo) return; // nothing actually happened - no flash/toast for a no-op keypress
            EditHistory.Undo();
            TriggerUndoRedoFeedback("Undo");
        }

        public void Redo()
        {
            EndActiveDrags();
            if (!EditHistory.CanRedo) return;
            EditHistory.Redo();
            TriggerUndoRedoFeedback("Redo");
        }

        // Very brief white flash across the sculpted surface (skipped if nothing is selected -
        // undoing a ZSphere convert, say, has no single mesh to flash) plus the toast text,
        // which shows regardless of selection since it's confirming the action happened at all,
        // not that a particular mesh changed.
        private void TriggerUndoRedoFeedback(string label)
        {
            if (sculptableMesh != null) SelectionFlashEffect.Play(sculptableMesh.gameObject, UndoFlashDuration, UndoFlashColor);
            TriggerActionToast(label);
        }

        /// Toast-only half of TriggerUndoRedoFeedback above, with no mesh flash - for actions
        /// like Save/Save As (see SceneGraphUIBuilder.Save/SaveAs) that have no single mesh to
        /// flash and shouldn't get one anyway (nothing on the model itself changed).
        public void TriggerActionToast(string label)
        {
            _actionToastText = label;
            _actionToastTimer = ActionToastDuration;
        }

        /// Steps of history held and what they cost, for the UI - see EditHistory.Summary.
        public static string HistorySummary => EditHistory.Summary();

        /// How many undo steps history keeps. Surfaced here so the UI reaches it the same way it
        /// reaches every other setting, rather than touching a static class directly.
        public int UndoSteps
        {
            get => EditHistory.MaxSteps;
            set => EditHistory.MaxSteps = value;
        }

        // Not wired into undo/redo, same deliberate scope call as PaintMask itself (see
        // SculptableMesh.PaintMask remarks) - masking doesn't move geometry.
        public void InvertMask() => sculptableMesh?.InvertMask();

        // Which SculptableMesh brushes currently target - the scene's SelectionManager's
        // primary selection, not a fixed reference. Lazily resolved (rather than in Awake)
        // since SculptUIBuilder reads Mirror while building the HUD from ITS OWN Start(), and
        // MonoBehaviour Awake/OnEnable order between separate GameObjects isn't guaranteed -
        // see SelectionManager's class remarks for the full reasoning.
        private SelectionManager _selection;
        private SelectionManager Selection => _selection != null ? _selection : (_selection = FindFirstObjectByType<SelectionManager>());
        private SculptableMesh Target => Selection != null ? Selection.PrimarySelection : null;

        /// The camera every brush raycast and every screen-space projection in the app goes
        /// through. Exposed so RegionSelectTool projects vertices with the SAME camera the
        /// brushes use, rather than making its own Camera.main guess that could differ.
        public Camera ActiveCamera => cam;

        // Box/lasso hide and mask (see RegionSelectTool). Found lazily, and ADDED to this
        // GameObject if the scene has none - the scene file is edited through Unity MCP, which
        // cannot wire object references (its property setter fails to deserialize any
        // GameObject/Component reference field), so a
        // tool that self-installs is the one that reliably exists at runtime. Added rather than
        // required, so an older scene picks the feature up with no scene edit at all.
        private RegionSelectTool _regionSelect;
        public RegionSelectTool RegionSelect
        {
            get
            {
                if (_regionSelect != null) return _regionSelect;
                _regionSelect = FindFirstObjectByType<RegionSelectTool>();
                if (_regionSelect == null) _regionSelect = gameObject.AddComponent<RegionSelectTool>();
                return _regionSelect;
            }
        }

        // True while a region gesture is armed - the brushes, the shift-to-smooth override and
        // the ring cursor all stand down, exactly as they do for a non-Sculpt gizmo mode.
        private bool RegionSelectActive => RegionSelect != null && RegionSelect.IsActive;

        // World-space grid that previews RemeshResolution while the R gauge is held (see
        // HandleRemeshDensityKey) - same self-installing idiom as RegionSelect above, and for
        // the same reason (no scene wiring reaches it through Unity MCP).
        private RemeshDensityGrid _densityGrid;
        public RemeshDensityGrid DensityGrid
        {
            get
            {
                if (_densityGrid != null) return _densityGrid;
                _densityGrid = FindFirstObjectByType<RemeshDensityGrid>();
                if (_densityGrid == null) _densityGrid = gameObject.AddComponent<RemeshDensityGrid>();
                return _densityGrid;
            }
        }

        /// True while the R-hold gauge is armed - RemeshDensityGrid reads this to know when to
        /// show/fade in, SculptUIBuilder reads it to know when to show the density label, and
        /// UpdateBrushCursor reads it to suppress the ordinary brush ring for the same reason it
        /// already suppresses it for a region gesture or a non-Sculpt gizmo.
        public bool ShowRemeshDensityGrid => _isAdjustingRemeshDensity;

        // Which whole-object tool (see GizmoMode) is currently active - HandleSculptInput
        // early-outs while a non-Sculpt mode is active so gizmo dragging and brush strokes can
        // never fight over the same click. Lazily resolved, same reasoning as Selection above.
        private TransformGizmo _gizmo;
        private TransformGizmo Gizmo => _gizmo != null ? _gizmo : (_gizmo = FindFirstObjectByType<TransformGizmo>());

        // Live per-call, not cached from the synced sculptableMesh/mirrorController fields
        // below - a caller (e.g. a Scene Graph UI button) can change the selection and read
        // Mirror in the very same frame, before this component's own Update() has run to
        // re-sync those fields, so this always resolves against the CURRENT Target directly.
        //
        // Adds the component if the target hasn't got one instead of returning null. Every
        // runtime path that creates a sculptable object pairs it with a MirrorController
        // (PrimitiveSpawner, MeshMirror, MeshCloner, SceneSerializer), but a SculptableMesh
        // placed by hand in the scene can easily be saved without one - and one was: the scene
        // shipped a "Sphere" that registered ahead of SculptSphere, became the default primary
        // selection, and returned null here. That killed SculptUIBuilder.BuildUI partway
        // through the Mirror toggles (so the bottom of the brush panel - mirror axes, plane
        // visibility, wireframe, undo/redo, the brush-resize gauge - silently never got built)
        // and would have thrown out of GetMirrorSigns on the first brush stroke against that
        // object. Self-healing here fixes every one of those call sites at once, and costs a
        // GetComponent on a path that already did one.
        public MirrorController Mirror
        {
            get
            {
                SculptableMesh target = Target;
                if (target == null) return null;
                MirrorController mirror = target.GetComponent<MirrorController>();
                return mirror != null ? mirror : target.gameObject.AddComponent<MirrorController>();
            }
        }

        // Detects a selection change once per Update() (see SyncSelectionTarget) rather than
        // re-resolving Target on every one of the ~60 sculptableMesh/mirrorController call
        // sites below - cheap and correct, since every one of those call sites only ever runs
        // from within this same Update() (directly or via a method it calls).
        private SculptableMesh _lastSyncedTarget;

        private void Awake()
        {
            if (cam == null) cam = Camera.main;

            // The serialized `isPositive`/`accumulate`/`brushStrength` predate per-brush memory
            // and may be stale for whatever brush is currently selected - start from this brush's
            // own remembered defaults instead (see
            // _brushPolarity/_brushAccumulate/_brushStrengthPerType remarks). `brushRadius` is
            // shared by every brush, so its serialized value is already the right starting point.
            isPositive = _brushPolarity[(int)currentBrush];
            accumulate = _brushAccumulate[(int)currentBrush];
            accumulateStrength = _accumulateStrengthPerType[(int)currentBrush];
            brushStrength = _brushStrengthPerType[(int)currentBrush];
        }

        private void Update()
        {
            // Decayed FIRST, before anything below can (re)trigger either timer this frame -
            // otherwise a fresh trigger this same frame would immediately lose one frame's worth
            // of decay before anyone ever reads the full un-decayed value (e.g. the stroke-end
            // fade would never actually reach its intended "blinks fully out" starting point).
            if (_actionToastTimer > 0f) _actionToastTimer = Mathf.Max(0f, _actionToastTimer - Time.deltaTime);
            if (_strokeEndFadeTimer > 0f) _strokeEndFadeTimer = Mathf.Max(0f, _strokeEndFadeTimer - Time.deltaTime);

            SyncSelectionTarget();
            HandleBrushSwitchKeys();
            HandleBrushResizeKey();
            HandleBrushStrengthKey();
            HandleRemeshDensityKey();
            HandleUndoRedoKeys();
            HandleSaveKeys();
            UpdatePoseChainVisual();
            UpdatePenPressure();
            HandleSculptInput();
            HandleBrushSizeScroll();
            HandleStrokeEndCommit();
            UpdateBrushCursor();
        }

        // Cursor.visible is a global OS setting, not per-component - if this component (or the
        // whole app) goes away while the ring cursor had it hidden, the real pointer must come
        // back or the user is left with no visible cursor at all outside this app's control.
        private void OnDisable() => Cursor.visible = true;

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus) Cursor.visible = true;
        }

        /// Re-points sculptableMesh/mirrorController at the SelectionManager's current
        /// PrimarySelection whenever it changes (a no-op most frames). Also resets every
        /// piece of per-stroke continuity state that would otherwise reference the OLD
        /// target's vertex indices/local space if a drag/stroke happened to be mid-flight when
        /// the selection changed underneath it (e.g. clicking a different row in the Scene
        /// Graph panel mid-drag) - same defensive reset CurrentBrush's setter already does on
        /// an ordinary brush switch.
        private void SyncSelectionTarget()
        {
            SculptableMesh target = Target;
            if (target == _lastSyncedTarget) return;

            _lastSyncedTarget = target;
            sculptableMesh = target;
            mirrorController = target != null ? target.GetComponent<MirrorController>() : null;

            EndActiveDrags();
            _isHovering = false;
            _lastCarveStrokeLocal = null;
            _lastClayStrokeLocal = null;
            _lastClayStrokeNormalLocal = null;
            _lastStrokeHitPointWorld = null;
            _strokeSpeed = 0f;
        }

        private void HandleSculptInput()
        {
            var mouse = Mouse.current;
            if (mouse == null || cam == null || sculptableMesh == null) return;

            // Anything OUTSIDE this component can have moved geometry since the last frame - an
            // undo (HandleUndoRedoKeys, a few lines earlier in Update), a gizmo drag, a Remesh, a
            // symmetry op. None of those go through the brush write paths that invalidate the
            // position mirror, so the frame starts by assuming they did. Within the frame the
            // brushes' own invalidation keeps it exact - see RefreshPositionMirror.
            MarkPositionMirrorStale();

            // A non-Sculpt gizmo tool (Transpose/Scale) is active - it owns mouse input for
            // dragging the selected object's transform instead, see TransformGizmo/GizmoMode.
            if (Gizmo != null && Gizmo.Mode != GizmoMode.Sculpt)
            {
                _isHovering = false;
                return;
            }

            // Same carve-out for a box/lasso region gesture: it owns the drag while armed (see
            // RegionSelectTool), and a brush firing under the marquee would sculpt whatever the
            // user was only trying to draw a selection around. _isOverUI is still refreshed on
            // the way out - the region crosshair below reads it, and leaving it frozen at
            // whatever the last sculpting frame saw made the crosshair vanish (or persist over a
            // panel) depending on where the pointer happened to be when the mode was armed.
            if (RegionSelectActive)
            {
                _isHovering = false;
                _isOverUI = UnityEngine.EventSystems.EventSystem.current != null &&
                            UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
                return;
            }

            // Same carve-out again for the R-hold density gauge: it owns horizontal mouse
            // movement while armed, and the grid/label are its whole visual, so - unlike the S/F
            // gauges below - the brush ring is suppressed rather than frozen in place (see
            // UpdateBrushCursor's sculptToolActive).
            if (_isAdjustingRemeshDensity)
            {
                _isHovering = false;
                _isOverUI = UnityEngine.EventSystems.EventSystem.current != null &&
                            UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
                return;
            }

            // While the resize gauge is up, mouse movement scrubs brush size, not sculpting.
            // Force _isOverUI false too so UpdateBrushCursor keeps showing the ring at the
            // mouse ray (the deliberate resize-gauge UX) rather than hiding it as "over UI".
            // Same reasoning applies to the strength gauge (F) below.
            if (_isResizingBrush || _isAdjustingStrength)
            {
                _isHovering = false;
                _isOverUI = false;
                return;
            }

            bool overUI = UnityEngine.EventSystems.EventSystem.current != null &&
                          UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
            _isOverUI = overUI;
            bool altHeld = Keyboard.current != null && Keyboard.current.leftAltKey.isPressed;

            HandleObjectPickDoubleClick(mouse, overUI, altHeld);

            // Mask painting is its own input mode, not one of the sculpting brushes - doesn't
            // move vertices, so it skips the undo snapshot/spatial-grid-rebuild-on-press below
            // entirely (mask isn't part of undo history - see HandleMaskPaintInput remarks).
            if (_isMaskPaintMode)
            {
                HandleMaskPaintInput(mouse, overUI, altHeld);
                return;
            }

            // Prepare the vertex spatial index once at the start of every stroke so Clay/Smooth/
            // Crease/Dam Standard/Move's per-stroke vertex lookups don't have to scan the whole
            // mesh. Cell size tracks the current brush radius so the grid stays well-matched to
            // typical query size; the previous stroke's index is reused as-is when it still fits
            // (see SculptableMesh.PrepareSpatialIndex), since a rebuild is O(vertex count) and
            // would otherwise land in the first frame of every stroke. Also begins the stroke's undo
            // delta accumulator here, once per stroke rather than per frame for the same reason -
            // a stroke that turns out to be a click on empty space (missing the mesh) never
            // records anything, so HandleStrokeEndCommit's EndStrokeUndo call just no-ops for it
            // at zero cost (see SculptableMesh.BeginStrokeUndo/EndStrokeUndo remarks) - not worth
            // the extra complexity of gating this from inside every individual brush handler.
            if (!overUI && !altHeld && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
            {
                sculptableMesh.PrepareSpatialIndex(Mathf.Max(brushRadius * 0.5f, 0.01f));
                sculptableMesh.BeginStrokeUndo();
                // Fresh stroke, fresh speed reading - without this, a new stroke's first frame
                // would measure "speed" against wherever the cursor last hit the mesh at the END
                // of a PREVIOUS, unrelated stroke (see UpdateStrokeSpeed's remarks).
                _lastStrokeHitPointWorld = null;
                _strokeSpeed = 0f;
                // Fresh stroke, fresh touched-vertex set - see _strokeDirtyVertexScratch/
                // ApplyPostStrokeUnifyPass.
                _strokeDirtyVertexScratch.Clear(sculptableMesh.Vertices.Length);
            }

            switch (currentBrush)
            {
                case BrushType.Move:
                    HandleMoveDrag(mouse, overUI, altHeld);
                    break;
                case BrushType.Smooth:
                    HandleSmoothInput(mouse, overUI, altHeld);
                    break;
                case BrushType.Crease:
                    HandleCreaseInput(mouse, overUI, altHeld);
                    break;
                case BrushType.DamStandard:
                    HandleDamStandardInput(mouse, overUI, altHeld);
                    break;
                case BrushType.Inflate:
                    HandleInflateInput(mouse, overUI, altHeld);
                    break;
                case BrushType.Flatten:
                    HandleFlattenInput(mouse, overUI, altHeld);
                    break;
                case BrushType.Pose:
                    HandlePoseInput(mouse, overUI, altHeld);
                    break;
                case BrushType.Clay:
                    HandleClayInput(mouse, overUI, altHeld);
                    break;
                default:
                    // Every BrushType needs its own case above. This used to fall through to Clay,
                    // so a newly added brush silently sculpted as Clay instead of failing.
                    Debug.LogError($"[Sculpt] Unhandled BrushType {currentBrush} in HandleSculptInput", this);
                    break;
            }

        }
    }
}
