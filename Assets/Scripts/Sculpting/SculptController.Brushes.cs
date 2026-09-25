using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Plumbing every brush shares: the per-frame dirty set, the effective strength/pacing
    /// values, the standard-brush input and mirror dispatch, and the stroke-end commit. Each
    /// brush's own handler and Job/Managed apply paths live in its own partial.
    public partial class SculptController
    {

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
            _dirtyVertexScratch.Clear(sculptableMesh.VertexCount);
        }

        /// Pushes whatever the frame's brush application moved into the mesh, in one call. Skips
        /// entirely when nothing moved - a held-but-stationary stroke places no dabs at all, and
        /// an empty apply would still walk the sync/filter preamble for no result.
        private void FlushDirtyVertices()
        {
            if (_dirtyVertexScratch.Count == 0) return;
            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch.Items);
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

        // One brush diameter per second: the stroke speed the distance-paced brushes are
        // calibrated against (see DabTimeQuantum), and the rate Build Up on Hold feeds virtual
        // travel at. Relative to the brush rather than an absolute speed, because "how many brush
        // widths did the stroke cover" is what decides how worked-over a surface looks.
        private float StrokePacingReference => Mathf.Max(brushRadius * 2f, 0.001f);

        // Every brush should behave the same at any size, just scaled - ZBrush's brushes do, which
        // is what lets a sculptor block out with a huge brush and detail with a tiny one without
        // retuning strength. Three of ours didn't: Clay's per-dab approach to its plateau was
        // proportional to the radius (small brushes were weak), and Crease's dig rate and Inflate's
        // push were fixed world distances per dab/frame (small brushes dug and ballooned out of
        // all proportion - Inflate at a 0.05 radius could swell a 0.5 sphere to twice its size in
        // one stroke). Each now multiplies its world-distance rate by RadiusScale and uses a
        // size-independent fraction for anything that approaches a target. At this reference
        // radius every rate is exactly what it was, so existing slider settings keep their feel at
        // ordinary sizes.
        private const float StrengthReferenceRadius = 0.25f;
        private float RadiusScale => brushRadius / StrengthReferenceRadius;

        // With Build Up on Hold ON, a motionless cursor keeps building at this fraction of a
        // stroke moving at StrokePacingReference - the steppers feed it in as virtual travel (see
        // ApplyCarveStroke, StepDabStroke). With it OFF a stopped cursor places no dabs at all.
        private const float AccumulateSpeedFloor = 0.35f;

        // Every brush now places a fixed quantum per dab, and dabs by distance travelled (Clay:
        // ApplyClayStroke, Crease: ApplyCarveStroke, the rest: StepDabStroke), so the material laid
        // down per centimetre no longer depends on stroke speed or frame rate. The measured-speed
        // factor that used to approximate that for the frame-paced brushes is gone; what it was
        // compensating for no longer exists.

        /// The Accumulate-OFF (ease toward one dab's worth, then stop) strength for every
        /// distance-paced brush. Multiplied by accumulateStrength because that slider is the
        /// build-up strength for BOTH build-up modes - see its "Build-Up Strength" label.
        private float EffectiveDabStrength => EffectiveBrushStrength * accumulateStrength;

        /// The Accumulate-ON (keeps building for as long as the stroke travels) strength.
        private float EffectiveDabStrengthAccumulate => brushStrength * Mathf.Lerp(1f, CurrentPressure, AccumulatePressureInfluence) * accumulateStrength;

        /// Clay's accumulate strength - the same value, kept under its own name because Clay's
        /// ClayWeight/ClayDisplace plumbing predates the other brushes' move to dabs.
        private float EffectiveClayStrengthAccumulate => EffectiveDabStrengthAccumulate;

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
                // Once per stroke, over everything it touched: the relax shell's quiet moves are not
                // refreshed per frame (that would give back what the drift filter saves), so without
                // this the NEXT stroke could read normals and curvature left stale on one half of the
                // model and fresh on the other - see SculptableMesh.RefreshDroppedVertices.
                sculptableMesh.RefreshStrokeNormalsAndCurvature();
                sculptableMesh.EndStrokeUndo();
                _strokeEndFadeTimer = StrokeEndFadeDuration;
            }
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

        private void HandleStandardBrushInput(Mouse mouse, bool overUI, bool altHeld,
            Action<Vector3, Vector3, bool, float> applyBrushLocal, float dabDt, DabHoldMode hold, bool footprintNormal)
        {
            _isHovering = false;
            if (overUI) { ResetDabStroke(); return; }

            Ray ray = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) { ResetDabStroke(); return; }

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            bool rightHeld = mouse.rightButton.isPressed;
            bool invertHeld = rightHeld || CtrlHeld;
            _previewPositive = invertHeld ? !isPositive : isPositive;

            LogRayHit(mouse, ray, hitPoint, hitNormal);

            bool sculptingLeft = mouse.leftButton.isPressed && !altHeld;
            if (!sculptingLeft && !rightHeld) { ResetDabStroke(); return; }

            // dt is read once here and handed down - see ApplyInflateBrushLocal.
            ApplyStandardStroke(hitPoint, hitNormal, sculptingLeft ? (invertHeld ? !isPositive : isPositive) : !isPositive,
                Time.deltaTime, applyBrushLocal, dabDt, hold, footprintNormal);
        }

        /// One frame of a distance-paced Inflate/Flatten/Standard/Layer stroke: however many dabs
        /// this frame's travel calls for (see StepDabStroke), each mirrored, all pushed into the
        /// mesh at once. footprintNormal: push along the footprint's averaged normal instead of the
        /// hit triangle's - the brushes that move everything along ONE direction need it, because
        /// the facet normal jitters from triangle to triangle and would steer each dab differently.
        private void ApplyStandardStroke(Vector3 worldPoint, Vector3 worldNormal, bool positive, float dt,
            Action<Vector3, Vector3, bool, float> applyBrushLocal, float dabDt, DabHoldMode hold, bool footprintNormal)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            Vector3 localNormal = sculptableMesh.WorldToLocalNormal(worldNormal);
            if (footprintNormal) localNormal = AverageFootprintNormal(localPoint, localNormal);

            _standardDabApply = applyBrushLocal;
            _standardDabPositive = positive;
            _standardDabDt = dabDt;
            BeginDirtyVertices();
            StepDabStroke(localPoint, localNormal, dt, hold, _placeStandardDab ??= PlaceStandardDab);
            FlushDirtyVertices();
        }

        // The dab StepDabStroke is placing - fields rather than a closure so the per-frame path
        // allocates nothing.
        private Action<Vector3, Vector3, bool, float> _standardDabApply;
        private bool _standardDabPositive;
        private float _standardDabDt;
        private Action<Vector3, Vector3> _placeStandardDab;

        private void PlaceStandardDab(Vector3 localPoint, Vector3 localNormal) =>
            ApplyMirroredDabLocal(localPoint, localNormal, _standardDabPositive, _standardDabDt, _standardDabApply);

        /// One dab, applied once (the world-space entry point the tests drive directly).
        private void ApplyMirroredBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive, float dt,
            Action<Vector3, Vector3, bool, float> applyBrushLocal)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            // Not InverseTransformDirection: that is rotation-only and mis-tilts the normal
            // on a non-uniformly scaled object - see SculptableMesh.WorldToLocalNormal.
            Vector3 localNormal = sculptableMesh.WorldToLocalNormal(worldNormal);

            BeginDirtyVertices();
            ApplyMirroredDabLocal(localPoint, localNormal, positive, dt, applyBrushLocal);
            FlushDirtyVertices();
        }

        private void ApplyMirroredDabLocal(Vector3 localPoint, Vector3 localNormal, bool positive, float dt,
            Action<Vector3, Vector3, bool, float> applyBrushLocal)
        {
            // Order-symmetric near a mirror plane - see MirroredDabWalk.
            MirroredDabWalk dabs = BeginMirroredDabs(localPoint, brushRadius);
            while (NextMirroredDab(ref dabs, out Vector3 sign))
            {
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                applyBrushLocal(Vector3.Scale(localPoint, sign), mirroredNormal, positive, dt);
            }
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
            EndSnakeDrag();
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
