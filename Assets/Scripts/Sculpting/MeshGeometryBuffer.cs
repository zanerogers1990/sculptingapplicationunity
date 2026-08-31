using System;
using UnityEngine;

namespace Sculpting
{
    /// Plain growable vertex/normal/index arrays for meshes the remesher builds.
    ///
    /// Replaces the List&lt;Vector3&gt;/List&lt;int&gt; pair the extraction used to append into, for
    /// three reasons that all only start to matter in the millions of triangles:
    ///
    ///   - The parallel passes know their exact output size in advance (a prefix sum over
    ///     per-brick counts), so they can size these once and have every worker write straight
    ///     into disjoint slices. A List cannot be written past its Count, which forced the old
    ///     pipeline's compaction pass to be sequential.
    ///   - List growth doubles, so a 3M-vertex build transiently held a 2M-entry array and a
    ///     4M-entry array at once. Sizing up front removes that spike and the copies.
    ///   - Unity's Mesh.SetVertices/SetIndices take (array, start, length) overloads, so an
    ///     over-allocated buffer uploads without being trimmed to size first.
    ///
    /// Reused across remeshes (arrays grow, never shrink), which is why Reset only rewinds the
    /// counts. Not thread-safe: the parallel passes write disjoint pre-reserved ranges through
    /// the public arrays, and only the single-threaded tail (hole patching) uses Add.
    internal sealed class MeshGeometryBuffer
    {
        public Vector3[] Vertices = Array.Empty<Vector3>();
        public Vector3[] Normals = Array.Empty<Vector3>();
        public int[] Indices = Array.Empty<int>();

        public int VertexCount;
        public int IndexCount;
        public int TriangleCount => IndexCount / 3;

        public void Reset()
        {
            VertexCount = 0;
            IndexCount = 0;
        }

        /// Makes room for `count` vertices, preserving what is already there. Grows
        /// geometrically past the request so an Add-driven tail (hole patching) doesn't
        /// reallocate per vertex.
        public void EnsureVertexCapacity(int count)
        {
            if (Vertices.Length >= count) return;
            int size = Mathf.Max(count, Vertices.Length * 2);
            Array.Resize(ref Vertices, size);
            Array.Resize(ref Normals, size);
        }

        public void EnsureIndexCapacity(int count)
        {
            if (Indices.Length >= count) return;
            Array.Resize(ref Indices, Mathf.Max(count, Indices.Length * 2));
        }

        public int AddVertex(Vector3 position, Vector3 normal)
        {
            EnsureVertexCapacity(VertexCount + 1);
            Vertices[VertexCount] = position;
            Normals[VertexCount] = normal;
            return VertexCount++;
        }

        public void AddTriangle(int a, int b, int c)
        {
            EnsureIndexCapacity(IndexCount + 3);
            Indices[IndexCount] = a;
            Indices[IndexCount + 1] = b;
            Indices[IndexCount + 2] = c;
            IndexCount += 3;
        }

        /// Releases the retained capacity when it has become far larger than what is actually
        /// being used.
        ///
        /// The buffer is a static scratch shared by every remesh, so without this a single
        /// remesh at maximum density would leave a few hundred megabytes of scratch arrays
        /// resident for the rest of the session, long after the user dropped back to working
        /// densities. The 4x slack threshold is deliberately loose - the point of keeping the
        /// arrays is that a run of remeshes at similar sizes reallocates nothing, and that
        /// still holds; only an order-of-magnitude drop gives the memory back.
        public void TrimExcess()
        {
            const int Threshold = 1 << 20; // below a megabyte of entries, not worth the churn
            if (Vertices.Length > Threshold && Vertices.Length > VertexCount * 4)
            {
                Array.Resize(ref Vertices, VertexCount);
                Array.Resize(ref Normals, VertexCount);
            }
            if (Indices.Length > Threshold && Indices.Length > IndexCount * 4)
                Array.Resize(ref Indices, IndexCount);
        }

        /// Exact-length copies, for callers that need to own the arrays (SculptableMesh keeps
        /// them as its working buffers). Skipped by the Mesh upload path, which reads the
        /// oversized arrays directly through the (array, start, length) overloads.
        public Vector3[] CopyVertices()
        {
            var v = new Vector3[VertexCount];
            Array.Copy(Vertices, v, VertexCount);
            return v;
        }

        public Vector3[] CopyNormals()
        {
            var n = new Vector3[VertexCount];
            Array.Copy(Normals, n, VertexCount);
            return n;
        }

        public int[] CopyIndices()
        {
            var t = new int[IndexCount];
            Array.Copy(Indices, t, IndexCount);
            return t;
        }
    }
}
