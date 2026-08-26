using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Undo/redo stack for sculpting edits. Two entry shapes:
    /// - Full: a complete vertex+triangle snapshot, for topology-changing edits (Remesh, Reset
    ///   Mesh) where nothing less than the whole mesh can describe the change.
    /// - Delta: just the indices an ordinary brush stroke actually touched plus their PRE-stroke
    ///   positions - the common case, and the whole reason for this split. A stroke typically
    ///   moves a footprint-bounded fraction of the mesh, so a delta is orders of magnitude
    ///   smaller than a full clone at the multi-million-vertex resolutions MeshRemesher supports
    ///   (previously EVERY entry was a full clone - "tens of MB" per step was a known, disclosed
    ///   tradeoff, see [[project_sculpting_application]] memory - this is the fix for that).
    /// Delta entries are self-symmetric: undoing one means swapping the stored "before" values
    /// into those same indices while capturing whatever was CURRENTLY there as the reciprocal
    /// redo entry - undo and redo of a delta are the same "swap stored vs. current" operation,
    /// just walking opposite stacks. See TryUndoDelta/TryRedoDelta.
    public class SculptHistory
    {
        public delegate Vector3 VertexReader(int index);

        private readonly struct Entry
        {
            public readonly bool IsFull;
            public readonly Vector3[] FullVertices;
            public readonly int[] FullTriangles;
            public readonly int[] DeltaIndices;
            public readonly Vector3[] DeltaPositions;

            public Entry(Vector3[] vertices, int[] triangles)
            {
                IsFull = true;
                FullVertices = vertices;
                FullTriangles = triangles;
                DeltaIndices = null;
                DeltaPositions = null;
            }

            public Entry(int[] indices, Vector3[] positions)
            {
                IsFull = false;
                FullVertices = null;
                FullTriangles = null;
                DeltaIndices = indices;
                DeltaPositions = positions;
            }
        }

        // ~15 steps keeps worst-case retained memory bounded - previously this had to guard
        // against 15x tens-of-MB FULL snapshots; now only the (rare) full entries from Remesh/
        // Reset Mesh cost that much, ordinary stroke deltas are negligible by comparison.
        private const int MaxDepth = 15;

        private readonly List<Entry> _undoStack = new List<Entry>();
        private readonly List<Entry> _redoStack = new List<Entry>();

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        /// Call for a topology-changing edit (Remesh, Reset Mesh) with the mesh's current
        /// (pre-edit) state. Clears redo - a fresh edit invalidates whatever was undone.
        public void PushFullUndo(Vector3[] vertices, int[] triangles)
        {
            _undoStack.Add(new Entry(vertices, triangles));
            if (_undoStack.Count > MaxDepth) _undoStack.RemoveAt(0);
            _redoStack.Clear();
        }

        /// Call once a brush stroke ends, with exactly the vertices it touched and each one's
        /// position from BEFORE the stroke first moved it (not its current, post-stroke value -
        /// see SculptableMesh.RecordUndoBeforeIfNeeded). Skip calling this entirely for an empty
        /// delta (a stroke that touched nothing, e.g. a click that missed the mesh) - unlike the
        /// old always-clone-something behavior, that now costs nothing instead of a wasted clone.
        public void PushDeltaUndo(int[] indices, Vector3[] beforePositions)
        {
            _undoStack.Add(new Entry(indices, beforePositions));
            if (_undoStack.Count > MaxDepth) _undoStack.RemoveAt(0);
            _redoStack.Clear();
        }

        /// If the top undo entry is a full snapshot, pops it and returns its vertices/triangles,
        /// pushing currentVertices/currentTriangles onto redo first. Returns false (no-op, stack
        /// untouched) if the top entry is a delta instead, or the stack is empty - callers
        /// should try TryUndoDelta first and only fall back to this.
        public bool TryUndoFull(Vector3[] currentVertices, int[] currentTriangles, out Vector3[] vertices, out int[] triangles)
        {
            if (_undoStack.Count == 0 || !_undoStack[^1].IsFull) { vertices = null; triangles = null; return false; }

            _redoStack.Add(new Entry(currentVertices, currentTriangles));
            if (_redoStack.Count > MaxDepth) _redoStack.RemoveAt(0);

            int last = _undoStack.Count - 1;
            Entry e = _undoStack[last];
            _undoStack.RemoveAt(last);
            vertices = e.FullVertices;
            triangles = e.FullTriangles;
            return true;
        }

        /// If the top undo entry is a delta, pops it, uses readCurrent to capture the CURRENT
        /// value at each of its indices (before the caller overwrites them) as the reciprocal
        /// redo entry, and returns the indices plus the stored pre-stroke positions to restore.
        /// Returns false (no-op) if the top entry is a full snapshot instead, or the stack is
        /// empty.
        public bool TryUndoDelta(VertexReader readCurrent, out int[] indices, out Vector3[] restorePositions)
        {
            if (_undoStack.Count == 0 || _undoStack[^1].IsFull) { indices = null; restorePositions = null; return false; }

            int last = _undoStack.Count - 1;
            Entry e = _undoStack[last];
            _undoStack.RemoveAt(last);

            var currentAtIndices = new Vector3[e.DeltaIndices.Length];
            for (int i = 0; i < e.DeltaIndices.Length; i++) currentAtIndices[i] = readCurrent(e.DeltaIndices[i]);
            _redoStack.Add(new Entry(e.DeltaIndices, currentAtIndices));
            if (_redoStack.Count > MaxDepth) _redoStack.RemoveAt(0);

            indices = e.DeltaIndices;
            restorePositions = e.DeltaPositions;
            return true;
        }

        /// Symmetric to TryUndoFull, walking the redo stack back onto undo.
        public bool TryRedoFull(Vector3[] currentVertices, int[] currentTriangles, out Vector3[] vertices, out int[] triangles)
        {
            if (_redoStack.Count == 0 || !_redoStack[^1].IsFull) { vertices = null; triangles = null; return false; }

            _undoStack.Add(new Entry(currentVertices, currentTriangles));
            if (_undoStack.Count > MaxDepth) _undoStack.RemoveAt(0);

            int last = _redoStack.Count - 1;
            Entry e = _redoStack[last];
            _redoStack.RemoveAt(last);
            vertices = e.FullVertices;
            triangles = e.FullTriangles;
            return true;
        }

        /// Symmetric to TryUndoDelta, walking the redo stack back onto undo.
        public bool TryRedoDelta(VertexReader readCurrent, out int[] indices, out Vector3[] restorePositions)
        {
            if (_redoStack.Count == 0 || _redoStack[^1].IsFull) { indices = null; restorePositions = null; return false; }

            int last = _redoStack.Count - 1;
            Entry e = _redoStack[last];
            _redoStack.RemoveAt(last);

            var currentAtIndices = new Vector3[e.DeltaIndices.Length];
            for (int i = 0; i < e.DeltaIndices.Length; i++) currentAtIndices[i] = readCurrent(e.DeltaIndices[i]);
            _undoStack.Add(new Entry(e.DeltaIndices, currentAtIndices));
            if (_undoStack.Count > MaxDepth) _undoStack.RemoveAt(0);

            indices = e.DeltaIndices;
            restorePositions = e.DeltaPositions;
            return true;
        }

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
        }
    }
}
