using System;
using System.Collections.Generic;
using Unity.Jobs;
using UnityEngine;

namespace Sculpting
{
    /// The Inflate and Flatten brushes' apply paths. Their input handling is the shared
    /// HandleStandardBrushInput in SculptController.Brushes.cs.
    public partial class SculptController
    {
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

        // The Accumulate-ON push rate, per dab-second. Much lower than InflateSpeed: that path has
        // no target to converge on, and at InflateSpeed one ordinary pass at the default strength
        // swelled the surface by about three brush RADII - the "balloons the sphere" behaviour.
        // At this rate the same pass raises the centre of the stroke by roughly a quarter of the
        // radius, in line with what one default-strength Clay pass builds.
        private const float InflateAccumulateSpeed = 1.25f;

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

        // Inflate's and Flatten's input handler and world-space wrapper were character-for-character
        // copies of each other, differing only in which Apply*BrushLocal they ended in, so both
        // brushes now route through these two methods. Clay, Crease and Smooth keep their own
        // handlers because they genuinely differ from this one: Clay and Crease carry extra
        // per-stroke state (a frozen tip frame, a travel direction) through their own dab
        // steppers, while Smooth has no polarity and a neutral preview. Move, Pose and mask
        // painting are different gestures altogether.
        //
        // The delegates are cached rather than passed as method groups: converting a method group
        // allocates a new delegate on every call, and the handler runs every frame the brush hovers.
        private Action<Vector3, Vector3, bool, float> _applyInflateBrushLocal;

        private Action<Vector3, Vector3, bool, float> _applyFlattenBrushLocal;

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
            float effectiveStrength = EffectiveDabStrength;
            float amount = sign * EffectiveDabStrengthAccumulate * InflateAccumulateSpeed * dt * RadiusScale;

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
            float effectiveStrength = EffectiveDabStrength;
            float effectiveStrengthAccumulate = EffectiveDabStrengthAccumulate;
            Vector3 target = localPoint + localNormal * (brushRadius * InflateOffCapFactor * sign);
            Vector3 cameraLocalPos = _dabCameraLocal; // this dab's viewpoint - see _dabCameraLocal
            SculptableMesh mesh = sculptableMesh;
            float[] mask = mesh.Mask;
            float radiusSqr = brushRadius * brushRadius;
            float invRadius = 1f / brushRadius;
            float accumulateRate = sign * effectiveStrengthAccumulate * InflateAccumulateSpeed * dt * RadiusScale;
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
                float weight = BrushFalloff.Apply(t01, t01 * t01 * (3f - 2f * t01)) * (1f - mask[i]) // smoothstep, masked-out
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
    }
}
