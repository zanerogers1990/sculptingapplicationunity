using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Sculpting.Tests
{
    /// Covers the buffer substrate dynamic topology is built on: MeshAdjacency's (start, count)
    /// layout and its in-place mutation, and the spatial grids' ability to take on vertices and
    /// triangles that did not exist when they were built.
    ///
    /// The first test here is the important one. Moving MeshAdjacency off monotonic CSR offsets
    /// touched every Laplacian in the app (Smooth, surface relax, cavity, the post-stroke unify
    /// pass), all of which sum neighbours in array order - so a reordering too small to fail a
    /// topology assertion would still move results in the last bits of a float, which is exactly
    /// the resolution SymmetryDriftTests measures at. ReproducesTheCsrBuildExactly pins the new
    /// build against an independent implementation of the old one.
    public class GrowableMeshBufferTests
    {
        private static Mesh BuildSphere()
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Mesh source = go.GetComponent<MeshFilter>().sharedMesh;
            var copy = Object.Instantiate(source);
            Object.DestroyImmediate(go);
            return copy;
        }

        // The build MeshAdjacency replaced, reimplemented here rather than referenced: the point is
        // to have a second opinion that cannot drift along with the code under test.
        private static void ReferenceCsr(int vertexCount, int[] triangles,
                                         out int[] neighborOffsets, out int[] neighborIndices,
                                         out int[] triangleOffsets, out int[] triangleIndices)
        {
            int cornerCount = triangles.Length - triangles.Length % 3;
            int triangleCount = cornerCount / 3;

            triangleOffsets = new int[vertexCount + 1];
            for (int k = 0; k < cornerCount; k++) triangleOffsets[triangles[k] + 1]++;
            for (int v = 0; v < vertexCount; v++) triangleOffsets[v + 1] += triangleOffsets[v];

            triangleIndices = new int[cornerCount];
            var cursor = new int[vertexCount];
            System.Array.Copy(triangleOffsets, cursor, vertexCount);
            for (int t = 0; t < triangleCount; t++)
            {
                int b = t * 3;
                triangleIndices[cursor[triangles[b]]++] = t;
                triangleIndices[cursor[triangles[b + 1]]++] = t;
                triangleIndices[cursor[triangles[b + 2]]++] = t;
            }

            neighborOffsets = new int[vertexCount + 1];
            var neighbors = new List<int>();
            for (int v = 0; v < vertexCount; v++)
            {
                neighborOffsets[v] = neighbors.Count;
                int start = neighbors.Count;
                for (int k = triangleOffsets[v]; k < triangleOffsets[v + 1]; k++)
                {
                    int b = triangleIndices[k] * 3;
                    int c0 = triangles[b], c1 = triangles[b + 1], c2 = triangles[b + 2];
                    int p, q;
                    if (c0 == v) { p = c1; q = c2; }
                    else if (c1 == v) { p = c0; q = c2; }
                    else { p = c0; q = c1; }

                    if (neighbors.IndexOf(p, start, neighbors.Count - start) < 0) neighbors.Add(p);
                    if (neighbors.IndexOf(q, start, neighbors.Count - start) < 0) neighbors.Add(q);
                }
            }
            neighborOffsets[vertexCount] = neighbors.Count;
            neighborIndices = neighbors.ToArray();
        }

        /// Same neighbours, same INCIDENT TRIANGLES, and above all the same ORDER as the CSR build
        /// this replaced - see the class remarks for why order is the part that matters.
        [Test]
        public void ReproducesTheCsrBuildExactly()
        {
            Mesh mesh = BuildSphere();
            Vector3[] verts = mesh.vertices;
            int[] tris = mesh.triangles;

            ReferenceCsr(verts.Length, tris, out int[] refNeighborOffsets, out int[] refNeighbors,
                         out int[] refTriangleOffsets, out int[] refTriangles);
            MeshAdjacency adjacency = MeshAdjacency.Build(verts.Length, tris);

            Assert.That(adjacency.VertexCount, Is.EqualTo(verts.Length));
            for (int v = 0; v < verts.Length; v++)
            {
                int refFrom = refNeighborOffsets[v], refCount = refNeighborOffsets[v + 1] - refFrom;
                Assert.That(adjacency.NeighborCount[v], Is.EqualTo(refCount), $"valence of vertex {v}");
                for (int k = 0; k < refCount; k++)
                    Assert.That(adjacency.NeighborIndices[adjacency.NeighborStart[v] + k],
                                Is.EqualTo(refNeighbors[refFrom + k]), $"neighbour {k} of vertex {v}");

                int refTriFrom = refTriangleOffsets[v], refTriCount = refTriangleOffsets[v + 1] - refTriFrom;
                Assert.That(adjacency.TriangleCount[v], Is.EqualTo(refTriCount), $"incident triangles of vertex {v}");
                for (int k = 0; k < refTriCount; k++)
                    Assert.That(adjacency.TriangleIndices[adjacency.TriangleStart[v] + k],
                                Is.EqualTo(refTriangles[refTriFrom + k]), $"triangle {k} of vertex {v}");
            }

            Object.DestroyImmediate(mesh);
        }

        /// Only the first cornerCount entries are topology; the rest of the array is spare capacity
        /// and must not be read as a run of degenerate triangles on vertex 0.
        [Test]
        public void BuildIgnoresSpareTriangleCapacity()
        {
            Mesh mesh = BuildSphere();
            Vector3[] verts = mesh.vertices;
            int[] tris = mesh.triangles;

            var padded = new int[tris.Length + 300];
            System.Array.Copy(tris, padded, tris.Length);

            MeshAdjacency exact = MeshAdjacency.Build(verts.Length, tris);
            MeshAdjacency withSpare = MeshAdjacency.Build(verts.Length, padded, tris.Length);

            for (int v = 0; v < verts.Length; v++)
            {
                Assert.That(withSpare.NeighborCount[v], Is.EqualTo(exact.NeighborCount[v]), $"valence of vertex {v}");
                Assert.That(withSpare.TriangleCount[v], Is.EqualTo(exact.TriangleCount[v]), $"triangles of vertex {v}");
            }

            Object.DestroyImmediate(mesh);
        }

        /// A rewritten neighbour list is readable afterwards, does not disturb any other vertex's
        /// list, and bumps the version the Burst mirror rebuilds on.
        [Test]
        public void SetNeighborsRelocatesWithoutDisturbingOtherVertices()
        {
            Mesh mesh = BuildSphere();
            MeshAdjacency adjacency = MeshAdjacency.Build(mesh.vertices.Length, mesh.triangles);

            var before = new List<int[]>();
            for (int v = 0; v < adjacency.VertexCount; v++)
            {
                var list = new int[adjacency.NeighborCount[v]];
                for (int k = 0; k < list.Length; k++) list[k] = adjacency.NeighborIndices[adjacency.NeighborStart[v] + k];
                before.Add(list);
            }

            int versionBefore = adjacency.Version;
            // Deliberately LONGER than the slot it sits in, which is what forces a relocation.
            var grown = new List<int> { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            adjacency.SetNeighbors(5, grown, grown.Count);

            Assert.That(adjacency.Version, Is.GreaterThan(versionBefore), "a mutation must bump Version");
            Assert.That(adjacency.NeighborCount[5], Is.EqualTo(grown.Count));
            for (int k = 0; k < grown.Count; k++)
                Assert.That(adjacency.NeighborIndices[adjacency.NeighborStart[5] + k], Is.EqualTo(grown[k]));

            for (int v = 0; v < adjacency.VertexCount; v++)
            {
                if (v == 5) continue;
                Assert.That(adjacency.NeighborCount[v], Is.EqualTo(before[v].Length), $"valence of vertex {v}");
                for (int k = 0; k < before[v].Length; k++)
                    Assert.That(adjacency.NeighborIndices[adjacency.NeighborStart[v] + k],
                                Is.EqualTo(before[v][k]), $"neighbour {k} of vertex {v}");
            }

            Object.DestroyImmediate(mesh);
        }

        /// Enough relocations to drive the block past its waste threshold and force a compaction,
        /// which rewrites every start offset in place - the one operation here that can scramble
        /// unrelated vertices if it moves a list onto one it has not read yet.
        [Test]
        public void CompactionPreservesEveryList()
        {
            Mesh mesh = BuildSphere();
            MeshAdjacency adjacency = MeshAdjacency.Build(mesh.vertices.Length, mesh.triangles);
            int vertexCount = adjacency.VertexCount;

            var expected = new List<int>[vertexCount];
            var scratch = new List<int>();
            // Every vertex rewritten twice over, so the abandoned space comfortably exceeds the
            // live block and compaction is guaranteed to have run at least once.
            for (int pass = 0; pass < 2; pass++)
            {
                for (int v = 0; v < vertexCount; v++)
                {
                    scratch.Clear();
                    int count = 3 + (v + pass) % 9;
                    for (int k = 0; k < count; k++) scratch.Add((v * 7 + k * 13 + pass) % vertexCount);
                    adjacency.SetNeighbors(v, scratch, scratch.Count);
                    expected[v] = new List<int>(scratch);
                }
            }

            for (int v = 0; v < vertexCount; v++)
            {
                Assert.That(adjacency.NeighborCount[v], Is.EqualTo(expected[v].Count), $"valence of vertex {v}");
                for (int k = 0; k < expected[v].Count; k++)
                    Assert.That(adjacency.NeighborIndices[adjacency.NeighborStart[v] + k],
                                Is.EqualTo(expected[v][k]), $"neighbour {k} of vertex {v} after compaction");
            }

            Object.DestroyImmediate(mesh);
        }

        /// Appended vertices get a list of their own without disturbing the existing ones.
        [Test]
        public void AppendVertexExtendsTheMap()
        {
            Mesh mesh = BuildSphere();
            MeshAdjacency adjacency = MeshAdjacency.Build(mesh.vertices.Length, mesh.triangles);
            int original = adjacency.VertexCount;

            int added = adjacency.AppendVertex();
            Assert.That(added, Is.EqualTo(original));
            Assert.That(adjacency.VertexCount, Is.EqualTo(original + 1));
            Assert.That(adjacency.NeighborCount[added], Is.EqualTo(0), "a fresh vertex starts with no neighbours");

            var list = new List<int> { 0, 1, 2 };
            adjacency.SetNeighbors(added, list, list.Count);
            for (int k = 0; k < list.Count; k++)
                Assert.That(adjacency.NeighborIndices[adjacency.NeighborStart[added] + k], Is.EqualTo(list[k]));

            Object.DestroyImmediate(mesh);
        }

        /// A vertex grid told about new vertices has to bucket them exactly as a grid built over
        /// the whole set would, or a brush query silently misses the geometry a refine just added.
        [Test]
        public void VertexGridAppendMatchesAFreshBuild()
        {
            const int original = 400;
            const int appended = 120;
            var rng = new System.Random(20260912);
            var positions = new Vector3[original + appended];
            for (int i = 0; i < positions.Length; i++)
                positions[i] = new Vector3((float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble());

            var partial = new Vector3[original];
            System.Array.Copy(positions, partial, original);

            var grown = new VertexSpatialGrid(partial, 0.1f);
            grown.AppendVertices(positions, original, appended);
            var fresh = new VertexSpatialGrid(positions, 0.1f);

            Assert.That(grown.VertexCount, Is.EqualTo(positions.Length));
            var probe = new Vector3(0.5f, 0.5f, 0.5f);
            foreach (float radius in new[] { 0.05f, 0.15f, 0.4f })
            {
                var a = new List<int>(grown.Query(probe, radius));
                var b = new List<int>(fresh.Query(probe, radius));
                a.Sort();
                b.Sort();
                Assert.That(a, Is.EqualTo(b), $"query at radius {radius} after append");
            }
        }
    }
}
