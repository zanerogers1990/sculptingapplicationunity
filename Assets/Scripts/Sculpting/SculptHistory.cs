using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Undo/redo stack for sculpting edits (brush strokes, Remesh, Reset Mesh). Each entry is
    /// a full, self-contained snapshot of the mesh's vertex + triangle buffers - simple and
    /// robust (an entry never depends on any other entry to restore correctly) at the cost of
    /// real memory per entry, which is why depth is capped: at the multi-million-vertex
    /// resolutions MeshRemesher now supports, a single snapshot can be tens of MB. Callers are
    /// expected to pass already-owned arrays (SculptableMesh clones _workingVertices itself
    /// before calling in, since that field is mutated in place every frame during a stroke;
    /// Mesh.triangles doesn't need a second clone since Unity's getter already returns a
    /// fresh copy) - this class does no cloning of its own.
    public class SculptHistory
    {
        private readonly struct Snapshot
        {
            public readonly Vector3[] Vertices;
            public readonly int[] Triangles;

            public Snapshot(Vector3[] vertices, int[] triangles)
            {
                Vertices = vertices;
                Triangles = triangles;
            }
        }

        // ~15 steps keeps worst-case retained memory (15 x tens-of-MB at extreme resolutions)
        // from silently ballooning into hundreds of MB, while still covering a reasonable
        // undo depth at the sizes most sculpting happens at.
        private const int MaxDepth = 15;

        private readonly List<Snapshot> _undoStack = new List<Snapshot>();
        private readonly List<Snapshot> _redoStack = new List<Snapshot>();

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        /// Call before applying an edit, with the mesh's current (pre-edit) state. Clears any
        /// redo history - a fresh edit invalidates whatever was previously undone, same
        /// convention as every other undo system.
        public void PushUndo(Vector3[] vertices, int[] triangles)
        {
            _undoStack.Add(new Snapshot(vertices, triangles));
            if (_undoStack.Count > MaxDepth) _undoStack.RemoveAt(0);
            _redoStack.Clear();
        }

        /// Pops the most recent undo snapshot and returns it via the out params, pushing the
        /// caller-supplied current state onto the redo stack first so Redo can restore it.
        /// Returns false (no output) if there's nothing to undo.
        public bool Undo(Vector3[] currentVertices, int[] currentTriangles, out Vector3[] vertices, out int[] triangles)
        {
            if (_undoStack.Count == 0) { vertices = null; triangles = null; return false; }

            _redoStack.Add(new Snapshot(currentVertices, currentTriangles));
            if (_redoStack.Count > MaxDepth) _redoStack.RemoveAt(0);

            int last = _undoStack.Count - 1;
            Snapshot s = _undoStack[last];
            _undoStack.RemoveAt(last);
            vertices = s.Vertices;
            triangles = s.Triangles;
            return true;
        }

        public bool Redo(Vector3[] currentVertices, int[] currentTriangles, out Vector3[] vertices, out int[] triangles)
        {
            if (_redoStack.Count == 0) { vertices = null; triangles = null; return false; }

            _undoStack.Add(new Snapshot(currentVertices, currentTriangles));
            if (_undoStack.Count > MaxDepth) _undoStack.RemoveAt(0);

            int last = _redoStack.Count - 1;
            Snapshot s = _redoStack[last];
            _redoStack.RemoveAt(last);
            vertices = s.Vertices;
            triangles = s.Triangles;
            return true;
        }

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }
}
