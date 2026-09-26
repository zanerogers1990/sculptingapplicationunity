using System;
using System.Collections.Generic;
using Sculpting;
using UnityEngine;

namespace GeometryHarness
{
    /// Procedural test meshes with closed-form topology, so tests can assert exact counts.
    internal static class Fixtures
    {
        /// Cube subdivided `n` per edge and pushed onto a sphere of `radius`. All quads, closed,
        /// genus 0: V = 6n^2 + 2, F = 6n^2, E = 12n^2, and exactly 8 valence-3 poles (the cube
        /// corners) - the ideal answer a quad remesher should find on a sphere.
        public static PolyMesh QuadSphere(int n, float radius = 1f)
        {
            var b = new PolyMeshBuilder(6 * n * n + 2, 6 * n * n);
            var ids = new Dictionary<int, int>();
            int stride = n + 1;

            int Vertex(int i, int j, int k)
            {
                int key = (i * stride + j) * stride + k;
                if (ids.TryGetValue(key, out int id)) return id;
                var p = new Vector3(2f * i / n - 1f, 2f * j / n - 1f, 2f * k / n - 1f);
                id = b.AddVertex(p.normalized * radius);
                ids.Add(key, id);
                return id;
            }

            // (origin, du, dv) per cube face with Cross(du, dv) = outward normal, so each quad
            // (o, o+du, o+du+dv, o+dv) has Cross(b - a, c - a) pointing out.
            int[][] faces =
            {
                new[] { n, 0, 0,   0, 1, 0,   0, 0, 1 }, // +X
                new[] { 0, 0, 0,   0, 0, 1,   0, 1, 0 }, // -X
                new[] { 0, n, 0,   0, 0, 1,   1, 0, 0 }, // +Y
                new[] { 0, 0, 0,   1, 0, 0,   0, 0, 1 }, // -Y
                new[] { 0, 0, n,   1, 0, 0,   0, 1, 0 }, // +Z
                new[] { 0, 0, 0,   0, 1, 0,   1, 0, 0 }, // -Z
            };

            foreach (int[] f in faces)
            {
                for (int u = 0; u < n; u++)
                for (int v = 0; v < n; v++)
                {
                    int At(int du, int dv, int axis) => f[axis] + (u + du) * f[3 + axis] + (v + dv) * f[6 + axis];
                    int a = Vertex(At(0, 0, 0), At(0, 0, 1), At(0, 0, 2));
                    int bb = Vertex(At(1, 0, 0), At(1, 0, 1), At(1, 0, 2));
                    int c = Vertex(At(1, 1, 0), At(1, 1, 1), At(1, 1, 2));
                    int d = Vertex(At(0, 1, 0), At(0, 1, 1), At(0, 1, 2));
                    b.AddFace(a, bb, c, d);
                }
            }
            return b.Build();
        }

        /// QuadSphere split into triangles (shorter diagonal) - a triangle mesh with known topology.
        public static PolyMesh TriSphere(int n, float radius = 1f)
        {
            PolyMesh q = QuadSphere(n, radius);
            return PolyMesh.FromTriangles(q.Vertices, PolyMeshTriangulator.Triangulate(q, out _));
        }

        /// Torus of `nu` x `nv` quads. Closed, genus 1, every vertex valence 4: V = F = nu*nv,
        /// E = 2*nu*nv. Oriented outward.
        public static PolyMesh Torus(int nu, int nv, float major = 1f, float minor = 0.35f)
        {
            var b = new PolyMeshBuilder(nu * nv, nu * nv);
            for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                double u = 2 * Math.PI * i / nu, v = 2 * Math.PI * j / nv;
                double ring = major + minor * Math.Cos(v);
                b.AddVertex(new Vector3((float)(ring * Math.Cos(u)), (float)(minor * Math.Sin(v)), (float)(ring * Math.Sin(u))));
            }
            for (int i = 0; i < nu; i++)
            for (int j = 0; j < nv; j++)
            {
                int i1 = (i + 1) % nu, j1 = (j + 1) % nv;
                b.AddFace(i * nv + j, i1 * nv + j, i1 * nv + j1, i * nv + j1);
            }
            return OrientOutward(b.Build());
        }

