using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Scene-level half of the boolean feature: takes selected objects, runs MeshBoolean on
    /// them, and puts the result back into the scene. Sits next to MeshJoiner, which does the
    /// same job for the non-boolean "merge down" it implements - the split of concerns here is
    /// the same one MeshExtractor/MeshBoolean use: geometry stays in a class that knows nothing
    /// about GameObjects, and this file owns the transforms, undo and selection.
    ///
    /// Unlike Join this is NOT destructive of the other objects: the cutters are only hidden
    /// (or, if the user asks, deleted). A cutter is usually worth keeping - the same cutter is
    /// what makes the matching half of a two-part mold, and a subtraction you want to redo at a
    /// different resolution needs it back.
    public static class MeshBooleanTool
    {
        /// Applies `op` between `target` and `cutters`, replacing the target's mesh with the
        /// result. Returns false with `message` explaining why if nothing was changed - the
        /// target is left untouched in that case, including its undo history.
        ///
        /// `hideCutters` takes the cutters out of the viewport afterwards, since a cutter left
        /// visible sits exactly inside the hole it just made and reads as if nothing happened.
        /// Re-showing one is the eye toggle in the object list. Note that this - like the
        /// delete - is NOT part of the undo step: pressing undo brings the target's geometry
        /// back but leaves the cutters as they are.
        public static bool Apply(SculptableMesh target, IReadOnlyList<SculptableMesh> cutters, BooleanOp op, int resolution,
                                 bool hideCutters, bool deleteCutters, out string message)
        {
            message = null;
            if (target == null)
            {
                message = "No target object selected.";
                return false;
            }
            if (cutters == null || cutters.Count == 0)
            {
                message = "Select the target first, then Ctrl+click the other object.";
                return false;
            }

            var operands = new List<MeshBoolean.Operand>(cutters.Count);
            foreach (SculptableMesh cutter in cutters)
            {
                if (cutter == null || cutter == target || cutter.Vertices == null || cutter.Vertices.Length == 0) continue;

                // Into the target's local space, which is where its own vertices live and where
                // the result has to end up (the target keeps its transform - only its mesh is
                // replaced). Same matrix product MeshJoiner builds for CombineInstance.
                Matrix4x4 toTarget = target.transform.worldToLocalMatrix * cutter.transform.localToWorldMatrix;
                Vector3[] src = cutter.Vertices;
                var verts = new Vector3[src.Length];
                for (int i = 0; i < src.Length; i++) verts[i] = toTarget.MultiplyPoint3x4(src[i]);

                // Vertices/Triangles are the authoritative CPU-side arrays - reading the managed
                // Mesh instead would give the stale pre-sculpt shape, since sculpting writes
                // through a compute shader the Mesh getters do not reflect (the same trap
                // MeshJoiner and Remesh both document).
                int[] tris = cutter.Triangles;

                // A mirrored or negatively-scaled cutter comes through with its triangles wound
                // inside-out, which flips its inside/outside sign and would make it ADD material
                // instead of removing it. Flipping the winding back is the fix; the determinant
                // of the 3x3 part is what says whether the transform mirrors.
                if (toTarget.determinant < 0f)
                {
                    var flipped = new int[tris.Length];
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        flipped[t] = tris[t];
                        flipped[t + 1] = tris[t + 2];
                        flipped[t + 2] = tris[t + 1];
                    }
                    tris = flipped;
                }

                operands.Add(new MeshBoolean.Operand(verts, tris, cutter.name));
            }

            if (operands.Count == 0)
            {
                message = "Select the target first, then Ctrl+click the other object.";
                return false;
            }

            Mesh result = MeshBoolean.Build(target.Vertices, target.Triangles, operands, op, resolution, out string error);
            if (result == null)
            {
                message = "Nothing changed - " + error + ".";
                return false;
            }

            // Same convention as every other topology-changing call site (Remesh, Join): a full
            // snapshot first, so Z steps the target back to its pre-boolean shape.
            target.SnapshotForUndo();
            target.ReplaceMesh(result);

            SelectionManager selection = Object.FindFirstObjectByType<SelectionManager>();
            int affected = 0;
            foreach (SculptableMesh cutter in cutters)
            {
                if (cutter == null || cutter == target) continue;
                affected++;
                if (deleteCutters) selection?.DeleteObject(cutter);
                else if (hideCutters && cutter.Visible) selection?.SetVisible(cutter, false);
            }

            // Leaves only the target selected: the cutters are gone or hidden, and a selection
            // still listing them would make the next boolean press try to reuse them.
            selection?.Select(target, false);

            string verb = op == BooleanOp.Subtract ? "Subtracted" : op == BooleanOp.Union ? "United" : "Intersected";
            string fate = deleteCutters ? ", deleted" : hideCutters ? ", hidden" : "";
            message = $"{verb} {affected} object{(affected == 1 ? "" : "s")}{fate}. " +
                      $"{target.Triangles.Length / 3:n0} triangles.";
            return true;
        }
    }
}
