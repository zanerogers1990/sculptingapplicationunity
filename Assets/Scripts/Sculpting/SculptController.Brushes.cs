using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Every brush: input handlers, stroke pacing, and the Apply* world-to-local, mirror and
    /// Job/Managed layers.
    public partial class SculptController
    {
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

        // Inflate's and Flatten's input handler and world-space wrapper were character-for-character
        // copies of each other, differing only in which Apply*BrushLocal they ended in, so both
        // brushes now route through these two methods. Clay, Crease/Dam Standard and Smooth keep
        // their own handlers because they genuinely differ from this one: Clay and Crease/Dam
        // Standard reset their stroke-continuity state on the frames this path simply returns
        // (and Crease/Dam Standard deliberately skip UpdateStrokeSpeed), while Smooth has no
        // polarity, a neutral preview and no stroke-speed tracking. Move, Pose and mask painting
        // are different gestures altogether.
        //
        // The delegates are cached rather than passed as method groups: converting a method group
        // allocates a new delegate on every call, and the handler runs every frame the brush hovers.
        private Action<Vector3, Vector3, bool> _applyInflateBrushLocal;
        private Action<Vector3, Vector3, bool> _applyFlattenBrushLocal;

        private void HandleStandardBrushInput(Mouse mouse, bool overUI, bool altHeld,
            Action<Vector3, Vector3, bool> applyBrushLocal)
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
                ApplyMirroredBrush(hitPoint, hitNormal, invertHeld ? !isPositive : isPositive, applyBrushLocal);
            else if (rightHeld)
                ApplyMirroredBrush(hitPoint, hitNormal, !isPositive, applyBrushLocal);
        }

        private void ApplyMirroredBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive,
            Action<Vector3, Vector3, bool> applyBrushLocal)
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
                applyBrushLocal(Vector3.Scale(localPoint, sign), mirroredNormal, positive);
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

            // Read once here and handed to whichever path runs, instead of inside each of them, so
            // both paths are pure functions of their arguments - which is what lets a test run the
            // two on identical inputs (see SculptControllerJobParityTests). Same value either way:
            // Time.deltaTime cannot change within a frame.
            float dt = Time.deltaTime;
            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyInflateBrushLocalJob(localPoint, localNormal, positive, dt, candidates, verts, normals);
            else
                ApplyInflateBrushLocalManaged(localPoint, localNormal, positive, dt, candidates, verts, normals);
        }

        private void ApplyInflateBrushLocalJob(Vector3 localPoint, Vector3 localNormal, bool positive, float dt, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
            float sign = positive ? 1f : -1f;
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

        private void ApplyInflateBrushLocalManaged(Vector3 localPoint, Vector3 localNormal, bool positive, float dt, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
            float sign = positive ? 1f : -1f;
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

            float dt = Time.deltaTime; // read once for both paths - see ApplyInflateBrushLocal
            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyFlattenBrushLocalJob(localPoint, localNormal, positive, dt, candidates, verts, normals);
            else
                ApplyFlattenBrushLocalManaged(localPoint, localNormal, positive, dt, candidates, verts, normals);
        }

        private void ApplyFlattenBrushLocalJob(Vector3 localPoint, Vector3 localNormal, bool positive, float dt, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
            GatherCandidatesNative(candidates, verts, normals, sculptableMesh.Mask);
            sculptableMesh.CopyStrokeStartPositions(candidates, _nativeStrokeStart);

            // Clay's pass-1 job, reused with the round tip (TipRoundness 1) and a full-radius
            // taper (EdgeSoftness 1) - which reduces ClayTipShapeT01/ClayFalloff to a quintic
            // falloff over the whole radius, the same call ApplyFlattenBrushLocalManaged makes, and
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

        private void ApplyFlattenBrushLocalManaged(Vector3 localPoint, Vector3 localNormal, bool positive, float dt, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
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
                // ClayFalloff at full edge softness - the quintic ApplyFlattenBrushLocalJob gets from
                // ClayWeightJob. Was a cubic smoothstep, left behind when ClayFalloff went quintic.
                float planeW = ClayFalloff(t01, 1f)
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

            float dt = Time.deltaTime; // read once for both paths - see ApplyInflateBrushLocal
            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplySmoothBrushLocalJob(localPoint, dt, candidates, verts);
            else
                ApplySmoothBrushLocalManaged(localPoint, dt, candidates, verts);
        }

        // See SmoothRelaxJob's remarks for why this is a Jacobi-style parallel relaxation rather
        // than the managed method's Gauss-Seidel-style in-place one - a deliberate, necessary
        // substitution for parallelism, not a bug.
        private void ApplySmoothBrushLocalJob(Vector3 localPoint, float dt, List<int> candidates, Vector3[] verts)
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

        private void ApplySmoothBrushLocalManaged(Vector3 localPoint, float dt, List<int> candidates, Vector3[] verts)
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
    }
}
