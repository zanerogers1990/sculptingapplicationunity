using UnityEngine;

namespace Sculpting
{
    /// Turns a PolyMesh into the triangle list the sculpting engine runs on, remembering which
    /// polygon every triangle came from so the quads can be recovered for display and export.
    internal static class PolyMeshTriangulator
    {
        public static int TriangleCountOf(PolyMesh mesh)
        {
            int count = 0;
            for (int f = 0; f < mesh.FaceCount; f++)
            {
                int n = mesh.FaceSize(f);
                if (n >= 3) count += n - 2;
            }
            return count;
        }

        /// Triangles, three indices each, winding kept. `triangleFace[t]` is the source face.
        ///
        /// Quads split along their shorter diagonal: the longer one would put a fold across the
        /// flatter direction of a non-planar quad and makes thinner triangles. Mirror-image quads
        /// have bitwise-equal diagonal lengths, so they split the same way - except a quad
        /// straddling the mirror plane, whose two diagonals are each other's reflection and tie;
        /// no triangulation of that quad can be symmetric, and the quad topology still is.
        ///
        /// Larger faces fan from their first corner. The remesher's rare pentagons are nearly
        /// convex, where a fan is fine; this is not meant for arbitrary concave n-gons.
        public static int[] Triangulate(PolyMesh mesh, out int[] triangleFace)
        {
            Vector3[] p = mesh.Vertices;
            int[] start = mesh.FaceStart, idx = mesh.FaceIndices;
            int triCount = TriangleCountOf(mesh);
            var tris = new int[triCount * 3];
            triangleFace = new int[triCount];
            int t = 0;

            for (int f = 0; f < mesh.FaceCount; f++)
            {
                int s = start[f], n = start[f + 1] - s;
                if (n < 3) continue;

                if (n == 4)
                {
                    int a = idx[s], b = idx[s + 1], c = idx[s + 2], d = idx[s + 3];
                    if ((p[a] - p[c]).sqrMagnitude <= (p[b] - p[d]).sqrMagnitude)
                    {
                        Emit(tris, triangleFace, ref t, f, a, b, c);
                        Emit(tris, triangleFace, ref t, f, a, c, d);
                    }
                    else
                    {
                        Emit(tris, triangleFace, ref t, f, a, b, d);
                        Emit(tris, triangleFace, ref t, f, b, c, d);
                    }
                    continue;
                }

                for (int k = 1; k + 1 < n; k++)
                    Emit(tris, triangleFace, ref t, f, idx[s], idx[s + k], idx[s + k + 1]);
            }
            return tris;
        }

        private static void Emit(int[] tris, int[] triangleFace, ref int t, int face, int a, int b, int c)
        {
            tris[t * 3] = a;
            tris[t * 3 + 1] = b;
            tris[t * 3 + 2] = c;
            triangleFace[t] = face;
            t++;
        }
    }
}
