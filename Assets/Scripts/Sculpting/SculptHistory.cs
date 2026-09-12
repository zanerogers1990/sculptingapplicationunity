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
    ///   was a known, disclosed tradeoff; even on a 4,208-vertex mesh a typical stroke's delta
    ///   measured ~19x smaller).
    /// - MaskDelta: the same idea for a mask-paint stroke - touched indices plus their
    ///   pre-stroke mask values. Four bytes a vertex instead of twelve.
    /// - MaskInvert: no payload at all. Inverting the mask is its own inverse, so storing a
    ///   whole-mesh mask delta for it would be pure waste - at a million vertices that is 8MB
    ///   per press of a button people press repeatedly while dialling a selection in.
    /// - VisibilityDelta: the TRIANGLE indices a hide/show operation flipped plus each one's
    ///   previous hidden flag (see SculptableMesh's _hiddenTriangles). Indexed by triangle
    ///   rather than by vertex because hiding is per-polygon - a vertex on the border of a
    ///   hidden region belongs to both hidden and visible triangles, so a per-vertex record
    ///   could not describe the change without ambiguity.
    /// - TopologyDelta: a stroke that CHANGED the vertex and triangle counts as it went - dynamic
    ///   topology (see DynamicTopology.DynamicTopologyRemesher). Carries an ordinary vertex delta
    ///   for the geometry that merely moved, plus the counts to truncate back to, the triangle
    ///   slots that were overwritten along with what they held before, and the attributes of the
    ///   vertices the stroke appended so redo can put them back. A Full snapshot would also be
    ///   correct, and is what Remesh uses - but a stroke refines many times over, and a whole-mesh
    ///   clone per stroke at multi-million-vertex resolutions is precisely the cost the delta
    ///   split above exists to avoid.
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
        /// Reads the CURRENT hidden flag of a triangle, for the reciprocal entry when undoing
        /// or redoing a VisibilityDelta - the visibility counterpart of VertexReader/MaskReader.
        public delegate bool VisibilityReader(int triangleIndex);

        /// Supplies a full snapshot of the CURRENT mesh, for the reciprocal entry when undoing
        /// or redoing a Full. A delegate rather than a parameter because it is needed for
        /// exactly one entry kind, and cloning a multi-million-vertex mesh to hand to a call
        /// that turns out to be undoing a 200-vertex brush stroke is the expensive mistake.
        public delegate void FullCapture(out Vector3[] vertices, out int[] triangles);

        /// Supplies the reciprocal of a TopologyDelta: the mesh's CURRENT counts, what the named
        /// triangle slots hold right now, and the attributes of every vertex at or past
        /// `appendedFrom` - which is exactly the geometry an undo is about to truncate away and a
        /// redo would otherwise have no way to recreate.
        public delegate TopologyPayload TopologyCapture(int appendedFrom, int[] triangleSlots);

        public enum EntryKind { Full, VertexDelta, MaskDelta, MaskInvert, VisibilityDelta, VisibilityInvert, TopologyDelta }

        /// The half of a TopologyDelta that describes STRUCTURE, as against the vertex delta that
        /// rides alongside it describing movement. A class rather than more fields on Entry: it is
        /// null for five of the seven entry kinds, and six more always-null arrays on a struct that
        /// history keeps thousands of is not free.
        public sealed class TopologyPayload
        {
            /// Counts to restore. Truncating to these is what removes appended geometry - no index
            /// below them changes meaning, so every mask value, hidden-triangle flag and vertex
            /// index in an older history entry goes on naming exactly what it always did.
            public readonly int VertexCount;
            public readonly int CornerCount;

            /// Triangle slots to rewrite, and the three corners each should end up holding.
            public readonly int[] TriangleSlots;
            public readonly int[] TriangleCorners;

            /// Attributes of the vertices from AppendedFrom upward, for the direction of travel
            /// that ADDS them back. Empty on the undo side, where truncation needs no payload.
            public readonly int AppendedFrom;
            public readonly Vector3[] AppendedPositions;
            public readonly Vector3[] AppendedNormals;
            public readonly float[] AppendedMask;

            public readonly long Bytes;

            public TopologyPayload(int vertexCount, int cornerCount, int[] triangleSlots, int[] triangleCorners,
                                   int appendedFrom, Vector3[] appendedPositions, Vector3[] appendedNormals,
                                   float[] appendedMask)
            {
                VertexCount = vertexCount;
                CornerCount = cornerCount;
                TriangleSlots = triangleSlots;
                TriangleCorners = triangleCorners;
                AppendedFrom = appendedFrom;
                AppendedPositions = appendedPositions;
                AppendedNormals = appendedNormals;
                AppendedMask = appendedMask;

                long bytes = 48;
                if (triangleSlots != null) bytes += (long)triangleSlots.Length * 4;
                if (triangleCorners != null) bytes += (long)triangleCorners.Length * 4;
                if (appendedPositions != null) bytes += (long)appendedPositions.Length * 12;
                if (appendedNormals != null) bytes += (long)appendedNormals.Length * 12;
                if (appendedMask != null) bytes += (long)appendedMask.Length * 4;
                Bytes = bytes;
            }
        }

        private readonly struct Entry
        {
            public readonly EntryKind Kind;
            public readonly Vector3[] FullVertices;
            public readonly int[] FullTriangles;
            public readonly int[] Indices;
            public readonly Vector3[] Positions;
            public readonly float[] MaskValues;
            public readonly bool[] Flags;
            public readonly TopologyPayload Topology;
            public readonly long Bytes;

            private Entry(EntryKind kind, Vector3[] fullVertices, int[] fullTriangles,
                          int[] indices, Vector3[] positions, float[] maskValues, bool[] flags,
                          TopologyPayload topology = null)
            {
                Kind = kind;
                FullVertices = fullVertices;
                FullTriangles = fullTriangles;
                Indices = indices;
                Positions = positions;
                MaskValues = maskValues;
                Flags = flags;
                Topology = topology;

                long bytes = 32; // object headers and the entry itself - small but not nothing at depth
                if (topology != null) bytes += topology.Bytes;
                if (fullVertices != null) bytes += (long)fullVertices.Length * 12;
                if (fullTriangles != null) bytes += (long)fullTriangles.Length * 4;
                if (indices != null) bytes += (long)indices.Length * 4;
                if (positions != null) bytes += (long)positions.Length * 12;
                if (maskValues != null) bytes += (long)maskValues.Length * 4;
                if (flags != null) bytes += flags.Length;
                Bytes = bytes;
            }

            public static Entry Full(Vector3[] vertices, int[] triangles) =>
                new Entry(EntryKind.Full, vertices, triangles, null, null, null, null);

            public static Entry VertexDelta(int[] indices, Vector3[] positions) =>
                new Entry(EntryKind.VertexDelta, null, null, indices, positions, null, null);

            public static Entry MaskDelta(int[] indices, float[] values) =>
                new Entry(EntryKind.MaskDelta, null, null, indices, null, values, null);

            public static Entry MaskInvert() =>
                new Entry(EntryKind.MaskInvert, null, null, null, null, null, null);

            public static Entry VisibilityDelta(int[] triangleIndices, bool[] flags) =>
                new Entry(EntryKind.VisibilityDelta, null, null, triangleIndices, null, null, flags);

            public static Entry VisibilityInvert() =>
                new Entry(EntryKind.VisibilityInvert, null, null, null, null, null, null);

            public static Entry TopologyDelta(int[] indices, Vector3[] positions, TopologyPayload topology) =>
                new Entry(EntryKind.TopologyDelta, null, null, indices, positions, null, null, topology);
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
            public readonly bool[] Flags;
            public readonly TopologyPayload Topology;

            internal Restore(EntryKind kind, Vector3[] fullVertices, int[] fullTriangles,
                             int[] indices, Vector3[] positions, float[] maskValues, bool[] flags,
                             TopologyPayload topology = null)
            {
                Kind = kind;
                FullVertices = fullVertices;
                FullTriangles = fullTriangles;
                Indices = indices;
                Positions = positions;
                MaskValues = maskValues;
                Flags = flags;
                Topology = topology;
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

        /// Call once a stroke that CHANGED TOPOLOGY ends, with the same vertex delta an ordinary
        /// stroke would push (restricted to vertices that existed before it) plus the structural
        /// record. See the class remarks, and SculptableMesh.EndStrokeUndo for where the two halves
        /// are accumulated.
        public void PushTopologyDelta(int[] indices, Vector3[] beforePositions, TopologyPayload topology) =>
            Push(Entry.TopologyDelta(indices, beforePositions, topology));

        /// The mask equivalent, pushed when a mask-paint stroke ends.
        public void PushMaskDelta(int[] indices, float[] beforeValues) =>
            Push(Entry.MaskDelta(indices, beforeValues));

        /// Records that the whole mask was inverted. Payload-free - see the class remarks.
        public void PushMaskInvert() => Push(Entry.MaskInvert());

        /// Records a hide/show operation: exactly the triangles whose hidden flag changed, and
        /// what that flag was before. Only CHANGED triangles are stored (see
        /// SculptableMesh.ApplyVisibilityChange), so re-hiding an already-hidden region costs
        /// nothing rather than pushing a no-op entry the user then has to undo twice.
        public void PushVisibilityDelta(int[] triangleIndices, bool[] beforeFlags) =>
            Push(Entry.VisibilityDelta(triangleIndices, beforeFlags));

        /// Records that hidden and visible were swapped mesh-wide. Payload-free for exactly the
        /// reason PushMaskInvert is - see the class remarks.
        public void PushVisibilityInvert() => Push(Entry.VisibilityInvert());

        private void Push(Entry entry)
        {
            _undoStack.Add(entry);
            ApproxBytes += entry.Bytes;
            ClearRedo();
        }

        // ------------------------------------------------------------------- undo and redo

        /// Pops the newest undo entry, pushes its reciprocal onto redo, and reports what to
        /// apply. False (stack untouched) if there is nothing to undo.
        public bool TryUndo(VertexReader readVertex, MaskReader readMask, VisibilityReader readVisibility,
                            FullCapture captureFull, TopologyCapture captureTopology, out Restore restore) =>
            TryStep(_undoStack, _redoStack, readVertex, readMask, readVisibility, captureFull, captureTopology, out restore);

        /// Symmetric to TryUndo, walking the redo stack back onto undo.
        public bool TryRedo(VertexReader readVertex, MaskReader readMask, VisibilityReader readVisibility,
                            FullCapture captureFull, TopologyCapture captureTopology, out Restore restore) =>
            TryStep(_redoStack, _undoStack, readVertex, readMask, readVisibility, captureFull, captureTopology, out restore);

        /// One shared implementation for both directions. Undo and redo of any entry kind here
        /// are the identical "take the stored values, hand back whatever is currently in their
        /// place" swap - they differ only in which stack is the source and which is the
        /// destination, so writing them twice only creates two places for a fix to be missed.
        private bool TryStep(List<Entry> from, List<Entry> to, VertexReader readVertex, MaskReader readMask,
                             VisibilityReader readVisibility, FullCapture captureFull,
                             TopologyCapture captureTopology, out Restore restore)
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
                    restore = new Restore(EntryKind.Full, entry.FullVertices, entry.FullTriangles, null, null, null, null);
                    break;
                }
                case EntryKind.VertexDelta:
                {
                    var current = new Vector3[entry.Indices.Length];
                    for (int i = 0; i < entry.Indices.Length; i++) current[i] = readVertex(entry.Indices[i]);
                    reciprocal = Entry.VertexDelta(entry.Indices, current);
                    restore = new Restore(EntryKind.VertexDelta, null, null, entry.Indices, entry.Positions, null, null);
                    break;
                }
                case EntryKind.TopologyDelta:
                {
                    // The reciprocal is captured while the mesh is still in its post-stroke state,
                    // which is the only moment the appended vertices still exist to be recorded.
                    // Once this entry is applied they are truncated away, and nothing else in the
                    // app remembers them.
                    TopologyPayload current = captureTopology(entry.Topology.VertexCount, entry.Topology.TriangleSlots);
                    var currentPositions = new Vector3[entry.Indices.Length];
                    for (int i = 0; i < entry.Indices.Length; i++) currentPositions[i] = readVertex(entry.Indices[i]);
                    reciprocal = Entry.TopologyDelta(entry.Indices, currentPositions, current);
                    restore = new Restore(EntryKind.TopologyDelta, null, null, entry.Indices, entry.Positions,
                                          null, null, entry.Topology);
                    break;
                }
                case EntryKind.MaskDelta:
                {
                    var current = new float[entry.Indices.Length];
                    for (int i = 0; i < entry.Indices.Length; i++) current[i] = readMask(entry.Indices[i]);
                    reciprocal = Entry.MaskDelta(entry.Indices, current);
                    restore = new Restore(EntryKind.MaskDelta, null, null, entry.Indices, null, entry.MaskValues, null);
                    break;
                }
                case EntryKind.VisibilityDelta:
                {
                    var current = new bool[entry.Indices.Length];
                    for (int i = 0; i < entry.Indices.Length; i++) current[i] = readVisibility(entry.Indices[i]);
                    reciprocal = Entry.VisibilityDelta(entry.Indices, current);
                    restore = new Restore(EntryKind.VisibilityDelta, null, null, entry.Indices, null, null, entry.Flags);
                    break;
                }
                case EntryKind.VisibilityInvert:
                {
                    reciprocal = Entry.VisibilityInvert();
                    restore = new Restore(EntryKind.VisibilityInvert, null, null, null, null, null, null);
                    break;
                }
                default:
                {
                    reciprocal = Entry.MaskInvert();
                    restore = new Restore(EntryKind.MaskInvert, null, null, null, null, null, null);
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
