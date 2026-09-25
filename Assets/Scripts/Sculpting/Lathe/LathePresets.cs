using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Starting shapes for the lathe. Each is a unit-height profile (radii as fractions of the
    /// height), scaled to the scene when applied - see LatheController.ApplyPreset.
    ///
    /// Every closed preset traces the whole cross-section from axis to axis, the way a turner
    /// would draw it, so the result is a solid with real wall thickness rather than a surface:
    /// the bowl and cup are hollow because their profile goes up the outside, over the rim and
    /// back down the inside, not because the surface is open.
    public enum LathePreset
    {
        Vase,
        Bowl,
        Cup,
        Bottle,
        Egg,
        Ring
    }

    public static class LathePresets
    {
        public static readonly LathePreset[] All =
        {
            LathePreset.Vase, LathePreset.Bowl, LathePreset.Cup,
            LathePreset.Bottle, LathePreset.Egg, LathePreset.Ring
        };

        /// Profile points for a preset at `height`, plus whether it is a closed loop.
        public static List<LatheProfile.Point> Build(LathePreset preset, float height, out bool closedLoop)
        {
            closedLoop = false;
            var pts = new List<LatheProfile.Point>();

            void P(float r, float h, bool sharp = false) =>
                pts.Add(new LatheProfile.Point(new Vector2(r * height, h * height), sharp));

            switch (preset)
            {
                case LathePreset.Vase:
                    // Flat foot, full belly, narrow neck, flared lip - the open top is capped
                    // unless Cap Open Ends is turned off.
                    P(0f, 0f, true);
                    P(0.20f, 0f, true);
                    P(0.30f, 0.18f);
                    P(0.32f, 0.38f);
                    P(0.20f, 0.66f);
                    P(0.13f, 0.84f);
                    P(0.19f, 1f);
                    break;

                case LathePreset.Bowl:
                    // Foot ring, outer wall, rounded rim, inner wall back down to the axis.
                    P(0f, 0f, true);
                    P(0.22f, 0f, true);
                    P(0.24f, 0.08f);
                    P(0.48f, 0.34f);
                    P(0.66f, 0.64f);
                    P(0.70f, 0.72f);
                    P(0.64f, 0.70f);
                    P(0.42f, 0.36f);
                    P(0f, 0.20f);
                    break;

                case LathePreset.Cup:
                    P(0f, 0f, true);
                    P(0.30f, 0f, true);
                    P(0.34f, 0.50f);
                    P(0.38f, 1f);
                    P(0.33f, 1f);
                    P(0.29f, 0.50f);
                    P(0.25f, 0.10f, true);
                    P(0f, 0.10f);
                    break;

                case LathePreset.Bottle:
                    P(0f, 0f, true);
                    P(0.24f, 0f, true);
                    P(0.26f, 0.08f);
                    P(0.26f, 0.52f);
                    P(0.20f, 0.66f);
                    P(0.09f, 0.78f);
                    P(0.08f, 0.94f);
                    P(0.10f, 1f, true);
                    P(0f, 1f);
                    break;

                case LathePreset.Egg:
                    P(0f, 0f);
                    P(0.30f, 0.14f);
                    P(0.36f, 0.42f);
                    P(0.26f, 0.80f);
                    P(0f, 1f);
                    break;

                case LathePreset.Ring:
                    // A rounded, slightly squared section well off the axis - a torus.
                    closedLoop = true;
                    P(0.34f, 0.40f);
                    P(0.44f, 0.36f);
                    P(0.54f, 0.40f);
                    P(0.58f, 0.50f);
                    P(0.54f, 0.60f);
                    P(0.44f, 0.64f);
                    P(0.34f, 0.60f);
                    P(0.30f, 0.50f);
                    break;
            }

            return pts;
        }
    }
}
