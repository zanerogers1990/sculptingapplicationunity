using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace Sculpting
{
    /// Undo payload capture and restore - stroke deltas, mask deltas, full snapshots. The ORDER of
    /// undo steps across objects lives in EditHistory; this is only what one object needs to put
    /// itself back.
    public partial class SculptableMesh
    {
        // See SculptHistory - snapshot-based undo/redo for brush strokes, Remesh, and Reset
        // Mesh. Owned here (not SculptController) since this class already owns all the mesh
        // state a snapshot needs to capture/restore.
        private readonly SculptHistory _history = new SculptHistory();

        // Undo/redo ORDER lives in EditHistory, not here - a per-object stack cannot say
        // whether the last thing the user did was on THIS object (see EditHistory's remarks).
        // These are the hooks it drives this object's own payload stack through.
        public long HistoryBytes => _history.ApproxBytes;

        public void ClearHistory() => _history.Clear();

        public bool DropOldestUndoEntry() => _history.DropOldestUndo();

        public bool DropNewestRedoEntry() => _history.DropNewestRedo();

        public void ResetMesh()
        {
            // VertexCount, not _originalVertices.Length: the baseline buffer carries the same
            // spare capacity the live one does (see Vertices), so copying its whole length would
            // write the spare tail's filler over live geometry.
            Array.Copy(_originalVertices, _workingVertices, _vertexCount);
            _spatialGrid = null;
            ApplyVertices();
        }

        /// Call before Remesh/Reset Mesh (topology-changing edits) so Undo can revert them - a
        /// full clone is unavoidable here since nothing less can describe a topology change.
        /// Both arrays are cloned from the CPU-authoritative copies, never read back from the Mesh:
        /// while any geometry is hidden (see RefreshVisibility) the Mesh's index buffer holds only
        /// the VISIBLE triangles, so a snapshot taken through Mesh.triangles silently dropped every
        /// hidden one - and restoring that snapshot deleted them.
        /// Ordinary brush strokes use BeginStrokeUndo/EndStrokeUndo instead - see their remarks.
        public void SnapshotForUndo()
        {
            // Trimmed to what is in use, not a clone of the whole buffer: RestoreSnapshot tells
            // "same topology" from "rebuilt" by comparing a snapshot's lengths against the live
            // COUNTS, so a snapshot padded out to capacity would never match itself.
            _history.PushFullUndo(CloneExact(_workingVertices, _vertexCount),
                                  CloneExact(_workingTriangles, _cornerCount));
            EditHistory.RecordMeshEdit(this);
        }

        // Accumulates each touched vertex's PRE-stroke position the first time a held stroke
        // touches it, committed as one delta undo entry when the stroke ends - see
        // RecordUndoBeforeIfNeeded/EndStrokeUndo. Replaces the old up-front full-mesh clone on
        // every stroke START (paid regardless of what the stroke ends up touching, or even if it
        // misses the mesh entirely) with a cost proportional to what actually moved. Two
        // parallel lists rather than a Dictionary<int,Vector3> - insertion order doesn't matter
        // here and this avoids dictionary overhead for what's typically hundreds-to-thousands of
        // entries per stroke.
        private readonly List<int> _strokeDeltaIndices = new List<int>();

        private readonly List<Vector3> _strokeDeltaBefore = new List<Vector3>();

        // Slot into _strokeDeltaBefore per vertex, or -1 for "not touched this stroke". Serves
        // two jobs at once: it is the O(1)-readable record of where the surface was when this
        // stroke began (StrokeStartPosition - see its remarks for why a brush needs that), AND it
        // is the "have I already recorded this vertex" membership test RecordUndoBeforeIfNeeded
        // needs. It used to be only the first, with a parallel HashSet<int> answering the second -
        // which meant a hash probe per touched vertex per dab, and a wide stroke reports hundreds
        // of thousands of those per frame. A slot of -1 already means exactly "not recorded yet",
        // so the set was pure duplicated state on the hottest write path in the app.
        private int[] _strokeRecordSlot;

        /// Where a vertex was when the CURRENT stroke started, or its live position if this
        /// stroke hasn't moved it yet (which is the same thing - an untouched vertex is still
        /// exactly where the stroke found it). Lets a brush measure against the surface it began
        /// with rather than the surface its own earlier dabs already deposited; see
        /// SculptController's Clay area-plane, which would otherwise chase its own output.
        public Vector3 StrokeStartPosition(int index)
        {
            if (_strokeRecordSlot == null || _strokeRecordSlot.Length != _workingVertices.Length)
                return _workingVertices[index];
            int slot = _strokeRecordSlot[index];
            return slot >= 0 ? _strokeDeltaBefore[slot] : _workingVertices[index];
        }

        /// Bulk StrokeStartPosition over a whole candidate list. Hoists the null/length guard and
        /// the two field loads out of a loop that runs once per candidate per dab - at wide-brush
        /// footprints that loop is hundreds of thousands of iterations a frame, and the guard was
        /// re-testing the same two unchanged conditions on every one of them.
        public void CopyStrokeStartPositions(List<int> indices, NativeArray<Vector3> destination)
        {
            Vector3[] verts = _workingVertices;
            int[] slots = _strokeRecordSlot != null && _strokeRecordSlot.Length == verts.Length
                ? _strokeRecordSlot : null;

            if (slots == null)
            {
                for (int k = 0; k < indices.Count; k++) destination[k] = verts[indices[k]];
                return;
            }

            List<Vector3> before = _strokeDeltaBefore;
            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];
                int slot = slots[i];
                destination[k] = slot >= 0 ? before[slot] : verts[i];
            }
        }

        // The mask equivalent of the three lists above, filled by RecordMaskBeforeIfNeeded and
        // committed by the same EndStrokeUndo. No slot array to go with it: nothing needs to read
        // "what was the mask here when this stroke started" mid-stroke the way Clay's area-plane
        // needs StrokeStartPosition for geometry.
        private readonly List<int> _maskStrokeIndices = new List<int>();

        private readonly List<float> _maskStrokeBefore = new List<float>();

        private readonly HashSet<int> _maskRecordedIndices = new HashSet<int>();

        /// Call once on stroke start (mouse-press) to clear the previous stroke's accumulator.
        public void BeginStrokeUndo()
        {
            ReleaseStrokeSlots();
            _strokeDeltaIndices.Clear();
            _strokeDeltaBefore.Clear();
        }

        /// The mask-paint equivalent, called on mouse-press in mask mode. Separate from
        /// BeginStrokeUndo because mask painting takes its own path through SculptController and
        /// never reaches that one - the two modes are mutually exclusive, so exactly one
        /// accumulator is ever live at a time.
        public void BeginMaskStroke()
        {
            _maskStrokeIndices.Clear();
            _maskStrokeBefore.Clear();
            _maskRecordedIndices.Clear();
        }

        /// Call from PaintMask BEFORE overwriting _mask[index], so the FIRST touch during this
        /// stroke captures the true pre-stroke value - mask paint ramps a vertex over many frames
        /// of a held drag, and recording every frame would make one undo press step back a single
        /// frame's worth of paint instead of the whole stroke.
        private void RecordMaskBeforeIfNeeded(int index)
        {
            if (!_maskRecordedIndices.Add(index)) return;
            _maskStrokeIndices.Add(index);
            _maskStrokeBefore.Add(_mask[index]);
        }

        /// Resets only the slots this stroke actually used, rather than refilling the whole
        /// per-vertex array with -1 on every stroke - O(touched) instead of O(vertex count),
        /// which matters because a stroke can be a click that touches nothing at all.
        private void ReleaseStrokeSlots()
        {
            if (_strokeRecordSlot == null) return;
            for (int k = 0; k < _strokeDeltaIndices.Count; k++)
            {
                int vi = _strokeDeltaIndices[k];
                if (vi >= 0 && vi < _strokeRecordSlot.Length) _strokeRecordSlot[vi] = -1;
            }
        }

        /// Call from a brush's per-candidate write site, BEFORE overwriting
        /// _workingVertices[index], so the FIRST touch during this stroke captures the true
        /// pre-stroke value - a vertex touched across multiple frames of the same held stroke
        /// only records once (its value from before the very first touch, not the most recent).
        public void RecordUndoBeforeIfNeeded(int index)
        {
            EnsureStrokeSlots();
            if (_strokeRecordSlot[index] >= 0) return;

            _strokeRecordSlot[index] = _strokeDeltaIndices.Count;
            _strokeDeltaIndices.Add(index);
            _strokeDeltaBefore.Add(_workingVertices[index]);
        }

        // Reallocated (and reset) whenever topology changed under us - a Remesh mid-session
        // leaves the old array sized to the old vertex count, and indexing it would either throw
        // or, worse, silently return another vertex's slot. Whatever the accumulator held at that
        // point describes the OLD vertex set, so it is dropped rather than carried across:
        // restoring those indices into the new topology would corrupt geometry rather than undo
        // it, and the topology change pushed its own full-mesh entry anyway (SnapshotForUndo).
        private void EnsureStrokeSlots()
        {
            if (_strokeRecordSlot != null && _strokeRecordSlot.Length == _workingVertices.Length) return;

            _strokeDeltaIndices.Clear();
            _strokeDeltaBefore.Clear();
            _strokeRecordSlot = new int[_workingVertices.Length];
            for (int i = 0; i < _strokeRecordSlot.Length; i++) _strokeRecordSlot[i] = -1;
        }

        /// Call once when a stroke ends (mouse-up) to commit whatever was recorded as one undo
        /// entry - a no-op if the stroke touched nothing (e.g. a click that missed the mesh),
        /// which now costs nothing instead of the old unconditional full-mesh clone up front.
        /// Idempotent: clears the accumulator after pushing, so calling this more than once
        /// without an intervening BeginStrokeUndo (e.g. a caller's release-detection firing from
        /// more than one place) harmlessly no-ops on the second call instead of pushing the same
        /// delta twice.
        public void EndStrokeUndo()
        {
            // Commits whichever accumulator actually ran. In practice never both - sculpting and
            // mask painting are separate input modes - but handling them independently means the
            // one call site SculptController already has (HandleStrokeEndCommit, which fires on
            // every mouse release regardless of mode) covers both without knowing which is live.
            if (_strokeDeltaIndices.Count > 0)
            {
                _history.PushVertexDelta(_strokeDeltaIndices.ToArray(), _strokeDeltaBefore.ToArray());
                EditHistory.RecordMeshEdit(this);
                ReleaseStrokeSlots();
                _strokeDeltaIndices.Clear();
                _strokeDeltaBefore.Clear();
            }

            if (_maskStrokeIndices.Count > 0)
            {
                _history.PushMaskDelta(_maskStrokeIndices.ToArray(), _maskStrokeBefore.ToArray());
                EditHistory.RecordMeshEdit(this);
                _maskStrokeIndices.Clear();
                _maskStrokeBefore.Clear();
                _maskRecordedIndices.Clear();
            }
        }

        /// Steps this object's own history back one entry. Called by EditHistory, which owns the
        /// decision of WHICH object to step - never call it directly, or undo stops following the
        /// order the edits actually happened in. Returns false when this object has nothing left
        /// to undo, which tells EditHistory to skip this step and try the one before it.
        public bool ApplyUndoStep()
        {
            if (!_history.TryUndo(ReadVertex, ReadMask, ReadVisibility, CaptureFull,
                                  out SculptHistory.Restore restore)) return false;
            ApplyRestore(restore);
            return true;
        }

        public bool ApplyRedoStep()
        {
            if (!_history.TryRedo(ReadVertex, ReadMask, ReadVisibility, CaptureFull,
                                  out SculptHistory.Restore restore)) return false;
            ApplyRestore(restore);
            return true;
        }

        private Vector3 ReadVertex(int index) => _workingVertices[index];

        private float ReadMask(int index) => _mask[index];

        // Null _hiddenTriangles means nothing is hidden, so every triangle reads back visible -
        // which is exactly right for the reciprocal of an entry that is about to re-hide them.
        private bool ReadVisibility(int triangleIndex) =>
            _hiddenTriangles != null && triangleIndex >= 0 && triangleIndex < TriangleCount
            && _hiddenTriangles[triangleIndex];

        private void CaptureFull(out Vector3[] vertices, out int[] triangles)
        {
            // Trimmed to the counts for the same reason SnapshotForUndo is - see its remarks.
            vertices = CloneExact(_workingVertices, _vertexCount);
            // Not _mesh.triangles, which omits hidden triangles - see SnapshotForUndo.
            triangles = CloneExact(_workingTriangles, _cornerCount);
        }

        private void ApplyRestore(SculptHistory.Restore restore)
        {
            switch (restore.Kind)
            {
                case SculptHistory.EntryKind.Full:
                    RestoreSnapshot(restore.FullVertices, restore.FullTriangles);
                    break;
                case SculptHistory.EntryKind.VertexDelta:
                    RestoreDelta(restore.Indices, restore.Positions);
                    break;
                case SculptHistory.EntryKind.MaskDelta:
                    RestoreMaskDelta(restore.Indices, restore.MaskValues);
                    break;
                case SculptHistory.EntryKind.MaskInvert:
                    InvertMaskWithoutUndo();
                    break;
                case SculptHistory.EntryKind.VisibilityDelta:
                    RestoreVisibilityDelta(restore.Indices, restore.Flags);
                    break;
                case SculptHistory.EntryKind.VisibilityInvert:
                    InvertVisibilityWithoutUndo();
                    break;
            }
        }

        /// Fast-path restore for a delta undo/redo entry - writes the given positions directly
        /// into _workingVertices at the given indices (no full-array reassignment) and reuses
        /// the exact same incremental update path (ApplyVerticesLocal) a live brush stroke
        /// already goes through for normals/bounds/triangle-grid/cavity/GPU upload - no separate
        /// propagation logic needed.
        private void RestoreDelta(int[] indices, Vector3[] positions)
        {
            for (int k = 0; k < indices.Length; k++)
                _workingVertices[indices[k]] = positions[k];
            ApplyVerticesLocal(indices);
        }

        /// Mask counterpart of RestoreDelta. Writes both places the mask is stored - _mask, which
        /// the brushes read, and _cavityColors[i].g, which the shader reads - then scatters just
        /// those vertices to the GPU, the same footprint-scoped upload PaintMask itself uses.
        ///
        /// Bounds-checked per index rather than trusting the entry: a Remesh between painting the
        /// mask and undoing it resizes _mask (see RestoreSnapshot), and while the full snapshot
        /// for that Remesh is a NEWER entry and so is always undone first, restoring stale indices
        /// into a shorter array would be an exception rather than a visible mistake, so it is
        /// worth the cheap guard.
        private void RestoreMaskDelta(int[] indices, float[] values)
        {
            if (_mask == null) return;

            _paintMaskScratch.Clear();
            for (int k = 0; k < indices.Length; k++)
            {
                int i = indices[k];
                if (i < 0 || i >= _vertexCount) continue;
                _mask[i] = Mathf.Clamp01(values[k]);
                Color c = _cavityColors[i];
                c.g = _mask[i];
                _cavityColors[i] = c;
                _paintMaskScratch.Add(i);
            }
            MaskVersion++;

            EnsureGpuScatter();
            _gpuScatter.ScatterDirty(_paintMaskScratch, _paintMaskScratch.Count, _workingVertices, _workingNormals, _cavityColors);
        }

        /// The body of InvertMask without the history push - what undoing (or redoing) a
        /// MaskInvert entry runs. Going back through InvertMask itself would push a fresh entry
        /// from inside an undo, which is how an undo stack ends up unable to reach past the last
        /// thing you undid.
        private void InvertMaskWithoutUndo()
        {
            for (int i = 0; i < _vertexCount; i++)
            {
                _mask[i] = 1f - _mask[i];
                Color c = _cavityColors[i];
                c.g = _mask[i];
                _cavityColors[i] = c;
            }
            _mesh.colors = _cavityColors;
            MaskVersion++;
        }

        // Undoing/redoing a brush stroke never changes topology (only positions), so the
        // common case is as cheap as an ordinary stroke's own ApplyVertices() call - only
        // crossing a Remesh boundary needs the heavier full rebuild (adjacency, cavity
        // buffer, collider). Triangle array LENGTH is used as the same-topology check rather
        // than a full content comparison - in this app the only thing that ever changes
        // topology is Remesh, and two different remesh results coincidentally sharing an
        // exact triangle count is vanishingly unlikely, so this is a deliberate, cheap
        // approximation rather than an oversight.
        private void RestoreSnapshot(Vector3[] vertices, int[] triangles)
        {
            // Against the COUNTS, not the arrays' lengths - a snapshot is always exact, while the
            // live buffers may carry spare capacity (see Vertices).
            bool sameTopology = triangles.Length == _cornerCount && vertices.Length == _vertexCount;
            if (sameTopology)
            {
                // Copied IN rather than adopted as the live buffer. A snapshot is exactly
                // VertexCount long, and swapping it in would leave _workingVertices shorter than
                // every buffer that shadows it (normals, cavity, mask, the sync baseline) - which
                // the "has this changed under me" length guards on those would read as a topology
                // change and answer by reallocating, silently discarding the normals and mask
                // this path is not supposed to touch at all.
                Array.Copy(vertices, _workingVertices, _vertexCount);
                ApplyVertices();
                return;
            }

            // Full rebuild, mirroring Remesh()'s tail. Note: unlike Remesh(), this doesn't
            // recompute the spherical UVs MeshRemesher assigns - harmless today since
            // SculptPBR's vertex shader has no TEXCOORD0 input at all (ConfigureGpuVertexLayout
            // below drops it from the buffer entirely regardless, same as Remesh()'s tail).
            _mesh.Clear();
            _mesh.indexFormat = vertices.Length > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16;
            ConfigureGpuVertexLayout(_mesh, vertices.Length);
            _mesh.vertices = vertices;
            _mesh.triangles = triangles;
            _mesh.RecalculateNormals();
            _mesh.RecalculateBounds();
            _workingNormals = _mesh.normals;

            _originalVertices = (Vector3[])vertices.Clone();
            _workingVertices = vertices;
            _workingTriangles = triangles;
            SetGeometryCounts(vertices.Length, triangles.Length);
            _spatialGrid = null;
            BuildAdjacency();
            RebuildTriangleGrid();
            _cavityColors = new Color[_workingVertices.Length];
            _cavityRaw = new float[_workingVertices.Length];
            _mask = new float[_workingVertices.Length];
            // The new topology has no mapping onto the old mask, so it starts blank - which is
            // itself a mask change any watcher needs to hear about (a live extract preview built
            // from the pre-undo mask is describing geometry that no longer exists). Matches what
            // ReplaceMesh already does for the same reason.
            MaskVersion++;
            ResetVisibility();
            RecomputeCavity();
            _mesh.colors = _cavityColors;
            BindGpuScatter();

            ReseatCollider();

            // Undoing or redoing across a Remesh is a topology change like any other - see
            // MirrorLink.OnTopologyChanged.
            if (LinkedMirror != null) LinkedMirror.OnTopologyChanged(this);
        }
    }
}
