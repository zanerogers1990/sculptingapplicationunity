using UnityEngine;

namespace Sculpting
{
    /// Vector normalization without the hidden cutoff in Vector3.normalized.
    ///
    /// Vector3.normalized and Vector3.Normalize hand back the ZERO vector whenever the length is
    /// 1e-5 or less. That is an absolute length, blind to the scale of whatever is being
    /// normalized - and a triangle's raw cross product, the usual way to get its normal, has a
    /// length of twice the triangle's area. So every triangle under 5e-6 square units silently had
    /// no normal: none of a coarse unit-sized model's, but over half of a 612k-triangle unit
    /// sphere's, and every one of a centimetre-sized object's at any density. The remesher's vertex
    /// normals and the brush raycast's hit normal both went through it (see RemeshNormalTests).
    ///
    /// SculptableMesh's per-vertex normal passes already normalize by hand, inline, for the same
    /// reason; this is that rule for everywhere that is not one of those hot loops.
    internal static class VectorMath
    {
        /// `v` at unit length, or `fallback` when its squared length is not above
        /// `minSqrMagnitude` (zero by default, so any direction at all is kept) or is not finite.
        public static Vector3 NormalizeOr(Vector3 v, Vector3 fallback, float minSqrMagnitude = 0f)
        {
            float sqr = v.x * v.x + v.y * v.y + v.z * v.z;
            // Negated so a NaN length takes the fallback too; an infinite one would scale to zero.
            if (!(sqr > minSqrMagnitude) || float.IsInfinity(sqr)) return fallback;
            float inv = 1f / Mathf.Sqrt(sqr);
            return new Vector3(v.x * inv, v.y * inv, v.z * inv);
        }

        /// Where `ray` crosses the plane through `planePoint` with normal `planeNormal`, if it does so
        /// in front of the ray's origin. Shared by the Move/Pose/Snake Hook drags and TransformGizmo's
        /// axis-constrained handles, so both drag with the same technique.
        public static bool RayPlaneIntersect(Ray ray, Vector3 planePoint, Vector3 planeNormal, out Vector3 point)
        {
            float denom = Vector3.Dot(ray.direction, planeNormal);
            if (Mathf.Abs(denom) < 1e-6f) { point = default; return false; }

            float dist = Vector3.Dot(planePoint - ray.origin, planeNormal) / denom;
            if (dist < 0f) { point = default; return false; }

            point = ray.origin + ray.direction * dist;
            return true;
        }
    }
}
