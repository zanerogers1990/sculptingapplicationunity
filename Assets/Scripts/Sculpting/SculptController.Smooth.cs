using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// The Smooth brush: Laplacian relaxation passes, Jacobi (job) and Gauss-Seidel (managed).
    public partial class SculptController
    {
        // Smooth has no "amount" concept beyond how far it eases toward the neighbor
        // average each frame, so it gets its own speed constant rather than reusing Clay's.
        private const float SmoothSpeed = 4f;

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

        private void HandleSmoothInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) { ResetDabStroke(); return; }

            Ray ray = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) { ResetDabStroke(); return; }

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;
            _previewPositive = true; // Smooth has no add/subtract direction - always neutral/green

            LogRayHit(mouse, ray, hitPoint, hitNormal);

            // Same Alt-reserved-for-orbit rule as Clay; either mouse button smooths since
            // there's no positive/negative to invert.
            if ((mouse.leftButton.isPressed && !altHeld) || mouse.rightButton.isPressed)
                ApplySmoothStroke(hitPoint, hitNormal, Time.deltaTime); // dt read once - see ApplyInflateBrushLocal
            else
                ResetDabStroke();
        }

        /// One frame of a distance-paced Smooth stroke - see StepDabStroke. Holding still keeps
        /// smoothing (DabHoldMode.AlwaysWorks).
        private void ApplySmoothStroke(Vector3 worldPoint, Vector3 worldNormal, float dt)
        {
            Transform t = sculptableMesh.transform;
            BeginDirtyVertices();
            StepDabStroke(t.InverseTransformPoint(worldPoint), sculptableMesh.WorldToLocalNormal(worldNormal), dt,
                DabHoldMode.AlwaysWorks, _placeSmoothDab ??= PlaceSmoothDab);
            FlushDirtyVertices();
        }

        private Action<Vector3, Vector3> _placeSmoothDab;

        private void PlaceSmoothDab(Vector3 localPoint, Vector3 localNormal) => ApplySmoothDabLocal(localPoint, DabTimeQuantum);

        /// One dab, applied once (the world-space entry point the tests drive directly).
        private void ApplySmoothBrush(Vector3 worldPoint, float dt)
        {
            Transform t = sculptableMesh.transform;
            BeginDirtyVertices();
            ApplySmoothDabLocal(t.InverseTransformPoint(worldPoint), dt);
            FlushDirtyVertices();
        }

        private void ApplySmoothDabLocal(Vector3 localPoint, float dt)
        {
            // Order-symmetric near a mirror plane - see MirroredDabWalk. Smooth also reads one ring
            // past its footprint, which MirrorInteractionMargin covers.
            MirroredDabWalk dabs = BeginMirroredDabs(localPoint, brushRadius);
            while (NextMirroredDab(ref dabs, out Vector3 sign))
                ApplySmoothBrushLocal(Vector3.Scale(localPoint, sign), dt);
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
                weights[ci] = BrushFalloff.Apply(t01, t01 * t01 * (3f - 2f * t01)) * (1f - mask[i]) // smoothstep, masked-out
                    * BrushMath.FrontFacingWeight(frontFacingOnly, normals[i], p, cameraLocalPos);
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
    }
}
