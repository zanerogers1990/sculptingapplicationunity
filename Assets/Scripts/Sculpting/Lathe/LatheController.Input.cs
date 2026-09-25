using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Sculpting
{
    /// Picking and gestures. The same ground rules as the ZSphere tool, for the same reasons:
    ///   - a drag is computed ABSOLUTELY from the press (the point's start position and where on it
    ///     you grabbed), never by accumulating per-frame deltas, so nothing creeps and Esc can put
    ///     it back exactly;
    ///   - the handle stays under the cursor: the grab offset is kept, so nothing jumps on the
    ///     first frame;
    ///   - grabbing a mirrored handle (the far silhouette) edits the same point through the mirror.
    ///
    /// Gestures, all on the left button unless noted:
    ///   drag a handle               move that point (Shift: only along the dominant direction)
    ///   click the curve             insert a point there, and keep dragging it
    ///   click empty space           extend the profile from its nearer end
    ///   Ctrl-drag                   sketch a whole new profile in one stroke
    ///   double-click a handle       toggle it between smooth and corner
    ///   right-click a handle        delete it (Delete / Backspace deletes the selected one)
    ///   Esc                         cancel the drag in progress
    ///   Enter                       Create the mesh
    /// Alt-drag, the middle button and the wheel stay with the camera throughout.
    public partial class LatheController
    {
        private const float HandlePickPixels = 11f;
        private const float CurvePickPixels = 8f;
        /// An END point dropped this close to the axis, on screen, lands on it and closes that end.
        private const float AxisSnapPixels = 10f;
        private const float DragThresholdPixels = 3f;
        private const float DoubleClickSeconds = 0.3f;
        private const float SketchSpacingPixels = 4f;
        /// A sketch whose ends meet within this many pixels closes into a loop.
        private const float SketchCloseLoopPixels = 18f;
        private const int SketchMaxPoints = 18;

        private enum DragKind { None, Point, Sketch }
        private DragKind _drag = DragKind.None;

        private int _dragIndex = -1;
        private float _dragSide = 1f;
        private Vector2 _dragStartPoint;
        private Vector2 _dragGrabOffset;
        private Vector2 _pressMouse;
        private bool _dragMoved;
        /// A pole that rides at the dragged point's height - see ExtendProfile. -1 for none.
        private int _dragPoleFollower = -1;

        private int _hoverPoint = -1;
        private float _hoverSide = 1f;

        private struct InsertHit
        {
            public bool Valid;
            /// Where the new point goes in the profile's point list.
            public int InsertIndex;
            /// Profile-space position of the new point, on the curve.
            public Vector2 Position;
            public float Side;
            /// The hit was on a cap: the old end point becomes an interior corner.
            public int SharpenIndex;
        }

        private InsertHit _hoverInsert;

        private readonly List<Vector2> _sketch = new List<Vector2>();
        private Vector2 _lastSketchMouse;

        private float _lastClickTime;
        private int _lastClickPoint = -1;

        /// The profile plane's in-plane horizontal direction, world space, perpendicular to the
        /// axis. The profile is drawn at +_u (the canonical side) and mirrored at -_u. Follows the
        /// camera between drags and is frozen during one, so the plane cannot turn under a handle.
        private Vector3 _u = Vector3.right;

        // -------------------------------------------------------------------- geometry

        private void UpdateProfileBasis()
        {
            if (_drag != DragKind.None || _cam == null || _root == null) return;

            Vector3 axis = Vector3.up;
            // For perspective, the plane through the axis facing the CAMERA POSITION (the true
            // silhouette plane of a solid of revolution); for orthographic, the one facing the view
            // direction.
            Vector3 toAxis = _cam.orthographic ? _cam.transform.forward : _root.position - _cam.transform.position;
            toAxis -= axis * Vector3.Dot(toAxis, axis);
            Vector3 u = Vector3.Cross(axis, toAxis);
            if (u.sqrMagnitude < 1e-8f)
            {
                // Looking straight down the axis: every plane through it faces the camera equally,
                // so keep the handles on the screen's right.
                u = _cam.transform.right;
                u -= axis * Vector3.Dot(u, axis);
            }
            if (u.sqrMagnitude < 1e-8f) u = Vector3.right;
            _u = u.normalized;
        }

        private Vector3 ProfileToWorld(Vector2 p, float side) =>
            _root.TransformPoint(_u * (p.x * side) + Vector3.up * p.y);

        private bool TryScreen(Vector2 p, float side, out Vector2 screen)
        {
            Vector3 s = _cam.WorldToScreenPoint(ProfileToWorld(p, side));
            screen = new Vector2(s.x, s.y);
            return s.z > 0f;
        }

        /// The cursor on the profile plane, as a SIGNED profile coordinate: x is negative on the
        /// mirrored side. False when the plane is edge-on to the view ray.
        private bool CursorToProfile(Vector2 mouse, out Vector2 signed)
        {
            signed = default;
            Ray ray = _cam.ScreenPointToRay(mouse);
            Vector3 normal = Vector3.Cross(_u, Vector3.up);
            float denom = Vector3.Dot(ray.direction, normal);
            if (Mathf.Abs(denom) < 0.02f) return false;
            float t = Vector3.Dot(_root.position - ray.origin, normal) / denom;
            if (t < 0f) return false;
            Vector3 local = _root.InverseTransformPoint(ray.origin + ray.direction * t);
            signed = new Vector2(Vector3.Dot(local, _u), local.y);
            return true;
        }

        /// Puts an end point exactly on the axis when it is within AxisSnapPixels of it on screen -
        /// the only reliable way to hit r = 0 by hand, and the thing that closes an end into a pole.
        private Vector2 SnapToAxis(Vector2 p)
        {
            if (p.x <= 0f) return new Vector2(0f, p.y);
            if (TryScreen(p, 1f, out Vector2 s) && TryScreen(new Vector2(0f, p.y), 1f, out Vector2 a) &&
                (s - a).magnitude < AxisSnapPixels)
                p.x = 0f;
            return p;
        }

        // ---------------------------------------------------------------------- picking

        private int PickPoint(Vector2 mouse, out float side)
        {
            side = 1f;
            int best = -1;
            float bestScore = HandlePickPixels;
            for (int i = 0; i < _profile.Count; i++)
            {
                Vector2 p = _profile[i].Position;
                for (int s = 1; s >= -1; s -= 2)
                {
                    if (s < 0 && p.x <= 0f) continue;
                    if (!TryScreen(p, s, out Vector2 screen)) continue;
                    // A hair of preference for the canonical side, where the two overlap near the axis.
                    float score = (screen - mouse).magnitude + (s < 0 ? 0.5f : 0f);
                    if (score >= bestScore) continue;
                    bestScore = score;
                    best = i;
                    side = s;
                }
            }
            return best;
        }

        /// The nearest point on the curve (or on a cap) within `maxPixels` of the cursor, as a place
        /// to insert a new point.
        private InsertHit PickCurve(Vector2 mouse, float maxPixels)
        {
            var hit = new InsertHit { SharpenIndex = -1 };
            int count = _profile.Count;
            if (count < 2) return hit;

            bool loop = _profile.ClosedLoop;
            int segments = LatheCurve.SegmentCount(_profile.Points, loop);
            float best = maxPixels;
            const int steps = 20;

            for (int side = 1; side >= -1; side -= 2)
            {
                for (int seg = 0; seg < segments; seg++)
                {
                    Vector2 prevProfile = _profile[seg].Position;
                    if (!TryScreen(prevProfile, side, out Vector2 prevScreen)) continue;
                    for (int step = 1; step <= steps; step++)
                    {
                        Vector2 curProfile = LatheCurve.Evaluate(_profile.Points, loop, seg, step / (float)steps);
                        if (!TryScreen(curProfile, side, out Vector2 curScreen))
                        {
                            prevProfile = curProfile;
                            prevScreen = curScreen;
                            continue;
                        }
                        float d = DistanceToSegment(mouse, prevScreen, curScreen, out float t);
                        // Not right on top of an existing handle - that press is a handle grab.
                        bool nearEnd = (step == 1 && t < 0.3f) || (step == steps && t > 0.7f);
                        if (d < best && !nearEnd)
                        {
                            best = d;
                            hit.Valid = true;
                            hit.InsertIndex = seg + 1;
                            hit.Position = Vector2.Lerp(prevProfile, curProfile, t);
                            hit.Side = side;
                            hit.SharpenIndex = -1;
                        }
                        prevProfile = curProfile;
                        prevScreen = curScreen;
                    }
                }

                // The caps, which are part of the silhouette too. A point inserted on one becomes
                // the new end, and the old end - where the cap met the wall - stays a corner.
                if (loop || !CapEnds) continue;
                TryCapHit(mouse, 0, 0, side, ref best, ref hit);
                TryCapHit(mouse, count - 1, count, side, ref best, ref hit);
            }
            return hit;
        }

        private void TryCapHit(Vector2 mouse, int endIndex, int insertIndex, float side, ref float best, ref InsertHit hit)
        {
            Vector2 end = _profile[endIndex].Position;
            if (end.x <= 0f) return;
            var axisPoint = new Vector2(0f, end.y);
            if (!TryScreen(end, side, out Vector2 a) || !TryScreen(axisPoint, side, out Vector2 b)) return;
            float d = DistanceToSegment(mouse, a, b, out float t);
            if (d >= best || t < 0.15f || t > 0.95f) return;
            best = d;
            hit.Valid = true;
            hit.InsertIndex = insertIndex;
            hit.Position = Vector2.Lerp(end, axisPoint, t);
            hit.Side = side;
            hit.SharpenIndex = endIndex;
        }

        private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b, out float t)
        {
            Vector2 ab = b - a;
            float len2 = ab.sqrMagnitude;
            t = len2 > 1e-6f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2) : 0f;
            return (a + ab * t - p).magnitude;
        }

        // ------------------------------------------------------------------------ input

        private void HandleInput()
        {
            Mouse mouse = Mouse.current;
            if (mouse == null || _root == null) return;

            // The turntable's clean view is look-only.
            if (TurntableController.CleanViewActive)
            {
                ClearHover();
                return;
            }

            Keyboard kb = Keyboard.current;
            bool shift = kb != null && (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed);
            bool ctrl = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);
            // Alt belongs to the camera: orbiting must never also edit the profile.
            bool alt = kb != null && (kb.leftAltKey.isPressed || kb.rightAltKey.isPressed);
            bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            Vector2 mousePos = mouse.position.ReadValue();

            UpdateProfileBasis();
            if (HandleKeys(kb)) return;

            if (_drag != DragKind.None)
            {
                ClearHover();
                if (mouse.leftButton.isPressed) UpdateDrag(mousePos, shift);
                else
                {
                    if (_drag == DragKind.Sketch) FinishSketch();
                    EndDrag();
                }
                return;
            }

            ClearHover();
            if (overUI || alt) return;

            _hoverPoint = PickPoint(mousePos, out _hoverSide);
            if (_hoverPoint < 0) _hoverInsert = PickCurve(mousePos, CurvePickPixels);

            if (mouse.rightButton.wasPressedThisFrame && _hoverPoint >= 0)
            {
                DeletePoint(_hoverPoint);
                return;
            }

            if (!mouse.leftButton.wasPressedThisFrame) return;

            if (ctrl)
            {
                BeginSketch(mousePos);
                return;
            }

            if (_hoverPoint >= 0)
            {
                bool doubleClick = _hoverPoint == _lastClickPoint &&
                                   Time.unscaledTime - _lastClickTime < DoubleClickSeconds;
                _lastClickTime = Time.unscaledTime;
                _lastClickPoint = _hoverPoint;
                if (doubleClick)
                {
                    _lastClickPoint = -1;
                    SelectedPoint = _hoverPoint;
                    ToggleSharp(_hoverPoint);
                    return;
                }

                SelectedPoint = _hoverPoint;
                BeginPointDrag(_hoverPoint, _hoverSide, mousePos, "Move Point");
                return;
            }
            _lastClickPoint = -1;

            if (_hoverInsert.Valid)
            {
                BeginEdit("Add Point");
                if (_hoverInsert.SharpenIndex >= 0) _profile.SetSharp(_hoverInsert.SharpenIndex, true);
                int inserted = _profile.Insert(_hoverInsert.InsertIndex, _hoverInsert.Position);
                SelectedPoint = inserted;
                BeginPointDrag(inserted, _hoverInsert.Side, mousePos, "Add Point");
                return;
            }

            ExtendProfile(mousePos);
        }

        private void ClearHover()
        {
            _hoverPoint = -1;
            _hoverInsert = default;
        }

        /// A click away from the curve adds a point to the nearer END of an open profile - which,
        /// on an empty profile, is simply drawing it point by point - or, on a loop, into the
        /// nearest segment.
        private void ExtendProfile(Vector2 mousePos)
        {
            if (!CursorToProfile(mousePos, out Vector2 signed)) return;
            float side = signed.x < 0f ? -1f : 1f;
            var position = new Vector2(Mathf.Abs(signed.x), signed.y);

            int index;
            int count = _profile.Count;
            if (count < 2) index = count;
            else if (_profile.ClosedLoop) index = NearestSegmentInsertIndex(position);
            else
            {
                float toFirst = (position - _profile[0].Position).sqrMagnitude;
                float toLast = (position - _profile[count - 1].Position).sqrMagnitude;
                index = toFirst < toLast ? 0 : count;
            }

            // A new end point, so the axis snap applies from the first frame.
            if (!_profile.ClosedLoop) position = SnapToAxis(position);

            // Extending past an end that is CLOSED on the axis means "take the outline up to here,
            // still closed" - not "reopen it". Demoting the old pole to an interior point would
            // leave it pinned a hair off the axis: a pinched neck nobody drew. So the old pole
            // becomes the new point, and a fresh pole closes the end level with it (and follows
            // it for the rest of the drag).
            if (count >= 2 && !_profile.ClosedLoop)
            {
                int end = index == 0 ? 0 : count - 1;
                if (_profile[end].Position.x <= 0f)
                {
                    BeginEdit("Extend");
                    if (position.x <= 0f)
                    {
                        // Dropped on the axis: just move the pole.
                        _profile.Set(end, position);
                        SelectedPoint = end;
                        BeginPointDrag(end, side, mousePos, "Extend");
                        return;
                    }

                    _profile.Insert(index, new Vector2(0f, position.y));
                    int moved = index == 0 ? 1 : count - 1;
                    int pole = index == 0 ? 0 : count;
                    _profile.Set(moved, position);
                    SelectedPoint = moved;
                    BeginPointDrag(moved, side, mousePos, "Extend");
                    _dragPoleFollower = pole;
                    return;
                }
            }

            BeginEdit("Add Point");
            int inserted = _profile.Insert(index, position);
            SelectedPoint = inserted;
            BeginPointDrag(inserted, side, mousePos, "Add Point");

            if (_profile.Count == 1)
                SetStatus("First point placed. Click again for the next - the shape appears at two.");
        }

        private int NearestSegmentInsertIndex(Vector2 position)
        {
            int segments = LatheCurve.SegmentCount(_profile.Points, _profile.ClosedLoop);
            int best = _profile.Count;
            float bestDistance = float.MaxValue;
            for (int seg = 0; seg < segments; seg++)
            {
                Vector2 prev = _profile[seg].Position;
                for (int step = 1; step <= 12; step++)
                {
                    Vector2 cur = LatheCurve.Evaluate(_profile.Points, _profile.ClosedLoop, seg, step / 12f);
                    float d = DistanceToSegment(position, prev, cur, out _);
                    if (d < bestDistance)
                    {
                        bestDistance = d;
                        best = seg + 1;
                    }
                    prev = cur;
                }
            }
            return best;
        }

        // --------------------------------------------------------------- point drags

        private void BeginPointDrag(int index, float side, Vector2 mousePos, string label)
        {
            // Re-entrant with the insert that may have just opened this step: an added point and the
            // drag that places it are ONE undo, and Esc takes both back.
            BeginEdit(label);
            _drag = DragKind.Point;
            _dragIndex = index;
            _dragSide = side;
            _pressMouse = mousePos;
            _dragMoved = false;
            _dragPoleFollower = -1;
            _dragStartPoint = _profile[index].Position;
            _dragGrabOffset = CursorToProfile(mousePos, out Vector2 signed)
                ? _dragStartPoint - new Vector2(signed.x * side, signed.y)
                : Vector2.zero;
        }

        private void UpdateDrag(Vector2 mousePos, bool shift)
        {
            if (_drag == DragKind.Sketch)
            {
                if ((mousePos - _lastSketchMouse).magnitude < SketchSpacingPixels) return;
                if (CursorToProfile(mousePos, out Vector2 s)) _sketch.Add(s);
                _lastSketchMouse = mousePos;
                return;
            }

            if (_dragIndex < 0 || _dragIndex >= _profile.Count) return;
            if (!_dragMoved && (mousePos - _pressMouse).magnitude < DragThresholdPixels) return;
            _dragMoved = true;
            if (!CursorToProfile(mousePos, out Vector2 signed)) return;

            Vector2 target = new Vector2(signed.x * _dragSide, signed.y) + _dragGrabOffset;
            if (shift)
            {
                Vector2 delta = target - _dragStartPoint;
                if (Mathf.Abs(delta.x) > Mathf.Abs(delta.y)) target.y = _dragStartPoint.y;
                else target.x = _dragStartPoint.x;
            }

            // Pulled across the axis, a point stops at it rather than flipping to the other side -
            // the other side is its own mirror image. End points snap onto it, closing that end.
            if (_profile.IsEndpoint(_dragIndex)) target = SnapToAxis(target);
            Vector2 placed = _profile.Set(_dragIndex, target);
            if (_dragPoleFollower >= 0 && _dragPoleFollower < _profile.Count)
                _profile.Set(_dragPoleFollower, new Vector2(0f, placed.y));
        }

        /// Ends any gesture, keeping what it did. Safe to call when nothing is in progress - every
        /// operation calls it first so a panel click mid-drag can never leave a drag hanging.
        private void EndDrag()
        {
            if (_drag == DragKind.None) return;
            _drag = DragKind.None;
            _dragIndex = -1;
            _dragPoleFollower = -1;
            _sketch.Clear();
            CommitEdit();
        }

        /// Esc: puts the profile back exactly as it was at the press - including taking away a point
        /// the press itself added.
        private void CancelDrag()
        {
            if (_drag == DragKind.None) return;
            _drag = DragKind.None;
            _dragIndex = -1;
            _dragPoleFollower = -1;
            _sketch.Clear();
            CancelEdit();
            if (SelectedPoint >= _profile.Count) SelectedPoint = -1;
            SetStatus("Cancelled.");
        }

        // --------------------------------------------------------------------- sketch

        private void BeginSketch(Vector2 mousePos)
        {
            _drag = DragKind.Sketch;
            _sketch.Clear();
            _lastSketchMouse = mousePos;
            _pressMouse = mousePos;
            if (CursorToProfile(mousePos, out Vector2 s)) _sketch.Add(s);
        }

        /// Turns the stroke into a profile: onto one side of the axis, simplified to a handful of
        /// smooth points (the curve through them reproduces the stroke closely, and a handful is what
        /// can be refined by hand afterwards), ends snapped onto the axis if they were drawn to it,
        /// and closed into a loop if the stroke came back to where it started.
        private void FinishSketch()
        {
            if (_sketch.Count < 3)
            {
                SetStatus("Sketch a longer stroke: hold Ctrl and drag out the outline.");
                return;
            }

            float sum = 0f;
            for (int i = 0; i < _sketch.Count; i++) sum += _sketch[i].x;
            float side = sum < 0f ? -1f : 1f;

            var stroke = new List<Vector2>(_sketch.Count);
            for (int i = 0; i < _sketch.Count; i++)
                stroke.Add(new Vector2(Mathf.Max(0f, _sketch[i].x * side), _sketch[i].y));

            bool loop = false;
            if (stroke.Count >= 6 && TryScreen(stroke[0], 1f, out Vector2 a) &&
                TryScreen(stroke[stroke.Count - 1], 1f, out Vector2 b) && (a - b).magnitude < SketchCloseLoopPixels)
            {
                loop = true;
                stroke.RemoveAt(stroke.Count - 1);
            }

            Vector2 min = stroke[0], max = stroke[0];
            for (int i = 1; i < stroke.Count; i++)
            {
                min = Vector2.Min(min, stroke[i]);
                max = Vector2.Max(max, stroke[i]);
            }
            float extent = Mathf.Max(max.x - min.x, max.y - min.y);
            if (extent < 1e-5f) return;

            float tolerance = extent * 0.012f;
            List<Vector2> simplified = Simplify(stroke, tolerance, loop);
            while (simplified.Count > SketchMaxPoints)
            {
                tolerance *= 1.4f;
                simplified = Simplify(stroke, tolerance, loop);
            }

            if (!loop)
            {
                simplified[0] = SnapToAxis(simplified[0]);
                simplified[simplified.Count - 1] = SnapToAxis(simplified[simplified.Count - 1]);
            }

            if (simplified.Count < (loop ? 3 : 2)) return;

            var points = new List<LatheProfile.Point>(simplified.Count);
            for (int i = 0; i < simplified.Count; i++) points.Add(new LatheProfile.Point(simplified[i]));

            BeginEdit("Sketch Profile");
            _profile.SetPoints(points, loop);
            SelectedPoint = -1;
            CommitEdit();
            SetStatus(loop
                ? $"Sketched a closed loop ({points.Count} points) - a ring."
                : $"Sketched {points.Count} points. Drag the handles to refine.");
        }

        /// Ramer-Douglas-Peucker. A loop is split at its farthest pair so both halves have fixed ends.
        private static List<Vector2> Simplify(List<Vector2> points, float tolerance, bool loop)
        {
            var keep = new bool[points.Count];
            if (!loop)
            {
                keep[0] = keep[points.Count - 1] = true;
                SimplifyRange(points, 0, points.Count - 1, tolerance, keep);
            }
            else
            {
                int far = 0;
                float farDistance = -1f;
                for (int i = 1; i < points.Count; i++)
                {
                    float d = (points[i] - points[0]).sqrMagnitude;
                    if (d > farDistance) { farDistance = d; far = i; }
                }
                keep[0] = keep[far] = true;
                SimplifyRange(points, 0, far, tolerance, keep);
                // The closing half, walked as indices far..count-1 then back to 0.
                var tail = new List<Vector2>();
                for (int i = far; i < points.Count; i++) tail.Add(points[i]);
                tail.Add(points[0]);
                var tailKeep = new bool[tail.Count];
                tailKeep[0] = tailKeep[tail.Count - 1] = true;
                SimplifyRange(tail, 0, tail.Count - 1, tolerance, tailKeep);
                for (int i = 1; i < tail.Count - 1; i++) keep[far + i] |= tailKeep[i];
            }

            var result = new List<Vector2>();
            for (int i = 0; i < points.Count; i++)
                if (keep[i]) result.Add(points[i]);
            return result;
        }

        private static void SimplifyRange(List<Vector2> points, int first, int last, float tolerance, bool[] keep)
        {
            if (last <= first + 1) return;
            int index = -1;
            float maxDistance = tolerance;
            for (int i = first + 1; i < last; i++)
            {
                float d = DistanceToSegment(points[i], points[first], points[last], out _);
                if (d > maxDistance)
                {
                    maxDistance = d;
                    index = i;
                }
            }
            if (index < 0) return;
            keep[index] = true;
            SimplifyRange(points, first, index, tolerance, keep);
            SimplifyRange(points, index, last, tolerance, keep);
        }

        // ----------------------------------------------------------------------- keys

        private bool HandleKeys(Keyboard kb)
        {
            if (kb == null || IsTypingInUI()) return false;

            if (kb.escapeKey.wasPressedThisFrame)
            {
                if (_drag != DragKind.None) { CancelDrag(); return true; }
                if (SelectedPoint >= 0) { SelectedPoint = -1; return true; }
                return false;
            }

            if (_drag != DragKind.None) return false;

            if (kb.deleteKey.wasPressedThisFrame || kb.backspaceKey.wasPressedThisFrame)
            {
                if (SelectedPoint >= 0) DeletePoint(SelectedPoint);
                return true;
            }

            if (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame)
            {
                CreateMesh();
                return true;
            }

            return false;
        }

        private static bool IsTypingInUI()
        {
            GameObject focused = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            if (focused == null) return false;
            var field = focused.GetComponent<InputField>();
            return field != null && field.isFocused;
        }
    }
}
