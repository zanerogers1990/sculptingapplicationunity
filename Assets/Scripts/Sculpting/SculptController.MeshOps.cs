using System;
using System.IO;
using UnityEngine;

namespace Sculpting
{
    /// Whole-mesh operations on the selected object: Reset, Remesh, symmetry repair, Export.
    public partial class SculptController
    {
        public void ResetMesh()
        {
            if (sculptableMesh == null) return;
            EndActiveDrags();
            sculptableMesh.SnapshotForUndo();
            sculptableMesh.ResetMesh();
        }

        public void Remesh()
        {
            if (sculptableMesh == null) return;
            sculptableMesh.RemeshUndoable(remeshResolution);
        }

        /// Live symmetry report for the selected object - pairs found, centreline size, and how
        /// many vertices have no counterpart. See SymmetryOps.Status for why it is recomputed
        /// rather than cached.
        public string SymmetryStatus() => SymmetryOps.Status(sculptableMesh, symmetryAxis, symmetryToleranceScale);

        /// Copies one side of the selected object onto the other through the vertex
        /// correspondence map. Returns a short result string for the UI, since "nothing visibly
        /// happened" and "the map could not pair anything" look identical in the viewport.
        public string MakeSymmetric(bool sourceIsPositive)
        {
            if (sculptableMesh == null) return "No object selected";
            EndActiveDrags();

            int changed = SymmetryOps.MakeSymmetric(sculptableMesh, symmetryAxis, symmetryToleranceScale,
                                                    sourceIsPositive, out int pairs, out int unmatched,
                                                    out int carried);

            string axis = SymmetryOps.AxisName(symmetryAxis);
            string from = sourceIsPositive ? "+" + axis : "-" + axis;
            string to = sourceIsPositive ? "-" + axis : "+" + axis;

            // Nothing was modified in this case - mirroring through a partial correspondence
            // tears the surface instead of repairing it (see SymmetryOps.MaxUnmatchedFraction).
            // The message names the alternative, because "too asymmetric to mirror" with no way
            // forward is what makes a refusal read as the tool being broken.
            if (changed == SymmetryOps.TooAsymmetric)
                return $"Too asymmetric to match up: {unmatched} vertices have no counterpart " +
                       $"across {axis} ({pairs} pairs do). Nudging vertices would tear those " +
                       $"apart - use Cut & Mirror {from} to {to} instead, which rebuilds that " +
                       "side outright.";

            if (changed < 0) return "No geometry to mirror";
            if (pairs == 0) return $"Nothing paired across {axis} - raise Match Tolerance";
            if (changed == 0) return $"Already symmetric across {axis} - {pairs} pairs match";

            // The unmatched count rides along on success too: it is the part of the model that
            // has no counterpart to be mirrored onto, and leaving it out is what let a partial
            // mirror look like a complete one. It is now carried along with the surface around it
            // rather than left standing (see SymmetryTools.CarryUnmatched), so the message says
            // which of the two happened to it.
            string leftover = unmatched > 0
                ? (carried > 0 ? $", {carried} of {unmatched} unmatched carried along"
                               : $", {unmatched} unmatched")
                : string.Empty;
            return $"Mirrored {from} onto {to}: {changed} of {pairs} pairs{leftover}";
        }

        /// Cuts the selected object at the symmetry plane and rebuilds the far side as a
        /// reflection of the near one. The unconditional version of MakeSymmetric: it needs no
        /// vertex correspondence, so it is what to reach for when MakeSymmetric reports the model
        /// is too asymmetric to match up (see SymmetryOps.MirrorAndWeld).
        public string MirrorAndWeld(bool sourceIsPositive)
        {
            if (sculptableMesh == null) return "No object selected";
            EndActiveDrags();

            string axis = SymmetryOps.AxisName(symmetryAxis);
            string from = sourceIsPositive ? "+" + axis : "-" + axis;
            string to = sourceIsPositive ? "-" + axis : "+" + axis;

            if (!SymmetryOps.MirrorAndWeld(sculptableMesh, symmetryAxis, symmetryToleranceScale,
                                           sourceIsPositive,
                                           out int kept, out int discarded, out int vertexCount))
                return $"Nothing on the {from} side to mirror";

            // Reports what was THROWN AWAY as well as what was built, because that is the part
            // this operation cannot undo by pressing the other direction - the far side's own
            // shape is gone, and a user who meant the opposite direction should see that
            // immediately rather than discover it later.
            return $"Cut & mirrored {from} onto {to}: kept {kept} triangles, " +
                   $"replaced {discarded}, now {vertexCount} vertices";
        }

        /// Snaps the centreline onto the mirror plane and welds the duplicate vertices that
        /// leaves - the repair for a model joined from two mirrored halves, whose seam is two
        /// coincident shells rather than one shared edge loop.
        public string SymmetryCleanup()
        {
            if (sculptableMesh == null) return "No object selected";
            EndActiveDrags();

            if (!SymmetryOps.Cleanup(sculptableMesh, symmetryAxis, symmetryToleranceScale,
                                     out int snapped, out int welded))
                return "No geometry to clean up";

            string axis = SymmetryOps.AxisName(symmetryAxis);
            if (snapped == 0 && welded == 0) return $"Already clean across {axis} - nothing to do";
            if (welded == 0) return $"Snapped {snapped} vertices onto {axis} - no duplicates found";
            return snapped == 0
                ? $"Welded {welded} duplicate vertices"
                : $"Snapped {snapped} onto {axis}, welded {welded} duplicate vertices";
        }

        // Fixed destination rather than a save-file dialog - EditorUtility.SaveFilePanel only
        // exists in the Editor and would silently vanish once this ships as a standalone
        // build, whereas Environment.GetFolderPath is plain .NET and resolves the real
        // Desktop path in both. A proper save/load feature (with its own file-picker UX) is
        // planned as separate future work; this is just "get the current sculpt out to a
        // file I can open elsewhere" for now.
        public string Export()
        {
            if (sculptableMesh == null) return null;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string folder = Path.Combine(desktop, "SculptExports");
            string path = ObjExporter.Export(sculptableMesh, folder);
            if (path != null) Debug.Log($"[Sculpt] Exported to {path}");
            return path;
        }
    }
}
