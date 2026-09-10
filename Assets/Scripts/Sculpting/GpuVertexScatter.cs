using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpting
{
    /// Pushes just the touched vertices' position/normal/color into a mesh's own GPU vertex
    /// buffer via a compute-shader scatter write, instead of Unity's managed Mesh.vertices/
    /// .normals/.colors setters - those always reupload the ENTIRE array regardless of how many
    /// vertices actually changed, which is the real per-frame ceiling at high polycounts. See
    /// SculptableMesh.ApplyVerticesLocal, the main caller.
    ///
    /// CPU stays fully authoritative for every other system - raycasting, undo, mask, mirror,
    /// and export all keep reading SculptableMesh's own Vector3[]/Color[] arrays exactly as
    /// before. This class only ever WRITES into the mesh's buffer for rendering; nothing reads
    /// it back, so there's no async-readback latency to reason about here (unlike a full GPU
    /// brush rewrite would need).
    ///
    /// One packed upload per call. The dirty set used to go up as four separate structured
    /// buffers (indices, positions, normals, colours): four staging arrays written in lock-step,
    /// four SetData calls, and string-keyed SetBuffer/SetInt lookups on every dispatch. Each dirty
    /// vertex is now one 44-byte record in a single raw buffer that the kernel reads by byte offset
    /// (see DirtyVertex), staged across cores when the set is large.
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

        private static readonly int VertexBufferId = Shader.PropertyToID("_VertexBuffer");
        private static readonly int DirtyVerticesId = Shader.PropertyToID("_DirtyVertices");
        private static readonly int StrideId = Shader.PropertyToID("_Stride");
        private static readonly int PositionOffsetId = Shader.PropertyToID("_PositionOffset");
        private static readonly int NormalOffsetId = Shader.PropertyToID("_NormalOffset");
        private static readonly int ColorOffsetId = Shader.PropertyToID("_ColorOffset");
        private static readonly int DirtyStartId = Shader.PropertyToID("_DirtyStart");
        private static readonly int DirtyCountId = Shader.PropertyToID("_DirtyCount");

        /// One dirty vertex exactly as VertexScatter.compute reads it: a uint index, then position,
        /// normal and colour as raw float bits - 44 bytes. Every field is four bytes wide, so there is
        /// no padding on any platform, and the kernel reads it through a ByteAddressBuffer by offset
        /// rather than as a structured type, because Metal and Vulkan do not agree with D3D on how a
        /// float3 inside a structured element is aligned.
        private struct DirtyVertex
        {
            public uint Index;
            public Vector3 Position;
            public Vector3 Normal;
            public Color Color;
        }

        private const int DirtyVertexStride = 44;

        private GraphicsBuffer _vertexBuffer;
        private uint _stride, _positionOffset, _normalOffset, _colorOffset;

        private GraphicsBuffer _dirtyBuffer;
        private int _bufferCapacity;
        private DirtyVertex[] _staging = Array.Empty<DirtyVertex>();

        // Inputs of the staging pass in flight, held in fields so ParallelPass can split it without a
        // closure allocation per call. Cleared as soon as the pass returns.
        private List<int> _stageIndices;
        private Vector3[] _stagePositions;
        private Vector3[] _stageNormals;
        private Color[] _stageColors;
        private Action<int, int> _stageRange;

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
            if (_bufferCapacity >= count && _dirtyBuffer != null) return;

            _dirtyBuffer?.Dispose();
            _bufferCapacity = Mathf.Max(Mathf.NextPowerOfTwo(count), 64);
            _dirtyBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, _bufferCapacity, DirtyVertexStride);
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
            if (!BeginScatter(count)) return;

            DirtyVertex[] staging = _staging;
            int n = 0;
            foreach (int vi in indices)
            {
                staging[n].Index = (uint)vi;
                staging[n].Position = positions[vi];
                staging[n].Normal = normals[vi];
                staging[n].Color = colors[vi];
                n++;
            }

            Upload(n);
        }

        /// List overload, used by the brush hot path - see SculptableMesh's _affectedList for why
        /// that set stopped being a HashSet. Staged across cores past ParallelPass's threshold: every
        /// entry reads shared arrays and writes only its own staging slot.
        public void ScatterDirty(List<int> indices, int count, Vector3[] positions, Vector3[] normals, Color[] colors)
        {
            if (!BeginScatter(count)) return;

            _stageIndices = indices;
            _stagePositions = positions;
            _stageNormals = normals;
            _stageColors = colors;
            ParallelPass.ForRange(count, _stageRange ?? (_stageRange = StageRange));
            _stageIndices = null;
            _stagePositions = null;
            _stageNormals = null;
            _stageColors = null;

            Upload(count);
        }

        private void StageRange(int start, int end)
        {
            DirtyVertex[] staging = _staging;
            List<int> indices = _stageIndices;
            Vector3[] positions = _stagePositions;
            Vector3[] normals = _stageNormals;
            Color[] colors = _stageColors;
            for (int n = start; n < end; n++)
            {
                int vi = indices[n];
                staging[n].Index = (uint)vi;
                staging[n].Position = positions[vi];
                staging[n].Normal = normals[vi];
                staging[n].Color = colors[vi];
            }
        }

        /// Shared preamble: returns false when there is nothing to do (or no shader to do it
        /// with), otherwise leaves the GPU and staging buffers big enough for `count` entries.
        private bool BeginScatter(int count)
        {
            EnsureShaderLoaded();
            if (_shader == null || _vertexBuffer == null || count == 0) return false;

            EnsureCapacity(count);
            if (_staging.Length < count) _staging = new DirtyVertex[Mathf.Max(Mathf.NextPowerOfTwo(count), 64)];
            return true;
        }

        private void Upload(int n)
        {
            _dirtyBuffer.SetData(_staging, 0, 0, n);

            _shader.SetBuffer(_kernel, VertexBufferId, _vertexBuffer);
            _shader.SetBuffer(_kernel, DirtyVerticesId, _dirtyBuffer);
            _shader.SetInt(StrideId, (int)_stride);
            _shader.SetInt(PositionOffsetId, (int)_positionOffset);
            _shader.SetInt(NormalOffsetId, (int)_normalOffset);
            _shader.SetInt(ColorOffsetId, (int)_colorOffset);

            // A dispatch is capped at 65535 thread groups per axis - at 64 per group that is ~4.2M
            // vertices, which a whole-mesh edit at the densities the remesher produces can reach -
            // so a larger set goes out as consecutive windows over the same uploaded buffer.
            int groupSize = (int)_threadGroupSize;
            int window = 65535 * groupSize;
            for (int start = 0; start < n; start += window)
            {
                int count = Mathf.Min(window, n - start);
                _shader.SetInt(DirtyStartId, start);
                _shader.SetInt(DirtyCountId, count);
                _shader.Dispatch(_kernel, (count + groupSize - 1) / groupSize, 1, 1);
            }
        }

        public void Dispose()
        {
            _vertexBuffer?.Dispose();
            _dirtyBuffer?.Dispose();
            _vertexBuffer = _dirtyBuffer = null;
            _bufferCapacity = 0;
        }
    }
}
