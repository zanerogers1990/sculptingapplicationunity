using System;
using System.Collections.Generic;
using System.IO;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
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
    public class SculptController : MonoBehaviour
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

        // Where the rope's near end currently sits, in screen pixels - only meaningful while
        // _lazyMouseActive is true (see GetStrokeScreenPosition).
        private Vector2 _lazyMouseScreenPos;
        private bool _lazyMouseActive;
        // The rope's FAR end - the raw cursor, recorded on the same frame _lazyMouseScreenPos
        // was last advanced, so the two describe one consistent rope rather than two positions
        // sampled at different moments. Read only through LazyMouseTetherFrom, for the tether
        // line SculptUIBuilder draws between them.
        private Vector2 _lazyMouseRawScreenPos;

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
        // brush math itself - see [[project_perf_overhaul_no_gpu_rewrite]] memory for why. Only
        // matters for large-radius brushes on dense meshes (thousands+ vertices per footprint);
        // ordinary strokes are already fast enough via the footprint scoping alone. Exposed as a
        // toggle for A/B profiling - each brush keeps its original plain-C# method (suffixed
        // ...Managed) as both the small-footprint fast path and a correctness reference.
        [SerializeField] private bool useBurstJobs = true;

        // Below this candidate count, Job scheduling's fixed per-call overhead costs more than
        // it saves - use the plain C# loop instead. Small brush radii commonly touch footprints
        // well under this.
        private const int MinJobVertexCount = 256;

        // Clay eases each vertex toward a plateau offset from the hit point so volume builds
        // up instead of spiking indefinitely; it scales with Time.deltaTime for frame-rate
        // independence. Move instead drags 1:1 with the cursor (see HandleMoveDrag), so it
        // has no speed/strength constant of its own - brushStrength only affects Clay.
        // (Plateau depth used to be a constant here too; it's now the serialized
        // clayHeightFactor field/ClayHeightFactor property above so it's tunable from the UI.)
        private const float ClaySpeed = 4f;

        // The most a SINGLE Clay stroke may displace any one vertex, as a multiple of that
        // stroke's clay depth (brushRadius * clayHeightFactor). Measured per vertex from where
        // the stroke found it (SculptableMesh.StrokeStartPosition), along the dab's plane
        // normal. This is what makes a held or back-and-forth stroke SETTLE instead of
        // ballooning - the "bubbles at both ends of a back-and-forth stroke" report.
        //
        // Why Clay ran away at all: the area plane each dab targets is averaged from the
        // footprint's CURRENT positions, which include whatever this very stroke just deposited
        // there, so each pass re-bases its target on its own output and climbs again.
        // Algebraically, once the footprint converges onto plane + height*w, the new
        // weighted-mean height is the old one plus height * mean(w) - a fixed rise per pass,
        // forever. Dragging back and forth parks the cursor at each turnaround, which is where
        // the passes pile up.
        //
        // An earlier attempt at this fixed the FEEDBACK instead of the SYMPTOM: it averaged the
        // plane from stroke-start positions (the original-coordinates trick Blender's
        // flatten-family brushes use). That does stop the runaway, but it also changes what Clay
        // fundamentally does - from "add on top of the surface as it is now" to "reshape toward
        // an absolute profile". Dabs then FIGHT: as the brush moves on, a vertex's weight drops,
        // its absolute target drops with it, and the trailing dabs pull back down what the
        // leading ones just raised. Every dab stamps its own dome over its neighbour's instead
        // of sweeping one continuous ridge, which is what produced the rippled/corrugated
        // surface the user reported next - worst at low Tip Softness, where the flat-topped
        // profile gives each competing stamp a hard rim. The plane is deliberately LIVE again;
        // only this per-stroke displacement cap holds the buildup down, and because it is a
        // flat per-vertex limit (NOT scaled by the dab's falloff weight) it is identical for
        // every dab that reaches it - so it truncates into one clean plateau instead of
        // re-imposing each dab's profile the way a weight-scaled ceiling did.
        //
        // Releasing and stroking again re-bases the cap, so buildup ACROSS strokes - which is
        // what clay buildup actually means - is untouched.
        private const float ClayStrokeDepthLimitAccumulate = 3f;
        private const float ClayStrokeDepthLimit = 1.5f;
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
        // Smooth has no "amount" concept beyond how far it eases toward the neighbor
        // average each frame, so it gets its own speed constant rather than reusing Clay's.
        private const float SmoothSpeed = 4f;
        // Shared by Crease and Dam Standard, which reuse the same pinch+carve core.
        private const float CreaseSpeed = 4f;
        // Inflate pushes along each vertex's own normal at a constant rate (no target to
        // ease toward, unlike Clay/Crease/Smooth), so its factor is a plain velocity
        // multiplier rather than a lerp-fraction scale. That's Inflate's Accumulate-ON path;
        // with Accumulate off it instead eases toward a single fixed target above the hit
        // point/normal (see ApplyInflateBrushLocalManaged), same shape as Crease's OFF target -
        // InflateOffCapFactor sets how far above the hit point that target sits, as a fraction
        // of brushRadius. Inflate has no user-facing height-factor slider of its own (unlike
        // Clay's clayHeightFactor), so this is a fixed internal ratio rather than a new control;
        // chosen to match Clay's own default (0.6) for a comparable one-dab feel.
        private const float InflateSpeed = 4f;
        private const float InflateOffCapFactor = 0.6f;
        // Flatten eases each vertex toward the footprint's own area plane, so - unlike Clay's
        // live-replanned buildup - it CONVERGES on its own: once the footprint is flat the
        // remaining distance to the plane is zero and further dwelling does nothing. That is why
        // it needs no per-stroke depth cap in the flatten direction, and why it ignores the
        // Accumulate toggle entirely (there is nothing to accumulate), same as Move/Smooth do.
        private const float FlattenSpeed = 4f;
        // The inverted (RMB / Ctrl) direction pushes vertices AWAY from the plane instead -
        // Blender's Ctrl+Flatten "sharpen"/contrast behaviour, which exaggerates whatever relief
        // is already there. That direction is the one that DIVERGES (every frame's push moves a
        // vertex further from the plane it is measured against, so the next push is larger), so
        // it gets a cap on how far one stroke may drive any vertex off its starting height,
        // as a fraction of brushRadius. Measured from SculptableMesh.StrokeStartPosition and
        // applied symmetrically - unlike Clay's one-sided ClampStrokeDepth, contrast moves the
        // two sides of the plane in OPPOSITE directions, so both need bounding.
        private const float FlattenContrastLimit = 0.5f;
        // Mask paint/erase rate range - reuses brushStrength/brushRadius rather than adding a
        // separate intensity slider, matching the "just a basic one" scope of the original
        // masking feature. maskHardness (see its own field) interpolates between these two:
        // at hardness 0 the rate matches the old constant (4) - a deliberately slow accumulation
        // so a soft brush stays a gentle, dwell-to-build-up wash, matching what "soft" means in
        // most sculpting apps. At hardness 1 the rate is high enough that a single ordinary
        // click-drag reaches full mask in a fraction of a second even at default brushStrength,
        // matching "hard is immediately dark" - hardness alone reshaping the falloff (see
        // SculptableMesh.PaintMask) wasn't enough on its own, since the per-frame accumulation
        // amount was the same tiny value at the brush center regardless of hardness.
        private const float MaskPaintSpeedSoft = 4f;
        private const float MaskPaintSpeedHard = 40f;

        // See CurrentPressure/UpdatePenPressure remarks (near the BrushStrength property) for
        // why pressure is smoothed and curved rather than applied raw.
        private const float PressureSmoothingSpeed = 20f;
        private float _smoothedPenPressure = 1f;

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

        // How many world units BrushRadius changes per pixel of horizontal mouse movement
        // while resizing (holding S). Tuned so a full-width drag across a ~1080p window
        // covers roughly the whole 0.01-2 range.
        private const float ResizeSensitivity = 0.0025f;
        // Half ResizeSensitivity, matching that BrushStrength's range (0.01-1) is about half
        // the width of BrushRadius's (0.01-2) - same "full-width drag covers roughly the whole
        // range" feel, scaled to the smaller range.
        private const float StrengthAdjustSensitivity = 0.00125f;
        // Was 0.05 - too coarse once the camera is zoomed in close (CameraOrbitController's
        // minDistance is 0.5) for fine detail work: the smallest available brush still covered
        // a visibly large patch of the zoomed-in surface. 0.01 matches the floor
        // RebuildSpatialIndex/QueryNear already clamp their own cell size to, so the rest of the
        // brush pipeline was already exercised at this scale.
        public const float MinBrushRadius = 0.01f;
        public const float MaxBrushRadius = 2f;

        private static readonly Color PositiveColor = new Color(0.2f, 1f, 0.4f);
        private static readonly Color NegativeColor = new Color(1f, 0.3f, 0.3f);
        // Smooth has no add/subtract polarity (see _previewPositive's "always neutral" comment
        // in each brush handler) - blue instead of green/red reads as its own third state
        // rather than looking like an ordinary positive dab, matching the dashed ring
        // (BrushCursorDashed) SculptUIBuilder swaps in for the same reason.
        private static readonly Color SmoothColor = new Color(0.3f, 0.65f, 1f);

        private bool _isHovering;
        private Vector3 _hoverPoint;
        private Vector3 _hoverNormal;
        private bool _previewPositive;
        private bool _isOverUI;

        // 2D screen-space brush cursor (replaces the old world-space BrushPreview sphere) - a
        // ZBrush/Blender-style ring that tracks the mouse directly rather than a 3D object
        // positioned via raycast hit point, so it reads correctly even when hovering empty
        // space beside the model. SculptUIBuilder polls these every frame to position/size/tint
        // its ring Image, same delegation pattern as IsAdjustingStrength below.
        // The OS cursor is hidden directly here (see UpdateBrushCursor) whenever this
        // ring is shown, and restored whenever it isn't - over a UI panel, over no sculptable
        // target, or while another tool (Transpose/Scale/ZSphere) owns the viewport.
        private bool _showBrushCursor;
        private Vector2 _brushCursorScreenPos;
        private float _brushCursorScreenDiameter;
        private Color _brushCursorColor;
        private bool _brushCursorDashed;
        private const float MinCursorScreenDiameterPx = 14f;

        // Brief "stroke committed" pulse: blinks the cursor out and eases it back in over
        // StrokeEndFadeDuration starting the instant a stroke ends (see HandleStrokeEndCommit),
        // so releasing the mouse gives an unmistakable beat of feedback distinct from just
        // continuing to hover in the same spot. Counts DOWN from StrokeEndFadeDuration to 0;
        // BrushCursorFadeAlpha (read by SculptUIBuilder) turns that into a 0->1 ramp.
        private float _strokeEndFadeTimer;
        private const float StrokeEndFadeDuration = 0.1f;

        // "Undo"/"Redo" toast (see Undo/Redo/TriggerUndoRedoFeedback) - a short-lived text
        // popup independent of the brush cursor above, since it needs to be readable even when
        // nothing is selected (undoing a ZSphere convert, say - see the remarks on Undo/Redo
        // below) and shouldn't disappear the instant the mouse moves off the model.
        private string _undoToastText;
        private float _undoToastTimer;
        private const float UndoToastDuration = 0.8f;
        private const float UndoToastFadeDuration = 0.3f;

        // Very brief, neutral (not brush-polarity-colored) flash across the sculpted surface on
        // Undo/Redo - see TriggerUndoRedoFeedback. Deliberately much shorter than
        // SelectionFlashEffect's own default (0.35s, blue) used for the double-click object-pick
        // confirmation: that one is announcing "you just changed WHAT you're working on" and
        // wants to be noticed, this one is just a wordless "yes, the surface actually changed"
        // beat alongside the toast text doing the actual explaining.
        private const float UndoFlashDuration = 0.1f;
        private static readonly Color UndoFlashColor = new Color(1f, 1f, 1f, 0.5f);

        // Previous stroke sample in the mesh's local space, used only by Dam Standard to
        // derive a stroke-travel direction for its leading-edge lip; null between strokes
        // (mouse up / hover lost / brush switched) so a fresh stroke starts symmetric.
        private Vector3? _lastDamHoverLocal;

        // Clay's own stroke-continuity memory, in mesh-local space - null between strokes
        // (mouse up / hover lost / brush switched), same lifecycle as _lastDamHoverLocal
        // above. Used by ApplyClayStroke to sub-divide a fast drag into multiple dabs instead
        // of one dab per rendered frame - see its remarks for why.
        private Vector3? _lastClayStrokeLocal;
        private Vector3? _lastClayStrokeNormalLocal;

        private bool _isMoveDragging;
        private Vector3 _dragPlanePoint;
        private Vector3 _dragPlaneNormal;
        private Vector3 _lastDragPoint;
        // One selection per active mirror sign, paired with the sign used to make it, so a
        // drag delta can be re-mirrored before being applied to that selection.
        private List<(SculptableMesh.GrabSelection selection, Vector3 sign)> _grabSelections;

        private bool _isResizingBrush;
        private float _resizeStartRadius;
        private float _resizeStartMouseX;
        // Where the S-drag started - UpdateBrushCursor freezes the ring here instead of
        // following the live mouse position while resizing, since the drag scrubs BrushRadius
        // by horizontal delta alone and the ring flying across the screen with the mouse would
        // otherwise fight the "grow/shrink in place" feedback the gesture is meant to give.
        private Vector2 _resizeAnchorScreenPos;

        // Same S-drag pattern as above (see HandleBrushStrengthKey), but for per-brush
        // BrushStrength instead of the shared BrushRadius.
        private bool _isAdjustingStrength;
        private float _strengthAdjustStartValue;
        private float _strengthAdjustStartMouseX;
        // Same freeze-in-place reasoning as _resizeAnchorScreenPos above, for the F-drag.
        private Vector2 _strengthAdjustAnchorScreenPos;

        private bool _isShiftSmoothActive;
        private BrushType _preShiftBrush;

        // Toggled by tapping M - see HandleMaskPaintInput. A persistent mode switch (like the
        // 1-5 brush hotkeys) rather than a held modifier (like Shift-to-Smooth), since painting
        // a mask is typically its own multi-stroke pass, not a quick one-off tweak mid-sculpt.
        private bool _isMaskPaintMode;

        // Reusable scratch buffers for Clay's/Flatten's area-plane weights and Smooth's relaxation
        // weights - sized once and grown on demand rather than allocated fresh every frame a
        // stroke is held, matching the allocation-avoidance already applied to MeshRemesher
        // (see VertexSpatialGrid/EmitQuads history).
        private float[] _clayWeightScratch = System.Array.Empty<float>();
        private float[] _smoothWeightScratch = System.Array.Empty<float>();

        // Vertex indices actually moved by the current frame's brush application (across every
        // mirror sign) - cleared at the start of each Apply*Brush wrapper, filled in by the
        // matching *Local method(s), then handed to SculptableMesh.ApplyVerticesLocal so it only
        // has to update the triangle-raycast grid for triangles touching these vertices instead
        // of rescanning the whole mesh. See TriangleSpatialGrid for why this matters at higher
        // triangle counts.
        private readonly HashSet<int> _dirtyVertexScratch = new HashSet<int>();

        // Persistent, grow-on-demand NativeArray scratch shared by every Burst job below -
        // sized to the current candidate footprint, never the whole mesh (see EnsureNativeScratch).
        // Allocator.Persistent (not TempJob) since these are reused every frame of a held stroke,
        // not allocated fresh each call. Positions/normals/mask are gathered from the managed
        // arrays into these once per job dispatch (GatherCandidatesNative); results are scattered
        // back via ScatterJobResults, which also rebuilds _dirtyVertexScratch exactly as the
        // managed per-candidate loops did (each job's AppliedOut flag replicates that brush's own
        // dirty-marking rule - see each job struct's remarks for the exact condition it mirrors).
        private NativeArray<Vector3> _nativePositionsIn;
        private NativeArray<Vector3> _nativeNormalsIn;
        private NativeArray<float> _nativeMaskIn;
        private NativeArray<Vector3> _nativePositionsOut;
        private NativeArray<byte> _nativeAppliedOut;
        // Clay/Flatten extra scratch (pass-1 weighted reduction inputs to pass-2 - both brushes
        // share ClayWeightJob for that pass) - grown alongside
        // the arrays above for simplicity; the extra memory is trivial at footprint-bounded sizes.
        private NativeArray<float> _nativeClayWeights;
        private NativeArray<Vector3> _nativeClayWeightedPos;
        private NativeArray<Vector3> _nativeClayWeightedNormal;
        // Each candidate's position as of THIS stroke's start (see
        // SculptableMesh.StrokeStartPosition) - the reference ClampStrokeDepth measures Clay's
        // per-stroke buildup cap against (and Flatten's contrast cap - see
        // FlattenContrastLimit). Gathered in the Clay/Flatten job paths only, alongside
        // GatherCandidatesNative's shared arrays; the managed path reads StrokeStartPosition
        // directly.
        private NativeArray<Vector3> _nativeClayStrokeStart;
        private int _nativeScratchCapacity;

        // Clay's alpha stamp (see BrushAlphaLibrary) baked into a NativeArray once per type
        // change rather than ported to Burst noise/hash math - the CPU already computes and
        // caches this exact float[] forever per BrushAlphaType, so a job just needs a Burst-safe
        // bilinear lookup into a copy of it (see ClayDisplacementJob.SampleAlphaBilinear).
        private NativeArray<float> _nativeAlphaSamples;
        private int _nativeAlphaSize;
        private BrushAlphaType _nativeAlphaCachedType = (BrushAlphaType)(-1);

        private void EnsureNativeScratch(int count)
        {
            if (_nativeScratchCapacity < count)
            {
                DisposeNativeVertexScratch();
                _nativeScratchCapacity = Mathf.Max(Mathf.NextPowerOfTwo(count), MinJobVertexCount);
                _nativePositionsIn = new NativeArray<Vector3>(_nativeScratchCapacity, Allocator.Persistent);
                _nativeNormalsIn = new NativeArray<Vector3>(_nativeScratchCapacity, Allocator.Persistent);
                _nativeMaskIn = new NativeArray<float>(_nativeScratchCapacity, Allocator.Persistent);
                _nativePositionsOut = new NativeArray<Vector3>(_nativeScratchCapacity, Allocator.Persistent);
                _nativeAppliedOut = new NativeArray<byte>(_nativeScratchCapacity, Allocator.Persistent);
                _nativeClayWeights = new NativeArray<float>(_nativeScratchCapacity, Allocator.Persistent);
                _nativeClayWeightedPos = new NativeArray<Vector3>(_nativeScratchCapacity, Allocator.Persistent);
                _nativeClayWeightedNormal = new NativeArray<Vector3>(_nativeScratchCapacity, Allocator.Persistent);
                _nativeClayStrokeStart = new NativeArray<Vector3>(_nativeScratchCapacity, Allocator.Persistent);
            }
        }

        private void DisposeNativeVertexScratch()
        {
            if (_nativePositionsIn.IsCreated) _nativePositionsIn.Dispose();
            if (_nativeNormalsIn.IsCreated) _nativeNormalsIn.Dispose();
            if (_nativeMaskIn.IsCreated) _nativeMaskIn.Dispose();
            if (_nativePositionsOut.IsCreated) _nativePositionsOut.Dispose();
            if (_nativeAppliedOut.IsCreated) _nativeAppliedOut.Dispose();
            if (_nativeClayWeights.IsCreated) _nativeClayWeights.Dispose();
            if (_nativeClayWeightedPos.IsCreated) _nativeClayWeightedPos.Dispose();
            if (_nativeClayWeightedNormal.IsCreated) _nativeClayWeightedNormal.Dispose();
            if (_nativeClayStrokeStart.IsCreated) _nativeClayStrokeStart.Dispose();
        }

        private void EnsureAlphaNative()
        {
            if (!_nativeAlphaSamples.IsCreated)
                _nativeAlphaSamples = new NativeArray<float>(64 * 64, Allocator.Persistent);

            if (alphaType != _nativeAlphaCachedType)
            {
                BrushAlphaLibrary.AlphaData data = BrushAlphaLibrary.Get(alphaType);
                NativeArray<float>.Copy(data.Samples, _nativeAlphaSamples, data.Samples.Length);
                _nativeAlphaSize = data.Size;
                _nativeAlphaCachedType = alphaType;
            }
        }

        // Smooth-only, full-MESH-sized scratch (unlike every other brush's footprint-sized
        // scratch above) - Smooth's relaxation needs a neighbor's position even when that
        // neighbor sits outside the current brush footprint, so it needs a way to tell "is this
        // global vertex index also one of this call's candidates" (_nativeVertexToSlot) and a
        // fallback position source for when it isn't (_nativeFullPositionMirror). Both avoid an
        // O(total vertex count) COST despite being O(total vertex count) SIZED: the slot map is
        // only ever touched at the (footprint-bounded) candidate indices - populated before the
        // job, reset back to -1 after, an O(1)-per-candidate operation - and only ALLOCATED/
        // filled with -1 once per topology change, not per call (see EnsureSmoothFullMeshScratch).
        // The position mirror IS refreshed via a full O(total) copy each Smooth call, but that's
        // a plain memcpy-like array copy, not per-vertex math - a deliberately accepted
        // tradeoff, see [[project_perf_overhaul_no_gpu_rewrite]] memory.
        private NativeArray<int> _nativeVertexToSlot;
        private NativeArray<Vector3> _nativeFullPositionMirror;
        private int _nativeFullMeshCapacity;
        private NativeArray<int> _nativeSmoothCandidates;

        private void EnsureSmoothFullMeshScratch(int totalVertexCount)
        {
            if (_nativeFullMeshCapacity == totalVertexCount) return;

            if (_nativeVertexToSlot.IsCreated) _nativeVertexToSlot.Dispose();
            if (_nativeFullPositionMirror.IsCreated) _nativeFullPositionMirror.Dispose();

            _nativeFullMeshCapacity = totalVertexCount;
            _nativeVertexToSlot = new NativeArray<int>(totalVertexCount, Allocator.Persistent);
            for (int i = 0; i < totalVertexCount; i++) _nativeVertexToSlot[i] = -1; // one-time O(total) init
            _nativeFullPositionMirror = new NativeArray<Vector3>(totalVertexCount, Allocator.Persistent);
        }

        private void OnDestroy() => ReleaseNativeResources();

        /// Frees every Allocator.Persistent array this component owns AND resets the capacity
        /// trackers that gate the Ensure* methods above, so they reallocate instead of handing
        /// out a disposed array. Called from OnDestroy, and from NativeReloadGuard before an
        /// editor domain reload - which wipes these fields WITHOUT calling OnDestroy, orphaning
        /// whatever they pointed at (see that class for the full story).
        internal void ReleaseNativeResources()
        {
            DisposeNativeVertexScratch();
            _nativeScratchCapacity = 0;
            if (_nativeAlphaSamples.IsCreated) _nativeAlphaSamples.Dispose();
            _nativeAlphaCachedType = (BrushAlphaType)(-1);
            if (_nativeVertexToSlot.IsCreated) _nativeVertexToSlot.Dispose();
            if (_nativeFullPositionMirror.IsCreated) _nativeFullPositionMirror.Dispose();
            _nativeFullMeshCapacity = 0;
            if (_nativeSmoothCandidates.IsCreated) _nativeSmoothCandidates.Dispose();
        }

        // Copies the current candidate footprint's position/normal/mask into the shared native
        // scratch (growing it first if needed) - shared gather step for every Tier-A job
        // (Inflate/Crease/DamStandard/Clay), which only ever read/write within the footprint
        // itself and never need to look outside it (unlike Smooth's neighbor lookups).
        private void GatherCandidatesNative(List<int> candidates, Vector3[] verts, Vector3[] normals, float[] mask)
        {
            EnsureNativeScratch(candidates.Count);
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                _nativePositionsIn[ci] = verts[i];
                _nativeNormalsIn[ci] = normals[i];
                _nativeMaskIn[ci] = mask[i];
            }
        }

        // Writes job results back into the managed vertex array and rebuilds the dirty set -
        // shared scatter step for every Tier-A job. Only consumes PositionsOut where AppliedOut
        // is set, exactly mirroring each managed loop's own "continue" (skip, don't mark dirty)
        // conditions - see each job struct's remarks.
        private void ScatterJobResults(List<int> candidates, Vector3[] verts)
        {
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                if (_nativeAppliedOut[ci] == 0) continue;
                int i = candidates[ci];
                sculptableMesh.RecordUndoBeforeIfNeeded(i);
                verts[i] = _nativePositionsOut[ci];
                _dirtyVertexScratch.Add(i);
            }
        }

        // Direct Burst port of ApplyInflateBrushLocalManaged's per-candidate body. AppliedOut
        // mirrors that method's "if (weight <= 0f) continue" - a candidate outside the radius or
        // fully masked never gets marked dirty, matching the managed path exactly.
        //
        // CompileSynchronously = true on every job struct in this file: Burst compiles jobs in a
        // BACKGROUND thread by default, running the plain-C#-fallback path (no real speedup, in
        // some cases slower than the managed method it's replacing) until that finishes - which
        // could take an unpredictable few seconds after each domain reload/Editor start, giving
        // inconsistent perf on whichever early large-radius stroke happens to race the compile.
        // Forcing synchronous compilation costs a one-time hitch on each job type's very first
        // Schedule() call instead, after which every subsequent call is fully Burst-compiled -
        // the better tradeoff for a live sculpting tool with occasional large-footprint strokes.
        [BurstCompile(CompileSynchronously = true)]
        private struct InflateJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> PositionsIn;
            [ReadOnly] public NativeArray<Vector3> NormalsIn;
            [ReadOnly] public NativeArray<float> MaskIn;
            public NativeArray<Vector3> PositionsOut;
            public NativeArray<byte> AppliedOut;

            public Vector3 LocalPoint;
            public float BrushRadius;
            public float Amount; // sign * brushStrength * InflateSpeed * dt, precomputed
            public bool Accumulate;
            public Vector3 LocalNormal;
            public float CapAmount; // brushRadius * InflateOffCapFactor * sign, only used when !Accumulate
            public float LerpFactorScale; // brushStrength * InflateSpeed * dt, only used when !Accumulate
            public bool FrontFacingOnly;
            public Vector3 CameraLocalPos;

            public void Execute(int index)
            {
                Vector3 pos = PositionsIn[index];
                float dist = Vector3.Distance(pos, LocalPoint);
                if (dist > BrushRadius) { AppliedOut[index] = 0; return; }

                float t01 = 1f - dist / BrushRadius;
                float weight = t01 * t01 * (3f - 2f * t01) * (1f - MaskIn[index])
                    * FrontFacingWeight(FrontFacingOnly, NormalsIn[index], pos, CameraLocalPos);
                if (weight <= 0f) { AppliedOut[index] = 0; return; }

                if (Accumulate)
                {
                    PositionsOut[index] = pos + NormalsIn[index] * (weight * Amount);
                }
                else
                {
                    Vector3 target = LocalPoint + LocalNormal * CapAmount;
                    Vector3 toTarget = target - pos;
                    PositionsOut[index] = pos + toTarget * Mathf.Clamp01(weight * LerpFactorScale);
                }
                AppliedOut[index] = 1;
            }
        }

        // Shared by Crease and DamStandard, which already share the same pinch+carve core in
        // their managed form - DirLocal/Lip default to zero for plain Crease (a zero DirLocal
        // makes the leading-edge dot-product test never fire, so the lip term naturally never
        // applies, no separate "HasDir" flag needed). AppliedOut mirrors the managed loops' own
        // rule: ANY candidate within BrushRadius counts as touched/dirty, regardless of the
        // resulting lerp factor - unlike Inflate/Clay, Crease/DamStandard never skip on
        // weight <= 0 alone (see ApplyCreaseBrushLocalManaged/ApplyDamStandardBrushLocalManaged).
        [BurstCompile(CompileSynchronously = true)]
        private struct CreaseJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> PositionsIn;
            // Was gathered-but-unread before Front Facing Only existed (GatherCandidatesNative
            // always populates all three arrays regardless of brush) - now actually read below.
            [ReadOnly] public NativeArray<Vector3> NormalsIn;
            [ReadOnly] public NativeArray<float> MaskIn;
            public NativeArray<Vector3> PositionsOut;
            public NativeArray<byte> AppliedOut;

            public Vector3 LocalPoint;
            public Vector3 LocalNormal;
            public Vector3 DirLocal; // Vector3.zero for plain Crease
            public float BrushRadius;
            public float Depth;
            public float Lip; // 0 for plain Crease
            public float Pinch;
            public float LerpFactorScale; // brushStrength * CreaseSpeed * dt
            public bool Accumulate;
            public float DepthRate; // sign * creaseDepthFactor * brushStrength * CreaseSpeed * dt
            public float LipRate; // 0 for plain Crease
            public float PinchRateScale; // creasePinch * brushStrength * CreaseSpeed * dt
            public bool FrontFacingOnly;
            public Vector3 CameraLocalPos;

            public void Execute(int index)
            {
                Vector3 pos = PositionsIn[index];
                Vector3 toVert = pos - LocalPoint;
                float dist = toVert.magnitude;
                if (dist > BrushRadius) { AppliedOut[index] = 0; return; }

                float t01 = 1f - dist / BrushRadius;
                float weight = t01 * t01 * t01 * (1f - MaskIn[index]) // sharper falloff than Clay's smoothstep
                    * FrontFacingWeight(FrontFacingOnly, NormalsIn[index], pos, CameraLocalPos);

                float alongNormal = Vector3.Dot(toVert, LocalNormal);
                Vector3 tangentialOffset = toVert - LocalNormal * alongNormal;
                bool hasLip = Vector3.Dot(tangentialOffset, DirLocal) > 0f;

                if (Accumulate)
                {
                    float normalRate = DepthRate;
                    if (hasLip) normalRate += LipRate;
                    Vector3 pinchDelta = -tangentialOffset * Mathf.Clamp01(weight * PinchRateScale);
                    PositionsOut[index] = pos + LocalNormal * (normalRate * weight) + pinchDelta;
                }
                else
                {
                    Vector3 pinched = tangentialOffset * (1f - Pinch * weight);
                    float normalOffset = Depth * weight;
                    if (hasLip) normalOffset += Lip * weight;

                    Vector3 target = LocalPoint + pinched + LocalNormal * normalOffset;
                    Vector3 toTarget = target - pos;
                    float lerp = Mathf.Clamp01(weight * LerpFactorScale);
                    PositionsOut[index] = pos + toTarget * lerp;
                }
                AppliedOut[index] = 1;
            }
        }

        // Shared by every brush's weight computation, multiplied in alongside the mask term
        // right next to it (MaskIn / sculptableMesh.Mask) - see frontFacingOnly's remarks for
        // what this is for. A vertex counts as front-facing when its OWN mesh normal points at
        // least partly back toward the camera; compared per-vertex against the camera's actual
        // local-space position rather than one shared view direction, so the test stays correct
        // up close, where a sculpt's own scale can be comparable to the camera's distance from
        // it. Plain float/Vector3 math (like ClayFalloff below), so Burst inlines it into a
        // job's Execute exactly the same way.
        private static float FrontFacingWeight(bool frontFacingOnly, Vector3 normalLocal, Vector3 posLocal, Vector3 cameraLocalPos)
        {
            if (!frontFacingOnly) return 1f;
            return Vector3.Dot(normalLocal, cameraLocalPos - posLocal) > 0f ? 1f : 0f;
        }

        // Clay's own radial falloff (see clayEdgeSoftness remarks) - full weight through the
        // inner (1 - clayEdgeSoftness) of the radius, smoothstepping down to 0 only across the
        // outer edge band. Shared by ClayWeightJob (Burst) and ApplyClayBrushLocalManaged so
        // both brush paths build an identical flat-topped profile; plain float math, so Burst
        // can inline it into the job same as any other method call.
        private static float ClayFalloff(float t01, float edgeSoftness)
        {
            // Max() rather than trusting the caller: ClayEdgeSoftness/the Range attribute both
            // clamp to 0.05, but a scene serialized before this field existed can still feed a
            // literal 0 through, and the divide below would turn that into NaN vertex positions
            // - which, unlike a merely wrong weight, permanently corrupts the mesh.
            edgeSoftness = Mathf.Max(edgeSoftness, 0.001f);
            if (t01 >= edgeSoftness) return 1f;
            float e = t01 / edgeSoftness;
            return e * e * (3f - 2f * e);
        }

        // Blends Clay's footprint shape between round (plain 3D distance, today's original
        // math) and square (Chebyshev distance across a tangent0/bitangent0 frame - the same
        // technique the alpha stamp below already uses for its own square domain). Returns a
        // t01 usable directly by ClayFalloff, exactly like the old inline `1f - dist/radius`
        // did - at roundness=1 this returns bit-for-bit the same value as before (the square
        // term is skipped entirely), so the default tip is unchanged.
        private static float ClayTipShapeT01(Vector3 toVert, float brushRadius, Vector3 tangent0, Vector3 bitangent0, float tipRoundness)
        {
            float invRadius = 1f / brushRadius;
            float roundT01 = 1f - toVert.magnitude * invRadius;
            if (tipRoundness >= 1f) return roundT01;

            float u = Vector3.Dot(toVert, tangent0);
            float v = Vector3.Dot(toVert, bitangent0);
            float squareT01 = 1f - Mathf.Max(Mathf.Abs(u), Mathf.Abs(v)) * invRadius;
            return Mathf.Lerp(squareT01, roundT01, tipRoundness);
        }

        // Clay's pass 1 (see ApplyClayBrushLocalManaged) - a per-candidate PARALLEL MAP, not a
        // parallel reduction: each thread only computes its own weighted contribution
        // (weight, weight*position, weight*normal). The actual sum-across-candidates happens
        // sequentially on the main thread afterward (see ApplyClayBrushLocalJob) - candidate
        // counts are footprint-bounded (hundreds-to-low-thousands), so summing floats
        // sequentially there is cheap enough that a second reduction job would cost more in
        // scheduling overhead than it saves.
        [BurstCompile(CompileSynchronously = true)]
        private struct ClayWeightJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> PositionsIn;
            [ReadOnly] public NativeArray<Vector3> NormalsIn;
            [ReadOnly] public NativeArray<float> MaskIn;
            public NativeArray<float> WeightsOut;
            public NativeArray<Vector3> WeightedPosOut;
            public NativeArray<Vector3> WeightedNormalOut;

            public Vector3 LocalPoint;
            public float BrushRadius;
            // Tangent frame built from the STROKE's raycast normal (not the area-averaged
            // plane normal - that isn't known until after this pass reduces), used only to
            // define the square profile's two in-plane axes. See TipRoundness/ClayTipShapeT01.
            public Vector3 Tangent0;
            public Vector3 Bitangent0;
            public float TipRoundness;
            public float EdgeSoftness;
            public bool FrontFacingOnly;
            public Vector3 CameraLocalPos;

            public void Execute(int index)
            {
                Vector3 pos = PositionsIn[index];
                Vector3 toVert = pos - LocalPoint;
                float t01 = ClayTipShapeT01(toVert, BrushRadius, Tangent0, Bitangent0, TipRoundness);
                if (t01 <= 0f)
                {
                    WeightsOut[index] = 0f;
                    WeightedPosOut[index] = Vector3.zero;
                    WeightedNormalOut[index] = Vector3.zero;
                    return;
                }

                float w = ClayFalloff(t01, EdgeSoftness) * (1f - MaskIn[index])
                    * FrontFacingWeight(FrontFacingOnly, NormalsIn[index], pos, CameraLocalPos);
                WeightsOut[index] = w;
                WeightedPosOut[index] = pos * w;
                WeightedNormalOut[index] = NormalsIn[index] * w;
            }
        }

        // Clay's pass 2 - per-candidate displacement toward the plane computed from pass 1's
        // reduction. AppliedOut mirrors ApplyClayBrushLocalManaged's three skip points exactly:
        // weight <= 0 before any alpha sampling, outside the (rotated/scaled) alpha stamp's
        // [-1,1] square, and weight <= 0 again after the alpha multiply - all three leave a
        // candidate untouched/not-dirty, matching the managed loop's "continue" at each point.
        [BurstCompile(CompileSynchronously = true)]
        private struct ClayDisplacementJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> PositionsIn;
            // See ClampStrokeDepth - the cap is per-vertex, relative to where this stroke found
            // each one, so it bounds only what this stroke added.
            [ReadOnly] public NativeArray<Vector3> StrokeStartIn;
            [ReadOnly] public NativeArray<float> WeightsIn;
            [ReadOnly] public NativeArray<float> AlphaSamples;
            public NativeArray<Vector3> PositionsOut;
            public NativeArray<byte> AppliedOut;

            public Vector3 LocalPoint;
            public Vector3 PlaneOrigin;
            public Vector3 PlaneNormal;
            public Vector3 Tangent;
            public Vector3 Bitangent;
            public float Height;
            public float LerpFactorScale; // brushStrength * ClaySpeed * dt
            public bool UseAlpha;
            public bool InvertAlpha;
            public float CosR, SinR;
            public float InvStampRadius;
            public int AlphaSize;
            public bool Accumulate;
            public float Rate; // sign * brushStrength * ClaySpeed * dt, only used when Accumulate
            // Signed per-stroke displacement cap along PlaneNormal - see ClampStrokeDepth.
            // Deliberately NOT multiplied by this dab's weight; see ClayStrokeDepthLimit.
            public float MaxAlong;

            public void Execute(int index)
            {
                float weight = WeightsIn[index];
                if (weight <= 0f) { AppliedOut[index] = 0; return; }

                Vector3 pos = PositionsIn[index];

                if (UseAlpha)
                {
                    Vector3 toVert = pos - LocalPoint;
                    float u = Vector3.Dot(toVert, Tangent) * InvStampRadius;
                    float v = Vector3.Dot(toVert, Bitangent) * InvStampRadius;
                    float ru = u * CosR - v * SinR;
                    float rv = u * SinR + v * CosR;
                    if (ru < -1f || ru > 1f || rv < -1f || rv > 1f) { AppliedOut[index] = 0; return; }

                    float a = SampleAlphaBilinear(AlphaSamples, AlphaSize, ru * 0.5f + 0.5f, rv * 0.5f + 0.5f);
                    weight *= InvertAlpha ? 1f - a : a;
                    if (weight <= 0f) { AppliedOut[index] = 0; return; }
                }

                // See ApplyClayBrushLocalManaged's Accumulate branch for why this blends two
                // terms (a constant build rate + a self-limiting flatten-toward-plane term)
                // instead of a plain push - fills dips/settles bumps while still building
                // indefinitely as long as the stroke is held.
                // Height is scaled by the same per-vertex `weight` the lerp factor uses, so the
                // TARGET follows the brush profile rather than being one flat height shared by
                // the whole footprint. Without this the falloff only controlled how FAST each
                // vertex reached an identical height - so it washed out completely on any dab
                // held to convergence, and Clay's settled form was a flat-topped cylinder with
                // near-vertical walls at the footprint boundary (the "blobby" result) instead
                // of the falloff-shaped pad the profile describes. Same reason this multiply
                // has to come AFTER the alpha multiply above: an alpha stamp previously only
                // varied approach speed and flattened out to the same uniform plateau at
                // convergence, where now it carves real relief into the deposited clay.
                Vector3 toPlane = pos - PlaneOrigin;
                float alongNormal = Vector3.Dot(toPlane, PlaneNormal);
                Vector3 tangentialOffset = toPlane - PlaneNormal * alongNormal;
                Vector3 target = PlaneOrigin + tangentialOffset + PlaneNormal * (Height * weight);
                Vector3 toTarget = target - pos;
                float lerp = Mathf.Clamp01(weight * LerpFactorScale);

                Vector3 moved = Accumulate
                    ? pos + PlaneNormal * (Rate * weight) + toTarget * lerp
                    : pos + toTarget * lerp;

                // Inlined ClampStrokeDepth - a Burst job can't call the shared static without
                // dragging Vector3 method-call overhead into the inner loop, and the two must
                // stay identical or the Burst and managed paths would diverge (see
                // MinJobVertexCount: which one runs depends only on footprint size).
                float along = Vector3.Dot(moved - StrokeStartIn[index], PlaneNormal);
                bool overshot = Height >= 0f ? along > MaxAlong : along < MaxAlong;
                if (overshot) moved -= PlaneNormal * (along - MaxAlong);

                PositionsOut[index] = moved;
                AppliedOut[index] = 1;
            }

            // Line-for-line port of BrushAlphaLibrary.Sample, operating on a NativeArray copy of
            // the same cached float[] instead of porting any noise/hash generation math to Burst.
            private static float SampleAlphaBilinear(NativeArray<float> samples, int size, float u, float v)
            {
                u = Mathf.Clamp01(u);
                v = Mathf.Clamp01(v);
                float fx = u * (size - 1);
                float fy = v * (size - 1);
                int x0 = Mathf.FloorToInt(fx);
                int y0 = Mathf.FloorToInt(fy);
                int x1 = Mathf.Min(x0 + 1, size - 1);
                int y1 = Mathf.Min(y0 + 1, size - 1);
                float tx = fx - x0;
                float ty = fy - y0;

                float s00 = samples[y0 * size + x0];
                float s10 = samples[y0 * size + x1];
                float s01 = samples[y1 * size + x0];
                float s11 = samples[y1 * size + x1];
                float a = Mathf.Lerp(s00, s10, tx);
                float b = Mathf.Lerp(s01, s11, tx);
                return Mathf.Lerp(a, b, ty);
            }
        }

        // Flatten's pass 2 - direct Burst port of ApplyFlattenBrushLocalManaged's per-candidate
        // body. Pass 1 is ClayWeightJob, reused as-is with a round tip and full edge softness
        // (see ApplyFlattenBrushLocalJob): both brushes need exactly the same thing from it -
        // per-vertex falloff weights plus the weighted position/normal sums the area plane is
        // reduced from - so a second copy of that job would only be Clay's with two parameters
        // frozen. AppliedOut mirrors the managed loop's single "weight <= 0 -> continue".
        [BurstCompile(CompileSynchronously = true)]
        private struct FlattenDisplacementJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> PositionsIn;
            // Only read in the Contrast direction - see FlattenContrastLimit.
            [ReadOnly] public NativeArray<Vector3> StrokeStartIn;
            [ReadOnly] public NativeArray<float> WeightsIn;
            public NativeArray<Vector3> PositionsOut;
            public NativeArray<byte> AppliedOut;

            public Vector3 PlaneOrigin; // already includes the flattenPlaneOffset shift
            public Vector3 PlaneNormal;
            public float LerpFactorScale; // brushStrength * FlattenSpeed * dt
            public bool Contrast; // false = flatten toward the plane, true = push away from it
            public float MaxOffStart; // brushRadius * FlattenContrastLimit, Contrast only

            public void Execute(int index)
            {
                float weight = WeightsIn[index];
                if (weight <= 0f) { AppliedOut[index] = 0; return; }

                Vector3 pos = PositionsIn[index];
                float along = Vector3.Dot(pos - PlaneOrigin, PlaneNormal);
                float lerp = Mathf.Clamp01(weight * LerpFactorScale);
                Vector3 moved = pos + PlaneNormal * ((Contrast ? along : -along) * lerp);

                if (Contrast)
                {
                    float fromStart = Vector3.Dot(moved - StrokeStartIn[index], PlaneNormal);
                    if (fromStart > MaxOffStart) moved -= PlaneNormal * (fromStart - MaxOffStart);
                    else if (fromStart < -MaxOffStart) moved -= PlaneNormal * (fromStart + MaxOffStart);
                }

                PositionsOut[index] = moved;
                AppliedOut[index] = 1;
            }
        }

        // Precomputes Smooth's per-candidate falloff weight once, shared read-only across every
        // relaxation pass - direct port of ApplySmoothBrushLocalManaged's first loop.
        [BurstCompile(CompileSynchronously = true)]
        private struct SmoothWeightJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> PositionsIn;
            // Was gathered-but-unread before Front Facing Only existed (GatherCandidatesNative
            // always populates all three arrays regardless of brush) - now actually read below.
            [ReadOnly] public NativeArray<Vector3> NormalsIn;
            [ReadOnly] public NativeArray<float> MaskIn;
            public NativeArray<float> WeightsOut;
            public Vector3 LocalPoint;
            public float BrushRadius;
            public bool FrontFacingOnly;
            public Vector3 CameraLocalPos;

            public void Execute(int index)
            {
                Vector3 pos = PositionsIn[index];
                float dist = Vector3.Distance(pos, LocalPoint);
                if (dist > BrushRadius) { WeightsOut[index] = 0f; return; }
                float t01 = 1f - dist / BrushRadius;
                WeightsOut[index] = t01 * t01 * (3f - 2f * t01) * (1f - MaskIn[index]) // smoothstep, masked-out
                    * FrontFacingWeight(FrontFacingOnly, NormalsIn[index], pos, CameraLocalPos);
            }
        }

        // One relaxation pass, scheduled once per pass (ping-ponging PositionsRead/PositionsWrite
        // between passes - see ApplySmoothBrushLocalJob). This is a JACOBI-style parallel
        // relaxation (every candidate reads last pass's values, writes this pass's values to a
        // SEPARATE buffer) rather than the managed method's GAUSS-SEIDEL-style in-place update
        // (candidate N can see candidate N-1's ALREADY-updated position within the SAME pass,
        // since the managed loop mutates verts[] directly as it goes). This is a deliberate,
        // necessary substitution, not an oversight: Gauss-Seidel's per-candidate sequential
        // dependency is fundamentally not parallelizable, while Jacobi is its standard parallel
        // analog for exactly this kind of iterative relaxation. Both converge toward the same
        // smoothed result; they differ in the transient path between passes, most visible at
        // high brushStrength (many folded passes) - verified empirically to still converge to a
        // visually/numerically reasonable result, see [[project_perf_overhaul_no_gpu_rewrite]].
        [BurstCompile(CompileSynchronously = true)]
        private struct SmoothRelaxJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> Candidates; // global vertex indices, candidate-indexed
            [ReadOnly] public NativeArray<int> AdjacencyOffsets; // full mesh, CSR
            [ReadOnly] public NativeArray<int> AdjacencyNeighbors; // full mesh, CSR
            [ReadOnly] public NativeArray<int> VertexToSlot; // full mesh, -1 if not a candidate this call
            [ReadOnly] public NativeArray<Vector3> FullPositions; // full mesh mirror, for non-candidate neighbors
            [ReadOnly] public NativeArray<Vector3> PositionsRead; // candidate-indexed, this pass's input
            public NativeArray<Vector3> PositionsWrite; // candidate-indexed, this pass's output
            [ReadOnly] public NativeArray<float> Weights; // candidate-indexed, constant across passes
            public float PassFactor; // 1f for a full pass, partialFactor for the trailing partial one
            public float LerpFactorScale; // brushStrength * SmoothSpeed * dt

            public void Execute(int ci)
            {
                Vector3 currentPos = PositionsRead[ci];
                float w = Weights[ci];
                if (w <= 0f) { PositionsWrite[ci] = currentPos; return; }

                int globalIdx = Candidates[ci];
                int start = AdjacencyOffsets[globalIdx];
                int end = AdjacencyOffsets[globalIdx + 1];
                if (end == start) { PositionsWrite[ci] = currentPos; return; } // no neighbors - GetNeighborAverage returns self

                Vector3 sum = Vector3.zero;
                for (int n = start; n < end; n++)
                {
                    int neighborGlobal = AdjacencyNeighbors[n];
                    int slot = VertexToSlot[neighborGlobal];
                    sum += slot >= 0 ? PositionsRead[slot] : FullPositions[neighborGlobal];
                }
                Vector3 average = sum / (end - start);

                Vector3 toAverage = average - currentPos;
                float lerp = Mathf.Clamp01(w * PassFactor * LerpFactorScale);
                PositionsWrite[ci] = currentPos + toAverage * lerp;
            }
        }

        // Smooth's per-application relaxation strength: brushStrength scales how many
        // Laplacian relaxation passes get folded into one application (from a single partial
        // pass at minimum strength up to MaxSmoothIterations full passes at maximum), not just
        // how far a single pass blends toward the neighbor average. A single 1-ring average is
        // inherently weak - it only pulls in direct neighbors, so no per-pass blend factor
        // alone removes wider bumps in one shot. Repeated passes propagate influence from
        // further-out neighbors each time, which is what actually flattens noise - the same
        // reason ZBrush/Blender's Smooth intensity effectively controls a repeated-relaxation
        // count rather than a single lerp factor. At the default brushStrength (0.1) this
        // resolves to exactly 1 pass, matching the brush's old feel; only higher strength
        // ramps into genuinely stronger multi-pass smoothing.
        private const int MaxSmoothIterations = 10;

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

        // Windows Ink (and any other Input System pen backend) exposes a stylus as
        // Pen.current with a 0-1 pressure axis. CurrentPressure reads _smoothedPenPressure
        // (updated once/frame by UpdatePenPressure - see its remarks) rather than the raw
        // control directly, so this stays safe to read more than once per frame (Mirror can
        // call each brush's apply path once per mirrored plane).
        //
        // Two shaping steps turn the raw 0-1 axis into something that feels like ZBrush/
        // Blender rather than "underwhelming and jittery" (reported after wiring pressure in
        // directly): most tablets rarely report raw pressure anywhere near 1.0 even under a
        // firm press, so brush strengths tuned for a constant mouse click (always 1) read as
        // underpowered; and sensor noise in raw pressure was showing up frame-to-frame as a
        // visibly uneven stroke instead of an evenly built-up ridge. pressureFloor guarantees
        // even the lightest touch still applies a meaningful fraction of full strength, and
        // pressureCurve reshapes how the stylus's travel maps onto that remaining range.
        //
        // The curve used to be a fixed sqrt, which overcorrected the "underpowered" complaint
        // into an oversensitive one: sqrt is steepest at zero (its slope there is unbounded),
        // so the lightest touches - the noisiest part of the sensor, and the part used for
        // delicate passes - produced the LARGEST strength swings, on top of a 0.35 floor that
        // already started the response at over a third power. Both are now serialized fields
        // with an exponent >1 by default; see their remarks for the numbers.
        private float CurrentPressure
        {
            get
            {
                var pen = Pen.current;
                if (pen == null || !pen.tip.isPressed) return 1f;
                float shaped = Mathf.Pow(Mathf.Clamp01(_smoothedPenPressure), pressureCurve);
                return pressureFloor + (1f - pressureFloor) * shaped;
            }
        }

        // Real tablet pressure sensors are noisy enough that reading Pen.current.pressure raw
        // every frame produces a visibly jittery, stair-stepped stroke rather than the smooth,
        // evenly-building ridge ZBrush/Blender strokes have - this exponentially chases the raw
        // value instead of tracking it 1:1, filtering that noise out before CurrentPressure's
        // curve is applied. Deliberately updated exactly once per frame (from Update(), not from
        // inside CurrentPressure's getter) so its smoothing rate doesn't scale with how many
        // times a brush's apply path runs this frame (once per Mirror plane).
        private void UpdatePenPressure()
        {
            var pen = Pen.current;
            // Deliberately leaves _smoothedPenPressure untouched while not pressed, rather than
            // resetting it to 1 (full strength) - resetting meant every new touch-down eased
            // DOWN from full strength for its first few frames instead of picking up from
            // wherever pressure actually was, which read as a strength spike right at the start
            // of each stroke (most visible when toggling Accumulate - that click lifts the pen to
            // tap the UI checkbox, then touches back down to resume, so the very next stroke got
            // the spike). Holding the last value means a fresh touch continues smoothing from a
            // realistic starting point instead of a synthetic one.
            if (pen == null || !pen.tip.isPressed) return;

            float raw = pen.pressure.ReadValue();
            _smoothedPenPressure = Mathf.Lerp(_smoothedPenPressure, raw, Mathf.Clamp01(Time.deltaTime * PressureSmoothingSpeed));
        }

        // What every brush handler's OFF/plateau path applies - brushStrength scaled by live pen
        // pressure. Deliberately never mutates brushStrength itself: that field backs the
        // BrushStrength property the UI slider is bound to (SculptUIBuilder), so a mouse user
        // (or a pen user between strokes) always sees the base value they set, not a
        // pressure-jittered one.
        private float EffectiveBrushStrength => brushStrength * CurrentPressure;

        // Accumulate mode's brush handlers reapply their rate every single frame with no
        // self-limiting cap toward a target (unlike the OFF/plateau path's Clamp01 ease-toward-
        // height, which converges to the same result regardless of small pressure variance) - so
        // any pressure fluctuation compounds for as long as the brush is held instead of
        // converging, reading as "wildly out of control" rather than merely textured. Blending
        // only halfway toward CurrentPressure (instead of using it directly, like the OFF path
        // does) keeps Accumulate responsive to a deliberately light vs. hard press while far less
        // twitchy about the moment-to-moment fluctuation the OFF path's own Clamp01 already
        // shrugs off on its own.
        private const float AccumulatePressureInfluence = 0.5f;

        // Mitigates (does not fully solve - the real fix is distance-based stroke spacing, a
        // bigger separate change) the "blob where a stroke decelerates into a stop" artifact:
        // every brush deposits once per rendered FRAME, not once per unit of distance the cursor
        // actually travels, so a decelerating stroke packs many overlapping full-strength
        // deposits into a small area right where it slows down, on top of the fast-moving middle
        // of the same stroke that only got one deposit per (much larger) step. Scaling
        // Accumulate's rate down as stroke speed drops toward zero softens that without
        // eliminating the deliberate ZBrush/Blender-style "hold in place to keep building"
        // feature outright - AccumulateSpeedFloor keeps a genuine stationary hold still building,
        // just more gently, rather than stopping dead.
        // Was a flat 1 world unit/sec. That made pacing depend on scene scale AND on brush
        // size: the same physical drag reads as a much "slower" stroke with a small brush or on
        // a small object, so the carve quietly faded toward nothing on exactly the close-in
        // detail work Crease/Dam Standard exist for. Pacing is a statement about travel relative
        // to the BRUSH ("how many brush widths did this stroke cover"), so the reference is one
        // brush diameter per second and the absolute constant is gone.
        private float StrokePacingReference => Mathf.Max(brushRadius * 2f, 0.001f);
        // Diameters/sec past which the pacing stops counting extra speed. Purely a spike guard
        // for a frame in which the cursor teleports (a drag re-entering the mesh, a frame
        // hitch); ordinary strokes live far below it, and inside it the factor stays LINEAR in
        // speed, which is what makes the deposit per centimetre of travel speed-invariant.
        private const float StrokePacingCeiling = 3f;
        // Calibration gain - the counterpart of ClayReferenceStrokeSpeed. Clay got an explicit
        // constant so that switching it to distance pacing deposited what time pacing used to;
        // Crease/Dam Standard/Inflate got the speed factor with no such compensation, and at a
        // normal carving speed that silently cost them roughly 4x. That is the difference
        // between "calmer" and the reported "straight up not working". Sized so one pass at max
        // Brush Strength reaches the full plateau/dab depth, which puts the 0.1 default at a
        // clearly visible cut rather than a rounding error.
        private const float StrokePacingGain = 4f;
        // Rate a motionless cursor still builds at, as a fraction of a full-speed stroke's.
        // Only used when Build Up on Hold is ON; with it OFF there is no floor at all, which is
        // what turns "held in place" into "deposits nothing" - see AccumulateSpeedFactor.
        private const float AccumulateSpeedFloor = 0.35f;
        private const float StrokeSpeedSmoothingSpeed = 15f;
        private Vector3? _lastStrokeHitPointWorld;
        private float _strokeSpeed;

        // Called once per brush application (not per Mirror copy - see UpdatePenPressure's own
        // remarks on why that matters) from each accumulate-capable brush's Handle*Input, right
        // after that frame's raycast hit is known. Smoothed for the same reason pressure is -
        // raw per-frame speed is noisy (frame-time jitter, small hand tremor), and feeding that
        // straight into the accumulate rate would just trade "blob at the stop" for "flicker
        // mid-stroke".
        private void UpdateStrokeSpeed(Vector3 worldHitPoint)
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            // No prior point on a stroke's first frame - every stroke's first touch is
            // inherently a stationary point sample, not yet a stroke, so treat it as speed 0
            // (gentle first dab) rather than assuming full speed.
            float instant = _lastStrokeHitPointWorld.HasValue
                ? Vector3.Distance(worldHitPoint, _lastStrokeHitPointWorld.Value) / dt
                : 0f;
            _lastStrokeHitPointWorld = worldHitPoint;
            _strokeSpeed = Mathf.Lerp(_strokeSpeed, instant, Mathf.Clamp01(dt * StrokeSpeedSmoothingSpeed));
        }

        // With Build Up on Hold OFF the floor drops to zero, which makes the per-frame deposit
        // proportional to stroke speed - and that is exactly distance pacing: a stroke half as
        // fast deposits half as much per frame but spends twice as many frames covering the same
        // ground, so the material laid down per centimetre is the same either way. A stopped
        // cursor has zero speed and so deposits nothing. This is the cheap form of the
        // "distance-based stroke spacing" the comment above calls the real fix; Clay does the
        // full sub-stepped version (ApplyClayStroke) because its flat stamp leaves visible gaps
        // between dabs if a fast drag outruns the frame rate, which these smooth-falloff brushes
        // do not.
        //
        // Note _strokeSpeed is smoothed, so lifting off or stopping dead fades out over ~0.2s
        // rather than cutting instantly. That is deliberate - it reads as a soft stroke end
        // rather than a hard clip - but it does mean "stop" is a fast taper, not a hard stop.
        // Build Up on Hold ON keeps the original capped shape verbatim: a floor so a genuine
        // stationary hold still builds, saturating at 1 once the stroke is moving normally.
        //
        // With it OFF the factor is LINEAR in speed (up to the spike guard) and carries the
        // calibration gain. The linearity is the distance pacing - clamping it at 1 was the
        // other half of why the carving brushes went quiet, because every stroke faster than
        // the reference fell straight back to time pacing and so still deposited less the
        // faster it moved. A stopped cursor is still exactly zero, which is the behaviour this
        // whole mechanism exists for.
        private float AccumulateSpeedFactor => buildUpOnHold
            ? Mathf.Lerp(AccumulateSpeedFloor, 1f, Mathf.Clamp01(_strokeSpeed / StrokePacingReference))
            : StrokePacingGain * Mathf.Min(_strokeSpeed / StrokePacingReference, StrokePacingCeiling);

        private float EffectiveBrushStrengthAccumulate => brushStrength * Mathf.Lerp(1f, CurrentPressure, AccumulatePressureInfluence) * AccumulateSpeedFactor * accumulateStrength;

        /// Build-up rate for the Accumulate-OFF path - the one that eases toward a single dab's
        /// worth of depth and stops - as used by Crease, Dam Standard and Inflate.
        ///
        /// That path is self-limiting, so it was never the runaway "digs forever" case Accumulate
        /// is. But it still approaches its plateau on a CLOCK, which means holding still keeps
        /// deepening the cut until it bottoms out, and easing off to place a careful crease cuts
        /// deeper than drawing the same line at speed. Pacing the approach by stroke speed makes
        /// the depth a function of the path drawn rather than how long the cursor lingered on it.
        /// This can only ever slow the approach, never overshoot: the plateau still caps it.
        ///
        /// Deliberately NOT applied to Smooth, Flatten or Move. Holding those in place to keep
        /// working an area is the point of them, and every sculpting app behaves that way - the
        /// complaint this addresses was specifically about carving brushes deepening under a
        /// stationary or slowing cursor.
        /// Multiplied by accumulateStrength for the same reason the Accumulate path is: with
        /// Accumulate OFF (Crease's default) that slider was a dead control, so the one knob a
        /// user reaches for when a carve is too shallow did nothing at all on the brush most
        /// likely to need it. It is the build-up strength for BOTH build-up modes now - see the
        /// field's own remarks and its "Build-Up Strength" label in SculptUIBuilder.
        private float EffectiveBrushStrengthPlateau => EffectiveBrushStrength * accumulateStrength
            * (buildUpOnHold ? 1f : AccumulateSpeedFactor);

        /// Clay's own accumulate strength, identical to the above minus AccumulateSpeedFactor.
        /// That factor exists to stop a time-driven brush dumping material wherever the cursor
        /// slows down; Clay no longer deposits on a clock at all (see ApplyClayStroke), so a
        /// slow stroke already lays down exactly the same material per unit of travel as a fast
        /// one. Keeping the factor here would double-count speed and invert the intent - fast
        /// strokes would deposit MORE per unit distance than careful ones. The other brushes
        /// are still time-driven and still want it.
        private float EffectiveClayStrengthAccumulate => brushStrength * Mathf.Lerp(1f, CurrentPressure, AccumulatePressureInfluence) * accumulateStrength;

        // One value for every brush, unlike BrushStrength above - see _brushStrengthPerType.
        public float BrushRadius
        {
            get => brushRadius;
            set => brushRadius = Mathf.Clamp(value, MinBrushRadius, MaxBrushRadius);
        }
        public bool IsAdjustingStrength => _isAdjustingStrength;

        // Polled by SculptUIBuilder every frame to draw the 2D ring cursor (see
        // UpdateBrushCursor) - same read-only delegation pattern as IsAdjustingStrength
        // above.
        public bool ShowBrushCursor => _showBrushCursor;
        public Vector2 BrushCursorScreenPosition => _brushCursorScreenPos;
        public float BrushCursorScreenDiameter => _brushCursorScreenDiameter;
        public Color BrushCursorColor => _brushCursorColor;
        public bool BrushCursorDashed => _brushCursorDashed;

        // The Lazy Mouse rope, for the tether line SculptUIBuilder draws while a stabilized
        // stroke is running (ZBrush/Nomad both draw the same thing). Without it the stabilizer
        // is invisible: the brush is acting somewhere the pointer is not, with nothing on screen
        // to say so, and a lag you cannot see reads as the app dropping input rather than as the
        // smoothing you asked for. The line also makes the rope's LENGTH legible, which is what
        // the Radius slider is actually setting.
        //
        // `To` is where the brush is really working (and where UpdateBrushCursor now puts the
        // ring, so the ring never lies about where a dab is about to land); `From` is the raw
        // pointer. Only meaningful while Active - the two lag one frame behind a brush handler
        // having run, which is invisible at frame rate and is why both come from the same
        // recorded pair rather than one being re-read live here.
        public bool LazyMouseTetherActive => _lazyMouseActive && _showBrushCursor;
        public Vector2 LazyMouseTetherFrom => _lazyMouseRawScreenPos;
        public Vector2 LazyMouseTetherTo => _lazyMouseScreenPos;

        // 0 right on stroke-release, easing back to 1 over StrokeEndFadeDuration - see
        // _strokeEndFadeTimer. SculptUIBuilder multiplies every cursor layer's own alpha by this.
        public float BrushCursorFadeAlpha => _strokeEndFadeTimer <= 0f
            ? 1f
            : Mathf.Clamp01(1f - _strokeEndFadeTimer / StrokeEndFadeDuration);

        // "Undo"/"Redo" toast - see TriggerUndoRedoFeedback/_undoToastTimer.
        public bool ShowUndoToast => _undoToastTimer > 0f;
        public string UndoToastText => _undoToastText;
        public float UndoToastAlpha => _undoToastTimer <= 0f
            ? 0f
            : (_undoToastTimer >= UndoToastFadeDuration ? 1f : _undoToastTimer / UndoToastFadeDuration);
        // 0 the instant it appears, 1 the instant it's gone - drives a gentle upward drift
        // (SculptUIBuilder) so it reads as "popping up", not just fading in place.
        public float UndoToastProgress01 => 1f - Mathf.Clamp01(_undoToastTimer / UndoToastDuration);

        public bool IsMaskPaintMode
        {
            get => _isMaskPaintMode;
            set
            {
                if (_isMaskPaintMode == value) return;
                _isMaskPaintMode = value;
                if (_isMaskPaintMode) EndMoveDrag(); // don't leave a grab mid-drag while painting mask
            }
        }

        public BrushType CurrentBrush
        {
            get => currentBrush;
            set
            {
                if (currentBrush != value)
                {
                    EndMoveDrag();
                    _lastDamHoverLocal = null;
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
                }
            }
        }
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
        public int RemeshResolution { get => remeshResolution; set => remeshResolution = Mathf.Clamp(value, 4, 500); }

        // One value shared by every brush (unlike BrushStrength) - lazy mouse is an input-
        // smoothing behavior, not a property of any particular brush's effect.
        public bool LazyMouseEnabled { get => lazyMouseEnabled; set => lazyMouseEnabled = value; }
        public bool BuildUpOnHold { get => buildUpOnHold; set => buildUpOnHold = value; }
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
            EndMoveDrag();
            if (!EditHistory.CanUndo) return; // nothing actually happened - no flash/toast for a no-op keypress
            EditHistory.Undo();
            TriggerUndoRedoFeedback("Undo");
        }

        public void Redo()
        {
            EndMoveDrag();
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
            _undoToastText = label;
            _undoToastTimer = UndoToastDuration;
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

        // Double-click-in-viewport object switching (see HandleObjectPickDoubleClick) - lets
        // you make a different scene object the sculpt target without hunting for its row in
        // the Scene Graph panel. Tracked here rather than via EventSystem's PointerClick
        // clickCount (what SceneGraphUIBuilder's row double-click uses) because the viewport
        // isn't a uGUI element - there's no PointerClick event to read a clickCount off of, so
        // this measures the same thing by hand against consecutive wasPressedThisFrame presses.
        private float _lastLeftClickTime = -1f;
        private Vector2 _lastLeftClickScreenPos;
        private const float DoubleClickMaxInterval = 0.35f;
        private const float DoubleClickMaxPixelDist = 12f;

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
            if (_undoToastTimer > 0f) _undoToastTimer = Mathf.Max(0f, _undoToastTimer - Time.deltaTime);
            if (_strokeEndFadeTimer > 0f) _strokeEndFadeTimer = Mathf.Max(0f, _strokeEndFadeTimer - Time.deltaTime);

            SyncSelectionTarget();
            HandleBrushSwitchKeys();
            HandleBrushResizeKey();
            HandleBrushStrengthKey();
            HandleUndoRedoKeys();
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

            EndMoveDrag();
            _isHovering = false;
            _lastDamHoverLocal = null;
            _lastClayStrokeLocal = null;
            _lastClayStrokeNormalLocal = null;
            _lastStrokeHitPointWorld = null;
            _strokeSpeed = 0f;
        }

        // Commits whatever BeginStrokeUndo/RecordUndoBeforeIfNeeded accumulated during a stroke
        // - fires uniformly across every brush type (including Move, whose own drag-end
        // detection in HandleMoveDrag coincides with this same release frame). Deliberately its
        // OWN top-level Update() step, not nested inside HandleSculptInput: that method returns
        // early while resizing the brush (holding S) or in mask-paint mode, and a mouse release
        // landing on exactly one of those frames would otherwise never reach the commit at all -
        // silently dropping that stroke's undo entry the next time BeginStrokeUndo clears the
        // accumulator for a new stroke. EndStrokeUndo is a no-op if nothing was accumulated (and
        // idempotent if called more than once before the next BeginStrokeUndo - see its
        // remarks), so calling it unconditionally on every release is always safe. A brush
        // hotkey pressed mid-hold (mouse still down) never hits this until the mouse actually
        // releases, so switching brushes mid-stroke just lumps every brush's touched vertices
        // into one accumulated delta/one undo step - an intentional, acceptable simplification
        // (today's behavior already has no notion of a hard stroke boundary at a brush switch
        // either).
        private void HandleStrokeEndCommit()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || sculptableMesh == null) return;
            if (mouse.leftButton.wasReleasedThisFrame || mouse.rightButton.wasReleasedThisFrame)
            {
                sculptableMesh.EndStrokeUndo();
                _strokeEndFadeTimer = StrokeEndFadeDuration;
            }
        }

        // Bare Z (not Ctrl+Z) is deliberate: this app runs inside the Unity Editor during
        // development, where Ctrl+Z is already bound to the EDITOR's own global Undo shortcut
        // and can fire instead of (or alongside) this one regardless of which window has
        // focus. A bare key isn't bound to anything Editor-level, so it reaches Keyboard.current
        // reliably - the same reasoning the existing S (resize) and M (remesh) shortcuts
        // already rely on.
        private void HandleUndoRedoKeys()
        {
            var kb = Keyboard.current;
            if (kb == null || _isResizingBrush || _isAdjustingStrength) return;
            if (!kb.zKey.wasPressedThisFrame) return;

            bool redo = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            // The ZSphere tool keeps its own history for the rig (a scaffold, not a scene object -
            // see ZSphereController's rig-undo remarks), and takes the key while it is the active
            // tool and has something left to step through. Asking IT rather than duplicating the
            // condition here is what guarantees exactly one of the two answers a given press, and
            // that Z falls back to scene history the moment the rig's own runs out.
            if (_zsphereForUndo == null) _zsphereForUndo = FindFirstObjectByType<ZSphereController>();
            if (_zsphereForUndo != null && _zsphereForUndo.HandlesUndoKey(redo)) return;

            if (redo) Redo(); else Undo();
        }

        // Only ever looked up on a frame Z is actually pressed, so the find costs nothing in a
        // scene that has no ZSphereController at all.
        private ZSphereController _zsphereForUndo;

        private void HandleBrushSwitchKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            HandleShiftSmoothOverride(kb);

            // Suppressed while the Shift-to-Smooth override is active (see
            // HandleShiftSmoothOverride) - switching brushes mid-hold would fight with what
            // Shift is about to restore on release, same reasoning ZBrush/Blender's own
            // hold-to-smooth doesn't let other brush hotkeys interrupt it either.
            if (_isShiftSmoothActive) return;

            // Also suppressed while the strength gauge (F) is up - CurrentBrush's setter swaps
            // brushStrength out from under _strengthAdjustStartValue's mid-drag baseline
            // (BrushRadius has no such per-brush swap, which is why HandleBrushResizeKey's S
            // doesn't need this same guard), so switching brushes mid-drag would let the next
            // mouse-move frame stomp the NEWLY-switched-to brush's stored strength with a value
            // computed from the OLD brush's baseline.
            if (_isAdjustingStrength) return;

            if (kb.digit1Key.wasPressedThisFrame) CurrentBrush = BrushType.Move;
            else if (kb.digit2Key.wasPressedThisFrame) CurrentBrush = BrushType.Clay;
            else if (kb.digit3Key.wasPressedThisFrame) CurrentBrush = BrushType.Smooth;
            else if (kb.digit4Key.wasPressedThisFrame) CurrentBrush = BrushType.Crease;
            else if (kb.digit5Key.wasPressedThisFrame) CurrentBrush = BrushType.DamStandard;
            else if (kb.digit6Key.wasPressedThisFrame) CurrentBrush = BrushType.Inflate;
            else if (kb.digit7Key.wasPressedThisFrame) CurrentBrush = BrushType.Flatten;

            // M used to trigger Remesh directly; moved to R (still reachable via the Remesh
            // button in the Brush panel either way) so M is free for the mask-paint toggle,
            // matching most sculpting apps' M-for-mask convention.
            if (kb.mKey.wasPressedThisFrame) IsMaskPaintMode = !IsMaskPaintMode;
            if (kb.rKey.wasPressedThisFrame) Remesh();
        }

        // Holding Shift temporarily switches to the Smooth brush, ZBrush/Blender-style,
        // reverting to whatever brush was active the moment Shift is released - lets you
        // smooth out a stroke without breaking flow to switch brushes and back. Guarded off
        // during the resize gauge for the same reason other input handlers are.
        private void HandleShiftSmoothOverride(Keyboard kb)
        {
            if (_isResizingBrush || _isAdjustingStrength) return;
            bool shiftHeld = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            if (shiftHeld && !_isShiftSmoothActive)
            {
                _preShiftBrush = currentBrush;
                _isShiftSmoothActive = true;
                CurrentBrush = BrushType.Smooth;
            }
            else if (!shiftHeld && _isShiftSmoothActive)
            {
                _isShiftSmoothActive = false;
                CurrentBrush = _preShiftBrush;
            }
        }

        // Holding S enters a resize mode (instead of sculpting) where horizontal mouse
        // movement scrubs BrushRadius live, ZBrush/Blender-style - the ring cursor itself
        // (UpdateBrushCursor/SculptUIBuilder) grows and shrinks with it, so there's no
        // separate popup readout to keep in sync.
        private void HandleBrushResizeKey()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (kb.sKey.wasPressedThisFrame)
            {
                EndMoveDrag(); // don't leave a grab mid-drag while resizing
                _isResizingBrush = true;
                _resizeStartRadius = brushRadius;
                _resizeStartMouseX = mouse.position.ReadValue().x;
                _resizeAnchorScreenPos = mouse.position.ReadValue();
            }
            else if (_isResizingBrush && !kb.sKey.isPressed)
            {
                _isResizingBrush = false;
            }

            if (!_isResizingBrush) return;

            float deltaX = mouse.position.ReadValue().x - _resizeStartMouseX;
            BrushRadius = _resizeStartRadius + deltaX * ResizeSensitivity;
        }

        // Holding F enters a strength-adjust mode (instead of sculpting) where horizontal
        // mouse movement scrubs BrushStrength live - same S-drag UX as HandleBrushResizeKey
        // above, but for the CURRENT brush's own strength (see _brushStrengthPerType) rather
        // than the shared BrushRadius. UpdateBrushCursor/SculptUIBuilder show a red inner
        // circle inside the ring cursor for the duration, scaled to the live strength value -
        // see IsAdjustingStrength.
        private void HandleBrushStrengthKey()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (kb.fKey.wasPressedThisFrame)
            {
                EndMoveDrag(); // don't leave a grab mid-drag while adjusting strength
                _isAdjustingStrength = true;
                _strengthAdjustStartValue = brushStrength;
                _strengthAdjustStartMouseX = mouse.position.ReadValue().x;
                _strengthAdjustAnchorScreenPos = mouse.position.ReadValue();
            }
            else if (_isAdjustingStrength && !kb.fKey.isPressed)
            {
                _isAdjustingStrength = false;
            }

            if (!_isAdjustingStrength) return;

            float deltaX = mouse.position.ReadValue().x - _strengthAdjustStartMouseX;
            BrushStrength = _strengthAdjustStartValue + deltaX * StrengthAdjustSensitivity;
        }

        // Shared by every brush handler's invert check below - Ctrl mirrors Blender's
        // hold-to-invert sculpt convention, alongside this app's pre-existing right-mouse-
        // inverts convention (kept for parity with users already used to that scheme).
        private static bool CtrlHeld => Keyboard.current != null &&
            (Keyboard.current.leftCtrlKey.isPressed || Keyboard.current.rightCtrlKey.isPressed);

        // Lets CameraOrbitController skip its own scroll-zoom while the cursor is over the
        // sculptable surface, so the same wheel resizes the active brush there instead (see
        // HandleBrushSizeScroll) and zooms the camera everywhere else.
        public static bool IsHoveringSculptSurface { get; private set; }

        // Scroll-to-resize: adjusts BrushRadius by a percentage per notch, same feel as
        // CameraOrbitController's own scroll-zoom (see its zoomPercentPerNotch remarks), so
        // brush size can be tuned without reaching for the S-drag resize gauge. Runs after
        // HandleSculptInput so _isHovering/_isOverUI already reflect this frame's raycast.
        private const float ScrollResizePercentPerNotch = 0.1f;

        private void HandleBrushSizeScroll()
        {
            var mouse = Mouse.current;
            IsHoveringSculptSurface = mouse != null && _isHovering && !_isOverUI && !_isResizingBrush && !_isAdjustingStrength;
            if (!IsHoveringSculptSurface) return;

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) < 0.01f) return;

            BrushRadius = brushRadius * (1f + Mathf.Sign(scroll) * ScrollResizePercentPerNotch);
        }

        /// Double-clicking anywhere in the viewport (that isn't UI, and isn't the Alt-orbit
        /// gesture) selects whichever registered SculptableMesh is under the cursor, making it
        /// the new sculpt target - the same effect as clicking its row in the Scene Graph
        /// panel. Deliberately raycasts every registered object rather than just the current
        /// Target: a click that misses the current target already has no sculpting side effect
        /// (see the individual brush handlers below, which just return on a miss), so this only
        /// ever adds behavior on an otherwise-inert click. Double-clicking the ALREADY-primary
        /// object still leaves two ordinary brush dabs at the click point from ProcessInput's
        /// own handling of those two presses - accepted as a minor quirk rather than adding the
        /// extra latency of holding every click for a double-click window before sculpting.
        private void HandleObjectPickDoubleClick(Mouse mouse, bool overUI, bool altHeld)
        {
            if (overUI || altHeld || !mouse.leftButton.wasPressedThisFrame) return;

            Vector2 pos = mouse.position.ReadValue();
            bool isDoubleClick = Time.unscaledTime - _lastLeftClickTime <= DoubleClickMaxInterval &&
                                  Vector2.Distance(pos, _lastLeftClickScreenPos) <= DoubleClickMaxPixelDist;

            if (isDoubleClick)
            {
                PickObjectUnderCursor(pos);
                _lastLeftClickTime = -1f; // consume - a third quick click starts a fresh pair, not another pick
            }
            else
            {
                _lastLeftClickTime = Time.unscaledTime;
                _lastLeftClickScreenPos = pos;
            }
        }

        private void PickObjectUnderCursor(Vector2 screenPos)
        {
            if (Selection == null || cam == null) return;

            Ray ray = cam.ScreenPointToRay(screenPos);
            SculptableMesh closest = null;
            float closestDist = float.MaxValue;
            foreach (SculptableMesh obj in Selection.AllObjects)
            {
                if (obj == null || !obj.Visible) continue;
                if (obj.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out _))
                {
                    float dist = Vector3.Distance(ray.origin, hitPoint);
                    if (dist < closestDist) { closestDist = dist; closest = obj; }
                }
            }

            // Only flash on an actual switch - double-clicking the object that's already
            // primary shouldn't flash, since nothing about the sculpt target changed.
            if (closest != null && closest != Selection.PrimarySelection)
            {
                Selection.Select(closest, false);
                SelectionFlashEffect.Play(closest.gameObject);
            }
        }

        private void HandleSculptInput()
        {
            var mouse = Mouse.current;
            if (mouse == null || cam == null || sculptableMesh == null) return;

            // A non-Sculpt gizmo tool (Transpose/Scale) is active - it owns mouse input for
            // dragging the selected object's transform instead, see TransformGizmo/GizmoMode.
            if (Gizmo != null && Gizmo.Mode != GizmoMode.Sculpt)
            {
                _isHovering = false;
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

            // Rebuild the vertex spatial index once at the start of every stroke (not every
            // frame - rebuilding is itself O(vertex count), so doing it per-frame would defeat
            // the point) so Clay/Smooth/Crease/Dam Standard/Move's per-stroke vertex lookups
            // don't have to scan the whole mesh. Cell size tracks the current brush radius so
            // the grid stays well-matched to typical query size. Also begins the stroke's undo
            // delta accumulator here, once per stroke rather than per frame for the same reason -
            // a stroke that turns out to be a click on empty space (missing the mesh) never
            // records anything, so HandleStrokeEndCommit's EndStrokeUndo call just no-ops for it
            // at zero cost (see SculptableMesh.BeginStrokeUndo/EndStrokeUndo remarks) - not worth
            // the extra complexity of gating this from inside every individual brush handler.
            if (!overUI && !altHeld && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
            {
                sculptableMesh.RebuildSpatialIndex(Mathf.Max(brushRadius * 0.5f, 0.01f));
                sculptableMesh.BeginStrokeUndo();
                // Fresh stroke, fresh speed reading - without this, a new stroke's first frame
                // would measure "speed" against wherever the cursor last hit the mesh at the END
                // of a PREVIOUS, unrelated stroke (see UpdateStrokeSpeed's remarks).
                _lastStrokeHitPointWorld = null;
                _strokeSpeed = 0f;
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
                default:
                    HandleClayInput(mouse, overUI, altHeld);
                    break;
            }

        }

        // Returns the screen position a paint/sculpt stroke should raycast from this frame -
        // the raw cursor, or (while Lazy Mouse is on and a stroke is actively being drawn) a
        // point trailing behind it on a taut "rope" of lazyMouseRadius pixels (see that field's
        // remarks). Used by every brush's input handler and by mask painting; deliberately NOT
        // used by Move's grab-drag (documented as intentionally 1:1 with the cursor) or by the
        // resize/strength drag gauges (S/F - those want raw, instant tracking).
        //
        // Bypasses straight to the raw position whenever no paint button is held, so ordinary
        // hovering (and the brush-size preview it drives) is never laggy - only an actual
        // stroke engages the rope. The rope resets to the raw position on the first frame of
        // every new stroke, so a fresh click always starts exactly under the cursor rather than
        // inheriting wherever a previous, unrelated stroke left it.
        private Vector2 GetStrokeScreenPosition(Mouse mouse)
        {
            Vector2 raw = mouse.position.ReadValue();
            _lazyMouseRawScreenPos = raw;
            if (!lazyMouseEnabled) { _lazyMouseActive = false; return raw; }

            bool pressed = mouse.leftButton.isPressed || mouse.rightButton.isPressed;
            if (!pressed) { _lazyMouseActive = false; return raw; }

            if (!_lazyMouseActive)
            {
                _lazyMouseActive = true;
                _lazyMouseScreenPos = raw;
                return _lazyMouseScreenPos;
            }

            Vector2 delta = raw - _lazyMouseScreenPos;
            float dist = delta.magnitude;
            if (dist > lazyMouseRadius)
                _lazyMouseScreenPos += delta.normalized * ((dist - lazyMouseRadius) * lazyMouseStrength);

            return _lazyMouseScreenPos;
        }

        // Left mouse paints mask (protects the area from every brush - see
        // SculptableMesh.Mask/PaintMask), right mouse erases it, same LMB-apply/RMB-invert
        // convention as the sculpting brushes. Deliberately NOT part of undo history - masking
        // doesn't move geometry, and folding it into SculptHistory's vertex/triangle snapshot
        // format would be a larger change than this "just a basic one" ask called for; flagged
        // here rather than silently left out.
        // The one diagnostic every brush handler emits, on the press frame only. Was six
        // byte-identical copies inline; kept as a method rather than folded into the
        // handlers so the `logRayHits` gate and the message stay in one place.
        private void LogRayHit(Mouse mouse, Ray ray, Vector3 hitPoint, Vector3 hitNormal)
        {
            if (!logRayHits) return;
            if (!mouse.leftButton.wasPressedThisFrame && !mouse.rightButton.wasPressedThisFrame) return;
            Debug.Log($"[Sculpt] Ray hit at {hitPoint}, normal {hitNormal}, "
                      + $"distance {Vector3.Distance(ray.origin, hitPoint):F2}");
        }

        private void HandleMaskPaintInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            // Mask painting queries the vertex spatial index (SculptableMesh.PaintMask ->
            // QueryNear) just like the sculpting brushes do, so it needs the same start-of-
            // stroke rebuild they get in HandleSculptInput - which it never reached, since
            // mask mode returns before that block. Left over from whatever the last sculpt
            // stroke built, the index was sized for THAT stroke's brush radius and bucketed
            // against pre-stroke positions, so vertices the stroke had since moved dropped out
            // of the footprint entirely and painted a mask full of holes - invisible until
            // Invert Mask turned those holes into islands of protected surface, which is the
            // "weird effects after inverting" this fixes. (ApplyVerticesLocal now also keeps
            // the index current as geometry moves - see VertexSpatialGrid.UpdateVertices - so
            // this rebuild is really about matching cell size to the mask brush's own radius.)
            if (!altHeld && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
            {
                sculptableMesh.RebuildSpatialIndex(Mathf.Max(brushRadius * 0.5f, 0.01f));
                // Opens the mask stroke's undo accumulator. Mask mode returns from
                // HandleSculptInput before its BeginStrokeUndo block, so this is the only place
                // that can do it; the matching commit needs no new call site, since
                // HandleStrokeEndCommit already fires EndStrokeUndo on every mouse release
                // regardless of which mode is active.
                sculptableMesh.BeginMaskStroke();
            }

            Ray ray = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            // Ctrl inverts while held, exactly as it does for every sculpting brush (see
            // CtrlHeld) - mask mode used to ignore it, so the Blender/ZBrush reflex of
            // Ctrl-dragging to erase mask silently painted MORE mask instead.
            bool rightHeld = mouse.rightButton.isPressed;
            bool erasing = rightHeld || CtrlHeld;
            _previewPositive = !erasing; // green while painting, red while erasing

            if (altHeld) return;
            if (mouse.leftButton.isPressed) ApplyMaskPaint(hitPoint, !erasing);
            else if (rightHeld) ApplyMaskPaint(hitPoint, false);
        }

        private void ApplyMaskPaint(Vector3 worldPoint, bool applying)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            float speed = Mathf.Lerp(MaskPaintSpeedSoft, MaskPaintSpeedHard, maskHardness);
            float amount = (applying ? 1f : -1f) * EffectiveBrushStrength * speed * Time.deltaTime;

            foreach (Vector3 sign in Mirror.GetMirrorSigns())
                sculptableMesh.PaintMask(Vector3.Scale(localPoint, sign), brushRadius, amount, maskHardness);
        }

        private void HandleClayInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) { _lastClayStrokeLocal = null; return; }

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;
            UpdateStrokeSpeed(hitPoint);

            bool rightHeld = mouse.rightButton.isPressed;
            bool invertHeld = rightHeld || CtrlHeld;
            _previewPositive = invertHeld ? !isPositive : isPositive;

            LogRayHit(mouse, ray, hitPoint, hitNormal);

            // Alt+Left-drag is reserved for orbiting the camera (see CameraOrbitController),
            // so don't also sculpt while Alt is held. Right-drag, or holding Ctrl while
            // left-dragging, sculpts with the sign inverted (Ctrl mirrors Blender's
            // invert-while-held sculpt convention).
            bool sculptingLeft = mouse.leftButton.isPressed && !altHeld;
            if (!sculptingLeft && !rightHeld) { _lastClayStrokeLocal = null; return; }

            ApplyClayStroke(hitPoint, hitNormal, sculptingLeft ? (invertHeld ? !isPositive : isPositive) : !isPositive);
        }

        // Paces a held Clay stroke by DISTANCE TRAVELLED rather than by elapsed time: a dab of
        // fixed size and fixed material is laid down every `spacing` of cursor travel, and a
        // cursor that isn't moving lays down nothing at all.
        //
        // It used to split the frame's `Time.deltaTime` across however many sub-dabs the travel
        // needed, so the material deposited was a function of how long the cursor spent
        // somewhere rather than how far it moved. That put a full round stamp's worth of clay
        // wherever the cursor paused - which is precisely the press at the start of a stroke,
        // every turnaround of a back-and-forth scrub, and the moment before release. The user's
        // own description: "it builds up more at the beginning and ending of my stroke, you get
        // a pool of the alpha I'm using... vs Nomad where it softens near the end of the stroke
        // and has more mass in the centre, like a very long arch."
        //
        // Distance pacing produces that arch for free, and it's worth being precise about why:
        // the deposited height along the path is the convolution of the dab's falloff with the
        // dab density. Uniform density over the stroke's length integrates to a full-height
        // plateau in the middle, falling to HALF height at the exact endpoints and tapering over
        // about one brush radius on either side. That taper IS the soft end Nomad shows; the old
        // scheme buried it under the extra dabs the pause deposited. It also makes a stroke
        // look the same whether it was drawn quickly or slowly, and immune to frame-rate jitter -
        // dt no longer enters Clay's deposit at all. Every dab already flattens its own
        // footprint onto a freshly area-averaged plane (see ApplyClayBrushLocal's remarks) -
        // without spacing, a fast drag (or a frame-rate dip under a heavy stroke) leaves visible
        // gaps between consecutive dabs' plateaus, which read as a washboard of separate raised
        // terraces rather than one continuous ridge - exactly the "blobby"/lumpy look the Nomad
        // Sculpt comparison this was built to close showed, since Nomad (and every other
        // sculpting app) resamples along the stroke path for the same reason. Interpolates the
        // mesh-local hit point/normal directly rather than re-raycasting per sub-step (a real
        // re-raycast per dab would track surface curvature more precisely, but a straight lerp
        // is a good approximation at the sub-brush-radius travel distances this only kicks in
        // for, and avoids doubling the raycast cost of every held frame). dt is split evenly
        // across sub-steps so a stroke's total build-up over one real frame stays correct
        // regardless of how many dabs that frame took - a fast drag shouldn't deposit MORE clay
        // than a slow one just because it needed more dabs to stay gap-free.
        private const float ClayDabSpacingFraction = 0.2f;
        // Raised from 8 now that a dab is a fixed quantum of material rather than a slice of the
        // frame's time: the cap is purely a cost ceiling for a frame in which the cursor jumped a
        // long way, and hitting it now means dropping material (a visible gap) rather than just
        // spreading the same amount thinner. 24 covers ~4.8 brush radii of travel in one frame,
        // which no plausible drag exceeds.
        private const int ClayMaxDabsPerFrame = 24;

        // Travel that a single dab's worth of material corresponds to, expressed as the stroke
        // speed at which the new distance-driven pacing deposits exactly what the old
        // time-driven pacing did. Purely a calibration constant so existing Brush Strength /
        // Clay Depth settings keep feeling the same: at this speed the two schemes agree
        // exactly, below it the new one deposits less per second (but the same per centimetre),
        // above it more per second. Its counterpart on the carving brushes is StrokePacingGain,
        // which encodes the same "keep the existing settings feeling the same" calibration for
        // their own switch to distance pacing.
        private const float ClayReferenceStrokeSpeed = 1f;

        // Distance travelled since the last dab was placed, carried ACROSS frames. Without it a
        // slow drag - one that covers less than a dab spacing per frame - would round down to
        // zero dabs every frame and deposit nothing at all.
        private float _clayDabCarry;

        // The square tip's own axes (see ClayTipShapeT01) - frozen for the WHOLE stroke rather
        // than rebuilt per dab from that dab's own (interpolated) normal. Early testing showed
        // rebuilding per dab lets the square's orientation drift/flip dab to dab on a curved
        // surface (BuildTangentBasis's reference-axis switch is a discontinuity - a stroke that
        // crosses it swings the square ~90 degrees between consecutive dabs), which reads as
        // overlapping misaligned "ghost" square prints rather than one clean stamp. Only
        // matters when clayTipRoundness < 1 (the round shape is rotation-invariant), but always
        // kept in sync with the stroke so there's no stale-orientation edge case.
        private Vector3 _clayStrokeTangent0;
        private Vector3 _clayStrokeBitangent0;

        private void ApplyClayStroke(Vector3 worldPoint, Vector3 worldNormal, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            // Not InverseTransformDirection: that is rotation-only and mis-tilts the normal
            // on a non-uniformly scaled object - see SculptableMesh.WorldToLocalNormal.
            Vector3 localNormal = sculptableMesh.WorldToLocalNormal(worldNormal);
            float dt = Time.deltaTime;

            float spacing = Mathf.Max(brushRadius * ClayDabSpacingFraction, 0.0005f);
            // One dab = one fixed quantum of material, NOT a slice of this frame's time.
            float dabDt = spacing / ClayReferenceStrokeSpeed;

            if (_lastClayStrokeLocal.HasValue)
            {
                Vector3 from = _lastClayStrokeLocal.Value;
                Vector3 fromNormal = _lastClayStrokeNormalLocal.Value;
                float dist = Vector3.Distance(from, localPoint);

                // Place a dab every `spacing` of TRAVEL, continuing from wherever the previous
                // frame's leftover distance left off. A stationary cursor travels nothing and so
                // deposits nothing - which is the whole point of this rewrite.
                _clayDabCarry += dist;
                int placed = 0;
                while (_clayDabCarry >= spacing && placed < ClayMaxDabsPerFrame)
                {
                    _clayDabCarry -= spacing;
                    // Where along THIS frame's segment the dab falls. dist can be ~0 while carry
                    // still crosses the threshold (a dab banked by earlier frames finally firing),
                    // in which case the dab belongs at the current point.
                    float u = dist > 1e-9f ? Mathf.Clamp01((dist - _clayDabCarry) / dist) : 1f;
                    Vector3 stepPoint = Vector3.Lerp(from, localPoint, u);
                    Vector3 stepNormal = Vector3.Slerp(fromNormal, localNormal, u).normalized;
                    ApplyClayBrushAtLocal(stepPoint, stepNormal, positive, dabDt);
                    placed++;
                }

                // Hit the per-frame ceiling: drop the unspent travel instead of banking it into
                // a burst of dabs next frame, which would pile material exactly where the stroke
                // was already struggling to keep up.
                if (placed >= ClayMaxDabsPerFrame) _clayDabCarry = 0f;
            }
            else
            {
                // Fresh stroke - (re)lock the square tip's orientation to this first dab's
                // normal for the rest of the stroke, and lay one dab down so a tap still marks
                // the surface (the distance-driven path above never fires for a click that
                // doesn't move).
                BuildTangentBasis(localNormal, out _clayStrokeTangent0, out _clayStrokeBitangent0);
                _clayDabCarry = 0f;
                ApplyClayBrushAtLocal(localPoint, localNormal, positive, dabDt);
            }

            _lastClayStrokeLocal = localPoint;
            _lastClayStrokeNormalLocal = localNormal;
        }

        private void ApplyClayBrushAtLocal(Vector3 localPoint, Vector3 localNormal, bool positive, float dt)
        {
            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                Vector3 mirroredPoint = Vector3.Scale(localPoint, sign);
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                // Mirror the frozen stroke tangent frame the same way the point/normal are
                // mirrored, instead of rebuilding it from the mirrored normal - keeps a
                // mirrored stroke's square exactly as stable as the primary one.
                Vector3 mirroredTangent0 = Vector3.Scale(_clayStrokeTangent0, sign);
                Vector3 mirroredBitangent0 = Vector3.Scale(_clayStrokeBitangent0, sign);
                ApplyClayBrushLocal(mirroredPoint, mirroredNormal, mirroredTangent0, mirroredBitangent0, positive, dt);
            }

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        // Eases each vertex toward a point on the brush's tangent PLANE rather than toward
        // localPoint itself - so the whole footprint rises to a level plateau together, ZBrush
        // ClayBuildup/Blender Clay Strips style, instead of every vertex sagging toward one
        // shared target point. The plane's origin and normal are an area-weighted average of
        // the footprint's OWN current vertex positions/normals (same falloff weights used to
        // apply the brush), not the single raw raycast hit point/normal - a lone raycast hits
        // one triangle's flat face normal, which can differ noticeably from its neighbors on a
        // tessellated/previously-sculpted surface, so a plane built from it alone jitters
        // slightly frame to frame as the stroke crosses different triangles, stacking into a
        // lumpy, stair-stepped buildup instead of a coherent flat plateau. Averaging over the
        // footprint the brush is about to touch makes the plane immune to any single
        // triangle's noise - the same "area plane" approach ZBrush/Blender's own
        // Clay/Flatten-family brushes use. An optional alpha stamp (see BrushAlphaLibrary)
        // multiplies the same per-vertex weight to vary the plateau's surface detail.
        // A square tip's corners reach out to brushRadius*sqrt(2) from the center - widen the
        // candidate query so those corners have vertices to pull from at all, instead of being
        // silently clipped back to the inscribed circle by a query that only ever fetched
        // brushRadius's worth of vertices. No-op (query radius == brushRadius exactly) at the
        // default clayTipRoundness=1, so this changes nothing for the plain round tip.
        private const float Sqrt2 = 1.4142136f;

        private void ApplyClayBrushLocal(Vector3 localPoint, Vector3 localNormal, Vector3 tangent0, Vector3 bitangent0, bool positive, float dt)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            Vector3[] normals = sculptableMesh.Normals;

            float queryRadius = clayTipRoundness < 1f ? brushRadius * Sqrt2 : brushRadius;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, queryRadius);
            if (candidates.Count == 0) return;

            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyClayBrushLocalJob(localPoint, localNormal, tangent0, bitangent0, positive, dt, candidates, verts, normals);
            else
                ApplyClayBrushLocalManaged(localPoint, localNormal, tangent0, bitangent0, positive, dt, candidates, verts, normals);
        }

        private void ApplyClayBrushLocalJob(Vector3 localPoint, Vector3 localNormal, Vector3 tangent0, Vector3 bitangent0, bool positive, float dt, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
            float sign = positive ? 1f : -1f;
            float effectiveStrength = EffectiveBrushStrength;
            float effectiveStrengthAccumulate = EffectiveClayStrengthAccumulate;
            float height = brushRadius * clayHeightFactor * sign;

            GatherCandidatesNative(candidates, verts, normals, sculptableMesh.Mask);
            for (int ci = 0; ci < candidates.Count; ci++)
                _nativeClayStrokeStart[ci] = sculptableMesh.StrokeStartPosition(candidates[ci]);

            var weightJob = new ClayWeightJob
            {
                PositionsIn = _nativePositionsIn,
                NormalsIn = _nativeNormalsIn,
                MaskIn = _nativeMaskIn,
                WeightsOut = _nativeClayWeights,
                WeightedPosOut = _nativeClayWeightedPos,
                WeightedNormalOut = _nativeClayWeightedNormal,
                LocalPoint = localPoint,
                BrushRadius = brushRadius,
                Tangent0 = tangent0,
                Bitangent0 = bitangent0,
                TipRoundness = clayTipRoundness,
                EdgeSoftness = clayEdgeSoftness,
                FrontFacingOnly = frontFacingOnly,
                CameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position),
            };
            weightJob.Schedule(candidates.Count, 32).Complete();

            // Sequential reduction across the (footprint-bounded) candidate list - see
            // ClayWeightJob's remarks on why this stays on the main thread.
            Vector3 planeOriginSum = Vector3.zero, planeNormalSum = Vector3.zero;
            float planeWeightSum = 0f;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                planeOriginSum += _nativeClayWeightedPos[ci];
                planeNormalSum += _nativeClayWeightedNormal[ci];
                planeWeightSum += _nativeClayWeights[ci];
            }
            if (planeWeightSum <= 1e-6f) return;

            Vector3 planeOrigin = planeOriginSum / planeWeightSum;
            Vector3 planeNormal = planeNormalSum.sqrMagnitude > 1e-8f ? planeNormalSum.normalized : localNormal;

            BuildTangentBasis(planeNormal, out Vector3 tangent, out Vector3 bitangent);
            float rot = alphaRotation * Mathf.Deg2Rad;

            if (useAlpha) EnsureAlphaNative();

            var dispJob = new ClayDisplacementJob
            {
                PositionsIn = _nativePositionsIn,
                StrokeStartIn = _nativeClayStrokeStart,
                WeightsIn = _nativeClayWeights,
                AlphaSamples = useAlpha ? _nativeAlphaSamples : _nativeClayWeights, // unread when !UseAlpha; just needs to be a valid array
                PositionsOut = _nativePositionsOut,
                AppliedOut = _nativeAppliedOut,
                LocalPoint = localPoint,
                PlaneOrigin = planeOrigin,
                PlaneNormal = planeNormal,
                Tangent = tangent,
                Bitangent = bitangent,
                Height = height,
                LerpFactorScale = effectiveStrength * ClaySpeed * dt,
                UseAlpha = useAlpha,
                InvertAlpha = invertAlpha,
                CosR = Mathf.Cos(rot),
                SinR = Mathf.Sin(rot),
                InvStampRadius = 1f / Mathf.Max(0.0001f, brushRadius * alphaScale),
                AlphaSize = useAlpha ? _nativeAlphaSize : 0,
                Accumulate = accumulate,
                Rate = sign * clayHeightFactor * effectiveStrengthAccumulate * ClaySpeed * dt,
                MaxAlong = height * (accumulate ? ClayStrokeDepthLimitAccumulate : ClayStrokeDepthLimit),
            };
            dispJob.Schedule(candidates.Count, 32).Complete();

            ScatterJobResults(candidates, verts);
        }

        private void ApplyClayBrushLocalManaged(Vector3 localPoint, Vector3 localNormal, Vector3 tangent0, Vector3 bitangent0, bool positive, float dt, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
            float sign = positive ? 1f : -1f;
            float effectiveStrength = EffectiveBrushStrength;
            float effectiveStrengthAccumulate = EffectiveClayStrengthAccumulate;
            float height = brushRadius * clayHeightFactor * sign;

            if (_clayWeightScratch.Length < candidates.Count) _clayWeightScratch = new float[candidates.Count];
            float[] weights = _clayWeightScratch;

            Vector3 planeOriginSum = Vector3.zero;
            Vector3 planeNormalSum = Vector3.zero;
            float planeWeightSum = 0f;
            Vector3 cameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position);

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 toVert = verts[i] - localPoint;
                float t01 = ClayTipShapeT01(toVert, brushRadius, tangent0, bitangent0, clayTipRoundness);
                if (t01 <= 0f) { weights[ci] = 0f; continue; }

                float w = ClayFalloff(t01, clayEdgeSoftness) * (1f - sculptableMesh.Mask[i]) // flat plateau, edge-only taper - see clayEdgeSoftness
                    * FrontFacingWeight(frontFacingOnly, normals[i], verts[i], cameraLocalPos);
                weights[ci] = w;

                planeOriginSum += verts[i] * w;
                planeNormalSum += normals[i] * w;
                planeWeightSum += w;
            }

            if (planeWeightSum <= 1e-6f) return;

            Vector3 planeOrigin = planeOriginSum / planeWeightSum;
            Vector3 planeNormal = planeNormalSum.sqrMagnitude > 1e-8f
                ? planeNormalSum.normalized : localNormal;

            BuildTangentBasis(planeNormal, out Vector3 tangent, out Vector3 bitangent);
            float rot = alphaRotation * Mathf.Deg2Rad;
            float cosR = Mathf.Cos(rot), sinR = Mathf.Sin(rot);
            BrushAlphaLibrary.AlphaData alpha = useAlpha ? BrushAlphaLibrary.Get(alphaType) : default;
            float invStampRadius = 1f / Mathf.Max(0.0001f, brushRadius * alphaScale);

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                float weight = weights[ci];
                if (weight <= 0f) continue;
                int i = candidates[ci];

                if (useAlpha)
                {
                    Vector3 toVert = verts[i] - localPoint;
                    float u = Vector3.Dot(toVert, tangent) * invStampRadius;
                    float v = Vector3.Dot(toVert, bitangent) * invStampRadius;
                    float ru = u * cosR - v * sinR;
                    float rv = u * sinR + v * cosR;
                    if (ru < -1f || ru > 1f || rv < -1f || rv > 1f)
                    {
                        continue;
                    }

                    float a = BrushAlphaLibrary.Sample(alpha, ru * 0.5f + 0.5f, rv * 0.5f + 0.5f);
                    weight *= invertAlpha ? 1f - a : a;
                    if (weight <= 0f) continue;
                }

                sculptableMesh.RecordUndoBeforeIfNeeded(i);

                if (accumulate)
                {
                    // Two blended terms instead of a plain push: (1) a continuous, unbounded
                    // rate along the plane normal - same as before, this is what makes
                    // Accumulate keep climbing the longer the stroke is held instead of
                    // plateauing at `height`; (2) a flatten term identical in shape to the
                    // OFF-mode target below, easing each vertex toward the plane's own current
                    // height. Term (2) alone is self-limiting (it converges and stops, same as
                    // OFF mode) - but planeOrigin/planeNormal are recomputed fresh every frame
                    // from vertices term (1) just raised, so the flatten target keeps climbing
                    // right along with the buildup. The combination fills dips and settles
                    // bumps toward one shared level AS mass builds, instead of uniformly
                    // ballooning every vertex (dips and bumps alike) by the same amount and
                    // preserving whatever unevenness was already there underneath the stroke.
                    Vector3 buildDelta = planeNormal * (sign * clayHeightFactor * effectiveStrengthAccumulate * ClaySpeed * dt) * weight;

                    Vector3 toPlaneAcc = verts[i] - planeOrigin;
                    float alongNormalAcc = Vector3.Dot(toPlaneAcc, planeNormal);
                    Vector3 tangentialOffsetAcc = toPlaneAcc - planeNormal * alongNormalAcc;
                    // height * weight for the same reason as the OFF target below - the flatten
                    // term is deliberately identical in shape to it.
                    Vector3 flattenTarget = planeOrigin + tangentialOffsetAcc + planeNormal * (height * weight);
                    Vector3 flattenDelta = (flattenTarget - verts[i]) * Mathf.Clamp01(weight * effectiveStrength * ClaySpeed * dt);

                    verts[i] = ClampStrokeDepth(verts[i] + buildDelta + flattenDelta,
                        sculptableMesh.StrokeStartPosition(i), planeNormal,
                        height * ClayStrokeDepthLimitAccumulate, height);
                }
                else
                {
                    Vector3 toPlane = verts[i] - planeOrigin;
                    float alongNormal = Vector3.Dot(toPlane, planeNormal);
                    Vector3 tangentialOffset = toPlane - planeNormal * alongNormal;
                    // height * weight - see ClayDisplacementJob's matching line for why the
                    // target follows the brush profile instead of being one shared flat height.
                    Vector3 target = planeOrigin + tangentialOffset + planeNormal * (height * weight);

                    Vector3 toTarget = target - verts[i];
                    // Clamp01: this is a lerp fraction toward target, not a velocity - on a
                    // frame hitch (large dt, e.g. during a heavy Remesh) an unclamped factor can
                    // exceed 1 and overshoot past the target plane. Since Clay's target
                    // recomputes from the vertex's own (now overshot) position next frame, an
                    // uncapped factor compounds into a runaway explosion rather than settling -
                    // reproduced this empirically while testing this brush (a synthetic
                    // large-dt stroke sent a vertex from radius 0.5 to over 3.0 in 90 frames
                    // before this clamp existed).
                    verts[i] = ClampStrokeDepth(verts[i] + toTarget * Mathf.Clamp01(weight * effectiveStrength * ClaySpeed * dt),
                        sculptableMesh.StrokeStartPosition(i), planeNormal,
                        height * ClayStrokeDepthLimit, height);
                }

                _dirtyVertexScratch.Add(i);
            }
        }

        /// Caps how far THIS stroke has displaced one vertex, measured from where the stroke
        /// found it rather than from any absolute height - so it bounds only the growth this
        /// stroke is itself responsible for, and a stroke crossing a ridge an earlier stroke
        /// built neither chisels it nor is blocked by it. Only the normal component is capped;
        /// tangential motion is left alone. Shared by the managed path and (as an inlined copy)
        /// ClayDisplacementJob - see ClayStrokeDepthLimit for the whole rationale, including why
        /// maxAlong must NOT be scaled by the dab's falloff weight.
        private static Vector3 ClampStrokeDepth(Vector3 position, Vector3 strokeStart,
                                                Vector3 planeNormal, float maxAlong, float height)
        {
            float along = Vector3.Dot(position - strokeStart, planeNormal);
            bool within = height >= 0f ? along <= maxAlong : along >= maxAlong;
            return within ? position : position - planeNormal * (along - maxAlong);
        }


        private static void BuildTangentBasis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent)
        {
            Vector3 up = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            tangent = Vector3.Cross(up, normal).normalized;
            bitangent = Vector3.Cross(normal, tangent);
        }

        private void HandleCreaseInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;
            UpdateStrokeSpeed(hitPoint);

            bool rightHeld = mouse.rightButton.isPressed;
            bool invertHeld = rightHeld || CtrlHeld;
            _previewPositive = invertHeld ? !isPositive : isPositive;

            LogRayHit(mouse, ray, hitPoint, hitNormal);

            if (mouse.leftButton.isPressed && !altHeld)
                ApplyCreaseBrush(hitPoint, hitNormal, invertHeld ? !isPositive : isPositive);
            else if (rightHeld)
                ApplyCreaseBrush(hitPoint, hitNormal, !isPositive);
        }

        private void ApplyCreaseBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            // Not InverseTransformDirection: that is rotation-only and mis-tilts the normal
            // on a non-uniformly scaled object - see SculptableMesh.WorldToLocalNormal.
            Vector3 localNormal = sculptableMesh.WorldToLocalNormal(worldNormal);

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                Vector3 mirroredPoint = Vector3.Scale(localPoint, sign);
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                ApplyCreaseBrushLocal(mirroredPoint, mirroredNormal, positive);
            }

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        // Pinches the tangential footprint toward the stroke centerline while carving along
        // the normal with a depth that's scaled by the same per-vertex weight (unlike Clay's
        // constant plateau height), so the profile tapers to a sharp ridge/valley along the
        // stroke instead of a flat-topped dome.
        private void ApplyCreaseBrushLocal(Vector3 localPoint, Vector3 localNormal, bool positive)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyCreaseBrushLocalJob(localPoint, localNormal, positive, Vector3.zero, 0f, candidates, verts);
            else
                ApplyCreaseBrushLocalManaged(localPoint, localNormal, positive, candidates, verts);
        }

        // Shared by Crease (dirLocal=zero, lip=0 - see CreaseJob remarks) and DamStandard.
        private void ApplyCreaseBrushLocalJob(Vector3 localPoint, Vector3 localNormal, bool positive,
            Vector3 dirLocal, float lipFactor, List<int> candidates, Vector3[] verts)
        {
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float effectiveStrength = EffectiveBrushStrengthPlateau;
            float effectiveStrengthAccumulate = EffectiveBrushStrengthAccumulate;

            GatherCandidatesNative(candidates, verts, sculptableMesh.Normals, sculptableMesh.Mask);
            var job = new CreaseJob
            {
                PositionsIn = _nativePositionsIn,
                NormalsIn = _nativeNormalsIn,
                MaskIn = _nativeMaskIn,
                PositionsOut = _nativePositionsOut,
                AppliedOut = _nativeAppliedOut,
                LocalPoint = localPoint,
                LocalNormal = localNormal,
                DirLocal = dirLocal,
                BrushRadius = brushRadius,
                Depth = brushRadius * creaseDepthFactor * sign,
                Lip = brushRadius * lipFactor * sign,
                Pinch = creasePinch,
                LerpFactorScale = effectiveStrength * CreaseSpeed * dt,
                Accumulate = accumulate,
                DepthRate = sign * creaseDepthFactor * effectiveStrengthAccumulate * CreaseSpeed * dt,
                LipRate = sign * lipFactor * effectiveStrengthAccumulate * CreaseSpeed * dt,
                PinchRateScale = creasePinch * effectiveStrengthAccumulate * CreaseSpeed * dt,
                FrontFacingOnly = frontFacingOnly,
                CameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position),
            };
            job.Schedule(candidates.Count, 32).Complete();

            ScatterJobResults(candidates, verts);
        }

        private void ApplyCreaseBrushLocalManaged(Vector3 localPoint, Vector3 localNormal, bool positive, List<int> candidates, Vector3[] verts)
        {
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float effectiveStrength = EffectiveBrushStrengthPlateau;
            float effectiveStrengthAccumulate = EffectiveBrushStrengthAccumulate;
            float depth = brushRadius * creaseDepthFactor * sign;
            Vector3 cameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position);

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 toVert = verts[i] - localPoint;
                float dist = toVert.magnitude;
                if (dist > brushRadius) continue;

                float t01 = 1f - dist / brushRadius;
                float weight = t01 * t01 * t01 * (1f - sculptableMesh.Mask[i]) // sharper falloff than Clay's smoothstep - a narrower peak
                    * FrontFacingWeight(frontFacingOnly, sculptableMesh.Normals[i], verts[i], cameraLocalPos);

                float alongNormal = Vector3.Dot(toVert, localNormal);
                Vector3 tangentialOffset = toVert - localNormal * alongNormal;

                sculptableMesh.RecordUndoBeforeIfNeeded(i);

                if (accumulate)
                {
                    // Depth keeps digging deeper for as long as the brush is held - a
                    // continuous rate, not a target/plateau (same shape as Clay/Inflate's
                    // accumulate-on push).
                    verts[i] += localNormal * (sign * creaseDepthFactor * effectiveStrengthAccumulate * CreaseSpeed * dt) * weight;
                    // Pinch stays a bounded pull toward the stroke centerline - it's a shape
                    // control, not a depth amount, so letting it run away would just make the
                    // groove's cross-section overshoot past center and oscillate instead of
                    // digging deeper.
                    verts[i] += -tangentialOffset * Mathf.Clamp01(creasePinch * weight * effectiveStrengthAccumulate * CreaseSpeed * dt);
                }
                else
                {
                    Vector3 pinched = tangentialOffset * (1f - creasePinch * weight);
                    Vector3 target = localPoint + pinched + localNormal * (depth * weight);
                    Vector3 toTarget = target - verts[i];
                    verts[i] += toTarget * Mathf.Clamp01(weight * effectiveStrength * CreaseSpeed * dt); // see Clamp01 note on Clay
                }

                _dirtyVertexScratch.Add(i);
            }
        }

        private void HandleDamStandardInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) { _lastDamHoverLocal = null; return; }

            Ray ray = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) { _lastDamHoverLocal = null; return; }

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;
            UpdateStrokeSpeed(hitPoint);

            bool rightHeld = mouse.rightButton.isPressed;
            bool invertHeld = rightHeld || CtrlHeld;
            _previewPositive = invertHeld ? !isPositive : isPositive;

            LogRayHit(mouse, ray, hitPoint, hitNormal);

            bool sculpting = (mouse.leftButton.isPressed && !altHeld) || rightHeld;
            if (!sculpting) { _lastDamHoverLocal = null; return; }

            ApplyDamStandardBrush(hitPoint, hitNormal, invertHeld ? !isPositive : isPositive);
        }

        private void ApplyDamStandardBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            // Not InverseTransformDirection: that is rotation-only and mis-tilts the normal
            // on a non-uniformly scaled object - see SculptableMesh.WorldToLocalNormal.
            Vector3 localNormal = sculptableMesh.WorldToLocalNormal(worldNormal);

            // Stroke-travel direction in the tangent plane, used to bias a raised lip onto the
            // leading edge and leave a groove on the trailing edge - the asymmetry that
            // distinguishes Dam Standard from a plain symmetric Crease. No reliable direction
            // exists yet on the stroke's first sample or a stationary dab, so this falls back
            // to symmetric Crease-like carving in that case - an honest simplification rather
            // than full directional Dam Standard behavior.
            Vector3 dirLocal = Vector3.zero;
            if (_lastDamHoverLocal.HasValue)
            {
                Vector3 raw = localPoint - _lastDamHoverLocal.Value;
                Vector3 tangential = raw - localNormal * Vector3.Dot(raw, localNormal);
                if (tangential.sqrMagnitude > 1e-8f) dirLocal = tangential.normalized;
            }
            _lastDamHoverLocal = localPoint;

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                Vector3 mirroredPoint = Vector3.Scale(localPoint, sign);
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                Vector3 mirroredDir = Vector3.Scale(dirLocal, sign);
                ApplyDamStandardBrushLocal(mirroredPoint, mirroredNormal, mirroredDir, positive);
            }

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        private void ApplyDamStandardBrushLocal(Vector3 localPoint, Vector3 localNormal, Vector3 dirLocal, bool positive)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            // A zero dirLocal (no reliable stroke direction yet - see ApplyDamStandardBrush's
            // remarks) naturally disables the lip term in both paths: the managed loop's own
            // hasDir/dot-product gate, and CreaseJob's dot-product-with-zero-vector (always 0,
            // never > 0) - no separate "hasDir" needed there either.
            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyCreaseBrushLocalJob(localPoint, localNormal, positive, dirLocal, damLipHeight, candidates, verts);
            else
                ApplyDamStandardBrushLocalManaged(localPoint, localNormal, dirLocal, positive, candidates, verts);
        }

        private void ApplyDamStandardBrushLocalManaged(Vector3 localPoint, Vector3 localNormal, Vector3 dirLocal, bool positive, List<int> candidates, Vector3[] verts)
        {
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float effectiveStrength = EffectiveBrushStrengthPlateau;
            float effectiveStrengthAccumulate = EffectiveBrushStrengthAccumulate;
            float depth = brushRadius * creaseDepthFactor * sign;
            float lip = brushRadius * damLipHeight * sign;
            bool hasDir = dirLocal.sqrMagnitude > 1e-6f;
            Vector3 cameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position);

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 toVert = verts[i] - localPoint;
                float dist = toVert.magnitude;
                if (dist > brushRadius) continue;

                float t01 = 1f - dist / brushRadius;
                float weight = t01 * t01 * t01 * (1f - sculptableMesh.Mask[i])
                    * FrontFacingWeight(frontFacingOnly, sculptableMesh.Normals[i], verts[i], cameraLocalPos);

                float alongNormal = Vector3.Dot(toVert, localNormal);
                Vector3 tangentialOffset = toVert - localNormal * alongNormal;
                bool hasLip = hasDir && Vector3.Dot(tangentialOffset, dirLocal) > 0f;

                sculptableMesh.RecordUndoBeforeIfNeeded(i);

                if (accumulate)
                {
                    // Depth (and the leading-edge lip, when active) keep digging/building for as
                    // long as the brush is held - continuous rates, not a target/plateau, same
                    // as Crease's own accumulate-on branch.
                    float normalRate = sign * creaseDepthFactor * effectiveStrengthAccumulate * CreaseSpeed * dt;
                    if (hasLip) normalRate += sign * damLipHeight * effectiveStrengthAccumulate * CreaseSpeed * dt;
                    verts[i] += localNormal * (normalRate * weight);
                    verts[i] += -tangentialOffset * Mathf.Clamp01(creasePinch * weight * effectiveStrengthAccumulate * CreaseSpeed * dt);
                }
                else
                {
                    Vector3 pinched = tangentialOffset * (1f - creasePinch * weight);
                    float normalOffset = depth * weight;
                    if (hasLip) normalOffset += lip * weight;

                    Vector3 target = localPoint + pinched + localNormal * normalOffset;
                    Vector3 toTarget = target - verts[i];
                    verts[i] += toTarget * Mathf.Clamp01(weight * effectiveStrength * CreaseSpeed * dt); // see Clamp01 note on Clay
                }

                _dirtyVertexScratch.Add(i);
            }
        }

        private void HandleInflateInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;
            UpdateStrokeSpeed(hitPoint);

            bool rightHeld = mouse.rightButton.isPressed;
            bool invertHeld = rightHeld || CtrlHeld;
            _previewPositive = invertHeld ? !isPositive : isPositive;

            LogRayHit(mouse, ray, hitPoint, hitNormal);

            if (mouse.leftButton.isPressed && !altHeld)
                ApplyInflateBrush(hitPoint, hitNormal, invertHeld ? !isPositive : isPositive);
            else if (rightHeld)
                ApplyInflateBrush(hitPoint, hitNormal, !isPositive);
        }

        private void ApplyInflateBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            // Not InverseTransformDirection: that is rotation-only and mis-tilts the normal
            // on a non-uniformly scaled object - see SculptableMesh.WorldToLocalNormal.
            Vector3 localNormal = sculptableMesh.WorldToLocalNormal(worldNormal);

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                ApplyInflateBrushLocal(Vector3.Scale(localPoint, sign), mirroredNormal, positive);
            }

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        // Pushes each vertex outward along its OWN normal (the mesh's per-vertex normals,
        // not the single raycast hit normal or an averaged plane like Clay) so corners round
        // off and the whole footprint puffs up like a balloon - the ZBrush Inflate / Blender
        // Inflate-Deflate feel, distinct from Clay's flat plateau or Crease's pinch-to-ridge.
        // A constant per-frame push along a fixed direction rather than a lerp toward a target
        // - that's the Accumulate-ON path (this brush's only behavior before Accumulate
        // existed), so unlike Clay/Crease/Smooth it doesn't need the Clamp01 overshoot guard
        // there. Accumulate OFF (see ApplyInflateBrushLocalManaged) DOES lerp toward a target,
        // and does need that guard - it's the one place in this method a large dt can overshoot.
        private void ApplyInflateBrushLocal(Vector3 localPoint, Vector3 localNormal, bool positive)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            Vector3[] normals = sculptableMesh.Normals;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyInflateBrushLocalJob(localPoint, localNormal, positive, candidates, verts, normals);
            else
                ApplyInflateBrushLocalManaged(localPoint, localNormal, positive, candidates, verts, normals);
        }

        private void ApplyInflateBrushLocalJob(Vector3 localPoint, Vector3 localNormal, bool positive, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float effectiveStrength = EffectiveBrushStrengthPlateau;
            float amount = sign * EffectiveBrushStrengthAccumulate * InflateSpeed * dt;

            GatherCandidatesNative(candidates, verts, normals, sculptableMesh.Mask);
            var job = new InflateJob
            {
                PositionsIn = _nativePositionsIn,
                NormalsIn = _nativeNormalsIn,
                MaskIn = _nativeMaskIn,
                PositionsOut = _nativePositionsOut,
                AppliedOut = _nativeAppliedOut,
                LocalPoint = localPoint,
                BrushRadius = brushRadius,
                Amount = amount,
                Accumulate = accumulate,
                LocalNormal = localNormal,
                CapAmount = brushRadius * InflateOffCapFactor * sign,
                LerpFactorScale = effectiveStrength * InflateSpeed * dt,
                FrontFacingOnly = frontFacingOnly,
                CameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position),
            };
            job.Schedule(candidates.Count, 32).Complete();

            ScatterJobResults(candidates, verts);
        }

        private void ApplyInflateBrushLocalManaged(Vector3 localPoint, Vector3 localNormal, bool positive, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float effectiveStrength = EffectiveBrushStrengthPlateau;
            float effectiveStrengthAccumulate = EffectiveBrushStrengthAccumulate;
            Vector3 target = localPoint + localNormal * (brushRadius * InflateOffCapFactor * sign);
            Vector3 cameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position);

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(verts[i], localPoint);
                if (dist > brushRadius) continue;

                float t01 = 1f - dist / brushRadius;
                float weight = t01 * t01 * (3f - 2f * t01) * (1f - sculptableMesh.Mask[i]) // smoothstep, masked-out
                    * FrontFacingWeight(frontFacingOnly, normals[i], verts[i], cameraLocalPos);
                if (weight <= 0f) continue;

                sculptableMesh.RecordUndoBeforeIfNeeded(i);

                if (accumulate)
                {
                    verts[i] += normals[i] * (weight * sign * effectiveStrengthAccumulate * InflateSpeed * dt);
                }
                else
                {
                    Vector3 toTarget = target - verts[i];
                    verts[i] += toTarget * Mathf.Clamp01(weight * effectiveStrength * InflateSpeed * dt); // see Clamp01 note on Clay
                }

                _dirtyVertexScratch.Add(i);
            }
        }

        private void HandleFlattenInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;
            UpdateStrokeSpeed(hitPoint);

            bool rightHeld = mouse.rightButton.isPressed;
            bool invertHeld = rightHeld || CtrlHeld;
            _previewPositive = invertHeld ? !isPositive : isPositive;

            LogRayHit(mouse, ray, hitPoint, hitNormal);

            if (mouse.leftButton.isPressed && !altHeld)
                ApplyFlattenBrush(hitPoint, hitNormal, invertHeld ? !isPositive : isPositive);
            else if (rightHeld)
                ApplyFlattenBrush(hitPoint, hitNormal, !isPositive);
        }

        private void ApplyFlattenBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            // Not InverseTransformDirection: that is rotation-only and mis-tilts the normal
            // on a non-uniformly scaled object - see SculptableMesh.WorldToLocalNormal.
            Vector3 localNormal = sculptableMesh.WorldToLocalNormal(worldNormal);

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                ApplyFlattenBrushLocal(Vector3.Scale(localPoint, sign), mirroredNormal, positive);
            }

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        // Projects every vertex in the footprint onto one shared plane - the classic
        // ZBrush/Nomad/Blender Flatten. The plane is the footprint's own AREA plane: a
        // falloff-weighted average over the whole footprint rather than the single raycast hit's
        // own triangle, for exactly the reasons spelled out on ApplyClayBrushLocal (one
        // triangle's normal jitters as the stroke crosses triangles, and stacks into a
        // stair-stepped surface). flattenPlaneOffset then slides that plane along its own normal
        // to get the Fill/Scrape variants out of the same code.
        //
        // The origin is averaged from where this stroke FOUND each vertex
        // (SculptableMesh.StrokeStartPosition), not from where they are now - the "original
        // coordinates" trick Blender's flatten-family brushes use, and the one point where
        // Flatten must differ from Clay. Clay deliberately re-plans against the live surface
        // because it adds material on top of whatever is already there; Flatten's plane is
        // instead the absolute profile the stroke is driving toward, and averaging it live makes
        // it chase its own output: at a non-zero offset the plane is re-derived each frame from
        // vertices the previous frame just moved to it, then offset AGAIN, so holding the brush
        // still drills or balloons without limit instead of settling. Measured before the fix,
        // a held Fill dab at offset +0.3 on a radius-0.5 sphere pushed the surface out to 0.89
        // (it should stop at ~0.6); the anchored plane converges and stops. The plane NORMAL is
        // still averaged from live normals, which is stable on its own - flattening turns them
        // toward the plane normal, so that average converges rather than running away.
        //
        // Unlike Clay, which offsets each vertex's target ALONG the normal by height*weight and
        // therefore builds material, Flatten's target is the plane itself for every vertex - the
        // falloff only controls how fast each one gets there. That difference is the whole brush:
        // a held Clay dab keeps rising, a held Flatten dab converges onto the plane and stops.
        // The weight still matters at the footprint edge, where it leaves a soft blend into the
        // untouched surface instead of a disc-shaped step.
        //
        // Inverted (RMB / Ctrl, or the Positive toggle off) pushes vertices AWAY from the plane
        // instead - Blender's Ctrl+Flatten contrast/sharpen - which is the divergent direction
        // and so is the only one that needs a cap (see FlattenContrastLimit).
        private void ApplyFlattenBrushLocal(Vector3 localPoint, Vector3 localNormal, bool positive)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            Vector3[] normals = sculptableMesh.Normals;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyFlattenBrushLocalJob(localPoint, localNormal, positive, candidates, verts, normals);
            else
                ApplyFlattenBrushLocalManaged(localPoint, localNormal, positive, candidates, verts, normals);
        }

        private void ApplyFlattenBrushLocalJob(Vector3 localPoint, Vector3 localNormal, bool positive, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
            float dt = Time.deltaTime;

            GatherCandidatesNative(candidates, verts, normals, sculptableMesh.Mask);
            for (int ci = 0; ci < candidates.Count; ci++)
                _nativeClayStrokeStart[ci] = sculptableMesh.StrokeStartPosition(candidates[ci]);

            // Clay's pass-1 job, reused with the round tip (TipRoundness 1) and a full-radius
            // taper (EdgeSoftness 1) - which reduces ClayTipShapeT01/ClayFalloff to exactly the
            // plain smoothstep-over-the-radius weight ApplyFlattenBrushLocalManaged computes, and
            // makes Tangent0/Bitangent0 dead parameters (ClayTipShapeT01 returns before reading
            // them at roundness 1), hence Vector3.zero rather than a basis nothing consumes. Only
            // WeightsOut is consumed here (see the reduction below), so the job's two weighted-sum
            // outputs are written into the shared scratch and ignored.
            var weightJob = new ClayWeightJob
            {
                PositionsIn = _nativePositionsIn,
                NormalsIn = _nativeNormalsIn,
                MaskIn = _nativeMaskIn,
                WeightsOut = _nativeClayWeights,
                WeightedPosOut = _nativeClayWeightedPos,
                WeightedNormalOut = _nativeClayWeightedNormal,
                LocalPoint = localPoint,
                BrushRadius = brushRadius,
                Tangent0 = Vector3.zero,
                Bitangent0 = Vector3.zero,
                TipRoundness = 1f,
                EdgeSoftness = 1f,
                FrontFacingOnly = frontFacingOnly,
                CameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position),
            };
            weightJob.Schedule(candidates.Count, 32).Complete();

            // Sequential reduction across the footprint, on the main thread for the same reason
            // Clay's is (see ClayWeightJob). Weights come from the job; the weighted sums are
            // recomputed here from _nativeClayStrokeStart rather than read out of the job's
            // WeightedPosOut, because Flatten's plane is anchored to where the stroke FOUND the
            // surface, not to where its own earlier frames have already pushed it - see
            // ApplyFlattenBrushLocal. Two multiplies per candidate in a loop that already runs,
            // against a second copy of ClayWeightJob differing only in which array it reduces.
            Vector3 planeOriginSum = Vector3.zero, planeNormalSum = Vector3.zero;
            float planeWeightSum = 0f;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                float w = _nativeClayWeights[ci];
                planeOriginSum += _nativeClayStrokeStart[ci] * w;
                planeNormalSum += _nativeNormalsIn[ci] * w;
                planeWeightSum += w;
            }
            if (planeWeightSum <= 1e-6f) return;

            Vector3 planeNormal = planeNormalSum.sqrMagnitude > 1e-8f ? planeNormalSum.normalized : localNormal;
            Vector3 planeOrigin = planeOriginSum / planeWeightSum + planeNormal * (brushRadius * flattenPlaneOffset);

            var dispJob = new FlattenDisplacementJob
            {
                PositionsIn = _nativePositionsIn,
                StrokeStartIn = _nativeClayStrokeStart,
                WeightsIn = _nativeClayWeights,
                PositionsOut = _nativePositionsOut,
                AppliedOut = _nativeAppliedOut,
                PlaneOrigin = planeOrigin,
                PlaneNormal = planeNormal,
                LerpFactorScale = EffectiveBrushStrength * FlattenSpeed * dt,
                Contrast = !positive,
                MaxOffStart = brushRadius * FlattenContrastLimit,
            };
            dispJob.Schedule(candidates.Count, 32).Complete();

            ScatterJobResults(candidates, verts);
        }

        private void ApplyFlattenBrushLocalManaged(Vector3 localPoint, Vector3 localNormal, bool positive, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
            float dt = Time.deltaTime;
            float lerpFactorScale = EffectiveBrushStrength * FlattenSpeed * dt;
            float maxOffStart = brushRadius * FlattenContrastLimit;

            if (_clayWeightScratch.Length < candidates.Count) _clayWeightScratch = new float[candidates.Count];
            float[] weights = _clayWeightScratch;

            Vector3 planeOriginSum = Vector3.zero;
            Vector3 planeNormalSum = Vector3.zero;
            float planeWeightSum = 0f;
            Vector3 cameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position);

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(verts[i], localPoint);
                if (dist > brushRadius) { weights[ci] = 0f; continue; }

                float t01 = 1f - dist / brushRadius;
                float w = t01 * t01 * (3f - 2f * t01) * (1f - sculptableMesh.Mask[i]) // smoothstep, masked-out
                    * FrontFacingWeight(frontFacingOnly, normals[i], verts[i], cameraLocalPos);
                weights[ci] = w;

                // StrokeStartPosition, not verts[i] - see ApplyFlattenBrushLocal on why the
                // plane is anchored to the surface this stroke began with.
                planeOriginSum += sculptableMesh.StrokeStartPosition(i) * w;
                planeNormalSum += normals[i] * w;
                planeWeightSum += w;
            }

            if (planeWeightSum <= 1e-6f) return;

            Vector3 planeNormal = planeNormalSum.sqrMagnitude > 1e-8f
                ? planeNormalSum.normalized : localNormal;
            // The plane the footprint gets projected onto, slid along its own normal by the
            // Plane Offset control - see flattenPlaneOffset for what the two directions mean.
            Vector3 planeOrigin = planeOriginSum / planeWeightSum + planeNormal * (brushRadius * flattenPlaneOffset);

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                float weight = weights[ci];
                if (weight <= 0f) continue;
                int i = candidates[ci];

                sculptableMesh.RecordUndoBeforeIfNeeded(i);

                // Signed height above the plane. Flatten cancels it; contrast doubles down on it.
                float along = Vector3.Dot(verts[i] - planeOrigin, planeNormal);
                // Clamp01 for the same reason Clay's does - this is a lerp fraction toward the
                // plane, and a frame hitch with a large dt would otherwise overshoot past it.
                float lerp = Mathf.Clamp01(weight * lerpFactorScale);
                verts[i] += planeNormal * ((positive ? -along : along) * lerp);

                if (!positive)
                {
                    // Contrast only - see FlattenContrastLimit for why this direction alone
                    // needs bounding, and why the bound is symmetric.
                    float fromStart = Vector3.Dot(verts[i] - sculptableMesh.StrokeStartPosition(i), planeNormal);
                    if (fromStart > maxOffStart) verts[i] -= planeNormal * (fromStart - maxOffStart);
                    else if (fromStart < -maxOffStart) verts[i] -= planeNormal * (fromStart + maxOffStart);
                }

                _dirtyVertexScratch.Add(i);
            }
        }

        private void HandleSmoothInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;
            _previewPositive = true; // Smooth has no add/subtract direction - always neutral/green

            LogRayHit(mouse, ray, hitPoint, hitNormal);

            // Same Alt-reserved-for-orbit rule as Clay; either mouse button smooths since
            // there's no positive/negative to invert.
            if ((mouse.leftButton.isPressed && !altHeld) || mouse.rightButton.isPressed)
                ApplySmoothBrush(hitPoint);
        }

        private void ApplySmoothBrush(Vector3 worldPoint)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
                ApplySmoothBrushLocal(Vector3.Scale(localPoint, sign));

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        private void ApplySmoothBrushLocal(Vector3 localPoint)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplySmoothBrushLocalJob(localPoint, candidates, verts);
            else
                ApplySmoothBrushLocalManaged(localPoint, candidates, verts);
        }

        // See SmoothRelaxJob's remarks for why this is a Jacobi-style parallel relaxation rather
        // than the managed method's Gauss-Seidel-style in-place one - a deliberate, necessary
        // substitution for parallelism, not a bug.
        private void ApplySmoothBrushLocalJob(Vector3 localPoint, List<int> candidates, Vector3[] verts)
        {
            int totalVerts = verts.Length;
            EnsureSmoothFullMeshScratch(totalVerts);
            NativeArray<Vector3>.Copy(verts, _nativeFullPositionMirror, totalVerts); // full-mesh mirror, refreshed once per call - see field remarks

            GatherCandidatesNative(candidates, verts, sculptableMesh.Normals, sculptableMesh.Mask);

            if (!_nativeSmoothCandidates.IsCreated || _nativeSmoothCandidates.Length < candidates.Count)
            {
                if (_nativeSmoothCandidates.IsCreated) _nativeSmoothCandidates.Dispose();
                _nativeSmoothCandidates = new NativeArray<int>(Mathf.Max(Mathf.NextPowerOfTwo(candidates.Count), MinJobVertexCount), Allocator.Persistent);
            }
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int globalIdx = candidates[ci];
                _nativeSmoothCandidates[ci] = globalIdx;
                _nativeVertexToSlot[globalIdx] = ci;
            }

            var weightJob = new SmoothWeightJob
            {
                PositionsIn = _nativePositionsIn,
                NormalsIn = _nativeNormalsIn,
                MaskIn = _nativeMaskIn,
                WeightsOut = _nativeClayWeights,
                LocalPoint = localPoint,
                BrushRadius = brushRadius,
                FrontFacingOnly = frontFacingOnly,
                CameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position),
            };
            weightJob.Schedule(candidates.Count, 32).Complete();

            float dt = Time.deltaTime;
            float iterAmount = EffectiveBrushStrength * MaxSmoothIterations;
            int fullIterations = Mathf.FloorToInt(iterAmount);
            float partialFactor = iterAmount - fullIterations;
            float lerpFactorScale = SmoothSpeed * dt;

            NativeArray<int> adjOffsets = sculptableMesh.AdjacencyOffsets;
            NativeArray<int> adjNeighbors = sculptableMesh.AdjacencyNeighbors;

            NativeArray<Vector3> readBuf = _nativePositionsIn;
            NativeArray<Vector3> writeBuf = _nativePositionsOut;
            bool anyPassRan = fullIterations > 0 || partialFactor > 0.001f;

            // Every pass is scheduled up front as one dependency chain and waited on ONCE, rather
            // than Schedule().Complete() per pass. The passes are inherently sequential (each
            // reads the previous one's output - see SmoothRelaxJob's Jacobi remarks) and the
            // chain preserves that exactly; what it drops is the 9 extra main-thread sync points
            // a high-strength application used to pay, which at MaxSmoothIterations is most of
            // what makes Smooth cost more per frame than the single-pass brushes.
            JobHandle chain = default;
            for (int pass = 0; pass < fullIterations; pass++)
            {
                chain = ScheduleSmoothRelaxJob(candidates.Count, readBuf, writeBuf, adjOffsets, adjNeighbors, 1f, lerpFactorScale, chain);
                (readBuf, writeBuf) = (writeBuf, readBuf);
            }
            if (partialFactor > 0.001f)
            {
                chain = ScheduleSmoothRelaxJob(candidates.Count, readBuf, writeBuf, adjOffsets, adjNeighbors, partialFactor, lerpFactorScale, chain);
                (readBuf, writeBuf) = (writeBuf, readBuf);
            }
            chain.Complete();

            // Scatter the final (post-swap, so it's in readBuf) result back - mirrors the
            // managed method's own dirty rule (weight > 0), constant across every pass. The
            // vertexToSlot reset always has to happen (so a later call at a different footprint
            // never reads a stale slot), but writing verts[]/marking dirty only makes sense if a
            // pass actually ran - matches the managed method's own no-op-when-iterAmount-too-
            // small edge case (theoretical given BrushStrength's enforced 0.01 minimum, kept
            // correct anyway rather than assuming the UI clamp is the only caller).
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int globalIdx = candidates[ci];
                _nativeVertexToSlot[globalIdx] = -1; // targeted reset, not a full-array clear - see field remarks
                if (!anyPassRan || _nativeClayWeights[ci] <= 0f) continue;
                sculptableMesh.RecordUndoBeforeIfNeeded(globalIdx);
                verts[globalIdx] = readBuf[ci];
                _dirtyVertexScratch.Add(globalIdx);
            }
        }

        private JobHandle ScheduleSmoothRelaxJob(int candidateCount, NativeArray<Vector3> readBuf, NativeArray<Vector3> writeBuf,
            NativeArray<int> adjOffsets, NativeArray<int> adjNeighbors, float passFactor, float lerpFactorScale, JobHandle dependency)
        {
            var job = new SmoothRelaxJob
            {
                Candidates = _nativeSmoothCandidates,
                AdjacencyOffsets = adjOffsets,
                AdjacencyNeighbors = adjNeighbors,
                VertexToSlot = _nativeVertexToSlot,
                FullPositions = _nativeFullPositionMirror,
                PositionsRead = readBuf,
                PositionsWrite = writeBuf,
                Weights = _nativeClayWeights,
                PassFactor = passFactor,
                LerpFactorScale = lerpFactorScale,
            };
            return job.Schedule(candidateCount, 32, dependency);
        }

        private void ApplySmoothBrushLocalManaged(Vector3 localPoint, List<int> candidates, Vector3[] verts)
        {
            if (_smoothWeightScratch.Length < candidates.Count) _smoothWeightScratch = new float[candidates.Count];
            float[] weights = _smoothWeightScratch;
            bool anyInRange = false;
            Vector3[] normals = sculptableMesh.Normals;
            Vector3 cameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position);

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(verts[i], localPoint);
                if (dist > brushRadius) { weights[ci] = 0f; continue; }

                float t01 = 1f - dist / brushRadius;
                weights[ci] = t01 * t01 * (3f - 2f * t01) * (1f - sculptableMesh.Mask[i]) // smoothstep, masked-out
                    * FrontFacingWeight(frontFacingOnly, normals[i], verts[i], cameraLocalPos);
                anyInRange = true;
            }
            if (!anyInRange) return;

            float dt = Time.deltaTime;
            float iterAmount = EffectiveBrushStrength * MaxSmoothIterations;
            int fullIterations = Mathf.FloorToInt(iterAmount);
            float partialFactor = iterAmount - fullIterations;

            for (int pass = 0; pass < fullIterations; pass++)
                RunSmoothRelaxationPass(verts, candidates, weights, 1f, dt);
            if (partialFactor > 0.001f)
                RunSmoothRelaxationPass(verts, candidates, weights, partialFactor, dt);
        }

        private void RunSmoothRelaxationPass(Vector3[] verts, List<int> candidates, float[] weights, float passFactor, float dt)
        {
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                float w = weights[ci];
                if (w <= 0f) continue;
                int i = candidates[ci];

                Vector3 toAverage = sculptableMesh.GetNeighborAverage(i) - verts[i];
                sculptableMesh.RecordUndoBeforeIfNeeded(i);
                verts[i] += toAverage * Mathf.Clamp01(w * passFactor * SmoothSpeed * dt); // see Clamp01 note on Clay
                _dirtyVertexScratch.Add(i);
            }
        }

        // Grabs whatever's under the cursor on mouse-down and drags it with the cursor's
        // world-space movement along a camera-facing plane through the grab point, instead of
        // re-raycasting the mesh every frame. That's what makes it keep tracking once the
        // cursor moves past the mesh's silhouette, and gives 1:1 "pull" instead of a slow
        // per-frame nudge along a fixed normal.
        private void HandleMoveDrag(Mouse mouse, bool overUI, bool altHeld)
        {
            if (_isMoveDragging)
            {
                if (!mouse.leftButton.isPressed)
                {
                    EndMoveDrag();
                    return;
                }

                Ray dragRay = cam.ScreenPointToRay(mouse.position.ReadValue());
                if (RayPlaneIntersect(dragRay, _dragPlanePoint, _dragPlaneNormal, out Vector3 current))
                {
                    Vector3 worldDelta = current - _lastDragPoint;
                    if (worldDelta.sqrMagnitude > 1e-12f)
                    {
                        Vector3 localDelta = sculptableMesh.transform.InverseTransformVector(worldDelta);
                        _dirtyVertexScratch.Clear();
                        foreach (var (selection, sign) in _grabSelections)
                        {
                            sculptableMesh.ApplyGrabDelta(selection, Vector3.Scale(localDelta, sign));
                            foreach (int i in selection.Indices) _dirtyVertexScratch.Add(i);
                        }
                        sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
                    }
                    _lastDragPoint = current;
                }

                _isHovering = true;
                _hoverPoint = _lastDragPoint;
                _previewPositive = true;
                return;
            }

            // Not dragging: only start one on a fresh click while actually hovering the mesh.
            _isHovering = false;
            if (overUI || altHeld) return;

            Ray hoverRay = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(hoverRay, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);
            _isHovering = hasHit;
            if (_isHovering)
            {
                _hoverPoint = hitPoint;
                _hoverNormal = hitNormal;
                _previewPositive = true;
            }

            if (!_isHovering || !mouse.leftButton.wasPressedThisFrame) return;

            Vector3 localHit = sculptableMesh.transform.InverseTransformPoint(hitPoint);
            Vector3 cameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position);
            var selections = new List<(SculptableMesh.GrabSelection, Vector3)>();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                var selection = sculptableMesh.SelectGrab(Vector3.Scale(localHit, sign), brushRadius, frontFacingOnly, cameraLocalPos);
                if (selection.IsValid) selections.Add((selection, sign));
            }
            if (selections.Count == 0) return;
            _grabSelections = selections;

            _isMoveDragging = true;
            _dragPlanePoint = hitPoint;
            _dragPlaneNormal = -cam.transform.forward;
            _lastDragPoint = hitPoint;

            if (logRayHits) Debug.Log($"[Sculpt] Move grab started at {hitPoint}");
        }

        private void EndMoveDrag()
        {
            if (!_isMoveDragging) return;
            _grabSelections = null;
            _isMoveDragging = false;
        }

        // internal (not private) so TransformGizmo can reuse the exact same axis-constrained
        // drag technique Move-brush dragging already uses, for its own Move/Scale handles - no
        // .asmdef boundary in this project (see [[project_scene_graph_epic]] memory), so
        // internal is enough without a public API change.
        internal static bool RayPlaneIntersect(Ray ray, Vector3 planePoint, Vector3 planeNormal, out Vector3 point)
        {
            float denom = Vector3.Dot(ray.direction, planeNormal);
            if (Mathf.Abs(denom) < 1e-6f) { point = default; return false; }

            float dist = Vector3.Dot(planePoint - ray.origin, planeNormal) / denom;
            if (dist < 0f) { point = default; return false; }

            point = ray.origin + ray.direction * dist;
            return true;
        }

        public void ResetMesh()
        {
            if (sculptableMesh == null) return;
            EndMoveDrag();
            sculptableMesh.SnapshotForUndo();
            sculptableMesh.ResetMesh();
        }

        public void Remesh()
        {
            if (sculptableMesh == null) return;
            sculptableMesh.SnapshotForUndo();
            sculptableMesh.Remesh(remeshResolution);
        }

        /// Live symmetry report for the selected object - pairs found, centreline size, and how
        /// many vertices have no counterpart. See SymmetryOps.Status for why it is recomputed
        /// rather than cached.
        public string SymmetryStatus() => SymmetryOps.Status(sculptableMesh, symmetryAxis, symmetryToleranceScale);

        /// Copies one side of the selected object onto the other through the vertex
        /// correspondence map. Returns a short result string for the UI, since "nothing visibly
        /// happened" and "the map could not pair anything" look identical in the viewport.
        public string MakeSymmetric(bool sourceIsPositive)
        {
            if (sculptableMesh == null) return "No object selected";
            EndMoveDrag();

            int changed = SymmetryOps.MakeSymmetric(sculptableMesh, symmetryAxis, symmetryToleranceScale,
                                                    sourceIsPositive, out int pairs, out int unmatched);

            string axis = SymmetryOps.AxisName(symmetryAxis);
            string from = sourceIsPositive ? "+" + axis : "-" + axis;
            string to = sourceIsPositive ? "-" + axis : "+" + axis;

            // Nothing was modified in this case - mirroring through a partial correspondence
            // tears the surface instead of repairing it (see SymmetryOps.MaxUnmatchedFraction).
            // The message names the alternative, because "too asymmetric to mirror" with no way
            // forward is what makes a refusal read as the tool being broken.
            if (changed == SymmetryOps.TooAsymmetric)
                return $"Too asymmetric to match up: {unmatched} vertices have no counterpart " +
                       $"across {axis} ({pairs} pairs do). Nudging vertices would tear those " +
                       $"apart - use Cut & Mirror {from} to {to} instead, which rebuilds that " +
                       "side outright.";

            if (changed < 0) return "No geometry to mirror";
            if (pairs == 0) return $"Nothing paired across {axis} - raise Match Tolerance";
            if (changed == 0) return $"Already symmetric across {axis} - {pairs} pairs match";

            // The unmatched count rides along on success too: it is the part of the model this
            // operation could not touch, and leaving it out is what let a partial mirror look
            // like a complete one.
            string leftover = unmatched > 0 ? $", {unmatched} unmatched" : string.Empty;
            return $"Mirrored {from} onto {to}: {changed} of {pairs} pairs{leftover}";
        }

        /// Cuts the selected object at the symmetry plane and rebuilds the far side as a
        /// reflection of the near one. The unconditional version of MakeSymmetric: it needs no
        /// vertex correspondence, so it is what to reach for when MakeSymmetric reports the model
        /// is too asymmetric to match up (see SymmetryOps.MirrorAndWeld).
        public string MirrorAndWeld(bool sourceIsPositive)
        {
            if (sculptableMesh == null) return "No object selected";
            EndMoveDrag();

            string axis = SymmetryOps.AxisName(symmetryAxis);
            string from = sourceIsPositive ? "+" + axis : "-" + axis;
            string to = sourceIsPositive ? "-" + axis : "+" + axis;

            if (!SymmetryOps.MirrorAndWeld(sculptableMesh, symmetryAxis, symmetryToleranceScale,
                                           sourceIsPositive,
                                           out int kept, out int discarded, out int vertexCount))
                return $"Nothing on the {from} side to mirror";

            // Reports what was THROWN AWAY as well as what was built, because that is the part
            // this operation cannot undo by pressing the other direction - the far side's own
            // shape is gone, and a user who meant the opposite direction should see that
            // immediately rather than discover it later.
            return $"Cut & mirrored {from} onto {to}: kept {kept} triangles, " +
                   $"replaced {discarded}, now {vertexCount} vertices";
        }

        /// Snaps the centreline onto the mirror plane and welds the duplicate vertices that
        /// leaves - the repair for a model joined from two mirrored halves, whose seam is two
        /// coincident shells rather than one shared edge loop.
        public string SymmetryCleanup()
        {
            if (sculptableMesh == null) return "No object selected";
            EndMoveDrag();

            if (!SymmetryOps.Cleanup(sculptableMesh, symmetryAxis, symmetryToleranceScale,
                                     out int snapped, out int welded))
                return "No geometry to clean up";

            string axis = SymmetryOps.AxisName(symmetryAxis);
            if (snapped == 0 && welded == 0) return $"Already clean across {axis} - nothing to do";
            if (welded == 0) return $"Snapped {snapped} vertices onto {axis} - no duplicates found";
            return snapped == 0
                ? $"Welded {welded} duplicate vertices"
                : $"Snapped {snapped} onto {axis}, welded {welded} duplicate vertices";
        }

        // Fixed destination rather than a save-file dialog - EditorUtility.SaveFilePanel only
        // exists in the Editor and would silently vanish once this ships as a standalone
        // build, whereas Environment.GetFolderPath is plain .NET and resolves the real
        // Desktop path in both. A proper save/load feature (with its own file-picker UX) is
        // planned as separate future work; this is just "get the current sculpt out to a
        // file I can open elsewhere" for now.
        public string Export()
        {
            if (sculptableMesh == null) return null;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string folder = Path.Combine(desktop, "SculptExports");
            string path = ObjExporter.Export(sculptableMesh, folder);
            if (path != null) Debug.Log($"[Sculpt] Exported to {path}");
            return path;
        }

        // Drives the 2D screen-space ring cursor (see ShowBrushCursor/BrushCursorScreenPosition/
        // BrushCursorScreenDiameter/BrushCursorColor - SculptUIBuilder polls these to actually
        // draw it) and owns Cursor.visible alongside it: the OS pointer is hidden whenever the
        // ring is shown, and restored the instant it isn't, so a UI panel or another tool always
        // gets the ordinary pointer back. Sized/tinted every frame the same way the old
        // world-space BrushPreview sphere was - snapped to the sculpted surface while hovering
        // it, floating along the camera ray at the model's rough depth otherwise - just measured
        // in screen pixels instead of world units.
        private void UpdateBrushCursor()
        {
            bool show = false;
            Color color = PositiveColor;

            // Same "another tool owns the cursor" carve-out the old preview had - Transpose/
            // Scale drag the transform, ZSpheres place and grow rig spheres, and each shows its
            // own affordance instead.
            bool sculptToolActive = sculptableMesh != null && cam != null && (Gizmo == null || Gizmo.Mode == GizmoMode.Sculpt);

            if (sculptToolActive && !_isOverUI)
            {
                Mouse mouse = Mouse.current;
                if (mouse != null)
                {
                    // Frozen at the drag's start point while resizing/adjusting strength,
                    // instead of following the live mouse - the S/F drags scrub their value off
                    // horizontal mouse DELTA alone (see HandleBrushResizeKey/
                    // HandleBrushStrengthKey), so the ring only needs to grow/shrink in place,
                    // not chase the mouse across the screen while the gesture plays out.
                    // While Lazy Mouse has the rope taut the ring follows the ROPE's near end,
                    // not the pointer: that is where the dab is actually landing (it is the
                    // position every brush handler just raycast from, and the position
                    // _hoverPoint below was derived from), so a ring left under the pointer
                    // would be drawing the brush somewhere it is not about to touch. The
                    // pointer end is not lost - SculptUIBuilder draws the tether line back to
                    // it, see LazyMouseTetherActive.
                    Vector2 screenPos = _isResizingBrush ? _resizeAnchorScreenPos
                        : _isAdjustingStrength ? _strengthAdjustAnchorScreenPos
                        : _lazyMouseActive ? _lazyMouseScreenPos
                        : mouse.position.ReadValue();
                    Vector3 worldPoint;
                    bool positive;

                    if (_isHovering)
                    {
                        worldPoint = _hoverPoint + _hoverNormal * 0.01f;
                        positive = _previewPositive;
                    }
                    else
                    {
                        float fallbackDistance = Mathf.Max(1f, Vector3.Distance(cam.transform.position, sculptableMesh.transform.position));
                        Ray ray = cam.ScreenPointToRay(screenPos);
                        worldPoint = ray.GetPoint(fallbackDistance);
                        positive = true; // neutral tint when just showing size, not actively sculpting
                    }

                    float diameterPx = ProjectDiameterToScreenPixels(worldPoint, brushRadius * AverageScale());
                    if (diameterPx > 0f)
                    {
                        show = true;
                        _brushCursorScreenPos = screenPos;
                        // Floored so a tiny brush viewed from far away still reads as a visible
                        // ring rather than shrinking past legibility - the projected size is
                        // otherwise unbounded in both directions.
                        _brushCursorScreenDiameter = Mathf.Max(diameterPx, MinCursorScreenDiameterPx);

                        // Smooth gets its own blue/dashed look (see SmoothColor/
                        // BrushCursorDashed) since it has no add/subtract polarity at all -
                        // showing it as an ordinary "positive" green dab would suggest it adds
                        // material the way Clay/Inflate/etc. do. The outer ring keeps this same
                        // tint while adjusting strength (F) too - it's still showing brush
                        // radius/polarity, unchanged - and SculptUIBuilder layers a separate red
                        // inner circle on top for the strength readout (see IsAdjustingStrength).
                        bool isSmooth = currentBrush == BrushType.Smooth;
                        color = isSmooth ? SmoothColor
                            : positive ? PositiveColor : NegativeColor;
                        _brushCursorDashed = isSmooth;
                    }
                }
            }

            _showBrushCursor = show;
            if (show) _brushCursorColor = color;
            Cursor.visible = !show;
        }

        // Measures how many screen pixels `worldRadius` covers at `worldCenter` by projecting
        // both the center and a point one radius away (along the camera's own right vector,
        // always perpendicular to view direction) and comparing their screen positions - works
        // unmodified for perspective and orthographic cameras alike, unlike computing it from
        // FOV/distance by hand.
        private float ProjectDiameterToScreenPixels(Vector3 worldCenter, float worldRadius)
        {
            Vector3 centerScreen = cam.WorldToScreenPoint(worldCenter);
            if (centerScreen.z <= 0f) return 0f; // behind the camera
            Vector3 edgeScreen = cam.WorldToScreenPoint(worldCenter + cam.transform.right * worldRadius);
            return Vector2.Distance(centerScreen, edgeScreen) * 2f;
        }

        private float AverageScale()
        {
            Vector3 s = sculptableMesh.transform.lossyScale;
            return (s.x + s.y + s.z) / 3f;
        }

        private void OnDrawGizmos()
        {
            if (sculptableMesh == null) return;

            if (showWireframeGizmo && sculptableMesh.Mesh != null)
            {
                Gizmos.color = new Color(1f, 1f, 1f, 0.3f);
                Gizmos.DrawWireMesh(sculptableMesh.Mesh, sculptableMesh.transform.position,
                    sculptableMesh.transform.rotation, sculptableMesh.transform.lossyScale);
            }

            if (_isHovering)
            {
                Gizmos.color = _previewPositive ? PositiveColor : NegativeColor;
                Gizmos.DrawWireSphere(_hoverPoint, brushRadius * AverageScale());
                Gizmos.DrawLine(_hoverPoint, _hoverPoint + _hoverNormal * 0.2f);
            }
        }

        // ------------------------------------------------------------------- save/load state

        /// Every brush setting worth persisting, as a flat JsonUtility-serializable block (see
        /// SceneSerializer). Lives INSIDE SculptController, and Capture/Apply touch the private
        /// backing fields directly, deliberately: the alternative was ~25 new public properties
        /// existing only for the serializer, and a set of per-brush arrays that have no public
        /// surface at all. Keeping it here means a future brush setting is remembered by editing
        /// one class rather than three.
        ///
        /// Per-brush arrays (strength/radius/polarity/accumulate/accumulate-strength) are saved
        /// alongside the live values because they ARE the user's tuning: without them, loading a
        /// file would restore the current brush correctly and silently reset every other brush's
        /// remembered feel to defaults the first time it was selected.
        [Serializable]
        public class Settings
        {
            public float brushStrength;
            public float brushRadius;
            public int currentBrush;
            public bool isPositive;
            public bool accumulate;
            public float accumulateStrength;
            public bool frontFacingOnly;
            public bool buildUpOnHold;

            public float clayHeightFactor;
            public float clayTipRoundness;
            public float clayEdgeSoftness;

            public bool useAlpha;
            public int alphaType;
            public float alphaRotation;
            public float alphaScale;
            public bool invertAlpha;

            public float creasePinch;
            public float creaseDepthFactor;
            public float damLipHeight;
            public float maskHardness;

            public float pressureFloor;
            public float pressureCurve;

            public int remeshResolution;
            public bool useBurstJobs;
            public bool showWireframeGizmo;

            public bool maskPaintMode;

            // Per-brush memory, indexed by BrushType. Length is validated on Apply rather than
            // trusted - a file written by an older build (or hand-edited) can legitimately have
            // fewer entries than today's BrushType has members.
            public float[] perBrushStrength;
            // No perBrushRadius counterpart: radius is one value shared by every brush (see
            // _brushStrengthPerType), saved as `brushRadius` above. A file written before that
            // change still carries the old per-brush array; JsonUtility drops the unknown field
            // and `brushRadius` restores the size that was actually in hand when it was saved.
            public bool[] perBrushPolarity;
            public bool[] perBrushAccumulate;
            public float[] perBrushAccumulateStrength;
            public bool[] perBrushFrontFacingOnly;
        }

        public Settings CaptureSettings()
        {
            // Flush the live values into the per-brush arrays first. BrushStrength writes
            // through on every set, but currentBrush's own slot is the one that can be mid-edit,
            // and Capture must not save a stale entry for the brush in hand.
            int cur = (int)currentBrush;
            _brushStrengthPerType[cur] = brushStrength;
            _brushPolarity[cur] = isPositive;
            _brushAccumulate[cur] = accumulate;
            _accumulateStrengthPerType[cur] = accumulateStrength;
            _brushFrontFacingOnly[cur] = frontFacingOnly;

            return new Settings
            {
                brushStrength = brushStrength,
                brushRadius = brushRadius,
                currentBrush = cur,
                isPositive = isPositive,
                accumulate = accumulate,
                accumulateStrength = accumulateStrength,
                frontFacingOnly = frontFacingOnly,
                buildUpOnHold = buildUpOnHold,

                clayHeightFactor = clayHeightFactor,
                clayTipRoundness = clayTipRoundness,
                clayEdgeSoftness = clayEdgeSoftness,

                useAlpha = useAlpha,
                alphaType = (int)alphaType,
                alphaRotation = alphaRotation,
                alphaScale = alphaScale,
                invertAlpha = invertAlpha,

                creasePinch = creasePinch,
                creaseDepthFactor = creaseDepthFactor,
                damLipHeight = damLipHeight,
                maskHardness = maskHardness,

                pressureFloor = pressureFloor,
                pressureCurve = pressureCurve,

                remeshResolution = remeshResolution,
                useBurstJobs = useBurstJobs,
                showWireframeGizmo = showWireframeGizmo,

                maskPaintMode = IsMaskPaintMode,

                perBrushStrength = (float[])_brushStrengthPerType.Clone(),
                perBrushPolarity = (bool[])_brushPolarity.Clone(),
                perBrushAccumulate = (bool[])_brushAccumulate.Clone(),
                perBrushAccumulateStrength = (float[])_accumulateStrengthPerType.Clone(),
                perBrushFrontFacingOnly = (bool[])_brushFrontFacingOnly.Clone(),
            };
        }

        /// Routes through the public CLAMPING properties wherever one exists rather than
        /// assigning the private fields, so a corrupt or hand-edited file can't push a value
        /// outside the range the rest of the code assumes (ClayFalloff divides by
        /// clayEdgeSoftness, for one - a zero there would produce NaN vertex positions).
        public void ApplySettings(Settings s)
        {
            if (s == null) return;

            CopyPerBrush(s.perBrushStrength, _brushStrengthPerType);
            CopyPerBrush(s.perBrushPolarity, _brushPolarity);
            CopyPerBrush(s.perBrushAccumulate, _brushAccumulate);
            CopyPerBrush(s.perBrushAccumulateStrength, _accumulateStrengthPerType);
            CopyPerBrush(s.perBrushFrontFacingOnly, _brushFrontFacingOnly);

            ClayHeightFactor = s.clayHeightFactor;
            ClayTipRoundness = s.clayTipRoundness;
            ClayEdgeSoftness = s.clayEdgeSoftness;

            UseAlpha = s.useAlpha;
            AlphaType = (BrushAlphaType)Mathf.Clamp(s.alphaType, 0, System.Enum.GetValues(typeof(BrushAlphaType)).Length - 1);
            AlphaRotation = s.alphaRotation;
            AlphaScale = s.alphaScale;
            InvertAlpha = s.invertAlpha;

            CreasePinch = s.creasePinch;
            CreaseDepthFactor = s.creaseDepthFactor;
            DamLipHeight = s.damLipHeight;
            MaskHardness = s.maskHardness;

            PressureFloor = s.pressureFloor;
            PressureCurve = s.pressureCurve;

            RemeshResolution = s.remeshResolution;
            UseBurstJobs = s.useBurstJobs;
            ShowWireframeGizmo = s.showWireframeGizmo;

            // CurrentBrush's setter swaps in that brush's remembered strength/polarity from
            // the arrays just restored above, so it has to come AFTER them - and the live values
            // are assigned after IT, since the swap would otherwise overwrite them. BrushRadius
            // is unaffected by the swap either way, being shared across brushes.
            CurrentBrush = (BrushType)Mathf.Clamp(s.currentBrush, 0, System.Enum.GetValues(typeof(BrushType)).Length - 1);
            BrushStrength = s.brushStrength;
            BrushRadius = s.brushRadius;
            IsPositive = s.isPositive;
            Accumulate = s.accumulate;
            AccumulateStrength = s.accumulateStrength;
            FrontFacingOnly = s.frontFacingOnly;
            BuildUpOnHold = s.buildUpOnHold;

            IsMaskPaintMode = s.maskPaintMode;

            // A load can land mid-stroke/mid-hover, and it replaces every object in the scene.
            // Clearing the sync sentinel forces SyncSelectionTarget to re-run on the next
            // Update, which already drops every per-stroke continuity cache (hover point, clay
            // stroke memory, move-drag, stroke speed) - reused rather than duplicated here so
            // the two can't drift apart.
            _lastSyncedTarget = null;
        }

        // Tolerates a saved array that is shorter (older build with fewer brushes) or longer
        // (file from a newer build) than this build's BrushType - copies the overlap and leaves
        // the rest at its compiled-in default rather than throwing or truncating the live array.
        private static void CopyPerBrush<T>(T[] src, T[] dst)
        {
            if (src == null || dst == null) return;
            System.Array.Copy(src, dst, Mathf.Min(src.Length, dst.Length));
        }
    }
}
