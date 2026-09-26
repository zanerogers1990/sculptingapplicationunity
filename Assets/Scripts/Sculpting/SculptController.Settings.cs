using System;
using UnityEngine;

namespace Sculpting
{
    /// Brush settings save/load: the Settings DTO and its Capture/Apply.
    public partial class SculptController
    {
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
            public int quadRemeshTarget;

            public bool useBurstJobs;
            public bool showWireframeGizmo;

            public bool maskPaintMode;

            // Per-brush memory, indexed by BrushType. Length is validated on Apply rather than
            // trusted - a file written by an older build (or hand-edited) can legitimately have
            // fewer entries than today's BrushType has members.
            public float[] perBrushStrength;
            // Custom falloff curves, one entry per brush that has one (see BrushFalloff).
            public FalloffCurveEntry[] falloffCurves;
            // Focal Shift per brush, -1..1 (see BrushFalloff.Shift). Missing in older files: 0.
            public float[] perBrushFocalShift;
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
                quadRemeshTarget = quadRemeshTarget,
                useBurstJobs = useBurstJobs,
                showWireframeGizmo = showWireframeGizmo,

                maskPaintMode = IsMaskPaintMode,

                perBrushStrength = (float[])_brushStrengthPerType.Clone(),
                perBrushPolarity = (bool[])_brushPolarity.Clone(),
                perBrushAccumulate = (bool[])_brushAccumulate.Clone(),
                perBrushAccumulateStrength = (float[])_accumulateStrengthPerType.Clone(),
                perBrushFrontFacingOnly = (bool[])_brushFrontFacingOnly.Clone(),
                falloffCurves = CaptureFalloffCurves(),
                perBrushFocalShift = (float[])_focalShiftPerType.Clone(),
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
            System.Array.Clear(_focalShiftPerType, 0, _focalShiftPerType.Length);
            CopyPerBrush(s.perBrushFocalShift, _focalShiftPerType);
            for (int b = 0; b < _focalShiftPerType.Length; b++)
                _focalShiftPerType[b] = Mathf.Clamp(_focalShiftPerType[b], -1f, 1f); // hand-edited files

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
            // A file from before Quad Remesh existed has 0 here (JsonUtility's default for a
            // missing field) - keep the built-in default rather than clamping 0 up to the minimum.
            if (s.quadRemeshTarget > 0) QuadRemeshTarget = s.quadRemeshTarget;
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
