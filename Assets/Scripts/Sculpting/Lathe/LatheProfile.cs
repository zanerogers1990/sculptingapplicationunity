using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// The 2D profile a lathe revolves: an ordered list of control points in (radius, height)
    /// space, where x is the distance from the axis and y the height along it. The curve passes
    /// THROUGH every point (see LatheCurve), so what the artist drags is exactly where the
    /// silhouette goes.
    ///
    /// Radius is never negative - the profile lives on one side of the axis, and the mesh builder
    /// supplies the other side by revolving it. Endpoints may sit exactly on the axis (x = 0), which
    /// closes that end of the solid with a single pole vertex; interior points are kept a hair off
    /// it (MinInteriorRadius), because an interior point on the axis would pinch the surface to a
    /// point and split it into two solids touching at a vertex - non-manifold, and useless for
    /// sculpting or molding.
    public sealed class LatheProfile
    {
        public struct Point
        {
            /// x = radius (>= 0), y = height.
            public Vector2 Position;
            /// A corner: the curve arrives and leaves in straight-ish lines instead of rounding
            /// through. Off by default - smooth is what a thrown pot looks like - but a flat base
            /// meeting a wall needs one.
            public bool Sharp;

            public Point(Vector2 position, bool sharp = false)
            {
                Position = position;
                Sharp = sharp;
            }
        }

        /// Interior points closer to the axis than this fraction of the profile's size are held
        /// off it. Small enough to allow a very narrow neck, large enough that the neck is never a
        /// single vertex.
        public const float MinInteriorRadiusFraction = 0.004f;

        private readonly List<Point> _points = new List<Point>();
        private bool _closedLoop;

        public int Count => _points.Count;
        public Point this[int index] => _points[index];
        public IReadOnlyList<Point> Points => _points;

        /// Bumped on every change, so views and the mesh cache can tell whether they are stale
        /// with one integer compare.
        public int Version { get; private set; }

        /// The curve closes back on itself (last point joins the first) - revolving it makes a
        /// ring or torus rather than a solid of revolution.
        public bool ClosedLoop
        {
            get => _closedLoop;
            set
            {
                if (_closedLoop == value) return;
                _closedLoop = value;
                // A loop has no ends, so a point parked on the axis as a pole would become an
                // interior point on the axis - exactly the pinch the class remarks rule out.
                if (value) ClampAll();
                Version++;
            }
        }

        /// The fewest points that still describe a curve.
        public int MinPoints => _closedLoop ? 3 : 2;

        public bool IsEndpoint(int index) =>
            !_closedLoop && _points.Count > 0 && (index == 0 || index == _points.Count - 1);

        // ---------------------------------------------------------------------------- edits

        /// Moves a point, applying the axis rules. Returns the position actually stored.
        public Vector2 Set(int index, Vector2 position)
        {
            Point p = _points[index];
            p.Position = Clamp(index, position);
            if (p.Position == _points[index].Position) return p.Position;
            _points[index] = p;
            Version++;
            return p.Position;
        }

        public void SetSharp(int index, bool sharp)
        {
            Point p = _points[index];
            if (p.Sharp == sharp) return;
            p.Sharp = sharp;
            _points[index] = p;
            Version++;
        }

        public int Insert(int index, Vector2 position, bool sharp = false)
        {
            index = Mathf.Clamp(index, 0, _points.Count);
            _points.Insert(index, new Point(position, sharp));
            // Re-clamped as a whole: inserting at an end demotes the old endpoint to an interior
            // point, which may no longer sit on the axis.
            ClampAll();
            Version++;
            return index;
        }

        public void RemoveAt(int index)
        {
            _points.RemoveAt(index);
            // And the reverse: removing an end promotes its neighbour, which keeps its position.
            ClampAll();
            Version++;
        }

        public void Clear()
        {
            if (_points.Count == 0 && !_closedLoop) return;
            _points.Clear();
            _closedLoop = false;
            Version++;
        }

        public void SetPoints(IReadOnlyList<Point> points, bool closedLoop)
        {
            _points.Clear();
            if (points != null) _points.AddRange(points);
            _closedLoop = closedLoop;
            ClampAll();
            Version++;
        }

        public Point[] Snapshot() => _points.ToArray();

        // --------------------------------------------------------------------- measurement

        public float MaxRadius
        {
            get
            {
                float max = 0f;
                for (int i = 0; i < _points.Count; i++) max = Mathf.Max(max, _points[i].Position.x);
                return max;
            }
        }

        public float MinHeight
        {
            get
            {
                if (_points.Count == 0) return 0f;
                float min = float.MaxValue;
                for (int i = 0; i < _points.Count; i++) min = Mathf.Min(min, _points[i].Position.y);
                return min;
            }
        }

        public float MaxHeight
        {
            get
            {
                if (_points.Count == 0) return 0f;
                float max = float.MinValue;
                for (int i = 0; i < _points.Count; i++) max = Mathf.Max(max, _points[i].Position.y);
                return max;
            }
        }

        public float Height => MaxHeight - MinHeight;

        /// A length that stands for "how big is this profile", for the tolerances above.
        public float Size => Mathf.Max(Mathf.Max(MaxRadius, Height), 1e-4f);

        public float MinInteriorRadius => Size * MinInteriorRadiusFraction;

        // ------------------------------------------------------------------- whole-profile

        /// Stretches every point away from (or toward) the axis. The Radius slider.
        public void ScaleRadius(float factor)
        {
            if (factor <= 0f || Mathf.Approximately(factor, 1f)) return;
            for (int i = 0; i < _points.Count; i++)
            {
                Point p = _points[i];
                p.Position.x *= factor;
                _points[i] = p;
            }
            ClampAll();
            Version++;
        }

        /// Stretches every point along the axis about the profile's base, so the bottom stays
        /// where it is and the top moves. The Height slider.
        public void ScaleHeight(float factor)
        {
            if (factor <= 0f || Mathf.Approximately(factor, 1f)) return;
            float baseHeight = MinHeight;
            for (int i = 0; i < _points.Count; i++)
            {
                Point p = _points[i];
                p.Position.y = baseHeight + (p.Position.y - baseHeight) * factor;
                _points[i] = p;
            }
            Version++;
        }

        // ------------------------------------------------------------------------ clamping

        private Vector2 Clamp(int index, Vector2 position)
        {
            if (float.IsNaN(position.x) || float.IsNaN(position.y)) return _points[index].Position;
            float minRadius = IsEndpoint(index) ? 0f : MinInteriorRadius;
            position.x = Mathf.Max(position.x, minRadius);
            return position;
        }

        private void ClampAll()
        {
            if (_points.Count == 0) return;
            float minInterior = MinInteriorRadius;
            for (int i = 0; i < _points.Count; i++)
            {
                Point p = _points[i];
                float minRadius = IsEndpoint(i) ? 0f : minInterior;
                if (p.Position.x >= minRadius) continue;
                p.Position.x = minRadius;
                _points[i] = p;
            }
        }
    }
}
