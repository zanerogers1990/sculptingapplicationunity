using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Mask painting (M): input handling and the distance-paced mask dab.
    public partial class SculptController
    {
        // Mask paint/erase rate range - reuses brushStrength/brushRadius rather than adding a
        // separate intensity slider, matching the "just a basic one" scope of the original
        // masking feature. maskHardness (see its own field) interpolates between these two:
        // at hardness 0 the rate matches the old constant (4) - a deliberately slow accumulation
        // so a soft brush stays a gentle, dwell-to-build-up wash, matching what "soft" means in
        // most sculpting apps. At hardness 1 the rate is high enough that a single ordinary
        // click-drag reaches full mask in a fraction of a second even at default brushStrength,
        // matching "hard is immediately dark" - hardness alone reshaping the falloff (see
        // SculptableMesh.PaintMask) wasn't enough on its own, since the per-frame accumulation
        // amount was the same tiny value at the brush center regardless of hardness.
        private const float MaskPaintSpeedSoft = 4f;

        private const float MaskPaintSpeedHard = 40f;

        private void HandleMaskPaintInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) { ResetDabStroke(); return; }

            // Mask painting queries the vertex spatial index (SculptableMesh.PaintMask ->
            // QueryNear) just like the sculpting brushes do, so it needs the same start-of-
            // stroke rebuild they get in HandleSculptInput - which it never reached, since
            // mask mode returns before that block. Left over from whatever the last sculpt
            // stroke built, the index was sized for THAT stroke's brush radius and bucketed
            // against pre-stroke positions, so vertices the stroke had since moved dropped out
            // of the footprint entirely and painted a mask full of holes - invisible until
            // Invert Mask turned those holes into islands of protected surface, which is the
            // "weird effects after inverting" this fixes. (ApplyVerticesLocal now also keeps
            // the index current as geometry moves - see VertexSpatialGrid.UpdateVertices - so
            // this rebuild is really about matching cell size to the mask brush's own radius.)
            if (!altHeld && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
            {
                sculptableMesh.PrepareSpatialIndex(Mathf.Max(brushRadius * 0.5f, 0.01f));
                // Opens the mask stroke's undo accumulator. Mask mode returns from
                // HandleSculptInput before its BeginStrokeUndo block, so this is the only place
                // that can do it; the matching commit needs no new call site, since
                // HandleStrokeEndCommit already fires EndStrokeUndo on every mouse release
                // regardless of which mode is active.
                sculptableMesh.BeginMaskStroke();
                ResetDabStroke();
            }

            Ray ray = cam.ScreenPointToRay(GetStrokeScreenPosition(mouse));
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) { ResetDabStroke(); return; }

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            // Ctrl inverts while held, exactly as it does for every sculpting brush (see
            // CtrlHeld) - mask mode used to ignore it, so the Blender/ZBrush reflex of
            // Ctrl-dragging to erase mask silently painted MORE mask instead.
            bool rightHeld = mouse.rightButton.isPressed;
            bool erasing = rightHeld || CtrlHeld;
            _previewPositive = !erasing; // green while painting, red while erasing

            if (altHeld) { ResetDabStroke(); return; }
            if (mouse.leftButton.isPressed) ApplyMaskPaint(hitPoint, hitNormal, !erasing, Time.deltaTime);
            else if (rightHeld) ApplyMaskPaint(hitPoint, hitNormal, false, Time.deltaTime);
            else ResetDabStroke();
        }

        // Mask paints in distance-spaced dabs like every brush (see StepDabStroke), holding still
        // included: a soft mask brush is meant to be a dwell-to-build-up wash.
        private void ApplyMaskPaint(Vector3 worldPoint, Vector3 worldNormal, bool applying, float dt)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            float speed = Mathf.Lerp(MaskPaintSpeedSoft, MaskPaintSpeedHard, maskHardness);
            _maskDabAmount = (applying ? 1f : -1f) * EffectiveBrushStrength * speed * DabTimeQuantum;
            StepDabStroke(localPoint, sculptableMesh.WorldToLocalNormal(worldNormal), dt, DabHoldMode.AlwaysWorks,
                _placeMaskDab ??= PlaceMaskDab);
        }

        private float _maskDabAmount;

        private Action<Vector3, Vector3> _placeMaskDab;

        private void PlaceMaskDab(Vector3 localPoint, Vector3 localNormal)
        {
            SymmetryGroup symmetry = Symmetry();
            for (int k = 0; k < symmetry.Count; k++)
                sculptableMesh.PaintMask(symmetry[k].Apply(localPoint), brushRadius, _maskDabAmount, maskHardness);
        }
    }
}
