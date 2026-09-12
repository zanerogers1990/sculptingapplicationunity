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

        // Crease's stroke-continuity memory, in mesh-local space - null between strokes (mouse
        // up / hover lost / brush switched) so a fresh stroke starts clean. Drives BOTH the
        // distance-spaced dab stepper (ApplyCarveStroke) and the stroke-travel direction the
        // carve is built around: Crease pinches ACROSS that direction.
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
        // Next-pass positions for the managed Laplacian passes (Smooth, Surface Relax), which
        // compute every target before writing any - see RunSmoothRelaxationPass.
        private Vector3[] _laplacianTargetScratch = System.Array.Empty<Vector3>();

        private Vector3[] LaplacianTargetScratch(int count)
        {
            if (_laplacianTargetScratch.Length < count) _laplacianTargetScratch = new Vector3[count];
            return _laplacianTargetScratch;
        }

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

            /// Grows the stamp array to cover a mesh that just got bigger, KEEPING what is already
            /// marked. Distinct from Clear, which starts a new build: dynamic topology appends
            /// vertices part-way through a frame whose dirty set is still being used, and throwing
            /// that set away mid-frame would lose everything the dab before the refine had moved.
            /// Append-only growth is what makes keeping it safe - every existing index still names
            /// the vertex it always did.
            public void EnsureCapacity(int vertexCount)
            {
                if (_stamp == null) { _stamp = new int[vertexCount]; _generation = 1; return; }
                if (_stamp.Length >= vertexCount) return;
                System.Array.Resize(ref _stamp, vertexCount);
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
            _dirtyVertexScratch.Clear(sculptableMesh.VertexCount);
        }

        /// Pushes whatever the frame's brush application moved into the mesh, in one call. Skips
        /// entirely when nothing moved - a held-but-stationary stroke places no dabs at all, and
        /// an empty apply would still walk the sync/filter preamble for no result.
        private void FlushDirtyVertices()
        {
            FoldDirtyIntoStroke();
            PushDirtyVertices();
        }

        /// Folds the frame's dirty set into the stroke-wide one, the one place every brush's
        /// per-frame flush already passes through - see _strokeDirtyVertexScratch's remarks.
        private void FoldDirtyIntoStroke()
        {
            List<int> items = _dirtyVertexScratch.Items;
            for (int i = 0; i < items.Count; i++) _strokeDirtyVertexScratch.Add(items[i]);
        }

        private void PushDirtyVertices()
        {
            if (_dirtyVertexScratch.Count == 0) return;
            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch.Items);
            RefineTopologyIfDue();
        }

        /// The single hook dynamic topology enters through. Sits here, after the frame's dab has
        /// been applied, because this is the one place EVERY brush's per-frame flush passes
        /// through - so no brush can be added later that silently skips it, and no brush needed a
        /// line of its own.
        ///
        /// The refined region is derived from the dirty set rather than plumbed down from the
        /// brush: that set IS the footprint, by construction, across every mirror sign and every
        /// sub-dab of the frame. Taking it from here means the refine follows mirrored strokes and
        /// multi-dab frames for free, and the brush handlers stay untouched.
        private void RefineTopologyIfDue()
        {
            if (sculptableMesh == null) return;

            DynamicTopology.DynamicTopologyRemesher remesher = DynamicTopologyRemesher;
            if (!remesher.CanRefine(sculptableMesh, currentBrush)) return;
            // A masked whole-object Transpose/Scale re-derives its result from a pre-drag snapshot
            // every frame (SculptableMesh.BeginMaskedTransform), which changing the vertex count
            // under would leave indexing past the end of.
            if (sculptableMesh.IsMaskedTransformActive) return;

            if (!TryVertexBounds(_dirtyVertexScratch.Items, out Vector3 centreLocal, out float radiusLocal)) return;
            if (!remesher.ShouldRefine(sculptableMesh, centreLocal, radiusLocal)) return;
            RunRefine(remesher, centreLocal, radiusLocal);
        }

        /// Move and Pose refine at the EDGES of a gesture - once as it starts, once as it ends -
        /// rather than continuously the way the deforming brushes do.
        ///
        /// Not a limitation of the remesher but of the brushes: both capture a vertex selection
        /// with fixed indices and weights on the press frame and re-derive the whole drag from it
        /// (SculptableMesh.SelectGrab/SelectPose), so changing the vertex set underneath aims those
        /// indices at different geometry. Blender excludes grab-type brushes from dyntopo outright
        /// for the same reason. Refining at the bounds gets most of the value anyway: on press the
        /// pull starts from adequate density instead of dragging three vertices into a spike, and
        /// on release whatever the drag stretched is brought back to the target edge length.
        private void RefineTopologyAtStrokeBounds(Vector3 centreLocal, float radiusLocal)
        {
            if (sculptableMesh == null) return;
            DynamicTopology.DynamicTopologyRemesher remesher = DynamicTopologyRemesher;
            if (!remesher.CanRefineAtStrokeBounds(sculptableMesh, currentBrush)) return;
            if (sculptableMesh.IsMaskedTransformActive) return;
            RunRefine(remesher, centreLocal, radiusLocal);
        }

        /// The end of a Move or Pose gesture, over everything the whole gesture touched - so a long
        /// pull is re-tessellated along its entire length, not just where it finished.
        private void RefineTopologyAfterStroke()
        {
            if (sculptableMesh == null) return;
            if (!DynamicTopologyRemesher.CanRefineAtStrokeBounds(sculptableMesh, currentBrush)) return;
            if (!TryVertexBounds(_strokeDirtyVertexScratch.Items, out Vector3 centre, out float radius)) return;
            RefineTopologyAtStrokeBounds(centre, radius);
        }

        private void RunRefine(DynamicTopology.DynamicTopologyRemesher remesher, Vector3 centreLocal, float radiusLocal)
        {
            DynamicTopology.TopologyPatch patch = remesher.Refine(sculptableMesh, centreLocal, radiusLocal);
            if (patch == null) return;

            sculptableMesh.ApplyTopologyPatch(patch);
            // Folded into the stroke's own undo entry rather than pushed as one of its own, so a
            // single undo press takes back the stroke AND the topology it created.
            sculptableMesh.AccumulateStrokeTopology(patch);
            // The refine both MOVED vertices (the relax pass) and ADDED them, and the Laplacian
            // jobs' full-mesh position mirror knows about neither - see RefreshPositionMirror for
            // what reading it unrefreshed does to the surface.
            MarkPositionMirrorStale();

            // The mesh the rest of this frame's bookkeeping refers to just changed size. Both dirty
            // sets are indexed by vertex and would reject (silently, by bounds check) every vertex
            // the refine added, so they are re-sized here rather than at the next stroke - see
            // DirtyVertexSet.Clear.
            _dirtyVertexScratch.EnsureCapacity(sculptableMesh.VertexCount);
            _strokeDirtyVertexScratch.EnsureCapacity(sculptableMesh.VertexCount);
        }

        /// The bounding sphere of a set of vertices, in local space. Returns false when that is
        /// empty or degenerate.
        private bool TryVertexBounds(List<int> items, out Vector3 centreLocal, out float radiusLocal)
        {
            centreLocal = default;
            radiusLocal = 0f;

            if (items == null || items.Count == 0) return false;

            Vector3[] verts = sculptableMesh.Vertices;
            int vertexCount = sculptableMesh.VertexCount;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            bool any = false;
            for (int k = 0; k < items.Count; k++)
            {
                int i = items[k];
                if ((uint)i >= (uint)vertexCount) continue;
                Vector3 p = verts[i];
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
            if (!any) return false;

            centreLocal = (min + max) * 0.5f;
            radiusLocal = Mathf.Max((max - min).magnitude * 0.5f, 1e-5f);
            return true;
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
        // detail work Crease exists for. Pacing is a statement about travel relative
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
        // Crease/Inflate got the speed factor with no such compensation, and at a
        // normal carving speed that silently cost them roughly 4x. That is the difference
        // between "calmer" and the reported "straight up not working". Sized so one pass at max
        // Brush Strength reaches the full plateau/dab depth, which puts the 0.1 default at a
        // clearly visible cut rather than a rounding error.
        // Only Inflate is left on this path: Crease has since moved to real
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
        /// worth of depth and stops - as used by Inflate. Crease used it too
        /// until it moved to distance-spaced dabs (see ApplyCarveStroke) and took Clay's
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

        /// Crease's equivalents of the two above, and speed-free for exactly the
        /// same reason: it now places a fixed quantum of carve every CreaseDabSpacing
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
        private void BeginMirroredDab(Vector3 sign)
        {
            _dabCameraLocal = Vector3.Scale(
                sculptableMesh.transform.InverseTransformPoint(cam.transform.position), sign);
            _dabFlipMask = FlipMaskOf(sign);
        }

        /// Which axes the dab being applied is reflected across: bit 0 X, bit 1 Y, bit 2 Z. 0 - the
        /// default, and what a path called without a mirror loop sees - is the unmirrored dab.
        /// Set by BeginMirroredDab alongside _dabCameraLocal, for the same reason: anything a dab
        /// derives from a DIRECTION has to be reflected with it, not rebuilt on the far side.
        private int _dabFlipMask;

        private static int FlipMaskOf(Vector3 sign) =>
            (sign.x < 0f ? 1 : 0) | (sign.y < 0f ? 2 : 0) | (sign.z < 0f ? 4 : 0);

        private static Vector3 SignOfFlipMask(int mask) =>
            new Vector3((mask & 1) != 0 ? -1f : 1f, (mask & 2) != 0 ? -1f : 1f, (mask & 4) != 0 ? -1f : 1f);

        /// BuildTangentBasis for the dab being applied, reflected along with it.
        ///
        /// BuildTangentBasis crosses the normal with a FIXED world axis, and a reflection does not
        /// commute with that: fed a mirrored normal it hands back a frame whose tangent points the
        /// opposite way along the mirror image of the primary one. Clay's alpha stamp is read in
        /// that frame, so the far side of every mirrored stroke got the stamp flipped in its own
        /// frame instead of the mirror image of the near side's - invisible on a round soft
        /// circle, 11% of the stroke's displacement with the Noise alpha (15% rotated) three brush
        /// radii from the plane (SymmetryDriftTests). Building the frame from the normal reflected
        /// back to the primary side, then reflecting the frame forward, gives the mirrored dab the
        /// mirror image of the primary frame exactly, so it samples the stamp at identical
        /// coordinates. The primary dab (mask 0) is unchanged bit for bit.
        private void BuildDabTangentBasis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent)
        {
            if (_dabFlipMask == 0)
            {
                BuildTangentBasis(normal, out tangent, out bitangent);
                return;
            }

            Vector3 sign = SignOfFlipMask(_dabFlipMask);
            BuildTangentBasis(Vector3.Scale(normal, sign), out tangent, out bitangent);
            tangent = Vector3.Scale(tangent, sign);
            bitangent = Vector3.Scale(bitangent, sign);
        }

        // ------------------------------------------------------------ order-symmetric mirrored dabs

        /// A dab closer than this many of its own reach to a mirror plane is treated as able to see
        /// its mirror image's output. 1 would be exact for dabs that only read and write inside their
        /// reach; the rest covers Smooth reading one ring of neighbours past its footprint, and a
        /// dab's own displacement carrying a vertex over the boundary mid-frame.
        private const float MirrorInteractionMargin = 1.5f;

        /// Walks the mirror signs for one dab (see BeginMirroredDabs / NextMirroredDab).
        ///
        /// Far from every mirror plane this is exactly the old loop - each sign once, in MirrorSigns
        /// order. Where a plane runs through the footprint the order is not innocent: the signs are
        /// applied one after another against the live vertex array, so the second dab reads positions
        /// (fits its plane, measures "deepest carve so far", weighs by distance) through vertices the
        /// first one has just moved, while the first saw the untouched surface. The two halves were
        /// given different operations, always in the same order, so the difference did not average
        /// out - it accumulated stroke after stroke into the "one side slowly becomes different" drift.
        /// Measured on a bitwise-symmetric sphere a third of a radius off the plane: mirror error of
        /// 65% of the stroke's own displacement for Inflate, 12% for Clay (Accumulate), 8% for Dam
        /// Standard, with centreline vertices pushed well off the plane (SymmetryDriftTests).
        ///
        /// There, every ordering that the mirror group maps onto another is applied - each from the
        /// same starting surface, restored in between - and the results are averaged. Reflecting the
        /// model only permutes those orderings, so the average is mirror-symmetric by construction,
        /// while each ordering is still the existing brush behaviour (including the doubled strength a
        /// dab centred ON the plane has always had), so no brush changes character. Signs whose
        /// footprints cannot meet are grouped apart and never repeated: one X plane through the dab
        /// costs two applications of the pair instead of one, and only for dabs that near the plane.
        private struct MirroredDabWalk
        {
            public Vector3 Point;
            public float Reach;
            public bool Symmetric;
            public int Index, Count;              // plain walk over MirrorSigns
            public int Near, Far;                 // flip bits whose planes do / do not reach the footprint
            public int Group, GroupCount;         // current coset of the far flips
            public int Ordering, Step, Orderings; // orderings over the near flips, and position in one
        }

        private readonly List<int> _mirrorGroupVertices = new List<int>();
        private int[] _mirrorGroupStamp;
        private int _mirrorGroupGeneration;
        private Vector3[] _mirrorGroupBefore = Array.Empty<Vector3>();
        private Vector3[] _mirrorGroupDeltaSum = Array.Empty<Vector3>();

        /// True while a repeat ordering is being applied. Per-dab bookkeeping that is not geometry
        /// (Clay's relax centres) must be recorded once, on the first ordering only.
        private bool _mirrorRepeatOrdering;

        /// `reach` is the widest radius the brush's per-sign apply reads or writes vertices within.
        private MirroredDabWalk BeginMirroredDabs(Vector3 localPoint, float reach)
        {
            List<Vector3> signs = MirrorSigns();
            var walk = new MirroredDabWalk { Point = localPoint, Reach = reach, Index = -1, Count = signs.Count, Step = -1 };
            if (signs.Count <= 1) return walk;

            int active = 0;
            for (int k = 0; k < signs.Count; k++) active |= FlipMaskOf(signs[k]);
            float limit = reach * MirrorInteractionMargin;
            int near = 0;
            if ((active & 1) != 0 && Mathf.Abs(localPoint.x) < limit) near |= 1;
            if ((active & 2) != 0 && Mathf.Abs(localPoint.y) < limit) near |= 2;
            if ((active & 4) != 0 && Mathf.Abs(localPoint.z) < limit) near |= 4;
            if (near == 0) return walk;

            walk.Symmetric = true;
            walk.Near = near;
            walk.Far = active & ~near;
            walk.Orderings = 1 << BitCount(near);
            walk.GroupCount = 1 << BitCount(walk.Far);
            return walk;
        }

        /// Advances the walk and points the per-dab frame at the next sign (BeginMirroredDab).
        private bool NextMirroredDab(ref MirroredDabWalk walk, out Vector3 sign)
        {
            if (!walk.Symmetric)
            {
                _mirrorRepeatOrdering = false;
                if (++walk.Index >= walk.Count) { sign = Vector3.one; return false; }
                sign = MirrorSigns()[walk.Index];
                BeginMirroredDab(sign);
                return true;
            }

            if (walk.Step < 0)
            {
                walk.Step = 0;
                BeginMirrorGroup(ref walk);
            }
            else if (++walk.Step == walk.Orderings)
            {
                walk.Step = 0;
                AccumulateMirrorOrdering();
                if (++walk.Ordering < walk.Orderings)
                {
                    RestoreMirrorGroup();
                }
                else
                {
                    CommitMirrorGroup(walk.Orderings);
                    walk.Ordering = 0;
                    if (++walk.Group == walk.GroupCount)
                    {
                        _mirrorRepeatOrdering = false;
                        sign = Vector3.one;
                        return false;
                    }
                    BeginMirrorGroup(ref walk);
                }
            }

            // Ordering o applies near-flips h_o ^ h_0, h_o ^ h_1, ... - one row of the group's own
            // table, which is what makes the SET of orderings map onto itself under any reflection.
            _mirrorRepeatOrdering = walk.Ordering > 0;
            sign = SignOfFlipMask(NthSubmask(walk.Far, walk.Group)
                                  ^ NthSubmask(walk.Near, walk.Ordering) ^ NthSubmask(walk.Near, walk.Step));
            BeginMirroredDab(sign);
            return true;
        }

        /// Snapshots every vertex the current group's dabs can write, before the first of them runs.
        private void BeginMirrorGroup(ref MirroredDabWalk walk)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            if (_mirrorGroupStamp == null || _mirrorGroupStamp.Length != verts.Length)
            {
                _mirrorGroupStamp = new int[verts.Length];
                _mirrorGroupGeneration = 0;
            }
            int generation = ++_mirrorGroupGeneration;
            _mirrorGroupVertices.Clear();

            int far = NthSubmask(walk.Far, walk.Group);
            for (int h = 0; h < walk.Orderings; h++)
            {
                Vector3 centre = Vector3.Scale(walk.Point, SignOfFlipMask(far ^ NthSubmask(walk.Near, h)));
                // The spatial grid's shared buffer - consumed fully before the next query.
                List<int> found = sculptableMesh.QueryNear(centre, walk.Reach);
                for (int k = 0; k < found.Count; k++)
                {
                    int vi = found[k];
                    if ((uint)vi >= (uint)verts.Length || _mirrorGroupStamp[vi] == generation) continue;
                    _mirrorGroupStamp[vi] = generation;
                    _mirrorGroupVertices.Add(vi);
                }
            }

            int count = _mirrorGroupVertices.Count;
            if (_mirrorGroupBefore.Length < count)
            {
                _mirrorGroupBefore = new Vector3[count];
                _mirrorGroupDeltaSum = new Vector3[count];
            }
            for (int u = 0; u < count; u++)
            {
                _mirrorGroupBefore[u] = verts[_mirrorGroupVertices[u]];
                _mirrorGroupDeltaSum[u] = Vector3.zero;
            }
        }

        private void AccumulateMirrorOrdering()
        {
            Vector3[] verts = sculptableMesh.Vertices;
            for (int u = 0; u < _mirrorGroupVertices.Count; u++)
                _mirrorGroupDeltaSum[u] += verts[_mirrorGroupVertices[u]] - _mirrorGroupBefore[u];
        }

        private void RestoreMirrorGroup()
        {
            Vector3[] verts = sculptableMesh.Vertices;
            for (int u = 0; u < _mirrorGroupVertices.Count; u++) verts[_mirrorGroupVertices[u]] = _mirrorGroupBefore[u];
            MarkPositionMirrorStale();
        }

        /// Writes the average of every ordering's displacement. Deltas rather than positions, so a
        /// vertex no ordering touched gets its own position back bit for bit.
        private void CommitMirrorGroup(int orderings)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            float inv = 1f / orderings; // a power of two, so exact
            for (int u = 0; u < _mirrorGroupVertices.Count; u++)
                verts[_mirrorGroupVertices[u]] = _mirrorGroupBefore[u] + _mirrorGroupDeltaSum[u] * inv;
            MarkPositionMirrorStale();
        }

        private static int BitCount(int value)
        {
            int count = 0;
            for (; value != 0; value &= value - 1) count++;
            return count;
        }

        /// The k-th subset of `mask`'s bits, counting in binary over those bits (k's bit 0 selects
        /// the lowest set bit of mask, and so on). Gives every group a fixed enumeration.
        private static int NthSubmask(int mask, int k)
        {
            int result = 0, bit = 0;
            for (int axis = 0; axis < 3; axis++)
            {
                if ((mask & (1 << axis)) == 0) continue;
                if ((k & (1 << bit)) != 0) result |= 1 << axis;
                bit++;
            }
            return result;
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
                // Before the unify pass, so the relaxation it runs sees the final topology rather
                // than smoothing geometry that is about to be re-tessellated underneath it.
                RefineTopologyAfterStroke();
                ApplyPostStrokeUnifyPass();
                // Once per stroke, over everything it touched: the relax shell's quiet moves are not
                // refreshed per frame (that would give back what the drift filter saves), so without
                // this the NEXT stroke could read normals and curvature left stale on one half of the
                // model and fresh on the other - see SculptableMesh.RefreshDroppedVertices.
                sculptableMesh.RefreshStrokeNormalsAndCurvature();
                // Before EndStrokeUndo commits, because it reads whether the stroke changed
                // topology - which EndStrokeUndo clears as it pushes.
                sculptableMesh.ReseatColliderIfTopologyChanged();
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
        private Vector3[] _postStrokeUnifyDeltaScratch = System.Array.Empty<Vector3>();

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

            // Jacobi, not Gauss-Seidel: every target is measured against the surface as the stroke
            // left it, and only then is anything written. Updating in place let each vertex read
            // neighbours this same loop had already moved, so the result depended on the order of
            // `touched` - and that order is primary side first, then the mirrored side in the
            // mirrored query's own cell-walk order, which is not the mirror of the first. A
            // perfectly mirrored stroke therefore came back asymmetric on EVERY release, for every
            // brush, anywhere on the model: measured 0 -> 5e-5 for Crease and 8e-7 -> 5.5e-4 for
            // Inflate three brush radii from the plane (SymmetryDriftTests).
            if (_postStrokeUnifyDeltaScratch.Length < touched.Count)
                _postStrokeUnifyDeltaScratch = new Vector3[touched.Count];
            Vector3[] deltas = _postStrokeUnifyDeltaScratch;
            for (int ci = 0; ci < touched.Count; ci++)
            {
                float w = weights[ci];
                if (w <= 0f) continue;
                int i = touched[ci];
                deltas[ci] = (mesh.GetNeighborAverage(i) - verts[i]) * (w * PostStrokeUnifyAmount);
            }

            bool anyMoved = false;
            for (int ci = 0; ci < touched.Count; ci++)
            {
                if (weights[ci] <= 0f) continue;
                int i = touched[ci];
                mesh.RecordUndoBeforeIfNeeded(i);
                verts[i] += deltas[ci];
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
            // The dabs' own footprints join the stroke's unify set BEFORE relax adds to the frame's
            // dirty set, and relax's additions are pushed to the mesh without joining it. Relax
            // reports a shell vertex dirty only once it has visibly drifted
            // (SculptableMesh.HasVisiblyDrifted), which is a knife edge that two mirrored halves
            // land on differently by float rounding - so the release-time unify pass smoothed a
            // vertex on one side and not its twin. Measured on a centreline Clay stroke: 6.7e-7 of
            // mirror error before unify, 1.6e-5 after; with that knife edge taken out, 1.9e-7
            // (SymmetryDriftTests). The shell has just been relaxed; unify is for the seams between
            // dabs, which are inside their footprints.
            FoldDirtyIntoStroke();
            ApplySurfaceRelaxBatched();
            PushDirtyVertices();
        }

        private void ApplyClayBrushAtLocal(Vector3 localPoint, Vector3 localNormal, bool positive, float dt)
        {
            int centresBefore = _relaxCentres.Count;
            // Order-symmetric near a mirror plane - see MirroredDabWalk. Reach is the widest query
            // ApplyClayBrushLocal makes.
            MirroredDabWalk dabs = BeginMirroredDabs(localPoint, clayTipRoundness < 1f ? brushRadius * Sqrt2 : brushRadius);
            while (NextMirroredDab(ref dabs, out Vector3 sign))
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

        // Shell vertices relax moved this frame by less than the drift filter reports - see
        // RefreshQuietRelaxNormals.
        private readonly List<int> _relaxQuietMoves = new List<int>();

        /// With Front Facing Only on, refreshes the CPU normals of the shell vertices relax moved
        /// too little to report dirty.
        ///
        /// The drift filter leaves those normals stale, which is harmless for display - that is the
        /// filter's point - but relax's own facing test reads them again next frame, and whether a
        /// vertex counted as "quiet" is a knife edge that two mirrored halves land on differently by
        /// float rounding. Through Front Facing Only's steep silhouette ramp that was the largest
        /// remaining seed of mirror drift: a centreline Clay stroke measured 4.2e-4 of mirror error
        /// with the filter as it is and 5.6e-5 with it disabled (SymmetryDriftTests). Only the CPU
        /// normal is refreshed, so the filter keeps its upload savings; with Front Facing Only off
        /// no weight reads these normals, so nothing is paid.
        private void RefreshQuietRelaxNormals()
        {
            if (_relaxQuietMoves.Count == 0) return;
            sculptableMesh.RefreshNormalsAndCurvature(_relaxQuietMoves);
            _relaxQuietMoves.Clear();
        }

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
                Vector3 viewpoint = _relaxCentreCameras[i];
                bool covered = false;
                for (int k = 0; k < w; k++)
                {
                    if ((_relaxCentres[k] - c).sqrMagnitude >= sqrSeparation) continue;
                    // Only a centre seen from the SAME viewpoint can stand in for this one. A dab on
                    // the mirror plane and its mirrored twin land on the same point but carry
                    // mirrored cameras, and merging them handed the whole relax shell around a
                    // centreline stroke the primary side's viewpoint - so with Front Facing Only on,
                    // the two halves were relaxed from different directions. Thinning per viewpoint
                    // also keeps the over-budget pass below symmetric: each mirror sign's centres are
                    // thinned among themselves, in the same dab order, so they thin identically.
                    // Still terminates - each viewpoint's group collapses to one centre at worst,
                    // and there are at most eight.
                    if (_relaxCentreCameras[k] != viewpoint) continue;
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
            int vertexCount = sculptableMesh.VertexCount;
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

            NativeArray<int> adjStarts = sculptableMesh.AdjacencyStarts;
            NativeArray<int> adjCounts = sculptableMesh.AdjacencyCounts;
            NativeArray<int> adjNeighbors = sculptableMesh.AdjacencyNeighbors;
            NativeArray<Vector3> readBuf = _nativePositionsIn;
            NativeArray<Vector3> writeBuf = _nativePositionsOut;

            // One dependency chain, waited on once - same reasoning as Smooth's (see
            // ApplySmoothBrushLocalJob): the passes are inherently sequential, but nothing needs
            // to look at the intermediate results on the main thread.
            JobHandle chain = weightJob.Schedule(candidates.Count, 64);
            for (int pass = 0; pass < fullPasses; pass++)
            {
                chain = ScheduleSurfaceRelaxJob(candidates.Count, readBuf, writeBuf, adjStarts, adjCounts, adjNeighbors, 1f, chain);
                (readBuf, writeBuf) = (writeBuf, readBuf);
            }
            if (partialFactor > 0.001f)
            {
                chain = ScheduleSurfaceRelaxJob(candidates.Count, readBuf, writeBuf, adjStarts, adjCounts, adjNeighbors, partialFactor, chain);
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
                else if (frontFacingOnly) _relaxQuietMoves.Add(globalIdx);
            }
            if (anyPassRan) MarkPositionMirrorStale();
            RefreshQuietRelaxNormals();
        }

        private JobHandle ScheduleSurfaceRelaxJob(int candidateCount, NativeArray<Vector3> readBuf, NativeArray<Vector3> writeBuf,
            NativeArray<int> adjStarts, NativeArray<int> adjCounts, NativeArray<int> adjNeighbors, float passFactor, JobHandle dependency)
        {
            var job = new SurfaceRelaxJob
            {
                Candidates = _nativeLaplacianCandidates,
                AdjacencyStarts = adjStarts,
                AdjacencyCounts = adjCounts,
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
                // Nearest centre's own viewpoint, with exact ties resolved to the most permissive -
                // identical to RelaxWeightJob, see its remarks.
                float facing = 1f;
                for (int c = 0; c < _relaxCentres.Count; c++)
                {
                    float d = (p - _relaxCentres[c]).sqrMagnitude;
                    if (d < sqrDist)
                    {
                        sqrDist = d;
                        facing = FrontFacingWeight(frontFacingOnly, normals[i], p, _relaxCentreCameras[c]);
                    }
                    else if (d == sqrDist && frontFacingOnly)
                    {
                        facing = Mathf.Max(facing, FrontFacingWeight(true, normals[i], p, _relaxCentreCameras[c]));
                    }
                }
                if (sqrDist > relaxRadiusSqr) { weights[ci] = 0f; continue; }
                // Shell profile, not a falloff from the dab centre: RelaxInnerFloor inside the
                // brush's own radius (leave the current dab alone - see its remarks), ramping
                // up to full strength just past brushRadius and tapering again only at the
                // outer rim (relaxRadius) - see RelaxRadiusFactor's remarks for why the SHELL,
                // not the core, is where a seam with a neighboring dab actually needs fixing.
                float spatialWeight = RelaxSpatialWeight(Mathf.Sqrt(sqrDist), brushRadius, relaxRadius,
                    RelaxEdgeSoftness, RelaxInnerFloor);

                // CurvatureDeviationAt is unclamped (unlike the visual cavity tint) - see
                // RelaxCurvatureFloor's remarks for why that distinction is what makes this
                // gate actually separate an ordinary rounded feature from a genuine crease,
                // rather than applying full strength uniformly across the whole shell
                // regardless of whether anything there actually needs it.
                float curvatureDeviation = mesh.CurvatureDeviationAt(i);
                float curvatureFactor = Mathf.Lerp(RelaxCurvatureFloor, 1f,
                    Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(RelaxCurvatureStart, RelaxCurvatureFull, curvatureDeviation)));

                float w = spatialWeight * curvatureFactor * (1f - mask[i]) * facing;
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

            // Same as the job path's scatter - see RefreshQuietRelaxNormals.
            if (frontFacingOnly && (fullPasses > 0 || partialFactor > 0.001f))
            {
                for (int ci = 0; ci < candidates.Count; ci++)
                    if (weights[ci] > 0f && !mesh.HasVisiblyDrifted(candidates[ci])) _relaxQuietMoves.Add(candidates[ci]);
                RefreshQuietRelaxNormals();
            }
        }

        private void RunSurfaceRelaxPass(List<int> candidates, float[] weights, float passFactor)
        {
            SculptableMesh mesh = sculptableMesh;
            Vector3[] verts = mesh.Vertices;
            Vector3[] targets = LaplacianTargetScratch(candidates.Count);
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
                targets[ci] = p + (mesh.GetNeighborAverage(i) - p) * Mathf.Clamp01(w * passFactor);
            }

            // Written only once every target is known - Jacobi, exactly as SurfaceRelaxJob does it.
            // In place (Gauss-Seidel) each vertex read neighbours this pass had already moved, so
            // the result depended on candidate order, which is NOT mirrored between the two halves
            // of a mirrored stroke: measured 8e-4 of mirror error from a Clay stroke three brush
            // radii from the plane on this path, against 5e-6 on the job path (SymmetryDriftTests).
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                if (weights[ci] <= 0f) continue;
                int i = candidates[ci];
                mesh.RecordUndoBeforeIfNeeded(i);
                verts[i] = targets[ci];
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
            // Once per sign, not once per ordering - a repeat ordering re-applies the same dab.
            if (surfaceRelax > 0f && !_mirrorRepeatOrdering)
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
            var plane = new AreaPlaneSums(); // double precision - see AreaPlaneSums
            for (int ci = 0; ci < candidates.Count; ci++)
                plane.Add(_nativeClayWeightedPos[ci], _nativeClayWeightedNormal[ci], _nativeClayPlaneWeights[ci]);
            if (!plane.HasWeight) return;

            Vector3 planeOrigin = plane.Origin;
            Vector3 planeNormal = plane.NormalOr(localNormal);

            BuildDabTangentBasis(planeNormal, out Vector3 tangent, out Vector3 bitangent); // mirrored with the dab - see its remarks
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

            var plane = new AreaPlaneSums(); // double precision - see AreaPlaneSums
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

                plane.Add(p * planeW, n * planeW, planeW);
            }

            if (!plane.HasWeight) return;

            Vector3 planeOrigin = plane.Origin;
            Vector3 planeNormal = plane.NormalOr(localNormal);

            BuildDabTangentBasis(planeNormal, out Vector3 tangent, out Vector3 bitangent); // mirrored with the dab - see its remarks
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


        /// A footprint's weighted area-plane sums, accumulated in double precision.
        ///
        /// Clay and Flatten reduce their plane from thousands of weighted positions summed in
        /// candidate order, and the two halves of a mirrored stroke visit the same - exactly
        /// mirrored - terms in DIFFERENT orders (the spatial query walks cells the same way on
        /// both sides). In single precision each partial sum rounds at around a ten-thousandth of
        /// a unit, which left the far half's plane a few millionths off the near half's on every
        /// dab: the whole of the residual mirror error once everything else was made exact
        /// (SymmetryDriftTests). In double the order-dependent rounding is ~1e-10. The terms
        /// themselves stay float - they are what the jobs produce, and already exact mirrors.
        private struct AreaPlaneSums
        {
            private double _ox, _oy, _oz, _nx, _ny, _nz, _w;

            public void Add(Vector3 weightedPosition, Vector3 weightedNormal, float weight)
            {
                _ox += weightedPosition.x; _oy += weightedPosition.y; _oz += weightedPosition.z;
                _nx += weightedNormal.x; _ny += weightedNormal.y; _nz += weightedNormal.z;
                _w += weight;
            }

            /// The same threshold the float sums were held to.
            public bool HasWeight => _w > 1e-6;

            public Vector3 Origin => new Vector3((float)(_ox / _w), (float)(_oy / _w), (float)(_oz / _w));

            /// The normalized normal sum, or `fallback` where the normals cancel (same 1e-8 bound).
            public Vector3 NormalOr(Vector3 fallback)
            {
                double sqr = _nx * _nx + _ny * _ny + _nz * _nz;
                if (sqr <= 1e-8) return fallback;
                double inv = 1.0 / Math.Sqrt(sqr);
                return new Vector3((float)(_nx * inv), (float)(_ny * inv), (float)(_nz * inv));
            }
        }

        private static void BuildTangentBasis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent)
        {
            Vector3 up = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            tangent = Vector3.Cross(up, normal).normalized;
            bitangent = Vector3.Cross(normal, tangent);
        }

        // Deliberately no UpdateStrokeSpeed call: Crease no longer paces itself off a measured
        // cursor speed, it places a fixed carve every fixed distance travelled (see
        // ApplyCarveStroke). Inflate is the only brush still on the speed factor.
        private void HandleCreaseInput(Mouse mouse, bool overUI, bool altHeld)
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
                leftSculpting ? (invertHeld ? !isPositive : isPositive) : !isPositive);
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

        private void ApplyCarveStroke(Vector3 worldPoint, Vector3 worldNormal, bool positive)
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
                ApplyCarveDab(localPoint, positive);
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
                ApplyCarveDab(Vector3.Lerp(from, localPoint, u), positive);
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

        private void ApplyCarveDab(Vector3 localPoint, bool positive)
        {
            // Order-symmetric near a mirror plane - see MirroredDabWalk.
            MirroredDabWalk dabs = BeginMirroredDabs(localPoint, brushRadius);
            while (NextMirroredDab(ref dabs, out Vector3 sign))
            {
                Vector3 mirroredPoint = Vector3.Scale(localPoint, sign);
                Vector3 mirroredNormal = Vector3.Scale(_carveStrokeNormal, sign).normalized;
                // Mirror the stroke frame the same way Clay mirrors its frozen tip axes, rather
                // than rebuilding it from the mirrored normal - keeps a mirrored groove exactly
                // as stable as the primary one. Scaling by a sign vector preserves length, so
                // the direction stays unit without a re-normalize.
                Vector3 mirroredDir = Vector3.Scale(_carveStrokeDir, sign);
                ApplyCarveDabLocal(mirroredPoint, mirroredNormal, mirroredDir, positive);
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
            bool positive)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyCarveDabLocalJob(localPoint, localNormal, dirLocal, positive, candidates, verts);
            else
                ApplyCarveDabLocalManaged(localPoint, localNormal, dirLocal, positive, candidates, verts);
        }

        private void ApplyCarveDabLocalJob(Vector3 localPoint, Vector3 localNormal, Vector3 dirLocal,
            bool positive, List<int> candidates, Vector3[] verts)
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
                Pinch = creasePinch,
                Sign = sign,
                LerpFactorScale = effectiveStrength * CreaseSpeed * CreaseDabTimeQuantum,
                Accumulate = accumulate,
                DepthRate = sign * creaseDepthFactor * effectiveStrengthAccumulate * CreaseSpeed * CreaseDabTimeQuantum,
                PinchRateScale = creasePinch * effectiveStrengthAccumulate * CreaseSpeed * CreaseDabTimeQuantum,
                FrontFacingOnly = frontFacingOnly,
                CameraLocalPos = _dabCameraLocal,
            };
            job.Schedule(candidates.Count, 32).Complete();

            ScatterJobResults(candidates, verts);
        }

        private void ApplyCarveDabLocalManaged(Vector3 localPoint, Vector3 localNormal, Vector3 dirLocal,
            bool positive, List<int> candidates, Vector3[] verts)
        {
            float sign = positive ? 1f : -1f;
            float effectiveStrength = EffectiveCarveStrength;
            float effectiveStrengthAccumulate = EffectiveCarveStrengthAccumulate;
            float depth = brushRadius * creaseDepthFactor * sign;
            float lerpScale = effectiveStrength * CreaseSpeed * CreaseDabTimeQuantum;
            float depthRate = sign * creaseDepthFactor * effectiveStrengthAccumulate * CreaseSpeed * CreaseDabTimeQuantum;
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

                mesh.RecordUndoBeforeIfNeeded(i);
                anyMoved = true;

                if (accumulate)
                {
                    // Depth keeps digging for as long as the stroke keeps travelling - a
                    // continuous rate, not a target/plateau, same as Clay/Inflate's
                    // accumulate-on push. The pinch stays a bounded pull toward the stroke
                    // line: it's a shape control, not a depth amount, so letting it run away
                    // would just make the groove's cross-section overshoot past the centreline
                    // and oscillate instead of cutting deeper.
                    verts[i] = p + localNormal * (depthRate * weight)
                                 - across * Mathf.Clamp01(weight * pinchRateScale);
                }
                else
                {
                    float carve = depth * weight;
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
        // brushes now route through these two methods. Clay, Crease and Smooth keep their own
        // handlers because they genuinely differ from this one: Clay and Crease reset their
        // stroke-continuity state on the frames this path simply returns (and Crease
        // deliberately skips UpdateStrokeSpeed), while Smooth has no polarity, a neutral preview
        // and no stroke-speed tracking. Move, Pose and mask painting are different gestures
        // altogether.
        //
        // The delegates are cached rather than passed as method groups: converting a method group
        // allocates a new delegate on every call, and the handler runs every frame the brush hovers.
        private Action<Vector3, Vector3, bool, float> _applyInflateBrushLocal;
        private Action<Vector3, Vector3, bool, float> _applyFlattenBrushLocal;

        private void HandleStandardBrushInput(Mouse mouse, bool overUI, bool altHeld,
            Action<Vector3, Vector3, bool, float> applyBrushLocal)
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

            // dt is read once here and handed down - see ApplyInflateBrushLocal.
            if (mouse.leftButton.isPressed && !altHeld)
                ApplyMirroredBrush(hitPoint, hitNormal, invertHeld ? !isPositive : isPositive, Time.deltaTime, applyBrushLocal);
            else if (rightHeld)
                ApplyMirroredBrush(hitPoint, hitNormal, !isPositive, Time.deltaTime, applyBrushLocal);
        }

        private void ApplyMirroredBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive, float dt,
            Action<Vector3, Vector3, bool, float> applyBrushLocal)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            // Not InverseTransformDirection: that is rotation-only and mis-tilts the normal
            // on a non-uniformly scaled object - see SculptableMesh.WorldToLocalNormal.
            Vector3 localNormal = sculptableMesh.WorldToLocalNormal(worldNormal);

            BeginDirtyVertices();
            // Order-symmetric near a mirror plane - see MirroredDabWalk.
            MirroredDabWalk dabs = BeginMirroredDabs(localPoint, brushRadius);
            while (NextMirroredDab(ref dabs, out Vector3 sign))
            {
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                applyBrushLocal(Vector3.Scale(localPoint, sign), mirroredNormal, positive, dt);
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
        private void ApplyInflateBrushLocal(Vector3 localPoint, Vector3 localNormal, bool positive, float dt)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            Vector3[] normals = sculptableMesh.Normals;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            // dt arrives as an argument - read once per frame by the input handler - rather than
            // from Time.deltaTime inside either path, so both paths AND the mirror loop above them
            // are pure functions of their arguments. That is what lets a test run them on identical
            // inputs (see SculptControllerJobParityTests and SymmetryDriftTests - Time.deltaTime is
            // 0 outside Play mode). Same value either way: it cannot change within a frame.
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
        private void ApplyFlattenBrushLocal(Vector3 localPoint, Vector3 localNormal, bool positive, float dt)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            Vector3[] normals = sculptableMesh.Normals;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            // dt: see ApplyInflateBrushLocal.
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
            var plane = new AreaPlaneSums(); // double precision - see AreaPlaneSums
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                // The mask-free weight, for the same reason Clay's reduction uses it - see
                // ClayWeightJob.Execute. Flatten is if anything the more sensitive of the two: it
                // projects the footprint ONTO this plane, so a plane tilted by a nearby mask does
                // not merely deposit unevenly, it shears the surface toward the wrong flat.
                float w = _nativeClayPlaneWeights[ci];
                plane.Add(_nativeStrokeStart[ci] * w, _nativeNormalsIn[ci] * w, w);
            }
            if (!plane.HasWeight) return;

            Vector3 planeNormal = plane.NormalOr(localNormal);
            Vector3 planeOrigin = plane.Origin + planeNormal * (brushRadius * flattenPlaneOffset);

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

            var plane = new AreaPlaneSums(); // double precision - see AreaPlaneSums
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
                plane.Add(mesh.StrokeStartPosition(i) * planeW, n * planeW, planeW);
            }

            if (!plane.HasWeight) return;

            Vector3 planeNormal = plane.NormalOr(localNormal);
            // The plane the footprint gets projected onto, slid along its own normal by the
            // Plane Offset control - see flattenPlaneOffset for what the two directions mean.
            Vector3 planeOrigin = plane.Origin + planeNormal * (brushRadius * flattenPlaneOffset);

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
                ApplySmoothBrush(hitPoint, Time.deltaTime); // dt read once - see ApplyInflateBrushLocal
        }

        private void ApplySmoothBrush(Vector3 worldPoint, float dt)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);

            BeginDirtyVertices();
            // Order-symmetric near a mirror plane - see MirroredDabWalk. Smooth also reads one ring
            // past its footprint, which MirrorInteractionMargin covers.
            MirroredDabWalk dabs = BeginMirroredDabs(localPoint, brushRadius);
            while (NextMirroredDab(ref dabs, out Vector3 sign))
                ApplySmoothBrushLocal(Vector3.Scale(localPoint, sign), dt);

            FlushDirtyVertices();
        }

        private void ApplySmoothBrushLocal(Vector3 localPoint, float dt)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            // dt: see ApplyInflateBrushLocal.
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

            NativeArray<int> adjStarts = sculptableMesh.AdjacencyStarts;
            NativeArray<int> adjCounts = sculptableMesh.AdjacencyCounts;
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
                chain = ScheduleSmoothRelaxJob(candidates.Count, readBuf, writeBuf, adjStarts, adjCounts, adjNeighbors, 1f, lerpFactorScale, chain);
                (readBuf, writeBuf) = (writeBuf, readBuf);
            }
            if (partialFactor > 0.001f)
            {
                chain = ScheduleSmoothRelaxJob(candidates.Count, readBuf, writeBuf, adjStarts, adjCounts, adjNeighbors, partialFactor, lerpFactorScale, chain);
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
            NativeArray<int> adjStarts, NativeArray<int> adjCounts, NativeArray<int> adjNeighbors, float passFactor, float lerpFactorScale, JobHandle dependency)
        {
            var job = new SmoothRelaxJob
            {
                Candidates = _nativeLaplacianCandidates,
                AdjacencyStarts = adjStarts,
                AdjacencyCounts = adjCounts,
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
            Vector3[] targets = LaplacianTargetScratch(candidates.Count);
            bool anyMoved = false;

            // Jacobi, matching SmoothRelaxJob: every target from this pass's starting positions,
            // then every write. The in-place Gauss-Seidel update this used to be swept the footprint
            // in candidate order, and the mirrored dab's candidates do not come back in mirrored
            // order - so the far half of every mirrored Smooth came out a little different from the
            // near half (SymmetryDriftTests). It also made small brushes, which take this path,
            // relax differently from large ones, which take the job.
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                float w = weights[ci];
                if (w <= 0f) continue;
                int i = candidates[ci];
                Vector3 p = verts[i];
                targets[ci] = p + (mesh.GetNeighborAverage(i) - p) * Mathf.Clamp01(w * lerpScale); // see Clamp01 note on Clay
            }

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                if (weights[ci] <= 0f) continue;
                int i = candidates[ci];
                mesh.RecordUndoBeforeIfNeeded(i);
                verts[i] = targets[ci];
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

            // BEFORE the selection is captured, not after: the selection records vertex indices and
            // weights, and a refine that ran afterwards would be adding vertices the drag has no
            // entry for. Refining here is what stops a Move on coarse geometry from dragging three
            // vertices out into a spike.
            RefineTopologyAtStrokeBounds(localHit, brushRadius);

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
