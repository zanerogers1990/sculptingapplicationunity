using System;
using UnityEngine;

namespace Sculpting
{
    /// A polygon mesh: vertices plus faces of any size, stored flat.
    ///
    /// This is the quad remesher's output format and the "real" topology a remeshed
    /// SculptableMesh keeps alongside its triangles. The sculpting engine itself only ever sees
    /// triangles (PolyMeshTriangulator); this keeps the quads so the wireframe, OBJ export and
    /// save file can show and write them.
    ///
    /// Face f's corners are FaceIndices[FaceStart[f] .. FaceStart[f + 1]), wound like the
    /// engine's triangles: Cross(b - a, c - a) points outward (clockwise seen from outside, Unity's
    /// front-face rule), so a quad (a, b, c, d) triangulates to (a, b, c) + (a, c, d) facing the
    /// same way and a closed mesh has positive signed volume.
    ///
    /// Immutable in shape once built - arrays are exact-length. Build one with PolyMeshBuilder.
    internal sealed class PolyMesh
    {
        public readonly Vector3[] Vertices;
        public readonly int[] FaceStart;   // FaceCount + 1 entries, FaceStart[0] == 0
        public readonly int[] FaceIndices; // FaceStart[FaceCount] entries

        public int VertexCount => Vertices.Length;
        public int FaceCount => FaceStart.Length - 1;
        public int CornerCount => FaceIndices.Length;

        public PolyMesh(Vector3[] vertices, int[] faceStart, int[] faceIndices)
        {
            if (faceStart == null || faceStart.Length == 0 || faceStart[0] != 0)
                throw new ArgumentException("faceStart must begin with 0", nameof(faceStart));
            if (faceStart[faceStart.Length - 1] != faceIndices.Length)
                throw new ArgumentException("faceStart must end at faceIndices.Length", nameof(faceStart));
            Vertices = vertices;
            FaceStart = faceStart;
            FaceIndices = faceIndices;
        }

        public int FaceSize(int face) => FaceStart[face + 1] - FaceStart[face];

        /// Corner `k` of `face`, with k taken modulo the face size (so k - 1 and k + 1 wrap).
        public int Corner(int face, int k)
        {
            int start = FaceStart[face];
            int n = FaceStart[face + 1] - start;
            k %= n;
            if (k < 0) k += n;
            return FaceIndices[start + k];
        }

        /// A mesh of only quads, from four indices per face.
        public static PolyMesh FromQuads(Vector3[] vertices, int[] quadIndices)
        {
            if (quadIndices.Length % 4 != 0)
                throw new ArgumentException("quad index count must be a multiple of 4", nameof(quadIndices));
            int faces = quadIndices.Length / 4;
            var start = new int[faces + 1];
            for (int f = 0; f <= faces; f++) start[f] = f * 4;
            return new PolyMesh(vertices, start, (int[])quadIndices.Clone());
        }

        /// A mesh of only triangles, from three indices per face - how a triangle sculpt enters
        /// the poly-mesh tools (analysis, OBJ writing) without a separate code path.
        public static PolyMesh FromTriangles(Vector3[] vertices, int[] triangles, int indexCount = -1)
        {
            if (indexCount < 0) indexCount = triangles.Length;
            if (indexCount % 3 != 0)
                throw new ArgumentException("triangle index count must be a multiple of 3", nameof(triangles));
            int faces = indexCount / 3;
            var start = new int[faces + 1];
            for (int f = 0; f <= faces; f++) start[f] = f * 3;
            var idx = new int[indexCount];
            Array.Copy(triangles, idx, indexCount);
            return new PolyMesh(vertices, start, idx);
        }
    }

    /// Append-only builder for a PolyMesh. Grows geometrically; Build() trims to exact length.
    internal sealed class PolyMeshBuilder
    {
        private Vector3[] _vertices;
        private int[] _faceStart;
        private int[] _faceIndices;
        private int _vertexCount;
        private int _faceCount;
        private int _cornerCount;

        public int VertexCount => _vertexCount;
        public int FaceCount => _faceCount;

        public PolyMeshBuilder(int vertexCapacity = 64, int faceCapacity = 64)
        {
            _vertices = new Vector3[Mathf.Max(vertexCapacity, 4)];
            _faceStart = new int[Mathf.Max(faceCapacity, 4) + 1];
            _faceIndices = new int[Mathf.Max(faceCapacity, 4) * 4];
        }

        public int AddVertex(Vector3 position)
        {
            if (_vertexCount == _vertices.Length) Array.Resize(ref _vertices, _vertices.Length * 2);
            _vertices[_vertexCount] = position;
            return _vertexCount++;
        }

        public int AddFace(int a, int b, int c)
        {
            EnsureFace(3);
            _faceIndices[_cornerCount++] = a;
            _faceIndices[_cornerCount++] = b;
            _faceIndices[_cornerCount++] = c;
            return CloseFace();
        }

        public int AddFace(int a, int b, int c, int d)
        {
            EnsureFace(4);
            _faceIndices[_cornerCount++] = a;
            _faceIndices[_cornerCount++] = b;
            _faceIndices[_cornerCount++] = c;
            _faceIndices[_cornerCount++] = d;
            return CloseFace();
        }

        public int AddFace(int[] corners, int count)
        {
            EnsureFace(count);
            Array.Copy(corners, 0, _faceIndices, _cornerCount, count);
            _cornerCount += count;
            return CloseFace();
        }

        public PolyMesh Build()
        {
            var v = new Vector3[_vertexCount];
            Array.Copy(_vertices, v, _vertexCount);
            var s = new int[_faceCount + 1];
            Array.Copy(_faceStart, s, _faceCount + 1);
            var idx = new int[_cornerCount];
            Array.Copy(_faceIndices, idx, _cornerCount);
            return new PolyMesh(v, s, idx);
        }

        private void EnsureFace(int corners)
        {
            if (_faceCount + 2 > _faceStart.Length) Array.Resize(ref _faceStart, _faceStart.Length * 2);
            if (_cornerCount + corners > _faceIndices.Length)
                Array.Resize(ref _faceIndices, Mathf.Max(_faceIndices.Length * 2, _cornerCount + corners));
        }

        private int CloseFace()
        {
            _faceStart[++_faceCount] = _cornerCount;
            return _faceCount - 1;
        }
    }
}
