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
        [Test]
        public void ClayProgramMatchesThePerDabPath([Values(true, false)] bool accumulate, [Values(true, false)] bool mirror,
            [Values(12, 60)] int framesPerSweep)
        {
            Vector3[] batched = ClayStroke(jobs: true, framesPerSweep, accumulate, mirror);
            ResetState();
            Vector3[] perDab = ClayStroke(jobs: false, framesPerSweep, accumulate, mirror);

            float moved = 0f, diff = 0f;
            for (int i = 0; i < batched.Length; i++)
            {
                moved = Mathf.Max(moved, (perDab[i] - _startVertices[i]).magnitude);
                diff = Mathf.Max(diff, (perDab[i] - batched[i]).magnitude);
            }
            Assert.That(moved, Is.GreaterThan(1e-3f), "The stroke barely moved the mesh.");
            Assert.That(diff, Is.LessThan(1e-5f), $"Batched Clay differs from per-dab Clay by {diff:E2} (stroke moved {moved:E2}).");
        }

        private Vector3[] ClayStroke(bool jobs, int framesPerSweep, bool accumulate, bool mirror)
        {
            _mirror.MirrorX = mirror;
            // Brush first: strength and Accumulate are remembered per brush, so setting them before
            // the switch would set them on the previous brush instead.
            _controller.CurrentBrush = BrushType.Clay;
            _controller.BrushStrength = 0.5f;
            _controller.Accumulate = accumulate;
            _controller.SurfaceRelax = 0f;
            _controller.UseAlpha = false;
            bool before = _controller.UseBurstJobs;
            _controller.UseBurstJobs = jobs;
            try
            {
                TestReflection.SetField(_controller, "_lastClayStrokeLocal", null);
                TestReflection.SetField(_controller, "_lastClayStrokeNormalLocal", null);
                var stroke = TestReflection.Bind<Action<Vector3, Vector3, bool>>(_controller, "ApplyClayStroke");
                for (int f = 0; f <= framesPerSweep * 2; f++)
                {
                    // Across the X plane and back.
                    float x = Mathf.Lerp(-0.15f, 0.15f, Mathf.PingPong(f / (float)framesPerSweep, 1f));
                    Vector3 dir = new Vector3(x, 0.08f, -0.45f).normalized;
                    Vector3 point = dir * SymmetricTestMesh.SurfaceRadius(dir);
                    TestReflection.Invoke(_controller, "MarkPositionMirrorStale");
                    stroke(point, dir, true);
                }
                return (Vector3[])_sculptable.Vertices.Clone();
            }
            finally
            {
                _controller.UseBurstJobs = before;
            }
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
