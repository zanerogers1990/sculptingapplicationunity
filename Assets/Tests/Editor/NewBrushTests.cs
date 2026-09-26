using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Sculpting.Tests
{
    /// Behaviour of the brushes added for ZBrush parity - Layer's height cap, Snakehook's pull and
    /// its symmetry, and Move's Connected Only - at the density the app is used at.
    public class NewBrushTests
    {
        private const float BrushRadius = 0.12f;
        private GameObject _meshObject, _controllerObject, _cameraObject;
        private Mesh _sourceMesh;
        private SculptableMesh _sculptable;
        private SculptController _controller;
        private MirrorController _mirror;
        private int[][] _partners;
        private Vector3[] _startVertices;

        [OneTimeSetUp]
        public void CreateObjects()
        {
            _sourceMesh = SymmetricTestMesh.BuildIcosphere(7, out _partners);
            _meshObject = new GameObject("NewBrushMesh") { hideFlags = HideFlags.HideAndDontSave };
            _meshObject.AddComponent<MeshFilter>().sharedMesh = _sourceMesh;
            _sculptable = _meshObject.AddComponent<SculptableMesh>();
            TestReflection.SetField(_sculptable, "useMeshCollider", false);
            if (_sculptable.Vertices == null) TestReflection.Invoke(_sculptable, "Awake");
            _mirror = _meshObject.AddComponent<MirrorController>();

            _cameraObject = new GameObject("NewBrushCamera") { hideFlags = HideFlags.HideAndDontSave };
            _cameraObject.transform.position = new Vector3(0f, 0f, -2.5f);
            _cameraObject.transform.LookAt(Vector3.zero);
            Camera camera = _cameraObject.AddComponent<Camera>();
            camera.enabled = false;

            _controllerObject = new GameObject("NewBrushController") { hideFlags = HideFlags.HideAndDontSave };
            _controller = _controllerObject.AddComponent<SculptController>();
            TestReflection.SetField(_controller, "sculptableMesh", _sculptable);
            TestReflection.SetField(_controller, "mirrorController", _mirror);
            TestReflection.SetField(_controller, "cam", camera);

            _startVertices = (Vector3[])_sculptable.Vertices.Clone();
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
            Array.Copy(_startVertices, _sculptable.Vertices, _startVertices.Length);
            Array.Clear(_sculptable.Mask, 0, _sculptable.Mask.Length);
            _sculptable.ApplyVertices();
            _sculptable.PrepareSpatialIndex(BrushRadius * 0.5f);
            _sculptable.BeginStrokeUndo();
            TestReflection.Invoke(_controller, "MarkPositionMirrorStale");
            TestReflection.Invoke(_controller, "ResetDabStroke");
            _mirror.MirrorX = false;
            _controller.BrushRadius = BrushRadius;
            _controller.BrushStrength = 0.5f;
            _controller.AccumulateStrength = 1f;
            _controller.FrontFacingOnly = false;
        }

        /// A stroke along a line of latitude on the camera-facing side, `passes` times back and forth,
        /// through the same entry point HandleSculptInput uses.
        private void StrokeLayer(int passes, float y)
        {
            _controller.CurrentBrush = BrushType.Layer;
            _controller.BrushStrength = 0.1f; // after the switch - strength is remembered per brush
            var local = TestReflection.Bind<Action<Vector3, Vector3, bool, float>>(_controller, "ApplyLayerBrushLocal");
            Type holdType = typeof(SculptController).GetNestedType("DabHoldMode", System.Reflection.BindingFlags.NonPublic);
            object alwaysWorks = Enum.Parse(holdType, "AlwaysWorks");
            var stroke = typeof(SculptController).GetMethod("ApplyStandardStroke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

            for (int pass = 0; pass < passes; pass++)
            {
                TestReflection.Invoke(_controller, "ResetDabStroke");
                for (int f = 0; f <= 40; f++)
                {
                    float x = Mathf.Lerp(-0.2f, 0.2f, (pass % 2 == 0 ? f : 40 - f) / 40f);
                    var ray = new Ray(new Vector3(x, y, -2f), Vector3.forward);
                    TestReflection.Invoke(_controller, "MarkPositionMirrorStale");
                    Assert.That(_sculptable.RaycastMesh(ray, 100f, out Vector3 hit, out Vector3 normal), Is.True);
                    stroke.Invoke(_controller, new object[] { hit, normal, true, 1f / 60f, local, 0.1f, alwaysWorks, true });
                }
            }
        }

        private float MaxRise()
        {
            Vector3[] v = _sculptable.Vertices;
            float max = 0f;
            for (int i = 0; i < v.Length; i++)
                max = Mathf.Max(max, Vector3.Dot(v[i] - _startVertices[i], _startVertices[i].normalized));
            return max;
        }

        [Test]
        public void LayerReachesItsHeightAndCrossingDoesNotDoubleIt()
        {
            _controller.LayerHeight = 0.2f;
            float height = BrushRadius * 0.2f;

            StrokeLayer(1, 0f);
            float one = MaxRise();
            Assert.That(one, Is.GreaterThan(height * 0.6f), "One pass should raise most of the layer.");

            StrokeLayer(4, 0f);
            // A second stroke re-bases on where the first left the surface, so only within one
            // stroke is the height a hard cap - these extra passes are that same stroke continuing.
            float many = MaxRise();
            Assert.That(many, Is.LessThanOrEqualTo(height * 1.05f), $"Layer overshot its height: {many / height:F2}x.");
        }

        private void Snake(Vector3 tipWorld, Vector3 delta, int frames)
        {
            _controller.CurrentBrush = BrushType.SnakeHook;
            TestReflection.SetField(_controller, "_snakeTipWorld", tipWorld);
            for (int f = 0; f < frames; f++)
            {
                TestReflection.Invoke(_controller, "MarkPositionMirrorStale");
                TestReflection.Invoke(_controller, "ApplySnakeHook", delta / frames);
            }
        }

        [Test]
        public void SnakehookPullsTheTipAllTheWay()
        {
            Vector3 tip = new Vector3(0.2f, 0.1f, 0f);
            tip = tip + new Vector3(0f, 0f, -Mathf.Sqrt(0.25f - tip.sqrMagnitude));
            Vector3 pull = new Vector3(0f, 0f, -0.4f); // straight out toward the camera, 3+ radii
            Snake(tip, pull, 20);

            Vector3[] v = _sculptable.Vertices;
            float best = 0f;
            for (int i = 0; i < v.Length; i++) best = Mathf.Max(best, Vector3.Dot(v[i] - _startVertices[i], pull.normalized));
            Assert.That(best, Is.GreaterThan(pull.magnitude * 0.8f),
                "The vertices under the tip should follow it out, re-grabbed step by step.");
        }

        [Test]
        public void SnakehookIsMirrorSymmetric()
        {
            _mirror.MirrorX = true;
            Vector3 tip = new Vector3(0.15f, 0.12f, 0f);
            tip = tip + new Vector3(0f, 0f, -Mathf.Sqrt(0.25f - tip.sqrMagnitude));
            Snake(tip, new Vector3(0.1f, 0.05f, -0.3f), 15);

            Vector3[] v = _sculptable.Vertices;
            float worst = 0f;
            for (int i = 0; i < v.Length; i++)
            {
                int j = _partners[0][i];
                if (j < 0) continue;
                Vector3 mirrored = new Vector3(-v[j].x, v[j].y, v[j].z);
                worst = Mathf.Max(worst, (v[i] - mirrored).magnitude);
            }
            Assert.That(worst, Is.LessThan(1e-5f), "The mirrored pull came out different from the primary one.");
        }

        [Test]
        public void MoveConnectedOnlyLeavesTheNeighbouringPieceAlone()
        {
            // Two separate spheres 0.02 apart; a grab near the gap reaches both with the radius.
            Mesh twin = BuildTwoSpheres();
            var go = new GameObject("TwinSpheres") { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                go.AddComponent<MeshFilter>().sharedMesh = twin;
                var mesh = go.AddComponent<SculptableMesh>();
                TestReflection.SetField(mesh, "useMeshCollider", false);
                if (mesh.Vertices == null) TestReflection.Invoke(mesh, "Awake");
                mesh.PrepareSpatialIndex(0.05f);

                Vector3 grab = new Vector3(-0.02f, 0f, 0f); // on sphere A's right edge
                var camera = new Vector3(0f, 0f, -3f);
                int half = mesh.VertexCount / 2;

                var all = mesh.SelectGrab(grab, 0.15f, false, camera, connectedOnly: false);
                var connected = mesh.SelectGrab(grab, 0.15f, false, camera, connectedOnly: true);
                Assert.That(Count(all.Indices, half), Is.GreaterThan(0), "The radius should reach sphere B, or the test proves nothing.");
                Assert.That(Count(connected.Indices, half), Is.EqualTo(0), "Connected Only grabbed the other piece.");
                Assert.That(connected.Indices.Length, Is.EqualTo(all.Indices.Length - Count(all.Indices, half)),
                    "Connected Only dropped part of the piece under the cursor.");
                TestReflection.Invoke(mesh, "ReleaseNativeResources");
                if (mesh.Mesh != null && mesh.Mesh != twin) Object.DestroyImmediate(mesh.Mesh);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(twin);
            }
        }

        /// The batched Clay program (one Burst job per frame, see SculptController.DabProgram) must move
        /// the mesh exactly as the plain per-dab path does - here with X symmetry on and the stroke
        /// crossing the plane, so the mirror-group steps run too. Surface Relax is off: its job and
        /// managed passes relax in different orders by design (see SculptControllerJobParityTests).
        ///
        /// pressureRamp: pen pressure climbing along the stroke, so every dab carries its own radius,
        /// edge softness and strength - recorded into each dab's op on the batched path, evaluated
        /// per dab on the per-dab one.
        [Test]
        public void ClayProgramMatchesThePerDabPath([Values(true, false)] bool accumulate, [Values(true, false)] bool mirror,
            [Values(12, 60)] int framesPerSweep, [Values(false, true)] bool pressureRamp)
        {
            Vector3[] batched = ClayStroke(jobs: true, framesPerSweep, accumulate, mirror, pressureRamp);
            ResetState();
            Vector3[] perDab = ClayStroke(jobs: false, framesPerSweep, accumulate, mirror, pressureRamp);

            float moved = 0f, diff = 0f;
            for (int i = 0; i < batched.Length; i++)
            {
                moved = Mathf.Max(moved, (perDab[i] - _startVertices[i]).magnitude);
                diff = Mathf.Max(diff, (perDab[i] - batched[i]).magnitude);
            }
            Assert.That(moved, Is.GreaterThan(1e-3f), "The stroke barely moved the mesh.");
            Assert.That(diff, Is.LessThan(1e-5f), $"Batched Clay differs from per-dab Clay by {diff:E2} (stroke moved {moved:E2}).");
        }

        private Vector3[] ClayStroke(bool jobs, int framesPerSweep, bool accumulate, bool mirror, bool pressureRamp = false)
        {
            _mirror.MirrorX = mirror;
            // Brush first: strength and Accumulate are remembered per brush, so setting them before
            // the switch would set them on the previous brush instead.
            _controller.CurrentBrush = BrushType.Clay;
            _controller.BrushStrength = 0.5f;
            _controller.Accumulate = accumulate;
            _controller.SurfaceRelax = 0f;
            _controller.UseAlpha = false;
            _controller.ClayPressureRadiusInfluence = 0.6f;
            _controller.ClayPressureSoftnessInfluence = 0.5f;
            bool before = _controller.UseBurstJobs;
            _controller.UseBurstJobs = jobs;
            try
            {
                TestReflection.Invoke(_controller, "ResetClayStroke");
                var stroke = TestReflection.Bind<Action<Vector3, Vector3, float, bool>>(_controller, "ApplyClayStrokeWithPressure");
                int frames = framesPerSweep * 2;
                for (int f = 0; f <= frames; f++)
                {
                    // Across the X plane and back.
                    float x = Mathf.Lerp(-0.15f, 0.15f, Mathf.PingPong(f / (float)framesPerSweep, 1f));
                    Vector3 dir = new Vector3(x, 0.08f, -0.45f).normalized;
                    Vector3 point = dir * SymmetricTestMesh.SurfaceRadius(dir);
                    TestReflection.Invoke(_controller, "MarkPositionMirrorStale");
                    stroke(point, dir, pressureRamp ? Mathf.Lerp(0.15f, 1f, f / (float)frames) : 1f, true);
                }
                // The release: the last segment is still waiting for a next sample.
                TestReflection.Invoke(_controller, "MarkPositionMirrorStale");
                TestReflection.Invoke(_controller, "EndClayStroke");
                return (Vector3[])_sculptable.Vertices.Clone();
            }
            finally
            {
                _controller.UseBurstJobs = before;
            }
        }

        /// The Apply ops the last frame's Clay program recorded (jobs on): each dab's point and radius.
        private List<(Vector3 Point, float Radius)> RecordedClayDabs()
        {
            var program = (System.Collections.IList)TestReflection.GetField(_controller, "_dabProgram");
            var dabs = new List<(Vector3, float)>();
            foreach (object op in program)
            {
                Type type = op.GetType();
                const System.Reflection.BindingFlags fields = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;
                if (type.GetField("Kind", fields).GetValue(op).ToString() != "Apply") continue;
                dabs.Add(((Vector3)type.GetField("Point", fields).GetValue(op), (float)type.GetField("Radius", fields).GetValue(op)));
            }
            return dabs;
        }

        private static Vector3 OnSphere(Vector3 dir)
        {
            dir.Normalize();
            return dir * SymmetricTestMesh.SurfaceRadius(dir);
        }

        /// A fast circular stroke, one sample every 20 degrees (brush-radius-sized jumps). Joining the
        /// samples with straight lines put the dabs up to rho(1 - cos 10deg) inside the circle, a
        /// visible polygon; along the stroke's spline they stay on it.
        ///
        /// Also run per dab with jobs off: the batched program finds each dab's footprint by capsules
        /// around the chord of the frame's dabs, which a curve bulges out of (see DabSide.Bulge).
        [Test]
        public void ClayDabsFollowTheCurveOfAFastStroke([Values(2, 3)] int samplesPerFrame)
        {
            var dabs = new List<Vector3>();
            float rho = 0.5f * Mathf.Sin(28f * Mathf.Deg2Rad);
            Vector3[] batched = ClayCircleStroke(jobs: true, samplesPerFrame, dabs);
            ResetState();
            Vector3[] perDab = ClayCircleStroke(jobs: false, samplesPerFrame, null);
            _controller.UseBurstJobs = true;

            float worst = 0f;
            foreach (Vector3 p in dabs) worst = Mathf.Max(worst, Mathf.Abs(new Vector2(p.x, p.y).magnitude - rho));
            float chordSag = rho * (1f - Mathf.Cos(10f * Mathf.Deg2Rad));
            TestContext.WriteLine($"{dabs.Count} dabs, worst off-circle {worst:E2} (chords would be {chordSag:E2})");
            Assert.That(dabs.Count, Is.GreaterThan(40));
            Assert.That(worst, Is.LessThan(chordSag * 0.2f), "The dabs cut across between samples instead of following the curve.");

            float diff = 0f;
            for (int i = 0; i < batched.Length; i++) diff = Mathf.Max(diff, (batched[i] - perDab[i]).magnitude);
            Assert.That(diff, Is.LessThan(1e-5f), $"Batched Clay differs from per-dab Clay by {diff:E2} along a curve.");
        }

        /// One sample every 20 degrees round a cone about the view axis, samplesPerFrame of them per
        /// frame (several knots, so several curved segments, per batched program). `dabs` collects
        /// the dabs the batched program recorded, skipping the first segment (closed with a phantom
        /// start) and the end.
        private Vector3[] ClayCircleStroke(bool jobs, int samplesPerFrame, List<Vector3> dabs)
        {
            _controller.CurrentBrush = BrushType.Clay;
            // After the switch - strength and Accumulate are remembered per brush.
            _controller.BrushStrength = 0.5f;
            _controller.AccumulateStrength = 1f;
            _controller.Accumulate = false;
            _controller.SurfaceRelax = 0f; // its job and managed passes differ by design
            _controller.UseAlpha = false;
            _controller.UseBurstJobs = jobs;
            TestReflection.Invoke(_controller, "ResetClayStroke");
            var knots = (System.Collections.IList)TestReflection.GetField(_controller, "_clayKnotScratch");
            var apply = TestReflection.Bind<Action<bool, bool>>(_controller, "ApplyClayKnots");

            float cone = 28f * Mathf.Deg2Rad;
            const int samples = 19;
            int k = 0;
            while (k < samples)
            {
                knots.Clear();
                for (int s = 0; s < samplesPerFrame && k < samples; s++, k++)
                {
                    float phi = k * 20f * Mathf.Deg2Rad;
                    // On an exact circle, not snapped to this fixture's slightly bumpy surface - the
                    // stroke takes the points as given, and a true circle is what dabs are judged by.
                    Vector3 point = 0.5f * new Vector3(Mathf.Sin(cone) * Mathf.Cos(phi), Mathf.Sin(cone) * Mathf.Sin(phi), -Mathf.Cos(cone));
                    knots.Add(new StrokePath.Knot { Point = point, Normal = point.normalized, Pressure = 1f });
                }
                TestReflection.Invoke(_controller, "MarkPositionMirrorStale");
                apply(true, false);
                if (dabs != null && k > 3)
                    foreach (var dab in RecordedClayDabs())
                        if (Vector3.Distance(dab.Point, 0.5f * new Vector3(Mathf.Sin(cone), 0f, -Mathf.Cos(cone))) > 0.1f)
                            dabs.Add(dab.Point);
            }
            return (Vector3[])_sculptable.Vertices.Clone();
        }

        /// Pressure is per dab: within one frame of a pen stroke whose pressure is rising, every dab
        /// gets its own (larger) footprint, instead of the whole frame sharing one value.
        [Test]
        public void ClayDabsCarryTheirOwnPressure()
        {
            _controller.CurrentBrush = BrushType.Clay;
            _controller.UseBurstJobs = true;
            _controller.ClayPressureRadiusInfluence = 0.6f;
            TestReflection.Invoke(_controller, "ResetClayStroke");
            var stroke = TestReflection.Bind<Action<Vector3, Vector3, float, bool>>(_controller, "ApplyClayStrokeWithPressure");

            var radii = new List<float>();
            int framesWithSeveral = 0;
            const int frames = 8;
            for (int f = 0; f <= frames; f++)
            {
                // Big jumps - several dabs a frame - with pressure rising from light to full.
                Vector3 point = OnSphere(new Vector3(Mathf.Lerp(-0.2f, 0.2f, f / (float)frames), 0.05f, -0.45f));
                TestReflection.Invoke(_controller, "MarkPositionMirrorStale");
                stroke(point, point.normalized, Mathf.Lerp(0.1f, 1f, f / (float)frames), true);
                var frameDabs = RecordedClayDabs();
                if (frameDabs.Count >= 2 && frameDabs[frameDabs.Count - 1].Radius > frameDabs[0].Radius) framesWithSeveral++;
                foreach (var dab in frameDabs) radii.Add(dab.Radius);
            }

            Assert.That(framesWithSeveral, Is.GreaterThan(3), "Dabs within a frame shared one pressure.");
            for (int i = 1; i < radii.Count; i++)
                Assert.That(radii[i], Is.GreaterThan(radii[i - 1]), $"dab {i}: pressure rose but the footprint did not grow.");
            // Radius = brushRadius * lerp(1 - influence, 1, pressure), between the two ends' values.
            Assert.That(radii[0], Is.EqualTo(BrushRadius * Mathf.Lerp(0.4f, 1f, 0.1f)).Within(1e-5f));
            Assert.That(radii[radii.Count - 1], Is.LessThanOrEqualTo(BrushRadius + 1e-6f));
        }

        private delegate Vector3 StrokeDepthLimit(Vector3 from, Vector3 moved, Vector3 strokeStart,
            Vector3 planeNormal, float maxAlong, float softBand);

        /// Clay's per-stroke depth cap eases a vertex in rather than stopping it dead: full steps
        /// outside the soft band, shrinking steps inside it, never past the cap, and nothing
        /// changed for a step away from the cap or sideways - for both stroke signs.
        [Test]
        public void StrokeDepthLimitEasesIntoTheCap()
        {
            var method = typeof(SculptController).GetMethod("LimitStrokeDepth",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null, "SculptController.LimitStrokeDepth not found - renamed? Update this test.");
            var limit = (StrokeDepthLimit)Delegate.CreateDelegate(typeof(StrokeDepthLimit), method);

            const float cap = 0.3f, band = 0.15f, step = 0.01f;
            foreach (float sign in new[] { 1f, -1f })
            {
                Vector3 n = Vector3.up * sign;
                Vector3 start = new Vector3(0.2f, -0.1f, 0.4f);
                float Along(Vector3 p) => Vector3.Dot(p - start, n);

                // Outside the band a step is untouched.
                Vector3 low = start + n * 0.05f;
                Assert.That(Along(limit(low, low + n * step, start, Vector3.up, sign * cap, band)) - Along(low),
                    Is.EqualTo(step).Within(1e-6f), $"sign {sign}: a step below the band was eased.");

                // Inside it, each step is smaller than the last and the vertex never passes the cap.
                Vector3 p = start + n * (cap - band);
                float last = float.MaxValue;
                for (int k = 0; k < 400; k++)
                {
                    Vector3 next = limit(p, p + n * step + Vector3.right * 0.002f, start, Vector3.up, sign * cap, band);
                    float moved = Along(next) - Along(p);
                    Assert.That(moved, Is.GreaterThanOrEqualTo(0f).And.LessThanOrEqualTo(last + 1e-7f),
                        $"sign {sign}: step {k} grew inside the band.");
                    Assert.That(Along(next), Is.LessThanOrEqualTo(cap + 1e-6f), $"sign {sign}: passed the cap.");
                    Assert.That(next.x - p.x, Is.EqualTo(0.002f).Within(1e-6f), $"sign {sign}: tangential motion was limited.");
                    last = moved;
                    p = next;
                }
                Assert.That(Along(p), Is.GreaterThan(cap - band * 0.1f), $"sign {sign}: never got close to the cap.");

                // A step back away from the cap is untouched, even right at it.
                Vector3 atCap = start + n * cap;
                Assert.That(Along(limit(atCap, atCap - n * step, start, Vector3.up, sign * cap, band)),
                    Is.EqualTo(cap - step).Within(1e-6f), $"sign {sign}: a step away from the cap was limited.");

                // One step big enough to jump the whole band still stops at the cap (the hard safety net).
                Assert.That(Along(limit(low, low + n * 1f, start, Vector3.up, sign * cap, band)),
                    Is.LessThanOrEqualTo(cap + 1e-6f), $"sign {sign}: a large step jumped the cap.");
            }
        }

        /// Scrubbing back and forth drives the middle of a Clay stroke up to its per-stroke cap. With a
        /// hard clamp that flattened ~230 vertices onto the cap at this density, with a slope break
        /// (a faint shelf) where the plateau met the part still rising; eased in, none sit on it.
        [Test]
        public void ScrubbedClayStrokeRoundsOffInsteadOfPinningAtTheCap()
        {
            _controller.CurrentBrush = BrushType.Clay;
            _controller.BrushStrength = 0.5f;
            _controller.Accumulate = false;
            _controller.SurfaceRelax = 0f; // relax is not capped, and would blur what is measured
            _controller.UseAlpha = false;
            TestReflection.Invoke(_controller, "ResetClayStroke");
            var stroke = TestReflection.Bind<Action<Vector3, Vector3, bool>>(_controller, "ApplyClayStroke");
            const int framesPerPass = 30, passes = 8;
            for (int f = 0; f <= framesPerPass * passes; f++)
            {
                float x = Mathf.Lerp(-0.15f, 0.15f, Mathf.PingPong(f / (float)framesPerPass, 1f));
                Vector3 dir = new Vector3(x, 0f, -0.5f).normalized;
                TestReflection.Invoke(_controller, "MarkPositionMirrorStale");
                stroke(dir * SymmetricTestMesh.SurfaceRadius(dir), dir, true);
            }

            float cap = BrushRadius * _controller.ClayHeightFactor * 1.5f; // ClayStrokeDepthLimit
            Vector3[] v = _sculptable.Vertices;
            int pinned = 0;
            float peak = 0f;
            for (int i = 0; i < v.Length; i++)
            {
                float rise = Vector3.Dot(v[i] - _startVertices[i], _startVertices[i].normalized);
                peak = Mathf.Max(peak, rise);
                if (Mathf.Abs(rise - cap) < cap * 0.002f) pinned++;
            }
            Assert.That(peak, Is.GreaterThan(cap * 0.9f), "The stroke never got near its cap - the test proves nothing.");
            Assert.That(pinned, Is.LessThan(10), $"{pinned} vertices sit flat on the cap.");
        }

        [Test]
        public void FalloffCurvePassesThroughItsPointsWithoutOvershoot()
        {
            foreach (FalloffPreset preset in (FalloffPreset[])Enum.GetValues(typeof(FalloffPreset)))
            {
                BrushFalloffCurve curve = BrushFalloffCurve.Preset(preset);
                foreach (Vector2 p in curve.Points)
                    Assert.That(curve.Evaluate(p.x), Is.EqualTo(p.y).Within(1e-5f), $"{preset} misses its point {p}");
                // Every preset falls from centre to edge; a monotone interpolant must never rise.
                float last = curve.Evaluate(0f);
                for (int i = 1; i <= 200; i++)
                {
                    float y = curve.Evaluate(i / 200f);
                    Assert.That(y, Is.LessThanOrEqualTo(last + 1e-6f), $"{preset} rises at {i / 200f:F3}");
                    Assert.That(y, Is.InRange(0f, 1f));
                    last = y;
                }
            }

            BrushFalloff.SetActive(BrushFalloffCurve.Preset(FalloffPreset.Plateau));
            try
            {
                // The table reproduces the curve (t01 is 1 at the centre).
                Assert.That(BrushFalloff.Apply(1f - 0.3f, -1f), Is.EqualTo(1f).Within(1e-3f));
                Assert.That(BrushFalloff.Apply(0f, -1f), Is.EqualTo(0f).Within(1e-3f));
            }
            finally
            {
                BrushFalloff.SetActive(null);
            }
            Assert.That(BrushFalloff.Apply(0.5f, 0.123f), Is.EqualTo(0.123f), "Inactive, the built-in falloff must pass through untouched.");
        }

        private static int Count(int[] indices, int firstOfB)
        {
            int n = 0;
            foreach (int i in indices) if (i >= firstOfB) n++;
            return n;
        }

        private static Mesh BuildTwoSpheres()
        {
            Mesh a = SymmetricTestMesh.BuildIcosphere(4, out _);
            Vector3[] va = a.vertices;
            int[] ta = a.triangles;
            int n = va.Length;
            var verts = new List<Vector3>(n * 2);
            var tris = new List<int>(ta.Length * 2);
            // Scale to radius 0.2, centre A at x = -0.22 and B at x = +0.22 - a 0.04 gap.
            for (int i = 0; i < n; i++) verts.Add(va[i] * 0.4f + new Vector3(-0.22f, 0f, 0f));
            for (int i = 0; i < n; i++) verts.Add(va[i] * 0.4f + new Vector3(0.22f, 0f, 0f));
            tris.AddRange(ta);
            foreach (int t in ta) tris.Add(t + n);
            Object.DestroyImmediate(a);
            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            return mesh;
        }
    }
}
