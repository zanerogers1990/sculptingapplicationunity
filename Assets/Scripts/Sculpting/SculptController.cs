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
    [RequireComponent(typeof(SculptableMesh))]
    [RequireComponent(typeof(MirrorController))]
    public class SculptController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Camera cam;
        [SerializeField] private SculptableMesh sculptableMesh;
        [SerializeField] private MirrorController mirrorController;
        public GameObject brushPreview;

        [Header("Brush Settings")]
        [SerializeField, Range(0.01f, 1f)] private float brushStrength = 0.1f;
        [SerializeField, Range(0.05f, 2f)] private float brushRadius = 0.5f;
        [SerializeField] private BrushType currentBrush = BrushType.Move;
        [SerializeField] private bool isPositive = true;
        [SerializeField] private bool accumulate = true;

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

        // Same per-brush-memory pattern as _brushPolarity/_brushAccumulate, for Brush Strength
        // and Brush Radius - previously one pair of values shared across every brush, so tuning
        // Crease's strength while Clay was selected would silently carry over the next time
        // Clay was picked back up. Every brush starts at the same default (matching the
        // brushStrength/brushRadius fields' own serialized defaults below) and only diverges
        // once the user actually changes a value while that brush is selected - kept in sync by
        // the CurrentBrush/BrushStrength/BrushRadius setters below.
        private readonly float[] _brushStrengthPerType = CreateDefaultBrushStrength();
        private readonly float[] _brushRadiusPerType = CreateDefaultBrushRadius();

        private static float[] CreateDefaultBrushStrength()
        {
            var arr = new float[Enum.GetValues(typeof(BrushType)).Length];
            for (int i = 0; i < arr.Length; i++) arr[i] = 0.1f; // matches brushStrength field's default below
            return arr;
        }

        private static float[] CreateDefaultBrushRadius()
        {
            var arr = new float[Enum.GetValues(typeof(BrushType)).Length];
            for (int i = 0; i < arr.Length; i++) arr[i] = 0.5f; // matches brushRadius field's default below
            return arr;
        }

        [Header("Clay Brush")]
        [SerializeField, Range(0.1f, 1.5f)] private float clayHeightFactor = 0.6f;
        [SerializeField] private bool useAlpha;
        [SerializeField] private BrushAlphaType alphaType = BrushAlphaType.SoftCircle;
        [SerializeField, Range(0f, 360f)] private float alphaRotation;
        [SerializeField, Range(0.3f, 3f)] private float alphaScale = 1f;
        [SerializeField] private bool invertAlpha;

        [Header("Crease / Dam Standard Brush")]
        [SerializeField, Range(0f, 1f)] private float creasePinch = 0.6f;
        [SerializeField, Range(0.05f, 1f)] private float creaseDepthFactor = 0.35f;
        [SerializeField, Range(0f, 1f)] private float damLipHeight = 0.25f;

        [Header("Masking")]
        // 0 = smoothstep across the whole radius (soft, gradual edges), 1 = full weight
        // everywhere inside the radius with a hard cutoff (immediate, opaque) - see
        // SculptableMesh.PaintMask's hardness remarks.
        [SerializeField, Range(0f, 1f)] private float maskHardness = 0.5f;

        [Header("Remesh Settings")]
        [SerializeField, Range(4, 500)] private int remeshResolution = 24;

        [Header("Debug")]
        [SerializeField] private bool showWireframeGizmo = false;
        [SerializeField] private bool logRayHits = false;

        // Multithreads Inflate/Crease/DamStandard/Clay/Smooth's per-candidate math via Unity
        // Jobs+Burst instead of a plain C# loop - mirrors how Blender/ZBrush get real-time perf
        // at high polycount (CPU spatial acceleration + multithreading), not GPU compute for the
        // brush math itself - see [[project_perf_overhaul_no_gpu_rewrite]] memory for why. Only
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
        // Fraction of the brush radius given over to Clay's edge taper (see ClayFalloff) -
        // the rest of the footprint sits at full weight. ZBrush/Blender's Clay Buildup builds
        // even strips when dragged because its brush profile stays near-flat through most of
        // the footprint and only falls off in a narrow band right at the edge; the old
        // whole-radius smoothstep falloff (still used by Inflate/Smooth, where a rounded
        // profile is correct - see their own remarks) tapered gradually from the center
        // outward instead, so a dragged stroke built a rounded ridge, tallest along the
        // centerline, rather than a flat-topped strip.
        private const float ClayEdgeSoftness = 0.3f;
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
        private const float PressureFloor = 0.35f;
        private float _smoothedPenPressure = 1f;

        // How many world units BrushRadius changes per pixel of horizontal mouse movement
        // while resizing (holding S). Tuned so a full-width drag across a ~1080p window
        // covers roughly the whole 0.01-2 range.
        private const float ResizeSensitivity = 0.0025f;
        // Was 0.05 - too coarse once the camera is zoomed in close (CameraOrbitController's
        // minDistance is 0.5) for fine detail work: the smallest available brush still covered
        // a visibly large patch of the zoomed-in surface. 0.01 matches the floor
        // RebuildSpatialIndex/QueryNear already clamp their own cell size to, so the rest of the
        // brush pipeline was already exercised at this scale.
        public const float MinBrushRadius = 0.01f;
        public const float MaxBrushRadius = 2f;

        private static readonly Color PositiveColor = new Color(0.2f, 1f, 0.4f);
        private static readonly Color NegativeColor = new Color(1f, 0.3f, 0.3f);

        private bool _isHovering;
        private Vector3 _hoverPoint;
        private Vector3 _hoverNormal;
        private bool _previewPositive;
        private Renderer _brushPreviewRenderer;
        private bool _isOverUI;
        // Last position the preview was legitimately shown at (on the mesh, or floating along
        // a viewport mouse ray) - reused whenever the mouse is over a UI panel (e.g. dragging
        // the brush radius slider) so the preview stays put near the model instead of jumping
        // to wherever the panel happens to be on screen.
        private Vector3 _lastGoodPreviewPos;

        // Previous stroke sample in the mesh's local space, used only by Dam Standard to
        // derive a stroke-travel direction for its leading-edge lip; null between strokes
        // (mouse up / hover lost / brush switched) so a fresh stroke starts symmetric.
        private Vector3? _lastDamHoverLocal;

        private bool _isMoveDragging;
        private Vector3 _dragPlanePoint;
        private Vector3 _dragPlaneNormal;
        private Vector3 _lastDragPoint;
        // One selection per active mirror sign, paired with the sign used to make it, so a
        // drag delta can be re-mirrored before being applied to that selection.
        private List<(SculptableMesh.GrabSelection selection, Vector3 sign)> _grabSelections;

        private bool _isResizingBrush;
        private float _resizeStartRadius;
        private float _resizeStartMouseX;
        private Vector2 _resizeAnchorScreenPos;

        private bool _isShiftSmoothActive;
        private BrushType _preShiftBrush;

        // Toggled by tapping M - see HandleMaskPaintInput. A persistent mode switch (like the
        // 1-5 brush hotkeys) rather than a held modifier (like Shift-to-Smooth), since painting
        // a mask is typically its own multi-stroke pass, not a quick one-off tweak mid-sculpt.
        private bool _isMaskPaintMode;

        // Reusable scratch buffers for Clay's area-plane weights and Smooth's relaxation
        // weights - sized once and grown on demand rather than allocated fresh every frame a
        // stroke is held, matching the allocation-avoidance already applied to MeshRemesher
        // (see VertexSpatialGrid/EmitQuads history).
        private float[] _clayWeightScratch = System.Array.Empty<float>();
        private float[] _smoothWeightScratch = System.Array.Empty<float>();

        // Vertex indices actually moved by the current frame's brush application (across every
        // mirror sign) - cleared at the start of each Apply*Brush wrapper, filled in by the
        // matching *Local method(s), then handed to SculptableMesh.ApplyVerticesLocal so it only
        // has to update the triangle-raycast grid for triangles touching these vertices instead
        // of rescanning the whole mesh. See TriangleSpatialGrid for why this matters at higher
        // triangle counts.
        private readonly HashSet<int> _dirtyVertexScratch = new HashSet<int>();

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
        // Clay-only extra scratch (pass-1 weighted reduction inputs to pass-2) - grown alongside
        // the arrays above for simplicity; the extra memory is trivial at footprint-bounded sizes.
        private NativeArray<float> _nativeClayWeights;
        private NativeArray<Vector3> _nativeClayWeightedPos;
        private NativeArray<Vector3> _nativeClayWeightedNormal;
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
                _nativeClayWeightedPos = new NativeArray<Vector3>(_nativeScratchCapacity, Allocator.Persistent);
                _nativeClayWeightedNormal = new NativeArray<Vector3>(_nativeScratchCapacity, Allocator.Persistent);
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
            if (_nativeClayWeightedPos.IsCreated) _nativeClayWeightedPos.Dispose();
            if (_nativeClayWeightedNormal.IsCreated) _nativeClayWeightedNormal.Dispose();
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

        // Smooth-only, full-MESH-sized scratch (unlike every other brush's footprint-sized
        // scratch above) - Smooth's relaxation needs a neighbor's position even when that
        // neighbor sits outside the current brush footprint, so it needs a way to tell "is this
        // global vertex index also one of this call's candidates" (_nativeVertexToSlot) and a
        // fallback position source for when it isn't (_nativeFullPositionMirror). Both avoid an
        // O(total vertex count) COST despite being O(total vertex count) SIZED: the slot map is
        // only ever touched at the (footprint-bounded) candidate indices - populated before the
        // job, reset back to -1 after, an O(1)-per-candidate operation - and only ALLOCATED/
        // filled with -1 once per topology change, not per call (see EnsureSmoothFullMeshScratch).
        // The position mirror IS refreshed via a full O(total) copy each Smooth call, but that's
        // a plain memcpy-like array copy, not per-vertex math - a deliberately accepted
        // tradeoff, see [[project_perf_overhaul_no_gpu_rewrite]] memory.
        private NativeArray<int> _nativeVertexToSlot;
        private NativeArray<Vector3> _nativeFullPositionMirror;
        private int _nativeFullMeshCapacity;
        private NativeArray<int> _nativeSmoothCandidates;

        private void EnsureSmoothFullMeshScratch(int totalVertexCount)
        {
            if (_nativeFullMeshCapacity == totalVertexCount) return;

            if (_nativeVertexToSlot.IsCreated) _nativeVertexToSlot.Dispose();
            if (_nativeFullPositionMirror.IsCreated) _nativeFullPositionMirror.Dispose();

            _nativeFullMeshCapacity = totalVertexCount;
            _nativeVertexToSlot = new NativeArray<int>(totalVertexCount, Allocator.Persistent);
            for (int i = 0; i < totalVertexCount; i++) _nativeVertexToSlot[i] = -1; // one-time O(total) init
            _nativeFullPositionMirror = new NativeArray<Vector3>(totalVertexCount, Allocator.Persistent);
        }

        private void OnDestroy()
        {
            DisposeNativeVertexScratch();
            if (_nativeAlphaSamples.IsCreated) _nativeAlphaSamples.Dispose();
            if (_nativeVertexToSlot.IsCreated) _nativeVertexToSlot.Dispose();
            if (_nativeFullPositionMirror.IsCreated) _nativeFullPositionMirror.Dispose();
            if (_nativeSmoothCandidates.IsCreated) _nativeSmoothCandidates.Dispose();
        }

        // Copies the current candidate footprint's position/normal/mask into the shared native
        // scratch (growing it first if needed) - shared gather step for every Tier-A job
        // (Inflate/Crease/DamStandard/Clay), which only ever read/write within the footprint
        // itself and never need to look outside it (unlike Smooth's neighbor lookups).
        private void GatherCandidatesNative(List<int> candidates, Vector3[] verts, Vector3[] normals, float[] mask)
        {
            EnsureNativeScratch(candidates.Count);
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                _nativePositionsIn[ci] = verts[i];
                _nativeNormalsIn[ci] = normals[i];
                _nativeMaskIn[ci] = mask[i];
            }
        }

        // Writes job results back into the managed vertex array and rebuilds the dirty set -
        // shared scatter step for every Tier-A job. Only consumes PositionsOut where AppliedOut
        // is set, exactly mirroring each managed loop's own "continue" (skip, don't mark dirty)
        // conditions - see each job struct's remarks.
        private void ScatterJobResults(List<int> candidates, Vector3[] verts)
        {
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                if (_nativeAppliedOut[ci] == 0) continue;
                int i = candidates[ci];
                sculptableMesh.RecordUndoBeforeIfNeeded(i);
                verts[i] = _nativePositionsOut[ci];
                _dirtyVertexScratch.Add(i);
            }
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

            public void Execute(int index)
            {
                Vector3 pos = PositionsIn[index];
                float dist = Vector3.Distance(pos, LocalPoint);
                if (dist > BrushRadius) { AppliedOut[index] = 0; return; }

                float t01 = 1f - dist / BrushRadius;
                float weight = t01 * t01 * (3f - 2f * t01) * (1f - MaskIn[index]);
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
        // their managed form - DirLocal/Lip default to zero for plain Crease (a zero DirLocal
        // makes the leading-edge dot-product test never fire, so the lip term naturally never
        // applies, no separate "HasDir" flag needed). AppliedOut mirrors the managed loops' own
        // rule: ANY candidate within BrushRadius counts as touched/dirty, regardless of the
        // resulting lerp factor - unlike Inflate/Clay, Crease/DamStandard never skip on
        // weight <= 0 alone (see ApplyCreaseBrushLocalManaged/ApplyDamStandardBrushLocalManaged).
        [BurstCompile(CompileSynchronously = true)]
        private struct CreaseJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> PositionsIn;
            [ReadOnly] public NativeArray<float> MaskIn;
            public NativeArray<Vector3> PositionsOut;
            public NativeArray<byte> AppliedOut;

            public Vector3 LocalPoint;
            public Vector3 LocalNormal;
            public Vector3 DirLocal; // Vector3.zero for plain Crease
            public float BrushRadius;
            public float Depth;
            public float Lip; // 0 for plain Crease
            public float Pinch;
            public float LerpFactorScale; // brushStrength * CreaseSpeed * dt
            public bool Accumulate;
            public float DepthRate; // sign * creaseDepthFactor * brushStrength * CreaseSpeed * dt
            public float LipRate; // 0 for plain Crease
            public float PinchRateScale; // creasePinch * brushStrength * CreaseSpeed * dt

            public void Execute(int index)
            {
                Vector3 pos = PositionsIn[index];
                Vector3 toVert = pos - LocalPoint;
                float dist = toVert.magnitude;
                if (dist > BrushRadius) { AppliedOut[index] = 0; return; }

                float t01 = 1f - dist / BrushRadius;
                float weight = t01 * t01 * t01 * (1f - MaskIn[index]); // sharper falloff than Clay's smoothstep

                float alongNormal = Vector3.Dot(toVert, LocalNormal);
                Vector3 tangentialOffset = toVert - LocalNormal * alongNormal;
                bool hasLip = Vector3.Dot(tangentialOffset, DirLocal) > 0f;

                if (Accumulate)
                {
                    float normalRate = DepthRate;
                    if (hasLip) normalRate += LipRate;
                    Vector3 pinchDelta = -tangentialOffset * Mathf.Clamp01(weight * PinchRateScale);
                    PositionsOut[index] = pos + LocalNormal * (normalRate * weight) + pinchDelta;
                }
                else
                {
                    Vector3 pinched = tangentialOffset * (1f - Pinch * weight);
                    float normalOffset = Depth * weight;
                    if (hasLip) normalOffset += Lip * weight;

                    Vector3 target = LocalPoint + pinched + LocalNormal * normalOffset;
                    Vector3 toTarget = target - pos;
                    float lerp = Mathf.Clamp01(weight * LerpFactorScale);
                    PositionsOut[index] = pos + toTarget * lerp;
                }
                AppliedOut[index] = 1;
            }
        }

        // Clay's own radial falloff (see ClayEdgeSoftness remarks) - full weight through the
        // inner (1 - ClayEdgeSoftness) of the radius, smoothstepping down to 0 only across the
        // outer edge band. Shared by ClayWeightJob (Burst) and ApplyClayBrushLocalManaged so
        // both brush paths build an identical flat-topped profile; plain float math, so Burst
        // can inline it into the job same as any other method call.
        private static float ClayFalloff(float t01)
        {
            if (t01 >= ClayEdgeSoftness) return 1f;
            float e = t01 / ClayEdgeSoftness;
            return e * e * (3f - 2f * e);
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

            public void Execute(int index)
            {
                Vector3 pos = PositionsIn[index];
                float dist = Vector3.Distance(pos, LocalPoint);
                if (dist > BrushRadius)
                {
                    WeightsOut[index] = 0f;
                    WeightedPosOut[index] = Vector3.zero;
                    WeightedNormalOut[index] = Vector3.zero;
                    return;
                }

                float t01 = 1f - dist / BrushRadius;
                float w = ClayFalloff(t01) * (1f - MaskIn[index]);
                WeightsOut[index] = w;
                WeightedPosOut[index] = pos * w;
                WeightedNormalOut[index] = NormalsIn[index] * w;
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

                if (Accumulate)
                {
                    PositionsOut[index] = pos + PlaneNormal * (Rate * weight);
                }
                else
                {
                    Vector3 toPlane = pos - PlaneOrigin;
                    float alongNormal = Vector3.Dot(toPlane, PlaneNormal);
                    Vector3 tangentialOffset = toPlane - PlaneNormal * alongNormal;
                    Vector3 target = PlaneOrigin + tangentialOffset + PlaneNormal * Height;

                    Vector3 toTarget = target - pos;
                    float lerp = Mathf.Clamp01(weight * LerpFactorScale);
                    PositionsOut[index] = pos + toTarget * lerp;
                }
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

        // Precomputes Smooth's per-candidate falloff weight once, shared read-only across every
        // relaxation pass - direct port of ApplySmoothBrushLocalManaged's first loop.
        [BurstCompile(CompileSynchronously = true)]
        private struct SmoothWeightJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Vector3> PositionsIn;
            [ReadOnly] public NativeArray<float> MaskIn;
            public NativeArray<float> WeightsOut;
            public Vector3 LocalPoint;
            public float BrushRadius;

            public void Execute(int index)
            {
                float dist = Vector3.Distance(PositionsIn[index], LocalPoint);
                if (dist > BrushRadius) { WeightsOut[index] = 0f; return; }
                float t01 = 1f - dist / BrushRadius;
                WeightsOut[index] = t01 * t01 * (3f - 2f * t01) * (1f - MaskIn[index]); // smoothstep, masked-out
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
        // visually/numerically reasonable result, see [[project_perf_overhaul_no_gpu_rewrite]].
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
        // visibly uneven stroke instead of an evenly built-up ridge. PressureFloor guarantees
        // even the lightest touch still applies a meaningful fraction of full strength, and the
        // sqrt curve front-loads the response so a moderate press already reads as most of full
        // strength - compressing how much a given pressure fluctuation swings the result.
        private float CurrentPressure
        {
            get
            {
                var pen = Pen.current;
                if (pen == null || !pen.tip.isPressed) return 1f;
                return PressureFloor + (1f - PressureFloor) * Mathf.Sqrt(Mathf.Clamp01(_smoothedPenPressure));
            }
        }

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
        private float EffectiveBrushStrengthAccumulate => brushStrength * Mathf.Lerp(1f, CurrentPressure, AccumulatePressureInfluence);

        // Same immediate-write-through as BrushStrength above.
        public float BrushRadius
        {
            get => brushRadius;
            set
            {
                brushRadius = Mathf.Clamp(value, MinBrushRadius, MaxBrushRadius);
                _brushRadiusPerType[(int)currentBrush] = brushRadius;
            }
        }
        public bool IsResizingBrush => _isResizingBrush;
        public Vector2 ResizeAnchorScreenPosition => _resizeAnchorScreenPos;

        public bool IsMaskPaintMode
        {
            get => _isMaskPaintMode;
            set
            {
                if (_isMaskPaintMode == value) return;
                _isMaskPaintMode = value;
                if (_isMaskPaintMode) EndMoveDrag(); // don't leave a grab mid-drag while painting mask
            }
        }

        public BrushType CurrentBrush
        {
            get => currentBrush;
            set
            {
                if (currentBrush != value)
                {
                    EndMoveDrag();
                    _lastDamHoverLocal = null;
                    _brushPolarity[(int)currentBrush] = isPositive;
                    _brushAccumulate[(int)currentBrush] = accumulate;
                    _brushStrengthPerType[(int)currentBrush] = brushStrength;
                    _brushRadiusPerType[(int)currentBrush] = brushRadius;
                    currentBrush = value;
                    isPositive = _brushPolarity[(int)currentBrush];
                    accumulate = _brushAccumulate[(int)currentBrush];
                    brushStrength = _brushStrengthPerType[(int)currentBrush];
                    brushRadius = _brushRadiusPerType[(int)currentBrush];
                }
            }
        }
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
        public float ClayHeightFactor { get => clayHeightFactor; set => clayHeightFactor = Mathf.Clamp(value, 0.1f, 1.5f); }
        public float CreasePinch { get => creasePinch; set => creasePinch = Mathf.Clamp01(value); }
        public float CreaseDepthFactor { get => creaseDepthFactor; set => creaseDepthFactor = Mathf.Clamp(value, 0.05f, 1f); }
        public float DamLipHeight { get => damLipHeight; set => damLipHeight = Mathf.Clamp01(value); }
        public float MaskHardness { get => maskHardness; set => maskHardness = Mathf.Clamp01(value); }
        public bool UseAlpha { get => useAlpha; set => useAlpha = value; }
        public BrushAlphaType AlphaType { get => alphaType; set => alphaType = value; }
        public float AlphaRotation { get => alphaRotation; set => alphaRotation = Mathf.Repeat(value, 360f); }
        public float AlphaScale { get => alphaScale; set => alphaScale = Mathf.Clamp(value, 0.3f, 3f); }
        public bool InvertAlpha { get => invertAlpha; set => invertAlpha = value; }
        public bool ShowWireframeGizmo { get => showWireframeGizmo; set => showWireframeGizmo = value; }
        public bool LogRayHits { get => logRayHits; set => logRayHits = value; }
        public bool UseBurstJobs { get => useBurstJobs; set => useBurstJobs = value; }
        public int RemeshResolution { get => remeshResolution; set => remeshResolution = Mathf.Clamp(value, 4, 500); }

        // GetIndexCount/vertexCount rather than .triangles/.vertices - those copy the whole
        // index/vertex buffer on every access, which would be a real cost read every frame by
        // the UI's poly-count display at multi-million-triangle mesh sizes.
        public int TriangleCount => sculptableMesh != null && sculptableMesh.Mesh != null
            ? (int)sculptableMesh.Mesh.GetIndexCount(0) / 3 : 0;
        public int VertexCount => sculptableMesh != null && sculptableMesh.Mesh != null
            ? sculptableMesh.Mesh.vertexCount : 0;

        public bool CanUndo => sculptableMesh != null && sculptableMesh.CanUndo;
        public bool CanRedo => sculptableMesh != null && sculptableMesh.CanRedo;
        public void Undo() { EndMoveDrag(); sculptableMesh.Undo(); }
        public void Redo() { EndMoveDrag(); sculptableMesh.Redo(); }

        // Not wired into undo/redo, same deliberate scope call as PaintMask itself (see
        // SculptableMesh.PaintMask remarks) - masking doesn't move geometry.
        public void InvertMask() => sculptableMesh.InvertMask();

        // Lazily resolved (rather than relying on Awake) since SculptUIBuilder reads this
        // while building the HUD, and MonoBehaviour Awake order between separate components
        // isn't guaranteed - its own Awake may not have run first.
        public MirrorController Mirror => mirrorController != null ? mirrorController : (mirrorController = GetComponent<MirrorController>());

        private void Awake()
        {
            if (sculptableMesh == null) sculptableMesh = GetComponent<SculptableMesh>();
            if (mirrorController == null) mirrorController = GetComponent<MirrorController>();
            if (cam == null) cam = Camera.main;
            if (brushPreview == null) brushPreview = GameObject.Find("BrushPreview");
            if (brushPreview != null) _brushPreviewRenderer = brushPreview.GetComponent<Renderer>();

            // Overrides whatever material is authored on the BrushPreview GameObject with a
            // runtime one that always draws on top of the depth buffer (see
            // BrushPreviewOverlay.shader) - a normal depth-tested material gets swallowed by
            // the sculpted mesh whenever the preview's position lands even slightly behind its
            // surface, which happens easily during the S-drag resize gesture.
            if (_brushPreviewRenderer != null)
            {
                Shader overlayShader = Shader.Find("Custom/BrushPreviewOverlay");
                if (overlayShader != null) _brushPreviewRenderer.material = new Material(overlayShader);
            }

            if (sculptableMesh != null) _lastGoodPreviewPos = sculptableMesh.transform.position;

            // The serialized `isPositive`/`accumulate`/`brushStrength`/`brushRadius` predate
            // per-brush memory and may be stale for whatever brush is currently selected - start
            // from this brush's own remembered defaults instead (see
            // _brushPolarity/_brushAccumulate/_brushStrengthPerType/_brushRadiusPerType remarks).
            isPositive = _brushPolarity[(int)currentBrush];
            accumulate = _brushAccumulate[(int)currentBrush];
            brushStrength = _brushStrengthPerType[(int)currentBrush];
            brushRadius = _brushRadiusPerType[(int)currentBrush];
        }

        private void Update()
        {
            HandleBrushSwitchKeys();
            HandleBrushResizeKey();
            HandleUndoRedoKeys();
            UpdatePenPressure();
            HandleSculptInput();
            HandleStrokeEndCommit();
            UpdateBrushPreview();
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
            if (mouse == null) return;
            if (mouse.leftButton.wasReleasedThisFrame || mouse.rightButton.wasReleasedThisFrame)
                sculptableMesh.EndStrokeUndo();
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
            if (kb == null || _isResizingBrush) return;

            if (kb.zKey.wasPressedThisFrame)
            {
                if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) Redo();
                else Undo();
            }
        }

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

            if (kb.digit1Key.wasPressedThisFrame) CurrentBrush = BrushType.Move;
            else if (kb.digit2Key.wasPressedThisFrame) CurrentBrush = BrushType.Clay;
            else if (kb.digit3Key.wasPressedThisFrame) CurrentBrush = BrushType.Smooth;
            else if (kb.digit4Key.wasPressedThisFrame) CurrentBrush = BrushType.Crease;
            else if (kb.digit5Key.wasPressedThisFrame) CurrentBrush = BrushType.DamStandard;
            else if (kb.digit6Key.wasPressedThisFrame) CurrentBrush = BrushType.Inflate;

            // M used to trigger Remesh directly; moved to R (still reachable via the Remesh
            // button in the Brush panel either way) so M is free for the mask-paint toggle,
            // matching most sculpting apps' M-for-mask convention.
            if (kb.mKey.wasPressedThisFrame) IsMaskPaintMode = !IsMaskPaintMode;
            if (kb.rKey.wasPressedThisFrame) Remesh();
        }

        // Holding Shift temporarily switches to the Smooth brush, ZBrush/Blender-style,
        // reverting to whatever brush was active the moment Shift is released - lets you
        // smooth out a stroke without breaking flow to switch brushes and back. Guarded off
        // during the resize gauge for the same reason other input handlers are.
        private void HandleShiftSmoothOverride(Keyboard kb)
        {
            if (_isResizingBrush) return;
            bool shiftHeld = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            if (shiftHeld && !_isShiftSmoothActive)
            {
                _preShiftBrush = currentBrush;
                _isShiftSmoothActive = true;
                CurrentBrush = BrushType.Smooth;
            }
            else if (!shiftHeld && _isShiftSmoothActive)
            {
                _isShiftSmoothActive = false;
                CurrentBrush = _preShiftBrush;
            }
        }

        // Holding S enters a resize mode (instead of sculpting) where horizontal mouse
        // movement scrubs BrushRadius live, ZBrush/Blender-style, with the popup gauge
        // SculptUIBuilder draws at ResizeAnchorScreenPosition tracking the value.
        private void HandleBrushResizeKey()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (kb.sKey.wasPressedThisFrame)
            {
                EndMoveDrag(); // don't leave a grab mid-drag while the resize gauge is up
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

        private void HandleSculptInput()
        {
            var mouse = Mouse.current;
            if (mouse == null || cam == null) return;

            // While the resize gauge is up, mouse movement scrubs brush size, not sculpting.
            // Force _isOverUI false too so UpdateBrushPreview follows the mouse ray (the
            // deliberate resize-gauge UX) rather than freezing at a stale over-UI position.
            if (_isResizingBrush)
            {
                _isHovering = false;
                _isOverUI = false;
                return;
            }

            bool overUI = UnityEngine.EventSystems.EventSystem.current != null &&
                          UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
            _isOverUI = overUI;
            bool altHeld = Keyboard.current != null && Keyboard.current.leftAltKey.isPressed;

            // Mask painting is its own input mode, not one of the sculpting brushes - doesn't
            // move vertices, so it skips the undo snapshot/spatial-grid-rebuild-on-press below
            // entirely (mask isn't part of undo history - see HandleMaskPaintInput remarks).
            if (_isMaskPaintMode)
            {
                HandleMaskPaintInput(mouse, overUI, altHeld);
                return;
            }

            // Rebuild the vertex spatial index once at the start of every stroke (not every
            // frame - rebuilding is itself O(vertex count), so doing it per-frame would defeat
            // the point) so Clay/Smooth/Crease/Dam Standard/Move's per-stroke vertex lookups
            // don't have to scan the whole mesh. Cell size tracks the current brush radius so
            // the grid stays well-matched to typical query size. Also begins the stroke's undo
            // delta accumulator here, once per stroke rather than per frame for the same reason -
            // a stroke that turns out to be a click on empty space (missing the mesh) never
            // records anything, so HandleStrokeEndCommit's EndStrokeUndo call just no-ops for it
            // at zero cost (see SculptableMesh.BeginStrokeUndo/EndStrokeUndo remarks) - not worth
            // the extra complexity of gating this from inside every individual brush handler.
            if (!overUI && !altHeld && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
            {
                sculptableMesh.RebuildSpatialIndex(Mathf.Max(brushRadius * 0.5f, 0.01f));
                sculptableMesh.BeginStrokeUndo();
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
                default:
                    HandleClayInput(mouse, overUI, altHeld);
                    break;
            }

        }

        // Left mouse paints mask (protects the area from every brush - see
        // SculptableMesh.Mask/PaintMask), right mouse erases it, same LMB-apply/RMB-invert
        // convention as the sculpting brushes. Deliberately NOT part of undo history - masking
        // doesn't move geometry, and folding it into SculptHistory's vertex/triangle snapshot
        // format would be a larger change than this "just a basic one" ask called for; flagged
        // here rather than silently left out.
        private void HandleMaskPaintInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            bool rightHeld = mouse.rightButton.isPressed;
            _previewPositive = !rightHeld; // green while painting, red while erasing

            if (altHeld) return;
            if (mouse.leftButton.isPressed) ApplyMaskPaint(hitPoint, true);
            else if (rightHeld) ApplyMaskPaint(hitPoint, false);
        }

        private void ApplyMaskPaint(Vector3 worldPoint, bool applying)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            float speed = Mathf.Lerp(MaskPaintSpeedSoft, MaskPaintSpeedHard, maskHardness);
            float amount = (applying ? 1f : -1f) * EffectiveBrushStrength * speed * Time.deltaTime;

            foreach (Vector3 sign in Mirror.GetMirrorSigns())
                sculptableMesh.PaintMask(Vector3.Scale(localPoint, sign), brushRadius, amount, maskHardness);
        }

        private void HandleClayInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            bool rightHeld = mouse.rightButton.isPressed;
            _previewPositive = rightHeld ? !isPositive : isPositive;

            if (logRayHits && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                Debug.Log($"[Sculpt] Ray hit at {hitPoint}, normal {hitNormal}, distance {Vector3.Distance(ray.origin, hitPoint):F2}");

            // Alt+Left-drag is reserved for orbiting the camera (see CameraOrbitController),
            // so don't also sculpt while Alt is held. Right-drag sculpts with the sign
            // inverted, independent of Alt, matching most sculpting apps' invert convention.
            if (mouse.leftButton.isPressed && !altHeld)
                ApplyClayBrush(hitPoint, hitNormal, isPositive);
            else if (rightHeld)
                ApplyClayBrush(hitPoint, hitNormal, !isPositive);
        }

        private void ApplyClayBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            Vector3 localNormal = t.InverseTransformDirection(worldNormal).normalized;

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                Vector3 mirroredPoint = Vector3.Scale(localPoint, sign);
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                ApplyClayBrushLocal(mirroredPoint, mirroredNormal, positive);
            }

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
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
        private void ApplyClayBrushLocal(Vector3 localPoint, Vector3 localNormal, bool positive)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            Vector3[] normals = sculptableMesh.Normals;

            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyClayBrushLocalJob(localPoint, localNormal, positive, candidates, verts, normals);
            else
                ApplyClayBrushLocalManaged(localPoint, localNormal, positive, candidates, verts, normals);
        }

        private void ApplyClayBrushLocalJob(Vector3 localPoint, Vector3 localNormal, bool positive, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float effectiveStrength = EffectiveBrushStrength;
            float effectiveStrengthAccumulate = EffectiveBrushStrengthAccumulate;
            float height = brushRadius * clayHeightFactor * sign;

            GatherCandidatesNative(candidates, verts, normals, sculptableMesh.Mask);

            var weightJob = new ClayWeightJob
            {
                PositionsIn = _nativePositionsIn,
                NormalsIn = _nativeNormalsIn,
                MaskIn = _nativeMaskIn,
                WeightsOut = _nativeClayWeights,
                WeightedPosOut = _nativeClayWeightedPos,
                WeightedNormalOut = _nativeClayWeightedNormal,
                LocalPoint = localPoint,
                BrushRadius = brushRadius,
            };
            weightJob.Schedule(candidates.Count, 32).Complete();

            // Sequential reduction across the (footprint-bounded) candidate list - see
            // ClayWeightJob's remarks on why this stays on the main thread.
            Vector3 planeOriginSum = Vector3.zero, planeNormalSum = Vector3.zero;
            float planeWeightSum = 0f;
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                planeOriginSum += _nativeClayWeightedPos[ci];
                planeNormalSum += _nativeClayWeightedNormal[ci];
                planeWeightSum += _nativeClayWeights[ci];
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
                InvStampRadius = 1f / Mathf.Max(0.0001f, brushRadius * alphaScale),
                AlphaSize = useAlpha ? _nativeAlphaSize : 0,
                Accumulate = accumulate,
                Rate = sign * clayHeightFactor * effectiveStrengthAccumulate * ClaySpeed * dt,
            };
            dispJob.Schedule(candidates.Count, 32).Complete();

            ScatterJobResults(candidates, verts);
        }

        private void ApplyClayBrushLocalManaged(Vector3 localPoint, Vector3 localNormal, bool positive, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float effectiveStrength = EffectiveBrushStrength;
            float effectiveStrengthAccumulate = EffectiveBrushStrengthAccumulate;
            float height = brushRadius * clayHeightFactor * sign;

            if (_clayWeightScratch.Length < candidates.Count) _clayWeightScratch = new float[candidates.Count];
            float[] weights = _clayWeightScratch;

            Vector3 planeOriginSum = Vector3.zero;
            Vector3 planeNormalSum = Vector3.zero;
            float planeWeightSum = 0f;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(verts[i], localPoint);
                if (dist > brushRadius) { weights[ci] = 0f; continue; }

                float t01 = 1f - dist / brushRadius;
                float w = ClayFalloff(t01) * (1f - sculptableMesh.Mask[i]); // flat plateau, edge-only taper - see ClayEdgeSoftness
                weights[ci] = w;

                planeOriginSum += verts[i] * w;
                planeNormalSum += normals[i] * w;
                planeWeightSum += w;
            }

            if (planeWeightSum <= 1e-6f) return;

            Vector3 planeOrigin = planeOriginSum / planeWeightSum;
            Vector3 planeNormal = planeNormalSum.sqrMagnitude > 1e-8f
                ? planeNormalSum.normalized : localNormal;

            BuildTangentBasis(planeNormal, out Vector3 tangent, out Vector3 bitangent);
            float rot = alphaRotation * Mathf.Deg2Rad;
            float cosR = Mathf.Cos(rot), sinR = Mathf.Sin(rot);
            BrushAlphaLibrary.AlphaData alpha = useAlpha ? BrushAlphaLibrary.Get(alphaType) : default;
            float invStampRadius = 1f / Mathf.Max(0.0001f, brushRadius * alphaScale);

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

                sculptableMesh.RecordUndoBeforeIfNeeded(i);

                if (accumulate)
                {
                    // No target/plateau to converge toward - a continuous rate along the plane
                    // normal, same shape as Inflate's push, so holding the brush in place keeps
                    // building the plateau higher instead of stopping at `height`. Still uses
                    // the plane normal (not each vertex's own normal), so Clay's "flattens the
                    // footprint together" character survives; only the OFF-mode cap is gone.
                    verts[i] += planeNormal * (sign * clayHeightFactor * effectiveStrengthAccumulate * ClaySpeed * dt) * weight;
                }
                else
                {
                    Vector3 toPlane = verts[i] - planeOrigin;
                    float alongNormal = Vector3.Dot(toPlane, planeNormal);
                    Vector3 tangentialOffset = toPlane - planeNormal * alongNormal;
                    Vector3 target = planeOrigin + tangentialOffset + planeNormal * height;

                    Vector3 toTarget = target - verts[i];
                    // Clamp01: this is a lerp fraction toward target, not a velocity - on a
                    // frame hitch (large dt, e.g. during a heavy Remesh) an unclamped factor can
                    // exceed 1 and overshoot past the target plane. Since Clay's target
                    // recomputes from the vertex's own (now overshot) position next frame, an
                    // uncapped factor compounds into a runaway explosion rather than settling -
                    // reproduced this empirically while testing this brush (a synthetic
                    // large-dt stroke sent a vertex from radius 0.5 to over 3.0 in 90 frames
                    // before this clamp existed).
                    verts[i] += toTarget * Mathf.Clamp01(weight * effectiveStrength * ClaySpeed * dt);
                }

                _dirtyVertexScratch.Add(i);
            }
        }

        private static void BuildTangentBasis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent)
        {
            Vector3 up = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            tangent = Vector3.Cross(up, normal).normalized;
            bitangent = Vector3.Cross(normal, tangent);
        }

        private void HandleCreaseInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            bool rightHeld = mouse.rightButton.isPressed;
            _previewPositive = rightHeld ? !isPositive : isPositive;

            if (logRayHits && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                Debug.Log($"[Sculpt] Ray hit at {hitPoint}, normal {hitNormal}, distance {Vector3.Distance(ray.origin, hitPoint):F2}");

            if (mouse.leftButton.isPressed && !altHeld)
                ApplyCreaseBrush(hitPoint, hitNormal, isPositive);
            else if (rightHeld)
                ApplyCreaseBrush(hitPoint, hitNormal, !isPositive);
        }

        private void ApplyCreaseBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            Vector3 localNormal = t.InverseTransformDirection(worldNormal).normalized;

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                Vector3 mirroredPoint = Vector3.Scale(localPoint, sign);
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                ApplyCreaseBrushLocal(mirroredPoint, mirroredNormal, positive);
            }

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        // Pinches the tangential footprint toward the stroke centerline while carving along
        // the normal with a depth that's scaled by the same per-vertex weight (unlike Clay's
        // constant plateau height), so the profile tapers to a sharp ridge/valley along the
        // stroke instead of a flat-topped dome.
        private void ApplyCreaseBrushLocal(Vector3 localPoint, Vector3 localNormal, bool positive)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyCreaseBrushLocalJob(localPoint, localNormal, positive, Vector3.zero, 0f, candidates, verts);
            else
                ApplyCreaseBrushLocalManaged(localPoint, localNormal, positive, candidates, verts);
        }

        // Shared by Crease (dirLocal=zero, lip=0 - see CreaseJob remarks) and DamStandard.
        private void ApplyCreaseBrushLocalJob(Vector3 localPoint, Vector3 localNormal, bool positive,
            Vector3 dirLocal, float lipFactor, List<int> candidates, Vector3[] verts)
        {
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float effectiveStrength = EffectiveBrushStrength;
            float effectiveStrengthAccumulate = EffectiveBrushStrengthAccumulate;

            // CreaseJob has no NormalsIn field (unused by this brush) - still gathered via the
            // shared helper since it always populates all three arrays; harmless, just unread.
            GatherCandidatesNative(candidates, verts, sculptableMesh.Normals, sculptableMesh.Mask);
            var job = new CreaseJob
            {
                PositionsIn = _nativePositionsIn,
                MaskIn = _nativeMaskIn,
                PositionsOut = _nativePositionsOut,
                AppliedOut = _nativeAppliedOut,
                LocalPoint = localPoint,
                LocalNormal = localNormal,
                DirLocal = dirLocal,
                BrushRadius = brushRadius,
                Depth = brushRadius * creaseDepthFactor * sign,
                Lip = brushRadius * lipFactor * sign,
                Pinch = creasePinch,
                LerpFactorScale = effectiveStrength * CreaseSpeed * dt,
                Accumulate = accumulate,
                DepthRate = sign * creaseDepthFactor * effectiveStrengthAccumulate * CreaseSpeed * dt,
                LipRate = sign * lipFactor * effectiveStrengthAccumulate * CreaseSpeed * dt,
                PinchRateScale = creasePinch * effectiveStrengthAccumulate * CreaseSpeed * dt,
            };
            job.Schedule(candidates.Count, 32).Complete();

            ScatterJobResults(candidates, verts);
        }

        private void ApplyCreaseBrushLocalManaged(Vector3 localPoint, Vector3 localNormal, bool positive, List<int> candidates, Vector3[] verts)
        {
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float effectiveStrength = EffectiveBrushStrength;
            float effectiveStrengthAccumulate = EffectiveBrushStrengthAccumulate;
            float depth = brushRadius * creaseDepthFactor * sign;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 toVert = verts[i] - localPoint;
                float dist = toVert.magnitude;
                if (dist > brushRadius) continue;

                float t01 = 1f - dist / brushRadius;
                float weight = t01 * t01 * t01 * (1f - sculptableMesh.Mask[i]); // sharper falloff than Clay's smoothstep - a narrower peak

                float alongNormal = Vector3.Dot(toVert, localNormal);
                Vector3 tangentialOffset = toVert - localNormal * alongNormal;

                sculptableMesh.RecordUndoBeforeIfNeeded(i);

                if (accumulate)
                {
                    // Depth keeps digging deeper for as long as the brush is held - a
                    // continuous rate, not a target/plateau (same shape as Clay/Inflate's
                    // accumulate-on push).
                    verts[i] += localNormal * (sign * creaseDepthFactor * effectiveStrengthAccumulate * CreaseSpeed * dt) * weight;
                    // Pinch stays a bounded pull toward the stroke centerline - it's a shape
                    // control, not a depth amount, so letting it run away would just make the
                    // groove's cross-section overshoot past center and oscillate instead of
                    // digging deeper.
                    verts[i] += -tangentialOffset * Mathf.Clamp01(creasePinch * weight * effectiveStrengthAccumulate * CreaseSpeed * dt);
                }
                else
                {
                    Vector3 pinched = tangentialOffset * (1f - creasePinch * weight);
                    Vector3 target = localPoint + pinched + localNormal * (depth * weight);
                    Vector3 toTarget = target - verts[i];
                    verts[i] += toTarget * Mathf.Clamp01(weight * effectiveStrength * CreaseSpeed * dt); // see Clamp01 note on Clay
                }

                _dirtyVertexScratch.Add(i);
            }
        }

        private void HandleDamStandardInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) { _lastDamHoverLocal = null; return; }

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) { _lastDamHoverLocal = null; return; }

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            bool rightHeld = mouse.rightButton.isPressed;
            _previewPositive = rightHeld ? !isPositive : isPositive;

            if (logRayHits && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                Debug.Log($"[Sculpt] Ray hit at {hitPoint}, normal {hitNormal}, distance {Vector3.Distance(ray.origin, hitPoint):F2}");

            bool sculpting = (mouse.leftButton.isPressed && !altHeld) || rightHeld;
            if (!sculpting) { _lastDamHoverLocal = null; return; }

            ApplyDamStandardBrush(hitPoint, hitNormal, rightHeld ? !isPositive : isPositive);
        }

        private void ApplyDamStandardBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            Vector3 localNormal = t.InverseTransformDirection(worldNormal).normalized;

            // Stroke-travel direction in the tangent plane, used to bias a raised lip onto the
            // leading edge and leave a groove on the trailing edge - the asymmetry that
            // distinguishes Dam Standard from a plain symmetric Crease. No reliable direction
            // exists yet on the stroke's first sample or a stationary dab, so this falls back
            // to symmetric Crease-like carving in that case - an honest simplification rather
            // than full directional Dam Standard behavior.
            Vector3 dirLocal = Vector3.zero;
            if (_lastDamHoverLocal.HasValue)
            {
                Vector3 raw = localPoint - _lastDamHoverLocal.Value;
                Vector3 tangential = raw - localNormal * Vector3.Dot(raw, localNormal);
                if (tangential.sqrMagnitude > 1e-8f) dirLocal = tangential.normalized;
            }
            _lastDamHoverLocal = localPoint;

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                Vector3 mirroredPoint = Vector3.Scale(localPoint, sign);
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                Vector3 mirroredDir = Vector3.Scale(dirLocal, sign);
                ApplyDamStandardBrushLocal(mirroredPoint, mirroredNormal, mirroredDir, positive);
            }

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        private void ApplyDamStandardBrushLocal(Vector3 localPoint, Vector3 localNormal, Vector3 dirLocal, bool positive)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            // A zero dirLocal (no reliable stroke direction yet - see ApplyDamStandardBrush's
            // remarks) naturally disables the lip term in both paths: the managed loop's own
            // hasDir/dot-product gate, and CreaseJob's dot-product-with-zero-vector (always 0,
            // never > 0) - no separate "hasDir" needed there either.
            if (useBurstJobs && candidates.Count >= MinJobVertexCount)
                ApplyCreaseBrushLocalJob(localPoint, localNormal, positive, dirLocal, damLipHeight, candidates, verts);
            else
                ApplyDamStandardBrushLocalManaged(localPoint, localNormal, dirLocal, positive, candidates, verts);
        }

        private void ApplyDamStandardBrushLocalManaged(Vector3 localPoint, Vector3 localNormal, Vector3 dirLocal, bool positive, List<int> candidates, Vector3[] verts)
        {
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float effectiveStrength = EffectiveBrushStrength;
            float effectiveStrengthAccumulate = EffectiveBrushStrengthAccumulate;
            float depth = brushRadius * creaseDepthFactor * sign;
            float lip = brushRadius * damLipHeight * sign;
            bool hasDir = dirLocal.sqrMagnitude > 1e-6f;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 toVert = verts[i] - localPoint;
                float dist = toVert.magnitude;
                if (dist > brushRadius) continue;

                float t01 = 1f - dist / brushRadius;
                float weight = t01 * t01 * t01 * (1f - sculptableMesh.Mask[i]);

                float alongNormal = Vector3.Dot(toVert, localNormal);
                Vector3 tangentialOffset = toVert - localNormal * alongNormal;
                bool hasLip = hasDir && Vector3.Dot(tangentialOffset, dirLocal) > 0f;

                sculptableMesh.RecordUndoBeforeIfNeeded(i);

                if (accumulate)
                {
                    // Depth (and the leading-edge lip, when active) keep digging/building for as
                    // long as the brush is held - continuous rates, not a target/plateau, same
                    // as Crease's own accumulate-on branch.
                    float normalRate = sign * creaseDepthFactor * effectiveStrengthAccumulate * CreaseSpeed * dt;
                    if (hasLip) normalRate += sign * damLipHeight * effectiveStrengthAccumulate * CreaseSpeed * dt;
                    verts[i] += localNormal * (normalRate * weight);
                    verts[i] += -tangentialOffset * Mathf.Clamp01(creasePinch * weight * effectiveStrengthAccumulate * CreaseSpeed * dt);
                }
                else
                {
                    Vector3 pinched = tangentialOffset * (1f - creasePinch * weight);
                    float normalOffset = depth * weight;
                    if (hasLip) normalOffset += lip * weight;

                    Vector3 target = localPoint + pinched + localNormal * normalOffset;
                    Vector3 toTarget = target - verts[i];
                    verts[i] += toTarget * Mathf.Clamp01(weight * effectiveStrength * CreaseSpeed * dt); // see Clamp01 note on Clay
                }

                _dirtyVertexScratch.Add(i);
            }
        }

        private void HandleInflateInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            bool rightHeld = mouse.rightButton.isPressed;
            _previewPositive = rightHeld ? !isPositive : isPositive;

            if (logRayHits && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                Debug.Log($"[Sculpt] Ray hit at {hitPoint}, normal {hitNormal}, distance {Vector3.Distance(ray.origin, hitPoint):F2}");

            if (mouse.leftButton.isPressed && !altHeld)
                ApplyInflateBrush(hitPoint, hitNormal, isPositive);
            else if (rightHeld)
                ApplyInflateBrush(hitPoint, hitNormal, !isPositive);
        }

        private void ApplyInflateBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            Vector3 localNormal = t.InverseTransformDirection(worldNormal).normalized;

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                ApplyInflateBrushLocal(Vector3.Scale(localPoint, sign), mirroredNormal, positive);
            }

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
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
            float effectiveStrength = EffectiveBrushStrength;
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
            };
            job.Schedule(candidates.Count, 32).Complete();

            ScatterJobResults(candidates, verts);
        }

        private void ApplyInflateBrushLocalManaged(Vector3 localPoint, Vector3 localNormal, bool positive, List<int> candidates, Vector3[] verts, Vector3[] normals)
        {
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float effectiveStrength = EffectiveBrushStrength;
            float effectiveStrengthAccumulate = EffectiveBrushStrengthAccumulate;
            Vector3 target = localPoint + localNormal * (brushRadius * InflateOffCapFactor * sign);

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(verts[i], localPoint);
                if (dist > brushRadius) continue;

                float t01 = 1f - dist / brushRadius;
                float weight = t01 * t01 * (3f - 2f * t01) * (1f - sculptableMesh.Mask[i]); // smoothstep, masked-out
                if (weight <= 0f) continue;

                sculptableMesh.RecordUndoBeforeIfNeeded(i);

                if (accumulate)
                {
                    verts[i] += normals[i] * (weight * sign * effectiveStrengthAccumulate * InflateSpeed * dt);
                }
                else
                {
                    Vector3 toTarget = target - verts[i];
                    verts[i] += toTarget * Mathf.Clamp01(weight * effectiveStrength * InflateSpeed * dt); // see Clamp01 note on Clay
                }

                _dirtyVertexScratch.Add(i);
            }
        }

        private void HandleSmoothInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;
            _previewPositive = true; // Smooth has no add/subtract direction - always neutral/green

            if (logRayHits && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                Debug.Log($"[Sculpt] Ray hit at {hitPoint}, normal {hitNormal}, distance {Vector3.Distance(ray.origin, hitPoint):F2}");

            // Same Alt-reserved-for-orbit rule as Clay; either mouse button smooths since
            // there's no positive/negative to invert.
            if ((mouse.leftButton.isPressed && !altHeld) || mouse.rightButton.isPressed)
                ApplySmoothBrush(hitPoint);
        }

        private void ApplySmoothBrush(Vector3 worldPoint)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
                ApplySmoothBrushLocal(Vector3.Scale(localPoint, sign));

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
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
            NativeArray<Vector3>.Copy(verts, _nativeFullPositionMirror, totalVerts); // full-mesh mirror, refreshed once per call - see field remarks

            GatherCandidatesNative(candidates, verts, sculptableMesh.Normals, sculptableMesh.Mask); // normals unused by these jobs

            if (!_nativeSmoothCandidates.IsCreated || _nativeSmoothCandidates.Length < candidates.Count)
            {
                if (_nativeSmoothCandidates.IsCreated) _nativeSmoothCandidates.Dispose();
                _nativeSmoothCandidates = new NativeArray<int>(Mathf.Max(Mathf.NextPowerOfTwo(candidates.Count), MinJobVertexCount), Allocator.Persistent);
            }
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int globalIdx = candidates[ci];
                _nativeSmoothCandidates[ci] = globalIdx;
                _nativeVertexToSlot[globalIdx] = ci;
            }

            var weightJob = new SmoothWeightJob
            {
                PositionsIn = _nativePositionsIn,
                MaskIn = _nativeMaskIn,
                WeightsOut = _nativeClayWeights,
                LocalPoint = localPoint,
                BrushRadius = brushRadius,
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

            for (int pass = 0; pass < fullIterations; pass++)
            {
                RunSmoothRelaxJob(candidates.Count, readBuf, writeBuf, adjOffsets, adjNeighbors, 1f, lerpFactorScale);
                (readBuf, writeBuf) = (writeBuf, readBuf);
            }
            if (partialFactor > 0.001f)
            {
                RunSmoothRelaxJob(candidates.Count, readBuf, writeBuf, adjOffsets, adjNeighbors, partialFactor, lerpFactorScale);
                (readBuf, writeBuf) = (writeBuf, readBuf);
            }

            // Scatter the final (post-swap, so it's in readBuf) result back - mirrors the
            // managed method's own dirty rule (weight > 0), constant across every pass. The
            // vertexToSlot reset always has to happen (so a later call at a different footprint
            // never reads a stale slot), but writing verts[]/marking dirty only makes sense if a
            // pass actually ran - matches the managed method's own no-op-when-iterAmount-too-
            // small edge case (theoretical given BrushStrength's enforced 0.01 minimum, kept
            // correct anyway rather than assuming the UI clamp is the only caller).
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int globalIdx = candidates[ci];
                _nativeVertexToSlot[globalIdx] = -1; // targeted reset, not a full-array clear - see field remarks
                if (!anyPassRan || _nativeClayWeights[ci] <= 0f) continue;
                sculptableMesh.RecordUndoBeforeIfNeeded(globalIdx);
                verts[globalIdx] = readBuf[ci];
                _dirtyVertexScratch.Add(globalIdx);
            }
        }

        private void RunSmoothRelaxJob(int candidateCount, NativeArray<Vector3> readBuf, NativeArray<Vector3> writeBuf,
            NativeArray<int> adjOffsets, NativeArray<int> adjNeighbors, float passFactor, float lerpFactorScale)
        {
            var job = new SmoothRelaxJob
            {
                Candidates = _nativeSmoothCandidates,
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
            job.Schedule(candidateCount, 32).Complete();
        }

        private void ApplySmoothBrushLocalManaged(Vector3 localPoint, List<int> candidates, Vector3[] verts)
        {
            if (_smoothWeightScratch.Length < candidates.Count) _smoothWeightScratch = new float[candidates.Count];
            float[] weights = _smoothWeightScratch;
            bool anyInRange = false;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(verts[i], localPoint);
                if (dist > brushRadius) { weights[ci] = 0f; continue; }

                float t01 = 1f - dist / brushRadius;
                weights[ci] = t01 * t01 * (3f - 2f * t01) * (1f - sculptableMesh.Mask[i]); // smoothstep, masked-out
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
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                float w = weights[ci];
                if (w <= 0f) continue;
                int i = candidates[ci];

                Vector3 toAverage = sculptableMesh.GetNeighborAverage(i) - verts[i];
                sculptableMesh.RecordUndoBeforeIfNeeded(i);
                verts[i] += toAverage * Mathf.Clamp01(w * passFactor * SmoothSpeed * dt); // see Clamp01 note on Clay
                _dirtyVertexScratch.Add(i);
            }
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
                    EndMoveDrag();
                    return;
                }

                Ray dragRay = cam.ScreenPointToRay(mouse.position.ReadValue());
                if (RayPlaneIntersect(dragRay, _dragPlanePoint, _dragPlaneNormal, out Vector3 current))
                {
                    Vector3 worldDelta = current - _lastDragPoint;
                    if (worldDelta.sqrMagnitude > 1e-12f)
                    {
                        Vector3 localDelta = sculptableMesh.transform.InverseTransformVector(worldDelta);
                        _dirtyVertexScratch.Clear();
                        foreach (var (selection, sign) in _grabSelections)
                        {
                            sculptableMesh.ApplyGrabDelta(selection, Vector3.Scale(localDelta, sign));
                            foreach (int i in selection.Indices) _dirtyVertexScratch.Add(i);
                        }
                        sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
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
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                var selection = sculptableMesh.SelectGrab(Vector3.Scale(localHit, sign), brushRadius);
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

        private static bool RayPlaneIntersect(Ray ray, Vector3 planePoint, Vector3 planeNormal, out Vector3 point)
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
            EndMoveDrag();
            sculptableMesh.SnapshotForUndo();
            sculptableMesh.ResetMesh();
        }

        public void Remesh()
        {
            sculptableMesh.SnapshotForUndo();
            sculptableMesh.Remesh(remeshResolution);
        }

        // Fixed destination rather than a save-file dialog - EditorUtility.SaveFilePanel only
        // exists in the Editor and would silently vanish once this ships as a standalone
        // build, whereas Environment.GetFolderPath is plain .NET and resolves the real
        // Desktop path in both. A proper save/load feature (with its own file-picker UX) is
        // planned as separate future work; this is just "get the current sculpt out to a
        // file I can open elsewhere" for now.
        public string Export()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string folder = Path.Combine(desktop, "SculptExports");
            string path = ObjExporter.Export(sculptableMesh, folder);
            if (path != null) Debug.Log($"[Sculpt] Exported to {path}");
            return path;
        }

        // Kept visible at all times (not just while hovering the mesh) so its size always
        // gives a visual read on the current brush radius - most useful while resizing (S)
        // off to the side of the model. Snaps to the sculpted surface when actually hovering
        // it; otherwise floats along the camera ray at the model's rough depth.
        private void UpdateBrushPreview()
        {
            if (brushPreview == null || cam == null) return;

            Vector3 previewPos;
            bool positive;

            if (_isHovering)
            {
                previewPos = _hoverPoint + _hoverNormal * 0.01f;
                positive = _previewPositive;
                _lastGoodPreviewPos = previewPos;
            }
            else if (_isOverUI)
            {
                // Mouse is over a panel (e.g. dragging the brush radius slider), not the
                // viewport - a fresh ray from there would send the preview flying off toward
                // the panel. Freeze at the last on-model/viewport position instead so its size
                // still reads clearly against the sculpt while the slider is being scrubbed.
                previewPos = _lastGoodPreviewPos;
                positive = true;
            }
            else
            {
                Mouse mouse = Mouse.current;
                if (mouse == null) { brushPreview.SetActive(false); return; }

                float fallbackDistance = Mathf.Max(1f, Vector3.Distance(cam.transform.position, sculptableMesh.transform.position));
                Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
                previewPos = ray.GetPoint(fallbackDistance);
                positive = true; // neutral tint when just showing size, not actively sculpting
                _lastGoodPreviewPos = previewPos;
            }

            brushPreview.SetActive(true);
            float diameter = brushRadius * 2f * AverageScale();
            brushPreview.transform.position = previewPos;
            brushPreview.transform.localScale = Vector3.one * diameter;

            if (_brushPreviewRenderer != null)
            {
                Color c = positive ? PositiveColor : NegativeColor;
                c.a = 0.35f;
                _brushPreviewRenderer.material.color = c;
            }
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
    }
}
