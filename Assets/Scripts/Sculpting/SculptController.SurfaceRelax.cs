using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Sculpting
{
    /// Clay's curvature-gated Surface Relax pass, batched across a frame's dabs.
    public partial class SculptController
    {
        // Where relax acts, and why only inside the brush ring.
        //
        // It used to reach 2.5 brush radii, relaxing a "shell" around the dab on the reasoning that
        // the seam with neighbouring geometry sits just outside the dab. That meant every Clay
        // stroke quietly moved surface well beyond the ring the user could see - which is what the
        // user asked to stop - and it was also more than half the cost of a Clay frame at 1.3M
        // triangles (BrushStrokeBenchmarks). Relax now stays inside the ring: RelaxInnerFloor in
        // the core, where the user is actively shaping (full-strength relax there fought the
        // brush - Clay's own buildup went net NEGATIVE on a held stroke), rising to full strength
        // across the outer RelaxEdgeBand of the radius - Clay's own falloff edge, where one dab's
        // plateau meets the surface it was laid on and where a stroke's side seams form - and back
        // to 0 on the ring itself. The curvature gate below still concentrates it on genuine
        // pinches.
        private const float RelaxRadiusFactor = 1f;

        private const float RelaxEdgeBand = 0.35f;

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
        /// exists to make fast. Measurement said otherwise, and by a wide margin: when relax reached
        /// 2.5x the brush radius, about six times the dab's own surface area, on a 270k-triangle
        /// sphere at a 0.25 brush radius this pass alone accounted for 52.8ms of a 61.1ms Clay dab
        /// - 86% of it. It now takes the same Burst path Smooth does (and stays inside the ring -
        /// see RelaxRadiusFactor).
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

            // One query per viewpoint - i.e. per mirror side, since each side's dabs carry their own
            // reflected camera - covering a sphere around all of that side's centres, instead of one
            // query per centre. A frame's centres sit a fifth of a radius apart and their footprints
            // overlap by ~90%, so a query per centre re-walked the same cells a dozen times; at 1.3M
            // triangles that was the bulk of what the relax pass cost. The sphere is a superset of the
            // per-centre union, and a superset changes nothing: RelaxWeightJob gives every candidate
            // beyond relax reach of its nearest centre weight 0, SurfaceRelaxJob leaves weight-0
            // slots exactly where they are, and a neighbour read from an unmoved slot is the same
            // value the full-mesh mirror would have supplied.
            _relaxGroupDone.Clear();
            for (int c = 0; c < _relaxCentres.Count; c++)
            {
                Vector3 viewpoint = _relaxCentreCameras[c];
                if (_relaxGroupDone.Contains(viewpoint)) continue;
                _relaxGroupDone.Add(viewpoint);

                Vector3 min = _relaxCentres[c], max = min;
                for (int k = c + 1; k < _relaxCentres.Count; k++)
                {
                    if (_relaxCentreCameras[k] != viewpoint) continue;
                    min = Vector3.Min(min, _relaxCentres[k]);
                    max = Vector3.Max(max, _relaxCentres[k]);
                }
                Vector3 centre = (min + max) * 0.5f;
                float extent = 0f;
                for (int k = c; k < _relaxCentres.Count; k++)
                    if (_relaxCentreCameras[k] == viewpoint)
                        extent = Mathf.Max(extent, Vector3.Distance(_relaxCentres[k], centre));

                // QueryNear hands back the spatial grid's own reused buffer, so this has to consume
                // it fully before the next query overwrites it.
                List<int> near = sculptableMesh.QueryNear(centre, extent + queryRadius);
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

        // Viewpoints GatherRelaxCandidates has already queried this frame - at most one per mirror
        // sign, so a list scan beats hashing.
        private readonly List<Vector3> _relaxGroupDone = new List<Vector3>(8);

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
                EdgeSoftness = RelaxEdgeBand,
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
                // Edge-band profile, not a falloff from the dab centre - see RelaxRadiusFactor.
                float spatialWeight = RelaxSpatialWeight(Mathf.Sqrt(sqrDist), relaxRadius,
                    RelaxEdgeBand, RelaxInnerFloor);

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

        private void RecordRelaxCentre(Vector3 localPoint)
        {
            // Once per sign, not once per ordering - a repeat ordering re-applies the same dab.
            if (surfaceRelax > 0f && !_mirrorRepeatOrdering)
            {
                _relaxCentres.Add(localPoint);
                _relaxCentreCameras.Add(_dabCameraLocal); // lockstep - see _relaxCentreCameras
            }
        }
    }
}
