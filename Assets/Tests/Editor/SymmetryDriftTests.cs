using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Sculpting.Tests
{
    /// Mirrored sculpting has to keep an exactly symmetric model exactly symmetric, stroke after
    /// stroke - otherwise the halves wander apart a little on every pass and the user finds one
    /// ear, one cheekbone, one side of a spine thicker than the other an hour later.
    ///
    /// The mesh is built symmetric TO THE BIT across all three planes (see SymmetricTestMesh), so
    /// every vertex's partner is known exactly and no tolerance is needed to find it. A stroke is
    /// then driven through the production per-frame entry points - the same sign loops, flush and
    /// release refresh a real stroke takes - and the halves are compared vertex for vertex.
    /// The only asymmetry allowed is float rounding from sums visited in a different order on the
    /// two sides, which is orders of magnitude below anything a stroke does.
    ///
    /// Three distances from the plane: ON it (the two dabs coincide), a third of a radius off it
    /// (the footprints overlap), and three radii away (no overlap at all - the control).
    [TestFixture(7)]
    [TestFixture(8)]
    public class SymmetryDriftTests
    {
        private const float BrushRadius = 0.12f;
        private const int StrokeFrames = 16;
        // Handed to the time-paced brushes explicitly: Time.deltaTime is 0 outside Play mode.
        private const float FrameDt = 1f / 60f;
        // Mirror error allowed, as a fraction of the largest displacement the stroke produced, plus
        // an absolute floor. The floor is not float epsilon, deliberately: SculptableMesh's drift
        // filter decides per vertex whether it has "visibly" moved (extent / 4000, about 1.3e-4
        // here) and leaves anything under that un-refreshed, so a mirrored pair whose motions differ
        // only by rounding can land either side of that knife edge. Everything within the filter's
        // threshold is, by that design, treated as invisible - the rendered surface itself may be
        // that far off - so that bound is the floor; ResidualDoesNotAccumulateOverASession checks a
        // whole session stays under it rather than adding up.
        private const float RelativeTolerance = 1e-4f;
        private const float AbsoluteTolerance = SymmetricTestMesh.SphereRadius / 4000f;

        // With Front Facing Only on, every operation is still mirror-exact, but the silhouette ramp
        // amplifies float-level differences within a stroke: measured up to 1% of the stroke's own
        // displacement for Clay Accumulate held ON the plane at 1.3M triangles - it was 50% before
        // this pass. Two percent still fails every defect this file was written against.
        private const float RelativeToleranceFrontFacing = 2e-2f;

        // A session may carry more than one stroke does. At 1.3M triangles an Inflate stroke laid
        // straight over a fresh centreline crease - pushing along the normals of a sharp groove,
        // which is ill-conditioned - amplified float noise to 1.1e-2 for one stroke, and the strokes
        // after it relaxed that back to 4.6e-4. A fifth of a percent of the radius; the systematic
        // drift this was written against reached tens of percent of a single stroke.
        private const float SessionTolerance = SymmetricTestMesh.SphereRadius / 500f;

        // Off to one side and above, so Front Facing Only judges the two halves from genuinely
        // different directions and a viewpoint that is not mirrored along with its dab shows up.
        private static readonly Vector3 CameraWorld = new Vector3(1.2f, 0.35f, -1.6f);

        public enum Brush { Clay, ClayAccumulate, Crease, Inflate, Flatten, Smooth }

        private readonly int _subdivisions;
        private GameObject _meshObject, _controllerObject, _cameraObject;
        private Mesh _sourceMesh;
        private SculptableMesh _sculptable;
        private SculptController _controller;
        private MirrorController _mirror;
        private Vector3[] _startVertices;
        private int[][] _partners;

        private float _strokeMs;

        public SymmetryDriftTests(int subdivisions) => _subdivisions = subdivisions;

        private Vector3[] Verts => _sculptable.Vertices;

        // ------------------------------------------------------------------------ fixture

        [OneTimeSetUp]
        public void CreateObjects()
        {
            _sourceMesh = SymmetricTestMesh.BuildIcosphere(_subdivisions, out _partners);

            _meshObject = new GameObject("SymmetryDriftMesh") { hideFlags = HideFlags.HideAndDontSave };
            _meshObject.AddComponent<MeshFilter>().sharedMesh = _sourceMesh;
            _sculptable = _meshObject.AddComponent<SculptableMesh>();
            TestReflection.SetField(_sculptable, "useMeshCollider", false);
            if (_sculptable.Vertices == null) TestReflection.Invoke(_sculptable, "Awake");
            // Edit mode runs no Awake for it either, so it never builds its plane quads - only
            // GetMirrorSigns is needed.
            _mirror = _meshObject.AddComponent<MirrorController>();

            _cameraObject = new GameObject("SymmetryDriftCamera") { hideFlags = HideFlags.HideAndDontSave };
            _cameraObject.transform.position = CameraWorld;
            Camera camera = _cameraObject.AddComponent<Camera>();
            camera.enabled = false;

            _controllerObject = new GameObject("SymmetryDriftController") { hideFlags = HideFlags.HideAndDontSave };
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
            // Normals, cavity, the drift baseline and both spatial indices back to the untouched mesh.
            _sculptable.ApplyVertices();
            TestReflection.Invoke(_controller, "MarkPositionMirrorStale");

            _mirror.MirrorX = true;
            _mirror.MirrorY = false;
            _mirror.MirrorZ = false;

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
        public void FixtureIsExactlySymmetric()
        {
            for (int axis = 0; axis < 3; axis++)
            {
                Measure(axis, out float pair, out float plane, out _, out int worst);
                Assert.That(pair, Is.EqualTo(0f), $"axis {axis}: vertex {worst} is not an exact mirror of its partner");
                Assert.That(plane, Is.EqualTo(0f), $"axis {axis}: an on-plane vertex sits off the plane");
            }
        }

        [Test]
        public void MirroredStrokeStaysSymmetric([Values] Brush brush,
            [Values(0f, 0.35f, 3f)] float radiiFromPlane,
            [Values(false, true)] bool frontFacingOnly,
            [Values(true, false)] bool burstJobs)
        {
            _controller.FrontFacingOnly = frontFacingOnly;
            TestReflection.SetField(_controller, "useBurstJobs", burstJobs);

            RunStroke(brush, radiiFromPlane * BrushRadius, startAngle: -2.5f, angleStep: 0.12f);
            AssertSymmetric($"{brush} {radiiFromPlane}r ffo={frontFacingOnly} jobs={burstJobs}", SymmetryMap.AxisX, frontFacingOnly);
        }

        /// An alpha that is not itself mirror-symmetric has to come out mirrored on the far side,
        /// at any stamp rotation - far from the plane, so nothing else can be the cause.
        [Test]
        public void MirroredAlphaStampIsMirrored([Values(0f, 30f)] float rotation, [Values(true, false)] bool burstJobs)
        {
            TestReflection.SetField(_controller, "useBurstJobs", burstJobs);
            _controller.UseAlpha = true;
            _controller.AlphaType = BrushAlphaType.Noise;
            _controller.AlphaRotation = rotation;
            _controller.AlphaScale = 1f;

            RunStroke(Brush.Clay, 3f * BrushRadius, startAngle: -2.5f, angleStep: 0.12f);
            AssertSymmetric($"Clay alpha rot={rotation} jobs={burstJobs}", SymmetryMap.AxisX);
        }

        /// X and Z on together, stroking across the top where both planes run through the footprint.
        [Test]
        public void StrokeNearTwoPlanesStaysSymmetric([Values(Brush.Clay, Brush.Crease, Brush.Smooth)] Brush brush)
        {
            _mirror.MirrorZ = true;
            RunStroke(brush, 0.35f * BrushRadius, startAngle: -0.45f, angleStep: 0.06f);
            AssertSymmetric($"{brush} two planes", SymmetryMap.AxisX);
            AssertSymmetric($"{brush} two planes", SymmetryMap.AxisZ);
        }

        /// The drift the user reported is slow: a little per stroke, adding up over a session. So:
        /// many strokes, every brush, both sides of the plane, on it, near it and far from it - and
        /// the halves must still agree at the end to within what a single stroke is held to.
        ///
        /// Without Front Facing Only. Each stroke is mirror-exact with it on as well (see
        /// MirroredStrokeStaysSymmetric), but its silhouette ramp has a large gain on whatever
        /// asymmetry is already present, float rounding included: this session with it on measured
        /// 5e-5 after the first seven strokes and 9e-2 after twenty-one, almost all of it from strong
        /// Clay Accumulate strokes raising a fin along the plane, where a hair's difference
        /// decides which wall is facing the camera. That is a property of the option rather than
        /// of mirroring, so it is not asserted.
        [Test]
        public void ResidualDoesNotAccumulateOverASession()
        {
            var brushes = new[] { Brush.ClayAccumulate, Brush.Crease, Brush.Inflate, Brush.Smooth, Brush.Clay, Brush.Flatten };
            // Six brushes against six distances, so every brush meets several distances over the session.
            float[] radiiFromPlane = { 0f, 0.35f, -1f, 3f, -0.35f, 1f };
            var history = new System.Text.StringBuilder();

            const int strokes = 21;
            for (int s = 0; s < strokes; s++)
            {
                Brush brush = brushes[s % brushes.Length];
                RunStroke(brush, radiiFromPlane[s % radiiFromPlane.Length] * BrushRadius,
                    startAngle: -2.5f + 0.2f * (s % 5), angleStep: 0.12f);
                Measure(SymmetryMap.AxisX, out float pair, out float plane, out _, out _);
                history.Append($"{brush}:{Mathf.Max(pair, plane):E1} ");
            }

            Measure(SymmetryMap.AxisX, out float finalPair, out float finalPlane, out float displacement, out int worst);
            string report = $"session of {strokes} strokes: pair {finalPair:E2}, plane {finalPlane:E2}, " +
                            $"displacement {displacement:E3} - per stroke: {history}";
            TestContext.WriteLine(report);
            Assert.That(displacement, Is.GreaterThan(1e-2f), report);
            Assert.That(Mathf.Max(finalPair, finalPlane), Is.LessThanOrEqualTo(SessionTolerance),
                $"{report} - worst pair at vertex {worst}");
        }

        // ------------------------------------------------------------------------ harness

        private void RunStroke(Brush brush, float planeOffset, float startAngle, float angleStep)
        {
            _controller.Accumulate = brush == Brush.ClayAccumulate || brush == Brush.Inflate;

            // What a mouse press does before the first frame of a stroke.
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

            var clock = Stopwatch.StartNew();
            for (int f = 0; f < StrokeFrames; f++)
            {
                // The mesh sits at the origin unrotated, so world and local coincide.
                Vector3 dir = RingDirection(planeOffset, startAngle + angleStep * f);
                Vector3 point = dir * SymmetricTestMesh.SurfaceRadius(dir);
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
            _strokeMs = (float)clock.Elapsed.TotalMilliseconds / StrokeFrames;

            // What a mouse release does (HandleStrokeEndCommit, minus the undo commit).
            _sculptable.RefreshStrokeNormalsAndCurvature();
        }

        /// A circle of constant distance from the X plane, parametrised by angle around the X axis.
        private static Vector3 RingDirection(float planeOffset, float angle)
        {
            float sx = planeOffset / SymmetricTestMesh.SphereRadius;
            float ring = Mathf.Sqrt(1f - sx * sx);
            return new Vector3(sx, ring * Mathf.Cos(angle), ring * Mathf.Sin(angle));
        }

        private void AssertSymmetric(string label, int axis, bool frontFacingOnly = false)
        {
            Measure(axis, out float pair, out float plane, out float displacement, out int worst);
            string report = $"{label} [axis {axis}]: pair {pair:E2}, plane {plane:E2}, " +
                            $"displacement {displacement:E3}, {_strokeMs:F2} ms/frame";
            TestContext.WriteLine(report);

            Assert.That(displacement, Is.GreaterThan(5e-5f), $"{report} - the stroke barely moved the mesh, so symmetry would prove nothing.");
            float allowed = (frontFacingOnly ? RelativeToleranceFrontFacing : RelativeTolerance) * displacement + AbsoluteTolerance;
            Assert.That(Mathf.Max(pair, plane), Is.LessThanOrEqualTo(allowed),
                $"{report} - worst pair at vertex {worst}, allowed {allowed:E2}");
        }

        private void Measure(int axis, out float pair, out float plane, out float displacement, out int worst)
        {
            Vector3[] v = Verts;
            int[] partner = _partners[axis];
            float pairSqr = 0f, displacementSqr = 0f;
            plane = 0f;
            worst = -1;
            for (int i = 0; i < v.Length; i++)
            {
                float moved = (v[i] - _startVertices[i]).sqrMagnitude;
                if (moved > displacementSqr) displacementSqr = moved;

                int j = partner[i];
                if (j == i)
                {
                    float off = Mathf.Abs(SymmetryMap.Coord(v[i], axis));
                    if (off > plane) plane = off;
                    continue;
                }
                float error = (v[i] - SymmetryMap.Reflect(v[j], axis)).sqrMagnitude;
                if (error > pairSqr) { pairSqr = error; worst = i; }
            }
            pair = Mathf.Sqrt(pairSqr);
            displacement = Mathf.Sqrt(displacementSqr);
        }
    }

    /// Make Symmetric and Symmetry Cleanup on the kind of mesh they actually get used on: a
    /// remesh of a symmetric shape, which has no exact vertex pairs at all - only the lattice
    /// alignment that makes the two halves come out mirrored to within the extractor's own noise.
    [TestFixture(190)]
    [TestFixture(375)]
    public class SymmetryRepairTests
    {
        private readonly int _resolution;
        private GameObject _meshObject;
        private Mesh _remeshed;
        private SculptableMesh _sculptable;
        private Vector3[] _startVertices;
        private int[] _triangles;
        private float _spacing;

        public SymmetryRepairTests(int resolution) => _resolution = resolution;

        [OneTimeSetUp]
        public void CreateObjects()
        {
            Mesh source = SymmetricTestMesh.BuildIcosphere(6, out _);
            _remeshed = MeshRemesher.Remesh(source.vertices, source.triangles, _resolution);
            Object.DestroyImmediate(source);
            Assert.That(_remeshed, Is.Not.Null, "remesh produced nothing");
            _remeshed.hideFlags = HideFlags.HideAndDontSave;

            _sculptable = CreateSculptable(Object.Instantiate(_remeshed), out _meshObject);
            _startVertices = (Vector3[])_sculptable.Vertices.Clone();
            _triangles = (int[])_sculptable.Triangles.Clone();
            _spacing = SymmetryMap.MeanEdgeLength(_startVertices, _triangles);
        }

        [OneTimeTearDown]
        public void DestroyObjects()
        {
            DestroySculptable(_sculptable, _meshObject);
            if (_remeshed != null) Object.DestroyImmediate(_remeshed);
            // SymmetryOps records every repair into the scene-wide history; those entries name
            // objects this fixture has just destroyed.
            EditHistory.Clear();
        }

        [SetUp]
        public void Restore()
        {
            Array.Copy(_startVertices, _sculptable.Vertices, _startVertices.Length);
            _sculptable.ApplyVertices();
        }

        /// A remesh of a symmetric shape must come back reported as a symmetric one: every vertex
        /// near the centreline either IS on it or has a twin across it, and no pair that clearly
        /// has a twin gets filed as a centreline vertex instead.
        [Test]
        public void RemeshedSymmetricShapeClassifiesCentrelineCorrectly()
        {
            Vector3[] v = _sculptable.Vertices;
            SymmetryMap map = SymmetryOps.BuildMap(_sculptable, SymmetryMap.AxisX, 1f);
            int missed = CountStraddlers(v, map, out int ambiguous);
            MirrorResidual(v, SymmetryMap.AxisX, out float mean, out float max, out int beyond);

            string report = $"res {_resolution}: {v.Length} verts, spacing {_spacing:E3}, tol {map.Tolerance:E3} " +
                            $"({map.Tolerance / _spacing:F2} spacings) - {map.Summary()}; on-plane vertices whose nearer " +
                            $"twin was left unmatched: {missed}, whose nearer twin was claimed by a closer match: {ambiguous}; " +
                            $"shape residual mean {mean / _spacing:F3} max {max / _spacing:F3} spacings, {beyond} beyond one spacing";
            TestContext.WriteLine(report);
            // Baseline before the fix: 873 of 1224 "centreline" vertices at res 190 (1934 of 2549 at
            // 375) had a twin sitting unclaimed across the plane.
            Assert.That(missed, Is.EqualTo(0), report);
        }

        /// On a model that is already symmetric the repair must be close to a no-op: no vertex
        /// dragged anywhere, and above all no edge collapsed into a degenerate triangle.
        [Test]
        public void MakeSymmetricOnSymmetricShapeDoesNoDamage([Values(true, false)] bool sourceIsPositive)
        {
            int degenerateBefore = DegenerateTriangles(_sculptable.Vertices, _triangles, _spacing);
            int changed = SymmetryOps.MakeSymmetric(_sculptable, SymmetryMap.AxisX, 1f, sourceIsPositive,
                out int pairs, out int unmatched, out int carried);
            Vector3[] v = _sculptable.Vertices;
            int degenerateAfter = DegenerateTriangles(v, _triangles, _spacing);
            float maxMove = MaxDelta(_startVertices, v);
            MirrorResidual(v, SymmetryMap.AxisX, out float mean, out float max, out int beyond);

            string report = $"res {_resolution} source {(sourceIsPositive ? "+" : "-")}: changed {changed}, pairs {pairs}, " +
                            $"unmatched {unmatched}, carried {carried}; degenerate triangles {degenerateBefore} -> " +
                            $"{degenerateAfter}; largest move {maxMove / _spacing:F3} spacings; residual mean " +
                            $"{mean / _spacing:F4} max {max / _spacing:F3} spacings, {beyond} beyond one spacing";
            TestContext.WriteLine(report);
            Assert.That(degenerateAfter, Is.LessThanOrEqualTo(degenerateBefore), report);
            // Not near zero: the remesher leaves mirrored pairs up to ~0.8 spacings out of true, and
            // lining those up is the job. A vertex dragged well past that went somewhere it had no
            // business going.
            Assert.That(maxMove, Is.LessThanOrEqualTo(1.5f * _spacing), report);
        }

        /// The job it exists for: one side pushed out of shape, including right up against the
        /// centreline. The repair must line the halves up, leave no collapsed geometry behind, and
        /// be idempotent - pressing it again must not keep moving things.
        [Test]
        public void MakeSymmetricRepairsDriftAndIsIdempotent([Values(true, false)] bool sourceIsPositive)
        {
            Vector3[] v = _sculptable.Vertices;
            ApplyOneSidedDrift(v);
            _sculptable.ApplyVertices();
            int degenerateBefore = DegenerateTriangles(v, _triangles, _spacing);
            MirrorResidual(v, SymmetryMap.AxisX, out float driftMean, out float driftMax, out _);

            int first = SymmetryOps.MakeSymmetric(_sculptable, SymmetryMap.AxisX, 1f, sourceIsPositive,
                out int pairs, out int unmatched, out int carried);
            v = _sculptable.Vertices;
            var afterFirst = (Vector3[])v.Clone();
            int degenerateAfter = DegenerateTriangles(v, _triangles, _spacing);
            MirrorResidual(v, SymmetryMap.AxisX, out float mean, out float max, out int beyond);

            int second = SymmetryOps.MakeSymmetric(_sculptable, SymmetryMap.AxisX, 1f, sourceIsPositive,
                out _, out int unmatchedSecond, out int carriedSecond);
            float secondMove = MaxDelta(afterFirst, _sculptable.Vertices);

            string report = $"res {_resolution} source {(sourceIsPositive ? "+" : "-")}: drift residual mean " +
                            $"{driftMean / _spacing:F3} max {driftMax / _spacing:F3} -> first press changed {first} " +
                            $"(pairs {pairs}, unmatched {unmatched}, carried {carried}), residual mean " +
                            $"{mean / _spacing:F4} max {max / _spacing:F3} spacings, {beyond} beyond one spacing; " +
                            $"degenerate {degenerateBefore} -> {degenerateAfter}; second press changed {second} " +
                            $"(unmatched {unmatchedSecond}, carried {carriedSecond}), moved up to " +
                            $"{secondMove / _spacing:E2} spacings";
            TestContext.WriteLine(report);

            Assert.That(first, Is.GreaterThan(0), report);
            Assert.That(degenerateAfter, Is.LessThanOrEqualTo(degenerateBefore), report);
            Assert.That(mean, Is.LessThanOrEqualTo(0.05f * _spacing), report);
            // A second press may still pair a handful of vertices the first had to carry, and those
            // move - measured at most 24 of 740k, by about a spacing. What it must not do is keep
            // reworking the model.
            Assert.That(second, Is.LessThanOrEqualTo(Mathf.Max(1, _startVertices.Length / 10000)), report);
            Assert.That(secondMove, Is.LessThanOrEqualTo(1.5f * _spacing), report);
        }

        /// Cleanup is for welding a seam that is two coincident shells. A remesh has no such seam, so
        /// what Cleanup may do to one is limited: weld centreline vertices that are genuinely within
        /// the seam radius of each other (short edges lying ON the plane), and nothing else - no
        /// degenerate faces, no real mirrored pairs collapsed into each other. Before the fix it
        /// welded 741 at res 190 (1574 at 375), most of them straddling pairs; measured after,
        /// 269 (463), all on the plane.
        [Test]
        public void CleanupOnRemeshedShapeStaysOnTheSeam()
        {
            // ReplaceMesh releases the displaced mesh with Destroy, which in edit mode only logs an
            // error (production runs in Play mode) - not what this test is about.
            LogAssert.ignoreFailingMessages = true;
            SculptableMesh copy = CreateSculptable(Object.Instantiate(_remeshed), out GameObject copyObject);
            try
            {
                int trianglesBefore = copy.TriangleCount;
                bool ran = SymmetryOps.Cleanup(copy, SymmetryMap.AxisX, 1f, out int snapped, out int welded);
                int trianglesAfter = copy.TriangleCount;
                int degenerate = DegenerateTriangles(copy.Vertices, copy.Triangles, _spacing);

                string report = $"res {_resolution}: ran {ran}, snapped {snapped}, welded {welded}, triangles " +
                                $"{trianglesBefore} -> {trianglesAfter}, degenerate after {degenerate}";
                TestContext.WriteLine(report);
                Assert.That(degenerate, Is.EqualTo(0), report);
                // Each weld removes at most the two faces on the collapsed edge.
                Assert.That(trianglesBefore - trianglesAfter, Is.LessThanOrEqualTo(2 * welded), report);
                SymmetryMap map = SymmetryOps.BuildMap(_sculptable, SymmetryMap.AxisX, 1f);
                Assert.That(welded, Is.LessThanOrEqualTo(map.OnPlaneCount), report);
            }
            finally
            {
                DestroySculptable(copy, copyObject);
            }
        }

        // ------------------------------------------------------------------------ harness

        private static SculptableMesh CreateSculptable(Mesh mesh, out GameObject go)
        {
            mesh.hideFlags = HideFlags.HideAndDontSave;
            go = new GameObject("SymmetryRepairMesh") { hideFlags = HideFlags.HideAndDontSave };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var sculptable = go.AddComponent<SculptableMesh>();
            TestReflection.SetField(sculptable, "useMeshCollider", false);
            if (sculptable.Vertices == null) TestReflection.Invoke(sculptable, "Awake");
            return sculptable;
        }

        private static void DestroySculptable(SculptableMesh sculptable, GameObject go)
        {
            if (sculptable != null)
            {
                TestReflection.Invoke(sculptable, "ReleaseNativeResources");
                if (sculptable.Mesh != null) Object.DestroyImmediate(sculptable.Mesh);
            }
            if (go != null)
            {
                MeshFilter filter = go.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null) Object.DestroyImmediate(filter.sharedMesh);
                Object.DestroyImmediate(go);
            }
        }

        /// A broad bump on the +X half away from the plane, and a second one that runs right up to
        /// it - cut off sharply AT the plane, which is what drift near a centreline looks like.
        private void ApplyOneSidedDrift(Vector3[] v)
        {
            var farCentre = new Vector3(0.25f, 0.1f, -0.35f);
            var nearCentre = new Vector3(0.03f, 0.3f, -0.25f);
            for (int i = 0; i < v.Length; i++)
            {
                if (v[i].x <= 0f) continue;
                Vector3 outward = v[i].normalized;
                float far = Mathf.Exp(-(v[i] - farCentre).sqrMagnitude / (2f * 0.08f * 0.08f));
                float near = Mathf.Exp(-(v[i] - nearCentre).sqrMagnitude / (2f * 0.06f * 0.06f));
                v[i] += outward * (_spacing * (4f * far + 2f * near));
            }
        }

        /// On-plane vertices (by the map) with some vertex across the plane closer to their reflection
        /// than they are themselves. Returned: those whose nearer twin the map left UNMATCHED - a pair
        /// it genuinely missed. `ambiguous`: those whose nearer twin is already the partner of a closer
        /// match or on the plane itself, where greedy closest-first matching is entitled to choose.
        private int CountStraddlers(Vector3[] v, SymmetryMap map, out int ambiguous)
        {
            Dictionary<Vector3Int, List<int>> grid = BuildGrid(v, _spacing);
            int missed = 0;
            ambiguous = 0;
            for (int i = 0; i < v.Length; i++)
            {
                if (!map.IsOnPlane(i)) continue;
                float off = Mathf.Abs(v[i].x);
                if (off <= 0.05f * map.Tolerance) continue;

                Vector3 reflected = SymmetryMap.Reflect(v[i], SymmetryMap.AxisX);
                float best = Nearest(v, grid, reflected, _spacing, i, v[i].x, out int twin);
                if (best >= off) continue;
                if (map.IsOnPlane(twin) || map.PartnerOf(twin) != SymmetryMap.NoPartner) ambiguous++;
                else missed++;
            }
            return missed;
        }

        private void MirrorResidual(Vector3[] v, int axis, out float mean, out float max, out int beyondSpacing)
        {
            Dictionary<Vector3Int, List<int>> grid = BuildGrid(v, _spacing);
            double sum = 0;
            max = 0f;
            beyondSpacing = 0;
            for (int i = 0; i < v.Length; i++)
            {
                float d = Nearest(v, grid, SymmetryMap.Reflect(v[i], axis), _spacing, -1, 0f, out _);
                if (d > _spacing) beyondSpacing++;
                d = Mathf.Min(d, 1.5f * _spacing);
                sum += d;
                if (d > max) max = d;
            }
            mean = (float)(sum / v.Length);
        }

        private static Dictionary<Vector3Int, List<int>> BuildGrid(Vector3[] v, float cell)
        {
            var grid = new Dictionary<Vector3Int, List<int>>(v.Length / 2);
            for (int i = 0; i < v.Length; i++)
            {
                Vector3Int key = Vector3Int.FloorToInt(v[i] / cell);
                if (!grid.TryGetValue(key, out List<int> list)) grid[key] = list = new List<int>(4);
                list.Add(i);
            }
            return grid;
        }

        /// Distance from `p` to the nearest vertex within about one cell, skipping `exclude` and,
        /// when `requireSideOpposite` is non-zero, anything on that same side of the X plane.
        private static float Nearest(Vector3[] v, Dictionary<Vector3Int, List<int>> grid, Vector3 p, float cell,
            int exclude, float requireSideOpposite, out int index)
        {
            Vector3Int home = Vector3Int.FloorToInt(p / cell);
            float best = float.MaxValue;
            index = -1;
            for (int z = -1; z <= 1; z++)
            for (int y = -1; y <= 1; y++)
            for (int x = -1; x <= 1; x++)
            {
                if (!grid.TryGetValue(new Vector3Int(home.x + x, home.y + y, home.z + z), out List<int> list)) continue;
                foreach (int j in list)
                {
                    if (j == exclude) continue;
                    if (requireSideOpposite != 0f && v[j].x * requireSideOpposite >= 0f) continue;
                    float d = (v[j] - p).sqrMagnitude;
                    if (d < best) { best = d; index = j; }
                }
            }
            return best == float.MaxValue ? float.MaxValue : Mathf.Sqrt(best);
        }

        private static int DegenerateTriangles(Vector3[] v, int[] triangles, float spacing)
        {
            // Twice the area, squared - a triangle under a ten-thousandth of a normal one's area.
            float threshold = 1e-4f * spacing * spacing;
            float thresholdSqr = threshold * threshold;
            int count = 0;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                Vector3 a = v[triangles[t]];
                if (Vector3.Cross(v[triangles[t + 1]] - a, v[triangles[t + 2]] - a).sqrMagnitude <= thresholdSqr) count++;
            }
            return count;
        }

        private static float MaxDelta(Vector3[] a, Vector3[] b)
        {
            float maxSqr = 0f;
            for (int i = 0; i < a.Length; i++) maxSqr = Mathf.Max(maxSqr, (a[i] - b[i]).sqrMagnitude);
            return Mathf.Sqrt(maxSqr);
        }
    }

    /// A subdivided icosahedron pushed out to a bumpy sphere, symmetric to the bit across all three
    /// axis planes. The base icosahedron is, midpoint subdivision preserves it exactly (negation
    /// commutes with every operation involved), and the surface noise reads only |x|, |y|, |z|.
    internal static class SymmetricTestMesh
    {
        public const float SphereRadius = 0.5f;

        public static float SurfaceRadius(Vector3 dir)
        {
            float x = Mathf.Abs(dir.x), y = Mathf.Abs(dir.y), z = Mathf.Abs(dir.z);
            return SphereRadius * (1f
                + 0.04f * Mathf.Sin(x * 23f + 0.4f) * Mathf.Sin(y * 19f + 1.3f) * Mathf.Sin(z * 17f + 0.7f)
                + 0.015f * Mathf.Sin(x * 71f + z * 53f + y * 11f));
        }

        /// `partners[axis][i]` is the vertex that is exactly vertex i's reflection across that axis's
        /// plane - i itself for a vertex on the plane.
        public static Mesh BuildIcosphere(int subdivisions, out int[][] partners)
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

            // Partners from the unit-sphere positions, before any noise - exact by construction.
            var index = new Dictionary<Vector3, int>(verts.Count);
            for (int i = 0; i < verts.Count; i++) index.Add(Canonical(verts[i]), i);
            partners = new int[3][];
            for (int axis = 0; axis < 3; axis++)
            {
                partners[axis] = new int[verts.Count];
                for (int i = 0; i < verts.Count; i++)
                {
                    if (!index.TryGetValue(Canonical(SymmetryMap.Reflect(verts[i], axis)), out int j))
                        throw new InvalidOperationException($"vertex {i} has no exact mirror across axis {axis}");
                    partners[axis][i] = j;
                }
            }

            float edge = 1.0515f * SphereRadius / (1 << subdivisions);
            for (int i = 0; i < verts.Count; i++)
            {
                Vector3 u = verts[i];
                float hash = Mathf.Sin(Mathf.Abs(u.x) * 12.9898f + Mathf.Abs(u.y) * 78.233f + Mathf.Abs(u.z) * 37.719f) * 43758.5453f;
                float jitter = ((hash - Mathf.Floor(hash)) * 2f - 1f) * 0.2f * edge;
                verts[i] = u * (SurfaceRadius(u) + jitter);
            }

            var mesh = new Mesh { name = "SymmetricIcosphere", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        // -0 and +0 compare equal but need not hash equal; adding +0 folds the former into the latter.
        private static Vector3 Canonical(Vector3 v) => new Vector3(v.x + 0f, v.y + 0f, v.z + 0f);
    }

    /// Reflection access for tests living outside Assembly-CSharp - see SculptControllerJobParityTests.
    internal static class TestReflection
    {
        private const BindingFlags AnyMember =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        public static TDelegate Bind<TDelegate>(object target, string methodName) where TDelegate : Delegate
        {
            MethodInfo method = target.GetType().GetMethod(methodName, AnyMember);
            Assert.That(method, Is.Not.Null, $"{target.GetType().Name}.{methodName} not found - renamed? Update this test.");
            return (TDelegate)Delegate.CreateDelegate(typeof(TDelegate), target, method);
        }

        public static object GetField(object target, string name) => FieldOf(target.GetType(), name).GetValue(target);

        public static void SetField(object target, string name, object value) => FieldOf(target.GetType(), name).SetValue(target, value);

        public static void Invoke(object target, string name, params object[] args)
        {
            var types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++) types[i] = args[i].GetType();
            MethodInfo method = target.GetType().GetMethod(name, AnyMember, null, types, null);
            Assert.That(method, Is.Not.Null, $"{target.GetType().Name}.{name} not found - renamed? Update this test.");
            method.Invoke(target, args);
        }

        private static FieldInfo FieldOf(Type type, string name)
        {
            FieldInfo field = type.GetField(name, AnyMember);
            Assert.That(field, Is.Not.Null, $"{type.Name}.{name} not found - renamed? Update this test.");
            return field;
        }
    }
}
