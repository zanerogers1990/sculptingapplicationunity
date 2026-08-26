using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpting
{
    /// Pushes just the touched vertices' position/normal/color into a mesh's own GPU vertex
    /// buffer via a compute-shader scatter write, instead of Unity's managed Mesh.vertices/
    /// .normals/.colors setters - those always reupload the ENTIRE array regardless of how many
    /// vertices actually changed, which is the real per-frame ceiling at high polycounts. See
    /// SculptableMesh.ApplyVerticesLocal, the only caller.
    ///
    /// CPU stays fully authoritative for every other system - raycasting, undo, mask, mirror,
    /// and export all keep reading SculptableMesh's own Vector3[]/Color[] arrays exactly as
    /// before. This class only ever WRITES into the mesh's buffer for rendering; nothing reads
    /// it back, so there's no async-readback latency to reason about here (unlike a full GPU
    /// brush rewrite would need).
    ///
    /// Loaded via Resources.Load rather than a serialized field: this project's Unity MCP
    /// tooling can't assign object-reference fields (see feedback_unity_mcp_object_refs memory),
    /// and a plain runtime script has no Editor-only AssetDatabase access once built standalone -
    /// Resources.Load is the one loading path that works in both contexts without Inspector
    /// wiring, matching this project's "self-resolve at runtime" convention elsewhere.
    public class GpuVertexScatter
    {
        private static ComputeShader _shader;
        private static int _kernel = -1;
        private static uint _threadGroupSize = 64;
        private static bool _loadAttempted;

        private GraphicsBuffer _vertexBuffer;
        private uint _stride, _positionOffset, _normalOffset, _colorOffset;

        private GraphicsBuffer _indexBuffer;
        private GraphicsBuffer _positionBuffer;
        private GraphicsBuffer _normalBuffer;
        private GraphicsBuffer _colorBuffer;
        private int _bufferCapacity;

        private uint[] _indexScratch = System.Array.Empty<uint>();
        private Vector3[] _positionScratch = System.Array.Empty<Vector3>();
        private Vector3[] _normalScratch = System.Array.Empty<Vector3>();
        private Vector4[] _colorScratch = System.Array.Empty<Vector4>();

        private static void EnsureShaderLoaded()
        {
            if (_loadAttempted) return;
            _loadAttempted = true;
            _shader = Resources.Load<ComputeShader>("VertexScatter");
            if (_shader == null)
            {
                Debug.LogError("[GpuVertexScatter] Assets/Resources/VertexScatter.compute not found - falling back to full-mesh vertex upload.");
                return;
            }
            _kernel = _shader.FindKernel("ScatterVertexAttributes");
            _shader.GetKernelThreadGroupSizes(_kernel, out _threadGroupSize, out _, out _);
        }

        /// Call whenever the owning SculptableMesh swaps to a brand-new Mesh instance (Remesh/
        /// RestoreSnapshot's full-rebuild path) or after (re)configuring an existing mesh's
        /// vertex layout - re-fetches the buffer handle/offsets and disposes the previous handle
        /// so native memory isn't leaked.
        public void BindMesh(Mesh mesh)
        {
            _vertexBuffer?.Dispose();
            _vertexBuffer = mesh.GetVertexBuffer(0);
            _stride = (uint)mesh.GetVertexBufferStride(0);
            _positionOffset = (uint)mesh.GetVertexAttributeOffset(VertexAttribute.Position);
            _normalOffset = (uint)mesh.GetVertexAttributeOffset(VertexAttribute.Normal);
            _colorOffset = (uint)mesh.GetVertexAttributeOffset(VertexAttribute.Color);
        }

        private void EnsureCapacity(int count)
        {
            if (_bufferCapacity >= count) return;

            _indexBuffer?.Dispose();
            _positionBuffer?.Dispose();
            _normalBuffer?.Dispose();
            _colorBuffer?.Dispose();

            _bufferCapacity = Mathf.Max(Mathf.NextPowerOfTwo(count), 64);
            _indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _bufferCapacity, sizeof(uint));
            _positionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _bufferCapacity, sizeof(float) * 3);
            _normalBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _bufferCapacity, sizeof(float) * 3);
            _colorBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _bufferCapacity, sizeof(float) * 4);
        }

        /// Scatters position/normal/color for exactly the vertices in `indices` (reading from
        /// the full `positions`/`normals`/`colors` arrays by index) into the mesh's GPU vertex
        /// buffer. `count` must equal `indices.Count` - callers already have this for free.
        /// Takes the concrete HashSet type rather than IEnumerable&lt;int&gt; deliberately: a
        /// HashSet's struct enumerator only avoids allocating when accessed through its own
        /// type - calling GetEnumerator() through an IEnumerable&lt;int&gt; reference boxes it,
        /// allocating garbage every single call during a held stroke.
        public void ScatterDirty(HashSet<int> indices, int count, Vector3[] positions, Vector3[] normals, Color[] colors)
        {
            EnsureShaderLoaded();
            if (_shader == null || _vertexBuffer == null || count == 0) return;

            EnsureCapacity(count);
            if (_indexScratch.Length < count)
            {
                _indexScratch = new uint[count];
                _positionScratch = new Vector3[count];
                _normalScratch = new Vector3[count];
                _colorScratch = new Vector4[count];
            }

            int n = 0;
            foreach (int vi in indices)
            {
                _indexScratch[n] = (uint)vi;
                _positionScratch[n] = positions[vi];
                _normalScratch[n] = normals[vi];
                _colorScratch[n] = colors[vi];
                n++;
            }

            _indexBuffer.SetData(_indexScratch, 0, 0, n);
            _positionBuffer.SetData(_positionScratch, 0, 0, n);
            _normalBuffer.SetData(_normalScratch, 0, 0, n);
            _colorBuffer.SetData(_colorScratch, 0, 0, n);

            _shader.SetBuffer(_kernel, "_VertexBuffer", _vertexBuffer);
            _shader.SetBuffer(_kernel, "_DirtyIndices", _indexBuffer);
            _shader.SetBuffer(_kernel, "_DirtyPositions", _positionBuffer);
            _shader.SetBuffer(_kernel, "_DirtyNormals", _normalBuffer);
            _shader.SetBuffer(_kernel, "_DirtyColors", _colorBuffer);
            _shader.SetInt("_Stride", (int)_stride);
            _shader.SetInt("_PositionOffset", (int)_positionOffset);
            _shader.SetInt("_NormalOffset", (int)_normalOffset);
            _shader.SetInt("_ColorOffset", (int)_colorOffset);
            _shader.SetInt("_DirtyCount", n);

            int groups = Mathf.Max(1, Mathf.CeilToInt(n / (float)_threadGroupSize));
            _shader.Dispatch(_kernel, groups, 1, 1);
        }

        public void Dispose()
        {
            _vertexBuffer?.Dispose();
            _indexBuffer?.Dispose();
            _positionBuffer?.Dispose();
            _normalBuffer?.Dispose();
            _colorBuffer?.Dispose();
            _vertexBuffer = _indexBuffer = _positionBuffer = _normalBuffer = _colorBuffer = null;
            _bufferCapacity = 0;
        }
    }
}
