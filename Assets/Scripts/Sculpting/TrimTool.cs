using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Scene-level half of the Trim tool: takes the object the gesture was drawn over, runs
    /// MeshTrimmer on it, and puts the result back. Same split of concerns as
    /// MeshBoolean/MeshBooleanTool and MeshExtractor/MaskExtractController - the geometry lives
    /// in a class that knows nothing about GameObjects, cameras, undo or selection, and this file
    /// owns all four.
    public static class TrimTool
    {
        /// Cuts `target` against `region` swept along `cam`'s view direction and replaces its
        /// mesh with the result, as one undoable step. Returns false with `message` explaining
        /// why when nothing was cut, in which case the target is left completely untouched -
        /// including its undo history.
        ///
        /// `removeCovered` false crops down to the shape instead of cutting it away.
        public static bool Apply(SculptableMesh target, Camera cam, ScreenRegionMask region,
                                 bool removeCovered, out string message)
        {
            if (target == null) { message = "No object selected."; return false; }
            if (cam == null) { message = "No camera."; return false; }
            if (region == null) { message = "Region too small."; return false; }

            // The authoritative CPU-side arrays, not the managed Mesh's: sculpting writes through
            // a compute shader that Mesh.vertices does not reflect, so reading the Mesh would cut
            // the shape the object had before it was ever sculpted. The same trap MeshJoiner,
            // MeshBooleanTool and Remesh all document.
            // Spare capacity dropped first so the lengths below are the counts, which is what the
            // trimmer's own index bookkeeping assumes - see SculptableMesh.CompactBuffers.
            target.CompactBuffers();
            Vector3[] verts = target.Vertices;
            int[] tris = target.Triangles;
            if (verts == null || verts.Length == 0 || tris == null || tris.Length < 3)
            {
                message = "No geometry.";
                return false;
            }

            // Every place the mesh is drawn - the object, then its live mirror copies (see
            // MirrorRepeater). A cut drawn over a copy cuts the vertices the copy shows, which is
            // the original's own mesh. Copied out: the shared list is reused by the next caller.
            var modelToViews = new List<Matrix4x4>(
                RegionSelectTool.MeshFrameMatrices(target, cam.worldToCameraMatrix));
            Rect viewport = cam.pixelRect;

            int openLoops = 0;
            int capTriangles = 0;
            int cuts = 0;
            string lastError = null;
            long trianglesBefore = tris.Length / 3;

            SymmetryGroup symmetry = Symmetry(target, removeCovered);
            for (int f = 0; f < modelToViews.Count; f++)
            {
                // A crop keeps what one shape covers; running it again through another copy would
                // keep only the overlap, so a crop stops at the first copy it actually lands on.
                if (!removeCovered && cuts > 0) break;

                Matrix4x4 modelToView = modelToViews[f];
                Matrix4x4 mvp = cam.projectionMatrix * modelToView;
                for (int k = 0; k < symmetry.Count; k++)
                {
                    MeshTrimmer.Result result = MeshTrimmer.Trim(
                        verts, tris, mvp, modelToView, viewport, symmetry[k], region, removeCovered);

                    if (!result.Success)
                    {
                        // A mirrored pass that lands on empty space is ordinary (a shape drawn
                        // over one arm has no counterpart when the other arm is turned away), so
                        // this only becomes a failure if EVERY pass misses.
                        lastError = result.Error;
                        continue;
                    }

                    verts = result.Vertices;
                    tris = result.Triangles;
                    openLoops += result.OpenLoops;
                    capTriangles += result.CapTriangles;
                    cuts++;
                }
            }

            if (cuts == 0)
            {
                message = "Nothing trimmed - " + (lastError ?? "the shape covered nothing") + ".";
                return false;
            }

            var mesh = new Mesh { name = target.name };
            // Required above 65535 vertices, which any sculpted mesh here is well past.
            if (verts.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.triangles = tris;
            // Recomputed for the whole result rather than carried across. The shell's normals
            // were already exactly this - area-weighted from the topology, see
            // SculptableMesh.RecomputeNormalsLocal - so untouched geometry is unchanged, and the
            // new cap gets normals it has no other source for.
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            // One undo step, like every other topology-changing call site (Remesh, Join, Boolean):
            // Z steps the object back to its pre-trim shape.
            target.ReplaceMeshUndoable(mesh);

            // Reported as before -> after plus the cut face's own share, rather than as "removed
            // N": the face is filled at the density of the surface around it, so a big cut can
            // easily add more triangles than it took away, and a bare "removed -5,522" reads as a
            // bug rather than as the tool working.
            long after = tris.Length / 3;
            message = $"Trimmed: {trianglesBefore:n0} -> {after:n0} triangles, "
                    + $"{capTriangles:n0} of them the new cut face.";
            if (openLoops > 0)
                message += $" {openLoops} cut edge{(openLoops == 1 ? "" : "s")} could not be closed cleanly.";
            return true;
        }

        // Symmetry is applied by running one whole cut per symmetry op (mirror / radial copy),
        // rather than by widening the coverage test to "covered under ANY copy". Each pass then has a single, well
        // defined swept surface, which is what the cap has to be built on - a test that answered
        // for two prisms at once would leave the cap with no coherent surface to follow where
        // they meet.
        //
        // Only for the cut-away direction. Sequential passes REMOVE the union of the mirrored
        // shapes, which is exactly right there; for a crop they would keep only the intersection,
        // which is the opposite of what mirroring should mean, so a crop runs unmirrored.
        private static SymmetryGroup Symmetry(SculptableMesh target, bool removeCovered)
        {
            if (!removeCovered) return SymmetryGroup.Trivial;
            var mirror = target.GetComponent<MirrorController>();
            return mirror != null ? mirror.GetSymmetry() : SymmetryGroup.Trivial;
        }
    }
}
