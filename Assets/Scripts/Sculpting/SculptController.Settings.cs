using System;
using System.IO;
using UnityEngine;

namespace Sculpting
{
    /// Brush settings save/load, and the whole-mesh operations: Reset, Remesh, symmetry repair,
    /// Export.
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
            sculptableMesh.SnapshotForUndo();
            sculptableMesh.Remesh(remeshResolution);
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

        // ------------------------------------------------------------------- save/load state

        /// Every brush setting worth persisting, as a flat JsonUtility-serializable block (see
        /// SceneSerializer). Lives INSIDE SculptController, and Capture/Apply touch the private
        /// backing fields directly, deliberately: the alternative was ~25 new public properties
        /// existing only for the serializer, and a set of per-brush arrays that have no public
        /// surface at all. Keeping it here means a future brush setting is remembered by editing
        /// one class rather than three.
        ///
        /// Per-brush arrays (strength/radius/polarity/accumulate/accumulate-strength) are saved
        /// alongside the live values because they ARE the user's tuning: without them, loading a
        /// file would restore the current brush correctly and silently reset every other brush's
        /// remembered feel to defaults the first time it was selected.
        [Serializable]
        public class FalloffCurveEntry
        {
            public int brush;
            public Vector2[] points;
        }

        [Serializable]
        public class Settings
        {
            public float brushStrength;
            public float brushRadius;
            // Inverted ("world" rather than "screen") so a file from before screen-space sizing
            // existed - where JsonUtility leaves it false - opens in the new default mode.
            public bool worldSpaceBrushSize;
            public float brushScreenRadius;
            public int currentBrush;
            public bool isPositive;
            public bool accumulate;
            public float accumulateStrength;
            public bool frontFacingOnly;
            public bool buildUpOnHold;
            public float surfaceRelax;
            public float poseRigidity;
            public int poseSegments;

            public float clayHeightFactor;
            public float clayTipRoundness;
            public float clayEdgeSoftness;
            public float clayPressureRadiusInfluence;
            public float clayPressureSoftnessInfluence;

            public bool useAlpha;
            public int alphaType;
            public float alphaRotation;
            public float alphaScale;
            public bool invertAlpha;

            public float creasePinch;
            public float creaseDepthFactor;
            public float maskHardness;

            public float pressureFloor;
            public float pressureCurve;

            public int remeshResolution;

            public bool useBurstJobs;
            public bool showWireframeGizmo;

            public bool maskPaintMode;

            // Per-brush memory, indexed by BrushType. Length is validated on Apply rather than
            // trusted - a file written by an older build (or hand-edited) can legitimately have
            // fewer entries than today's BrushType has members.
            public float[] perBrushStrength;
            // Custom falloff curves, one entry per brush that has one (see BrushFalloff).
            public FalloffCurveEntry[] falloffCurves;
            // No perBrushRadius counterpart: radius is one value shared by every brush (see
            // _brushStrengthPerType), saved as `brushRadius` above. A file written before that
            // change still carries the old per-brush array; JsonUtility drops the unknown field
            // and `brushRadius` restores the size that was actually in hand when it was saved.
            public bool[] perBrushPolarity;
            public bool[] perBrushAccumulate;
            public float[] perBrushAccumulateStrength;
            public bool[] perBrushFrontFacingOnly;
        }

        public Settings CaptureSettings()
        {
            // Flush the live values into the per-brush arrays first. BrushStrength writes
            // through on every set, but currentBrush's own slot is the one that can be mid-edit,
            // and Capture must not save a stale entry for the brush in hand.
            int cur = (int)currentBrush;
            _brushStrengthPerType[cur] = brushStrength;
            _brushPolarity[cur] = isPositive;
            _brushAccumulate[cur] = accumulate;
            _accumulateStrengthPerType[cur] = accumulateStrength;
            _brushFrontFacingOnly[cur] = frontFacingOnly;

            return new Settings
            {
                brushStrength = brushStrength,
                brushRadius = brushRadius,
                worldSpaceBrushSize = !screenSpaceBrushSize,
                brushScreenRadius = brushScreenRadius,
                currentBrush = cur,
                isPositive = isPositive,
                accumulate = accumulate,
                accumulateStrength = accumulateStrength,
                frontFacingOnly = frontFacingOnly,
                buildUpOnHold = buildUpOnHold,
                surfaceRelax = surfaceRelax,
                poseRigidity = poseRigidity,
                poseSegments = poseSegments,

                clayHeightFactor = clayHeightFactor,
                clayTipRoundness = clayTipRoundness,
                clayEdgeSoftness = clayEdgeSoftness,
                clayPressureRadiusInfluence = clayPressureRadiusInfluence,
                clayPressureSoftnessInfluence = clayPressureSoftnessInfluence,

                useAlpha = useAlpha,
                alphaType = (int)alphaType,
                alphaRotation = alphaRotation,
                alphaScale = alphaScale,
                invertAlpha = invertAlpha,

                creasePinch = creasePinch,
                creaseDepthFactor = creaseDepthFactor,
                maskHardness = maskHardness,

                pressureFloor = pressureFloor,
                pressureCurve = pressureCurve,

                remeshResolution = remeshResolution,
                useBurstJobs = useBurstJobs,
                showWireframeGizmo = showWireframeGizmo,

                maskPaintMode = IsMaskPaintMode,

                perBrushStrength = (float[])_brushStrengthPerType.Clone(),
                perBrushPolarity = (bool[])_brushPolarity.Clone(),
                perBrushAccumulate = (bool[])_brushAccumulate.Clone(),
                perBrushAccumulateStrength = (float[])_accumulateStrengthPerType.Clone(),
                perBrushFrontFacingOnly = (bool[])_brushFrontFacingOnly.Clone(),
                falloffCurves = CaptureFalloffCurves(),
            };
        }

