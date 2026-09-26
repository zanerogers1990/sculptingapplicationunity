using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

namespace Sculpting
{
    /// A whole frame of dabs run as ONE Burst job instead of one or two jobs per dab.
    ///
    /// Clay and Crease place a dab every fifth / tenth of a brush radius of travel - up to 24 / 48 a
    /// frame, times up to four mirror orderings near a symmetry plane - and every one of them used to
    /// pay its own spatial query, main-thread gather and scatter, and job sync points. That per-dab
    /// overhead, not the brush math, was the cost of a brisk stroke: at 1.3M triangles a large
    /// Crease stroke with X symmetry averaged 134ms a frame and Clay 117ms (BrushStrokeBenchmarks).
    ///
    /// The program is recorded by running the ordinary per-dab code with _dabProgramRecording set:
    /// the brushes' dab methods and the four mirror-group steps (see MirroredDabWalk) append an op
    /// instead of touching the mesh, so the order of operations comes from the very code it
    /// replaces. The frame then gathers every vertex the program can reach once, runs it in Burst,
    /// and writes the results back through the same ScatterJobResults the per-dab jobs use.
    ///
    /// - Crease (CarveProgramJob) reads and writes nothing but the vertex it is moving, so the
    ///   program runs vertex-parallel: each vertex replays the whole frame on its own, which gives
    ///   it exactly the operations it got dab by dab.
    /// - Clay (ClayProgramJob) fits each dab's plane to the whole footprint as the previous dabs
    ///   left it, so its dabs must run in order. It runs single-threaded in Burst, each dab finding
    ///   its footprint by bucket lookup along the stroke rather than a spatial query.
    public partial class SculptController
    {
        private enum DabOpKind : byte { Apply, GroupBegin, Accumulate, Restore, Commit }

        private struct DabOp
        {
            public DabOpKind Kind;
            public Vector3 Point;
            public Vector3 Normal;
            public Vector3 Dir;      // Crease: stroke direction. Clay: the frozen tip tangent.
            public Vector3 Aux;      // Clay: the frozen tip bitangent.
            public Vector3 Camera;   // the dab's viewpoint - see _dabCameraLocal
            public float Scale;      // Commit: the scale on the summed deltas (see CommitMirrorGroup). Clay Apply: the dab's dt.
            public int Side;         // Apply: which symmetric copy the dab is (its group index) - see _dabOpIndex
            public SymmetryOp Op;    // Apply: that copy's op - see _dabOp
            // Clay Apply: everything that depends on the dab's own pen pressure (see RecordClayDab) -
            // the dabs of one frame no longer share one value.
            public float Radius, Softness, LerpScale, Rate;
        }

        /// One symmetric copy's dabs (a "side" - one element of the symmetry group), for
        /// ClayProgramJob's lookup. A side's dabs follow the stroke's curve (see StrokePath), mapped
        /// through the side's op; First..Last is the chord from its first dab to its last, and Bulge
        /// the furthest any of its dabs lies from that chord - so a capsule of radius reach + Bulge
        /// around the chord holds every dab's footprint.
        private struct DabSide
        {
            public bool Active;
            public Vector3 Centre, Axis;
            public Vector3 First, Last;
            public float Bulge;
            // Sides whose dabs could move a vertex into each other's reach share a component - see
            // ClayProgramJob.
            public int Component;
        }

        private bool _dabProgramRecording;
        private readonly List<DabOp> _dabProgram = new List<DabOp>();
        private NativeArray<DabOp> _nativeDabProgram;

        // Every vertex any recorded dab can reach, deduped by generation stamp (same scheme as
        // DirtyVertexSet), in "slot" order - the index every native array of the program uses.
        // Gathered with ONE query per symmetric side, a sphere around all of that side's dab centres,
        // rather than one per dab: a frame's dabs lie along one short stretch of the stroke
        // and overlap each other by ~90%, so twenty footprint queries cost far more than the
        // job they feed. The sphere is a superset, and the jobs' own radius tests do the exact
        // selection. It is also complete: a vertex inside dab k's reach when dab k runs either had
        // not moved yet this frame - so it sits within that reach of dab k's centre now, inside the
        // sphere - or an earlier dab moved it, and it was inside that dab's reach before it moved.
        private readonly List<int> _dabUnion = new List<int>();
        private int[] _dabUnionStamp;
        private int _dabUnionGeneration;
        // One per element of the current symmetry group - see EnsureDabSides.
        private DabSide[] _dabSides = new DabSide[8];
        private float[] _dabSideExtents = new float[8];
        private int _dabSideCount;