        /// Flat open grid of `nx` x `nz` unit quads in the XZ plane, facing +Y. `shearDeg` tilts
        /// the z rows so every quad corner is 90 +- shearDeg degrees.
        public static PolyMesh Grid(int nx, int nz, float shearDeg = 0f)
        {
            var b = new PolyMeshBuilder((nx + 1) * (nz + 1), nx * nz);
            float shear = (float)Math.Tan(shearDeg * Math.PI / 180.0);
            for (int j = 0; j <= nz; j++)
            for (int i = 0; i <= nx; i++)
                b.AddVertex(new Vector3(i + shear * j, 0f, j));
            for (int j = 0; j < nz; j++)
            for (int i = 0; i < nx; i++)
            {
                int a = j * (nx + 1) + i;
                b.AddFace(a, a + nx + 1, a + nx + 2, a + 1); // Cross(+z, +x) = +y
            }
            return b.Build();
        }

        /// Copy of `mesh` with face `face` wound backwards.
        public static PolyMesh FlipFace(PolyMesh mesh, int face)
        {
            var idx = (int[])mesh.FaceIndices.Clone();
            Array.Reverse(idx, mesh.FaceStart[face], mesh.FaceSize(face));
            return new PolyMesh(mesh.Vertices, mesh.FaceStart, idx);
        }

        /// Copy of `mesh` with extra faces appended (and optionally extra vertices).
        public static PolyMesh Append(PolyMesh mesh, Vector3[] extraVertices, params int[][] extraFaces)
        {
            var b = new PolyMeshBuilder(mesh.VertexCount + extraVertices.Length, mesh.FaceCount + extraFaces.Length);
            foreach (Vector3 v in mesh.Vertices) b.AddVertex(v);
            foreach (Vector3 v in extraVertices) b.AddVertex(v);
            var corners = new int[64];
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                int n = mesh.FaceSize(f);
                Array.Copy(mesh.FaceIndices, mesh.FaceStart[f], corners, 0, n);
                b.AddFace(corners, n);
            }
            foreach (int[] f in extraFaces) b.AddFace(f, f.Length);
            return b.Build();
        }

        /// Copy of `mesh` with its first `count` quads each split into two triangles.
        public static PolyMesh SplitQuads(PolyMesh mesh, int count)
        {
            var b = new PolyMeshBuilder(mesh.VertexCount, mesh.FaceCount + count);
            foreach (Vector3 v in mesh.Vertices) b.AddVertex(v);
            var corners = new int[64];
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                int n = mesh.FaceSize(f);
                if (f < count && n == 4)
                {
                    b.AddFace(mesh.Corner(f, 0), mesh.Corner(f, 1), mesh.Corner(f, 2));
                    b.AddFace(mesh.Corner(f, 0), mesh.Corner(f, 2), mesh.Corner(f, 3));
                    continue;
                }
                Array.Copy(mesh.FaceIndices, mesh.FaceStart[f], corners, 0, n);
                b.AddFace(corners, n);
            }
            return b.Build();
        }

        /// Signed volume by the divergence theorem over a fan triangulation. Positive for a
        /// closed mesh wound the engine's way.
        public static double SignedVolume(PolyMesh mesh)
        {
            double sum = 0;
            Vector3[] p = mesh.Vertices;
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                int n = mesh.FaceSize(f);
                Vector3 a = p[mesh.Corner(f, 0)];
                for (int k = 1; k + 1 < n; k++)
                {
                    Vector3 b = p[mesh.Corner(f, k)], c = p[mesh.Corner(f, k + 1)];
                    sum += a.x * ((double)b.y * c.z - (double)b.z * c.y)
                         - a.y * ((double)b.x * c.z - (double)b.z * c.x)
                         + a.z * ((double)b.x * c.y - (double)b.y * c.x);
                }
            }
            return sum / 6.0;
        }

        public static PolyMesh OrientOutward(PolyMesh mesh)
        {
            if (SignedVolume(mesh) >= 0) return mesh;
            var idx = (int[])mesh.FaceIndices.Clone();
            for (int f = 0; f < mesh.FaceCount; f++) Array.Reverse(idx, mesh.FaceStart[f], mesh.FaceSize(f));
            return new PolyMesh(mesh.Vertices, mesh.FaceStart, idx);
        }
    }
}
