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
        // brush math itself - a GPU brush was ruled out because hit-testing the deformed surface
        // would need an async GPU readback, adding a frame or two of latency to every dab. Only
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
        // covers roughly the whole MinBrushRadius-MaxBrushRadius range - scale this together
        // with MaxBrushRadius (same ratio) if that range ever changes again, or a full-width
        // drag stops reaching the new max.
        private const float ResizeSensitivity = 0.01f;
        // Half ResizeSensitivity, matching that BrushStrength's range (0.01-1) is about half
        // the width of BrushRadius's (0.01-2) - same "full-width drag covers roughly the whole
        // range" feel, scaled to the smaller range.
        private const float StrengthAdjustSensitivity = 0.00125f;
        // Same "full-width drag covers roughly the whole range" tuning, for RemeshResolution's
        // 4-1024 span - see HandleRemeshDensityKey. Scaled up with the range so a full-width
        // drag still spans it rather than stopping halfway.
        private const float RemeshDensityDragSensitivity = 0.62f;
        // How long R must stay down before it arms the density gauge instead of firing a plain
        // Remesh() - long enough that the existing tap-to-remesh shortcut still lands cleanly
        // without a drag attached, short enough that reaching for the gauge on purpose doesn't
        // feel laggy.
        private const float RemeshHoldThreshold = 0.35f;
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

        // Crease/Dam Standard's stroke-continuity memory, in mesh-local space - null between
        // strokes (mouse up / hover lost / brush switched) so a fresh stroke starts clean.
        // Drives BOTH the distance-spaced dab stepper (ApplyCarveStroke) and the stroke-travel
        // direction the carve is built around: Crease pinches ACROSS that direction, and Dam
        // Standard additionally biases its lip onto the leading edge.
        private Vector3? _lastCarveStrokeLocal;
        private Vector3 _carveStrokeNormal;
        private Vector3 _carveStrokeDir;
        private float _carveDabCarry;

        // Clay's own stroke-continuity memory, in mesh-local space - null between strokes
        // (mouse up / hover lost / brush switched), same lifecycle as _lastCarveStrokeLocal
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

        // Pose brush - see HandlePoseInput. Same click/hold-drag/release shape as Move above,
        // right down to reusing RayPlaneIntersect against a camera-facing plane through the
        // grab point, but each mirrored selection is a PoseSelection (a chain, not a flat
        // weighted blob) and the drag target is read fresh from that plane every frame rather
        // than accumulated - see SculptableMesh.ApplyPoseDelta's remarks on why.
        private bool _isPoseDragging;
        private Vector3 _poseDragPlanePoint;
        private Vector3 _poseDragPlaneNormal;
        private List<(SculptableMesh.PoseSelection selection, Vector3 sign)> _poseSelections;

        // Guide line (see UpdatePoseChainVisual): a world-space LineRenderer per active pose
        // selection - up to one per mirror sign, matching _poseSelections. First version used
        // Sprites/Default with ordinary depth testing and was reported as barely-visible - "maybe
        // a slight outline" - because the chain runs directly along the mesh's own surface, which
        // z-fights against that same surface almost everywhere except where floating-point noise
        // happens to let it win. Custom/BrushPreviewOverlay (ZTest Always, ZWrite Off) is this
        // project's existing fix for exactly that problem - TransformGizmo's handles and the
        // brush-preview cursor already draw on top of everything for the identical reason (see
        // TransformGizmo.ApplyUnlitColor's remarks) - so this reuses it rather than re-solving it.
        private static Material _poseChainMaterial;
        private static readonly int PoseChainColorId = Shader.PropertyToID("_Color");
        private readonly List<LineRenderer> _poseChainLines = new List<LineRenderer>();
        private static readonly Color PoseChainColor = Color.white;
        // Fraction of brushRadius, not an absolute width - brushRadius is already the one value
        // in scope that tracks how big the CURRENT model/selection is (an artist sets it relative
        // to their own mesh's scale, same as every other brush), so a line this thin relative to
        // it reads consistently thin whether the mesh is a 1-unit test sphere or a 2-meter figure
        // - a fixed absolute width would have been fine for the former and invisible on the
        // latter, which is the other half of why the first version read as barely-there.
        private const float PoseChainWidthFactor = 0.03f;

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

        // Same S-drag pattern again (see HandleRemeshDensityKey), but gated behind a hold
        // threshold rather than starting the instant R goes down - R already has a meaning on a
        // plain tap (Remesh at whatever resolution is set), so the gauge only arms once the key
        // has been held long enough to tell a drag-in-progress from that tap.
        private bool _isAdjustingRemeshDensity;
        private float _remeshDensityStartValue;
        private float _remeshDensityStartMouseX;
        // -1 while R is up; the time it went down otherwise, so the hold threshold and the
        // tap-vs-hold decision on release can both be measured off it.
        private float _rKeyDownTime = -1f;

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
        private float[] _relaxWeightScratch = System.Array.Empty<float>();

        // Vertex indices actually moved by the current frame's brush application (across every
        // mirror sign AND every sub-dab of the frame) - cleared at the start of each Apply*Brush
        // wrapper, filled in by the matching *Local method(s), then handed to
        // SculptableMesh.ApplyVerticesLocal so it only has to update the triangle-raycast grid for
        // triangles touching these vertices instead of rescanning the whole mesh. See
        // TriangleSpatialGrid for why this matters at higher triangle counts.
        private readonly DirtyVertexSet _dirtyVertexScratch = new DirtyVertexSet();

        // Union of _dirtyVertexScratch across every frame of the CURRENT stroke - cleared once on
        // mouse-down (see HandleSculptInput) rather than every frame, and unioned into (never
        // cleared by) FlushDirtyVertices below. HandleStrokeEndCommit reads this on release to run
        // ApplyPostStrokeUnifyPass over everything the whole stroke touched, across however many
        // brushes/frames/mirror dabs contributed to it - not just the last frame's footprint.
        private readonly DirtyVertexSet _strokeDirtyVertexScratch = new DirtyVertexSet();

        /// Add-once-if-not-present set of vertex indices, stamp-marked rather than hashed.
        ///
        /// This was a HashSet&lt;int&gt;, and it is written once per moved vertex per dab - which on
        /// a wide stroke over a dense mesh is hundreds of thousands of writes per frame, each
        /// paying a hash, a bucket probe and (on insert) a possible resize. A per-vertex "which
        /// build was I last added in" stamp gives identical semantics for one array read, leaves
        /// the members in a flat List that ApplyVerticesLocal can walk in index order with no
        /// enumerator at all, and never rehashes. Same scheme (and same reasoning) as
        /// SculptableMesh's own _affectedList and TriangleSpatialGrid's _movedTriangleStamp.
        ///
        /// The generation counter is what avoids clearing the stamp array between frames; it is
        /// reset alongside the array whenever the vertex count changes under it.
        private sealed class DirtyVertexSet
        {
            private int[] _stamp;
            private int _generation;

            /// The members of the current build, in insertion order. Handed straight to
            /// SculptableMesh.ApplyVerticesLocal's List overload - never copied.
            public readonly List<int> Items = new List<int>();

            public int Count => Items.Count;

            /// Starts a new build. `vertexCount` is the CURRENT mesh's vertex count - a Remesh (or
            /// an undo across one) resizes the mesh under this set, and a stamp array sized to the
            /// old topology would either throw or silently mark the wrong vertices.
            public void Clear(int vertexCount)
            {
                if (_stamp == null || _stamp.Length != vertexCount)
                {
                    _stamp = new int[vertexCount];
                    _generation = 0;
                }
                _generation++;
                Items.Clear();
            }

            public void Add(int vertexIndex)
            {
                // Bounds-checked rather than trusting the caller: every brush indexes this with a
                // spatial-query result, and a query answered from an index built before a topology
                // change can still name a vertex that no longer exists.
                if (_stamp == null || (uint)vertexIndex >= (uint)_stamp.Length) return;
                if (_stamp[vertexIndex] == _generation) return;
                _stamp[vertexIndex] = _generation;
                Items.Add(vertexIndex);
            }
        }

        /// Starts a fresh per-frame dirty set for whatever brush is about to run. Every brush
        /// entry point goes through this rather than clearing the set inline, so none of them can
        /// forget to size the stamp array to the current topology.
        private void BeginDirtyVertices()
        {
            _dirtyVertexScratch.Clear(sculptableMesh.Vertices.Length);
        }

        /// Pushes whatever the frame's brush application moved into the mesh, in one call. Skips
        /// entirely when nothing moved - a held-but-stationary stroke places no dabs at all, and
        /// an empty apply would still walk the sync/filter preamble for no result.
        private void FlushDirtyVertices()
        {
            if (_dirtyVertexScratch.Count == 0) return;
            // Folded into the stroke-wide set here, the one place every brush's per-frame flush
            // already passes through - see _strokeDirtyVertexScratch's remarks.
            List<int> items = _dirtyVertexScratch.Items;
            for (int i = 0; i < items.Count; i++) _strokeDirtyVertexScratch.Add(items[i]);
            sculptableMesh.ApplyVerticesLocal(items);
        }

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
        // The same falloff WITHOUT the mask term, which is what the area plane is measured with -
        // see ClayWeightJob. Kept as its own array rather than derived from _nativeClayWeights,
        // which cannot be divided back out where the mask is 1.
        private NativeArray<float> _nativeClayPlaneWeights;
        private NativeArray<Vector3> _nativeClayWeightedPos;
        private NativeArray<Vector3> _nativeClayWeightedNormal;
        // Each candidate's position as of THIS stroke's start (see
        // SculptableMesh.StrokeStartPosition) - the reference ClampStrokeDepth measures Clay's
        // per-stroke buildup cap against (and Flatten's contrast cap - see
        // FlattenContrastLimit, and Crease/Dam Standard's whole carve target - see CreaseJob).
        // Gathered in the Clay/Flatten/Crease job paths only, alongside
        // GatherCandidatesNative's shared arrays; the managed path reads StrokeStartPosition
        // directly.
        private NativeArray<Vector3> _nativeStrokeStart;
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
                _nativeClayPlaneWeights = new NativeArray<float>(_nativeScratchCapacity, Allocator.Persistent);
                _nativeClayWeightedPos = new NativeArray<Vector3>(_nativeScratchCapacity, Allocator.Persistent);
                _nativeClayWeightedNormal = new NativeArray<Vector3>(_nativeScratchCapacity, Allocator.Persistent);
                _nativeStrokeStart = new NativeArray<Vector3>(_nativeScratchCapacity, Allocator.Persistent);
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
            if (_nativeClayPlaneWeights.IsCreated) _nativeClayPlaneWeights.Dispose();
            if (_nativeClayWeightedPos.IsCreated) _nativeClayWeightedPos.Dispose();
            if (_nativeClayWeightedNormal.IsCreated) _nativeClayWeightedNormal.Dispose();
            if (_nativeStrokeStart.IsCreated) _nativeStrokeStart.Dispose();
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

        // Full-MESH-sized scratch (unlike every other brush's footprint-sized scratch above),
        // shared by the two Laplacian jobs - Smooth's own relaxation and Clay's surface-relax
        // pass. A relaxation needs a neighbor's position even when that
        // neighbor sits outside the current brush footprint, so it needs a way to tell "is this
        // global vertex index also one of this call's candidates" (_nativeVertexToSlot) and a
        // fallback position source for when it isn't (_nativeFullPositionMirror). Both avoid an
        // O(total vertex count) COST despite being O(total vertex count) SIZED: the slot map is
        // only ever touched at the (footprint-bounded) candidate indices - populated before the
        // job, reset back to -1 after, an O(1)-per-candidate operation - and only ALLOCATED/
        // filled with -1 once per topology change, not per call (see EnsureSmoothFullMeshScratch).
        // The position mirror IS refreshed via a full O(total) copy each Smooth call, but that's
        // a plain memcpy-like array copy, not per-vertex math - a deliberately accepted tradeoff.
        private NativeArray<int> _nativeVertexToSlot;
        private NativeArray<Vector3> _nativeFullPositionMirror;
        private int _nativeFullMeshCapacity;

        // Candidate-indexed scratch for whichever Laplacian job is running - Smooth's or Clay's
        // surface-relax pass. Shared rather than one set each because the two can never be live
        // at the same time (relax runs inside a Clay dab, Smooth is a different brush entirely),
        // and sized independently of _nativeScratchCapacity above because relax's candidate list
        // reaches 2.5x the brush radius, well past the footprint the shared brush scratch is
        // grown to. _nativeRelaxCurvature is relax-only (Smooth has no curvature gate).
        private NativeArray<int> _nativeLaplacianCandidates;
        private NativeArray<float> _nativeRelaxCurvature;
        private int _nativeLaplacianCapacity;

        private void EnsureLaplacianCandidates(int count)
        {
            // The shared brush scratch (positions/normals/mask/weights) is indexed by the same
            // candidate slots, so it has to reach at least as far.
            EnsureNativeScratch(count);
            if (_nativeLaplacianCapacity >= count && _nativeLaplacianCandidates.IsCreated) return;

            if (_nativeLaplacianCandidates.IsCreated) _nativeLaplacianCandidates.Dispose();
            if (_nativeRelaxCurvature.IsCreated) _nativeRelaxCurvature.Dispose();

            _nativeLaplacianCapacity = Mathf.Max(Mathf.NextPowerOfTwo(count), MinJobVertexCount);
            _nativeLaplacianCandidates = new NativeArray<int>(_nativeLaplacianCapacity, Allocator.Persistent);
            _nativeRelaxCurvature = new NativeArray<float>(_nativeLaplacianCapacity, Allocator.Persistent);
        }

        private void EnsureSmoothFullMeshScratch(int totalVertexCount)
        {
            if (_nativeFullMeshCapacity == totalVertexCount) return;

            if (_nativeVertexToSlot.IsCreated) _nativeVertexToSlot.Dispose();
            if (_nativeFullPositionMirror.IsCreated) _nativeFullPositionMirror.Dispose();

            _nativeFullMeshCapacity = totalVertexCount;
            _nativeVertexToSlot = new NativeArray<int>(totalVertexCount, Allocator.Persistent);
            for (int i = 0; i < totalVertexCount; i++) _nativeVertexToSlot[i] = -1; // one-time O(total) init
            _nativeFullPositionMirror = new NativeArray<Vector3>(totalVertexCount, Allocator.Persistent);
            _positionMirrorStale = true;
        }

        // Whether _nativeFullPositionMirror still describes the live vertex array. Set by every
        // path that writes a vertex (see MarkPositionMirrorStale) and once per frame up front, so
        // anything outside this component - undo, a gizmo drag, a Remesh - is covered too.
        //
        // The refresh is an O(total vertex count) copy and it used to run unconditionally on EVERY
        // Laplacian call. That is once per Clay dab, and Clay lays down up to ClayMaxDabsPerFrame
        // of them in a single frame: at a million vertices that is 12MB copied per dab, ~280MB a
        // frame, for data that had not changed between most of those dabs. Gating on "has anything
        // actually moved since the last refresh" collapses it to one copy per frame in the common
        // case while keeping the mirror exactly as fresh as before - a brush write always
        // invalidates it, so no job ever reads a stale neighbour position.
        private bool _positionMirrorStale = true;
        private Vector3[] _positionMirrorSource;

        private void MarkPositionMirrorStale() => _positionMirrorStale = true;

        /// Brings _nativeFullPositionMirror back in step with `verts`, if anything has moved since
        /// it was last refreshed. Call immediately before scheduling a job that reads it.
        private void RefreshPositionMirror(Vector3[] verts)
        {
            if (!_positionMirrorStale && ReferenceEquals(_positionMirrorSource, verts) &&
                _nativeFullMeshCapacity == verts.Length)
                return;

            NativeArray<Vector3>.Copy(verts, _nativeFullPositionMirror, verts.Length);
            _positionMirrorSource = verts;
            _positionMirrorStale = false;
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
            _positionMirrorStale = true;
            _positionMirrorSource = null;
            if (_nativeLaplacianCandidates.IsCreated) _nativeLaplacianCandidates.Dispose();
            if (_nativeRelaxCurvature.IsCreated) _nativeRelaxCurvature.Dispose();
            _nativeLaplacianCapacity = 0;
            if (_nativeRelaxCentres.IsCreated) _nativeRelaxCentres.Dispose();
            if (_nativeRelaxCentreCameras.IsCreated) _nativeRelaxCentreCameras.Dispose();
        }

        // Copies the current candidate footprint's position/normal/mask into the shared native
        // scratch (growing it first if needed) - shared gather step for every Tier-A job
        // (Inflate/Crease/DamStandard/Clay), which only ever read/write within the footprint
        // itself and never need to look outside it (unlike Smooth's neighbor lookups).
        private void GatherCandidatesNative(List<int> candidates, Vector3[] verts, Vector3[] normals, float[] mask)
        {
            int count = candidates.Count;
            EnsureNativeScratch(count);
            // The three destinations are hoisted out of the loop: a NativeArray field access goes
            // through a struct copy plus (in the Editor) a safety-handle check, and this loop runs
            // once per candidate per dab - six figures a frame under a wide brush.
            NativeArray<Vector3> positionsIn = _nativePositionsIn;
            NativeArray<Vector3> normalsIn = _nativeNormalsIn;
            NativeArray<float> maskIn = _nativeMaskIn;
            for (int ci = 0; ci < count; ci++)
            {
                int i = candidates[ci];
                positionsIn[ci] = verts[i];
                normalsIn[ci] = normals[i];
                maskIn[ci] = mask[i];
            }
        }

        // Writes job results back into the managed vertex array and rebuilds the dirty set -
        // shared scatter step for every Tier-A job. Only consumes PositionsOut where AppliedOut
        // is set, exactly mirroring each managed loop's own "continue" (skip, don't mark dirty)
        // conditions - see each job struct's remarks.
        private void ScatterJobResults(List<int> candidates, Vector3[] verts)
        {
            int count = candidates.Count;
            NativeArray<byte> applied = _nativeAppliedOut;
            NativeArray<Vector3> positionsOut = _nativePositionsOut;
            SculptableMesh mesh = sculptableMesh;

            for (int ci = 0; ci < count; ci++)
            {
                if (applied[ci] == 0) continue;
                int i = candidates[ci];
                mesh.RecordUndoBeforeIfNeeded(i);
                verts[i] = positionsOut[ci];
                _dirtyVertexScratch.Add(i);
            }
            MarkPositionMirrorStale();
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
        // their managed form - Lip defaults to 0 for plain Crease, which zeroes the leading-edge
        // term without a separate flag. AppliedOut mirrors the managed loop's own rule: ANY
        // candidate within BrushRadius counts as touched/dirty, regardless of the resulting lerp
        // factor - unlike Inflate/Clay, the carving brushes never skip on weight <= 0 alone
        // (see ApplyCarveDabLocalManaged).
        //
        // Every term here is measured from StrokeStartIn - where the stroke FOUND each vertex -
        // rather than from the dab's own tangent plane, and that is the whole shape of this
        // brush. See ApplyCarveDabLocal for why.
        [BurstCompile(CompileSynchronously = true)]
        private struct CreaseJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> PositionsIn;
            // Was gathered-but-unread before Front Facing Only existed (GatherCandidatesNative
            // always populates all three arrays regardless of brush) - now actually read below.
            [ReadOnly] public NativeArray<Vector3> NormalsIn;
            [ReadOnly] public NativeArray<float> MaskIn;
            [ReadOnly] public NativeArray<Vector3> StrokeStartIn;
            public NativeArray<Vector3> PositionsOut;
            public NativeArray<byte> AppliedOut;

            public Vector3 LocalPoint;
            public Vector3 LocalNormal;
            public Vector3 DirLocal; // stroke travel direction in the tangent plane; zero on a tap
            public float BrushRadius;
            public float Depth;
            public float Lip; // 0 for plain Crease
            public float Pinch;
            public float Sign;
            public float LerpFactorScale; // brushStrength * CreaseSpeed * dabDt
            public bool Accumulate;
            public float DepthRate; // sign * creaseDepthFactor * brushStrength * CreaseSpeed * dabDt
            public float LipRate; // 0 for plain Crease
            public float PinchRateScale; // creasePinch * brushStrength * CreaseSpeed * dabDt
            public bool FrontFacingOnly;
            public Vector3 CameraLocalPos;

            public void Execute(int index)
            {
                Vector3 pos = PositionsIn[index];
                Vector3 toVert = pos - LocalPoint;
                float dist = toVert.magnitude;
                if (dist > BrushRadius) { AppliedOut[index] = 0; return; }

                float weight = CarveFalloff(1f - dist / BrushRadius) * (1f - MaskIn[index])
                    * FrontFacingWeight(FrontFacingOnly, NormalsIn[index], pos, CameraLocalPos);

                Vector3 start = StrokeStartIn[index];
                SplitCarveFrame(start - LocalPoint, LocalNormal, DirLocal,
                    out float startNormal, out float startAlong, out Vector3 startAcross);
                bool hasLip = startAlong > 0f;

                if (Accumulate)
                {
                    float normalRate = DepthRate;
                    if (hasLip) normalRate += LipRate;
                    // Pinch pulls across the stroke line only - see ApplyCarveDabLocalManaged.
                    SplitCarveFrame(toVert, LocalNormal, DirLocal, out _, out _, out Vector3 across);
                    Vector3 pinchDelta = -across * Mathf.Clamp01(weight * PinchRateScale);
                    PositionsOut[index] = pos + LocalNormal * (normalRate * weight) + pinchDelta;
                }
                else
                {
                    float carve = Depth * weight;
                    if (hasLip) carve += Lip * weight;
                    float achieved = Vector3.Dot(pos - start, LocalNormal);
                    if (Sign * achieved > Sign * carve) carve = achieved;

                    Vector3 pinched = startAcross * (1f - Pinch * weight);
                    SplitCarveFrame(toVert, LocalNormal, DirLocal, out _, out _, out Vector3 across);
                    if (across.sqrMagnitude < pinched.sqrMagnitude) pinched = across;

                    Vector3 target = LocalPoint + LocalNormal * (startNormal + carve)
                        + DirLocal * startAlong + pinched;
                    float lerp = Mathf.Clamp01(weight * LerpFactorScale);
                    PositionsOut[index] = pos + (target - pos) * lerp;
                }
                AppliedOut[index] = 1;
            }
        }

        /// Crease/Dam Standard's radial profile. Was t01^3, which is a CONE: its slope is
        /// steepest exactly at the tip, so every dab left a pointed dimple and a line of them
        /// read as a row of pokes rather than one groove. Cubing a smoothstep instead keeps the
        /// same overall narrowness (both are 1/8 at half radius, so existing Crease Depth
        /// settings still feel the same) while flattening the slope to zero at BOTH ends - a
        /// rounded valley floor that neighbouring dabs blend into continuously.
        /// Plain float math so Burst inlines it, same as ClayFalloff.
        private static float CarveFalloff(float t01)
        {
            float s = t01 * t01 * (3f - 2f * t01);
            return s * s * s;
        }

        /// Decomposes an offset from the dab centre into the carve frame: along the carve
        /// normal, along the stroke's travel direction, and across it. The across component is
        /// the only one Crease's pinch is allowed to touch - see ApplyCarveDabLocalManaged.
        /// A zero dir (a tap, or the stroke's first dab) collapses `along` to 0 and leaves the
        /// whole tangential offset in `across`, which is the old radial pinch exactly.
        private static void SplitCarveFrame(Vector3 offset, Vector3 normal, Vector3 dir,
            out float alongNormal, out float alongDir, out Vector3 across)
        {
            alongNormal = Vector3.Dot(offset, normal);
            Vector3 tangential = offset - normal * alongNormal;
            alongDir = Vector3.Dot(tangential, dir);
            across = tangential - dir * alongDir;
        }

        // Shared by every brush's weight computation, multiplied in alongside the mask term
        // right next to it (MaskIn / sculptableMesh.Mask) - see frontFacingOnly's remarks for
        // what this is for. A vertex counts as front-facing when its OWN mesh normal points at
        // least partly back toward the camera; compared per-vertex against the camera's actual
        // local-space position rather than one shared view direction, so the test stays correct
        // up close, where a sculpt's own scale can be comparable to the camera's distance from
        // it. Plain float/Vector3 math (like ClayFalloff below), so Burst inlines it into a
        // job's Execute exactly the same way.
        /// How wide the accept/reject transition is, as the cosine of the angle between a vertex's
        /// normal and its own direction to the camera - so ~11.5 degrees either side of edge-on.
        ///
        /// The gate used to be a bare `dot > 0`, which is a 0/1 step, and a step is what made this
        /// option visibly chew up thin, strongly-curved geometry - an ear above all. Adjacent
        /// vertices on a remeshed surface do not share a normal (Surface Nets places one vertex per
        /// cell, so the output is genuinely bumpy at the cell scale - see
        /// SculptableMesh.EncodeCavityAt), so wherever the silhouette runs through a footprint the
        /// step hands neighbouring vertices full strength and none at all. That is a sawtooth
        /// written straight into the surface, and it reads exactly as one ear coming out jagged and
        /// faceted while the other is smooth.
        ///
        /// A ramp costs the option nothing it is actually for. A vertex on the FAR wall of a fin
        /// points away from the camera, lands at cosine <= 0, and is still rejected outright; only
        /// the sliver of surface within a few degrees of edge-on - where "is this the near wall or
        /// the far one" genuinely has no sharp answer - gets a partial weight instead of a coin
        /// flip.
        private const float SilhouetteBand = 0.2f;

        // Shared by every brush's weight computation, multiplied in alongside the mask term
        // right next to it (MaskIn / sculptableMesh.Mask) - see frontFacingOnly's remarks for
        // what this is for. A vertex counts as front-facing when its OWN mesh normal points at
        // least partly back toward the camera; compared per-vertex against the camera's actual
        // local-space position rather than one shared view direction, so the test stays correct
        // up close, where a sculpt's own scale can be comparable to the camera's distance from
        // it. Plain float/Vector3 math (like ClayFalloff below), so Burst inlines it into a
        // job's Execute exactly the same way.
        //
        // cameraLocalPos is the CURRENT DAB's viewpoint, which for a mirrored dab is the reflected
        // camera - see SculptController._dabCameraLocal.
        // internal, not private: SculptableMesh.SelectGrab needs the identical rule (Move picks its
        // vertex set once on mouse-down instead of running a per-frame weight loop), and a second
        // copy of a silhouette ramp is exactly the kind of thing that drifts out of step.
        internal static float FrontFacingWeight(bool frontFacingOnly, Vector3 normalLocal, Vector3 posLocal, Vector3 cameraLocalPos)
        {
            if (!frontFacingOnly) return 1f;

            Vector3 toCamera = cameraLocalPos - posLocal;
            float d = Vector3.Dot(normalLocal, toCamera);
            if (d <= 0f) return 0f; // facing away - rejected exactly as before

            // Comparing squared keeps the square root off the overwhelming majority of candidates:
            // anything comfortably front-facing clears this without one, and only the narrow
            // silhouette band pays for the cosine it actually needs. (Squaring is monotonic here
            // because d > 0 at this point.)
            float bandSqr = SilhouetteBand * SilhouetteBand * toCamera.sqrMagnitude;
            if (d * d >= bandSqr) return 1f;

            float cos = d / Mathf.Sqrt(toCamera.sqrMagnitude);
            float t = cos / SilhouetteBand;
            return t * t * (3f - 2f * t); // smoothstep
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
            // Quintic ("smootherstep") rather than the cubic smoothstep this used to be: both
            // run 0->1 across the same band and agree at the ends and the midpoint, but plain
            // smoothstep still has a nonzero SECOND derivative at e=0/1, which reads as a faint
            // crease exactly where the taper meets the flat plateau/the zero rim - most visible
            // where two dabs' edge bands overlap. Zeroing that too (Perlin's 6e^5-15e^4+10e^3)
            // is what actually reads as "soft" rather than merely "not a hard line".
            return e * e * e * (e * (e * 6f - 15f) + 10f);
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
            // The displacement weight's mask-free twin, which is what the area-plane reduction sums
            // - see Execute.
            public NativeArray<float> PlaneWeightsOut;

            public void Execute(int index)
            {
                Vector3 pos = PositionsIn[index];
                Vector3 toVert = pos - LocalPoint;
                float t01 = ClayTipShapeT01(toVert, BrushRadius, Tangent0, Bitangent0, TipRoundness);
                if (t01 <= 0f)
                {
                    WeightsOut[index] = 0f;
                    PlaneWeightsOut[index] = 0f;
                    WeightedPosOut[index] = Vector3.zero;
                    WeightedNormalOut[index] = Vector3.zero;
                    return;
                }

                // TWO weights, and the difference between them is the whole point of the split.
                //
                // The plane weight has NO mask term, because the area plane is a measurement of the
                // surface the brush is standing on, and the mask says which vertices may MOVE, not
                // which ones the surface is made of. Folding the mask in fits the plane to whatever
                // sliver of the footprint happens to be unmasked, which drags its origin out to the
                // rim and swings its normal round with it: measured on a 145k-triangle sculpt at a
                // 0.15 radius, a hard mask edge sweeping across the footprint tilted the plane 19
                // degrees at a third covered, 34 degrees at half, and 58 degrees at five sixths,
                // with the plane's height under the brush centre dropping 8%, 18% and 44% of a brush
                // radius respectively. Clay and Flatten then push every unmasked vertex onto THAT
                // plane, so a stroke run alongside a mask lifts a ridge that follows the mask's
                // outline - the reported "weird raised surface around the mask area".
                //
                // Front Facing Only stays in, and deliberately: unlike the mask it IS a statement
                // about which surface this stroke is on (the near wall of a fin, not the far one),
                // so a plane measured across both walls would be the wrong surface.
                float planeW = ClayFalloff(t01, EdgeSoftness)
                    * FrontFacingWeight(FrontFacingOnly, NormalsIn[index], pos, CameraLocalPos);
                PlaneWeightsOut[index] = planeW;
                WeightsOut[index] = planeW * (1f - MaskIn[index]);
                WeightedPosOut[index] = pos * planeW;
                WeightedNormalOut[index] = NormalsIn[index] * planeW;
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
        // visually/numerically reasonable result: the two paths differed by at most ~1e-4 even at
        // maximum strength (10 full passes).
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

        /// Clay's surface-relax weights (see ApplySurfaceRelaxBatched for what each term means and
        /// why it is shaped this way) - the same formula the managed setup loop computes, moved
        /// into a job because it runs over a candidate list 2.5x the brush radius wide, i.e.
        /// roughly six times the surface area of the dab it is supporting.
        ///
        /// The shell profile is measured from the NEAREST of the frame's dab centres rather than
        /// from one point, because relax now runs once per frame across every dab that frame
        /// placed rather than once per dab - see ApplySurfaceRelaxBatched. That is the exact
        /// generalisation of the single-centre profile: with one centre it reduces to it
        /// identically, and with several it keeps RelaxInnerFloor over the whole swept path (the
        /// region the user is actively shaping, which relax deliberately leaves alone) while the
        /// full-strength shell forms around the outside of the sweep, which is where a seam with
        /// neighbouring geometry actually is.
        [BurstCompile(CompileSynchronously = true)]
        private struct RelaxWeightJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> PositionsIn;
            [ReadOnly] public NativeArray<Vector3> NormalsIn;
            [ReadOnly] public NativeArray<float> MaskIn;
            [ReadOnly] public NativeArray<float> CurvatureIn; // CurvatureDeviationAt, candidate-indexed
            [ReadOnly] public NativeArray<Vector3> Centres; // this frame's dab centres, all mirror signs
            [ReadOnly] public NativeArray<Vector3> CentreCameras; // one per centre - see _relaxCentreCameras
            public NativeArray<float> WeightsOut;
            public int CentreCount;
            public float BrushRadius;
            public float RelaxRadius;
            public float EdgeSoftness;
            public float InnerFloor;
            public float CurvatureFloor, CurvatureStart, CurvatureFull;
            public bool FrontFacingOnly;
            public Vector3 CameraLocalPos;

            public void Execute(int ci)
            {
                Vector3 p = PositionsIn[ci];
                // Compared squared - the managed loop's Vector3.Distance took a square root for
                // every candidate purely to throw over half of them away (measured: 53,057 of
                // 101,771 at a 0.25 radius fall outside the relax radius entirely). With several
                // centres the square root is now taken at most once per candidate rather than once
                // per centre, for the same reason.
                float sqrDist = float.MaxValue;
                int nearest = 0;
                for (int c = 0; c < CentreCount; c++)
                {
                    float d = (p - Centres[c]).sqrMagnitude;
                    if (d < sqrDist) { sqrDist = d; nearest = c; }
                }
                if (sqrDist > RelaxRadius * RelaxRadius) { WeightsOut[ci] = 0f; return; }

                float dist = Mathf.Sqrt(sqrDist);
                float spatialWeight;
                if (dist <= BrushRadius)
                {
                    spatialWeight = InnerFloor;
                }
                else
                {
                    float shellT = (dist - BrushRadius) / Mathf.Max(RelaxRadius - BrushRadius, 1e-5f);
                    spatialWeight = ClayFalloff(1f - shellT, EdgeSoftness);
                }

                float curvatureFactor = Mathf.Lerp(CurvatureFloor, 1f,
                    Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(CurvatureStart, CurvatureFull, CurvatureIn[ci])));

                // Judged from the viewpoint of the dab that placed the nearest centre, not from one
                // shared camera - see _relaxCentreCameras.
                WeightsOut[ci] = spatialWeight * curvatureFactor * (1f - MaskIn[ci])
                    * FrontFacingWeight(FrontFacingOnly, NormalsIn[ci], p, CentreCameras[nearest]);
            }
        }

        /// The Laplacian pass itself, structurally identical to SmoothRelaxJob (see its remarks
        /// on the Jacobi-vs-Gauss-Seidel substitution parallelism requires, which applies here
        /// for the same reason) - it differs only in the blend factor, which for relax is the
        /// weight directly rather than a strength-and-dt-scaled lerp.
        [BurstCompile(CompileSynchronously = true)]
        private struct SurfaceRelaxJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int> Candidates;
            [ReadOnly] public NativeArray<int> AdjacencyOffsets;
            [ReadOnly] public NativeArray<int> AdjacencyNeighbors;
            [ReadOnly] public NativeArray<int> VertexToSlot;
            [ReadOnly] public NativeArray<Vector3> FullPositions;
            [ReadOnly] public NativeArray<Vector3> PositionsRead;
            public NativeArray<Vector3> PositionsWrite;
            [ReadOnly] public NativeArray<float> Weights;
            public float PassFactor;

            public void Execute(int ci)
            {
                Vector3 currentPos = PositionsRead[ci];
                float w = Weights[ci];
                if (w <= 0f) { PositionsWrite[ci] = currentPos; return; }

                int globalIdx = Candidates[ci];
                int start = AdjacencyOffsets[globalIdx];
                int end = AdjacencyOffsets[globalIdx + 1];
                if (end == start) { PositionsWrite[ci] = currentPos; return; }

                Vector3 sum = Vector3.zero;
                for (int n = start; n < end; n++)
                {
                    int neighborGlobal = AdjacencyNeighbors[n];
                    int slot = VertexToSlot[neighborGlobal];
                    sum += slot >= 0 ? PositionsRead[slot] : FullPositions[neighborGlobal];
                }

                Vector3 toAverage = sum / (end - start) - currentPos;
                PositionsWrite[ci] = currentPos + toAverage * Mathf.Clamp01(w * PassFactor);
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

        // Clay's own pressure-shaped footprint size - see clayPressureRadiusInfluence's remarks.
        // At CurrentPressure==1 (a firm press, or no pen at all - mouse users always read 1 here)
        // this returns exactly brushRadius, so the radius math below is a strict no-op until a
        // pen actually lifts off full pressure.
        private float EffectiveClayRadius =>
            brushRadius * Mathf.Lerp(1f - clayPressureRadiusInfluence, 1f, CurrentPressure);

        // Clay's own pressure-shaped edge softness - see clayPressureSoftnessInfluence's remarks.
        // Same CurrentPressure==1 no-op as EffectiveClayRadius above. Clamped to clayEdgeSoftness's
        // own [0.05, 1] range for the same NaN-avoidance reason ClayFalloff's own Max() guards.
        private float EffectiveClayEdgeSoftness => Mathf.Clamp(
            clayEdgeSoftness * Mathf.Lerp(1f + clayPressureSoftnessInfluence, 1f, CurrentPressure),
            0.05f, 1f);

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
        // Only Inflate is left on this path: Crease and Dam Standard have since moved to real
        // distance-spaced dabs (ApplyCarveStroke), which is what this factor was approximating.
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
        /// worth of depth and stops - as used by Inflate. Crease and Dam Standard used it too
        /// until they moved to distance-spaced dabs (see ApplyCarveStroke) and took Clay's
        /// speed-free pacing with them - see EffectiveCarveStrength.
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

        /// Crease/Dam Standard's equivalents of the two above, and speed-free for exactly the
        /// same reason: those brushes now place a fixed quantum of carve every CreaseDabSpacing
        /// of travel (see ApplyCarveStroke) rather than a slice of each frame's time, so the
        /// deposit per centimetre is already stroke-speed-invariant. AccumulateSpeedFactor on
        /// top of that would double-count speed. Build Up on Hold is honoured by feeding the
        /// stepper virtual travel while the cursor sits still, not by scaling these.
        private float EffectiveCarveStrength => EffectiveBrushStrength * accumulateStrength;
        private float EffectiveCarveStrengthAccumulate => brushStrength * Mathf.Lerp(1f, CurrentPressure, AccumulatePressureInfluence) * accumulateStrength;

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
        // The crosshair drawn in place of the ring while a region gesture is armed - see
        // UpdateBrushCursor for why the ring alone left the viewport looking cursorless.
        private bool _showRegionCrosshair;
        private Vector2 _regionCrosshairScreenPos;
        public bool ShowRegionCrosshair => _showRegionCrosshair;
        public Vector2 RegionCrosshairScreenPosition => _regionCrosshairScreenPos;

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

        /// The mirror sign list for the CURRENT target, off the reference SyncSelectionTarget
        /// already resolved once this frame.
        ///
        /// Every brush apply site used to reach this through the Mirror property, which resolves
        /// Target (a scene-manager lookup) and does a GetComponent - once per DAB, and Clay/Crease
        /// place up to ClayMaxDabsPerFrame/CreaseMaxDabsPerFrame of those in a single frame. The
        /// property's self-healing AddComponent path is still the fallback for a target that
        /// genuinely has no MirrorController, and the result is written back to the cached field so
        /// that only ever happens once per such target rather than once per dab.
        private List<Vector3> MirrorSigns()
        {
            if (mirrorController == null) mirrorController = Mirror;
            return mirrorController != null ? mirrorController.GetMirrorSigns() : IdentityMirrorSigns;
        }

        // Stand-in for a target with no MirrorController at all (Mirror returns null only when
        // there is no target). One unmirrored stroke, which is what "no mirroring" means.
        private static readonly List<Vector3> IdentityMirrorSigns = new List<Vector3> { Vector3.one };

        /// The camera's local-space position, reflected through the same mirror plane(s) as the dab
        /// currently being applied. Every brush's mirror loop sets it once per sign (see
        /// BeginMirroredDab) and every Front Facing Only test downstream reads it instead of
        /// re-deriving the raw camera position, so a mirrored dab is judged from the mirrored
        /// viewpoint. Identity sign leaves it as the real camera, which is what an unmirrored
        /// session sees.
        ///
        /// Front Facing Only asks "is this vertex facing the viewer", and a MIRRORED dab is not
        /// being viewed from where the real camera is - it is the same stroke seen from the mirrored
        /// viewpoint. Testing it against the unmirrored camera throws away roughly half of every
        /// mirrored footprint the moment the model is turned off dead-centre. Measured on a
        /// 145k-triangle sculpt with Mirror X on and the camera swept around it: at 0 degrees the
        /// two footprints match (639 of 1280 against 635 of 1281), but at 30 degrees the near side
        /// keeps 649 of 1166 while the far side keeps 350 of 1161, and the deficit holds at roughly
        /// 2:1 across the whole orbit, closing again only at a dead-on 180. That is one half of a
        /// symmetric model quietly receiving twice the material of the other for a whole session -
        /// the reported "one ear was taking detail and the other was not" - and because the test is
        /// a hard 0/1 cut rather than a ramp, the surviving part of the far footprint is stamped in
        /// with a sharp edge straight through it, which is an artifact appearing on the side you are
        /// NOT working on. Reflecting the camera with the dab restores parity (measured 776 against
        /// the near side's 780 at 60 degrees) and costs the option nothing: each dab still rejects
        /// everything facing away from its OWN viewpoint, which is the whole point of the setting.
        private Vector3 _dabCameraLocal;

        /// Points the per-dab frame at one mirror sign. Call once per sign, before applying that
        /// sign's dab - the mirror loops below all do, including the identity sign, so no apply path
        /// can read a viewpoint left behind by the previous dab.
        private void BeginMirroredDab(Vector3 sign) =>
            _dabCameraLocal = Vector3.Scale(
                sculptableMesh.transform.InverseTransformPoint(cam.transform.position), sign);

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
                // Runs BEFORE EndStrokeUndo, not after: this pass's own vertex moves have to land
                // inside the SAME accumulated undo delta as the rest of the stroke, so one Undo
                // reverts the whole thing (the stroke plus its unify pass) rather than needing two.
                ApplyPostStrokeUnifyPass();
                sculptableMesh.EndStrokeUndo();
                _strokeEndFadeTimer = StrokeEndFadeDuration;
            }
        }

        // How much of the gap toward each vertex's neighbor average ApplyPostStrokeUnifyPass
        // closes in its one pass. Deliberately small and fixed, not an iteration ramp like
        // Smooth's own MaxSmoothIterations - this isn't a tool the user reaches for, it's one
        // quiet pass that runs automatically on every stroke release, so it has to stay far too
        // gentle to read as "the surface got smoothed" on its own; it only has to blend away the
        // faint facet where one frame's dab meets the next; see the method's own remarks.
        private const float PostStrokeUnifyAmount = 0.35f;

        // Candidate-indexed scratch for the pass below - reused across strokes rather than
        // reallocated, same reasoning as _clayWeightScratch/_smoothWeightScratch.
        private float[] _postStrokeUnifyWeightScratch = System.Array.Empty<float>();

        /// Runs once, when a stroke ends (see HandleStrokeEndCommit), over every vertex ANY brush
        /// moved during the whole stroke - not just Clay's own per-frame Surface Relax above,
        /// which only ever sees one frame's dab footprint and only ever rides along on Clay.
        /// Consecutive dabs (of any brush, Clay's flat-topped plateau most of all) leave a faint
        /// facet where one frame's geometry meets the next; one gentle, curvature-gated Laplacian
        /// pass over the whole stroke's footprint blends those together into one continuous
        /// surface, "unifying" it the way a real sculptor's hand settles clay after a pass.
        ///
        /// Curvature-gated with the exact same shape (and the exact same calibrated constants -
        /// see their own remarks) as Clay's Surface Relax above: a vertex whose curvature already
        /// departs sharply from the mesh's own baseline (a genuine seam/pinch) gets real
        /// correction, while an ordinary rounded feature - which measures close to baseline by
        /// this same test - is mostly left alone. That is what keeps this "without removing
        /// detail" rather than sanding the whole stroke smooth.
        ///
        /// Deliberately plain managed C#, not a Burst job: unlike Surface Relax, which reruns
        /// every frame of a held stroke, this runs exactly once, at release - even a wide stroke's
        /// full touched-vertex set is a one-off cost at that point, not a per-frame one.
        private void ApplyPostStrokeUnifyPass()
        {
            if (sculptableMesh == null) return;
            List<int> touched = _strokeDirtyVertexScratch.Items;
            if (touched.Count == 0) return;

            SculptableMesh mesh = sculptableMesh;
            Vector3[] verts = mesh.Vertices;
            float[] mask = mesh.Mask;

            if (_postStrokeUnifyWeightScratch.Length < touched.Count)
                _postStrokeUnifyWeightScratch = new float[touched.Count];
            float[] weights = _postStrokeUnifyWeightScratch;

            bool anyInRange = false;
            for (int ci = 0; ci < touched.Count; ci++)
            {
                int i = touched[ci];
                // A Remesh mid-stroke resizes the mesh out from under indices gathered on earlier
                // frames - see DirtyVertexSet's own remarks on the same hazard.
                if ((uint)i >= (uint)verts.Length) { weights[ci] = 0f; continue; }

                float curvatureDeviation = mesh.CurvatureDeviationAt(i);
                float curvatureFactor = Mathf.Lerp(RelaxCurvatureFloor, 1f,
                    Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(RelaxCurvatureStart, RelaxCurvatureFull, curvatureDeviation)));
                float w = curvatureFactor * (1f - mask[i]);
                weights[ci] = w;
                if (w > 0f) anyInRange = true;
            }
            if (!anyInRange) return;

            // Reuses the per-frame scratch set as plain scratch here (not through
            // BeginDirtyVertices/FlushDirtyVertices - this pass's own results must NOT fold back
            // into _strokeDirtyVertexScratch, which is about to be cleared by the next stroke's
            // mouse-down and has no reason to remember this pass's touches beyond that).
            _dirtyVertexScratch.Clear(verts.Length);
            bool anyMoved = false;
            for (int ci = 0; ci < touched.Count; ci++)
            {
                float w = weights[ci];
                if (w <= 0f) continue;
                int i = touched[ci];

                Vector3 toAverage = mesh.GetNeighborAverage(i) - verts[i];
                mesh.RecordUndoBeforeIfNeeded(i);
                verts[i] += toAverage * (w * PostStrokeUnifyAmount);
                anyMoved = true;
                _dirtyVertexScratch.Add(i);
            }

            if (!anyMoved) return;
            MarkPositionMirrorStale();
            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch.Items);
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
            if (kb == null || _isResizingBrush || _isAdjustingStrength || _isAdjustingRemeshDensity) return;
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

        // Ctrl+S / Ctrl+Shift+S, matching the quick-save/save-as split most creative software
        // uses (see SceneGraphUIBuilder.Save/SaveAs). Routed via SendMessage rather than a
        // direct reference - same "invoke a private MonoBehaviour method without reflection"
        // idiom RebuildOtherPanels already uses - because this controller has no other reason
        // to depend on the scene-file UI panel.
        private void HandleSaveKeys()
        {
            var kb = Keyboard.current;
            if (kb == null || !CtrlHeld) return;
            if (!kb.sKey.wasPressedThisFrame) return;

            bool saveAs = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            if (_sceneGraphForSave == null) _sceneGraphForSave = FindFirstObjectByType<SceneGraphUIBuilder>();
            if (_sceneGraphForSave == null) return;

            _sceneGraphForSave.gameObject.SendMessage(saveAs ? "SaveAs" : "Save", SendMessageOptions.DontRequireReceiver);
        }

        // Only ever looked up on a frame Ctrl+S is actually pressed, so the find costs nothing
        // otherwise - same reasoning as _zsphereForUndo above.
        private SceneGraphUIBuilder _sceneGraphForSave;

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
            else if (kb.digit8Key.wasPressedThisFrame) CurrentBrush = BrushType.Pose;

            // M used to trigger Remesh directly; moved to R (still reachable via the Remesh
            // button in the Brush panel either way, or a plain R tap - see
            // HandleRemeshDensityKey) so M is free for the mask-paint toggle, matching most
            // sculpting apps' M-for-mask convention.
            if (kb.mKey.wasPressedThisFrame) IsMaskPaintMode = !IsMaskPaintMode;

            // X toggles Mirror X on the selected object, Blender/ZBrush-style. Mirror resolves
            // against the live selection (see its getter) and self-heals a missing
            // MirrorController, so this is safe the moment anything is selected.
            if (kb.xKey.wasPressedThisFrame)
            {
                MirrorController mirror = Mirror;
                if (mirror != null) mirror.MirrorX = !mirror.MirrorX;
            }
        }

        // Holding Shift temporarily switches to the Smooth brush, ZBrush/Blender-style,
        // reverting to whatever brush was active the moment Shift is released - lets you
        // smooth out a stroke without breaking flow to switch brushes and back. Guarded off
        // during the resize gauge for the same reason other input handlers are.
        private void HandleShiftSmoothOverride(Keyboard kb)
        {
            if (_isResizingBrush || _isAdjustingStrength || _isAdjustingRemeshDensity) return;
            bool shiftHeld = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            // Shift means "act on everything outside the region" while a region gesture is armed
            // (see RegionSelectTool), so it must not also swap the brush underneath it - that
            // would repaint the brush row and leave the panel highlighting Smooth for the whole
            // gesture. The release branch is deliberately still reachable, so an override that
            // was already running when the mode was armed still gets unwound.
            if (shiftHeld && !_isShiftSmoothActive && RegionSelectActive) return;

            // Same shape of guard for mask painting, and needed twice over: the brush is not used
            // at all while masking (the click paints mask - see HandleMaskPaintInput), so swapping
            // it under Shift only mislabels the panel; and selecting a brush now leaves mask paint
            // mode (see CurrentBrush's setter), so without this a stray Shift would silently drop
            // the user out of masking mid-selection. As above, the release branch stays reachable so
            // an override already running when masking was entered still unwinds.
            if (shiftHeld && !_isShiftSmoothActive && _isMaskPaintMode) return;

            if (shiftHeld && !_isShiftSmoothActive)
            {
                _preShiftBrush = currentBrush;
                _isShiftSmoothActive = true;
                CurrentBrush = BrushType.Smooth;
            }
            else if (!shiftHeld && _isShiftSmoothActive)
            {
                _isShiftSmoothActive = false;
                // Unwinding a held modifier is not the user choosing a tool, so it must not carry
                // that choice's side effect of leaving mask paint mode (see CurrentBrush's setter).
                // The guard above keeps the override from starting while masking, but the Mask
                // BUTTON is still clickable mid-hold, and dropping the mode on the next Shift
                // release would look like the button simply failed.
                bool maskWasOn = _isMaskPaintMode;
                CurrentBrush = _preShiftBrush;
                IsMaskPaintMode = maskWasOn;
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

            // CtrlHeld excluded: Ctrl+S is the save hotkey (see HandleSaveKeys) and must not
            // also drop the brush into resize mode.
            if (kb.sKey.wasPressedThisFrame && !CtrlHeld)
            {
                EndActiveDrags(); // don't leave a grab mid-drag while resizing
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
                EndActiveDrags(); // don't leave a grab mid-drag while adjusting strength
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

        // R is dual-purpose: a plain tap still fires the old immediate Remesh() at whatever
        // resolution is already set, but holding it past RemeshHoldThreshold instead arms a
        // density gauge - same S/F-drag feel as the two handlers above (horizontal mouse
        // movement scrubs the value), except the changed value isn't applied to the geometry
        // live. Remeshing is too expensive to run on every mouse-move frame of a drag (see
        // MeshRemesher's own hitch-at-high-resolution remarks), so the drag only scrubs
        // RemeshResolution and the DensityGrid preview; the actual rebuild happens once, on
        // release.
        private void HandleRemeshDensityKey()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (kb.rKey.wasPressedThisFrame)
            {
                _rKeyDownTime = Time.unscaledTime;
            }
            else if (_rKeyDownTime >= 0f && kb.rKey.isPressed && !_isAdjustingRemeshDensity
                     && !_isResizingBrush && !_isAdjustingStrength
                     && Time.unscaledTime - _rKeyDownTime >= RemeshHoldThreshold)
            {
                EndActiveDrags(); // don't leave a grab mid-drag while adjusting density
                _isAdjustingRemeshDensity = true;
                _remeshDensityStartValue = remeshResolution;
                _remeshDensityStartMouseX = mouse.position.ReadValue().x;
            }
            else if (_rKeyDownTime >= 0f && !kb.rKey.isPressed)
            {
                // A tap that never crossed the hold threshold keeps the pre-existing shortcut
                // (Remesh at whatever resolution was already set); a completed drag commits the
                // gauge's final value the same way the Remesh button does. Same call either way.
                _isAdjustingRemeshDensity = false;
                _rKeyDownTime = -1f;
                Remesh();
            }

            if (!_isAdjustingRemeshDensity) return;

            float deltaX = mouse.position.ReadValue().x - _remeshDensityStartMouseX;
            RemeshResolution = Mathf.RoundToInt(_remeshDensityStartValue + deltaX * RemeshDensityDragSensitivity);
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
            IsHoveringSculptSurface = mouse != null && _isHovering && !_isOverUI
                && !_isResizingBrush && !_isAdjustingStrength && !_isAdjustingRemeshDensity;
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

            // Shares SelectionManager.Raycast with TransformGizmo's own click-to-select rather
            // than keeping a second copy of the same visible-objects-only hit test.
            SculptableMesh closest = Selection.Raycast(cam.ScreenPointToRay(screenPos));

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
                sculptableMesh.PrepareSpatialIndex(Mathf.Max(brushRadius * 0.5f, 0.01f));
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

            foreach (Vector3 sign in MirrorSigns())
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

            float spacing = Mathf.Max(brushRadius * ClayDabSpacingFraction, 0.0005f);
            // One dab = one fixed quantum of material, NOT a slice of this frame's time.
            float dabDt = spacing / ClayReferenceStrokeSpeed;

            // One dirty set and one relax batch for the WHOLE frame, however many dabs it turns
            // out to place - see FlushClayFrame.
            BeginDirtyVertices();
            BeginRelaxBatch();

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

            FlushClayFrame();

            _lastClayStrokeLocal = localPoint;
            _lastClayStrokeNormalLocal = localNormal;
        }

        /// Closes out a frame's worth of Clay dabs: one relaxation pass across everything they
        /// touched, then one push into the mesh.
        ///
        /// Both of those used to run per DAB, and a single frame of a moving stroke places several
        /// (up to ClayMaxDabsPerFrame). Every one of them paid a full ApplyVerticesLocal - a
        /// normal recompute, a cavity recompute, a triangle re-bucket and four GPU buffer uploads
        /// plus a compute dispatch - over footprints that overlap each other by around 90%, so
        /// almost all of that work was being redone on the same vertices within one frame. Folding
        /// it into the frame's union does each vertex once, which is where most of the "the surface
        /// lags behind the cursor on a wide stroke" latency was going.
        private void FlushClayFrame()
        {
            ApplySurfaceRelaxBatched();
            FlushDirtyVertices();
        }

        private void ApplyClayBrushAtLocal(Vector3 localPoint, Vector3 localNormal, bool positive, float dt)
        {
            int centresBefore = _relaxCentres.Count;
            foreach (Vector3 sign in MirrorSigns())
            {
                BeginMirroredDab(sign);
                Vector3 mirroredPoint = Vector3.Scale(localPoint, sign);
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                // Mirror the frozen stroke tangent frame the same way the point/normal are
                // mirrored, instead of rebuilding it from the mirrored normal - keeps a
                // mirrored stroke's square exactly as stable as the primary one.
                Vector3 mirroredTangent0 = Vector3.Scale(_clayStrokeTangent0, sign);
                Vector3 mirroredBitangent0 = Vector3.Scale(_clayStrokeBitangent0, sign);
                ApplyClayBrushLocal(mirroredPoint, mirroredNormal, mirroredTangent0, mirroredBitangent0, positive, dt);
            }

            // Counted only if the dab actually landed on geometry (ApplyClayBrushLocal records a
            // centre exactly then), so a stroke that runs off the edge of the mesh doesn't inflate
            // the frame's relaxation budget with dabs that deposited nothing.
            if (_relaxCentres.Count > centresBefore) _relaxDabCount++;
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

        // v1 of ApplySurfaceRelaxLocal reused the calling brush's OWN candidate list and its
        // OWN brushRadius-sized falloff - which weighted relax by distance from THIS dab's
        // centre. That put the WEAKEST relax exactly at the dab's own edge, which is exactly
        // where a neighboring dab's geometry begins - i.e. exactly where a seam pinches.
        // Verified live: two lobes sculpted right next to each other still pinched hard with
        // surfaceRelax at its default 0.35. Reaching further than the dab itself (into the
        // neighbor's territory) fixed that half of it.
        //
        // v2/v3 (this version's predecessor) then applied near-full strength across almost the
        // WHOLE reach, including the brush's own core - which fixed the seam but broke the
        // brush itself: user-reported, verified live, Crease's own groove couldn't hold shape
        // and Clay's own accumulate-mode buildup was net LOSING height every dab (relax pulling
        // a freshly-raised peak back down toward its still-low neighbors faster than Clay could
        // raise it - eventually undershooting past the start height entirely on a held stroke).
        // Both are the SAME bug: relax was second-guessing the brush's own CURRENT dab, not
        // just the seam around it. RelaxInnerFloor is the fix - inside the brush's own radius
        // (dist <= brushRadius) is exactly where the user is actively shaping RIGHT NOW, sharp
        // or not, and gets left alone; only the SHELL beyond it (out to relaxRadius) - the
        // transition into whatever geometry was already there before this dab, which is where a
        // seam with a NEIGHBORING dab actually forms - gets meaningful relax.
        private const float RelaxRadiusFactor = 2.5f;
        private const float RelaxEdgeSoftness = 0.35f;
        private const float RelaxInnerFloor = 0.05f;
        // Max relaxation passes surfaceRelax=1 folds into one dab - same "iteration count IS
        // the strength knob" idea Smooth's own MaxSmoothIterations uses (a single pass is too
        // weak to visibly close a seam within one ordinary stroke), just a smaller ceiling
        // since this rides along on top of each brush's own displacement rather than being
        // the primary tool.
        private const int MaxRelaxIterations = 4;

        // How much a vertex's ALREADY-EXISTING curvature (see
        // SculptableMesh.CurvatureDeviationAt) has to depart from the mesh's own baseline
        // before relax ramps up toward full strength - keeps it mostly out of the way of
        // ordinary curved shaping (a lobe's own rounded tip, a deliberate broad bump) and
        // concentrated on genuine creases/pinches, which read as a MUCH sharper local
        // departure from that baseline than an ordinary rounded feature does. User-reported:
        // full-strength-everywhere was smoothing "fill" areas away too, not just creases.
        // RelaxCurvatureFloor keeps a small residual amount everywhere (still enough for
        // stray-triangle cleanup) rather than an all-or-nothing cutoff that would let ordinary
        // areas re-develop the same stretched-triangle problem this pass exists to prevent.
        // Calibrated empirically against this app's own live cavity numbers, not guessed - a
        // rounded lobe tip measured ~0.07, the mesh-wide 90th/99th percentiles were ~2.9/~12.5,
        // and an actual reported pinch measured ~21 - a clean 300x separation between ordinary
        // shaping and a genuine crease, comfortably straddled by Start/Full below. (Sampled
        // across a 78k-vertex sculpt, whose median deviation was ~0.6 and maximum ~64.)
        private const float RelaxCurvatureFloor = 0.08f;
        private const float RelaxCurvatureStart = 3f;
        private const float RelaxCurvatureFull = 15f;

        /// Curvature-adaptive relaxation pass, called once per dab right after Clay's own
        /// displacement - see surfaceRelax's field remarks for why this exists and why it's
        /// Clay-only. Runs its OWN wider QueryNear rather than reusing the calling brush's
        /// candidate list - see RelaxRadiusFactor's remarks.
        ///
        /// This used to be deliberately plain managed C#, on the reasoning that a few passes over
        /// a footprint-bounded candidate list was a small fraction of what Smooth's own job
        /// exists to make fast. Measurement said otherwise, and by a wide margin: "footprint-
        /// bounded" is 2.5x the brush radius here, about six times the dab's own surface area, so
        /// on a 270k-triangle sphere at a 0.25 brush radius this pass alone accounted for 52.8ms
        /// of a 61.1ms Clay dab - 86% of it. It now takes the same Burst path Smooth does.
        // The centres of every dab this frame placed, across every mirror sign - the input to the
        // one batched relax pass that runs after them (see ApplySurfaceRelaxBatched). Reused
        // between frames rather than reallocated; ClayMaxDabsPerFrame x 8 mirror signs bounds it.
        private readonly List<Vector3> _relaxCentres = new List<Vector3>();
        // How many dabs the frame placed (NOT counting mirror signs - a mirrored dab is the same
        // dab, applied twice). Sets how much relaxation the batched pass has to make up for.
        private int _relaxDabCount;
        // Union of the per-centre candidate queries, deduped by generation stamp - same scheme as
        // DirtyVertexSet, and needed for the same reason: consecutive dab centres are a fifth of a
        // brush radius apart while relax reaches two and a half radii, so their candidate lists
        // overlap by well over 90% and the union is barely larger than one of them.
        private readonly List<int> _relaxCandidateUnion = new List<int>();
        private int[] _relaxUnionStamp;
        private int _relaxUnionGeneration;
        private NativeArray<Vector3> _nativeRelaxCentres;

        // One viewpoint per entry of _relaxCentres, kept in lockstep with it: the camera position
        // the dab that placed that centre was applied from (see _dabCameraLocal). The relax pass is
        // batched across every sign in the frame, so unlike a dab it cannot have a single viewpoint
        // of its own - a candidate is weighted against its NEAREST centre, and the same centre is
        // what says which side of the mirror that candidate is being relaxed for. Without this the
        // batched pass would reintroduce, for the relaxation, exactly the asymmetry _dabCameraLocal
        // removes from the dabs themselves.
        private readonly List<Vector3> _relaxCentreCameras = new List<Vector3>();
        private NativeArray<Vector3> _nativeRelaxCentreCameras;

        private void BeginRelaxBatch()
        {
            _relaxCentres.Clear();
            _relaxCentreCameras.Clear();
            _relaxDabCount = 0;
        }

        /// Runs Clay's relaxation ONCE for the whole frame, over the union of every dab centre it
        /// placed, instead of once per dab.
        ///
        /// A dab's own displacement is cheap next to this pass - relax reaches RelaxRadiusFactor
        /// (2.5) brush radii, about six times the dab's own surface area - and consecutive dabs
        /// are only ClayDabSpacingFraction (0.2) of a radius apart, so running it per dab redid
        /// almost exactly the same work several times over within a single frame: the same spatial
        /// query, the same gather, the same full-mesh position mirror copy, the same weight job and
        /// the same scatter, over a candidate set that had barely moved. On a wide stroke over a
        /// dense mesh that is the single dominant cost of a brush frame, and it is the reason a
        /// zoomed-out stroke lagged the cursor while a zoomed-in one felt instant.
        ///
        /// The total amount of relaxation is preserved, not reduced: each dab still contributes its
        /// own surfaceRelax * MaxRelaxIterations worth of passes, they are just accumulated and run
        /// back-to-back over one candidate set. What changes is that the passes now interleave with
        /// the frame's dabs rather than each dab's own - a distinction the pass is already
        /// deliberately insensitive to, since RelaxInnerFloor exists precisely to keep it out of
        /// whatever the brush is actively shaping.
        private void ApplySurfaceRelaxBatched()
        {
            if (surfaceRelax <= 0f || _relaxDabCount == 0 || _relaxCentres.Count == 0) return;

            // Thinned first, and the candidate query widened by however much that cost, so the
            // union covers exactly what the unthinned centres would have - see CompactRelaxCentres.
            float centreSeparation = CompactRelaxCentres();
            float relaxRadius = brushRadius * RelaxRadiusFactor;
            List<int> candidates = GatherRelaxCandidates(relaxRadius + centreSeparation);
            if (candidates.Count == 0) return;

            // Total pass budget for the frame. Capped so a frame in which the cursor jumped a long
            // way (many banked dabs) cannot turn into a burst of relaxation passes over a large
            // union - the same "a hitching frame must not also become the most expensive one"
            // reasoning ClayMaxDabsPerFrame itself encodes.
            float passAmount = Mathf.Min(surfaceRelax * MaxRelaxIterations * _relaxDabCount,
                                         MaxRelaxIterations * MaxRelaxFrameBudget);

            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
            {
                ApplySurfaceRelaxLocalJob(relaxRadius, candidates, passAmount);
                return;
            }
            ApplySurfaceRelaxLocalManaged(relaxRadius, candidates, passAmount);
        }

        // How many whole MaxRelaxIterations-worth of passes one frame may fold in, however many
        // dabs it placed. At the default surfaceRelax this is only reached past ~18 dabs in a
        // single frame, i.e. only on a frame that was already dropping material at
        // ClayMaxDabsPerFrame.
        private const int MaxRelaxFrameBudget = 4;

        // Ceiling on how many centres the weight pass measures each candidate against. The pass is
        // O(candidates x centres), and a frame that banked a long cursor jump with all three mirror
        // axes enabled could otherwise reach ClayMaxDabsPerFrame x 8 of them.
        private const int MaxRelaxCentres = 48;

        /// Drops centres that add nothing, and returns how far a dropped centre can be from the
        /// one that replaced it (0 when nothing meaningful was dropped) so the caller can widen its
        /// candidate query by the same amount and keep coverage identical.
        ///
        /// The first pass removes only near-exact duplicates - a mirrored dab landing on its own
        /// mirror plane produces the same point twice, and each duplicate costs a whole extra
        /// spatial query plus a distance test per candidate for no change in the result. The second
        /// only runs on a frame that would otherwise blow past MaxRelaxCentres, and trades a small
        /// shift in where the shell profile sits for a bounded cost on exactly the frames that are
        /// already the most expensive ones.
        private float CompactRelaxCentres()
        {
            ThinRelaxCentres(brushRadius * 1e-3f);
            if (_relaxCentres.Count <= MaxRelaxCentres) return 0f;

            float separation = Mathf.Max(brushRadius * ClayDabSpacingFraction, 1e-6f);
            float applied = 0f;
            while (_relaxCentres.Count > MaxRelaxCentres)
            {
                ThinRelaxCentres(separation);
                applied = separation;
                separation *= 2f; // always terminates: doubling eventually leaves a single centre
            }
            return applied;
        }

        /// Greedy in-place thin: keeps a centre only if it is at least `separation` from every
        /// centre kept before it. O(kept x total), which at these list lengths (tens) is nothing.
        private void ThinRelaxCentres(float separation)
        {
            if (separation <= 0f) return;
            float sqrSeparation = separation * separation;

            int w = 0;
            for (int i = 0; i < _relaxCentres.Count; i++)
            {
                Vector3 c = _relaxCentres[i];
                bool covered = false;
                for (int k = 0; k < w; k++)
                {
                    if ((_relaxCentres[k] - c).sqrMagnitude >= sqrSeparation) continue;
                    covered = true;
                    break;
                }
                if (covered) continue;
                // The viewpoint list is compacted in lockstep, never separately - a centre and the
                // viewpoint its dab was applied from have to stay the same entry (see
                // _relaxCentreCameras).
                _relaxCentreCameras[w] = _relaxCentreCameras[i];
                _relaxCentres[w++] = c;
            }
            _relaxCentres.RemoveRange(w, _relaxCentres.Count - w);
            _relaxCentreCameras.RemoveRange(w, _relaxCentreCameras.Count - w);
        }

        /// Every vertex within queryRadius of ANY of this frame's dab centres, deduped. One query
        /// per centre: the query itself is a cell walk plus a bulk list copy, which is a couple of
        /// operations per returned index against the dozens each candidate costs downstream, so
        /// paying it per centre to keep the expensive part paid once per vertex is the right trade.
        private List<int> GatherRelaxCandidates(float queryRadius)
        {
            int vertexCount = sculptableMesh.Vertices.Length;
            if (_relaxUnionStamp == null || _relaxUnionStamp.Length != vertexCount)
            {
                _relaxUnionStamp = new int[vertexCount];
                _relaxUnionGeneration = 0;
            }
            _relaxUnionGeneration++;
            _relaxCandidateUnion.Clear();

            int[] stamp = _relaxUnionStamp;
            int generation = _relaxUnionGeneration;
            for (int c = 0; c < _relaxCentres.Count; c++)
            {
                // QueryNear hands back the spatial grid's own reused buffer, so this has to consume
                // it fully before the next centre's query overwrites it.
                List<int> near = sculptableMesh.QueryNear(_relaxCentres[c], queryRadius);
                for (int k = 0; k < near.Count; k++)
                {
                    int vi = near[k];
                    if ((uint)vi >= (uint)stamp.Length || stamp[vi] == generation) continue;
                    stamp[vi] = generation;
                    _relaxCandidateUnion.Add(vi);
                }
            }
            return _relaxCandidateUnion;
        }

        private void EnsureRelaxCentresNative()
        {
            if (_nativeRelaxCentres.IsCreated && _nativeRelaxCentres.Length >= _relaxCentres.Count) return;
            if (_nativeRelaxCentres.IsCreated) _nativeRelaxCentres.Dispose();
            if (_nativeRelaxCentreCameras.IsCreated) _nativeRelaxCentreCameras.Dispose();
            int capacity = Mathf.Max(Mathf.NextPowerOfTwo(_relaxCentres.Count), 32);
            _nativeRelaxCentres = new NativeArray<Vector3>(capacity, Allocator.Persistent);
            // Grown together so the two can never disagree on length - see _relaxCentreCameras.
            _nativeRelaxCentreCameras = new NativeArray<Vector3>(capacity, Allocator.Persistent);
        }

        private void ApplySurfaceRelaxLocalJob(float relaxRadius, List<int> candidates, float passAmount)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            int totalVerts = verts.Length;

            // Same neighbour-lookup scaffolding Smooth's job uses - a relax pass reads the
            // positions of neighbours that can sit outside its own candidate list, so it needs
            // both the slot map and the full-mesh position mirror. See EnsureSmoothFullMeshScratch.
            EnsureSmoothFullMeshScratch(totalVerts);
            RefreshPositionMirror(verts);

            // Grown BEFORE the gather, not after: EnsureNativeScratch reallocates rather than
            // resizes, so growing the shared scratch after filling it would throw the gathered
            // footprint away.
            EnsureLaplacianCandidates(candidates.Count);
            GatherCandidatesNative(candidates, verts, sculptableMesh.Normals, sculptableMesh.Mask);

            EnsureRelaxCentresNative();
            for (int c = 0; c < _relaxCentres.Count; c++)
            {
                _nativeRelaxCentres[c] = _relaxCentres[c];
                _nativeRelaxCentreCameras[c] = _relaxCentreCameras[c];
            }

            SculptableMesh mesh = sculptableMesh;
            NativeArray<int> laplacianCandidates = _nativeLaplacianCandidates;
            NativeArray<int> vertexToSlot = _nativeVertexToSlot;
            NativeArray<float> curvature = _nativeRelaxCurvature;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int globalIdx = candidates[ci];
                laplacianCandidates[ci] = globalIdx;
                vertexToSlot[globalIdx] = ci;
                curvature[ci] = mesh.CurvatureDeviationAt(globalIdx);
            }

            var weightJob = new RelaxWeightJob
            {
                PositionsIn = _nativePositionsIn,
                NormalsIn = _nativeNormalsIn,
                MaskIn = _nativeMaskIn,
                CurvatureIn = _nativeRelaxCurvature,
                Centres = _nativeRelaxCentres,
                CentreCameras = _nativeRelaxCentreCameras,
                CentreCount = _relaxCentres.Count,
                WeightsOut = _nativeClayWeights,
                BrushRadius = brushRadius,
                RelaxRadius = relaxRadius,
                EdgeSoftness = RelaxEdgeSoftness,
                InnerFloor = RelaxInnerFloor,
                CurvatureFloor = RelaxCurvatureFloor,
                CurvatureStart = RelaxCurvatureStart,
                CurvatureFull = RelaxCurvatureFull,
                FrontFacingOnly = frontFacingOnly,
                CameraLocalPos = sculptableMesh.transform.InverseTransformPoint(cam.transform.position),
            };

            int fullPasses = Mathf.FloorToInt(passAmount);
            float partialFactor = passAmount - fullPasses;

            NativeArray<int> adjOffsets = sculptableMesh.AdjacencyOffsets;
            NativeArray<int> adjNeighbors = sculptableMesh.AdjacencyNeighbors;
            NativeArray<Vector3> readBuf = _nativePositionsIn;
            NativeArray<Vector3> writeBuf = _nativePositionsOut;

            // One dependency chain, waited on once - same reasoning as Smooth's (see
            // ApplySmoothBrushLocalJob): the passes are inherently sequential, but nothing needs
            // to look at the intermediate results on the main thread.
            JobHandle chain = weightJob.Schedule(candidates.Count, 64);
            for (int pass = 0; pass < fullPasses; pass++)
            {
                chain = ScheduleSurfaceRelaxJob(candidates.Count, readBuf, writeBuf, adjOffsets, adjNeighbors, 1f, chain);
                (readBuf, writeBuf) = (writeBuf, readBuf);
            }
            if (partialFactor > 0.001f)
            {
                chain = ScheduleSurfaceRelaxJob(candidates.Count, readBuf, writeBuf, adjOffsets, adjNeighbors, partialFactor, chain);
                (readBuf, writeBuf) = (writeBuf, readBuf);
            }
            chain.Complete();

            bool anyPassRan = fullPasses > 0 || partialFactor > 0.001f;
            NativeArray<float> weights = _nativeClayWeights;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int globalIdx = candidates[ci];
                vertexToSlot[globalIdx] = -1; // targeted reset, see field remarks
                if (!anyPassRan || weights[ci] <= 0f) continue;

                mesh.RecordUndoBeforeIfNeeded(globalIdx);
                verts[globalIdx] = readBuf[ci];
                // The relax shell moves the overwhelming majority of its candidates by an amount
                // no one can see (see SculptableMesh.FilterToDrifted for the measurement), and
                // reporting those dirty costs a normal recompute, a cavity recompute, a triangle
                // re-bucket and a GPU upload each. Asking first keeps them out of the dirty set
                // entirely rather than relying on the filter downstream to take them back out.
                if (mesh.HasVisiblyDrifted(globalIdx)) _dirtyVertexScratch.Add(globalIdx);
            }
            if (anyPassRan) MarkPositionMirrorStale();
        }

        private JobHandle ScheduleSurfaceRelaxJob(int candidateCount, NativeArray<Vector3> readBuf, NativeArray<Vector3> writeBuf,
            NativeArray<int> adjOffsets, NativeArray<int> adjNeighbors, float passFactor, JobHandle dependency)
        {
            var job = new SurfaceRelaxJob
            {
                Candidates = _nativeLaplacianCandidates,
                AdjacencyOffsets = adjOffsets,
                AdjacencyNeighbors = adjNeighbors,
                VertexToSlot = _nativeVertexToSlot,
                FullPositions = _nativeFullPositionMirror,
                PositionsRead = readBuf,
                PositionsWrite = writeBuf,
                Weights = _nativeClayWeights,
                PassFactor = passFactor,
            };
            return job.Schedule(candidateCount, 64, dependency);
        }

        private void ApplySurfaceRelaxLocalManaged(float relaxRadius, List<int> candidates, float passAmount)
        {
            SculptableMesh mesh = sculptableMesh;
            Vector3[] verts = mesh.Vertices;
            Vector3[] normals = mesh.Normals;
            float[] mask = mesh.Mask;

            if (_relaxWeightScratch.Length < candidates.Count) _relaxWeightScratch = new float[candidates.Count];
            float[] weights = _relaxWeightScratch;
            float relaxRadiusSqr = relaxRadius * relaxRadius;
            float invShellSpan = 1f / Mathf.Max(relaxRadius - brushRadius, 1e-5f);
            bool anyInRange = false;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 p = verts[i];

                // Distance to the NEAREST of this frame's dab centres - see RelaxWeightJob, whose
                // Burst counterpart this must stay identical to (which one runs depends only on
                // footprint size). Compared squared so the square root is paid at most once per
                // candidate rather than once per centre, and not at all for a candidate the whole
                // sweep misses.
                float sqrDist = float.MaxValue;
                int nearest = 0;
                for (int c = 0; c < _relaxCentres.Count; c++)
                {
                    float d = (p - _relaxCentres[c]).sqrMagnitude;
                    if (d < sqrDist) { sqrDist = d; nearest = c; }
                }
                if (sqrDist > relaxRadiusSqr) { weights[ci] = 0f; continue; }
                float dist = Mathf.Sqrt(sqrDist);

                // Shell profile, not a falloff from the dab centre: RelaxInnerFloor inside the
                // brush's own radius (leave the current dab alone - see its remarks), ramping
                // up to full strength just past brushRadius and tapering again only at the
                // outer rim (relaxRadius) - see RelaxRadiusFactor's remarks for why the SHELL,
                // not the core, is where a seam with a neighboring dab actually needs fixing.
                float spatialWeight;
                if (dist <= brushRadius)
                {
                    spatialWeight = RelaxInnerFloor;
                }
                else
                {
                    float shellT = (dist - brushRadius) * invShellSpan;
                    spatialWeight = ClayFalloff(1f - shellT, RelaxEdgeSoftness);
                }

                // CurvatureDeviationAt is unclamped (unlike the visual cavity tint) - see
                // RelaxCurvatureFloor's remarks for why that distinction is what makes this
                // gate actually separate an ordinary rounded feature from a genuine crease,
                // rather than applying full strength uniformly across the whole shell
                // regardless of whether anything there actually needs it.
                float curvatureDeviation = mesh.CurvatureDeviationAt(i);
                float curvatureFactor = Mathf.Lerp(RelaxCurvatureFloor, 1f,
                    Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(RelaxCurvatureStart, RelaxCurvatureFull, curvatureDeviation)));

                // Nearest centre's own viewpoint, matching RelaxWeightJob - see _relaxCentreCameras.
                float w = spatialWeight * curvatureFactor * (1f - mask[i])
                    * FrontFacingWeight(frontFacingOnly, normals[i], p, _relaxCentreCameras[nearest]);
                weights[ci] = w;
                if (w > 0f) anyInRange = true;
            }
            if (!anyInRange) return;

            int fullPasses = Mathf.FloorToInt(passAmount);
            float partialFactor = passAmount - fullPasses;

            for (int pass = 0; pass < fullPasses; pass++)
                RunSurfaceRelaxPass(candidates, weights, 1f);
            if (partialFactor > 0.001f)
                RunSurfaceRelaxPass(candidates, weights, partialFactor);
        }

        private void RunSurfaceRelaxPass(List<int> candidates, float[] weights, float passFactor)
        {
            SculptableMesh mesh = sculptableMesh;
            Vector3[] verts = mesh.Vertices;
            bool anyMoved = false;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                float w = weights[ci];
                if (w <= 0f) continue;
                int i = candidates[ci];

                // Full Laplacian delta (same "average of my neighbors" Smooth uses) - NOT
                // stripped down to its tangential-only component. That was tried first and
                // measured to do almost nothing to an actual pinch: a hard crease is a fold in
                // the HEIGHT field (a vertex sitting sharply along its own normal relative to
                // its neighbors), and moving a vertex purely sideways leaves its position along
                // its own (unchanged) normal identical by construction - verified live, 20 dabs
                // of tangential-only relax at max strength reduced a measured pinch's curvature
                // by under 4%. Letting relax actually ease the along-normal component too is
                // what closes a fold; it's still much gentler than Smooth itself (weighted by
                // the same flat-plateau/wide-reach falloff above, and gated by MaxRelaxIterations
                // rather than Smooth's own up-to-10), which is the tradeoff surfaceRelax's own
                // slider exists to tune - default keeps it subtle enough not to flatten
                // deliberate broad shaping.
                Vector3 p = verts[i];
                Vector3 toAverage = mesh.GetNeighborAverage(i) - p;

                mesh.RecordUndoBeforeIfNeeded(i);
                verts[i] = p + toAverage * Mathf.Clamp01(w * passFactor);
                anyMoved = true;
                // Same invisible-movement gate the job path applies - see its remarks.
                if (mesh.HasVisiblyDrifted(i)) _dirtyVertexScratch.Add(i);
            }

            if (anyMoved) MarkPositionMirrorStale();
        }

        private void ApplyClayBrushLocal(Vector3 localPoint, Vector3 localNormal, Vector3 tangent0, Vector3 bitangent0, bool positive, float dt)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            Vector3[] normals = sculptableMesh.Normals;

            // Query radius stays keyed to the UN-shrunk brushRadius even under a light touch -
            // EffectiveClayRadius only ever shrinks the footprint (see its remarks), so this
            // always covers it, and re-querying a smaller radius every pressure fluctuation
            // would just invalidate the spatial grid's result buffer for no benefit.
            float queryRadius = clayTipRoundness < 1f ? brushRadius * Sqrt2 : brushRadius;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, queryRadius);
            if (candidates.Count == 0) return;

            float effectiveRadius = EffectiveClayRadius;
            float effectiveEdgeSoftness = EffectiveClayEdgeSoftness;

            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyClayBrushLocalJob(localPoint, localNormal, tangent0, bitangent0, positive, dt, candidates, verts, normals, effectiveRadius, effectiveEdgeSoftness);
            else
                ApplyClayBrushLocalManaged(localPoint, localNormal, tangent0, bitangent0, positive, dt, candidates, verts, normals, effectiveRadius, effectiveEdgeSoftness);

            // The relax pass this dab needs runs once for the whole frame, over the union of every
            // dab centre in it - see ApplySurfaceRelaxBatched. Recorded here rather than in
            // ApplyClayBrushAtLocal so a mirrored dab contributes its own (mirrored) centre, and
            // only for dabs that actually landed on geometry.
            if (surfaceRelax > 0f)
            {
                _relaxCentres.Add(localPoint);
                _relaxCentreCameras.Add(_dabCameraLocal); // lockstep - see _relaxCentreCameras
            }
        }

        private void ApplyClayBrushLocalJob(Vector3 localPoint, Vector3 localNormal, Vector3 tangent0, Vector3 bitangent0, bool positive, float dt, List<int> candidates, Vector3[] verts, Vector3[] normals, float effectiveRadius, float effectiveEdgeSoftness)
        {
            float sign = positive ? 1f : -1f;
            float effectiveStrength = EffectiveBrushStrength;
            float effectiveStrengthAccumulate = EffectiveClayStrengthAccumulate;
            // Height stays tied to the UN-shrunk brushRadius - pressure already scales how much
            // this dab deposits via effectiveStrength, so scaling height too would double-count
            // it. Only the footprint's WIDTH (effectiveRadius/effectiveEdgeSoftness) responds to
            // pressure here - see EffectiveClayRadius's remarks.
            float height = brushRadius * clayHeightFactor * sign;

            GatherCandidatesNative(candidates, verts, normals, sculptableMesh.Mask);
            sculptableMesh.CopyStrokeStartPositions(candidates, _nativeStrokeStart);

            var weightJob = new ClayWeightJob
            {
                PositionsIn = _nativePositionsIn,
                NormalsIn = _nativeNormalsIn,
                MaskIn = _nativeMaskIn,
                WeightsOut = _nativeClayWeights,
                PlaneWeightsOut = _nativeClayPlaneWeights,
                WeightedPosOut = _nativeClayWeightedPos,
                WeightedNormalOut = _nativeClayWeightedNormal,
                LocalPoint = localPoint,
                BrushRadius = effectiveRadius,
                Tangent0 = tangent0,
                Bitangent0 = bitangent0,
                TipRoundness = clayTipRoundness,
                EdgeSoftness = effectiveEdgeSoftness,
                FrontFacingOnly = frontFacingOnly,
                CameraLocalPos = _dabCameraLocal,
            };
            weightJob.Schedule(candidates.Count, 32).Complete();

            // Sequential reduction across the (footprint-bounded) candidate list - see
            // ClayWeightJob's remarks on why this stays on the main thread.
            // Mask-free weights throughout - the plane describes the surface, not what may move.
            // See ClayWeightJob.Execute.
            Vector3 planeOriginSum = Vector3.zero, planeNormalSum = Vector3.zero;
            float planeWeightSum = 0f;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                planeOriginSum += _nativeClayWeightedPos[ci];
                planeNormalSum += _nativeClayWeightedNormal[ci];
                planeWeightSum += _nativeClayPlaneWeights[ci];
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
                StrokeStartIn = _nativeStrokeStart,
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
                InvStampRadius = 1f / Mathf.Max(0.0001f, effectiveRadius * alphaScale),
                AlphaSize = useAlpha ? _nativeAlphaSize : 0,
                Accumulate = accumulate,
                Rate = sign * clayHeightFactor * effectiveStrengthAccumulate * ClaySpeed * dt,
                MaxAlong = height * (accumulate ? ClayStrokeDepthLimitAccumulate : ClayStrokeDepthLimit),
            };
            dispJob.Schedule(candidates.Count, 32).Complete();

            ScatterJobResults(candidates, verts);
        }

        private void ApplyClayBrushLocalManaged(Vector3 localPoint, Vector3 localNormal, Vector3 tangent0, Vector3 bitangent0, bool positive, float dt, List<int> candidates, Vector3[] verts, Vector3[] normals, float effectiveRadius, float effectiveEdgeSoftness)
        {
            float sign = positive ? 1f : -1f;
            float effectiveStrength = EffectiveBrushStrength;
            float effectiveStrengthAccumulate = EffectiveClayStrengthAccumulate;
            // See ApplyClayBrushLocalJob's matching line - height deliberately stays tied to the
            // UN-shrunk brushRadius so pressure isn't double-counted between strength and height.
            float height = brushRadius * clayHeightFactor * sign;

            if (_clayWeightScratch.Length < candidates.Count) _clayWeightScratch = new float[candidates.Count];
            float[] weights = _clayWeightScratch;

            Vector3 planeOriginSum = Vector3.zero;
            Vector3 planeNormalSum = Vector3.zero;
            float planeWeightSum = 0f;
            // This dab's own viewpoint, not the raw camera - see _dabCameraLocal.
            Vector3 cameraLocalPos = _dabCameraLocal;
            float[] mask = sculptableMesh.Mask;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 p = verts[i];
                Vector3 toVert = p - localPoint;
                float t01 = ClayTipShapeT01(toVert, effectiveRadius, tangent0, bitangent0, clayTipRoundness);
                if (t01 <= 0f) { weights[ci] = 0f; continue; }

                Vector3 n = normals[i];
                // Plane weight (no mask) and displacement weight (masked), split for the reason
                // ClayWeightJob.Execute sets out - the plane measures the surface, the mask only
                // says which of its vertices may move.
                float planeW = ClayFalloff(t01, effectiveEdgeSoftness) // flat plateau, edge-only taper - see clayEdgeSoftness
                    * FrontFacingWeight(frontFacingOnly, n, p, cameraLocalPos);
                weights[ci] = planeW * (1f - mask[i]);

                planeOriginSum += p * planeW;
                planeNormalSum += n * planeW;
                planeWeightSum += planeW;
            }

            if (planeWeightSum <= 1e-6f) return;

            Vector3 planeOrigin = planeOriginSum / planeWeightSum;
            Vector3 planeNormal = planeNormalSum.sqrMagnitude > 1e-8f
                ? planeNormalSum.normalized : localNormal;

            BuildTangentBasis(planeNormal, out Vector3 tangent, out Vector3 bitangent);
            float rot = alphaRotation * Mathf.Deg2Rad;
            float cosR = Mathf.Cos(rot), sinR = Mathf.Sin(rot);
            BrushAlphaLibrary.AlphaData alpha = useAlpha ? BrushAlphaLibrary.Get(alphaType) : default;
            float invStampRadius = 1f / Mathf.Max(0.0001f, effectiveRadius * alphaScale);
            SculptableMesh mesh = sculptableMesh;
            bool anyMoved = false;

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

                mesh.RecordUndoBeforeIfNeeded(i);
                anyMoved = true;

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
                        mesh.StrokeStartPosition(i), planeNormal,
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
                        mesh.StrokeStartPosition(i), planeNormal,
                        height * ClayStrokeDepthLimit, height);
                }

                _dirtyVertexScratch.Add(i);
            }

            if (anyMoved) MarkPositionMirrorStale();
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
            HandleCarveInput(mouse, overUI, altHeld, 0f);
        }

        private void HandleDamStandardInput(Mouse mouse, bool overUI, bool altHeld)
        {
            HandleCarveInput(mouse, overUI, altHeld, damLipHeight);
        }

        // Crease and Dam Standard differ by exactly one number - the leading-edge lip height -
        // so they share one input path, one stroke stepper and one dab. Dam Standard IS Crease
        // with lip > 0; it always was in the inner loop, this just stops the outer layers
        // duplicating each other.
        //
        // Deliberately no UpdateStrokeSpeed call: these brushes no longer pace themselves off a
        // measured cursor speed, they place a fixed carve every fixed distance travelled (see
        // ApplyCarveStroke). Inflate is the only brush still on the speed factor.
        private void HandleCarveInput(Mouse mouse, bool overUI, bool altHeld, float lipFactor)
        {
            _isHovering = false;
            if (overUI) { _lastCarveStrokeLocal = null; return; }

            Ray ray = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) { _lastCarveStrokeLocal = null; return; }

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            bool rightHeld = mouse.rightButton.isPressed;
            bool invertHeld = rightHeld || CtrlHeld;
            _previewPositive = invertHeld ? !isPositive : isPositive;

            LogRayHit(mouse, ray, hitPoint, hitNormal);

            bool leftSculpting = mouse.leftButton.isPressed && !altHeld;
            if (!leftSculpting && !rightHeld) { _lastCarveStrokeLocal = null; return; }

            ApplyCarveStroke(hitPoint, hitNormal,
                leftSculpting ? (invertHeld ? !isPositive : isPositive) : !isPositive, lipFactor);
        }

        // Travel between consecutive carve dabs, as a fraction of brush radius. Half of Clay's
        // ClayDabSpacingFraction: Clay's stamp is a wide flat plateau that hides a seam, while a
        // crease is a narrow groove where the same gap reads as a visible scallop in the valley
        // floor. This is the same fix Clay got and Crease never did - carving on a clock meant
        // the depth a stroke cut depended on the frame rate and on how fast the hand moved, so
        // the same drawn line came out deep where the cursor slowed and shallow where it didn't.
        private const float CreaseDabSpacingFraction = 0.1f;
        // Cost ceiling for a frame in which the cursor teleports (a drag re-entering the mesh, a
        // frame hitch) - same role as ClayMaxDabsPerFrame, and higher only because the spacing
        // above is half of Clay's. Still covers ~4.8 brush radii of travel in one frame.
        private const int CreaseMaxDabsPerFrame = 48;
        // The dt one dab is worth. A calibration constant, not a real elapsed time: at this
        // value the distance-driven pacing cuts per centimetre of travel what the old
        // time-driven pacing cut at an ordinary carving speed, so existing Brush Strength /
        // Crease Depth settings keep feeling the same. Sized so a single pass at full Brush
        // Strength reaches the plateau depth - the same calibration StrokePacingGain encoded for
        // the scheme this replaces. Counterpart of Clay's ClayReferenceStrokeSpeed.
        private const float CreaseDabTimeQuantum = 0.2f;
        // How fast the carve frame (normal and travel direction) eases toward each new sample.
        // The raycast hands back the hit TRIANGLE's flat face normal (see
        // TriangleSpatialGrid.Raycast), which flips from facet to facet along a stroke and -
        // once the groove has any depth at all - starts reporting the groove WALL rather than
        // the surface, so the cut steers itself sideways and wanders off the line being drawn.
        // Averaging the footprint's vertex normals kills the facet jitter; easing toward that
        // average across dabs kills the wander.
        private const float CarveFrameTracking = 0.35f;

        private void ApplyCarveStroke(Vector3 worldPoint, Vector3 worldNormal, bool positive, float lipFactor)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            // Not InverseTransformDirection: that is rotation-only and mis-tilts the normal
            // on a non-uniformly scaled object - see SculptableMesh.WorldToLocalNormal.
            Vector3 localNormal = sculptableMesh.WorldToLocalNormal(worldNormal);
            Vector3 sampledNormal = AverageFootprintNormal(localPoint, localNormal);
            float spacing = Mathf.Max(brushRadius * CreaseDabSpacingFraction, 0.0005f);

            // One dirty set for the whole frame's dabs, pushed once at the end. A carve frame can
            // place up to CreaseMaxDabsPerFrame dabs at half Clay's spacing, and each used to pay
            // its own full ApplyVerticesLocal over a footprint that overlapped its neighbour's by
            // ~90% - see FlushClayFrame for the same fix on Clay. Carve is the easier case of the
            // two: its stroke frame (_carveStrokeNormal/_carveStrokeDir) is already sampled once
            // per frame before any dab runs, so nothing within the frame was reading the
            // per-dab-refreshed normals anyway.
            BeginDirtyVertices();

            if (!_lastCarveStrokeLocal.HasValue)
            {
                // Fresh stroke: no travel direction yet, so this first dab pinches radially (a
                // zero dir leaves the whole tangential offset in SplitCarveFrame's `across`) and
                // a tap still marks the surface - the distance path below never fires for a
                // click that doesn't move.
                _carveStrokeNormal = sampledNormal;
                _carveStrokeDir = Vector3.zero;
                _carveDabCarry = 0f;
                _lastCarveStrokeLocal = localPoint;
                ApplyCarveDab(localPoint, positive, lipFactor);
                FlushDirtyVertices();
                return;
            }

            Vector3 from = _lastCarveStrokeLocal.Value;
            Vector3 travel = localPoint - from;
            float dist = travel.magnitude;

            _carveStrokeNormal = Vector3.Slerp(_carveStrokeNormal, sampledNormal, CarveFrameTracking).normalized;
            // Only re-read the direction from a segment long enough to mean something - below a
            // quarter of the dab spacing the travel is hand tremor, and normalizing it hands the
            // pinch a random axis.
            if (dist > spacing * 0.25f)
            {
                Vector3 tangential = travel - _carveStrokeNormal * Vector3.Dot(travel, _carveStrokeNormal);
                if (tangential.sqrMagnitude > 1e-10f)
                {
                    Vector3 dir = tangential.normalized;
                    // Slerp rather than Lerp: a hairpin turn mid-stroke passes through zero
                    // length under a straight lerp, which would hand that dab a garbage axis.
                    _carveStrokeDir = _carveStrokeDir.sqrMagnitude > 1e-8f
                        ? Vector3.Slerp(_carveStrokeDir, dir, CarveFrameTracking).normalized
                        : dir;
                }
            }

            // A stationary cursor covers no ground and so carves nothing - that is the whole
            // point of distance pacing, and it is what stops a pause from drilling a hole.
            // Build Up on Hold opts back into a held cut by feeding the stepper virtual travel,
            // at the same fraction of a moving stroke's rate the old time-paced floor used.
            float advance = dist;
            if (buildUpOnHold) advance += StrokePacingReference * AccumulateSpeedFloor * Time.deltaTime;
            _carveDabCarry += advance;

            int placed = 0;
            while (_carveDabCarry >= spacing && placed < CreaseMaxDabsPerFrame)
            {
                _carveDabCarry -= spacing;
                // Where along THIS frame's segment the dab falls. dist can be ~0 while the carry
                // still crosses the threshold (a Build Up on Hold dab, or one banked by earlier
                // frames finally firing), in which case the dab belongs at the current point.
                float u = dist > 1e-9f ? Mathf.Clamp01((dist - _carveDabCarry) / dist) : 1f;
                ApplyCarveDab(Vector3.Lerp(from, localPoint, u), positive, lipFactor);
                placed++;
            }
            // Hit the ceiling: drop the unspent travel instead of banking it into a burst of
            // dabs next frame, which would dig hardest exactly where the stroke was already
            // struggling to keep up.
            if (placed >= CreaseMaxDabsPerFrame) _carveDabCarry = 0f;

            FlushDirtyVertices();
            _lastCarveStrokeLocal = localPoint;
        }

        /// The footprint's own vertex normals, weighted toward the OUTSIDE of the brush rather
        /// than by the carve falloff. Deliberately not the falloff: that peaks at the centre,
        /// which is exactly where the groove this brush is digging has already tipped the
        /// normals sideways, so a falloff-weighted average would chase the cut and steer it
        /// deeper into its own wall. A broad linear weight lets the surrounding surface - the
        /// thing the crease is being cut INTO - set the direction. Falls back to the raycast
        /// normal if the footprint is empty or its normals cancel out.
        private Vector3 AverageFootprintNormal(Vector3 localPoint, Vector3 fallback)
        {
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            Vector3[] verts = sculptableMesh.Vertices;
            Vector3[] normals = sculptableMesh.Normals;
            float invRadius = 1f / Mathf.Max(brushRadius, 0.0001f);
            Vector3 sum = Vector3.zero;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(verts[i], localPoint);
                if (dist > brushRadius) continue;
                sum += normals[i] * (1f - dist * invRadius);
            }
            return sum.sqrMagnitude > 1e-8f ? sum.normalized : fallback;
        }

        private void ApplyCarveDab(Vector3 localPoint, bool positive, float lipFactor)
        {
            foreach (Vector3 sign in MirrorSigns())
            {
                BeginMirroredDab(sign);
                Vector3 mirroredPoint = Vector3.Scale(localPoint, sign);
                Vector3 mirroredNormal = Vector3.Scale(_carveStrokeNormal, sign).normalized;
                // Mirror the stroke frame the same way Clay mirrors its frozen tip axes, rather
                // than rebuilding it from the mirrored normal - keeps a mirrored groove exactly
                // as stable as the primary one. Scaling by a sign vector preserves length, so
                // the direction stays unit without a re-normalize.
                Vector3 mirroredDir = Vector3.Scale(_carveStrokeDir, sign);
                ApplyCarveDabLocal(mirroredPoint, mirroredNormal, mirroredDir, positive, lipFactor);
            }
        }

        // Carves along the stroke's normal while pinching the footprint ACROSS the stroke line,
        // with every term measured from where this stroke found each vertex
        // (SculptableMesh.StrokeStartPosition) rather than from the dab's own tangent plane.
        // Both of those are the difference between an even groove and what this used to do:
        //
        // - Pinching toward the dab's CENTRE POINT vacuums the footprint inward from every side,
        //   including along the stroke. A moving stroke therefore drags material along with the
        //   cursor, which is what smeared creases into hooks and swirls and piled a bead up
        //   wherever the stroke ended. Only the across-stroke component of that pull is a
        //   crease; the along-stroke component was pure artefact.
        //
        // - Targeting `localPoint + tangentialOffset + normal * depth` snaps the whole footprint
        //   onto the tangent plane AT THE HIT POINT. On anything curved that FLATTENS the
        //   footprint: rim vertices sit behind that plane, so they were pulled outward and every
        //   stroke raised a puffy lip around itself on top of the groove it cut. Anchoring to
        //   each vertex's stroke-start position and preserving its own normal offset
        //   (`startNormal`) carves relative to the surface that is actually there.
        //
        // Anchoring also makes the plateau real: the target no longer moves as the brush's own
        // output moves, so repeated dabs converge on one depth instead of chasing themselves.
        // And successive dabs take the DEEPEST result rather than the latest, so the shallow
        // trailing edge of a passing dab cannot lift a cut that dab's own centre just made -
        // which is what turns a line of overlapping dabs into one groove of even depth.
        private void ApplyCarveDabLocal(Vector3 localPoint, Vector3 localNormal, Vector3 dirLocal,
            bool positive, float lipFactor)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyCarveDabLocalJob(localPoint, localNormal, dirLocal, positive, lipFactor, candidates, verts);
            else
                ApplyCarveDabLocalManaged(localPoint, localNormal, dirLocal, positive, lipFactor, candidates, verts);
        }

        private void ApplyCarveDabLocalJob(Vector3 localPoint, Vector3 localNormal, Vector3 dirLocal,
            bool positive, float lipFactor, List<int> candidates, Vector3[] verts)
        {
            float sign = positive ? 1f : -1f;
            float effectiveStrength = EffectiveCarveStrength;
            float effectiveStrengthAccumulate = EffectiveCarveStrengthAccumulate;

            GatherCandidatesNative(candidates, verts, sculptableMesh.Normals, sculptableMesh.Mask);
            // The dab's whole target is measured from here - see ApplyCarveDabLocal's remarks.
            sculptableMesh.CopyStrokeStartPositions(candidates, _nativeStrokeStart);

            var job = new CreaseJob
            {
                PositionsIn = _nativePositionsIn,
                NormalsIn = _nativeNormalsIn,
                MaskIn = _nativeMaskIn,
                StrokeStartIn = _nativeStrokeStart,
                PositionsOut = _nativePositionsOut,
                AppliedOut = _nativeAppliedOut,
                LocalPoint = localPoint,
                LocalNormal = localNormal,
                DirLocal = dirLocal,
                BrushRadius = brushRadius,
                Depth = brushRadius * creaseDepthFactor * sign,
                Lip = brushRadius * lipFactor * sign,
                Pinch = creasePinch,
                Sign = sign,
                LerpFactorScale = effectiveStrength * CreaseSpeed * CreaseDabTimeQuantum,
                Accumulate = accumulate,
                DepthRate = sign * creaseDepthFactor * effectiveStrengthAccumulate * CreaseSpeed * CreaseDabTimeQuantum,
                LipRate = sign * lipFactor * effectiveStrengthAccumulate * CreaseSpeed * CreaseDabTimeQuantum,
                PinchRateScale = creasePinch * effectiveStrengthAccumulate * CreaseSpeed * CreaseDabTimeQuantum,
                FrontFacingOnly = frontFacingOnly,
                CameraLocalPos = _dabCameraLocal,
            };
            job.Schedule(candidates.Count, 32).Complete();

            ScatterJobResults(candidates, verts);
        }

        private void ApplyCarveDabLocalManaged(Vector3 localPoint, Vector3 localNormal, Vector3 dirLocal,
            bool positive, float lipFactor, List<int> candidates, Vector3[] verts)
        {
            float sign = positive ? 1f : -1f;
            float effectiveStrength = EffectiveCarveStrength;
            float effectiveStrengthAccumulate = EffectiveCarveStrengthAccumulate;
            float depth = brushRadius * creaseDepthFactor * sign;
            float lip = brushRadius * lipFactor * sign;
            float lerpScale = effectiveStrength * CreaseSpeed * CreaseDabTimeQuantum;
            float depthRate = sign * creaseDepthFactor * effectiveStrengthAccumulate * CreaseSpeed * CreaseDabTimeQuantum;
            float lipRate = sign * lipFactor * effectiveStrengthAccumulate * CreaseSpeed * CreaseDabTimeQuantum;
            float pinchRateScale = creasePinch * effectiveStrengthAccumulate * CreaseSpeed * CreaseDabTimeQuantum;
            Vector3 cameraLocalPos = _dabCameraLocal; // this dab's viewpoint - see _dabCameraLocal
            SculptableMesh mesh = sculptableMesh;
            float[] mask = mesh.Mask;
            Vector3[] normals = mesh.Normals;
            float radiusSqr = brushRadius * brushRadius;
            float invRadius = 1f / brushRadius;
            bool anyMoved = false;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 p = verts[i];
                Vector3 toVert = p - localPoint;
                // Squared first - see ApplySmoothBrushLocalManaged. This loop also used to reach
                // sculptableMesh.Normals per candidate, which is a property call re-reading the
                // whole array reference on every iteration.
                float sqrDist = toVert.sqrMagnitude;
                if (sqrDist > radiusSqr) continue;

                float weight = CarveFalloff(1f - Mathf.Sqrt(sqrDist) * invRadius) * (1f - mask[i])
                    * FrontFacingWeight(frontFacingOnly, normals[i], p, cameraLocalPos);

                Vector3 start = mesh.StrokeStartPosition(i);
                SplitCarveFrame(start - localPoint, localNormal, dirLocal,
                    out float startNormal, out float startAlong, out Vector3 startAcross);
                SplitCarveFrame(toVert, localNormal, dirLocal, out _, out _, out Vector3 across);
                bool hasLip = startAlong > 0f;

                mesh.RecordUndoBeforeIfNeeded(i);
                anyMoved = true;

                if (accumulate)
                {
                    // Depth (and the leading-edge lip, when active) keep digging for as long as
                    // the stroke keeps travelling - continuous rates, not a target/plateau, same
                    // as Clay/Inflate's accumulate-on push. The pinch stays a bounded pull
                    // toward the stroke line: it's a shape control, not a depth amount, so
                    // letting it run away would just make the groove's cross-section overshoot
                    // past the centreline and oscillate instead of cutting deeper.
                    float normalRate = depthRate;
                    if (hasLip) normalRate += lipRate;
                    verts[i] = p + localNormal * (normalRate * weight)
                                 - across * Mathf.Clamp01(weight * pinchRateScale);
                }
                else
                {
                    float carve = depth * weight;
                    if (hasLip) carve += lip * weight;
                    // Deepest dab wins - see ApplyCarveDabLocal's remarks.
                    float achieved = Vector3.Dot(p - start, localNormal);
                    if (sign * achieved > sign * carve) carve = achieved;

                    Vector3 pinched = startAcross * (1f - creasePinch * weight);
                    // Same "never undo a neighbouring dab's work" rule, for the pinch.
                    if (across.sqrMagnitude < pinched.sqrMagnitude) pinched = across;

                    Vector3 target = localPoint + localNormal * (startNormal + carve)
                        + dirLocal * startAlong + pinched;
                    // Clamp01: a lerp fraction toward a target, not a velocity - see the
                    // matching note on Clay for what an unclamped factor does on a frame hitch.
                    verts[i] = p + (target - p) * Mathf.Clamp01(weight * lerpScale);
                }

                _dirtyVertexScratch.Add(i);
            }

            if (anyMoved) MarkPositionMirrorStale();
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

            BeginDirtyVertices();
            foreach (Vector3 sign in MirrorSigns())
            {
                BeginMirroredDab(sign);
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                ApplyInflateBrushLocal(Vector3.Scale(localPoint, sign), mirroredNormal, positive);
            }

            FlushDirtyVertices();
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
                CameraLocalPos = _dabCameraLocal,
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
            Vector3 cameraLocalPos = _dabCameraLocal; // this dab's viewpoint - see _dabCameraLocal
            SculptableMesh mesh = sculptableMesh;
            float[] mask = mesh.Mask;
            float radiusSqr = brushRadius * brushRadius;
            float invRadius = 1f / brushRadius;
            float accumulateRate = sign * effectiveStrengthAccumulate * InflateSpeed * dt;
            float lerpScale = effectiveStrength * InflateSpeed * dt;
            bool anyMoved = false;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 p = verts[i];
                // Squared first - see ApplySmoothBrushLocalManaged for why the square root moved
                // below the range test rather than above it.
                float sqrDist = (p - localPoint).sqrMagnitude;
                if (sqrDist > radiusSqr) continue;

                Vector3 n = normals[i];
                float t01 = 1f - Mathf.Sqrt(sqrDist) * invRadius;
                float weight = t01 * t01 * (3f - 2f * t01) * (1f - mask[i]) // smoothstep, masked-out
                    * FrontFacingWeight(frontFacingOnly, n, p, cameraLocalPos);
                if (weight <= 0f) continue;

                mesh.RecordUndoBeforeIfNeeded(i);
                anyMoved = true;

                if (accumulate)
                {
                    verts[i] = p + n * (weight * accumulateRate);
                }
                else
                {
                    verts[i] = p + (target - p) * Mathf.Clamp01(weight * lerpScale); // see Clamp01 note on Clay
                }

                _dirtyVertexScratch.Add(i);
            }

            if (anyMoved) MarkPositionMirrorStale();
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

            BeginDirtyVertices();
            foreach (Vector3 sign in MirrorSigns())
            {
                BeginMirroredDab(sign);
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                ApplyFlattenBrushLocal(Vector3.Scale(localPoint, sign), mirroredNormal, positive);
            }

            FlushDirtyVertices();
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
            sculptableMesh.CopyStrokeStartPositions(candidates, _nativeStrokeStart);

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
                PlaneWeightsOut = _nativeClayPlaneWeights,
                WeightedPosOut = _nativeClayWeightedPos,
                WeightedNormalOut = _nativeClayWeightedNormal,
                LocalPoint = localPoint,
                BrushRadius = brushRadius,
                Tangent0 = Vector3.zero,
                Bitangent0 = Vector3.zero,
                TipRoundness = 1f,
                EdgeSoftness = 1f,
                FrontFacingOnly = frontFacingOnly,
                CameraLocalPos = _dabCameraLocal,
            };
            weightJob.Schedule(candidates.Count, 32).Complete();

            // Sequential reduction across the footprint, on the main thread for the same reason
            // Clay's is (see ClayWeightJob). Weights come from the job; the weighted sums are
            // recomputed here from _nativeStrokeStart rather than read out of the job's
            // WeightedPosOut, because Flatten's plane is anchored to where the stroke FOUND the
            // surface, not to where its own earlier frames have already pushed it - see
            // ApplyFlattenBrushLocal. Two multiplies per candidate in a loop that already runs,
            // against a second copy of ClayWeightJob differing only in which array it reduces.
            Vector3 planeOriginSum = Vector3.zero, planeNormalSum = Vector3.zero;
            float planeWeightSum = 0f;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                // The mask-free weight, for the same reason Clay's reduction uses it - see
                // ClayWeightJob.Execute. Flatten is if anything the more sensitive of the two: it
                // projects the footprint ONTO this plane, so a plane tilted by a nearby mask does
                // not merely deposit unevenly, it shears the surface toward the wrong flat.
                float w = _nativeClayPlaneWeights[ci];
                planeOriginSum += _nativeStrokeStart[ci] * w;
                planeNormalSum += _nativeNormalsIn[ci] * w;
                planeWeightSum += w;
            }
            if (planeWeightSum <= 1e-6f) return;

            Vector3 planeNormal = planeNormalSum.sqrMagnitude > 1e-8f ? planeNormalSum.normalized : localNormal;
            Vector3 planeOrigin = planeOriginSum / planeWeightSum + planeNormal * (brushRadius * flattenPlaneOffset);

            var dispJob = new FlattenDisplacementJob
            {
                PositionsIn = _nativePositionsIn,
                StrokeStartIn = _nativeStrokeStart,
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
            Vector3 cameraLocalPos = _dabCameraLocal; // this dab's viewpoint - see _dabCameraLocal
            SculptableMesh mesh = sculptableMesh;
            float[] mask = mesh.Mask;
            float radiusSqr = brushRadius * brushRadius;
            float invRadius = 1f / brushRadius;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 p = verts[i];
                // Squared first - see ApplySmoothBrushLocalManaged.
                float sqrDist = (p - localPoint).sqrMagnitude;
                if (sqrDist > radiusSqr) { weights[ci] = 0f; continue; }

                Vector3 n = normals[i];
                float t01 = 1f - Mathf.Sqrt(sqrDist) * invRadius;
                // Plane weight (no mask) and displacement weight (masked) - see
                // ClayWeightJob.Execute, and ApplyFlattenBrushLocalJob's reduction for why Flatten
                // in particular cannot afford a mask-tilted plane.
                float planeW = t01 * t01 * (3f - 2f * t01) // smoothstep
                    * FrontFacingWeight(frontFacingOnly, n, p, cameraLocalPos);
                weights[ci] = planeW * (1f - mask[i]); // masked-out vertices hold still

                // StrokeStartPosition, not verts[i] - see ApplyFlattenBrushLocal on why the
                // plane is anchored to the surface this stroke began with.
                planeOriginSum += mesh.StrokeStartPosition(i) * planeW;
                planeNormalSum += n * planeW;
                planeWeightSum += planeW;
            }

            if (planeWeightSum <= 1e-6f) return;

            Vector3 planeNormal = planeNormalSum.sqrMagnitude > 1e-8f
                ? planeNormalSum.normalized : localNormal;
            // The plane the footprint gets projected onto, slid along its own normal by the
            // Plane Offset control - see flattenPlaneOffset for what the two directions mean.
            Vector3 planeOrigin = planeOriginSum / planeWeightSum + planeNormal * (brushRadius * flattenPlaneOffset);

            bool anyMoved = false;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                float weight = weights[ci];
                if (weight <= 0f) continue;
                int i = candidates[ci];

                mesh.RecordUndoBeforeIfNeeded(i);
                anyMoved = true;

                // Signed height above the plane. Flatten cancels it; contrast doubles down on it.
                Vector3 moved = verts[i];
                float along = Vector3.Dot(moved - planeOrigin, planeNormal);
                // Clamp01 for the same reason Clay's does - this is a lerp fraction toward the
                // plane, and a frame hitch with a large dt would otherwise overshoot past it.
                float lerp = Mathf.Clamp01(weight * lerpFactorScale);
                moved += planeNormal * ((positive ? -along : along) * lerp);

                if (!positive)
                {
                    // Contrast only - see FlattenContrastLimit for why this direction alone
                    // needs bounding, and why the bound is symmetric.
                    float fromStart = Vector3.Dot(moved - mesh.StrokeStartPosition(i), planeNormal);
                    if (fromStart > maxOffStart) moved -= planeNormal * (fromStart - maxOffStart);
                    else if (fromStart < -maxOffStart) moved -= planeNormal * (fromStart + maxOffStart);
                }

                verts[i] = moved;
                _dirtyVertexScratch.Add(i);
            }

            if (anyMoved) MarkPositionMirrorStale();
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

            BeginDirtyVertices();
            foreach (Vector3 sign in MirrorSigns())
            {
                BeginMirroredDab(sign);
                ApplySmoothBrushLocal(Vector3.Scale(localPoint, sign));
            }

            FlushDirtyVertices();
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
            RefreshPositionMirror(verts); // full-mesh mirror, refreshed only if anything moved - see its remarks

            // Grown before the gather - see ApplySurfaceRelaxLocalJob for why the order matters.
            EnsureLaplacianCandidates(candidates.Count);
            GatherCandidatesNative(candidates, verts, sculptableMesh.Normals, sculptableMesh.Mask);

            NativeArray<int> laplacianCandidates = _nativeLaplacianCandidates;
            NativeArray<int> vertexToSlot = _nativeVertexToSlot;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int globalIdx = candidates[ci];
                laplacianCandidates[ci] = globalIdx;
                vertexToSlot[globalIdx] = ci;
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
                CameraLocalPos = _dabCameraLocal,
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
            SculptableMesh mesh = sculptableMesh;
            NativeArray<float> weights = _nativeClayWeights;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int globalIdx = candidates[ci];
                vertexToSlot[globalIdx] = -1; // targeted reset, not a full-array clear - see field remarks
                if (!anyPassRan || weights[ci] <= 0f) continue;
                mesh.RecordUndoBeforeIfNeeded(globalIdx);
                verts[globalIdx] = readBuf[ci];
                _dirtyVertexScratch.Add(globalIdx);
            }
            if (anyPassRan) MarkPositionMirrorStale();
        }

        private JobHandle ScheduleSmoothRelaxJob(int candidateCount, NativeArray<Vector3> readBuf, NativeArray<Vector3> writeBuf,
            NativeArray<int> adjOffsets, NativeArray<int> adjNeighbors, float passFactor, float lerpFactorScale, JobHandle dependency)
        {
            var job = new SmoothRelaxJob
            {
                Candidates = _nativeLaplacianCandidates,
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
            Vector3 cameraLocalPos = _dabCameraLocal; // this dab's viewpoint - see _dabCameraLocal

            float[] mask = sculptableMesh.Mask;
            float radiusSqr = brushRadius * brushRadius;
            float invRadius = 1f / brushRadius;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 p = verts[i];
                // Squared first: over half a query's candidates fall outside the radius entirely
                // (see RelaxWeightJob's measurement), and taking a square root just to discard
                // them was the single most-executed operation in this loop.
                float sqrDist = (p - localPoint).sqrMagnitude;
                if (sqrDist > radiusSqr) { weights[ci] = 0f; continue; }

                float t01 = 1f - Mathf.Sqrt(sqrDist) * invRadius;
                weights[ci] = t01 * t01 * (3f - 2f * t01) * (1f - mask[i]) // smoothstep, masked-out
                    * FrontFacingWeight(frontFacingOnly, normals[i], p, cameraLocalPos);
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
            SculptableMesh mesh = sculptableMesh;
            float lerpScale = passFactor * SmoothSpeed * dt;
            bool anyMoved = false;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                float w = weights[ci];
                if (w <= 0f) continue;
                int i = candidates[ci];

                Vector3 toAverage = mesh.GetNeighborAverage(i) - verts[i];
                mesh.RecordUndoBeforeIfNeeded(i);
                verts[i] += toAverage * Mathf.Clamp01(w * lerpScale); // see Clamp01 note on Clay
                anyMoved = true;
                _dirtyVertexScratch.Add(i);
            }

            if (anyMoved) MarkPositionMirrorStale();
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
                    EndActiveDrags();
                    return;
                }

                Ray dragRay = cam.ScreenPointToRay(mouse.position.ReadValue());
                if (RayPlaneIntersect(dragRay, _dragPlanePoint, _dragPlaneNormal, out Vector3 current))
                {
                    Vector3 worldDelta = current - _lastDragPoint;
                    if (worldDelta.sqrMagnitude > 1e-12f)
                    {
                        Vector3 localDelta = sculptableMesh.transform.InverseTransformVector(worldDelta);
                        BeginDirtyVertices();
                        foreach (var (selection, sign) in _grabSelections)
                        {
                            sculptableMesh.ApplyGrabDelta(selection, Vector3.Scale(localDelta, sign));
                            int[] indices = selection.Indices;
                            for (int k = 0; k < indices.Length; k++) _dirtyVertexScratch.Add(indices[k]);
                        }
                        MarkPositionMirrorStale();
                        FlushDirtyVertices();
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
            var selections = new List<(SculptableMesh.GrabSelection, Vector3)>();
            foreach (Vector3 sign in MirrorSigns())
            {
                // Each mirrored selection is judged from its OWN mirrored viewpoint - see
                // _dabCameraLocal. A grab is picked once and then dragged for the whole gesture, so
                // getting this wrong here strands half of the far side's vertices behind for the
                // entire drag rather than merely weakening one frame of it.
                BeginMirroredDab(sign);
                var selection = sculptableMesh.SelectGrab(Vector3.Scale(localHit, sign), brushRadius, frontFacingOnly, _dabCameraLocal);
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

        // Every "don't leave a drag mid-gesture while X begins" guard scattered through this
        // file now needs to interrupt BOTH click-drag brushes, not just Move - Pose is the same
        // shape of gesture (see HandlePoseInput). One wrapper instead of duplicating a second
        // call at every one of those sites, which is what actually calls each real EndXDrag -
        // both are cheap no-ops when their own drag isn't active, so calling both unconditionally
        // costs nothing.
        private void EndActiveDrags()
        {
            EndMoveDrag();
            EndPoseDrag();
        }

        // Same click/hold-drag/release shape as HandleMoveDrag above, right down to reusing
        // RayPlaneIntersect against a camera-facing plane through the grab point. The
        // difference is entirely in what SelectPose/ApplyPoseDelta do with that drag target -
        // see SculptableMesh's Pose brush section for the actual chain/falloff logic. Masking
        // works the same direction here as every other brush (masked = protected): to pose an
        // arm, mask everything EXCEPT the arm, then drag from inside the unmasked part.
        private void HandlePoseInput(Mouse mouse, bool overUI, bool altHeld)
        {
            if (_isPoseDragging)
            {
                if (!mouse.leftButton.isPressed)
                {
                    EndActiveDrags();
                    return;
                }

                Ray dragRay = cam.ScreenPointToRay(mouse.position.ReadValue());
                if (RayPlaneIntersect(dragRay, _poseDragPlanePoint, _poseDragPlaneNormal, out Vector3 current))
                {
                    // Unlike Move, this reads the CURRENT drag point fresh off the plane rather
                    // than accumulating a delta - ApplyPoseDelta re-derives every vertex from
                    // its stroke-start position each frame, so there's nothing to accumulate
                    // onto (see its remarks for why that's the more robust choice for a
                    // rotation-based deform).
                    Vector3 localCurrent = sculptableMesh.transform.InverseTransformPoint(current);
                    BeginDirtyVertices();
                    foreach (var (selection, sign) in _poseSelections)
                    {
                        sculptableMesh.ApplyPoseDelta(selection, Vector3.Scale(localCurrent, sign));
                        int[] indices = selection.Indices;
                        for (int k = 0; k < indices.Length; k++) _dirtyVertexScratch.Add(indices[k]);
                    }
                    MarkPositionMirrorStale();
                    FlushDirtyVertices();
                }

                _isHovering = true;
                _hoverPoint = current;
                _previewPositive = true;
                return;
            }

            // Not dragging: still recompute the chain every hover frame from wherever the
            // cursor currently is, so the guide line tracks the cursor live - Blender does the
            // same, and it's what actually lets you see what you're about to grab before you
            // commit to a drag. Only the CLICK below promotes an already-fresh selection into an
            // active drag; nothing about the selection itself is special-cased for the click.
            _isHovering = false;
            if (overUI || altHeld) { _poseSelections = null; return; }

            Ray hoverRay = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(hoverRay, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);
            _isHovering = hasHit;
            if (!_isHovering) { _poseSelections = null; return; }

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;
            _previewPositive = true;

            Vector3 localHit = sculptableMesh.transform.InverseTransformPoint(hitPoint);
            var selections = new List<(SculptableMesh.PoseSelection, Vector3)>();
            foreach (Vector3 sign in MirrorSigns())
            {
                var selection = sculptableMesh.SelectPose(Vector3.Scale(localHit, sign), brushRadius, poseRigidity, poseSegments);
                if (selection.IsValid) selections.Add((selection, sign));
            }
            _poseSelections = selections.Count > 0 ? selections : null;

            if (!mouse.leftButton.wasPressedThisFrame) return;

            if (_poseSelections == null)
            {
                // Clicked outside any unmasked region, or the unmasked island has no masked
                // neighbor anywhere to anchor against - Pose has nothing sensible to do, same
                // as Move missing the mesh entirely. The toast is worth it here specifically
                // because the reason for "nothing happened" (mask setup, not a missed click)
                // is not otherwise visible.
                TriggerActionToast("Mask an anchor first");
                return;
            }

            _isPoseDragging = true;
            _poseDragPlanePoint = hitPoint;
            _poseDragPlaneNormal = -cam.transform.forward;

            if (logRayHits) Debug.Log($"[Sculpt] Pose grab started at {hitPoint}");
        }

        // Unconditional (no _isPoseDragging guard) - unlike EndMoveDrag, _poseSelections is also
        // the live hover-preview's own state (see HandlePoseInput), not exclusively an active-
        // drag flag, so this needs to clear it even when called mid-hover (e.g. switching away
        // from Pose to another brush - see EndActiveDrags' call sites) rather than only at the
        // end of an actual drag.
        private void EndPoseDrag()
        {
            _poseSelections = null;
            _isPoseDragging = false;
        }

        // Blender-style Pose Brush guide line: live from the moment the cursor hovers a posable
        // region with Pose selected, through the drag, same as Blender's own - not just while a
        // drag is underway (see HandlePoseInput, which now keeps _poseSelections fresh on every
        // hover frame too). Called from Update() every frame the app runs, not just while
        // hovering/posing - the early-out below is what keeps that free the rest of the time.
        private void UpdatePoseChainVisual()
        {
            if (_poseSelections == null || sculptableMesh == null)
            {
                for (int i = 0; i < _poseChainLines.Count; i++)
                    if (_poseChainLines[i] != null) _poseChainLines[i].gameObject.SetActive(false);
                return;
            }

            while (_poseChainLines.Count < _poseSelections.Count) _poseChainLines.Add(CreatePoseChainLine());

            float width = Mathf.Max(brushRadius * PoseChainWidthFactor, 0.0005f);
            for (int s = 0; s < _poseSelections.Count; s++)
            {
                var (selection, sign) = _poseSelections[s];
                LineRenderer lr = _poseChainLines[s];
                Vector3[] points = selection.ChainPoints;
                if (points == null || points.Length < 2) { lr.gameObject.SetActive(false); continue; }

                lr.gameObject.SetActive(true);
                lr.widthMultiplier = width;
                lr.positionCount = points.Length;
                for (int p = 0; p < points.Length; p++)
                    lr.SetPosition(p, sculptableMesh.transform.TransformPoint(Vector3.Scale(points[p], sign)));
            }
            for (int s = _poseSelections.Count; s < _poseChainLines.Count; s++)
                _poseChainLines[s].gameObject.SetActive(false);
        }

        private LineRenderer CreatePoseChainLine()
        {
            var go = new GameObject("PoseChainLine");
            go.transform.SetParent(transform, false);

            if (_poseChainMaterial == null)
            {
                // Same overlay-shader-with-Sprites/Default-fallback dance as
                // TransformGizmo.ApplyUnlitColor - see this field's own remarks for why the
                // overlay shader is what actually matters here.
                Shader overlayShader = Shader.Find("Custom/BrushPreviewOverlay");
                _poseChainMaterial = overlayShader != null ? new Material(overlayShader) : new Material(Shader.Find("Sprites/Default"));
                _poseChainMaterial.name = "Pose Chain Line (Runtime)";
                if (overlayShader != null) _poseChainMaterial.SetColor(PoseChainColorId, PoseChainColor);
                else _poseChainMaterial.color = PoseChainColor;
            }

            var lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = _poseChainMaterial;
            lr.useWorldSpace = true;
            lr.numCapVertices = 4;
            lr.numCornerVertices = 2;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;
            go.SetActive(false);
            return lr;
        }

        // internal (not private) so TransformGizmo can reuse the exact same axis-constrained
        // drag technique Move-brush dragging already uses, for its own Move/Scale handles - no
        // .asmdef boundary in this project, so internal is enough without a public API change.
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
            EndActiveDrags();
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
            EndActiveDrags();

            int changed = SymmetryOps.MakeSymmetric(sculptableMesh, symmetryAxis, symmetryToleranceScale,
                                                    sourceIsPositive, out int pairs, out int unmatched,
                                                    out int carried);

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

            // The unmatched count rides along on success too: it is the part of the model that
            // has no counterpart to be mirrored onto, and leaving it out is what let a partial
            // mirror look like a complete one. It is now carried along with the surface around it
            // rather than left standing (see SymmetryTools.CarryUnmatched), so the message says
            // which of the two happened to it.
            string leftover = unmatched > 0
                ? (carried > 0 ? $", {carried} of {unmatched} unmatched carried along"
                               : $", {unmatched} unmatched")
                : string.Empty;
            return $"Mirrored {from} onto {to}: {changed} of {pairs} pairs{leftover}";
        }

        /// Cuts the selected object at the symmetry plane and rebuilds the far side as a
        /// reflection of the near one. The unconditional version of MakeSymmetric: it needs no
        /// vertex correspondence, so it is what to reach for when MakeSymmetric reports the model
        /// is too asymmetric to match up (see SymmetryOps.MirrorAndWeld).
        public string MirrorAndWeld(bool sourceIsPositive)
        {
            if (sculptableMesh == null) return "No object selected";
            EndActiveDrags();

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
            EndActiveDrags();

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
            // own affordance instead - the R-hold density gauge joins them here, its grid/label
            // being its own affordance the same way (see DensityGrid).
            bool sculptToolActive = sculptableMesh != null && cam != null && !RegionSelectActive
                                    && !_isAdjustingRemeshDensity
                                    && (Gizmo == null || Gizmo.Mode == GizmoMode.Sculpt);

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

            // An armed region gesture suppresses the ring (sculptToolActive above) - but leaving
            // the viewport with NOTHING under the pointer is what made arming a mode read as
            // "my cursor is gone". Unity's own arrow does come back, and over a dark viewport at
            // the moment the familiar ring disappears that is not something anyone notices. So
            // the tool gets its own affordance instead: a crosshair, which every marquee tool in
            // every app draws, and which doubles as the signal that a region mode is armed at
            // all. Drawn by SculptUIBuilder from the two properties below, exactly like the ring.
            Mouse regionMouse = Mouse.current;
            _showRegionCrosshair = RegionSelectActive && !_isOverUI && regionMouse != null;
            if (_showRegionCrosshair) _regionCrosshairScreenPos = regionMouse.position.ReadValue();

            _showBrushCursor = show;
            if (show) _brushCursorColor = color;
            Cursor.visible = !show && !_showRegionCrosshair;
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
            public float surfaceRelax;
            public float poseRigidity;
            public int poseSegments;

            public float clayHeightFactor;
            public float clayTipRoundness;
            public float clayEdgeSoftness;
            public float clayPressureRadiusInfluence;
            public float clayPressureSoftnessInfluence;

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
                surfaceRelax = surfaceRelax,
                poseRigidity = poseRigidity,
                poseSegments = poseSegments,

                clayHeightFactor = clayHeightFactor,
                clayTipRoundness = clayTipRoundness,
                clayEdgeSoftness = clayEdgeSoftness,
                clayPressureRadiusInfluence = clayPressureRadiusInfluence,
                clayPressureSoftnessInfluence = clayPressureSoftnessInfluence,

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
            ClayPressureRadiusInfluence = s.clayPressureRadiusInfluence;
            ClayPressureSoftnessInfluence = s.clayPressureSoftnessInfluence;

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
            SurfaceRelax = s.surfaceRelax;
            PoseRigidity = s.poseRigidity;
            // A file from before this setting existed has poseSegments == 0 (JsonUtility's
            // default for an unseen int field) - Clamp below floors that to 1, which reproduces
            // the pre-segments behavior (root always the true anchor) rather than silently
            // producing an invalid 0-segment chain.
            PoseSegments = s.poseSegments > 0 ? s.poseSegments : 4;

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