        private NativeArray<DabSide> _nativeDabSides;
        private NativeArray<float> _nativeDabWeights;
        private NativeArray<int> _nativeDabGroupStamp;
        private NativeArray<int> _nativeDabGroupList;
        private NativeArray<Vector3> _nativeDabGroupBefore;
        private NativeArray<Vector3> _nativeDabGroupDelta;
        private NativeArray<float> _nativeDabBucketKeys;
        private NativeArray<int> _nativeDabBucketSlots;
        private NativeArray<int> _nativeDabBucketStarts;
        private NativeArray<int> _nativeDabBucketCursor;
        private NativeArray<float> _nativeDabBucketMin;
        private NativeArray<float> _nativeDabBucketWidth;
        private NativeArray<int> _nativeDabBucketCount;
        private NativeArray<float> _nativeDabStats;
        private int _nativeDabWorkCapacity, _nativeDabSideCapacity;

        // ClayProgramJob's bucket table: at most this many buckets per side. The width is a quarter
        // of the dab's reach, so this covers a side ~500 reaches long before buckets widen.
        private const int MaxDabBuckets = 2048;

        private void BeginDabProgram(int vertexCount)
        {
            _dabProgram.Clear();
            _dabUnion.Clear();
            if (_dabUnionStamp == null || _dabUnionStamp.Length != vertexCount)
            {
                _dabUnionStamp = new int[vertexCount];
                _dabUnionGeneration = 0;
            }
            _dabUnionGeneration++;
            _dabProgramRecording = true;
        }

        private void RecordDabOp(DabOpKind kind, float scale = 0f) =>
            _dabProgram.Add(new DabOp { Kind = kind, Scale = scale });

        private void RecordApplyDab(Vector3 point, Vector3 normal, Vector3 dir, Vector3 aux = default, float scale = 0f)
        {
            _dabProgram.Add(new DabOp
            {
                Kind = DabOpKind.Apply, Point = point, Normal = normal, Dir = dir, Aux = aux,
                Camera = _dabCameraLocal, Scale = scale, Side = _dabOpIndex, Op = _dabOp,
            });
        }

        /// Records one Clay dab, with everything its pressure decides worked out now - the same
        /// expressions ApplyClayBrushLocal's job and managed paths evaluate for a dab at that
        /// pressure, so the batched program cannot drift from them.
        private void RecordClayDab(Vector3 point, Vector3 normal, Vector3 tangent0, Vector3 bitangent0, bool positive,
            float dt, float pressure)
        {
            float sign = positive ? 1f : -1f;
            _dabProgram.Add(new DabOp
            {
                Kind = DabOpKind.Apply, Point = point, Normal = normal, Dir = tangent0, Aux = bitangent0,
                Camera = _dabCameraLocal, Scale = dt, Side = _dabOpIndex, Op = _dabOp,
                Radius = EffectiveClayRadiusAt(pressure),
                Softness = EffectiveClayEdgeSoftnessAt(pressure),
                LerpScale = EffectiveBrushStrengthAt(pressure) * ClaySpeed * dt,
                Rate = sign * clayHeightFactor * EffectiveClayStrengthAccumulateAt(pressure) * ClaySpeed * dt * RadiusScale,
            });
        }

