using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Sculpting.Tests
{
    /// Times whole brush strokes through the same entry points the input handlers call, frame by
    /// frame, at the two densities this app is actually used at. Not a pass/fail test - it reports
    /// mean / p95 / worst frame, the stroke-start frame and the release, so a change to the brush
    /// pipeline can be judged by numbers rather than by feel. Explicit: never runs in a normal suite.
    ///
    /// A "frame" here is what HandleSculptInput does for one Update with the button held: mark the
    /// position mirror stale, raycast the stroke point, apply the brush. The release is
    /// HandleStrokeEndCommit's calls. Rendering and the GPU are not included.
    [TestFixture(7)]
    [TestFixture(8)]
    [Explicit, Category("Benchmark")]
    public class BrushStrokeBenchmarks
    {
        private const BindingFlags AnyMember =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private const int FramesPerStroke = 90;
        private const int RadialFrames = 10;
        private const float FrameDt = 1f / 60f;
        // The stroke loops around this cone about the view axis, on the camera-facing side.
        private const float LoopHalfAngleDegrees = 28f;
        private static readonly Vector3 CameraPosition = new Vector3(0f, 0f, -2.2f);

        private readonly int _subdivisions;
        private GameObject _meshObject, _controllerObject, _cameraObject;
        private Mesh _sourceMesh;
        private SculptableMesh _sculptable;
        private SculptController _controller;
        private MirrorController _mirror;
        private Vector3[] _startVertices;

        public BrushStrokeBenchmarks(int subdivisions) => _subdivisions = subdivisions;

        [OneTimeSetUp]
        public void CreateObjects()
        {
            _sourceMesh = SculptControllerJobParityTests.BuildNoisyIcosphere(_subdivisions);

            _meshObject = new GameObject("BenchmarkMesh") { hideFlags = HideFlags.HideAndDontSave };
            _meshObject.AddComponent<MeshFilter>().sharedMesh = _sourceMesh;
            _meshObject.AddComponent<MeshRenderer>();
            _sculptable = _meshObject.AddComponent<SculptableMesh>();
            SetField(_sculptable, "useMeshCollider", false);
            if (_sculptable.Vertices == null) Invoke(_sculptable, "Awake");
            _mirror = _meshObject.AddComponent<MirrorController>();

            _cameraObject = new GameObject("BenchmarkCamera") { hideFlags = HideFlags.HideAndDontSave };
            _cameraObject.transform.position = CameraPosition;
            _cameraObject.transform.LookAt(Vector3.zero);
            Camera camera = _cameraObject.AddComponent<Camera>();
            camera.enabled = false;

            _controllerObject = new GameObject("BenchmarkController") { hideFlags = HideFlags.HideAndDontSave };
            _controller = _controllerObject.AddComponent<SculptController>();
            SetField(_controller, "sculptableMesh", _sculptable);
            SetField(_controller, "mirrorController", _mirror);
            SetField(_controller, "cam", camera);

            _startVertices = (Vector3[])_sculptable.Vertices.Clone();
        }

        [OneTimeTearDown]
        public void DestroyObjects()
        {
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

        // ------------------------------------------------------------------------ benchmarks

        /// radius: brush radius on a 0.5-radius sphere. travel: cursor travel per frame in brush
        /// radii (0.5 = a slow drag, 2 = a brisk one). mirrorX: X symmetry on; the loop crosses the
        /// plane twice per lap, so the near-plane ordering path is exercised.
        [Test]
        public void Stroke([Values("Clay", "Crease", "Inflate", "Flatten", "Smooth")] string brush,
            [Values(0.05f, 0.15f)] float radius,
            [Values(0.5f, 2f)] float travel,
            [Values(false, true)] bool mirrorX)
        {
            var timing = RunStroke(brush, radius, travel, mirrorX, surfaceRelax: null);
            Report($"{brush,-7} r={radius:F2} travel={travel:F1}r mirrorX={(mirrorX ? "on " : "off")}", timing);
        }

        /// Radial symmetry about the view (Z) axis, which the stroke loop circles: with the large
        /// brush every copy's footprint meets its neighbours', so the simultaneous path of the
        /// symmetric dab walk (apply, bank, restore per copy - see SculptController.MirroredDabWalk)
        /// runs on every dab.
        [Test]
        public void RadialStroke([Values("Clay", "Crease", "Inflate")] string brush,
            [Values(0.05f, 0.15f)] float radius,
            [Values(6, 8, 12, 16)] int count)
        {
            _mirror.Radial = true;
            _mirror.RadialAxisChoice = RadialAxis.Z;
            _mirror.RadialCount = count;
            try
            {
                // Short, and run a case or two at a time: an early design that applied
                // orderings^2 copies per dab near the axis cost hundreds of milliseconds a frame
                // here, and a full 90-frame run of every case once froze the machine for minutes.
                var timing = RunStroke(brush, radius, 2f, false, surfaceRelax: null, frames: RadialFrames);
                Report($"{brush,-7} r={radius:F2} radial={count,2}", timing);
            }
            finally
            {
                _mirror.Radial = false;
            }
        }

        /// How far ONE pass of each brush moves the surface at the default strength, in brush radii,
        /// along a straight stroke drawn at three speeds (travel per frame, in radii). Every brush is
        /// distance-paced, so the three speeds should agree; the numbers also show how the brushes
        /// compare in strength. Mirror off, default per-brush settings.
        [Test]
        public void PassStrength([Values("Clay", "Crease", "Inflate", "Flatten", "Smooth", "Standard", "Layer")] string brush,
            [Values(0.05f, 0.15f)] float radius,
            [Values(0.25f, 1f, 3f)] float travel)
        {
            RestoreGeometry();
            _mirror.MirrorX = false;
            _controller.CurrentBrush = (BrushType)Enum.Parse(typeof(BrushType), brush);
            _controller.BrushRadius = radius;
            _controller.BrushStrength = 0.1f;
            ResetStrokeContinuity();
            _sculptable.PrepareSpatialIndex(Mathf.Max(radius * 0.5f, 0.01f));
            _sculptable.BeginStrokeUndo();

            // A straight line eight radii long (capped to stay on the sphere) across its camera-facing side.
            float length = Mathf.Min(radius * 8f, 0.6f);
            int frames = Mathf.CeilToInt(length / (travel * radius)) + 1;
            int hits = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                float x = -length * 0.5f + Mathf.Min(frame * travel * radius, length);
                var ray = new Ray(new Vector3(x, 0.05f, -2f), Vector3.forward);
                Invoke(_controller, "MarkPositionMirrorStale");
                if (!_sculptable.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal)) continue;
                hits++;
                ApplyBrush(brush, hitPoint, hitNormal);
            }
            _sculptable.RefreshStrokeNormalsAndCurvature();
            _sculptable.EndStrokeUndo();

            TestContext.Out.WriteLine(
                $"[PASS] tris={_sculptable.TriangleCount} {brush,-7} r={radius:F2} travel={travel:F2}r frames={frames} " +
                $"| peak displacement {MaxDisplacement() / radius:F3} radii");
            Assert.That(hits, Is.EqualTo(frames), "The stroke left the mesh - the number means nothing.");
        }

        /// Clay with Surface Relax off, to split the relax pass out of Clay's frame.
        [Test]
        public void ClayWithoutRelax([Values(0.05f, 0.15f)] float radius, [Values(0.5f, 2f)] float travel)
        {
            var timing = RunStroke("Clay", radius, travel, false, surfaceRelax: 0f);
            Report($"Clay-norelax r={radius:F2} travel={travel:F1}r", timing);
        }

        /// The fixed per-frame costs, isolated: the full-mesh position-mirror copy and one
        /// ApplyVerticesLocal over a footprint-sized dirty set.
        [Test]
        public void FixedCosts([Values(0.05f, 0.15f)] float radius)
        {
            RestoreGeometry();
            var sw = new Stopwatch();
            const int reps = 20;

            MethodInfo refresh = typeof(SculptController).GetMethod("RefreshPositionMirror", AnyMember);
            Invoke(_controller, "EnsureSmoothFullMeshScratch", _sculptable.Vertices.Length);
            double mirrorMs = 0;
            for (int r = 0; r < reps; r++)
            {
                Invoke(_controller, "MarkPositionMirrorStale");
                sw.Restart();
                refresh.Invoke(_controller, new object[] { _sculptable.Vertices });
                mirrorMs += sw.Elapsed.TotalMilliseconds;
            }

            _sculptable.PrepareSpatialIndex(Mathf.Max(radius * 0.5f, 0.01f));
            Vector3 point = new Vector3(0f, 0f, -1f) * 0.5f;
            var footprint = new List<int>(_sculptable.QueryNear(point, radius));
            double queryMs = 0, applyMs = 0;
            for (int r = 0; r < reps; r++)
            {
                sw.Restart();
                _sculptable.QueryNear(point, radius);
                queryMs += sw.Elapsed.TotalMilliseconds;
                sw.Restart();
                _sculptable.ApplyVerticesLocal(footprint);
                applyMs += sw.Elapsed.TotalMilliseconds;
            }

            double prepMs = 0;
            for (int r = 0; r < 3; r++)
            {
                // Alternate the cell size so every call really rebuilds.
                sw.Restart();
                _sculptable.PrepareSpatialIndex(Mathf.Max((r % 2 == 0 ? radius * 2f : radius) * 0.5f, 0.01f));
                prepMs += sw.Elapsed.TotalMilliseconds;
            }

            TestContext.Out.WriteLine(
                $"[BENCH] tris={_sculptable.TriangleCount} r={radius:F2} footprint={footprint.Count} verts | " +
                $"positionMirrorCopy {mirrorMs / reps:F2}ms  QueryNear {queryMs / reps:F3}ms  " +
                $"ApplyVerticesLocal {applyMs / reps:F2}ms  PrepareSpatialIndex(rebuild) {prepMs / 3:F1}ms");
        }

        // ------------------------------------------------------------------------ stroke driver

        private struct StrokeTiming
        {
            public double StartMs, MeanMs, P95Ms, WorstMs, ReleaseMs;
            public int Hits, Frames, GridRebuilds, Collections;
            public float MaxDisplacement;
        }

        private StrokeTiming RunStroke(string brush, float radius, float travel, bool mirrorX, float? surfaceRelax,
            int frames = FramesPerStroke)
        {
            RestoreGeometry();
            _mirror.MirrorX = mirrorX;
            _controller.BrushRadius = radius;
            _controller.BrushStrength = 0.5f;
            _controller.CurrentBrush = (BrushType)Enum.Parse(typeof(BrushType), brush);
            float relaxBefore = (float)GetField(_controller, "surfaceRelax");
            if (surfaceRelax.HasValue) SetField(_controller, "surfaceRelax", surfaceRelax.Value);
            ResetStrokeContinuity();

            var sw = new Stopwatch();
            var frameMs = new List<double>(frames);
            double startMs = 0;
            int hits = 0;
            Camera camera = _cameraObject.GetComponent<Camera>();

            // RestoreGeometry's full ApplyVertices drops the vertex index (as Remesh does); build it
            // here, untimed, so "start" measures an ordinary press rather than a post-remesh one.
            _sculptable.PrepareSpatialIndex(Mathf.Max(radius * 0.5f, 0.01f));

            int rebuildsBefore = _sculptable.TriangleGridRebuilds;
            int collectionsBefore = GC.CollectionCount(0);
            // Stroke start - HandleSculptInput's press-frame block.
            sw.Restart();
            _sculptable.PrepareSpatialIndex(Mathf.Max(radius * 0.5f, 0.01f));
            _sculptable.BeginStrokeUndo();
            startMs = sw.Elapsed.TotalMilliseconds;

            float travelPerFrame = travel * radius;
            float loopRadius = 0.5f * Mathf.Sin(LoopHalfAngleDegrees * Mathf.Deg2Rad);
            for (int frame = 0; frame < frames; frame++)
            {
                float angle = frame * travelPerFrame / loopRadius;
                Vector3 target = LoopPoint(angle);
                var ray = new Ray(CameraPosition, (target - CameraPosition).normalized);

                sw.Restart();
                Invoke(_controller, "MarkPositionMirrorStale");
                if (_sculptable.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal))
                {
                    hits++;
                    ApplyBrush(brush, hitPoint, hitNormal);
                }
                frameMs.Add(sw.Elapsed.TotalMilliseconds);
                if (frame == 0) startMs += frameMs[0];
            }

            // Release - HandleStrokeEndCommit.
            sw.Restart();
            _sculptable.RefreshStrokeNormalsAndCurvature();
            _sculptable.EndStrokeUndo();
            double releaseMs = sw.Elapsed.TotalMilliseconds;

            SetField(_controller, "surfaceRelax", relaxBefore);
            _mirror.MirrorX = false;

            frameMs.RemoveAt(0); // counted in StartMs
            frameMs.Sort();
            double sum = 0;
            foreach (double ms in frameMs) sum += ms;
            return new StrokeTiming
            {
                StartMs = startMs,
                MeanMs = sum / frameMs.Count,
                P95Ms = frameMs[(int)(frameMs.Count * 0.95f)],
                WorstMs = frameMs[frameMs.Count - 1],
                ReleaseMs = releaseMs,
                Hits = hits,
                Frames = frames,
                GridRebuilds = _sculptable.TriangleGridRebuilds - rebuildsBefore,
                MaxDisplacement = MaxDisplacement(),
                Collections = GC.CollectionCount(0) - collectionsBefore,
            };
        }

        private void ApplyBrush(string brush, Vector3 hitPoint, Vector3 hitNormal)
        {
            switch (brush)
            {
                case "Clay":
                    Invoke(_controller, "ApplyClayStroke", hitPoint, hitNormal, true);
                    break;
                case "Crease":
                    Invoke(_controller, "ApplyCarveStroke", hitPoint, hitNormal, true);
                    break;
                case "Inflate":
                case "Flatten":
                case "Standard":
                case "Layer":
                    Delegate local = Delegate.CreateDelegate(typeof(Action<Vector3, Vector3, bool, float>), _controller,
                        typeof(SculptController).GetMethod("Apply" + brush + "BrushLocal", AnyMember));
                    // What HandleSculptInput passes for each - see its dispatch.
                    bool holdsBuild = brush == "Inflate" || brush == "Standard";
                    Invoke(_controller, "ApplyStandardStroke", hitPoint, hitNormal, true, FrameDt, local, DabTimeQuantum,
                        HoldMode(holdsBuild ? "BuildUpOnHold" : "AlwaysWorks"), brush == "Standard" || brush == "Layer");
                    break;
                case "Smooth":
                    Invoke(_controller, "ApplySmoothStroke", hitPoint, hitNormal, FrameDt);
                    break;
            }
        }

        private static float DabTimeQuantum =>
            (float)typeof(SculptController).GetField("DabTimeQuantum", AnyMember).GetValue(null);

        private static object HoldMode(string name) =>
            Enum.Parse(typeof(SculptController).GetNestedType("DabHoldMode", AnyMember), name);

        private float MaxDisplacement()
        {
            Vector3[] verts = _sculptable.Vertices;
            float max = 0f;
            for (int i = 0; i < _startVertices.Length; i++) max = Mathf.Max(max, (verts[i] - _startVertices[i]).magnitude);
            return max;
        }

        private static Vector3 LoopPoint(float angle)
        {
            float a = LoopHalfAngleDegrees * Mathf.Deg2Rad;
            // A cone about -Z (towards the camera), tilted up a little so it is not symmetric about
            // the equator; crosses x = 0 twice per lap.
            Vector3 dir = new Vector3(Mathf.Sin(a) * Mathf.Cos(angle), 0.1f + Mathf.Sin(a) * Mathf.Sin(angle), -Mathf.Cos(a));
            return dir.normalized * 0.5f;
        }

        private void ResetStrokeContinuity()
        {
            Invoke(_controller, "ResetClayStroke");
            SetField(_controller, "_lastCarveStrokeLocal", null);
            Invoke(_controller, "ResetDabStroke");
        }

        private void RestoreGeometry()
        {
            Array.Copy(_startVertices, _sculptable.Vertices, _startVertices.Length);
            _sculptable.ApplyVertices();
            Invoke(_controller, "MarkPositionMirrorStale");
        }

        private void Report(string label, StrokeTiming t)
        {
            TestContext.Out.WriteLine(
                $"[BENCH] tris={_sculptable.TriangleCount} {label} | frame mean {t.MeanMs:F2}ms p95 {t.P95Ms:F2}ms " +
                $"worst {t.WorstMs:F2}ms | start {t.StartMs:F1}ms release {t.ReleaseMs:F1}ms | hits {t.Hits}/{t.Frames} " +
                $"| triGridRebuilds {t.GridRebuilds} GCs {t.Collections} maxDisp {t.MaxDisplacement:F3}");
            Assert.That(t.Hits, Is.GreaterThan(t.Frames / 2), "The stroke mostly missed the mesh - the numbers mean nothing.");
        }

        // ------------------------------------------------------------------------ reflection

        private static FieldInfo FieldOf(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo field = t.GetField(name, AnyMember);
                if (field != null) return field;
            }
            Assert.Fail($"{type.Name}.{name} not found - renamed? Update this benchmark.");
            return null;
        }

        private static object GetField(object target, string name) => FieldOf(target.GetType(), name).GetValue(target);

        private static void SetField(object target, string name, object value) => FieldOf(target.GetType(), name).SetValue(target, value);

        private static readonly Dictionary<(Type, string, int), MethodInfo> MethodCache = new Dictionary<(Type, string, int), MethodInfo>();

        private static void Invoke(object target, string name, params object[] args) => Invoke(target, name, args, false);

        private static void Invoke(object target, string name, object[] args, bool optional)
        {
            var key = (target.GetType(), name, args.Length);
            if (!MethodCache.TryGetValue(key, out MethodInfo method))
            {
                foreach (MethodInfo m in target.GetType().GetMethods(AnyMember))
                    if (m.Name == name && m.GetParameters().Length == args.Length) { method = m; break; }
                MethodCache[key] = method;
            }
            if (method == null)
            {
                if (optional) return;
                Assert.Fail($"{target.GetType().Name}.{name} ({args.Length} args) not found - renamed? Update this benchmark.");
            }
            method.Invoke(target, args);
        }
    }
}
