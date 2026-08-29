using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Per-object undo/redo payload store. Holds WHAT changed; EditHistory holds WHEN, and is
    /// what actually decides which object's stack an undo press walks (see its remarks for why
    /// ordering cannot live here). Four entry shapes:
    ///
    /// - Full: a complete vertex+triangle snapshot, for topology-changing edits (Remesh, Reset
    ///   Mesh) where nothing less than the whole mesh can describe the change.
    /// - VertexDelta: just the indices an ordinary brush stroke actually touched plus their
    ///   PRE-stroke positions - the common case, and the whole reason for this split. A stroke
    ///   typically moves a footprint-bounded fraction of the mesh, so a delta is orders of
    ///   magnitude smaller than a full clone at the multi-million-vertex resolutions
    ///   MeshRemesher supports (previously EVERY entry was a full clone - "tens of MB" per step
    ///   was a known, disclosed tradeoff, see [[project_sculpting_application]] memory).
    /// - MaskDelta: the same idea for a mask-paint stroke - touched indices plus their
    ///   pre-stroke mask values. Four bytes a vertex instead of twelve.
    /// - MaskInvert: no payload at all. Inverting the mask is its own inverse, so storing a
    ///   whole-mesh mask delta for it would be pure waste - at a million vertices that is 8MB
    ///   per press of a button people press repeatedly while dialling a selection in.
    ///
    /// Delta entries are self-symmetric: undoing one means swapping the stored "before" values
    /// into those same indices while capturing whatever is CURRENTLY there as the reciprocal
    /// redo entry - undo and redo of a delta are the same "swap stored vs. current" operation,
    /// just walking opposite stacks.
    ///
    /// Deliberately has NO depth or memory cap of its own. Both live in EditHistory, which
    /// evicts through DropOldestUndo/DropNewestRedo - a second cap here could silently drop an
    /// entry EditHistory still had a step pointing at, turning one undo press into a no-op with
    /// nothing to explain it.
    public class SculptHistory
    {
        public delegate Vector3 VertexReader(int index);
        public delegate float MaskReader(int index);

        /// Supplies a full snapshot of the CURRENT mesh, for the reciprocal entry when undoing
        /// or redoing a Full. A delegate rather than a parameter because it is needed for
        /// exactly one entry kind, and cloning a multi-million-vertex mesh to hand to a call
        /// that turns out to be undoing a 200-vertex brush stroke is the expensive mistake.
        public delegate void FullCapture(out Vector3[] vertices, out int[] triangles);

        public enum EntryKind { Full, VertexDelta, MaskDelta, MaskInvert }

        private readonly struct Entry
        {
            public readonly EntryKind Kind;
            public readonly Vector3[] FullVertices;
            public readonly int[] FullTriangles;
            public readonly int[] Indices;
            public readonly Vector3[] Positions;
            public readonly float[] MaskValues;
            public readonly long Bytes;

            private Entry(EntryKind kind, Vector3[] fullVertices, int[] fullTriangles,
                          int[] indices, Vector3[] positions, float[] maskValues)
            {
                Kind = kind;
                FullVertices = fullVertices;
                FullTriangles = fullTriangles;
                Indices = indices;
                Positions = positions;
                MaskValues = maskValues;

                long bytes = 32; // object headers and the entry itself - small but not nothing at depth
                if (fullVertices != null) bytes += (long)fullVertices.Length * 12;
                if (fullTriangles != null) bytes += (long)fullTriangles.Length * 4;
                if (indices != null) bytes += (long)indices.Length * 4;
                if (positions != null) bytes += (long)positions.Length * 12;
                if (maskValues != null) bytes += (long)maskValues.Length * 4;
                Bytes = bytes;
            }

            public static Entry Full(Vector3[] vertices, int[] triangles) =>
                new Entry(EntryKind.Full, vertices, triangles, null, null, null);

            public static Entry VertexDelta(int[] indices, Vector3[] positions) =>
                new Entry(EntryKind.VertexDelta, null, null, indices, positions, null);

            public static Entry MaskDelta(int[] indices, float[] values) =>
                new Entry(EntryKind.MaskDelta, null, null, indices, null, values);

            public static Entry MaskInvert() =>
                new Entry(EntryKind.MaskInvert, null, null, null, null, null);
        }

        /// What an undo/redo press wants applied. `Kind` says which fields are meaningful; see
        /// SculptableMesh.ApplyRestore, the only consumer.
        public readonly struct Restore
        {
            public readonly EntryKind Kind;
            public readonly Vector3[] FullVertices;
            public readonly int[] FullTriangles;
            public readonly int[] Indices;
            public readonly Vector3[] Positions;
            public readonly float[] MaskValues;

            internal Restore(EntryKind kind, Vector3[] fullVertices, int[] fullTriangles,
                             int[] indices, Vector3[] positions, float[] maskValues)
            {
                Kind = kind;
                FullVertices = fullVertices;
                FullTriangles = fullTriangles;
                Indices = indices;
                Positions = positions;
                MaskValues = maskValues;
            }
        }

        private readonly List<Entry> _undoStack = new List<Entry>();
        private readonly List<Entry> _redoStack = new List<Entry>();

        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        /// Everything this object's history is holding onto, undo and redo stacks together.
        /// EditHistory sums this across objects to enforce the global memory budget - see
        /// EditHistory.TrimToLimits for why the budget is checked globally rather than tracked
        /// incrementally.
        public long ApproxBytes { get; private set; }

        // -------------------------------------------------------------------------- pushing

        /// Call for a topology-changing edit (Remesh, Reset Mesh) with the mesh's current
        /// (pre-edit) state. Clears redo - a fresh edit invalidates whatever was undone.
        public void PushFullUndo(Vector3[] vertices, int[] triangles) => Push(Entry.Full(vertices, triangles));

        /// Call once a brush stroke ends, with exactly the vertices it touched and each one's
        /// position from BEFORE the stroke first moved it (not its current, post-stroke value -
        /// see SculptableMesh.RecordUndoBeforeIfNeeded). Skip calling this entirely for an empty
        /// delta (a stroke that touched nothing, e.g. a click that missed the mesh).
        public void PushVertexDelta(int[] indices, Vector3[] beforePositions) =>
            Push(Entry.VertexDelta(indices, beforePositions));

        /// The mask equivalent, pushed when a mask-paint stroke ends.
        public void PushMaskDelta(int[] indices, float[] beforeValues) =>
            Push(Entry.MaskDelta(indices, beforeValues));

        /// Records that the whole mask was inverted. Payload-free - see the class remarks.
        public void PushMaskInvert() => Push(Entry.MaskInvert());

        private void Push(Entry entry)
        {
            _undoStack.Add(entry);
            ApproxBytes += entry.Bytes;
            ClearRedo();
        }

        // ------------------------------------------------------------------- undo and redo

        /// Pops the newest undo entry, pushes its reciprocal onto redo, and reports what to
        /// apply. False (stack untouched) if there is nothing to undo.
        public bool TryUndo(VertexReader readVertex, MaskReader readMask, FullCapture captureFull, out Restore restore) =>
            TryStep(_undoStack, _redoStack, readVertex, readMask, captureFull, out restore);

        /// Symmetric to TryUndo, walking the redo stack back onto undo.
        public bool TryRedo(VertexReader readVertex, MaskReader readMask, FullCapture captureFull, out Restore restore) =>
            TryStep(_redoStack, _undoStack, readVertex, readMask, captureFull, out restore);

        /// One shared implementation for both directions. Undo and redo of any entry kind here
        /// are the identical "take the stored values, hand back whatever is currently in their
        /// place" swap - they differ only in which stack is the source and which is the
        /// destination, so writing them twice only creates two places for a fix to be missed.
        private bool TryStep(List<Entry> from, List<Entry> to, VertexReader readVertex, MaskReader readMask,
                             FullCapture captureFull, out Restore restore)
        {
            restore = default;
            if (from.Count == 0) return false;

            int last = from.Count - 1;
            Entry entry = from[last];
            from.RemoveAt(last);
            ApproxBytes -= entry.Bytes;

            Entry reciprocal;
            switch (entry.Kind)
            {
                case EntryKind.Full:
                {
                    captureFull(out Vector3[] currentVertices, out int[] currentTriangles);
                    reciprocal = Entry.Full(currentVertices, currentTriangles);
                    restore = new Restore(EntryKind.Full, entry.FullVertices, entry.FullTriangles, null, null, null);
                    break;
                }
                case EntryKind.VertexDelta:
                {
                    var current = new Vector3[entry.Indices.Length];
                    for (int i = 0; i < entry.Indices.Length; i++) current[i] = readVertex(entry.Indices[i]);
                    reciprocal = Entry.VertexDelta(entry.Indices, current);
                    restore = new Restore(EntryKind.VertexDelta, null, null, entry.Indices, entry.Positions, null);
                    break;
                }
                case EntryKind.MaskDelta:
                {
                    var current = new float[entry.Indices.Length];
                    for (int i = 0; i < entry.Indices.Length; i++) current[i] = readMask(entry.Indices[i]);
                    reciprocal = Entry.MaskDelta(entry.Indices, current);
                    restore = new Restore(EntryKind.MaskDelta, null, null, entry.Indices, null, entry.MaskValues);
                    break;
                }
                default:
                {
                    reciprocal = Entry.MaskInvert();
                    restore = new Restore(EntryKind.MaskInvert, null, null, null, null, null);
                    break;
                }
            }

            to.Add(reciprocal);
            ApproxBytes += reciprocal.Bytes;
            return true;
        }

        // ------------------------------------------------------------------------ eviction

        /// Drops the OLDEST undo entry - the far end from where undo presses read. Called by
        /// EditHistory when this object's oldest step falls off the global log, so the two stay
        /// exactly in step. Returns false if there was nothing to drop.
        public bool DropOldestUndo()
        {
            if (_undoStack.Count == 0) return false;
            ApproxBytes -= _undoStack[0].Bytes;
            _undoStack.RemoveAt(0);
            return true;
        }

        /// Drops the NEWEST redo entry - the end a redo press would read next. Used when a fresh
        /// edit elsewhere in the scene invalidates the redo chain: this object's redo entries are
        /// the payload for global redo steps that are being discarded, so they have to go too.
        public bool DropNewestRedo()
        {
            if (_redoStack.Count == 0) return false;
            int last = _redoStack.Count - 1;
            ApproxBytes -= _redoStack[last].Bytes;
            _redoStack.RemoveAt(last);
            return true;
        }

        public void ClearRedo()
        {
            for (int i = 0; i < _redoStack.Count; i++) ApproxBytes -= _redoStack[i].Bytes;
            _redoStack.Clear();
        }

        public void Clear()
        {
            _undoStack.Clear();
            _redoStack.Clear();
            ApproxBytes = 0;
        }
    }
}
