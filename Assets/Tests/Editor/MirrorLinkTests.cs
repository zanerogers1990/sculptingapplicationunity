using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Sculpting.Tests
{
    /// Mirror Separate and Mirror Linked (see MeshMirror and MirrorLink).
    ///
    /// The first test is the reported bug: a mirrored Unity sphere looked lower-poly than its
    /// original with identical vertex and triangle counts, because the copy's normals were
    /// recalculated and every duplicated seam vertex got a one-sided average - hard creases along
    /// the UV seam and at the poles. The rest pin the link down: every vertex edit on either half
    /// lands on the other exactly, transforms follow whichever half moved, and a topology change
    /// finalizes the pair.
    ///
    /// Lives in Assembly-CSharp-Editor like the other suites here, so SculptableMesh's private
    /// members are reached by reflection.
    public class MirrorLinkTests
    {
        private static readonly Vector3 PlaneCenter = new Vector3(0.1f, 1f, -0.2f);

        // The primitive's own seam normals agree exactly, which Vector3.Angle still reports as a
        // few hundredths of a degree through its acos. The bug put them 17.5 degrees apart.
        private const float SeamToleranceDegrees = 0.1f;
        private const float NormalTolerance = 1e-5f;
        private const BindingFlags AnyMember =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private GameObject _sphere;
        private Mesh _sourceMesh, _twinMesh;
        private GameObject _sourceObject, _twinObject;
        private SculptableMesh _source, _twin;
        private MirrorLink _link;
        private Vector3 _signs;

        [TearDown]
        public void DestroyObjects()
        {
            DestroySculptable(_source, _sourceObject);
            DestroySculptable(_twin, _twinObject);
            if (_sourceMesh != null) Object.DestroyImmediate(_sourceMesh);
            if (_twinMesh != null) Object.DestroyImmediate(_twinMesh);
            if (_sphere != null) Object.DestroyImmediate(_sphere);
            // The undo test pushes real steps, which must not outlive the objects they name.
            EditHistory.Clear();
        }

        // ------------------------------------------------------------------------ the copy

        /// What the user saw: the same vertex and triangle counts, a different-looking surface.
        [Test]
        public void MirroredSphereKeepsItsSeamsSmooth()
        {
            _sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphere.hideFlags = HideFlags.HideAndDontSave;
            Mesh sphere = _sphere.GetComponent<MeshFilter>().sharedMesh;
            Vector3[] vertices = sphere.vertices;
            Vector3[] normals = sphere.normals;
            int[] triangles = sphere.triangles;
            Assert.That(MaxSeamAngle(vertices, normals), Is.LessThan(SeamToleranceDegrees), "the primitive itself should be seamless");
            Assert.That(CountFacesAgainstNormals(vertices, normals, triangles), Is.Zero, "winding check misreads the primitive");

            foreach (Vector3 signs in AllAxisSigns())
            {
                MeshMirror.ReflectGeometry(vertices, normals, triangles, signs,
                                           out Vector3[] mirroredVertices, out Vector3[] mirroredNormals, out int[] mirroredTriangles);

                Assert.That(mirroredVertices.Length, Is.EqualTo(vertices.Length), $"{signs}: vertex count");
                Assert.That(mirroredTriangles.Length, Is.EqualTo(triangles.Length), $"{signs}: triangle count");
                Assert.That(mirroredNormals, Is.Not.Null, $"{signs}: normals dropped");
                Assert.That(MaxSeamAngle(mirroredVertices, mirroredNormals), Is.LessThan(SeamToleranceDegrees), $"{signs}: seams split");
                Assert.That(CountFacesAgainstNormals(mirroredVertices, mirroredNormals, mirroredTriangles), Is.Zero,
                            $"{signs}: faces turned inside out");
            }
        }

        /// The copy is built in LOCAL space with a reflected transform. That has to put every point
        /// exactly where reflecting the original in WORLD space would, for every axis combination,
        /// with a rotation and a non-uniform scale in play.
        [Test]
        public void ReflectedTransformPlacesGeometryAtTheWorldReflection()
        {
            var position = new Vector3(0.7f, 1.3f, -0.4f);
            Quaternion rotation = Quaternion.Euler(23f, 131f, -47f);
            var scale = new Vector3(0.9f, 1.4f, 0.6f);
            Matrix4x4 original = Matrix4x4.TRS(position, rotation, scale);
            var localPoints = new[]
            {
                new Vector3(0.5f, 0.2f, -0.3f), new Vector3(-0.1f, 0.45f, 0.35f), new Vector3(0.05f, -0.5f, 0.12f),
            };

            foreach (Vector3 signs in AllAxisSigns())
            {
                Matrix4x4 copy = Matrix4x4.TRS(MeshMirror.ReflectPoint(position, PlaneCenter, signs),
                                               MeshMirror.ReflectRotation(rotation, signs), scale);
                foreach (Vector3 p in localPoints)
                {
                    Vector3 expected = MeshMirror.ReflectPoint(original.MultiplyPoint3x4(p), PlaneCenter, signs);
                    Vector3 actual = copy.MultiplyPoint3x4(Vector3.Scale(p, signs));
                    Assert.That(Vector3.Distance(expected, actual), Is.LessThan(1e-5f), $"signs {signs}, local point {p}");
                }
            }
        }

        // ------------------------------------------------------------------------ the link

        [Test]
        public void EveryVertexEditLandsOnTheOtherHalfExactly()
        {
            CreateLinkedPair(new Vector3(-1f, 1f, 1f));

            Vector3[] v = _source.Vertices;
            var dirty = new List<int>();
            for (int i = 0; i < v.Length; i += 7) { v[i] += new Vector3(0.013f, -0.021f, 0.008f); dirty.Add(i); }
            _source.ApplyVerticesLocal(dirty);
            AssertHalvesMirrored("brush-style apply on the original");

            v = _twin.Vertices;
            dirty.Clear();
            for (int i = 3; i < v.Length; i += 11) { v[i] += new Vector3(-0.017f, 0.009f, 0.02f); dirty.Add(i); }
            _twin.ApplyVerticesLocal(dirty);
            AssertHalvesMirrored("brush-style apply on the twin");

            v = _source.Vertices;
            for (int i = 0; i < v.Length; i++) v[i] *= 1.05f;
            _source.ApplyVertices();
            AssertHalvesMirrored("whole-mesh apply");
        }

        /// Brushes leave moves under the drift threshold unreported (see
        /// SculptableMesh.HasVisiblyDrifted), so stroke end is where those reach the other half.
        /// Undo and redo of the stroke have to follow as well.
        [Test]
        public void QuietStrokeMovesAndUndoReachTheOtherHalf()
        {
            // Two axes: a proper reflection, so the path with no winding flip is covered too.
            CreateLinkedPair(new Vector3(-1f, -1f, 1f));

            _source.BeginStrokeUndo();
            Vector3[] v = _source.Vertices;
            var reported = new List<int>();
            var touched = new List<int>();
            for (int i = 0; i < v.Length; i += 5)
            {
                _source.RecordUndoBeforeIfNeeded(i);
                touched.Add(i);
                if (i % 10 == 0) { v[i] += new Vector3(0.02f, 0.01f, -0.015f); reported.Add(i); }
                else v[i] += new Vector3(5e-7f, -5e-7f, 5e-7f);
            }
            _source.ApplyVerticesLocal(reported);
            _source.RefreshStrokeNormalsAndCurvature();
            _source.EndStrokeUndo();
            // Normals only where both halves just refreshed them: a neighbour of a quiet move that was
            // outside the stroke keeps the sub-threshold staleness the drift filter accepts by design.
            AssertHalvesMirrored("stroke end", touched);

            Assert.That(_source.ApplyUndoStep(), Is.True, "the stroke should be undoable");
            AssertHalvesMirrored("undo");
            Assert.That(_source.ApplyRedoStep(), Is.True, "the undo should be redoable");
            AssertHalvesMirrored("redo");
        }

        [Test]
        public void TransformsFollowWhicheverHalfMoved()
        {
            CreateLinkedPair(new Vector3(-1f, 1f, 1f));
            Transform original = _source.transform, twin = _twin.transform;

            original.SetPositionAndRotation(new Vector3(0.9f, 1.2f, 0.3f), Quaternion.Euler(10f, 40f, -25f));
            original.localScale = new Vector3(1.1f, 0.8f, 1.3f);
            _link.SyncTransforms();
            AssertTransformsMirrored("original moved");

            var twinTarget = new Vector3(-1.4f, 0.7f, -0.2f);
            twin.SetPositionAndRotation(twinTarget, Quaternion.Euler(-35f, 12f, 60f));
            twin.localScale = Vector3.one * 0.7f;
            _link.SyncTransforms();
            Assert.That(Vector3.Distance(twin.position, twinTarget), Is.LessThan(1e-5f), "the twin was pulled back instead of followed");
            AssertTransformsMirrored("twin moved");

            // Both halves dragged by the same offset, as one multi-selection: neither may be pulled
            // back, so the plane has to travel with them.
            var offset = new Vector3(0.25f, -0.1f, 0.05f);
            Vector3 originalTarget = original.position + offset;
            twinTarget = twin.position + offset;
            original.position = originalTarget;
            twin.position = twinTarget;
            _link.SyncTransforms();
            Assert.That(Vector3.Distance(original.position, originalTarget), Is.LessThan(1e-5f), "original pulled back");
            Assert.That(Vector3.Distance(twin.position, twinTarget), Is.LessThan(1e-5f), "twin pulled back");
            AssertTransformsMirrored("both moved together");
        }

        [Test]
        public void TopologyChangeFinalizesThePair()
        {
            CreateLinkedPair(new Vector3(-1f, 1f, 1f));
            Vector3[] twinBefore = (Vector3[])_twin.Vertices.Clone();

            // ReplaceGeometry releases the mesh it displaces with Destroy, which outside Play mode only
            // logs an error - not what this test is about (see SymmetryDriftTests' Cleanup test).
            LogAssert.ignoreFailingMessages = true;
            _source.ReplaceGeometry((Vector3[])_source.Vertices.Clone(), (Vector3[])_source.Normals.Clone(),
                                    (int[])_source.Triangles.Clone(), _source.Mesh.bounds);

            Assert.That(_source.LinkedMirror, Is.Null, "the original is still linked");
            Assert.That(_twin.LinkedMirror, Is.Null, "the twin is still linked");

            MoveEveryFourthVertexUp(_source);
            Assert.That(_twin.Vertices, Is.EqualTo(twinBefore), "a finalized twin must stop following");
        }

        [Test]
        public void FinalizedHalvesStopFollowing()
        {
            CreateLinkedPair(new Vector3(-1f, 1f, 1f));
            _link.Unlink();
            Assert.That(_source.LinkedMirror, Is.Null, "the original is still linked");
            Assert.That(_twin.LinkedMirror, Is.Null, "the twin is still linked");

            Vector3[] twinBefore = (Vector3[])_twin.Vertices.Clone();
            MoveEveryFourthVertexUp(_source);
            _source.ApplyVertices();
            Assert.That(_twin.Vertices, Is.EqualTo(twinBefore), "a finalized twin must stop following");
        }

        [Test]
        public void AnObjectBelongsToOnePairAtMost()
        {
            CreateLinkedPair(new Vector3(-1f, 1f, 1f));
            Assert.That(MirrorLink.Create(_source, _twin, PlaneCenter, _signs), Is.Null, "the same pair linked twice");
            Assert.That(MeshMirror.MirrorAcross(_twin, PlaneCenter, true, false, false, linked: true), Is.Null,
                        "a linked copy made of an object already in a pair");
        }

        // ------------------------------------------------------------------------ harness

        /// A lopsided sphere - Unity's own primitive, duplicated seam vertices and all - and its
        /// reflection, placed the way MeshMirror places a copy, then linked. Lopsided on every axis, so
        /// a "twin" that merely copied the original instead of reflecting it could not pass.
        private void CreateLinkedPair(Vector3 signs)
        {
            _signs = signs;
            _sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            _sphere.hideFlags = HideFlags.HideAndDontSave;
            Mesh sphere = _sphere.GetComponent<MeshFilter>().sharedMesh;

            Vector3[] vertices = sphere.vertices;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 p = vertices[i];
                vertices[i] = p + new Vector3(p.x > 0f ? 0.12f : 0f, p.y * p.y * 0.3f, p.z > 0.1f ? -0.07f : 0f);
            }
            Vector3[] normals = sphere.normals;
            int[] triangles = sphere.triangles;
            MeshMirror.ReflectGeometry(vertices, normals, triangles, signs,
                                       out Vector3[] twinVertices, out Vector3[] twinNormals, out int[] twinTriangles);

            _sourceMesh = BuildMesh("MirrorLinkTestsSource", vertices, normals, triangles);
            _twinMesh = BuildMesh("MirrorLinkTestsTwin", twinVertices, twinNormals, twinTriangles);
            _source = CreateSculptable(_sourceMesh, out _sourceObject);
            _twin = CreateSculptable(_twinMesh, out _twinObject);

            var position = new Vector3(0.6f, 1.1f, 0.15f);
            Quaternion rotation = Quaternion.Euler(5f, 30f, 10f);
            _sourceObject.transform.SetPositionAndRotation(position, rotation);
            _twinObject.transform.SetPositionAndRotation(MeshMirror.ReflectPoint(position, PlaneCenter, signs),
                                                         MeshMirror.ReflectRotation(rotation, signs));

            _link = MirrorLink.Create(_source, _twin, PlaneCenter, signs);
            Assert.That(_link, Is.Not.Null, "link refused");
        }

        private static void MoveEveryFourthVertexUp(SculptableMesh mesh)
        {
            Vector3[] v = mesh.Vertices;
            var dirty = new List<int>();
            for (int i = 0; i < v.Length; i += 4) { v[i] += Vector3.up * 0.05f; dirty.Add(i); }
            mesh.ApplyVerticesLocal(dirty);
        }

        /// Positions exactly - the link copies them, never recomputes them - and normals to a tolerance,
        /// either everywhere or over `normalsToCheck`.
        private void AssertHalvesMirrored(string stage, IReadOnlyList<int> normalsToCheck = null)
        {
            Vector3[] a = _source.Vertices, b = _twin.Vertices;
            Assert.That(b.Length, Is.EqualTo(a.Length), $"{stage}: vertex count");

            int positions = 0;
            string first = null;
            for (int i = 0; i < a.Length; i++)
            {
                Vector3 expected = Vector3.Scale(a[i], _signs);
                if (expected.Equals(b[i])) continue;
                positions++;
                first ??= $"vertex {i}: {b[i].ToString("F7")}, expected {expected.ToString("F7")}";
            }

            Vector3[] na = _source.Normals, nb = _twin.Normals;
            int normals = 0;
            int count = normalsToCheck != null ? normalsToCheck.Count : a.Length;
            for (int k = 0; k < count; k++)
            {
                int i = normalsToCheck != null ? normalsToCheck[k] : k;
                Vector3 expected = Vector3.Scale(na[i], _signs);
                if (Vector3.Distance(expected, nb[i]) <= NormalTolerance) continue;
                normals++;
                first ??= $"normal {i}: {nb[i].ToString("F7")}, expected {expected.ToString("F7")}";
            }

            Assert.That(positions + normals, Is.Zero,
                        $"{stage}: {positions} positions and {normals} normals not mirrored (of {a.Length}); first: {first}");
        }

        private void AssertTransformsMirrored(string stage)
        {
            Transform a = _source.transform, b = _twin.transform;
            var localPoints = new[] { Vector3.zero, new Vector3(0.4f, -0.2f, 0.3f), new Vector3(-0.3f, 0.5f, -0.1f) };
            foreach (Vector3 p in localPoints)
            {
                Vector3 expected = MeshMirror.ReflectPoint(a.TransformPoint(p), _link.Center, _signs);
                Vector3 actual = b.TransformPoint(Vector3.Scale(p, _signs));
                Assert.That(Vector3.Distance(expected, actual), Is.LessThan(1e-4f), $"{stage}: local point {p}");
            }
        }

        private static IEnumerable<Vector3> AllAxisSigns()
        {
            for (int mask = 1; mask < 8; mask++)
                yield return MeshMirror.AxisSigns((mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0);
        }

        /// Largest angle between the normals of vertices sharing one exact position: nothing for a
        /// smooth seam, and what RecalculateNormals pushed to 17.5 degrees.
        private static float MaxSeamAngle(Vector3[] vertices, Vector3[] normals)
        {
            var firstAt = new Dictionary<Vector3, int>(vertices.Length);
            float worst = 0f;
            for (int i = 0; i < vertices.Length; i++)
            {
                if (firstAt.TryGetValue(vertices[i], out int first))
                    worst = Mathf.Max(worst, Vector3.Angle(normals[first], normals[i]));
                else
                    firstAt.Add(vertices[i], i);
            }
            return worst;
        }

        /// Faces wound against their own corners' normals - what an improper reflection produces when
        /// the winding is not reversed along with it.
        private static int CountFacesAgainstNormals(Vector3[] vertices, Vector3[] normals, int[] triangles)
        {
            int against = 0;
            for (int t = 0; t + 2 < triangles.Length; t += 3)
            {
                int i0 = triangles[t], i1 = triangles[t + 1], i2 = triangles[t + 2];
                Vector3 face = Vector3.Cross(vertices[i1] - vertices[i0], vertices[i2] - vertices[i0]);
                if (face.sqrMagnitude < 1e-12f) continue;
                if (Vector3.Dot(face, normals[i0] + normals[i1] + normals[i2]) <= 0f) against++;
            }
            return against;
        }

        private static Mesh BuildMesh(string name, Vector3[] vertices, Vector3[] normals, int[] triangles)
        {
            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.triangles = triangles;
            mesh.RecalculateBounds();
            return mesh;
        }

        private static SculptableMesh CreateSculptable(Mesh mesh, out GameObject go)
        {
            go = new GameObject(mesh.name) { hideFlags = HideFlags.HideAndDontSave };
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var sculptable = go.AddComponent<SculptableMesh>();
            SetField(sculptable, "useMeshCollider", false);
            // Edit mode runs no Awake for an ordinary MonoBehaviour.
            if (sculptable.Vertices == null) Invoke(sculptable, "Awake");
            return sculptable;
        }

        private static void DestroySculptable(SculptableMesh sculptable, GameObject go)
        {
            if (sculptable != null)
            {
                Invoke(sculptable, "ReleaseNativeResources");
                if (sculptable.Mesh != null) Object.DestroyImmediate(sculptable.Mesh);
            }
            if (go != null) Object.DestroyImmediate(go);
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, AnyMember);
            Assert.That(field, Is.Not.Null, $"{target.GetType().Name}.{name} not found - renamed? Update this test.");
            field.SetValue(target, value);
        }

        private static void Invoke(object target, string name)
        {
            MethodInfo method = target.GetType().GetMethod(name, AnyMember, null, Type.EmptyTypes, null);
            Assert.That(method, Is.Not.Null, $"{target.GetType().Name}.{name} not found - renamed? Update this test.");
            method.Invoke(target, null);
        }
    }
}
