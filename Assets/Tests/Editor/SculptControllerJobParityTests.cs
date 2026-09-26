using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Sculpting.Tests
{
    /// Every brush keeps a Burst job path and a plain managed path, and production picks between
    /// them per dab by footprint size (useBurstJobs && candidates >= MinJobVertexCount). Only one of
    /// the two runs on any given dab, so nothing in normal use notices when they drift apart. These
    /// tests run both paths on identical inputs - same mesh, settings, candidate lists, frame time
    /// and dab sequence - and require the same vertex positions, and the same dirty set for the
    /// first dab.
    ///
    /// Lives in Assembly-CSharp-Editor with no asmdef: an asmdef cannot reference Assembly-CSharp,
    /// and this project's editor assembly already references NUnit and the Test Runner. That
    /// assembly cannot see Assembly-CSharp's private or internal members, so they are reached by
    /// reflection; a renamed or re-signatured member fails the test with a message naming it.
    ///
    /// Smooth and Surface Relax are deliberately NOT identical: the jobs relax Jacobi-style (every
    /// candidate reads last pass's positions) and the managed loops Gauss-Seidel-style (in place).
    /// Each gets two tests instead: exact parity on an independent candidate set, where no candidate
    /// neighbours another and the two schemes coincide, and a proven bound on the full footprint.
    ///
    /// Run at two densities - ~327k and ~1.3M triangles - because low-poly results do not carry
    /// over to the meshes this app is actually used on.
    [TestFixture(7)]
    [TestFixture(8)]
    public class SculptControllerJobParityTests
    {
        private const float SphereRadius = 0.5f;
        // The frame time handed to the brushes that are time-paced. Fixed rather than read from
        // Time.deltaTime, whose value outside Play mode says nothing about real sculpting.
        private const float FrameDt = 1f / 60f;
        // Burst and Mono round single-precision math differently (fused multiply-add, operand
        // order). Measured parity has been ~1e-7 per dab; six overlapping dabs get the larger
        // allowance, the one-to-three-pass Laplacian tests the tighter one.
        private const float DabSequenceTolerance = 1e-5f;
        private const float LaplacianTolerance = 2e-6f;
        // A comparison is only meaningful if the brush moved the mesh by well over the tolerance.
        private const float MeaningfulDisplacementFactor = 10f;
        private const BindingFlags AnyMember =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // Front Facing Only viewpoint, in mesh-local space. Placed so the long dab path runs from
        // back-facing through the silhouette band to front-facing, exercising the ramp.
        private static readonly Vector3 CameraLocal = new Vector3(2.5f, 0.3f, -1f);

        private delegate void InflateOrFlattenPath(Vector3 point, Vector3 normal, bool positive, float dt,
            List<int> candidates, Vector3[] verts, Vector3[] normals);
        private delegate void CarvePath(Vector3 point, Vector3 normal, Vector3 dir, bool positive,
            List<int> candidates, Vector3[] verts);
        private delegate void ClayPath(Vector3 point, Vector3 normal, Vector3 tangent0, Vector3 bitangent0,
            bool positive, float dt, List<int> candidates, Vector3[] verts, Vector3[] normals,
            float effectiveRadius, float effectiveEdgeSoftness, float pressure);
        private delegate void SmoothPath(Vector3 point, float dt, List<int> candidates, Vector3[] verts);
        private delegate void RelaxPath(float relaxRadius, List<int> candidates, float passAmount);

        private readonly int _subdivisions;
        private GameObject _meshObject, _controllerObject, _cameraObject;
        private Mesh _sourceMesh;
        private SculptableMesh _sculptable;
        private SculptController _controller;
        private Vector3[] _startVertices;
        private int[] _neighborStart, _neighborCount, _neighbors;

        public SculptControllerJobParityTests(int subdivisions) => _subdivisions = subdivisions;

        private Vector3[] Verts => _sculptable.Vertices;
        private Vector3[] Normals => _sculptable.Normals;

        // ------------------------------------------------------------------------ fixture

        [OneTimeSetUp]
        public void CreateObjects()
        {
            _sourceMesh = BuildNoisyIcosphere(_subdivisions);

            _meshObject = new GameObject("JobParityMesh") { hideFlags = HideFlags.HideAndDontSave };
            _meshObject.AddComponent<MeshFilter>().sharedMesh = _sourceMesh;
            _sculptable = _meshObject.AddComponent<SculptableMesh>();
            SetField(_sculptable, "useMeshCollider", false); // no PhysX cook of a million-triangle collider
            // Edit mode does not run Awake for components added from script.
            if (_sculptable.Vertices == null) Invoke(_sculptable, "Awake");

            _cameraObject = new GameObject("JobParityCamera") { hideFlags = HideFlags.HideAndDontSave };
            Camera camera = _cameraObject.AddComponent<Camera>();
            camera.enabled = false;

            _controllerObject = new GameObject("JobParityController") { hideFlags = HideFlags.HideAndDontSave };
            _controller = _controllerObject.AddComponent<SculptController>();
            SetField(_controller, "sculptableMesh", _sculptable);
            SetField(_controller, "cam", camera); // the relax job reads it

            _startVertices = (Vector3[])Verts.Clone();
            _neighborStart = _sculptable.AdjacencyStarts.ToArray();
            _neighborCount = _sculptable.AdjacencyCounts.ToArray();
            _neighbors = _sculptable.AdjacencyNeighbors.ToArray();
        }

        [OneTimeTearDown]
        public void DestroyObjects()
        {
            // Edit mode does not run OnDestroy either, so native and GPU memory is released by hand.
            if (_controller != null) Invoke(_controller, "ReleaseNativeResources");
            if (_sculptable != null)
            {
                Invoke(_sculptable, "ReleaseNativeResources");
                if (_sculptable.Mesh != null && _sculptable.Mesh != _sourceMesh) Object.DestroyImmediate(_sculptable.Mesh);
            }
            if (_controllerObject != null) Object.DestroyImmediate(_controllerObject);
            if (_cameraObject != null) Object.DestroyImmediate(_cameraObject);
            if (_meshObject != null) Object.DestroyImmediate(_meshObject);
            if (_sourceMesh != null) Object.DestroyImmediate(_sourceMesh);
        }

        [SetUp]
        public void ResetState()
        {
            Array.Clear(_sculptable.Mask, 0, _sculptable.Mask.Length);
            RestoreGeometry();
            _controller.AccumulateStrength = 1f;
            _controller.BuildUpOnHold = false;
            SetField(_controller, "_dabCameraLocal", CameraLocal);
        }

        // ------------------------------------------------------------------------ brushes

        [Test]
        public void Inflate([Values(true, false)] bool accumulate, [Values(true, false)] bool positive,
            [Values(false, true)] bool maskAndFrontFacing)
        {
            Configure(radius: 0.2f, strength: 0.6f, maskAndFrontFacing);
            _controller.Accumulate = accumulate;

            var job = Bind<InflateOrFlattenPath>("ApplyInflateBrushLocalJob");
            var managed = Bind<InflateOrFlattenPath>("ApplyInflateBrushLocalManaged");
            List<Dab> dabs = BuildDabs(LongPath, _controller.BrushRadius, withDirection: false);

            AssertParity("Inflate", DabSequenceTolerance,
                Run(dabs, d => managed(d.Point, d.Normal, positive, FrameDt, d.Candidates, Verts, Normals)),
                Run(dabs, d => job(d.Point, d.Normal, positive, FrameDt, d.Candidates, Verts, Normals)));
        }

        /// A custom falloff curve reaches the Burst jobs through a SharedStatic (see BrushFalloff),
        /// which a job can silently fail to see - so the job and managed paths are compared with
        /// one installed, and the job is checked to actually respond to it.
        [Test]
        public void CustomFalloffCurve([Values("Inflate", "Crease", "Clay", "Standard")] string brush)
        {
            Sequence builtIn = RunFalloffCase(brush, jobs: true);
            BrushFalloff.SetActive(BrushFalloffCurve.Preset(FalloffPreset.Plateau));
            try
            {
                switch (brush)
                {
                    case "Inflate": Inflate(accumulate: true, positive: true, maskAndFrontFacing: false); break;
                    case "Crease": Crease(accumulate: false, positive: false, maskAndFrontFacing: false); break;
                    case "Clay": Clay(1f, accumulate: true, positive: true, maskAndFrontFacing: false); break;
                    default: StandardAndLayer("ApplyStandardBrushLocal", accumulate: true, positive: true, maskAndFrontFacing: false); break;
                }
                Sequence curved = RunFalloffCase(brush, jobs: true);
                float change = MaxDelta(builtIn.Vertices, curved.Vertices, out _);
                Assert.That(change, Is.GreaterThan(DabSequenceTolerance * MeaningfulDisplacementFactor),
                    $"{brush}: the job ignored the custom falloff curve.");
            }
            finally
            {
                BrushFalloff.SetActive(null);
            }
        }

        /// Focal Shift rides the same shared table, on top of the built-in falloff: job and
        /// managed must still agree (the parity assertions inside each case), and the job must
        /// actually see the shift.
        [Test]
        public void FocalShift([Values("Inflate", "Crease", "Clay", "Standard")] string brush,
            [Values(-0.7f, 0.7f)] float focal)
        {
            Sequence unshifted = RunFalloffCase(brush, jobs: true);
            BrushFalloff.SetActive(null, focal);
            try
            {
                switch (brush)
                {
                    case "Inflate": Inflate(accumulate: true, positive: true, maskAndFrontFacing: false); break;
                    case "Crease": Crease(accumulate: false, positive: false, maskAndFrontFacing: false); break;
                    case "Clay": Clay(1f, accumulate: true, positive: true, maskAndFrontFacing: false); break;
                    default: StandardAndLayer("ApplyStandardBrushLocal", accumulate: true, positive: true, maskAndFrontFacing: false); break;
                }
                Sequence shifted = RunFalloffCase(brush, jobs: true);
                float change = MaxDelta(unshifted.Vertices, shifted.Vertices, out _);
                Assert.That(change, Is.GreaterThan(DabSequenceTolerance * MeaningfulDisplacementFactor),
                    $"{brush}: the job ignored the focal shift.");
            }
            finally
            {
                BrushFalloff.SetActive(null);
            }
        }

        private Sequence RunFalloffCase(string brush, bool jobs)
        {
            Configure(radius: 0.2f, strength: 0.5f, maskAndFrontFacing: false);
            _controller.Accumulate = true;
            bool jobsBefore = _controller.UseBurstJobs;
            _controller.UseBurstJobs = jobs;
            try
            {
                string method = brush == "Crease" ? null : "Apply" + brush + "BrushLocal";
                List<Dab> dabs = BuildDabs(LongPath, _controller.BrushRadius, withDirection: brush == "Crease");
                if (brush == "Crease")
                {
                    var carve = Bind<CarvePath>("ApplyCarveDabLocalJob");
                    return Run(dabs, d => carve(d.Point, d.Normal, d.Direction, false, d.Candidates, Verts));
                }
                if (brush == "Clay")
                {
                    var clay = Bind<ClayPath>("ApplyClayBrushLocalJob");
                    BuildTangentBasis(dabs[0].Normal, out Vector3 t0, out Vector3 b0);
                    float radius = _controller.BrushRadius, soft = _controller.ClayEdgeSoftness;
                    return Run(dabs, d => clay(d.Point, d.Normal, t0, b0, true, 0.05f, d.Candidates, Verts, Normals, radius, soft, 1f));
                }
                var apply = Bind<Action<Vector3, Vector3, bool, float>>(method);
                return Run(dabs, d => apply(d.Point, d.Normal, true, 0.1f));
            }
            finally
            {
                _controller.UseBurstJobs = jobsBefore;
            }
        }

        /// Standard and Layer choose their path inside one entry point, so the two runs flip
        /// useBurstJobs instead of calling the paths directly. dt is the dab quantum production uses.
        [Test]
        public void StandardAndLayer([Values("ApplyStandardBrushLocal", "ApplyLayerBrushLocal")] string method,
            [Values(true, false)] bool accumulate, [Values(true, false)] bool positive,
            [Values(false, true)] bool maskAndFrontFacing)
        {
            Configure(radius: 0.2f, strength: 0.6f, maskAndFrontFacing);
            _controller.Accumulate = accumulate;
            _controller.LayerHeight = 0.15f;
            var apply = Bind<Action<Vector3, Vector3, bool, float>>(method);
            List<Dab> dabs = BuildDabs(LongPath, _controller.BrushRadius, withDirection: false);
            const float dabDt = 0.1f; // DabTimeQuantum

            bool jobsBefore = _controller.UseBurstJobs;
            try
            {
                _controller.UseBurstJobs = false;
                Sequence managed = Run(dabs, d => apply(d.Point, d.Normal, positive, dabDt));
                _controller.UseBurstJobs = true;
                Sequence job = Run(dabs, d => apply(d.Point, d.Normal, positive, dabDt));
                AssertParity(method, DabSequenceTolerance, managed, job);
            }
            finally
            {
                _controller.UseBurstJobs = jobsBefore;
            }
        }

        [Test]
        public void Crease([Values(false, true)] bool accumulate,
            [Values(false, true)] bool positive, [Values(false, true)] bool maskAndFrontFacing)
        {
            Configure(radius: 0.2f, strength: 0.5f, maskAndFrontFacing);
            _controller.Accumulate = accumulate;
            _controller.CreaseDepthFactor = 0.35f;
            _controller.CreasePinch = 0.6f;

            var job = Bind<CarvePath>("ApplyCarveDabLocalJob");
            var managed = Bind<CarvePath>("ApplyCarveDabLocalManaged");
            List<Dab> dabs = BuildDabs(LongPath, _controller.BrushRadius, withDirection: true);

            AssertParity("Crease", DabSequenceTolerance,
                Run(dabs, d => managed(d.Point, d.Normal, d.Direction, positive, d.Candidates, Verts)),
                Run(dabs, d => job(d.Point, d.Normal, d.Direction, positive, d.Candidates, Verts)));
        }

        [Test]
        public void Clay([Values(1f, 0.35f)] float tipRoundness, [Values(true, false)] bool accumulate,
            [Values(true, false)] bool positive, [Values(false, true)] bool maskAndFrontFacing)
        {
            Configure(radius: 0.2f, strength: 0.5f, maskAndFrontFacing);
            _controller.Accumulate = accumulate;
            _controller.ClayTipRoundness = tipRoundness;
            _controller.UseAlpha = false;
            RunClay(positive);
        }

        // HardSquare is not run inverted: the stamp is fully opaque out to 90% of its half-width, and at
        // this scale a round tip never reaches past that band - so inverted, it deposits nothing on
        // either path and the comparison would be vacuous.
        [TestCase(BrushAlphaType.SoftCircle, false)]
        [TestCase(BrushAlphaType.SoftCircle, true)]
        [TestCase(BrushAlphaType.Ridges, false)]
        [TestCase(BrushAlphaType.Ridges, true)]
        [TestCase(BrushAlphaType.Noise, true)]
        [TestCase(BrushAlphaType.Bumps, false)]
        [TestCase(BrushAlphaType.HardSquare, false)]
        public void ClayWithAlpha(BrushAlphaType alpha, bool invertAlpha)
        {
            Configure(radius: 0.2f, strength: 0.5f, maskAndFrontFacing: false);
            _controller.Accumulate = true;
            _controller.ClayTipRoundness = 1f;
            _controller.UseAlpha = true;
            _controller.AlphaType = alpha;
            _controller.InvertAlpha = invertAlpha;
            _controller.AlphaRotation = 30f;
            _controller.AlphaScale = 1.2f;
            RunClay(positive: true);
        }

        private void RunClay(bool positive)
        {
            _controller.ClayHeightFactor = 0.3f;
            _controller.ClayEdgeSoftness = 0.45f;
            float radius = _controller.BrushRadius;
            float queryRadius = _controller.ClayTipRoundness < 1f ? radius * Mathf.Sqrt(2f) : radius;

            var job = Bind<ClayPath>("ApplyClayBrushLocalJob");
            var managed = Bind<ClayPath>("ApplyClayBrushLocalManaged");
            List<Dab> dabs = BuildDabs(LongPath, queryRadius, withDirection: false);
            // Production freezes the square tip's frame at the first dab of a stroke.
            BuildTangentBasis(dabs[0].Normal, out Vector3 tangent0, out Vector3 bitangent0);
            const float dabDt = 0.05f; // Clay's dab quantum - see ClayDabTimeQuantum
            float softness = _controller.ClayEdgeSoftness;

            AssertParity("Clay", DabSequenceTolerance,
                Run(dabs, d => managed(d.Point, d.Normal, tangent0, bitangent0, positive, dabDt, d.Candidates, Verts, Normals, radius, softness, 1f)),
                Run(dabs, d => job(d.Point, d.Normal, tangent0, bitangent0, positive, dabDt, d.Candidates, Verts, Normals, radius, softness, 1f)));
        }

        /// Pen pressure changing from dab to dab (see StrokePath): each dab's radius, edge softness and
        /// strength come from its own pressure, on both paths.
        [Test]
        public void ClayWithPerDabPressure([Values(true, false)] bool accumulate)
        {
            Configure(radius: 0.2f, strength: 0.5f, maskAndFrontFacing: false);
            _controller.Accumulate = accumulate;
            _controller.ClayTipRoundness = 1f;
            _controller.UseAlpha = false;
            _controller.ClayHeightFactor = 0.3f;
            _controller.ClayEdgeSoftness = 0.45f;
            _controller.ClayPressureRadiusInfluence = 0.5f;
            _controller.ClayPressureSoftnessInfluence = 0.5f;

            var job = Bind<ClayPath>("ApplyClayBrushLocalJob");
            var managed = Bind<ClayPath>("ApplyClayBrushLocalManaged");
            var radiusAt = Bind<Func<float, float>>("EffectiveClayRadiusAt");
            var softnessAt = Bind<Func<float, float>>("EffectiveClayEdgeSoftnessAt");
            List<Dab> dabs = BuildDabs(LongPath, _controller.BrushRadius, withDirection: false);
            BuildTangentBasis(dabs[0].Normal, out Vector3 tangent0, out Vector3 bitangent0);
            const float dabDt = 0.05f;

            int index = 0;
            Action<Dab> Apply(ClayPath path) => d =>
            {
                float p = Mathf.Lerp(0.2f, 1f, index++ / (float)(dabs.Count - 1));
                path(d.Point, d.Normal, tangent0, bitangent0, true, dabDt, d.Candidates, Verts, Normals, radiusAt(p), softnessAt(p), p);
            };

            Sequence managedRun = Run(dabs, Apply(managed));
            index = 0;
            Sequence jobRun = Run(dabs, Apply(job));
            AssertParity("Clay per-dab pressure", DabSequenceTolerance, managedRun, jobRun);
        }

        /// The job path computes Flatten's weights with ClayWeightJob at EdgeSoftness 1, which goes
        /// through ClayFalloff - so when ClayFalloff went from a cubic smoothstep to a quintic
        /// smootherstep for Clay's sake, Flatten's job path changed with it while the managed path kept
        /// the cubic. This test is what caught it: every case diverged by ~9% of a dab's displacement
        /// until the managed path was moved onto ClayFalloff as well.
        [Test]
        public void Flatten([Values(true, false)] bool positive, [Values(0f, 0.3f, -0.2f)] float planeOffset,
            [Values(false, true)] bool maskAndFrontFacing)
        {
            Configure(radius: 0.2f, strength: 0.6f, maskAndFrontFacing);
            _controller.FlattenPlaneOffset = planeOffset;

            var job = Bind<InflateOrFlattenPath>("ApplyFlattenBrushLocalJob");
            var managed = Bind<InflateOrFlattenPath>("ApplyFlattenBrushLocalManaged");
            List<Dab> dabs = BuildDabs(LongPath, _controller.BrushRadius, withDirection: false);

            AssertParity(positive ? "Flatten" : "Flatten contrast", DabSequenceTolerance,
                Run(dabs, d => managed(d.Point, d.Normal, positive, FrameDt, d.Candidates, Verts, Normals)),
                Run(dabs, d => job(d.Point, d.Normal, positive, FrameDt, d.Candidates, Verts, Normals)));
        }

        // 0.35 folds three full passes and a partial one into the application; 1 folds the full ten.
        [Test]
        public void SmoothOnIndependentCandidates([Values(0.35f, 1f)] float strength, [Values(false, true)] bool maskAndFrontFacing)
        {
            Configure(radius: 0.2f, strength: strength, maskAndFrontFacing);
            var job = Bind<SmoothPath>("ApplySmoothBrushLocalJob");
            var managed = Bind<SmoothPath>("ApplySmoothBrushLocalManaged");
            List<Dab> dabs = BuildDabs(ShortPath, _controller.BrushRadius, withDirection: false, independentCandidates: true);

            AssertParity("Smooth (independent set)", LaplacianTolerance,
                Run(dabs, d => managed(d.Point, FrameDt, d.Candidates, Verts)),
                Run(dabs, d => job(d.Point, FrameDt, d.Candidates, Verts)));
        }

        /// One pass over the full footprint. Processing candidates in order, Gauss-Seidel's neighbour
        /// average for a vertex differs from Jacobi's only through neighbours already updated this
        /// pass, each by at most the largest displacement D - so the two results differ by at most
        /// lambda * D, lambda being the pass's largest lerp factor. A sign, weight or pass-count error
        /// in either path breaks that bound.
        [Test]
        public void SmoothStaysWithinGaussSeidelBound([Values(false, true)] bool maskAndFrontFacing)
        {
            // Strength 0.1 folds exactly one full pass into the application (MaxSmoothIterations 10),
            // and a slow frame's dt lets that single pass move the mesh well clear of float noise.
            const float slowFrameDt = 1f / 20f;
            Configure(radius: 0.2f, strength: 0.1f, maskAndFrontFacing);
            float lambda = Mathf.Clamp01((float)Constant("SmoothSpeed") * slowFrameDt);

            var job = Bind<SmoothPath>("ApplySmoothBrushLocalJob");
            var managed = Bind<SmoothPath>("ApplySmoothBrushLocalManaged");
            List<Dab> dabs = BuildDabs(ShortPath.First, _controller.BrushRadius, withDirection: false);

            AssertBounded("Smooth", lambda,
                Run(dabs, d => managed(d.Point, slowFrameDt, d.Candidates, Verts)),
                Run(dabs, d => job(d.Point, slowFrameDt, d.Candidates, Verts)));
        }

        [Test]
        public void SurfaceRelaxOnIndependentCandidates([Values(0.6f, 2.4f)] float passAmount, [Values(false, true)] bool maskAndFrontFacing)
        {
            Configure(radius: 0.12f, strength: 0.5f, maskAndFrontFacing);
            RunRelax(passAmount, independentCandidates: true, bounded: false);
        }

        [Test]
        public void SurfaceRelaxStaysWithinGaussSeidelBound([Values(false, true)] bool maskAndFrontFacing)
        {
            Configure(radius: 0.12f, strength: 0.5f, maskAndFrontFacing);
            RunRelax(passAmount: 0.5f, independentCandidates: false, bounded: true); // one partial pass
        }

        private void RunRelax(float passAmount, bool independentCandidates, bool bounded)
        {
            float relaxRadius = _controller.BrushRadius * 1f; // RelaxRadiusFactor
            List<Dab> centres = BuildDabs(ShortPath, relaxRadius, withDirection: false);

            var candidates = new List<int>();
            var seen = new HashSet<int>();
            foreach (Dab centre in centres)
                foreach (int i in centre.Candidates)
                    if (seen.Add(i)) candidates.Add(i);
            if (independentCandidates) candidates = IndependentSubset(candidates);

            var centreList = (List<Vector3>)GetField(_controller, "_relaxCentres");
            var cameraList = (List<Vector3>)GetField(_controller, "_relaxCentreCameras");
            centreList.Clear();
            cameraList.Clear();
            for (int c = 0; c < centres.Count; c++)
            {
                centreList.Add(centres[c].Point);
                // A different viewpoint per centre, as mirrored dabs have in production.
                cameraList.Add(c % 2 == 0 ? CameraLocal : Vector3.Scale(CameraLocal, new Vector3(-1f, 1f, 1f)));
            }

            var job = Bind<RelaxPath>("ApplySurfaceRelaxLocalJob");
            var managed = Bind<RelaxPath>("ApplySurfaceRelaxLocalManaged");
            var single = new List<Dab> { new Dab(centres[0].Point, centres[0].Normal, Vector3.zero, candidates) };
            Sequence managedResult = Run(single, d => managed(relaxRadius, d.Candidates, passAmount));
            Sequence jobResult = Run(single, d => job(relaxRadius, d.Candidates, passAmount));

            if (bounded) AssertBounded("Surface Relax", Mathf.Clamp01(passAmount), managedResult, jobResult);
            else AssertParity("Surface Relax (independent set)", LaplacianTolerance, managedResult, jobResult);
        }

        // ------------------------------------------------------------------------ harness

        /// Six overlapping dabs sweeping from the masked, back-facing side into the unmasked,
        /// front-facing side; and three starting just past the mask ramp for the Laplacian tests.
        private static readonly DabPath LongPath = new DabPath(-40f, 15f, 6);
        private static readonly DabPath ShortPath = new DabPath(5f, 15f, 3);

        private readonly struct DabPath
        {
            public readonly float StartDegrees, StepDegrees;
            public readonly int Count;

            public DabPath(float startDegrees, float stepDegrees, int count)
            {
                StartDegrees = startDegrees;
                StepDegrees = stepDegrees;
                Count = count;
            }

            public DabPath First => new DabPath(StartDegrees, StepDegrees, 1);
        }

        private readonly struct Dab
        {
            public readonly Vector3 Point, Normal, Direction;
            public readonly List<int> Candidates;

            public Dab(Vector3 point, Vector3 normal, Vector3 direction, List<int> candidates)
            {
                Point = point;
                Normal = normal;
                Direction = direction;
                Candidates = candidates;
            }
        }

        private readonly struct Sequence
        {
            public readonly Vector3[] Vertices;
            public readonly List<int> FirstDabDirty;

            public Sequence(Vector3[] vertices, List<int> firstDabDirty)
            {
                Vertices = vertices;
                FirstDabDirty = firstDabDirty;
            }
        }

        private void Configure(float radius, float strength, bool maskAndFrontFacing)
        {
            _controller.BrushRadius = radius;
            _controller.BrushStrength = strength;
            _controller.FrontFacingOnly = maskAndFrontFacing;
            if (maskAndFrontFacing) PaintMaskBand();
        }

        // 1 below x = -0.05 (the side facing away from CameraLocal), 0 above x = 0.05, a ramp
        // between - the dab paths cross it, so footprints see masked, partial and unmasked vertices.
        private void PaintMaskBand()
        {
            float[] mask = _sculptable.Mask;
            for (int i = 0; i < mask.Length; i++) mask[i] = Mathf.Clamp01((0.05f - _startVertices[i].x) / 0.1f);
        }

        /// Dabs along an arc of the sphere. Candidate lists are gathered once, from the untouched
        /// mesh, and shared by both paths - so both see exactly the same footprints however the
        /// geometry moves.
        private List<Dab> BuildDabs(DabPath path, float queryRadius, bool withDirection, bool independentCandidates = false)
        {
            RestoreGeometry();
            int minJobVertexCount = (int)Constant("MinJobVertexCount");
            var dabs = new List<Dab>(path.Count);
            for (int k = 0; k < path.Count; k++)
            {
                float angle = (path.StartDegrees + path.StepDegrees * k) * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Sin(angle), 0.15f, -Mathf.Cos(angle)).normalized;
                Vector3 point = dir * SurfaceRadius(dir);
                // Stroke travel direction in the tangent plane; zero on the first dab, as in production.
                Vector3 travel = withDirection && k > 0
                    ? Vector3.ProjectOnPlane(new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)), dir).normalized
                    : Vector3.zero;

                var candidates = new List<int>(_sculptable.QueryNear(point, queryRadius));
                Assert.That(candidates.Count, Is.GreaterThanOrEqualTo(minJobVertexCount),
                    "Footprint too small for production to choose the job path - the test would not represent it.");
                if (independentCandidates) candidates = IndependentSubset(candidates);
                dabs.Add(new Dab(point, dir, travel, candidates));
            }
            return dabs;
        }

        private Sequence Run(List<Dab> dabs, Action<Dab> applyDab)
        {
            RestoreGeometry();
            List<int> firstDabDirty = null;
            foreach (Dab dab in dabs)
            {
                Invoke(_controller, "BeginDirtyVertices");
                applyDab(dab);
                if (firstDabDirty == null) firstDabDirty = new List<int>(DirtyItems());
            }
            return new Sequence((Vector3[])Verts.Clone(), firstDabDirty);
        }

        private void RestoreGeometry()
        {
            Array.Copy(_startVertices, Verts, _startVertices.Length);
            _sculptable.BeginStrokeUndo(); // stroke-start positions go back to the untouched mesh
            // Production marks this once per frame; the restore above moved every vertex behind its back.
            Invoke(_controller, "MarkPositionMirrorStale");
        }

        private void AssertParity(string label, float tolerance, Sequence managed, Sequence job)
        {
            float displacement = MaxDelta(_startVertices, managed.Vertices, out _);
            Assert.That(displacement, Is.GreaterThan(tolerance * MeaningfulDisplacementFactor),
                $"{label}: the managed path barely moved the mesh ({displacement:E3}), so agreement would prove nothing.");

            float divergence = MaxDelta(managed.Vertices, job.Vertices, out int worst);
            Assert.That(divergence, Is.LessThanOrEqualTo(tolerance),
                $"{label}: job and managed paths diverge by {divergence:E3} at vertex {worst} " +
                $"(managed {managed.Vertices[worst]:F6}, job {job.Vertices[worst]:F6}; largest displacement {displacement:E3}).");

            CollectionAssert.AreEqual(managed.FirstDabDirty, job.FirstDabDirty,
                $"{label}: the first dab marked a different set of vertices dirty.");
        }

        private void AssertBounded(string label, float lambda, Sequence managed, Sequence job)
        {
            float displacement = MaxDelta(_startVertices, managed.Vertices, out _);
            Assert.That(displacement, Is.GreaterThan(LaplacianTolerance * MeaningfulDisplacementFactor),
                $"{label}: the managed path barely moved the mesh ({displacement:E3}), so the bound would prove nothing.");

            float bound = lambda * displacement + LaplacianTolerance;
            float divergence = MaxDelta(managed.Vertices, job.Vertices, out int worst);
            Assert.That(divergence, Is.LessThanOrEqualTo(bound),
                $"{label}: job and managed paths diverge by {divergence:E3} at vertex {worst}, past the " +
                $"Jacobi/Gauss-Seidel bound {bound:E3} (lambda {lambda:F3}, largest displacement {displacement:E3}).");
        }

        private static float MaxDelta(Vector3[] a, Vector3[] b, out int worst)
        {
            float maxSqr = 0f;
            worst = 0;
            for (int i = 0; i < a.Length; i++)
            {
                float sqr = (a[i] - b[i]).sqrMagnitude;
                if (sqr > maxSqr) { maxSqr = sqr; worst = i; }
            }
            return Mathf.Sqrt(maxSqr);
        }

        /// Greedy maximal set of candidates no two of which share an edge, in candidate order.
        private List<int> IndependentSubset(List<int> candidates)
        {
            var blocked = new HashSet<int>();
            var picked = new List<int>();
            foreach (int c in candidates)
            {
                if (!blocked.Add(c)) continue;
                picked.Add(c);
                for (int n = _neighborStart[c], end = n + _neighborCount[c]; n < end; n++) blocked.Add(_neighbors[n]);
            }
            return picked;
        }

        // ------------------------------------------------------------------------ reflection

        private TDelegate Bind<TDelegate>(string methodName) where TDelegate : Delegate
        {
            MethodInfo method = typeof(SculptController).GetMethod(methodName, AnyMember);
            Assert.That(method, Is.Not.Null, $"SculptController.{methodName} not found - renamed? Update this test.");
            try
            {
                return (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), _controller, method);
            }
            catch (ArgumentException)
            {
                Assert.Fail($"SculptController.{methodName} no longer matches {typeof(TDelegate).Name} - its signature changed. Update this test.");
                return null;
            }
        }

        private List<int> DirtyItems()
        {
            object set = GetField(_controller, "_dirtyVertexScratch");
            return (List<int>)GetField(set, "Items");
        }

        private static object Constant(string name) => FieldOf(typeof(SculptController), name).GetRawConstantValue();

        private static void BuildTangentBasis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent)
        {
            MethodInfo method = typeof(SculptController).GetMethod("BuildTangentBasis", AnyMember);
            Assert.That(method, Is.Not.Null, "SculptController.BuildTangentBasis not found - renamed? Update this test.");
            object[] args = { normal, null, null };
            method.Invoke(null, args);
            tangent = (Vector3)args[1];
            bitangent = (Vector3)args[2];
        }

        private static FieldInfo FieldOf(Type type, string name)
        {
            FieldInfo field = type.GetField(name, AnyMember);
            Assert.That(field, Is.Not.Null, $"{type.Name}.{name} not found - renamed? Update this test.");
            return field;
        }

        private static object GetField(object target, string name) => FieldOf(target.GetType(), name).GetValue(target);

        private static void SetField(object target, string name, object value) => FieldOf(target.GetType(), name).SetValue(target, value);

        private static void Invoke(object target, string name)
        {
            MethodInfo method = target.GetType().GetMethod(name, AnyMember, null, Type.EmptyTypes, null);
            Assert.That(method, Is.Not.Null, $"{target.GetType().Name}.{name}() not found - renamed? Update this test.");
            method.Invoke(target, null);
        }

        // ------------------------------------------------------------------------ mesh

        private static float SurfaceRadius(Vector3 dir) => SphereRadius * (1f
            + 0.04f * Mathf.Sin(dir.x * 23f) * Mathf.Sin(dir.y * 19f + 1.3f) * Mathf.Sin(dir.z * 17f + 0.7f)
            + 0.015f * Mathf.Sin(dir.x * 71f + dir.z * 53f));

        /// A subdivided icosahedron pushed out to a bumpy sphere, with a per-vertex radial jitter of a
        /// fifth of an edge on top - the cell-scale roughness a remeshed sculpt has, and what gives the
        /// Laplacian tests something to move. Deterministic, closed, no duplicate vertices.
        /// 7 subdivisions is 163,842 vertices / 327,680 triangles; 8 is 655,362 / 1,310,720.
        internal static Mesh BuildNoisyIcosphere(int subdivisions)
        {
            float t = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var verts = new List<Vector3>
            {
                new Vector3(-1, t, 0), new Vector3(1, t, 0), new Vector3(-1, -t, 0), new Vector3(1, -t, 0),
                new Vector3(0, -1, t), new Vector3(0, 1, t), new Vector3(0, -1, -t), new Vector3(0, 1, -t),
                new Vector3(t, 0, -1), new Vector3(t, 0, 1), new Vector3(-t, 0, -1), new Vector3(-t, 0, 1),
            };
            for (int i = 0; i < verts.Count; i++) verts[i] = verts[i].normalized;
            var tris = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };

            for (int level = 0; level < subdivisions; level++)
            {
                var midpoints = new Dictionary<long, int>(tris.Count);
                var next = new List<int>(tris.Count * 4);
                for (int f = 0; f < tris.Count; f += 3)
                {
                    int a = tris[f], b = tris[f + 1], c = tris[f + 2];
                    int ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);
                    next.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                tris = next;

                int Midpoint(int i, int j)
                {
                    long key = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
                    if (midpoints.TryGetValue(key, out int existing)) return existing;
                    verts.Add((verts[i] + verts[j]).normalized);
                    midpoints[key] = verts.Count - 1;
                    return verts.Count - 1;
                }
            }

            float edge = 1.0515f * SphereRadius / (1 << subdivisions); // icosahedron edge / circumradius
            for (int i = 0; i < verts.Count; i++)
            {
                float hash = Mathf.Sin(i * 12.9898f) * 43758.5453f;
                float jitter = ((hash - Mathf.Floor(hash)) * 2f - 1f) * 0.2f * edge;
                verts[i] *= SurfaceRadius(verts[i]) + jitter;
            }

            var mesh = new Mesh { name = "JobParityIcosphere", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