        private FalloffCurveEntry[] CaptureFalloffCurves()
        {
            var entries = new System.Collections.Generic.List<FalloffCurveEntry>();
            for (int b = 0; b < _falloffCurves.Length; b++)
                if (_falloffCurves[b] != null)
                    entries.Add(new FalloffCurveEntry { brush = b, points = _falloffCurves[b].Points.ToArray() });
            return entries.ToArray();
        }

        private void ApplyFalloffCurves(FalloffCurveEntry[] entries)
        {
            for (int b = 0; b < _falloffCurves.Length; b++) _falloffCurves[b] = null;
            if (entries == null) return;
            foreach (FalloffCurveEntry e in entries)
            {
                if (e == null || e.points == null || e.brush < 0 || e.brush >= _falloffCurves.Length) continue;
                var curve = new BrushFalloffCurve();
                curve.Points.AddRange(e.points);
                curve.Normalize();
                _falloffCurves[e.brush] = curve;
            }
        }

        /// Routes through the public CLAMPING properties wherever one exists rather than
        /// assigning the private fields, so a corrupt or hand-edited file can't push a value
        /// outside the range the rest of the code assumes (ClayFalloff divides by
        /// clayEdgeSoftness, for one - a zero there would produce NaN vertex positions).
        public void ApplySettings(Settings s)
        {
            if (s == null) return;

            CopyPerBrush(s.perBrushStrength, _brushStrengthPerType);
            CopyPerBrush(s.perBrushPolarity, _brushPolarity);
            CopyPerBrush(s.perBrushAccumulate, _brushAccumulate);
            CopyPerBrush(s.perBrushAccumulateStrength, _accumulateStrengthPerType);
            CopyPerBrush(s.perBrushFrontFacingOnly, _brushFrontFacingOnly);
            ApplyFalloffCurves(s.falloffCurves);

            ClayHeightFactor = s.clayHeightFactor;
            ClayTipRoundness = s.clayTipRoundness;
            ClayEdgeSoftness = s.clayEdgeSoftness;
            ClayPressureRadiusInfluence = s.clayPressureRadiusInfluence;
            ClayPressureSoftnessInfluence = s.clayPressureSoftnessInfluence;

            UseAlpha = s.useAlpha;
            AlphaType = (BrushAlphaType)Mathf.Clamp(s.alphaType, 0, System.Enum.GetValues(typeof(BrushAlphaType)).Length - 1);
            AlphaRotation = s.alphaRotation;
            AlphaScale = s.alphaScale;
            InvertAlpha = s.invertAlpha;

            CreasePinch = s.creasePinch;
            CreaseDepthFactor = s.creaseDepthFactor;
            MaskHardness = s.maskHardness;

            PressureFloor = s.pressureFloor;
            PressureCurve = s.pressureCurve;

            RemeshResolution = s.remeshResolution;
            UseBurstJobs = s.useBurstJobs;
            ShowWireframeGizmo = s.showWireframeGizmo;

            // CurrentBrush's setter swaps in that brush's remembered strength/polarity from
            // the arrays just restored above, so it has to come AFTER them - and the live values
            // are assigned after IT, since the swap would otherwise overwrite them. BrushRadius
            // is unaffected by the swap either way, being shared across brushes.
            CurrentBrush = (BrushType)Mathf.Clamp(s.currentBrush, 0, System.Enum.GetValues(typeof(BrushType)).Length - 1);
            BrushStrength = s.brushStrength;
            BrushRadius = s.brushRadius;
            ScreenSpaceBrushSize = !s.worldSpaceBrushSize;
            // 0 is a file from before screen-space sizing - keep the default rather than clamping
            // it to the minimum.
            if (s.brushScreenRadius > 0f) BrushScreenRadius = s.brushScreenRadius;
            IsPositive = s.isPositive;
            Accumulate = s.accumulate;
            AccumulateStrength = s.accumulateStrength;
            FrontFacingOnly = s.frontFacingOnly;
            BuildUpOnHold = s.buildUpOnHold;
            SurfaceRelax = s.surfaceRelax;
            PoseRigidity = s.poseRigidity;
            // A file from before this setting existed has poseSegments == 0 (JsonUtility's
            // default for an unseen int field) - Clamp below floors that to 1, which reproduces
            // the pre-segments behavior (root always the true anchor) rather than silently
            // producing an invalid 0-segment chain.
            PoseSegments = s.poseSegments > 0 ? s.poseSegments : 4;

            IsMaskPaintMode = s.maskPaintMode;

            // A load can land mid-stroke/mid-hover, and it replaces every object in the scene.
            // Clearing the sync sentinel forces SyncSelectionTarget to re-run on the next
            // Update, which already drops every per-stroke continuity cache (hover point, clay
            // stroke memory, move-drag, stroke speed) - reused rather than duplicated here so
            // the two can't drift apart.
            _lastSyncedTarget = null;
        }

        // Tolerates a saved array that is shorter (older build with fewer brushes) or longer
        // (file from a newer build) than this build's BrushType - copies the overlap and leaves
        // the rest at its compiled-in default rather than throwing or truncating the live array.
        private static void CopyPerBrush<T>(T[] src, T[] dst)
        {
            if (src == null || dst == null) return;
            System.Array.Copy(src, dst, Mathf.Min(src.Length, dst.Length));
        }
    }
}
