using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Per-triangle hidden geometry - the store behind box/lasso hide.
    public partial class SculptableMesh
    {
        // ------------------------------------------------------------------ hidden geometry

        // Per-TRIANGLE visibility (true = hidden), the store behind box/lasso hide - see
        // RegionSelectTool. Per-triangle rather than per-vertex because that is what hiding a
        // region of a mesh actually means: a vertex on the border of the region is shared with
        // triangles that stay visible, so "is this vertex hidden" has no single right answer,
        // while "is this polygon drawn" always does. Null until something is first hidden, so
        // an ordinary sculpt pays nothing at all for this.
        private bool[] _hiddenTriangles;

        // Derived from _hiddenTriangles (see RefreshVisibility): a vertex is hidden only when
        // EVERY triangle using it is. This is what the brushes read, via QueryNear - border
        // vertices stay sculptable, so the visible edge of a partly-hidden mesh behaves like
        // ordinary surface rather than a frozen wall.
        private bool[] _hiddenVertices;

        // One-bool "is any of this in play" test, so the hot paths (QueryNear, RaycastMesh,
        // ApplyVertices) skip the visibility logic entirely in the normal case.
        private bool _anyHidden;

        // The visible-only index buffer handed to the Mesh, rebuilt in place on each visibility
        // change rather than reallocated.
        private int[] _visibleTriangleScratch;

        private readonly List<int> _visibilityChangedScratch = new List<int>();

        /// True while any part of this mesh is hidden - what the UI reads to enable Show All.
        public bool AnyHidden => _anyHidden;

        /// Bumped by every visibility change, for the same cheap "did anything move" polling
        /// MaskVersion serves.
        public int VisibilityVersion { get; private set; }

        /// Per-triangle hidden flags, or null when nothing is hidden. Handed to
        /// TriangleSpatialGrid.Raycast so every hit test passes through hidden geometry.
        public bool[] HiddenTriangles => _hiddenTriangles;

        /// Number of triangles in the CURRENT topology - what a caller building a per-triangle
        /// selection needs to size its own buffers. Counts what is in USE, which can be less than
        /// _workingTriangles holds - see Triangles.
        public int TriangleCount => _workingTriangles != null ? _cornerCount / 3 : 0;

        /// The per-triangle hidden flags trimmed to the current triangle count, or null when
        /// nothing is hidden - what a save file records (see SceneSerializer).
        public bool[] HiddenTrianglesExact() =>
            _anyHidden && _hiddenTriangles != null ? CloneExact(_hiddenTriangles, TriangleCount) : null;

        /// Puts back hidden flags read from a save file, with NO undo entry - loading a scene is
        /// not an edit, and the history it would join was just cleared (or, for an import, is
        /// about other objects). Ignored when the flag count does not match this mesh's
        /// triangle count, since flags for a different topology would hide the wrong polygons.
        public void RestoreHiddenTriangles(bool[] hidden)
        {
            if (hidden == null || _workingTriangles == null || hidden.Length != TriangleCount) return;
            EnsureVisibilityBuffer();
            Array.Copy(hidden, _hiddenTriangles, hidden.Length);
            RefreshVisibility();
        }

        /// True if this vertex sits strictly inside a hidden region (see _hiddenVertices).
        public bool IsVertexHidden(int index) =>
            _anyHidden && _hiddenVertices != null && index >= 0 && index < _vertexCount && _hiddenVertices[index];

        /// Hides (or shows) the given triangles as one undoable step.
        public void SetTrianglesHidden(IReadOnlyList<int> triangleIndices, bool hidden)
        {
            if (triangleIndices == null || triangleIndices.Count == 0 || _workingTriangles == null) return;
            EnsureVisibilityBuffer();

            _visibilityChangedScratch.Clear();
            for (int k = 0; k < triangleIndices.Count; k++)
            {
                int t = triangleIndices[k];
                if (t < 0 || t >= TriangleCount || _hiddenTriangles[t] == hidden) continue;
                _visibilityChangedScratch.Add(t);
            }
            CommitVisibilityChange(_visibilityChangedScratch, hidden);
        }

        /// Brings every hidden triangle back, as one undoable step. No-op (and no undo entry)
        /// when nothing is hidden.
        public void ShowAllGeometry()
        {
            if (!_anyHidden || _hiddenTriangles == null) return;

            _visibilityChangedScratch.Clear();
            for (int t = 0; t < TriangleCount; t++)
                if (_hiddenTriangles[t]) _visibilityChangedScratch.Add(t);
            CommitVisibilityChange(_visibilityChangedScratch, false);
        }

        /// Swaps hidden for visible across the whole mesh (ZBrush's Ctrl+Shift+I). Payload-free
        /// in history - inverting is its own inverse, exactly as InvertMask is, and at a couple
        /// of million triangles a stored delta for a button people press repeatedly while
        /// dialling in a selection would be megabytes a press.
        public void InvertVisibleGeometry()
        {
            if (!_anyHidden || _hiddenTriangles == null) return;
            _history.PushVisibilityInvert();
            EditHistory.RecordMeshEdit(this);
            InvertVisibilityWithoutUndo();
        }

        private void InvertVisibilityWithoutUndo()
        {
            EnsureVisibilityBuffer();
            for (int t = 0, n = TriangleCount; t < n; t++) _hiddenTriangles[t] = !_hiddenTriangles[t];
            RefreshVisibility();
        }

        private void EnsureVisibilityBuffer()
        {
            int triCount = TriangleCount;
            // Grown, not resized to fit: like every other per-element buffer here this one may run
            // ahead of the count (see Triangles), and reallocating it down to the count would throw
            // away the flags for triangles that are still hidden.
            if (_hiddenTriangles == null || _hiddenTriangles.Length < triCount)
                Array.Resize(ref _hiddenTriangles, Mathf.Max(triCount, _workingTriangles.Length / 3));
        }

        // Records the undo entry for `changed` (whose flags are still at their OLD values),
        // writes the new value, and rebuilds everything derived from visibility. Every hide/
        // show entry point funnels through here so none of them can forget the undo push, the
        // version bump or the index-buffer rebuild.
        private void CommitVisibilityChange(List<int> changed, bool newValue)
        {
            if (changed.Count == 0) return;

            int[] indices = changed.ToArray();
            var before = new bool[indices.Length];
            for (int k = 0; k < indices.Length; k++)
            {
                before[k] = _hiddenTriangles[indices[k]];
                _hiddenTriangles[indices[k]] = newValue;
            }

            _history.PushVisibilityDelta(indices, before);
            EditHistory.RecordMeshEdit(this);
            RefreshVisibility();
        }

        /// Undo/redo counterpart of CommitVisibilityChange - writes the stored flags back with
        /// no history push of its own (going through the public path would push a fresh entry
        /// from inside an undo, which is how an undo stack ends up unable to reach past the last
        /// thing you undid - same reasoning as InvertMaskWithoutUndo).
        private void RestoreVisibilityDelta(int[] indices, bool[] flags)
        {
            if (indices == null || flags == null) return;
            EnsureVisibilityBuffer();
            for (int k = 0; k < indices.Length; k++)
            {
                int t = indices[k];
                if (t < 0 || t >= TriangleCount) continue;
                _hiddenTriangles[t] = flags[k];
            }
            RefreshVisibility();
        }

        /// Rebuilds _anyHidden, _hiddenVertices and the mesh's index buffer from
        /// _hiddenTriangles. O(triangle count), paid once per hide/show gesture - never per
        /// frame.
        ///
        /// The vertex buffer keeps every vertex, hidden ones included: only the INDEX buffer
        /// shrinks. That keeps every vertex index in this class (undo deltas, mask, symmetry
        /// maps, the spatial grids) valid and stable across a hide, which is what lets hiding
        /// be a display-only concern that no other system has to know about.
        private void RefreshVisibility()
        {
            VisibilityVersion++;
            int triCount = TriangleCount;

            _anyHidden = false;
            if (_hiddenTriangles != null)
                for (int t = 0; t < triCount; t++)
                    if (_hiddenTriangles[t]) { _anyHidden = true; break; }

            if (!_anyHidden)
            {
                _hiddenTriangles = null;
                _hiddenVertices = null;
                SyncMeshFromWorkingArrays();
                _mesh.SetTriangles(_workingTriangles, 0, _cornerCount, 0, false);
                BindGpuScatter();
                return;
            }

            // Start every vertex hidden and clear it on the first VISIBLE triangle that uses
            // it - that is exactly the "hidden only if all its triangles are" rule, in one
            // pass. A vertex no triangle references at all stays flagged hidden, which is
            // honest: nothing draws it either way.
            if (_hiddenVertices == null || _hiddenVertices.Length != _workingVertices.Length)
                _hiddenVertices = new bool[_workingVertices.Length];
            for (int i = 0; i < _vertexCount; i++) _hiddenVertices[i] = true;

            int visibleCount = 0;
            for (int t = 0; t < triCount; t++)
            {
                if (_hiddenTriangles[t]) continue;
                visibleCount++;
                int b = t * 3;
                _hiddenVertices[_workingTriangles[b]] = false;
                _hiddenVertices[_workingTriangles[b + 1]] = false;
                _hiddenVertices[_workingTriangles[b + 2]] = false;
            }

            if (_visibleTriangleScratch == null || _visibleTriangleScratch.Length != visibleCount * 3)
                _visibleTriangleScratch = new int[visibleCount * 3];
            int w = 0;
            for (int t = 0; t < triCount; t++)
            {
                if (_hiddenTriangles[t]) continue;
                int b = t * 3;
                _visibleTriangleScratch[w++] = _workingTriangles[b];
                _visibleTriangleScratch[w++] = _workingTriangles[b + 1];
                _visibleTriangleScratch[w++] = _workingTriangles[b + 2];
            }

            // calculateBounds:false, because SyncMeshFromWorkingArrays just recalculated them
            // over every vertex by assigning positions - letting SetTriangles redo it would
            // shrink the bounds to the visible part alone, and both the culling volume and
            // MeshBoundsFitInsideTriangleGrid's check want the whole mesh.
            SyncMeshFromWorkingArrays();
            _mesh.SetTriangles(_visibleTriangleScratch, 0, false);
            // The MeshCollider is deliberately NOT re-cooked here - it keeps the full mesh.
            // Nothing hit-tests through it (RaycastMesh goes to _triangleGrid, which does honor
            // visibility), and re-cooking runs into tens of milliseconds at high polycounts,
            // which would turn every hide drag into a visible hitch for no observable gain.
            BindGpuScatter();
        }

        // A topology change (Remesh/Join/an undo that crosses one) leaves the old per-triangle
        // flags describing triangles that no longer exist, so visibility starts fresh - the same
        // choice, for the same reason, that the mask already makes at those points.
        private void ResetVisibility()
        {
            _hiddenTriangles = null;
            _hiddenVertices = null;
            _visibleTriangleScratch = null;
            _anyHidden = false;
            VisibilityVersion++;
        }
    }
}
