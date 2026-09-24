using System;
using UnityEngine;

namespace Sculpting
{
    /// Distance-spaced dab placement for the brushes that used to deposit once per rendered frame:
    /// Inflate, Flatten, Smooth and mask painting. Clay and Crease have their own steppers
    /// (ApplyClayStroke, ApplyCarveStroke) because they carry extra per-stroke state - Clay's frozen
    /// tip frame, Crease's travel direction - but the pacing is the same idea.
    ///
    /// A frame-paced brush does more wherever the cursor spends more FRAMES: a slow stroke gets
    /// more than a fast one over the same ground, a stroke on a struggling frame rate gets less
    /// than the same stroke at 144Hz, and a fast flick barely touches the surface. ZBrush and
    /// every other sculpting app place a dab every fixed fraction of the brush size along the
    /// path instead, so what a stroke does is a function of the path drawn. That is what this is.
    public partial class SculptController
    {
        /// What a stationary cursor does.
        private enum DabHoldMode
        {
            /// Nothing, unless Build Up on Hold is on - the building brushes (Inflate), for the same
            /// reason Clay and Crease stop: a pause must not blow a bubble into the surface.
            BuildUpOnHold,
            /// Keeps working at the rate a stroke moving at StrokePacingReference would, so holding
            /// the brush over an area still smooths, flattens or masks it. For these brushes that
            /// has always been the point of holding them, and they converge rather than pile up.
            /// Moving faster than that reference places dabs by distance instead - which is the
            /// fix: a fast Smooth stroke used to skim across the surface doing almost nothing.
            AlwaysWorks,
        }

        // A dab every fifth of the radius - Clay's spacing. These brushes all have smooth falloffs
        // that overlap into one continuous stroke at that density; Crease's narrow groove is the
        // one that needed half of it.
        private const float DabSpacingFraction = 0.2f;
        // Cost ceiling for a frame in which the cursor jumped a long way - see ClayMaxDabsPerFrame.
        private const int MaxDabsPerFrame = 24;

        /// The time one dab stands in for, for brushes whose rates are per second (Flatten, Smooth,
        /// mask). A dab is placed every DabSpacingFraction of the radius, and a stroke moving at
        /// StrokePacingReference (one brush diameter per second) covers that in exactly this long,
        /// so at that speed - and while holding still, see DabHoldMode.AlwaysWorks - these brushes
        /// do precisely what they did when they ran once per frame.
        private const float DabTimeQuantum = DabSpacingFraction * 0.5f;

        private struct DabStroke
        {
            public bool Active;
            public Vector3 Point, Normal; // mesh-local, where the previous frame's segment ended
            public float Carry;           // travel since the last dab, carried across frames
        }

        private DabStroke _dabStroke;

        /// Forgets the stroke in progress, so the next frame starts a new one (with a dab of its
        /// own) instead of joining a segment from wherever the last stroke ended.
        private void ResetDabStroke() => _dabStroke.Active = false;

        /// Places this frame's dabs along the segment from the previous frame's point to this one.
        /// A fresh stroke places one dab immediately so that a click without a drag still marks
        /// the surface. `placeDab` gets mesh-local points and normals; `dt` is this frame's
        /// elapsed time, which only the hold behaviour reads.
        private int StepDabStroke(Vector3 localPoint, Vector3 localNormal, float dt, DabHoldMode hold,
            Action<Vector3, Vector3> placeDab)
        {
            if (!_dabStroke.Active)
            {
                _dabStroke = new DabStroke { Active = true, Point = localPoint, Normal = localNormal };
                placeDab(localPoint, localNormal);
                return 1;
            }

            Vector3 from = _dabStroke.Point;
            Vector3 fromNormal = _dabStroke.Normal;
            float spacing = Mathf.Max(brushRadius * DabSpacingFraction, 0.0005f);
            float dist = Vector3.Distance(from, localPoint);

            // Virtual travel while held (see DabHoldMode). In AlwaysWorks it is a floor, not an
            // addition: moving slower than the reference gives exactly the old time-paced rate, so
            // a slow careful Smooth pass is no weaker than it was.
            float heldTravel = StrokePacingReference * Mathf.Max(dt, 0f);
            float advance = hold == DabHoldMode.AlwaysWorks
                ? Mathf.Max(dist, heldTravel)
                : dist + (buildUpOnHold ? heldTravel * AccumulateSpeedFloor : 0f);
            _dabStroke.Carry += advance;

            int placed = 0;
            while (_dabStroke.Carry >= spacing && placed < MaxDabsPerFrame)
            {
                _dabStroke.Carry -= spacing;
                // Where along this frame's segment the dab falls - see ApplyClayStroke. A held dab
                // (dist ~0) belongs at the current point.
                float u = dist > 1e-9f ? Mathf.Clamp01((dist - _dabStroke.Carry) / dist) : 1f;
                Vector3 point = Vector3.Lerp(from, localPoint, u);
                Vector3 normal = Vector3.Slerp(fromNormal, localNormal, u).normalized;
                placeDab(point, normal);
                placed++;
            }
            // Hit the ceiling: drop the unspent travel rather than bank a burst for next frame.
            if (placed >= MaxDabsPerFrame) _dabStroke.Carry = 0f;

            _dabStroke.Point = localPoint;
            _dabStroke.Normal = localNormal;
            return placed;
        }
    }
}
