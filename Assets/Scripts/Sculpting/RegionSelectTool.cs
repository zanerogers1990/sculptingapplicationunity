using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Which region gesture is armed, or Off for none. Hide, Mask and Trim are the same gesture
    /// pointed at three different things - per-triangle visibility, per-vertex mask, and the
    /// geometry itself - and Box/Lasso are the same gesture with two different shapes. One enum
    /// rather than a shape flag plus an action flag because they are mutually exclusive by
    /// nature (they all want the same drag), and one enum is what makes that impossible to get
    /// wrong. Mirrors GizmoMode's reasoning.
    public enum RegionSelectMode
    {
        Off,
        BoxHide,
        LassoHide,
        BoxMask,
        LassoMask,
        BoxTrim,
        LassoTrim
    }

    /// Box and lasso region tools for hiding geometry and for masking it - the screen-space
    /// counterpart to the mask BRUSH, and the app's answer to "hide the arms so I can work on
    /// the abdomen". Modelled on ZBrush's marquee and Nomad's hide/mask lasso: the shape starts
    /// wherever the cursor is when the drag begins and is dragged out from there, and everything
    /// it covers - front AND back, straight through the model, not just the surface facing you -
    /// is hidden or masked on release.
    ///
    /// One tool for six jobs rather than two: the gesture, the screen-space test, the symmetry
    /// handling and the on-screen overlay are identical in every case, and only the last step
    /// (flip triangle visibility, write mask values, or cut the geometry away) differs. TRIM is
    /// the destructive member of the family - the same shape that would have hidden geometry
    /// instead deletes it and closes the hole (see MeshTrimmer/TrimTool), which is exactly what
    /// makes "hide it first to check, then trim" a natural way to work.
    ///
    /// Modifiers follow the conventions the brushes already set, so there is nothing new to
    /// learn: RMB-drag or Ctrl inverts the action (show instead of hide, unmask instead of
    /// mask, and for Trim keep the covered part instead of cutting it away), exactly as RMB/Ctrl
    /// inverts every brush. Shift acts on everything OUTSIDE the shape instead of inside it,
    /// which is how "isolate this limb" is expressed - and which for Trim means the same thing
    /// as inverting, since a cut has only two sides. A click without a drag is the reset - Show
    /// All in a hide mode, Clear Mask in a mask mode - matching ZBrush's click-on-empty-canvas-
    /// to-reveal reflex; Trim has no reset, so a click there does nothing. Escape abandons a
    /// drag in progress.
    ///
    /// Hiding is per-POLYGON and honors symmetry: a triangle is hidden when all three of its
    /// vertices fall in the region (see SculptableMesh's _hiddenTriangles), and with a mirror
    /// axis enabled a vertex also counts as covered when its MIRRORED position falls in the
    /// region, so dragging over one arm takes both.
    ///
    /// Self-resolving and self-installing: SculptController adds this component if the scene
    /// doesn't have one, so no Inspector wiring is needed (see that class's RegionSelect
    /// property). This project avoids serialized object references because its scenes are
    /// edited through Unity MCP, which cannot assign them.
    public class RegionSelectTool : MonoBehaviour
    {
        // A drag shorter than this in both axes (or, for a lasso, whose whole path is shorter
        // than twice this) is treated as a click rather than a tiny region - see
        // ApplyClickShortcut. Small enough that a deliberate little box still works, large
        // enough to absorb the hand tremor in a click.
        private const float ClickSlopPx = 6f;
        // Minimum spacing between recorded lasso points. Recording every frame's position would
        // put hundreds of near-identical points on a slow drag, all of which the polygon
        // rasterizer then walks per scanline for no added accuracy.
        private const float LassoPointSpacingPx = 4f;

        [SerializeField] private RegionSelectMode mode = RegionSelectMode.Off;

        // A script recompile while already in Play mode preserves the running scene - including
        // this component's serialized `mode`, if a gesture was armed the moment the recompile
        // hit - instead of re-running the field initializer above. Forcing Off here as well
        // means a fresh reload always comes up with no gesture armed, matching what "self-
        // installing, no Inspector wiring" is supposed to guarantee (see the class remarks).
        private void Awake() => mode = RegionSelectMode.Off;

        private SculptController _controller;
        private SculptController Controller =>
            _controller != null ? _controller : (_controller = FindFirstObjectByType<SculptController>());

        private SelectionManager _selection;
        private SelectionManager Selection =>
            _selection != null ? _selection : (_selection = FindFirstObjectByType<SelectionManager>());

        private TransformGizmo _gizmo;
        private TransformGizmo Gizmo =>
            _gizmo != null ? _gizmo : (_gizmo = FindFirstObjectByType<TransformGizmo>());

        private SculptableMesh Target => Selection != null ? Selection.PrimarySelection : null;

        private Camera ActiveCamera
        {
            get
            {
                Camera fromController = Controller != null ? Controller.ActiveCamera : null;
                return fromController != null ? fromController : Camera.main;
            }
        }

        private bool _dragging;
        // True when the drag was started with the RIGHT button, which inverts the action for
        // the whole drag regardless of what Ctrl does later - captured at press time so the
        // overlay can tint itself correctly from the first frame.
        private bool _dragStartedInverted;
        private Vector2 _dragStart, _dragCurrent;
        private readonly List<Vector2> _lassoPoints = new List<Vector2>();

        // Per-vertex "this vertex is in the region" flags, reused across gestures rather than
        // reallocated - at multi-million-vertex resolutions this is the largest single
        // allocation the tool makes.
        private bool[] _insideScratch = System.Array.Empty<bool>();
        // The triangle (hide) or vertex (mask) indices one gesture actually changes.
        private readonly List<int> _indexScratch = new List<int>();

        private string _status = "";

        /// One-line result of the last gesture, for the panel's status label.
        public string Status => _status;

        public RegionSelectMode Mode
        {
            get => mode;
            set
            {
                if (mode == value) return;
                mode = value;
                CancelDrag();
                _status = "";
                // Mask PAINTING and region selection both want the same click, so arming one
                // disarms the other. Done in both setters (see SculptController.IsMaskPaintMode)
                // rather than polled in an Update somewhere, so the exclusion is decided at the
                // moment of the change instead of a frame later - and it can't recurse, since
                // each setter only ever turns the OTHER one off.
                if (mode != RegionSelectMode.Off && Controller != null)
                    Controller.IsMaskPaintMode = false;
            }
        }

        /// True while a region gesture is armed - what SculptController checks to keep the
        /// brushes (and the brush ring cursor) out of the way.
        public bool IsActive => mode != RegionSelectMode.Off;

        public bool IsHideMode => mode == RegionSelectMode.BoxHide || mode == RegionSelectMode.LassoHide;
        public bool IsTrimMode => mode == RegionSelectMode.BoxTrim || mode == RegionSelectMode.LassoTrim;
        public bool IsLassoMode => mode == RegionSelectMode.LassoHide || mode == RegionSelectMode.LassoMask ||
                                   mode == RegionSelectMode.LassoTrim;

        // ------------------------------------------------------------------ overlay readouts

        /// True while the user is actively dragging a region out - SculptUIBuilder polls this
        /// (and the three below) to draw the marquee, the same way it polls the brush cursor
        /// rather than drawing anything from in here.
        public bool IsDragging => _dragging;

        /// The dragged box in screen pixels, valid while IsDragging in a box mode.
        public Rect DragRect => Rect.MinMaxRect(
            Mathf.Min(_dragStart.x, _dragCurrent.x), Mathf.Min(_dragStart.y, _dragCurrent.y),
            Mathf.Max(_dragStart.x, _dragCurrent.x), Mathf.Max(_dragStart.y, _dragCurrent.y));

        /// The lasso path in screen pixels, valid while IsDragging in a lasso mode.
        public IReadOnlyList<Vector2> LassoPoints => _lassoPoints;

        /// True when the in-progress drag will REMOVE (show/unmask) rather than add - i.e. it
        /// was started with the right button or Ctrl is down. Drives the overlay's tint, so the
        /// gesture reads as add-or-remove before it is committed.
        public bool DragRemoves => _dragStartedInverted || CtrlHeld;

        /// True when the in-progress drag will act on everything OUTSIDE the shape (Shift).
        public bool DragActsOnOutside => ShiftHeld;

        private static bool CtrlHeld => Keyboard.current != null &&
                                        (Keyboard.current.leftCtrlKey.isPressed || Keyboard.current.rightCtrlKey.isPressed);

        private static bool ShiftHeld => Keyboard.current != null &&
                                         (Keyboard.current.leftShiftKey.isPressed || Keyboard.current.rightShiftKey.isPressed);

        // ------------------------------------------------------------------------ input loop

        private void Update()
        {
            HandleModeKeys();
            if (mode == RegionSelectMode.Off) return;

            // A non-Sculpt gizmo tool (Transpose/Scale/ZSphere) owns the mouse - stand down
            // entirely rather than fighting it for the same click, the same carve-out
            // SculptController's own brushes make.
            if (Gizmo != null && Gizmo.Mode != GizmoMode.Sculpt)
            {
                Mode = RegionSelectMode.Off;
                return;
            }

            HandleDrag();
        }

        /// H cycles the hide gestures (Box, Lasso, off again), N does the same for masking - N
        /// because it sits next to M, which already toggles mask PAINTING - and T for trimming.
        /// Any brush hotkey leaves region mode, so 1-7 always gets you straight back to
        /// sculpting without having to remember which region tool is armed.
        private void HandleModeKeys()
        {
            Keyboard kb = Keyboard.current;
            if (kb == null) return;

            if (kb.hKey.wasPressedThisFrame)
                Mode = mode == RegionSelectMode.BoxHide ? RegionSelectMode.LassoHide
                     : mode == RegionSelectMode.LassoHide ? RegionSelectMode.Off
                     : RegionSelectMode.BoxHide;
            else if (kb.nKey.wasPressedThisFrame)
                Mode = mode == RegionSelectMode.BoxMask ? RegionSelectMode.LassoMask
                     : mode == RegionSelectMode.LassoMask ? RegionSelectMode.Off
                     : RegionSelectMode.BoxMask;
            else if (kb.tKey.wasPressedThisFrame)
                Mode = mode == RegionSelectMode.BoxTrim ? RegionSelectMode.LassoTrim
                     : mode == RegionSelectMode.LassoTrim ? RegionSelectMode.Off
                     : RegionSelectMode.BoxTrim;
            else if (mode != RegionSelectMode.Off && AnyBrushKeyPressed(kb))
                Mode = RegionSelectMode.Off;
        }

        private static bool AnyBrushKeyPressed(Keyboard kb) =>
            kb.digit1Key.wasPressedThisFrame || kb.digit2Key.wasPressedThisFrame ||
            kb.digit3Key.wasPressedThisFrame || kb.digit4Key.wasPressedThisFrame ||
            kb.digit5Key.wasPressedThisFrame || kb.digit6Key.wasPressedThisFrame ||
            kb.digit7Key.wasPressedThisFrame;

        private void HandleDrag()
        {
            Mouse mouse = Mouse.current;
            Camera cam = ActiveCamera;
            if (mouse == null || cam == null) return;

            Keyboard kb = Keyboard.current;
            if (_dragging && kb != null && kb.escapeKey.wasPressedThisFrame)
            {
                CancelDrag();
                _status = "Cancelled.";
                return;
            }

            // Alt+drag is the camera orbit - never a region gesture. Checked on the press frame
            // only: releasing Alt mid-drag shouldn't abandon a region the user is halfway
            // through dragging out.
            bool altHeld = kb != null && kb.leftAltKey.isPressed;
            Vector2 pos = ClampToViewport(mouse.position.ReadValue(), cam);

            if (!_dragging)
            {
                if (altHeld) return;
                if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject()) return;
                if (!mouse.leftButton.wasPressedThisFrame && !mouse.rightButton.wasPressedThisFrame) return;

                _dragging = true;
                _dragStartedInverted = mouse.rightButton.wasPressedThisFrame;
                _dragStart = _dragCurrent = pos;
                _lassoPoints.Clear();
                _lassoPoints.Add(pos);
                return;
            }

            _dragCurrent = pos;
            if (IsLassoMode && Vector2.Distance(_lassoPoints[_lassoPoints.Count - 1], pos) >= LassoPointSpacingPx)
                _lassoPoints.Add(pos);

            bool released = _dragStartedInverted
                ? mouse.rightButton.wasReleasedThisFrame
                : mouse.leftButton.wasReleasedThisFrame;
            if (!released) return;

            bool inverse = _dragStartedInverted || CtrlHeld;
            bool actOnOutside = ShiftHeld;
            bool wasClick = IsClickRatherThanDrag();
            _dragging = false;

            if (wasClick) ApplyClickShortcut();
            else ApplyRegion(inverse, actOnOutside);
        }

        // Mouse positions can run outside the window while a button is held; a lasso point out
        // there would stretch the rasterized polygon's bounding box (and its allocation) far
        // past the screen for no benefit, since nothing is drawn or picked out there anyway.
        private static Vector2 ClampToViewport(Vector2 p, Camera cam)
        {
            Rect r = cam.pixelRect;
            return new Vector2(Mathf.Clamp(p.x, r.xMin, r.xMax), Mathf.Clamp(p.y, r.yMin, r.yMax));
        }

        private bool IsClickRatherThanDrag()
        {
            if (!IsLassoMode)
            {
                Vector2 span = _dragCurrent - _dragStart;
                return Mathf.Abs(span.x) < ClickSlopPx && Mathf.Abs(span.y) < ClickSlopPx;
            }

            float length = 0f;
            for (int i = 1; i < _lassoPoints.Count; i++)
                length += Vector2.Distance(_lassoPoints[i - 1], _lassoPoints[i]);
            return length < ClickSlopPx * 2f;
        }

        private void CancelDrag()
        {
            _dragging = false;
            _lassoPoints.Clear();
        }

        /// A click with no drag resets whatever the armed mode edits: reveal everything in a
        /// hide mode, clear the mask in a mask mode. Both are undoable like any other edit, and
        /// both are also plain buttons in the panel - this is the muscle-memory shortcut, not
        /// the only way to reach them. Trim has no reset (undo is the only way back from a cut),
        /// so a stray click there is deliberately inert rather than being given some invented
        /// meaning.
        private void ApplyClickShortcut()
        {
            SculptableMesh target = Target;
            if (target == null) { _status = "No object selected."; return; }

            if (IsTrimMode)
            {
                _status = "Drag a shape out to trim.";
                return;
            }

            if (IsHideMode)
            {
                if (!target.AnyHidden) { _status = "Nothing is hidden."; return; }
                target.ShowAllGeometry();
                _status = "Showed all geometry.";
            }
            else
            {
                if (!target.HasMask) { _status = "Nothing is masked."; return; }
                target.ClearMask();
                _status = "Cleared the mask.";
            }
        }

        // ------------------------------------------------------------------------- applying

        private void ApplyRegion(bool inverse, bool actOnOutside)
        {
            SculptableMesh target = Target;
            Camera cam = ActiveCamera;
            if (target == null || cam == null) { _status = "No object selected."; return; }

            ScreenRegionMask region = IsLassoMode
                ? ScreenRegionMask.Lasso(_lassoPoints)
                : ScreenRegionMask.Box(_dragStart, _dragCurrent);
            if (region == null) { _status = "Region too small."; return; }

            // Trim never builds the per-vertex coverage array the other two share: it has to
            // find the exact place the boundary crosses each straddling EDGE, not just which
            // side each vertex fell on, so it re-runs the same test itself (see MeshTrimmer).
            // Both modifiers mean the same thing here - a cut has only two sides - so either one
            // keeps the covered part instead of removing it.
            if (IsTrimMode)
            {
                TrimTool.Apply(target, cam, region, removeCovered: !(inverse || actOnOutside), out _status);
                _lassoPoints.Clear();
                return;
            }

            bool[] inside = MarkCoveredVertices(target, cam, region, actOnOutside);
            if (inside == null) return;

            if (IsHideMode) ApplyHide(target, inside, hide: !inverse);
            else ApplyMask(target, inside, maskValue: inverse ? 0f : 1f);

            _lassoPoints.Clear();
        }

        /// Flags every vertex the region covers, in the mesh's own vertex order. `actOnOutside`
        /// flips the whole result at the end rather than at the test, so "outside" consistently
        /// means "not covered by the region under ANY mirror" instead of "outside under each
        /// mirror separately", which would leave nothing selected the moment symmetry was on.
        private bool[] MarkCoveredVertices(SculptableMesh target, Camera cam, ScreenRegionMask region, bool actOnOutside)
        {
            Vector3[] verts = target.Vertices;
            if (verts == null || verts.Length == 0) { _status = "No geometry."; return null; }

            if (_insideScratch.Length != verts.Length) _insideScratch = new bool[verts.Length];

            // One matrix per gesture, then one multiply per vertex - Camera.WorldToScreenPoint
            // would redo the transform chain (and its own viewport lookups) per call, which is
            // the difference between a few milliseconds and a visible stall at multi-million-
            // vertex resolutions.
            Matrix4x4 mvp = cam.projectionMatrix * cam.worldToCameraMatrix * target.transform.localToWorldMatrix;
            Rect viewport = cam.pixelRect;

            MirrorController mirror = target.GetComponent<MirrorController>();
            List<Vector3> signs = mirror != null ? mirror.GetMirrorSigns() : null;
            bool mirrored = signs != null && signs.Count > 1;

            for (int i = 0; i < verts.Length; i++)
            {
                bool covered = ProjectsInside(mvp, verts[i], viewport, region);
                if (!covered && mirrored)
                {
                    // signs[0] is always Vector3.one (the un-mirrored position), already tested.
                    for (int s = 1; s < signs.Count && !covered; s++)
                        covered = ProjectsInside(mvp, Vector3.Scale(verts[i], signs[s]), viewport, region);
                }
                _insideScratch[i] = actOnOutside ? !covered : covered;
            }

            return _insideScratch;
        }

        private static bool ProjectsInside(Matrix4x4 mvp, Vector3 localPos, Rect viewport, ScreenRegionMask region) =>
            ScreenRegionMask.ProjectToScreen(mvp, localPos, viewport, out Vector2 screen) &&
            region.Contains(screen);

        /// Hides (or shows) every triangle whose three vertices are all covered. Per-polygon,
        /// not per-vertex: a triangle straddling the edge of the region keeps its vertices
        /// sculptable, so the border of a hidden area behaves like ordinary surface rather than
        /// a frozen wall.
        private void ApplyHide(SculptableMesh target, bool[] inside, bool hide)
        {
            int[] tris = target.Triangles;
            bool[] alreadyHidden = target.HiddenTriangles;
            int triCount = target.TriangleCount;
            if (tris == null || triCount == 0) { _status = "No geometry."; return; }

            _indexScratch.Clear();
            int hiddenAfter = 0;
            for (int t = 0; t < triCount; t++)
            {
                int b = t * 3;
                bool covered = inside[tris[b]] && inside[tris[b + 1]] && inside[tris[b + 2]];
                bool wasHidden = alreadyHidden != null && alreadyHidden[t];
                if (covered && wasHidden != hide) _indexScratch.Add(t);
                if (covered ? hide : wasHidden) hiddenAfter++;
            }

            if (_indexScratch.Count == 0)
            {
                _status = hide ? "Nothing new to hide there." : "Nothing hidden there.";
                return;
            }

            // Refused rather than allowed: an object that has vanished completely gives no clue
            // that it is still there and only hidden, and this is the one hide outcome with
            // nothing left on screen to drag a "show" gesture over. Show All and undo would both
            // still recover it - but a tool that can make the model disappear by overshooting a
            // drag is not one people trust.
            if (hiddenAfter >= triCount)
            {
                _status = "That would hide the whole object - skipped.";
                return;
            }

            target.SetTrianglesHidden(_indexScratch, hide);
            _status = (hide ? "Hid " : "Showed ") + _indexScratch.Count + " polygons.";
        }

        /// Hidden vertices are skipped: hiding is how you get a limb out of the way, and a mask
        /// box dragged over the torso silently masking the arm behind it would defeat that
        /// entirely - the same reasoning that keeps the brushes off hidden geometry (see
        /// SculptableMesh.QueryNear).
        private void ApplyMask(SculptableMesh target, bool[] inside, float maskValue)
        {
            _indexScratch.Clear();
            for (int i = 0; i < inside.Length; i++)
                if (inside[i] && !target.IsVertexHidden(i)) _indexScratch.Add(i);

            if (_indexScratch.Count == 0) { _status = "Region covered no vertices."; return; }

            target.SetMaskOnVertices(_indexScratch, maskValue);
            _status = (maskValue > 0f ? "Masked " : "Unmasked ") + _indexScratch.Count + " vertices.";
        }
    }
}
