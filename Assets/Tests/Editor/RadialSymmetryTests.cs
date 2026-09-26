using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Sculpting.Tests
{
    /// The symmetry group a MirrorController's settings turn into (see SymmetryGroup): the right
    /// elements, no duplicates, closed where it should be, and the mirror-only enumeration the
    /// order-symmetric dab walk has always used.
    public class SymmetryGroupTests
    {
        [Test]
        public void MirrorOnlyGroupsAreFlipMasksInOrder()
        {
            SymmetryGroup g = SymmetryGroup.Build(true, false, true, 1, Vector3.up);
            Assert.That(g.Count, Is.EqualTo(4));
            Assert.That(g.IsGroup, Is.True);
            int[] masks = { 0, 1, 4, 5 };
            for (int i = 0; i < 4; i++)
            {
                Assert.That(g[i].Diagonal, Is.True, $"element {i}");
                Assert.That(g[i].Sign, Is.EqualTo(SymmetryGroup.SignOfFlipMask(masks[i])), $"element {i}");
            }
        }

        [Test]
        public void RadialGroupHasNRotationsAndIsClosed([Values(2, 3, 5, 6, 8, 12, 32)] int n)
        {
            SymmetryGroup g = SymmetryGroup.Build(false, false, false, n, Vector3.up);
            Assert.That(g.Count, Is.EqualTo(n));
            Assert.That(g.IsGroup, Is.True);
            Assert.That(g[0].IsIdentity, Is.True);
            for (int i = 0; i < n; i++)
            {
                Assert.That(g[i].Reflects, Is.False, $"rotation {i} reverses handedness");
                Assert.That(Mathf.Abs(g[i].Determinant - 1f), Is.LessThan(1e-5f));
                Vector3 p = g[i].Apply(new Vector3(0.3f, 0.7f, -0.2f));
                Assert.That(p.y, Is.EqualTo(0.7f).Within(1e-6f), "a rotation about Y must keep the height");
                Assert.That(new Vector2(p.x, p.z).magnitude, Is.EqualTo(new Vector2(0.3f, -0.2f).magnitude).Within(1e-6f));
            }
        }

        /// Quarter turns about a coordinate axis are exact, so N = 2 and 4 are as exact as mirrors.
        [Test]
        public void QuarterTurnsAboutCoordinateAxesAreExact([Values(0, 1, 2)] int axis)
        {
            Vector3 dir = axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward;
            SymmetryGroup g = SymmetryGroup.Build(false, false, false, 4, dir);
            var p = new Vector3(0.123f, -0.456f, 0.789f);
            for (int i = 0; i < 4; i++)
            {
                Vector3 q = g[i].Apply(p);
                // Every coordinate of a quarter-turned point is +-one of the originals, bit for bit.
                foreach (float c in new[] { q.x, q.y, q.z })
                    Assert.That(Mathf.Abs(c) == 0.123f || Mathf.Abs(c) == 0.456f || Mathf.Abs(c) == 0.789f,
                        $"axis {axis} step {i}: {q:R}");
            }
            Assert.That(g[2].Diagonal, Is.True, "the half turn is a pure sign flip");
        }

        /// Mirror X with radial about Y is the dihedral group; adding Z adds nothing for even N
        /// (X then Z is the half turn), so it must not add duplicate elements that would apply a
        /// stroke twice in the same place.
        [Test]
        public void DihedralGroupsAreDeduplicated([Values(3, 4, 6)] int n)
        {
            SymmetryGroup x = SymmetryGroup.Build(true, false, false, n, Vector3.up);
            Assert.That(x.Count, Is.EqualTo(2 * n));
            Assert.That(x.IsGroup, Is.True);

            SymmetryGroup xz = SymmetryGroup.Build(true, false, true, n, Vector3.up);
            Assert.That(xz.Count, Is.EqualTo(n % 2 == 0 ? 2 * n : 4 * n));
            Assert.That(xz.IsGroup, Is.True);

            SymmetryGroup xy = SymmetryGroup.Build(true, true, false, n, Vector3.up);
            Assert.That(xy.Count, Is.EqualTo(4 * n), "Y is perpendicular to the axis: a new element per rotation");
            Assert.That(xy.IsGroup, Is.True);
        }

        /// A custom axis tilted against a mirror plane generates an infinite group: the list is the
        /// plain product and says it is not a group, so the dab walk falls back to plain order.
        [Test]
        public void TiltedCustomAxisWithMirrorIsNotAGroup()
        {
            SymmetryGroup g = SymmetryGroup.Build(true, false, false, 5, new Vector3(0.3f, 1f, 0.2f));
            Assert.That(g.Count, Is.EqualTo(10));
            Assert.That(g.IsGroup, Is.False);

            var subgroup = new List<int>();
            g.InteractingSubgroup(Vector3.zero, 1f, subgroup);
            Assert.That(subgroup, Is.EqualTo(new[] { 0 }));

            // Without the mirror a custom axis is an ordinary cyclic group.
            Assert.That(SymmetryGroup.Build(false, false, false, 5, new Vector3(0.3f, 1f, 0.2f)).IsGroup, Is.True);
        }

        /// The dab walk's planning: copies that can meet form a subgroup, and the cosets partition
        /// the group.
        [Test]
        public void InteractingSubgroupAndCosets()
        {
            SymmetryGroup g = SymmetryGroup.Build(true, false, false, 6, Vector3.up); // D6, 12 elements
            var h = new List<int>();
            var reps = new List<int>();

            // Far from the axis and off the X plane (at 30 degrees) but ON a mirror plane of the
            // dihedral group - the copy reflected across it lands on the dab itself.
            Vector3 onReflectionPlane = new Vector3(Mathf.Cos(Mathf.PI / 6f), 0f, Mathf.Sin(Mathf.PI / 6f));
            g.InteractingSubgroup(onReflectionPlane, 0.05f, h);
            Assert.That(h.Count, Is.EqualTo(2), "one reflection plane through the dab: the pair");
            g.CosetRepresentatives(h, reps);
            Assert.That(reps.Count, Is.EqualTo(6));

            g.InteractingSubgroup(new Vector3(0.01f, 0.5f, 0.005f), 0.1f, h);
            Assert.That(h.Count, Is.EqualTo(12), "next to the axis every copy meets every other");
            g.CosetRepresentatives(h, reps);
            Assert.That(reps, Is.EqualTo(new[] { 0 }));

            g.InteractingSubgroup(new Vector3(0.8f, 0f, 0.3f), 0.05f, h);
            Assert.That(h, Is.EqualTo(new[] { 0 }), "a generic point far from the axis meets nothing");
        }
    }

    /// Radial sculpting has to keep an N-fold symmetric model N-fold symmetric, stroke after stroke -
    /// the radial counterpart of SymmetryDriftTests, driven through the same production entry
    /// points.
    ///
    /// Unlike a mirror, a rotation by 360/N is not exact in float for most N, so the fixture's
    /// copies agree to rounding rather than to the bit, and that is the floor here too. What the
    /// test exists to catch is the systematic difference: copies applied one after another against
    /// the live mesh near the axis, where their footprints overlap, get different operations - the
    /// failure the order-symmetric walk (SculptController.MirroredDabWalk) exists to prevent.
    ///
    /// The mesh is a sphere of rings whose vertex counts double toward the equator, so triangles
    /// stay even up to the poles (see BuildLayout), with one extra vertex in the middle of every
    /// quad (four triangles per quad): its topology maps onto itself under every rotation by a
    /// multiple of 15 degrees about Y and under the X and Z mirrors, so a vertex's partner under
    /// each symmetry is known by index.
    public class RadialSymmetryTests
    {
        private const int BaseCount = 24;  // vertices on the rings nearest the poles - divisible by 3, 4, 6, 8, 12
        private const int Segments = 384;  // on the widest rings
        private const int Rings = 192;
        private const float SphereRadius = 0.5f;
        private const float BrushRadius = 0.06f;
        private const int StrokeFrames = 16;
        private const float FrameDt = 1f / 60f;

        // As SymmetryDriftTests: a fraction of the stroke's own displacement, plus the drift
        // filter's invisibility threshold as a floor.
        private const float RelativeTolerance = 1e-4f;
        private const float AbsoluteTolerance = SphereRadius / 4000f;
        private const float RelativeToleranceFrontFacing = 2e-2f;

        private static readonly Vector3 CameraWorld = new Vector3(1.2f, 0.9f, -1.6f);

        public enum Brush { Clay, ClayAccumulate, Crease, Inflate, Flatten, Smooth }

        public enum Config { Radial3, Radial6, Radial8, Radial4MirrorX, Radial12 }

        private GameObject _meshObject, _controllerObject, _cameraObject;
        private Mesh _sourceMesh;
        private SculptableMesh _sculptable;
        private SculptController _controller;
        private MirrorController _mirror;
        private Vector3[] _startVertices;
        private Vector3[] _idealDirections;
        private readonly Dictionary<Config, int[][]> _partners = new Dictionary<Config, int[][]>();
        private float _strokeMs;

        private Vector3[] Verts => _sculptable.Vertices;

        // ------------------------------------------------------------------------ fixture

        [OneTimeSetUp]
        public void CreateObjects()
        {
            _sourceMesh = BuildRadialSphere(out _idealDirections);

            _meshObject = new GameObject("RadialSymmetryMesh") { hideFlags = HideFlags.HideAndDontSave };
            _meshObject.AddComponent<MeshFilter>().sharedMesh = _sourceMesh;
            _sculptable = _meshObject.AddComponent<SculptableMesh>();
            TestReflection.SetField(_sculptable, "useMeshCollider", false);
            if (_sculptable.Vertices == null) TestReflection.Invoke(_sculptable, "Awake");
            _mirror = _meshObject.AddComponent<MirrorController>();

            _cameraObject = new GameObject("RadialSymmetryCamera") { hideFlags = HideFlags.HideAndDontSave };
            _cameraObject.transform.position = CameraWorld;
            Camera camera = _cameraObject.AddComponent<Camera>();
            camera.enabled = false;

            _controllerObject = new GameObject("RadialSymmetryController") { hideFlags = HideFlags.HideAndDontSave };
            _controller = _controllerObject.AddComponent<SculptController>();
            TestReflection.SetField(_controller, "sculptableMesh", _sculptable);
            TestReflection.SetField(_controller, "mirrorController", _mirror);
            TestReflection.SetField(_controller, "cam", camera);

            _startVertices = (Vector3[])Verts.Clone();
        }

        [OneTimeTearDown]
        public void DestroyObjects()
        {
            if (_controller != null) TestReflection.Invoke(_controller, "ReleaseNativeResources");
            if (_sculptable != null)
            {
                TestReflection.Invoke(_sculptable, "ReleaseNativeResources");
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
            Array.Copy(_startVertices, Verts, _startVertices.Length);
            Array.Clear(_sculptable.Mask, 0, _sculptable.Mask.Length);
            _sculptable.BeginStrokeUndo();
            _sculptable.ApplyVertices();
            TestReflection.Invoke(_controller, "MarkPositionMirrorStale");

            _controller.BrushRadius = BrushRadius;
            _controller.BrushStrength = 0.5f;
            _controller.AccumulateStrength = 1f;
            _controller.FrontFacingOnly = false;
            _controller.UseAlpha = false;
            _controller.FlattenPlaneOffset = 0f;
            TestReflection.SetField(_controller, "useBurstJobs", true);
        }

        // ------------------------------------------------------------------------ tests

        [Test]
        public void FixtureIsRadiallySymmetric([Values] Config config)
        {
            Apply(config);
            float error = MaxPairError(config, out _);
            // Not zero: a rotation by 360/N rounds. It is the floor every stroke is measured against.
            Assert.That(error, Is.LessThan(1e-6f), $"{config}: {error:E2}");
        }

        /// On the axis (every copy lands on the same dab), just off it (all copies overlap), two
        /// and a half radii out (neighbouring copies overlap for N = 6 and up) and far out (none do).
        [Test]
        public void RadialStrokeStaysSymmetric([Values] Brush brush,
            [Values(Config.Radial3, Config.Radial6, Config.Radial8, Config.Radial12, Config.Radial4MirrorX)] Config config,
            [Values(0f, 0.6f, 2.5f, 6f)] float radiiFromAxis,
            [Values(true, false)] bool burstJobs)
        {
            Apply(config);
            TestReflection.SetField(_controller, "useBurstJobs", burstJobs);
            RunStroke(brush, radiiFromAxis * BrushRadius);
            AssertSymmetric($"{brush} {config} {radiiFromAxis}r jobs={burstJobs}", config);
        }

        /// Each copy is judged from its own rotated viewpoint (see SculptController._dabCameraLocal).
        [Test]
        public void RadialStrokeWithFrontFacingOnlyStaysSymmetric([Values(Brush.Clay, Brush.Crease, Brush.Inflate)] Brush brush,
            [Values(0.6f, 2.5f, 6f)] float radiiFromAxis)
        {
            Apply(Config.Radial6);
            _controller.FrontFacingOnly = true;
            RunStroke(brush, radiiFromAxis * BrushRadius);
            AssertSymmetric($"{brush} ffo {radiiFromAxis}r", Config.Radial6, frontFacingOnly: true);
        }

        /// An alpha that is not itself symmetric has to come out rotated with each copy (the tangent
        /// frame turns with it - see SculptController.DabTangentBasis), at any stamp rotation.
        [Test]
        public void RadialAlphaStampIsRotated([Values(0f, 30f)] float rotation, [Values(true, false)] bool burstJobs)
        {
            Apply(Config.Radial6);
            TestReflection.SetField(_controller, "useBurstJobs", burstJobs);
            _controller.UseAlpha = true;
            _controller.AlphaType = BrushAlphaType.Noise;
            _controller.AlphaRotation = rotation;
            _controller.AlphaScale = 1f;
            RunStroke(Brush.Clay, 6f * BrushRadius);
            AssertSymmetric($"Clay alpha rot={rotation} jobs={burstJobs}", Config.Radial6);
        }

        /// Copies that land on top of each other at the pole act as one dab, not N stacked ones
        /// (see SculptController.MirroredDabWalk): a stroke across the pole moves the surface about
        /// as far with 12 repeats as with none, where applying the copies one after another would
        /// have moved it up to 12 times as far.
        [Test]
        public void CoincidentCopiesAtThePoleActAsOneDab([Values(Brush.Clay, Brush.ClayAccumulate, Brush.Crease, Brush.Inflate)] Brush brush)
        {
            _mirror.MirrorX = _mirror.MirrorY = _mirror.MirrorZ = false;
            _mirror.Radial = false;
            RunStroke(brush, 0f);
            float single = MaxDisplacement();

            ResetState();
            Apply(Config.Radial12);
            RunStroke(brush, 0f);
            float radial = MaxDisplacement();

            string report = $"{brush}: no symmetry {single:E3}, radial 12 {radial:E3} ({radial / single:F2}x)";
            TestContext.WriteLine(report);
            Assert.That(radial / single, Is.InRange(0.5f, 2f), report);
        }

        /// The largest group there is - radial 32 with a mirror, 64 copies - through Clay, whose
        /// surface relax thins its dab centres down to a cap. Centres from different copies never
        /// merge, so with more copies than the cap the thinning loop used to double its radius
        /// forever and hang the editor. Reaching the end of the stroke is the test.
        [Test]
        public void MoreCopiesThanTheRelaxCapDoNotStall()
        {
            _mirror.MirrorX = true;
            _mirror.MirrorY = _mirror.MirrorZ = false;
            _mirror.Radial = true;
            _mirror.RadialAxisChoice = RadialAxis.Y;
            _mirror.RadialCount = SymmetryGroup.MaxRadialCount;
            Assert.That(_mirror.GetSymmetry().Count, Is.EqualTo(2 * SymmetryGroup.MaxRadialCount));
            TestReflection.SetField(_controller, "surfaceRelax", 0.22f);

            RunStrokeFrames(Brush.Clay, 6f * BrushRadius, 2);
            Assert.That(MaxDisplacement(), Is.GreaterThan(0f));
        }

        // ------------------------------------------------------------------------ harness

        private void Apply(Config config)
        {
            _mirror.MirrorX = config == Config.Radial4MirrorX;
            _mirror.MirrorY = false;
            _mirror.MirrorZ = false;
            _mirror.Radial = true;
            _mirror.RadialAxisChoice = RadialAxis.Y;
            _mirror.RadialCount = CountOf(config);
        }

        private static int CountOf(Config config)
        {
            switch (config)
            {
                case Config.Radial3: return 3;
                case Config.Radial6: return 6;
                case Config.Radial8: return 8;
                case Config.Radial4MirrorX: return 4;
                default: return 12;
            }
        }

        private void RunStroke(Brush brush, float distanceFromAxis) => RunStrokeFrames(brush, distanceFromAxis, StrokeFrames);

        private void RunStrokeFrames(Brush brush, float distanceFromAxis, int strokeFrames)
        {
            _controller.Accumulate = brush == Brush.ClayAccumulate || brush == Brush.Inflate;

            _sculptable.BeginStrokeUndo();
            TestReflection.SetField(_controller, "_lastClayStrokeLocal", null);
            TestReflection.SetField(_controller, "_lastClayStrokeNormalLocal", null);
            TestReflection.SetField(_controller, "_lastCarveStrokeLocal", null);

            var clay = TestReflection.Bind<Action<Vector3, Vector3, bool>>(_controller, "ApplyClayStroke");
            var carve = TestReflection.Bind<Action<Vector3, Vector3, bool>>(_controller, "ApplyCarveStroke");
            var mirrored = TestReflection.Bind<Action<Vector3, Vector3, bool, float, Action<Vector3, Vector3, bool, float>>>(_controller, "ApplyMirroredBrush");
            var inflate = TestReflection.Bind<Action<Vector3, Vector3, bool, float>>(_controller, "ApplyInflateBrushLocal");
            var flatten = TestReflection.Bind<Action<Vector3, Vector3, bool, float>>(_controller, "ApplyFlattenBrushLocal");
            var smooth = TestReflection.Bind<Action<Vector3, float>>(_controller, "ApplySmoothBrush");

            // Centred at the given distance from the Y axis on the upper hemisphere, at an angle
            // that is no symmetry plane of any config, crossing it obliquely.
            float polar = Mathf.Asin(Mathf.Clamp01(distanceFromAxis / SphereRadius));
            const float azimuth = 0.37f;
            Vector3 centre = new Vector3(Mathf.Sin(polar) * Mathf.Cos(azimuth), Mathf.Cos(polar), Mathf.Sin(polar) * Mathf.Sin(azimuth));
            Vector3 across = Vector3.Cross(centre, new Vector3(0.3f, 0.2f, 1f)).normalized;

            var clock = Stopwatch.StartNew();
            for (int f = 0; f < strokeFrames; f++)
            {
                Vector3 dir = (centre + across * (0.012f * (f - StrokeFrames * 0.5f))).normalized;
                Vector3 point = dir * SurfaceRadius(dir);
                switch (brush)
                {
                    case Brush.Clay:
                    case Brush.ClayAccumulate: clay(point, dir, true); break;
                    case Brush.Crease: carve(point, dir, false); break;
                    case Brush.Inflate: mirrored(point, dir, true, FrameDt, inflate); break;
                    case Brush.Flatten: mirrored(point, dir, true, FrameDt, flatten); break;
                    case Brush.Smooth: smooth(point, FrameDt); break;
                }
            }
            _strokeMs = (float)clock.Elapsed.TotalMilliseconds / strokeFrames;
            _sculptable.RefreshStrokeNormalsAndCurvature();
        }

        private void AssertSymmetric(string label, Config config, bool frontFacingOnly = false)
        {
            float pair = MaxPairError(config, out int worst);
            float displacement = MaxDisplacement();
            string report = $"{label}: pair {pair:E2}, displacement {displacement:E3}, {_strokeMs:F2} ms/frame";
            TestContext.WriteLine(report);
            Assert.That(displacement, Is.GreaterThan(5e-5f), $"{report} - the stroke barely moved the mesh, so symmetry would prove nothing.");
            float allowed = (frontFacingOnly ? RelativeToleranceFrontFacing : RelativeTolerance) * displacement + AbsoluteTolerance;
            Assert.That(pair, Is.LessThanOrEqualTo(allowed), $"{report} - worst at vertex {worst}, allowed {allowed:E2}");
        }

        private float MaxDisplacement()
        {
            Vector3[] v = Verts;
            float max = 0f;
            for (int i = 0; i < _startVertices.Length; i++) max = Mathf.Max(max, (v[i] - _startVertices[i]).sqrMagnitude);
            return Mathf.Sqrt(max);
        }

        /// Largest |v[partner_g(i)] - g v[i]| over every element g of the config's group.
        private float MaxPairError(Config config, out int worst)
        {
            SymmetryGroup group = _mirror.GetSymmetry();
            int[][] partners = PartnersOf(config, group);
            Vector3[] v = Verts;
            float maxSqr = 0f;
            worst = -1;
            for (int g = 1; g < group.Count; g++)
            {
                SymmetryOp op = group[g];
                int[] p = partners[g];
                for (int i = 0; i < _startVertices.Length; i++)
                {
                    float e = (v[p[i]] - op.Apply(v[i])).sqrMagnitude;
                    if (e > maxSqr) { maxSqr = e; worst = i; }
                }
            }
            return Mathf.Sqrt(maxSqr);
        }

        private int[][] PartnersOf(Config config, SymmetryGroup group)
        {
            if (_partners.TryGetValue(config, out int[][] cached)) return cached;
            var partners = new int[group.Count][];
            for (int g = 0; g < group.Count; g++)
            {
                partners[g] = new int[_idealDirections.Length];
                for (int i = 0; i < _idealDirections.Length; i++)
                    partners[g][i] = IndexOfDirection(group[g].Apply(_idealDirections[i]));
            }
            _partners[config] = partners;
            return partners;
        }

        // ------------------------------------------------------------------------ the mesh

        private static float SurfaceRadius(Vector3 dir)
        {
            double polar = Math.Acos(Mathf.Clamp(dir.y, -1f, 1f));
            double azimuth = Math.Atan2(dir.z, dir.x);
            return (float)Radius(polar, azimuth);
        }

        // Symmetric under every rotation by a multiple of 360/24 about Y and under the X and Z
        // mirrors, with bumps in both directions so no brush sees a flat target.
        private static double Radius(double polar, double azimuth) =>
            SphereRadius * (1.0 + 0.04 * Math.Sin(7.0 * polar) + 0.02 * Math.Sin(polar) * Math.Sin(polar) * Math.Cos(24.0 * azimuth));

        // Ring r (1..Rings-1) has RingCount[r] vertices from RingStart[r]; band b (between rings b and
        // b+1, 1..Rings-2) has a centre vertex per quad from CentreStart[b] when both rings match, and
        // -1 when the count doubles across it. The poles are vertices 0 (north) and 1 (south).
        private static readonly int[] RingCount = new int[Rings];
        private static readonly int[] RingStart = new int[Rings];
        private static readonly int[] CentreStart = new int[Rings];
        private static readonly int LayoutVertexCount = BuildLayout();

        /// Ring vertex counts: BaseCount times a power of two, the largest that keeps the edge along
        /// the ring no shorter than the edge between rings, so triangles stay close to even right up
        /// to the pole. A plain UV sphere's pole fan of slivers amplifies float noise under Inflate by
        /// ~100x a frame - for mirrors and rotations alike - which says nothing about symmetry.
        private static int BuildLayout()
        {
            int next = 2;
            for (int ring = 1; ring < Rings; ring++)
            {
                double fit = 2.0 * Rings * Math.Sin(Math.PI * ring / Rings);
                int count = BaseCount;
                while (count * 2 <= Segments && count * 2 <= fit) count *= 2;
                RingCount[ring] = count;
                RingStart[ring] = next;
                next += count;
            }
            for (int band = 1; band < Rings - 1; band++)
            {
                CentreStart[band] = -1;
                if (RingCount[band] != RingCount[band + 1]) continue;
                CentreStart[band] = next;
                next += RingCount[band];
            }
            return next;
        }

        private static int Wrap(int k, int count) => ((k % count) + count) % count;

        private static int RingVertex(int ring, int k) => RingStart[ring] + Wrap(k, RingCount[ring]);

        private static int CentreVertex(int band, int k) => CentreStart[band] + Wrap(k, RingCount[band]);

        /// Which vertex sits in `dir` (a unit direction) - the inverse of the layout.
        private static int IndexOfDirection(Vector3 dir)
        {
            double polar = Math.Acos(Math.Max(-1.0, Math.Min(1.0, dir.y))) * Rings / Math.PI;
            int p2 = (int)Math.Round(polar * 2.0);
            if (p2 == 0) return 0;
            if (p2 == 2 * Rings) return 1;
            double turn = Math.Atan2(dir.z, dir.x) / (2.0 * Math.PI);
            int ring = p2 / 2;
            if (p2 % 2 == 0) return RingVertex(ring, (int)Math.Round(turn * RingCount[ring]));
            return CentreVertex(ring, (int)Math.Round(turn * RingCount[ring] - 0.5));
        }

        private static Mesh BuildRadialSphere(out Vector3[] directions)
        {
            var dirs = new Vector3[LayoutVertexCount];
            var verts = new Vector3[LayoutVertexCount];
            directions = dirs;

            void Put(int index, double polar, double azimuth)
            {
                var d = new Vector3((float)(Math.Sin(polar) * Math.Cos(azimuth)), (float)Math.Cos(polar),
                                    (float)(Math.Sin(polar) * Math.Sin(azimuth)));
                dirs[index] = d;
                double r = Radius(polar, azimuth);
                verts[index] = new Vector3((float)(Math.Sin(polar) * Math.Cos(azimuth) * r), (float)(Math.Cos(polar) * r),
                                           (float)(Math.Sin(polar) * Math.Sin(azimuth) * r));
            }

            Put(0, 0.0, 0.0);
            Put(1, Math.PI, 0.0);
            for (int ring = 1; ring < Rings; ring++)
            for (int k = 0; k < RingCount[ring]; k++)
                Put(RingVertex(ring, k), Math.PI * ring / Rings, 2.0 * Math.PI * k / RingCount[ring]);
            for (int band = 1; band < Rings - 1; band++)
            {
                if (CentreStart[band] < 0) continue;
                for (int k = 0; k < RingCount[band]; k++)
                    Put(CentreVertex(band, k), Math.PI * (band + 0.5) / Rings, 2.0 * Math.PI * (k + 0.5) / RingCount[band]);
            }

            var tris = new List<int>(LayoutVertexCount * 6);
            void Tri(int a, int b, int c)
            {
                // Outward by geometry, decided per triangle, so the rule itself is symmetric.
                Vector3 n = Vector3.Cross(verts[b] - verts[a], verts[c] - verts[a]);
                if (Vector3.Dot(n, verts[a] + verts[b] + verts[c]) < 0f) { int t = b; b = c; c = t; }
                tris.Add(a); tris.Add(b); tris.Add(c);
            }

            for (int k = 0; k < RingCount[1]; k++) Tri(0, RingVertex(1, k), RingVertex(1, k + 1));
            for (int k = 0; k < RingCount[Rings - 1]; k++) Tri(1, RingVertex(Rings - 1, k), RingVertex(Rings - 1, k + 1));
            for (int band = 1; band < Rings - 1; band++)
            {
                if (CentreStart[band] >= 0)
                {
                    // Matching rings: each quad split into four around its centre vertex.
                    for (int k = 0; k < RingCount[band]; k++)
                    {
                        int a = RingVertex(band, k), b = RingVertex(band, k + 1);
                        int c = RingVertex(band + 1, k + 1), d = RingVertex(band + 1, k);
                        int m = CentreVertex(band, k);
                        Tri(m, a, b); Tri(m, b, c); Tri(m, c, d); Tri(m, d, a);
                    }
                    continue;
                }

                // The count doubles: three triangles per edge of the smaller ring, a pattern that is
                // symmetric about that edge's midpoint, so it survives every rotation and mirror.
                bool grows = RingCount[band + 1] > RingCount[band];
                int small = grows ? band : band + 1, large = grows ? band + 1 : band;
                for (int k = 0; k < RingCount[small]; k++)
                {
                    int a0 = RingVertex(small, k), a1 = RingVertex(small, k + 1);
                    int b0 = RingVertex(large, 2 * k), b1 = RingVertex(large, 2 * k + 1), b2 = RingVertex(large, 2 * k + 2);
                    Tri(a0, b0, b1); Tri(a0, b1, a1); Tri(a1, b1, b2);
                }
            }

            var mesh = new Mesh { name = "RadialSymmetricSphere", indexFormat = IndexFormat.UInt32 };
            mesh.vertices = verts;
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
