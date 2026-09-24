using UnityEngine;

namespace Sculpting.Molding
{
    /// The orthonormal basis a mold is built in: Right and Eye span the parting surface, Up is
    /// the PULL direction - the way the two halves come apart.
    ///
    /// Every piece of the mold pipeline works in these coordinates rather than in world space,
    /// because the whole problem is one-dimensional along Up: "which half does this surface end
    /// up in" is a comparison of frame-Y against a height field, and "can this half release" is
    /// a question about what else lies along frame-Y. Expressing the model in the frame once,
    /// up front, is what turns those into array lookups.
    ///
    /// Deliberately a rotation only, with no translation: the field, the column map and the
    /// block bounds all measure against the model's own extent, so an origin would be one more
    /// thing to keep in step for nothing. That also means the basis has determinant +1 (see
    /// FromRightUp), so triangle winding means the same thing in frame space as in world space
    /// and the slab builder can reason about outward normals without a sign check.
    public readonly struct MoldFrame
    {
        public readonly Vector3 Right;
        /// The pull direction. Half B lifts along +Up, half A drops along -Up.
        public readonly Vector3 Up;
        public readonly Vector3 Eye;

        private MoldFrame(Vector3 right, Vector3 up, Vector3 eye)
        {
            Right = right;
            Up = up;
            Eye = eye;
        }

        public static MoldFrame Identity => new MoldFrame(Vector3.right, Vector3.up, Vector3.forward);

        /// Orthonormalises whatever pair it is given, keeping Right exactly and bending Up to be
        /// perpendicular to it - the same Gram-Schmidt the Blender addon's frame_matrix does.
        /// Vector3.Cross in Unity's left-handed space maps (x, y) to z, so the result is a
        /// right-handed-in-Unity-terms basis with determinant +1.
        public static MoldFrame FromRightUp(Vector3 right, Vector3 up)
        {
            Vector3 r = VectorMath.NormalizeOr(right, Vector3.right);
            Vector3 e = VectorMath.NormalizeOr(Vector3.Cross(r, up), Vector3.forward);
            Vector3 u = VectorMath.NormalizeOr(Vector3.Cross(e, r), Vector3.up);
            return new MoldFrame(r, u, e);
        }

        /// The frame that pulls along world axis `axis`, with the model's LONGER remaining axis
        /// as Right. Picking the long axis deliberately: the parting grid is rectangular, and
        /// putting the model's length along the grid's first index keeps the two resolutions
        /// meaning what their labels say ("across" really is across the long way).
        public static MoldFrame FromWorldAxis(int axis, Vector3 extents)
        {
            int a = axis == 0 ? 1 : 0;
            int b = axis == 2 ? 1 : 2;
            if (a == b) b = 3 - axis - a;
            int r = extents[a] >= extents[b] ? a : b;

            Vector3 rv = Vector3.zero; rv[r] = 1f;
            Vector3 uv = Vector3.zero; uv[axis] = 1f;
            return FromRightUp(rv, uv);
        }

        /// World point (or direction - there is no translation) into frame coordinates:
        /// x = along Right, y = along Up (the split height), z = along Eye.
        public Vector3 ToFrame(Vector3 world) =>
            new Vector3(Vector3.Dot(world, Right), Vector3.Dot(world, Up), Vector3.Dot(world, Eye));

        public Vector3 ToWorld(Vector3 frame) => Right * frame.x + Up * frame.y + Eye * frame.z;

        /// Nearest signed world axis, so a roughly-front camera gives an exactly upright mold
        /// rather than one tilted by however far off-axis the user happened to be orbiting.
        public static Vector3 SnapToAxis(Vector3 v)
        {
            int i = 0;
            float best = Mathf.Abs(v.x);
            if (Mathf.Abs(v.y) > best) { i = 1; best = Mathf.Abs(v.y); }
            if (Mathf.Abs(v.z) > best) i = 2;

            Vector3 outv = Vector3.zero;
            outv[i] = v[i] >= 0f ? 1f : -1f;
            return outv;
        }

        /// "+Z", "-X (tilted)" and so on, for the status line. The tilt note matters: a pull
        /// direction that is not on an axis still works, but the halves will not sit flat on a
        /// print bed without being re-oriented, and that is worth saying out loud.
        public static string AxisLabel(Vector3 v)
        {
            int i = 0;
            float best = Mathf.Abs(v.x);
            if (Mathf.Abs(v.y) > best) { i = 1; best = Mathf.Abs(v.y); }
            if (Mathf.Abs(v.z) > best) { i = 2; best = Mathf.Abs(v.z); }

            string sign = v[i] >= 0f ? "+" : "-";
            return sign + "XYZ"[i] + (best > 0.98f ? "" : " (tilted)");
        }

        /// Frame built from a camera, matching the addon's "From view" pull mode: the screen's
        /// up direction is the pull, the screen's right is Right. Snapping is offered rather
        /// than forced because a deliberately tilted parting line is sometimes exactly right on
        /// an organic shape.
        public static MoldFrame FromCamera(Camera cam, bool snap)
        {
            if (cam == null) return Identity;
            Vector3 r = cam.transform.right;
            Vector3 u = cam.transform.up;
            if (snap)
            {
                Vector3 rs = SnapToAxis(r), us = SnapToAxis(u);
                // Both snapping onto the same axis would collapse the basis - fall back to the
                // unsnapped pair rather than emitting a degenerate frame.
                if (Mathf.Abs(Vector3.Dot(rs, us)) < 0.5f) { r = rs; u = us; }
            }
            return FromRightUp(r, u);
        }
    }
}
