using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Mask painting, filtering and upload, and the masked Transpose/Scale deform the gizmo drives.
    public partial class SculptableMesh
    {
        // Per-vertex mask: 0 = fully sculptable (default), 1 = fully protected. Every brush
        // loop multiplies its falloff weight by (1 - Mask[i]), so a masked area simply doesn't
        // move under any brush. Reset to all-zero whenever topology changes (Awake/Remesh/
        // RestoreSnapshot) - a mask painted before a Remesh has no well-defined mapping onto
        // the remeshed vertex set, so starting fresh is the honest behavior rather than a
        // stale/misaligned carryover. Mirrored into _cavityColors' G channel (see PaintMask/
        // EncodeCavityAt) for SculptPBR's mask tint - .r stays cavity, .g is mask, so the two
        // overlays are independent.
        private float[] _mask;

        public float[] Mask => _mask;

        /// Bumped by every operation that changes _mask (painting, inverting, restoring, and the
        /// wholesale reset a topology change forces). Lets a watcher tell "the mask moved" from
        /// "nothing happened" without diffing an array that can be millions of entries long -
        /// same cheap-poll idiom SelectionManager.SelectionVersion already serves for the UI.
        /// Read by MaskExtractController to keep a live extract preview following the brush.
        public int MaskVersion { get; private set; }

        // Reused across PaintMask calls so a held mask-paint drag doesn't allocate a fresh
        // HashSet every frame - same "grow, don't reallocate" pattern. Holds exactly the
        // candidates that passed PaintMask's own dist &lt;= radius check, i.e. the vertices
        // actually touched this call (QueryNear's candidate list is a superset - see its
        // remarks).
        private readonly HashSet<int> _paintMaskScratch = new HashSet<int>();

        /// Paints (amount > 0) or erases (amount < 0) mask over a local-space brush footprint -
        /// does not move any vertex or touch normals/bounds/the triangle-raycast grid, just the
        /// per-vertex Mask value and its vertex-color visualization. Every brush's weight
        /// calculation reads Mask[i] to skip masked vertices - see SculptController's
        /// Apply*BrushLocal methods and SelectGrab below.
        ///
        /// hardness (0-1) reshapes the falloff instead of just scaling it: 0 is a smoothstep
        /// across the WHOLE radius (gradual, light-at-the-edges - ZBrush/Blender's "soft"
        /// feel), 1 collapses the smoothstep band down to zero width so every vertex inside
        /// the radius gets the full weight immediately (a hard cutoff at the edge, "hard"
        /// feel) - matches how most sculpting apps' brush hardness works: an inner radius that
        /// grows from 0 to the full brush radius as hardness increases.
        public void PaintMask(Vector3 localPoint, float radius, float amount, float hardness)
        {
            List<int> candidates = QueryNear(localPoint, radius);
            float innerRadius = radius * Mathf.Clamp01(hardness);
            float falloffSpan = Mathf.Max(radius - innerRadius, 1e-5f);

            _paintMaskScratch.Clear();
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(_workingVertices[i], localPoint);
                if (dist > radius) continue;

                float weight;
                if (dist <= innerRadius)
                {
                    weight = 1f;
                }
                else
                {
                    float t01 = 1f - (dist - innerRadius) / falloffSpan;
                    weight = t01 * t01 * (3f - 2f * t01); // smoothstep
                }
                RecordMaskBeforeIfNeeded(i);
                _mask[i] = Mathf.Clamp01(_mask[i] + amount * weight);

                Color c = _cavityColors[i];
                c.g = _mask[i];
                _cavityColors[i] = c;
                _paintMaskScratch.Add(i);
            }
            MaskVersion++;

            // Masking changes what the model looks like and is a deliberate step in the work, so
            // the timelapse records it - but only when the brush actually touched something, so a
            // paint drag out over empty space doesn't hold the recording open. Mask painting
            // never goes near the vertex apply paths that report for the sculpting brushes.
            if (_paintMaskScratch.Count > 0) SculptActivity.ReportEdit(this, localPoint);

            // Held mask-paint drags call this every frame (see SculptController.ApplyMaskPaint),
            // so at high polycounts this needs the same footprint-scoped GPU write
            // ApplyVerticesLocal uses instead of a full _mesh.colors= reassignment - see
            // GpuVertexScatter remarks. Position/normal are unchanged by mask painting; scattering
            // them anyway alongside the updated color is the same accepted redundant-write pattern
            // ApplyVerticesLocal already relies on for its neighbor-only entries.
            EnsureGpuScatter();
            _gpuScatter.ScatterDirty(_paintMaskScratch, _paintMaskScratch.Count, _workingVertices, _workingNormals, _cavityColors);
        }

        /// Flips every vertex's mask value (protected <-> sculptable), ZBrush Ctrl+I/Blender
        /// "Invert Mask" style. O(vertex count) - fine as a one-off button click, not something
        /// called per-frame like PaintMask.
        public void InvertMask()
        {
            // Payload-free undo entry: inverting is its own inverse, so there is nothing to
            // store. Worth the special case rather than reusing a mask delta - that would be a
            // whole-mesh array (8MB at a million vertices) for a button people press repeatedly
            // while dialling a selection in.
            _history.PushMaskInvert();
            EditHistory.RecordMeshEdit(this);
            SculptActivity.ReportEdit(this);
            InvertMaskWithoutUndo();
        }

        /// Restores a saved mask (see SceneSerializer). Deliberately pushes no undo entry,
        /// unlike InvertMask: its only caller is scene loading, which wipes history wholesale
        /// (EditHistory.Clear) because every object an entry could name was just destroyed.
        ///
        /// Mirrors InvertMask's body exactly: the
        /// mask is stored twice - in _mask (what the brushes read) and in _cavityColors[i].g
        /// (what the shader reads to tint protected areas) - so writing only _mask would restore
        /// the behaviour with no visual feedback at all. Silently ignores a length mismatch
        /// rather than throwing: that means the file's geometry and mask disagree, and a mesh
        /// with no mask is a far better failure than a half-applied one.
        public void SetMask(float[] mask)
        {
            if (mask == null || _mask == null || mask.Length != _vertexCount) return;
            for (int i = 0; i < _vertexCount; i++)
            {
                _mask[i] = Mathf.Clamp01(mask[i]);
                Color c = _cavityColors[i];
                c.g = _mask[i];
                _cavityColors[i] = c;
            }
            _mesh.colors = _cavityColors;
            MaskVersion++;
        }

        // Vertices whose mask a region op is about to clear, gathered once rather than
        // allocated per call - see ClearMask.
        private readonly List<int> _maskClearScratch = new List<int>();

        /// Sets a HARD mask value over an explicit set of vertices as one undoable step - what
        /// box/lasso masking uses (see RegionSelectTool), as against PaintMask's soft, ramped
        /// brush footprint. Routes through the same mask-stroke accumulator a painted stroke
        /// uses, so it lands in history in exactly the same shape and needs no new entry kind.
        /// Vertices already at `value` are skipped entirely: a second drag over the same region
        /// then records nothing instead of pushing an undo step that visibly does nothing.
        public void SetMaskOnVertices(IReadOnlyList<int> indices, float value)
        {
            if (_mask == null || indices == null || indices.Count == 0) return;
            value = Mathf.Clamp01(value);

            BeginMaskStroke();
            _paintMaskScratch.Clear();
            for (int k = 0; k < indices.Count; k++)
            {
                int i = indices[k];
                if (i < 0 || i >= _vertexCount || Mathf.Approximately(_mask[i], value)) continue;
                RecordMaskBeforeIfNeeded(i);
                _mask[i] = value;
                Color c = _cavityColors[i];
                c.g = value;
                _cavityColors[i] = c;
                _paintMaskScratch.Add(i);
            }
            if (_paintMaskScratch.Count == 0) return;

            MaskVersion++;
            // Box/lasso masking and Clear Mask, same reasoning as PaintMask's report above. Past
            // the early-out, so a drag that changed nothing stays out of the timelapse.
            SculptActivity.ReportEdit(this);
            UploadMaskColors();
            EndStrokeUndo();
        }

        /// Unmasks everything, as one undoable step. The complement of InvertMask for a mask
        /// built up by dragging regions, where "start over" is a far more common wish than
        /// "flip what I have".
        public void ClearMask()
        {
            if (_mask == null) return;
            _maskClearScratch.Clear();
            for (int i = 0; i < _vertexCount; i++)
                if (_mask[i] > 0f) _maskClearScratch.Add(i);
            SetMaskOnVertices(_maskClearScratch, 0f);
        }

        /// The whole-mask edits ZBrush keeps next to Invert/Clear: soften the mask's edge, crispen
        /// it, or move it outward/inward by one ring of vertices per step.
        public enum MaskFilter { Blur, Sharpen, Grow, Shrink }

        private float[] _maskFilterScratch = Array.Empty<float>();

        /// Applies `filter` to the whole mask, `steps` times, as one undoable step. Each step works
        /// over the mesh's edge rings, so its reach is in vertices, not world units - on a dense
        /// mesh one Grow is a thin band, which is what ZBrush's mask filters do too.
        ///
        /// Every step computes all new values from the previous step's before writing any (the
        /// same Jacobi order Smooth uses), so the result does not depend on vertex order - which
        /// is what keeps a symmetric mask symmetric.
        public void FilterMask(MaskFilter filter, int steps = 1)
        {
            if (_mask == null || _vertexCount == 0 || steps <= 0) return;
            MeshAdjacency topology = EnsureAdjacency();
            int[] starts = topology.NeighborStart, counts = topology.NeighborCount, neighbors = topology.NeighborIndices;

            if (_maskFilterScratch.Length < _vertexCount * 2) _maskFilterScratch = new float[_vertexCount * 2];
            float[] work = _maskFilterScratch;
            // work[0..n) holds the current values, work[n..2n) the next step's.
            Array.Copy(_mask, work, _vertexCount);
            int n = _vertexCount;

            for (int step = 0; step < steps; step++)
            {
                for (int i = 0; i < n; i++)
                {
                    float m = work[i];
                    int from = starts[i], to = from + counts[i];
                    float result = m;
                    if (to > from)
                    {
                        switch (filter)
                        {
                            case MaskFilter.Grow:
                                for (int k = from; k < to; k++) result = Mathf.Max(result, work[neighbors[k]]);
                                break;
                            case MaskFilter.Shrink:
                                for (int k = from; k < to; k++) result = Mathf.Min(result, work[neighbors[k]]);
                                break;
                            default:
                            {
                                // Double for the same order-independence reason as
                                // GetNeighborAverage: a mirror twin sums the same values in a
                                // different order.
                                double sum = 0d;
                                for (int k = from; k < to; k++) sum += work[neighbors[k]];
                                float average = (float)(sum / (to - from));
                                result = filter == MaskFilter.Blur
                                    ? 0.5f * (m + average)
                                    // Unsharp mask - push away from the neighbourhood average -
                                    // then a contrast step about 0.5. The unsharp part alone does
                                    // nothing to a wide, gentle ramp; the contrast part alone
                                    // shifts the edge instead of tightening it. Together they
                                    // narrow the ramp from both sides.
                                    : Mathf.Clamp01((Mathf.Clamp01(m + (m - average)) - 0.5f) * 1.25f + 0.5f);
                                break;
                            }
                        }
                    }
                    work[n + i] = result;
                }
                Array.Copy(work, n, work, 0, n);
            }

            BeginMaskStroke();
            _paintMaskScratch.Clear();
            for (int i = 0; i < n; i++)
            {
                float value = work[i];
                if (value == _mask[i]) continue;
                RecordMaskBeforeIfNeeded(i);
                _mask[i] = value;
                Color c = _cavityColors[i];
                c.g = value;
                _cavityColors[i] = c;
                _paintMaskScratch.Add(i);
            }
            if (_paintMaskScratch.Count == 0) return;

            MaskVersion++;
            SculptActivity.ReportEdit(this);
            UploadMaskColors();
            EndStrokeUndo();
        }

        // Above this fraction of the mesh, a scatter write is the wrong tool: it stages one
        // index/position/normal/color entry per vertex into GPU buffers sized for the whole
        // mesh, which is strictly more work than the single full-array upload it exists to
        // avoid. Region ops routinely touch most of the mesh at once (masking everything but a
        // limb, clearing the mask), unlike the brush footprints ScatterDirty was written for.
        private const float FullUploadVertexFraction = 0.25f;

        /// Pushes whatever is in _paintMaskScratch to the GPU - the scatter path for a small
        /// footprint, a whole-mesh reupload past FullUploadVertexFraction. See
        /// SyncMeshFromWorkingArrays for why the big path reassigns positions and normals too
        /// rather than colors alone.
        private void UploadMaskColors()
        {
            if (_paintMaskScratch.Count > _vertexCount * FullUploadVertexFraction)
            {
                SyncMeshFromWorkingArrays();
                return;
            }
            EnsureGpuScatter();
            _gpuScatter.ScatterDirty(_paintMaskScratch, _paintMaskScratch.Count, _workingVertices, _workingNormals, _cavityColors);
        }

        /// True if anything at all is masked - what TransformGizmo checks to decide whether a
        /// Transpose/Scale drag should move the whole object's Transform (nothing masked, the
        /// original behaviour) or deform the vertices around the frozen masked region instead
        /// (see BeginMaskedTransform). Early-outs on the first masked vertex rather than
        /// scanning the whole array, since it's called on every gizmo mouse-press.
        public bool HasMask
        {
            get
            {
                if (_mask == null) return false;
                for (int i = 0; i < _vertexCount; i++)
                    if (_mask[i] > 0.001f) return true;
                return false;
            }
        }

        // Pre-drag vertex positions for a masked Transpose/Scale drag. Every frame of the drag
        // re-derives the whole result from THESE rather than compounding onto last frame's
        // output - compounding a per-frame delta would let rounding drift accumulate over a
        // long drag, and (worse) makes dragging back to the start not actually return to the
        // start. Non-null exactly while such a drag is in progress; see BeginMaskedTransform.
        private Vector3[] _maskedTransformBase;

        /// True while a masked Transpose/Scale drag is in progress. Anything that would change the
        /// vertex count has to stand down for the duration: the drag re-derives every vertex from
        /// _maskedTransformBase on each frame, and that snapshot is indexed by vertex, so appending
        /// vertices under it leaves the drag reading past its end.
        public bool IsMaskedTransformActive => _maskedTransformBase != null;

        /// Starts a mask-aware whole-object transform: instead of moving the Transform (which
        /// would drag the masked region along with everything else), the drag deforms the
        /// vertex buffer, holding fully-masked vertices exactly where they are and blending
        /// smoothly through partially-masked ones. This is what makes "mask the torso, then
        /// Transpose-drag" pull a limb out of the surface - ZBrush's core Transpose-with-mask
        /// behaviour - rather than sliding the whole mesh sideways.
        ///
        /// Returns false (and starts nothing) when nothing is masked, so the caller can fall
        /// back to the plain Transform drag - with no mask, deforming every vertex by the same
        /// matrix and moving the Transform are visually identical, and the Transform is both
        /// free and undoable by simply dragging back.
        ///
        /// Records undo up front for every vertex the drag can touch (mask &lt; 1), reusing the
        /// ordinary stroke-delta accumulator - at drag start _workingVertices still holds the
        /// pre-drag values, which is exactly what RecordUndoBeforeIfNeeded captures.
        public bool BeginMaskedTransform()
        {
            if (_workingVertices == null || _mask == null || !HasMask) return false;

            _maskedTransformBase = (Vector3[])_workingVertices.Clone();

            BeginStrokeUndo();
            for (int i = 0; i < _vertexCount; i++)
                if (_mask[i] < 0.999f) RecordUndoBeforeIfNeeded(i);

            return true;
        }

        /// Applies one frame of a masked transform drag. localDelta is the drag's accumulated
        /// transform expressed in THIS object's local space (the gizmo builds it there - the
        /// object's own origin is the gizmo pivot, so a rotation/scale about the pivot is just
        /// a rotation/scale about local zero). Per-vertex weight is 1 - mask, so a fully masked
        /// vertex is pinned and a half-masked one travels half as far, which is what gives the
        /// pulled limb a smooth root instead of a torn ring.
        public void ApplyMaskedTransform(Matrix4x4 localDelta)
        {
            if (_maskedTransformBase == null) return;

            for (int i = 0; i < _vertexCount; i++)
            {
                Vector3 basePos = _maskedTransformBase[i];
                float weight = 1f - _mask[i];
                if (weight <= 0f) { _workingVertices[i] = basePos; continue; }

                Vector3 moved = localDelta.MultiplyPoint3x4(basePos);
                _workingVertices[i] = weight >= 1f ? moved : Vector3.LerpUnclamped(basePos, moved, weight);
            }

            // Cheap path - see ApplyVertices(bool). EndMaskedTransform does the full one.
            ApplyVertices(false);
        }

        /// Ends a masked transform drag: one full ApplyVertices so the triangle-raycast grid,
        /// cavity tint and collider catch up with the deformed surface, then commits the undo
        /// entry BeginMaskedTransform opened.
        public void EndMaskedTransform()
        {
            if (_maskedTransformBase == null) return;
            _maskedTransformBase = null;

            ApplyVertices();
            EndStrokeUndo();

            ReseatCollider();
        }
    }
}
