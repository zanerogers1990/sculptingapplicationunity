using UnityEngine;

namespace Sculpting
{
    /// Brush weighting math shared between SculptController's brushes (managed and Burst) and
    /// SculptableMesh's grab selection. Plain static float/Vector3 math so Burst can inline it.
    internal static class BrushMath
    {
        /// How wide the accept/reject transition is, as the cosine of the angle between a vertex's
        /// normal and its own direction to the camera - so ~11.5 degrees either side of edge-on.
        ///
        /// The gate used to be a bare `dot > 0`, which is a 0/1 step, and a step is what made this
        /// option visibly chew up thin, strongly-curved geometry - an ear above all. Adjacent
        /// vertices on a remeshed surface do not share a normal (Surface Nets places one vertex per
        /// cell, so the output is genuinely bumpy at the cell scale - see
        /// SculptableMesh.EncodeCavityAt), so wherever the silhouette runs through a footprint the
        /// step hands neighbouring vertices full strength and none at all. That is a sawtooth
        /// written straight into the surface, and it reads exactly as one ear coming out jagged and
        /// faceted while the other is smooth.
        ///
        /// A ramp costs the option nothing it is actually for. A vertex on the FAR wall of a fin
        /// points away from the camera, lands at cosine <= 0, and is still rejected outright; only
        /// the sliver of surface within a few degrees of edge-on - where "is this the near wall or
        /// the far one" genuinely has no sharp answer - gets a partial weight instead of a coin
        /// flip.
        private const float SilhouetteBand = 0.2f;

        // Shared by every brush's weight computation, multiplied in alongside the mask term
        // right next to it (MaskIn / sculptableMesh.Mask) - see SculptController.frontFacingOnly's
        // remarks for what this is for. A vertex counts as front-facing when its OWN mesh normal
        // points at least partly back toward the camera; compared per-vertex against the camera's
        // actual local-space position rather than one shared view direction, so the test stays
        // correct up close, where a sculpt's own scale can be comparable to the camera's distance
        // from it. Plain float/Vector3 math (like SculptController.ClayFalloff), so Burst inlines
        // it into a job's Execute exactly the same way.
        //
        // cameraLocalPos is the CURRENT DAB's viewpoint, which for a mirrored dab is the reflected
        // camera - see SculptController._dabCameraLocal.
        // SculptableMesh.SelectGrab applies the identical rule (Move picks its vertex set once on
        // mouse-down instead of running a per-frame weight loop), which is why this lives here and
        // not in SculptController: a second copy of a silhouette ramp would drift out of step.
        public static float FrontFacingWeight(bool frontFacingOnly, Vector3 normalLocal, Vector3 posLocal, Vector3 cameraLocalPos)
        {
            if (!frontFacingOnly) return 1f;

            Vector3 toCamera = cameraLocalPos - posLocal;
            float d = Vector3.Dot(normalLocal, toCamera);
            if (d <= 0f) return 0f; // facing away - rejected exactly as before

            // Comparing squared keeps the square root off the overwhelming majority of candidates:
            // anything comfortably front-facing clears this without one, and only the narrow
            // silhouette band pays for the cosine it actually needs. (Squaring is monotonic here
            // because d > 0 at this point.)
            float bandSqr = SilhouetteBand * SilhouetteBand * toCamera.sqrMagnitude;
            if (d * d >= bandSqr) return 1f;

            float cos = d / Mathf.Sqrt(toCamera.sqrMagnitude);
            float t = cos / SilhouetteBand;
            return t * t * (3f - 2f * t); // smoothstep
        }
    }
}
