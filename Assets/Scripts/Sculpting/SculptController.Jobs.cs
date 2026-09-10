using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Sculpting
{
    /// Burst jobs for the brush hot paths, the float math they share with the managed paths,
    /// and the persistent native scratch they run on.
    public partial class SculptController
    {
        // Below this candidate count, Job scheduling's fixed per-call overhead costs more than
        // it saves - use the plain C# loop instead. Small brush radii commonly touch footprints
        // well under this.
        private const int MinJobVertexCount = 256;

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
    }
}