        /// Fills _dabUnion - see its remarks - and _dabSides. One sphere per symmetric side, never
        /// one around everything: mirrored and radial copies sit on different sides of the model,
        /// and a sphere around them all would take in the whole thing between them.
        private void GatherDabUnion(float reach)
        {
            int sideCount = 1;
            for (int k = 0; k < _dabProgram.Count; k++)
                if (_dabProgram[k].Kind == DabOpKind.Apply) sideCount = Mathf.Max(sideCount, _dabProgram[k].Side + 1);
            EnsureDabSides(sideCount);

            int generation = _dabUnionGeneration;
            int[] stamp = _dabUnionStamp;
            float[] extents = _dabSideExtents;
            for (int flip = 0; flip < sideCount; flip++)
            {
                _dabSides[flip] = default;
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                Vector3 first = default, last = default;
                bool any = false;
                for (int k = 0; k < _dabProgram.Count; k++)
                {
                    DabOp op = _dabProgram[k];
                    if (op.Kind != DabOpKind.Apply || op.Side != flip) continue;
                    min = Vector3.Min(min, op.Point);
                    max = Vector3.Max(max, op.Point);
                    if (!any) first = op.Point;
                    last = op.Point;
                    any = true;
                }
                if (!any) continue;

                Vector3 centre = (min + max) * 0.5f;
                float extent = 0f;
                for (int k = 0; k < _dabProgram.Count; k++)
                {
                    DabOp op = _dabProgram[k];
                    if (op.Kind != DabOpKind.Apply || op.Side != flip) continue;
                    extent = Mathf.Max(extent, Vector3.Distance(op.Point, centre));
                }
                extents[flip] = extent;

                // The spatial grid's shared buffer - consumed fully before the next query.
                List<int> found = sculptableMesh.QueryNear(centre, extent + reach);
                for (int k = 0; k < found.Count; k++)
                {
                    int vi = found[k];
                    if ((uint)vi >= (uint)stamp.Length || stamp[vi] == generation) continue;
                    stamp[vi] = generation;
                    _dabUnion.Add(vi);
                }

                // How far the side's curve strays from its chord - see DabSide.
                float bulgeSqr = 0f;
                for (int k = 0; k < _dabProgram.Count; k++)
                {
                    DabOp op = _dabProgram[k];
                    if (op.Kind != DabOpKind.Apply || op.Side != flip) continue;
                    bulgeSqr = Mathf.Max(bulgeSqr, SqrDistanceToSegment(op.Point, first, last));
                }

                Vector3 axis = last - first;
                axis = axis.sqrMagnitude > 1e-12f ? axis.normalized : Vector3.right;
                _dabSides[flip] = new DabSide
                {
                    Active = true, Centre = centre, Axis = axis, First = first, Last = last,
                    Bulge = Mathf.Sqrt(bulgeSqr), Component = flip,
                };
            }

            // Sides close enough that one's dabs could carry a vertex into the other's reach within a
            // frame share a component (see ClayProgramJob). Judged generously - bounding spheres
            // within four reaches - since a false "close" costs a little work and a false "far"
            // would lose vertices. Merged to a fixed point, so closeness is transitive.
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int a = 0; a < sideCount; a++)
                for (int b = a + 1; b < sideCount; b++)
                {
                    if (!_dabSides[a].Active || !_dabSides[b].Active) continue;
                    if (_dabSides[a].Component == _dabSides[b].Component) continue;
                    float gap = Vector3.Distance(_dabSides[a].Centre, _dabSides[b].Centre) - extents[a] - extents[b];
                    if (gap > 4f * reach) continue;
                    int from = _dabSides[b].Component, to = _dabSides[a].Component;
                    for (int f = 0; f < sideCount; f++)
                        if (_dabSides[f].Component == from) _dabSides[f].Component = to;
                    merged = true;
                }
            }
        }

        private static float SqrDistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float lengthSqr = ab.sqrMagnitude;
            float u = lengthSqr > 1e-12f ? Mathf.Clamp01(Vector3.Dot(p - a, ab) / lengthSqr) : 0f;
            return (p - (a + ab * u)).sqrMagnitude;
        }

        /// Sizes the side tables for a symmetry group of `count` elements (8 covers every mirror
        /// combination; radial symmetry can need more).
        private void EnsureDabSides(int count)
        {
            _dabSideCount = count;
            if (_dabSides.Length >= count) return;
            _dabSides = new DabSide[count];
            _dabSideExtents = new float[count];
        }

        private void UploadDabProgram()
        {
            if (!_nativeDabProgram.IsCreated || _nativeDabProgram.Length < _dabProgram.Count)
            {
                if (_nativeDabProgram.IsCreated) _nativeDabProgram.Dispose();
                _nativeDabProgram = new NativeArray<DabOp>(Mathf.Max(Mathf.NextPowerOfTwo(_dabProgram.Count), 64), Allocator.Persistent);
            }
            NativeArray<DabOp> program = _nativeDabProgram;
            for (int k = 0; k < _dabProgram.Count; k++) program[k] = _dabProgram[k];
        }

        // ------------------------------------------------------------------------------ Crease

        /// Stops recording and runs the recorded carve dabs over their candidate union, then writes
        /// the results back exactly as the per-dab path's ScatterJobResults does.
        private void RunCarveProgram(bool positive)
        {
            _dabProgramRecording = false;
            if (_dabProgram.Count == 0) return;
            GatherDabUnion(brushRadius);
            int count = _dabUnion.Count;
            if (count == 0) return;

            Vector3[] verts = sculptableMesh.Vertices;
            GatherCandidatesNative(_dabUnion, verts, sculptableMesh.Normals, sculptableMesh.Mask);
            sculptableMesh.CopyStrokeStartPositions(_dabUnion, _nativeStrokeStart);
            UploadDabProgram();

            // Same values ApplyCarveDabLocalJob computes per dab; none of them change within a frame.
            float sign = positive ? 1f : -1f;
            float effectiveStrength = EffectiveDabStrength;
            float effectiveStrengthAccumulate = EffectiveDabStrengthAccumulate;
            var job = new CarveProgramJob
            {
                PositionsIn = _nativePositionsIn,
                NormalsIn = _nativeNormalsIn,
                MaskIn = _nativeMaskIn,
                StrokeStartIn = _nativeStrokeStart,
                PositionsOut = _nativePositionsOut,
                AppliedOut = _nativeAppliedOut,
                Program = _nativeDabProgram,
                ProgramLength = _dabProgram.Count,
                Settings = new CarveSettings
                {
                    BrushRadius = brushRadius,
                    Depth = brushRadius * creaseDepthFactor * sign,
                    Pinch = creasePinch,
                    Sign = sign,
                    LerpFactorScale = effectiveStrength * CreaseSpeed * CreaseDabTimeQuantum,
                    Accumulate = accumulate,
                    DepthRate = sign * creaseDepthFactor * effectiveStrengthAccumulate * CreaseSpeed * CreaseDabTimeQuantum * RadiusScale,
                    PinchRateScale = creasePinch * effectiveStrengthAccumulate * CreaseSpeed * CreaseDabTimeQuantum,
                    FrontFacingOnly = frontFacingOnly,
                },
            };
            job.Schedule(count, 64).Complete();

            ScatterJobResults(_dabUnion, verts);
        }

        /// Runs a frame's recorded carve program for one vertex per index. The mirror-group ops
        /// reproduce BeginMirrorGroup / AccumulateMirrorOrdering / RestoreMirrorGroup /
        /// CommitMirrorGroup for this vertex alone, with the same float expressions. Applying them
        /// to a vertex outside the group is exact, not approximate: nothing moved it, so its delta
        /// is zero and the commit hands back its own position bit for bit.
        [BurstCompile(CompileSynchronously = true)]
        private struct CarveProgramJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> PositionsIn;
            [ReadOnly] public NativeArray<Vector3> NormalsIn;
            [ReadOnly] public NativeArray<float> MaskIn;
            [ReadOnly] public NativeArray<Vector3> StrokeStartIn;
            [ReadOnly] public NativeArray<DabOp> Program;
            public NativeArray<Vector3> PositionsOut;
            public NativeArray<byte> AppliedOut;
            public int ProgramLength;
            public CarveSettings Settings;

            public void Execute(int index)
            {
                Vector3 pos = PositionsIn[index];
                Vector3 normal = NormalsIn[index];
                float mask = MaskIn[index];
                Vector3 start = StrokeStartIn[index];
                Vector3 before = pos;
                Vector3 deltaSum = Vector3.zero;
                bool applied = false;

                for (int k = 0; k < ProgramLength; k++)
                {
                    DabOp op = Program[k];
                    switch (op.Kind)
                    {
                        case DabOpKind.Apply:
                            if (CarveStep(ref pos, normal, mask, start, op.Point, op.Normal, op.Dir, op.Camera, Settings))
                                applied = true;
                            break;
                        case DabOpKind.GroupBegin:
                            before = pos;
                            deltaSum = Vector3.zero;
                            break;
                        case DabOpKind.Accumulate:
                            deltaSum += pos - before;
                            break;
                        case DabOpKind.Restore:
                            pos = before;
                            break;
                        case DabOpKind.Commit:
                            pos = before + deltaSum * op.Scale;
                            break;
                    }
                }

                PositionsOut[index] = pos;
                AppliedOut[index] = applied ? (byte)1 : (byte)0;
            }
        }

        // -------------------------------------------------------------------------------- Clay

        private void EnsureDabWorkCapacity(int slots, int activeSides)
        {
            if (_nativeDabWorkCapacity < slots)
            {
                DisposeDabWork();
                _nativeDabWorkCapacity = Mathf.Max(Mathf.NextPowerOfTwo(slots), 1024);
                _nativeDabWeights = new NativeArray<float>(_nativeDabWorkCapacity, Allocator.Persistent);
                _nativeDabGroupStamp = new NativeArray<int>(_nativeDabWorkCapacity, Allocator.Persistent);
                _nativeDabGroupList = new NativeArray<int>(_nativeDabWorkCapacity, Allocator.Persistent);
                _nativeDabGroupBefore = new NativeArray<Vector3>(_nativeDabWorkCapacity, Allocator.Persistent);
                _nativeDabGroupDelta = new NativeArray<Vector3>(_nativeDabWorkCapacity, Allocator.Persistent);
            }
            // One slot list per active side, each up to every slot long.
            int sideSlots = _nativeDabWorkCapacity * Mathf.Max(activeSides, 1);
            if (_nativeDabSideCapacity < sideSlots)
            {
                DisposeDabSideLists();
                _nativeDabSideCapacity = sideSlots;
                _nativeDabBucketKeys = new NativeArray<float>(sideSlots, Allocator.Persistent);
                _nativeDabBucketSlots = new NativeArray<int>(sideSlots, Allocator.Persistent);
            }
            if (!_nativeDabSides.IsCreated || _nativeDabSides.Length < _dabSideCount)
            {
                DisposeDabSideTables();
                int sides = Mathf.Max(8, _dabSideCount);
                _nativeDabSides = new NativeArray<DabSide>(sides, Allocator.Persistent);
                _nativeDabBucketStarts = new NativeArray<int>(sides * (MaxDabBuckets + 1), Allocator.Persistent);
                _nativeDabBucketCursor = new NativeArray<int>(MaxDabBuckets, Allocator.Persistent);
                _nativeDabBucketMin = new NativeArray<float>(sides, Allocator.Persistent);
                _nativeDabBucketWidth = new NativeArray<float>(sides, Allocator.Persistent);
                _nativeDabBucketCount = new NativeArray<int>(sides * 2, Allocator.Persistent); // count, list base
                _nativeDabStats = new NativeArray<float>(2, Allocator.Persistent);
            }
        }

        /// Stops recording and runs the recorded Clay dabs, in order, over everything they can
        /// reach; writes back through ScatterJobResults like the per-dab job path.
        private void RunClayProgram(bool positive)
        {
            _dabProgramRecording = false;
            if (_dabProgram.Count == 0) return;
            // The same reach ApplyClayBrushLocal queries with - a square tip's corners stick out.
            float reach = clayTipRoundness < 1f ? brushRadius * Sqrt2 : brushRadius;
            GatherDabUnion(reach);
            int count = _dabUnion.Count;
            if (count == 0) return;

            Vector3[] verts = sculptableMesh.Vertices;
            GatherCandidatesNative(_dabUnion, verts, sculptableMesh.Normals, sculptableMesh.Mask);
            sculptableMesh.CopyStrokeStartPositions(_dabUnion, _nativeStrokeStart);
            UploadDabProgram();
            int activeSides = 0;
            for (int f = 0; f < _dabSideCount; f++) if (_dabSides[f].Active) activeSides++;
            EnsureDabWorkCapacity(count, activeSides);
            NativeArray<DabSide> sides = _nativeDabSides;
            for (int f = 0; f < _dabSideCount; f++) sides[f] = _dabSides[f];
            EnsureAlphaNative(); // read only with useAlpha, but the job needs a valid array either way

            // Same values ApplyClayBrushLocalJob computes per dab that do not change within a frame.
            // Whatever depends on a dab's pressure (radius, softness, strength) travels in its op -
            // see RecordClayDab.
            float sign = positive ? 1f : -1f;
            float height = brushRadius * clayHeightFactor * sign;
            float rot = alphaRotation * Mathf.Deg2Rad;

            var job = new ClayProgramJob
            {
                PositionsStart = _nativePositionsIn,
                NormalsIn = _nativeNormalsIn,
                MaskIn = _nativeMaskIn,
                StrokeStartIn = _nativeStrokeStart,
                AlphaSamples = _nativeAlphaSamples,
                Program = _nativeDabProgram,
                ProgramLength = _dabProgram.Count,
                Sides = _nativeDabSides,
                SideCount = _dabSideCount,
                Positions = _nativePositionsOut,
                AppliedOut = _nativeAppliedOut,
                Weights = _nativeDabWeights,
                GroupStamp = _nativeDabGroupStamp,
                GroupList = _nativeDabGroupList,
                GroupBefore = _nativeDabGroupBefore,
                GroupDelta = _nativeDabGroupDelta,
                BucketKeys = _nativeDabBucketKeys,
                BucketSlots = _nativeDabBucketSlots,
                BucketStarts = _nativeDabBucketStarts,
                BucketCursor = _nativeDabBucketCursor,
                BucketMin = _nativeDabBucketMin,
                BucketWidth = _nativeDabBucketWidth,
                BucketInfo = _nativeDabBucketCount,
                Stats = _nativeDabStats,
                SlotCount = count,
                Reach = reach,
                AlphaScale = alphaScale,
                TipRoundness = clayTipRoundness,
                FrontFacingOnly = frontFacingOnly,
                Displace = new ClayDisplaceSettings
                {
                    Height = height,
                    UseAlpha = useAlpha,
                    InvertAlpha = invertAlpha,
                    CosR = Mathf.Cos(rot),
                    SinR = Mathf.Sin(rot),
                    AlphaSize = useAlpha ? _nativeAlphaSize : 0,
                    Accumulate = accumulate,
                    MaxAlong = height * (accumulate ? ClayStrokeDepthLimitAccumulate : ClayStrokeDepthLimit),
                    SoftBand = Mathf.Abs(height) * StrokeDepthSoftBand,
                },
            };
            job.Schedule().Complete();

            ScatterJobResults(_dabUnion, verts);
        }

        /// Runs a frame's recorded Clay program, one dab after another, exactly as the per-dab path
        /// would: pass 1 weighs the dab's footprint and fits its area plane (double-precision sums -
        /// see AreaPlaneSums), pass 2 displaces toward it, both through the same ClayWeight /
        /// ClayDisplace the per-dab jobs use.
        ///
        /// Each dab finds its footprint through a bucket table instead of a spatial query. Every
        /// active side gets a list of the gathered slots that lie within reach of a segment - its
        /// own, or that of any side in the same component - by frame-start position, counting-sorted
        /// along its own stroke axis. A dab scans the buckets within reach of its centre along that
        /// axis, plus the largest distance any vertex has moved so far this frame, and its own
        /// distance test does the exact selection.
        ///
        /// Why that is complete: a vertex can only be inside a dab if it either has not moved this
        /// frame - then at frame start it is within reach of that dab, so within reach + Bulge of
        /// its side's segment - or some earlier dab moved it, and before its first move it was
        /// within reach of THAT dab. Either way it sits within the capsule of a side it could have
        /// travelled between, which is what a component is. The axis window, which is centred on
        /// the dab itself rather than the segment, then covers it because it has moved at most
        /// maxMoved. Components are judged generously on the main thread (see GatherDabUnion); if
        /// anything ever moved further than they allow for, the dab falls back to testing every
        /// gathered slot, which is exact, and says so in Stats[0].
        ///
        /// The mirror-group ops snapshot lazily: a slot is saved the first time a dab inside the
        /// group writes it, which is the value it had when the group began (nothing else moves it in
        /// between). Restore / accumulate / commit then walk only those slots. A slot no dab wrote
        /// would have come back from the commit bit for bit, so leaving it out changes nothing.
        [BurstCompile(CompileSynchronously = true)]
        private struct ClayProgramJob : IJob
        {
            [ReadOnly] public NativeArray<Vector3> PositionsStart;
            [ReadOnly] public NativeArray<Vector3> NormalsIn;
            [ReadOnly] public NativeArray<float> MaskIn;
            [ReadOnly] public NativeArray<Vector3> StrokeStartIn;
            [ReadOnly] public NativeArray<float> AlphaSamples;
            [ReadOnly] public NativeArray<DabOp> Program;
            [ReadOnly] public NativeArray<DabSide> Sides;
            public NativeArray<Vector3> Positions;
            public NativeArray<byte> AppliedOut;
            public NativeArray<float> Weights;
            public NativeArray<int> GroupStamp;
            public NativeArray<int> GroupList;
            public NativeArray<Vector3> GroupBefore;
            public NativeArray<Vector3> GroupDelta;
            public NativeArray<float> BucketKeys;
            public NativeArray<int> BucketSlots;
            public NativeArray<int> BucketStarts;
            public NativeArray<int> BucketCursor;
            public NativeArray<float> BucketMin;
            public NativeArray<float> BucketWidth;
            public NativeArray<int> BucketInfo; // [f] bucket count, [SideCount + f] base of side f's slot list
            public NativeArray<float> Stats;    // [0] fallback dabs, [1] largest move this frame
            public int ProgramLength;
            public int SideCount;
            public int SlotCount;
            public float Reach;
            public float AlphaScale;
            public float TipRoundness;
            public bool FrontFacingOnly;
            // The frame-wide part; each dab fills in its pressure-dependent fields from its op.
            public ClayDisplaceSettings Displace;

            // How far a vertex could travel between sides and still count as the same component -
            // mirrors the 4 x reach GatherDabUnion merges components with, less the 2 x reach the
            // two capsules already account for.
            private float MaxTravel => 2f * Reach;

            public void Execute()
            {
                for (int s = 0; s < SlotCount; s++)
                {
                    Positions[s] = PositionsStart[s];
                    AppliedOut[s] = 0;
                    GroupStamp[s] = 0;
                }
                // Component by component: which slots lie within reach of the component's segments is
                // worked out once (into Weights, free until the first dab) and shared by each of its
                // sides, rather than re-tested per side against every side of the component - which
                // was quadratic in the side count, and radial symmetry near its axis puts every side
                // in one component.
                int listBase = 0;
                for (int f = 0; f < SideCount; f++)
                {
                    BucketInfo[f] = 0;
                    BucketInfo[SideCount + f] = 0;
                }
                for (int c = 0; c < SideCount; c++)
                {
                    if (!MarkComponent(c)) continue;
                    for (int f = 0; f < SideCount; f++)
                    {
                        if (!Sides[f].Active || Sides[f].Component != c) continue;
                        BucketInfo[SideCount + f] = listBase;
                        BuildBuckets(f, listBase);
                        listBase += SlotCount;
                    }
                }
                Stats[0] = 0f;

                float maxMoved = 0f;
                int groupGeneration = 0, groupCount = 0;
                bool inGroup = false;

                for (int k = 0; k < ProgramLength; k++)
                {
                    DabOp op = Program[k];
                    switch (op.Kind)
                    {
                        case DabOpKind.Apply:
                            ApplyDab(op, ref maxMoved, inGroup, groupGeneration, ref groupCount);
                            break;
                        case DabOpKind.GroupBegin:
                            groupGeneration++;
                            groupCount = 0;
                            inGroup = true;
                            break;
                        case DabOpKind.Accumulate:
                            for (int g = 0; g < groupCount; g++)
                            {
                                int s = GroupList[g];
                                GroupDelta[s] += Positions[s] - GroupBefore[s];
                            }
                            break;
                        case DabOpKind.Restore:
                            for (int g = 0; g < groupCount; g++)
                            {
                                int s = GroupList[g];
                                Positions[s] = GroupBefore[s];
                            }
                            break;
                        case DabOpKind.Commit:
                            for (int g = 0; g < groupCount; g++)
                            {
                                int s = GroupList[g];
                                Positions[s] = GroupBefore[s] + GroupDelta[s] * op.Scale;
                            }
                            inGroup = false;
                            break;
                    }
                }
                Stats[1] = maxMoved;
            }

            /// Flags in Weights (1 / 0) every slot within reach of a segment of any side in component
            /// c, by frame-start position - reach plus the side's Bulge, since its dabs follow a curve
            /// that can stray from its chord (see DabSide). False when no active side belongs to c.
            private bool MarkComponent(int c)
            {
                bool any = false;
                for (int f = 0; f < SideCount && !any; f++) any = Sides[f].Active && Sides[f].Component == c;
                if (!any) return false;

                for (int s = 0; s < SlotCount; s++)
                {
                    Vector3 p = PositionsStart[s];
                    bool inside = false;
                    for (int g = 0; g < SideCount && !inside; g++)
                    {
                        DabSide other = Sides[g];
                        if (!other.Active || other.Component != c) continue;
                        float capsule = Reach + other.Bulge;
                        inside = SqrDistanceToSegment(p, other.First, other.Last) <= capsule * capsule * 1.0001f; // a hair over, never under
                    }
                    Weights[s] = inside ? 1f : 0f;
                }
                return true;
            }

            /// Builds side f's slot list (see the job's remarks) at BucketSlots[listBase..], counting-
            /// sorted into buckets along its axis: the slots MarkComponent flagged for f's component.
            /// Buckets are a quarter of the reach wide, widened for a side so long it would need more
            /// than MaxDabBuckets.
            private void BuildBuckets(int f, int listBase)
            {
                DabSide side = Sides[f];
                float min = float.MaxValue, max = float.MinValue;
                int included = 0;
                for (int s = 0; s < SlotCount; s++)
                {
                    Vector3 p = PositionsStart[s];
                    if (Weights[s] == 0f)
                    {
                        BucketKeys[listBase + s] = float.NaN;
                        continue;
                    }
                    float key = Vector3.Dot(p - side.Centre, side.Axis);
                    BucketKeys[listBase + s] = key;
                    min = Mathf.Min(min, key);
                    max = Mathf.Max(max, key);
                    included++;
                }
                if (included == 0) return;

                int starts = f * (MaxDabBuckets + 1);
                float width = Mathf.Max(Mathf.Max(Reach * 0.25f, 1e-6f), (max - min) / (MaxDabBuckets - 1));
                int buckets = Mathf.Min((int)((max - min) / width) + 1, MaxDabBuckets);
                BucketMin[f] = min;
                BucketWidth[f] = width;
                BucketInfo[f] = buckets;

                for (int bk = 0; bk <= buckets; bk++) BucketStarts[starts + bk] = 0;
                for (int s = 0; s < SlotCount; s++)
                {
                    float key = BucketKeys[listBase + s];
                    if (float.IsNaN(key)) continue;
                    BucketStarts[starts + BucketOf(key, min, width, buckets) + 1]++;
                }
                for (int bk = 0; bk < buckets; bk++) BucketStarts[starts + bk + 1] += BucketStarts[starts + bk];

                for (int bk = 0; bk < buckets; bk++) BucketCursor[bk] = BucketStarts[starts + bk];
                for (int s = 0; s < SlotCount; s++)
                {
                    float key = BucketKeys[listBase + s];
                    if (float.IsNaN(key)) continue;
                    BucketSlots[listBase + BucketCursor[BucketOf(key, min, width, buckets)]++] = s;
                }
            }

            private static int BucketOf(float key, float min, float width, int buckets) =>
                (int)Mathf.Clamp((key - min) / width, 0f, buckets - 1);

            private void ApplyDab(DabOp op, ref float maxMoved, bool inGroup, int groupGeneration, ref int groupCount)
            {
                int lo, hi, listBase = 0;
                bool everySlot = maxMoved > MaxTravel;
                if (everySlot)
                {
                    // Moved further than the components allow for - see the job's remarks.
                    Stats[0] += 1f;
                    lo = 0;
                    hi = SlotCount;
                }
                else
                {
                    int f = op.Side;
                    DabSide side = Sides[f];
                    int buckets = BucketInfo[f];
                    if (!side.Active || buckets == 0) return;

                    listBase = BucketInfo[SideCount + f];
                    int starts = f * (MaxDabBuckets + 1);
                    float min = BucketMin[f];
                    float width = BucketWidth[f];
                    float t = Vector3.Dot(op.Point - side.Centre, side.Axis);
                    float pad = Reach + maxMoved;
                    lo = listBase + BucketStarts[starts + BucketOf(t - pad, min, width, buckets)];
                    hi = listBase + BucketStarts[starts + BucketOf(t + pad, min, width, buckets) + 1];
                }

                // Pass 1: weights and the area plane.
                var plane = new AreaPlaneSums();
                for (int j = lo; j < hi; j++)
                {
                    int s = everySlot ? j : BucketSlots[j];
                    Vector3 pos = Positions[s];
                    Vector3 normal = NormalsIn[s];
                    float weight = ClayWeight(pos, normal, MaskIn[s], op.Point, op.Radius, op.Dir, op.Aux,
                        TipRoundness, op.Softness, FrontFacingOnly, op.Camera, out float planeW);
                    Weights[s] = weight;
                    if (planeW > 0f) plane.Add(pos * planeW, normal * planeW, planeW);
                }
                if (!plane.HasWeight) return;

                Vector3 planeOrigin = plane.Origin;
                Vector3 planeNormal = plane.NormalOr(op.Normal);
                DabTangentBasis(planeNormal, op.Op, out Vector3 tangent, out Vector3 bitangent);

                // This dab's own pressure-dependent settings, exactly as ApplyClayBrushLocalJob
                // builds them for a dab of this radius and strength.
                ClayDisplaceSettings displace = Displace;
                displace.LerpFactorScale = op.LerpScale;
                displace.Rate = op.Rate;
                displace.InvStampRadius = 1f / Mathf.Max(0.0001f, op.Radius * AlphaScale);

                // Pass 2: displacement.
                for (int j = lo; j < hi; j++)
                {
                    int s = everySlot ? j : BucketSlots[j];
                    float weight = Weights[s];
                    if (weight <= 0f) continue;
                    Vector3 pos = Positions[s];
                    if (!ClayDisplace(ref pos, weight, StrokeStartIn[s], op.Point, planeOrigin, planeNormal,
                            tangent, bitangent, displace, AlphaSamples))
                        continue;

                    if (inGroup && GroupStamp[s] != groupGeneration)
                    {
                        GroupStamp[s] = groupGeneration;
                        GroupBefore[s] = Positions[s];
                        GroupDelta[s] = Vector3.zero;
                        GroupList[groupCount++] = s;
                    }
                    Positions[s] = pos;
                    AppliedOut[s] = 1;
                    maxMoved = Mathf.Max(maxMoved, (pos - PositionsStart[s]).magnitude);
                }
            }
        }

        private void DisposeDabWork()
        {
            if (_nativeDabWeights.IsCreated) _nativeDabWeights.Dispose();
            if (_nativeDabGroupStamp.IsCreated) _nativeDabGroupStamp.Dispose();
            if (_nativeDabGroupList.IsCreated) _nativeDabGroupList.Dispose();
            if (_nativeDabGroupBefore.IsCreated) _nativeDabGroupBefore.Dispose();
            if (_nativeDabGroupDelta.IsCreated) _nativeDabGroupDelta.Dispose();
            _nativeDabWorkCapacity = 0;
        }

        private void DisposeDabSideLists()
        {
            if (_nativeDabBucketKeys.IsCreated) _nativeDabBucketKeys.Dispose();
            if (_nativeDabBucketSlots.IsCreated) _nativeDabBucketSlots.Dispose();
            _nativeDabSideCapacity = 0;
        }

        private void ReleaseDabProgramResources()
        {
            if (_nativeDabProgram.IsCreated) _nativeDabProgram.Dispose();
            DisposeDabWork();
            DisposeDabSideLists();
            DisposeDabSideTables();
            _dabProgramRecording = false;
        }

        private void DisposeDabSideTables()
        {
            if (_nativeDabSides.IsCreated) _nativeDabSides.Dispose();
            if (_nativeDabBucketStarts.IsCreated) _nativeDabBucketStarts.Dispose();
            if (_nativeDabBucketCursor.IsCreated) _nativeDabBucketCursor.Dispose();
            if (_nativeDabBucketMin.IsCreated) _nativeDabBucketMin.Dispose();
            if (_nativeDabBucketWidth.IsCreated) _nativeDabBucketWidth.Dispose();
            if (_nativeDabBucketCount.IsCreated) _nativeDabBucketCount.Dispose();
            if (_nativeDabStats.IsCreated) _nativeDabStats.Dispose();
        }
    }
}
