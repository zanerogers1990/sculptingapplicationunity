using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// The Clay brush: stroke pacing, the frozen tip frame, and the Job/Managed apply paths.
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

        // Clay's own stroke-continuity memory, in mesh-local space - null between strokes
        // (mouse up / hover lost / brush switched), same lifecycle as _lastCarveStrokeLocal
        // above. Used by ApplyClayStroke to sub-divide a fast drag into multiple dabs instead
        // of one dab per rendered frame - see its remarks for why.
        private Vector3? _lastClayStrokeLocal;

        private Vector3? _lastClayStrokeNormalLocal;

        private void HandleClayInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) { _lastClayStrokeLocal = null; return; }

            Ray ray = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) { _lastClayStrokeLocal = null; return; }

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

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

        // The dt one Clay dab is worth - a calibration constant, not an elapsed time. It was the
        // dab SPACING (a fifth of the radius), which made the fraction of the way to the plateau
        // each dab covers proportional to the brush size: the same stroke settled fully at a big
        // radius and barely started at a small one. Fixed at what the spacing was at
        // StrengthReferenceRadius; the build rate, which IS a distance, gets RadiusScale instead.
        private const float ClayDabTimeQuantum = ClayDabSpacingFraction * StrengthReferenceRadius;

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
            float dabDt = ClayDabTimeQuantum;

            // One dirty set and one relax batch for the WHOLE frame, however many dabs it turns
            // out to place - see FlushClayFrame.
            BeginDirtyVertices();
            BeginRelaxBatch();
            BeginClayDabs();

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

            EndClayDabs(positive);
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

        private void ApplyClayBrushLocal(Vector3 localPoint, Vector3 localNormal, Vector3 tangent0, Vector3 bitangent0, bool positive, float dt)
        {
            if (_dabProgramRecording)
            {
                // Batched - see BeginClayDabs. The relax centre is recorded as it is below.
                RecordApplyDab(localPoint, localNormal, tangent0, bitangent0, dt);
                RecordRelaxCentre(localPoint);
                return;
            }

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
            RecordRelaxCentre(localPoint);
        }

        /// A frame's Clay dabs run as one batched Burst job when jobs are on (see
        /// SculptController.DabProgram): between these two calls ApplyClayBrushLocal records instead
        /// of applying. With jobs off every dab applies immediately, as it always has.
        private void BeginClayDabs()
        {
            if (useBurstJobs) BeginDabProgram(sculptableMesh.Vertices.Length);
        }

        private void EndClayDabs(bool positive)
        {
            if (_dabProgramRecording) RunClayProgram(positive);
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
                Rate = sign * clayHeightFactor * effectiveStrengthAccumulate * ClaySpeed * dt * RadiusScale,
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
                    * BrushMath.FrontFacingWeight(frontFacingOnly, n, p, cameraLocalPos);
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
                    Vector3 buildDelta = planeNormal * (sign * clayHeightFactor * effectiveStrengthAccumulate * ClaySpeed * dt * RadiusScale) * weight;

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
    }
}
