using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Sculpting.Tests
{
    /// Frame time of a fast, curved Clay stroke on a ~500k-triangle sphere, fed either one pointer
    /// sample per frame (what the brush saw before it read the Input System's event history) or
    /// every sample a 1000 Hz mouse delivers in a 60 Hz frame. A "frame" is what HandleClayInput
    /// does with the button held: the hover raycast, a raycast per sample that becomes a knot,
    /// the stroke curve and the dabs. Not a pass/fail test. Explicit: never runs in a normal suite.
    [Explicit, Category("Benchmark")]
    public class ClayFastStrokeBenchmark
    {
        private const BindingFlags AnyMember = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int Frames = 90;
        private const int SamplesPerFrame = 16; // 1000 Hz pointer, 60 Hz frames
        private const float FrameDt = 1f / 60f;
        private static readonly Vector3 CameraPosition = new Vector3(0f, 0f, -2.2f);

        private GameObject _meshObject, _controllerObject, _cameraObject;
        private Mesh _sourceMesh;
        private SculptableMesh _sculptable;
        private SculptController _controller;
        private Camera _camera;
        private Vector3[] _start;

        [OneTimeSetUp]
        public void CreateObjects()
        {
            _sourceMesh = UvSphere(360, 700, 0.5f); // 502,600 triangles
            _meshObject = new GameObject("FastStrokeMesh") { hideFlags = HideFlags.HideAndDontSave };
            _meshObject.AddComponent<MeshFilter>().sharedMesh = _sourceMesh;
            _meshObject.AddComponent<MeshRenderer>();
            _sculptable = _meshObject.AddComponent<SculptableMesh>();
            TestReflection.SetField(_sculptable, "useMeshCollider", false);
            if (_sculptable.Vertices == null) TestReflection.Invoke(_sculptable, "Awake");
            var mirror = _meshObject.AddComponent<MirrorController>();

            _cameraObject = new GameObject("FastStrokeCamera") { hideFlags = HideFlags.HideAndDontSave };
            _cameraObject.transform.position = CameraPosition;
            _cameraObject.transform.LookAt(Vector3.zero);
            _camera = _cameraObject.AddComponent<Camera>();
            _camera.enabled = false;

            _controllerObject = new GameObject("FastStrokeController") { hideFlags = HideFlags.HideAndDontSave };
            _controller = _controllerObject.AddComponent<SculptController>();
            TestReflection.SetField(_controller, "sculptableMesh", _sculptable);
            TestReflection.SetField(_controller, "mirrorController", mirror);
            TestReflection.SetField(_controller, "cam", _camera);
            _start = (Vector3[])_sculptable.Vertices.Clone();
        }

        [OneTimeTearDown]
        public void DestroyObjects()
        {
            TestReflection.Invoke(_controller, "ReleaseNativeResources");
            TestReflection.Invoke(_sculptable, "ReleaseNativeResources");
            if (_sculptable.Mesh != null && _sculptable.Mesh != _sourceMesh) Object.DestroyImmediate(_sculptable.Mesh);
            Object.DestroyImmediate(_controllerObject);
            Object.DestroyImmediate(_cameraObject);
            Object.DestroyImmediate(_meshObject);
            Object.DestroyImmediate(_sourceMesh);
        }

        /// travel: brush radii per frame - 3 is a brisk flick.
        [Test]
        public void FastCurvedStroke([Values(0.05f, 0.15f)] float radius, [Values(false, true)] bool everySample)
        {
            for (int rep = 0; rep < 3; rep++)
            {
                Array.Copy(_start, _sculptable.Vertices, _start.Length);
                _sculptable.ApplyVertices();
                _controller.CurrentBrush = BrushType.Clay;
                _controller.BrushRadius = radius;
                _controller.BrushStrength = 0.5f;
                _controller.UseBurstJobs = true;
                TestReflection.Invoke(_controller, "ResetClayStroke");
                _sculptable.PrepareSpatialIndex(Mathf.Max(radius * 0.5f, 0.01f));
                _sculptable.BeginStrokeUndo();

                var single = TestReflection.Bind<Action<Vector3, Vector3, bool>>(_controller, "ApplyClayStroke");
                var sampled = TestReflection.Bind<Action<Vector3, Vector3, bool>>(_controller, "ApplyClaySamples");
                var samples = (IList)TestReflection.GetField(_controller, "_strokeSamples");
                Type sampleType = typeof(SculptController).GetNestedType("FrameSample", AnyMember);
                FieldInfo screenField = sampleType.GetField("Screen"), pressureField = sampleType.GetField("Pressure"), timeField = sampleType.GetField("Time");

                var ms = new List<double>(Frames);
                var sw = new Stopwatch();
                float loopRadius = 0.5f * Mathf.Sin(28f * Mathf.Deg2Rad);
                float anglePerFrame = 3f * radius / loopRadius;
                int hits = 0;
                for (int f = 0; f < Frames; f++)
                {
                    Vector3 target = LoopPoint(f * anglePerFrame);
                    if (everySample)
                    {
                        // The samples of this frame, untimed: evenly along the loop since the last frame.
                        samples.Clear();
                        int count = f == 0 ? 1 : SamplesPerFrame;
                        for (int s = 1; s <= count; s++)
                        {
                            float a = f == 0 ? 0f : (f - 1 + s / (float)count) * anglePerFrame;
                            object sample = Activator.CreateInstance(sampleType);
                            screenField.SetValue(sample, (Vector2)_camera.WorldToScreenPoint(LoopPoint(a)));
                            pressureField.SetValue(sample, 1f);
                            timeField.SetValue(sample, (double)((f - 1 + s / (float)count) * FrameDt));
                            samples.Add(sample);
                        }
                    }

                    sw.Restart();
                    TestReflection.Invoke(_controller, "MarkPositionMirrorStale");
                    Ray ray = everySample ? _camera.ScreenPointToRay((Vector2)screenField.GetValue(samples[samples.Count - 1]))
                        : new Ray(CameraPosition, (target - CameraPosition).normalized);
                    if (_sculptable.RaycastMesh(ray, 1000f, out Vector3 hit, out Vector3 n))
                    {
                        hits++;
                        if (everySample) sampled(hit, n, true);
                        else single(hit, n, true);
                    }
                    ms.Add(sw.Elapsed.TotalMilliseconds);
                }
                _sculptable.RefreshStrokeNormalsAndCurvature();
                _sculptable.EndStrokeUndo();

                ms.RemoveAt(0);
                ms.Sort();
                double sum = 0;
                foreach (double m in ms) sum += m;
                TestContext.WriteLine(
                    $"[BENCH] tris={_sculptable.TriangleCount} r={radius:F2} {(everySample ? $"{SamplesPerFrame} samples/frame" : "1 sample/frame ")} rep={rep} | " +
                    $"mean {sum / ms.Count:F2}ms median {ms[ms.Count / 2]:F2}ms p95 {ms[(int)(ms.Count * 0.95f)]:F2}ms | hits {hits}/{Frames} screen {_camera.pixelWidth}x{_camera.pixelHeight}");
                Assert.That(hits, Is.EqualTo(Frames), "The stroke left the mesh - the numbers mean nothing.");
            }
        }

        private static Vector3 LoopPoint(float angle)
        {
            float a = 28f * Mathf.Deg2Rad;
            Vector3 dir = new Vector3(Mathf.Sin(a) * Mathf.Cos(angle), 0.1f + Mathf.Sin(a) * Mathf.Sin(angle), -Mathf.Cos(a));
            return dir.normalized * 0.5f;
        }

        private static Mesh UvSphere(int rings, int segments, float r)
        {
            var v = new List<Vector3>((rings + 1) * segments);
            var t = new List<int>(rings * segments * 6);
            for (int i = 0; i <= rings; i++)
            {
                float th = Mathf.PI * i / rings;
                for (int j = 0; j < segments; j++)
                {
                    float ph = 2f * Mathf.PI * j / segments;
                    v.Add(new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph)) * r);
                }
            }
            for (int i = 0; i < rings; i++)
            for (int j = 0; j < segments; j++)
            {
                int a = i * segments + j, b = i * segments + (j + 1) % segments;
                int c = a + segments, d = b + segments;
                if (i > 0) { t.Add(a); t.Add(b); t.Add(c); }
                if (i < rings - 1) { t.Add(b); t.Add(d); t.Add(c); }
            }
            var m = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(v);
            m.SetTriangles(t, 0);
            m.RecalculateNormals();
            m.RecalculateBounds();
            return m;
        }
    }
}
