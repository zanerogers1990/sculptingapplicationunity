using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Sculpting.Tests
{
    /// Normals on meshes whose triangles are tiny in ABSOLUTE terms - dense, or small, or both.
    ///
    /// Unity's Vector3.normalized returns the zero vector below a magnitude of 1e-5, and a
    /// triangle's raw cross product has a magnitude of twice its area, so every triangle under
    /// 5e-6 square units used to lose its normal outright. The remesher takes each vertex normal
    /// from the source triangles nearest its edge crossings, so a dense SOURCE handed it zeros:
    /// the vertex fell back to Vector3.up and the dual-contouring placement lost its planes. Seen
    /// by the user as dark speckle over a remeshed sculpt that a Smooth stroke wiped away (the
    /// brush path normalizes by hand). Measured before the fix on a unit sphere remeshed twice at
    /// 256: 84,106 of 306,208 normals pointed straight up and 42,551 faced backwards. Brush
    /// raycasts had the same flaw, returning a zero hit normal.
    ///
    /// Two ways in: the dense unit-scale chain the user actually hit, and a centimetre-sized
    /// object, which crosses the same absolute threshold at a fraction of the cost.
    ///
    /// Lives in Assembly-CSharp-Editor like the other suites here, so SculptableMesh's private
    /// members are reached by reflection - see SculptControllerJobParityTests.
    public class RemeshNormalTests
    {
        // A remeshed normal is the average of true source face normals, and on a smooth sphere it
        // agrees with the output's own geometry to within a few degrees. 60 degrees is far outside
        // anything a correct normal does there, and far inside what a lost one does (straight up
        // on a sphere is anywhere from 0 to 180 degrees off).
        private const float MaxDeviationDegrees = 60f;
        private const float UnitLengthTolerance = 1e-3f;
        private const BindingFlags AnyMember =
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// What the user hit: a unit-sized model remeshed, then remeshed AGAIN from that dense result.
        /// The first pass reads a 20k-triangle source and was always fine; the second reads ~612k.
        [Test]
        public void RemeshOfDenseSourceKeepsSurfaceNormals()
        {
            Mesh source = BuildIcosphere(5, 0.5f);
            Mesh first = null, second = null;
            try
            {
                first = MeshRemesher.Remesh(source.vertices, source.triangles, 256);
                Assert.That(first.triangles.Length / 3, Is.GreaterThan(500000), "first remesh should be dense");
                second = MeshRemesher.Remesh(first.vertices, first.triangles, 256);
                AssertNormalsFollowSurface(second, "unit sphere, 256 then 256");
            }
            finally
            {
                Object.DestroyImmediate(source);
                if (first != null) Object.DestroyImmediate(first);
                if (second != null) Object.DestroyImmediate(second);
            }
        }

        /// Same threshold, crossed by size instead of density: a 1 cm sphere of only 5k triangles.
        [Test]
        public void RemeshOfSmallObjectKeepsSurfaceNormals()
        {
            Mesh source = BuildIcosphere(4, 0.005f);
            Mesh remeshed = null;
            try
            {
                remeshed = MeshRemesher.Remesh(source.vertices, source.triangles, 64);
                AssertNormalsFollowSurface(remeshed, "1 cm sphere, 64");
            }
            finally
            {
                Object.DestroyImmediate(source);
                if (remeshed != null) Object.DestroyImmediate(remeshed);
            }
        }

        /// Every brush reads the surface normal from RaycastMesh, so a zero one there is a dab with no
        /// direction to push along.
        [Test]
        public void BrushRaycastOnSmallObjectReturnsUnitNormal()
        {
            const float radius = 0.005f;
            var go = new GameObject("RemeshNormalTestsMesh") { hideFlags = HideFlags.HideAndDontSave };
            Mesh source = BuildIcosphere(4, radius);
            source.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<MeshFilter>().sharedMesh = source;
            var sculptable = go.AddComponent<SculptableMesh>();
            try
            {
                FieldOf(typeof(SculptableMesh), "useMeshCollider").SetValue(sculptable, false);
                if (sculptable.Vertices == null) MethodOf(typeof(SculptableMesh), "Awake").Invoke(sculptable, null);

                // Directions chosen off the icosahedron's symmetry axes so no ray lands on a vertex,
                // where rounding can reject every incident triangle.
                var directions = new[]
                {
                    new Vector3(0.31f, 0.52f, -0.79f), new Vector3(-0.67f, 0.13f, 0.73f),
                    new Vector3(0.11f, -0.93f, 0.35f), new Vector3(0.83f, 0.29f, 0.47f),
                };
                int failures = 0;
                string first = null;
                foreach (Vector3 d in directions)
                {
                    Vector3 dir = d.normalized;
                    var ray = new Ray(-dir * (radius * 20f), dir);
                    Assert.That(sculptable.RaycastMesh(ray, 1f, out _, out Vector3 normal), Is.True, $"ray {dir} missed");

                    float length = normal.magnitude;
                    bool ok = Mathf.Abs(length - 1f) <= UnitLengthTolerance && Vector3.Dot(normal, -dir) > 0.5f;
                    if (ok) continue;
                    failures++;
                    first ??= $"ray {dir}: normal {normal} (length {length})";
                }
                Assert.That(failures, Is.Zero, $"{failures}/{directions.Length} hit normals wrong, first: {first}");
            }
            finally
            {
                MethodOf(typeof(SculptableMesh), "ReleaseNativeResources").Invoke(sculptable, null);
                if (sculptable.Mesh != null) Object.DestroyImmediate(sculptable.Mesh);
                Object.DestroyImmediate(go);
                Object.DestroyImmediate(source);
            }
        }

        /// Every stored normal must be unit length and within MaxDeviationDegrees of the output's
        /// own area-weighted geometric normal (computed in doubles, so this check has no epsilon trap
        /// of its own).
        private static void AssertNormalsFollowSurface(Mesh mesh, string label)
        {
            Vector3[] v = mesh.vertices;
            Vector3[] n = mesh.normals;
            int[] t = mesh.triangles;
            Assert.That(n.Length, Is.EqualTo(v.Length), $"{label}: normal count");

            var sum = new double[v.Length * 3];
            for (int i = 0; i < t.Length; i += 3)
            {
                Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                double e1x = b.x - a.x, e1y = b.y - a.y, e1z = b.z - a.z;
                double e2x = c.x - a.x, e2y = c.y - a.y, e2z = c.z - a.z;
                double cx = e1y * e2z - e1z * e2y, cy = e1z * e2x - e1x * e2z, cz = e1x * e2y - e1y * e2x;
                for (int k = 0; k < 3; k++)
                {
                    int s = t[i + k] * 3;
                    sum[s] += cx; sum[s + 1] += cy; sum[s + 2] += cz;
                }
            }

            double minCos = Math.Cos(MaxDeviationDegrees * Math.PI / 180.0);
            int notUnit = 0, deviating = 0;
            string firstBad = null;
            for (int i = 0; i < v.Length; i++)
            {
                double gx = sum[i * 3], gy = sum[i * 3 + 1], gz = sum[i * 3 + 2];
                double gl = Math.Sqrt(gx * gx + gy * gy + gz * gz);
                double nl = Math.Sqrt((double)n[i].x * n[i].x + (double)n[i].y * n[i].y + (double)n[i].z * n[i].z);
                if (Math.Abs(nl - 1.0) > UnitLengthTolerance)
                {
                    notUnit++;
                    firstBad ??= $"vertex {i} normal {n[i]} length {nl:F4}";
                    continue;
                }
                if (gl <= 0.0) continue;
                double cos = (gx * n[i].x + gy * n[i].y + gz * n[i].z) / (gl * nl);
                if (cos >= minCos) continue;
                deviating++;
                firstBad ??= $"vertex {i} normal {n[i]} vs surface ({gx / gl:F3}, {gy / gl:F3}, {gz / gl:F3})";
            }

            Assert.That(notUnit + deviating, Is.Zero,
                $"{label}: {v.Length} verts, {notUnit} non-unit normals, {deviating} more than {MaxDeviationDegrees} deg off the surface; first: {firstBad}");
        }

        /// A subdivided icosahedron on a sphere of `radius` - uniform triangles, none degenerate.
        private static Mesh BuildIcosphere(int subdivisions, float radius)
        {
            float g = (1f + Mathf.Sqrt(5f)) * 0.5f;
            var verts = new List<Vector3>
            {
                new Vector3(-1, g, 0), new Vector3(1, g, 0), new Vector3(-1, -g, 0), new Vector3(1, -g, 0),
                new Vector3(0, -1, g), new Vector3(0, 1, g), new Vector3(0, -1, -g), new Vector3(0, 1, -g),
                new Vector3(g, 0, -1), new Vector3(g, 0, 1), new Vector3(-g, 0, -1), new Vector3(-g, 0, 1),
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

            for (int i = 0; i < verts.Count; i++) verts[i] *= radius;

            var mesh = new Mesh { name = "RemeshNormalTestsIcosphere", indexFormat = IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return mesh;
        }

        private static FieldInfo FieldOf(Type type, string name)
        {
            FieldInfo field = type.GetField(name, AnyMember);
            Assert.That(field, Is.Not.Null, $"{type.Name}.{name} not found - renamed? Update this test.");
            return field;
        }

        private static MethodInfo MethodOf(Type type, string name)
        {
            MethodInfo method = type.GetMethod(name, AnyMember, null, Type.EmptyTypes, null);
            Assert.That(method, Is.Not.Null, $"{type.Name}.{name} not found - renamed? Update this test.");
            return method;
        }
    }
}
