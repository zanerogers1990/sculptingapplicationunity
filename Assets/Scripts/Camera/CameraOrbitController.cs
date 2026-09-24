using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Alt+left-drag orbits the camera around a pivot (mirrors the navigation scheme of most
    /// sculpting apps); middle-drag pans; scroll zooms, unless the cursor is over the
    /// sculptable surface (SculptController takes the wheel there to resize the brush instead)
    /// or over a UI panel (scrolling a panel's own scrollbar shouldn't also zoom the view
    /// underneath it). Ctrl+Alt+left-drag zooms too (drag-based alternative for stylus/trackpad
    /// input). Right mouse is not used here - it's reserved by SculptController for inverted
    /// sculpting. SculptController checks the same Alt/Ctrl state so these combos orbit/zoom
    /// instead of sculpting.
    ///
    /// It also owns the camera's PROJECTION, because perspective and orthographic disagree
    /// about what _distance means: in perspective it frames the subject, in orthographic it
    /// does nothing at all (orthographicSize frames it instead). Deriving both from the one
    /// _distance here is what lets the wheel keep zooming in ortho, and lets a projection
    /// switch leave the subject the same size on screen. ViewGizmoUIBuilder drives this and
    /// SnapToView from the corner axis gizmo.
    ///
    /// Close-up work: zooming is measured against the nearest SURFACE in view, not the orbit
    /// pivot. The pivot sits at the model's centre, so zooming toward it used to drive the
    /// camera straight through the near side of the model - with a fixed 0.3 near plane the
    /// surface was sliced open well before that (a hole in the sculpt wherever it came within
    /// 0.3 of the lens). Now each notch covers a percentage of the gap to the surface, so the
    /// camera approaches it asymptotically and never crosses it, and the near plane shrinks with
    /// that gap (see UpdateNearPlane). Pan moves the surface 1:1 with the cursor at any zoom.
    public class CameraOrbitController : MonoBehaviour
    {
        public Transform target;
        [SerializeField] private float orbitSpeed = 0.25f;
        // Fraction of the current distance covered per wheel notch. Using a percentage rather
        // than a fixed step keeps zoom speed consistent near and far, and - crucially - makes it
        // independent of how large a "notch" is reported as (raw wheel delta units vary a lot
        // across platforms/Input System versions), which is what made zoom feel too slow before.
        [SerializeField, Range(0.01f, 0.5f)] private float zoomPercentPerNotch = 0.18f;
        // How much Ctrl+Alt+left-drag zooms per pixel of vertical mouse movement, as a
        // percentage of the current distance (same feel as zoomPercentPerNotch, just driven by
        // drag distance instead of wheel notches) - a drag-based alternative for stylus/trackpad
        // input where a precise scroll wheel isn't available. Dragging up zooms in, matching
        // Maya's Alt+Ctrl dolly gesture.
        [SerializeField, Range(0.0005f, 0.02f)] private float dragZoomSensitivity = 0.004f;
        // Closest the camera may get to the ORBIT PIVOT. Only binds when no surface lies between
        // the camera and the pivot (e.g. zooming in on empty space, or in orthographic, where it
        // is the smallest framing) - otherwise minSurfaceGap does.
        [SerializeField] private float minPivotDistance = 0.05f;
        [SerializeField] private float maxDistance = 20f;
        // Closest a zoom may bring the camera to the surface in view. Zoom steps are a
        // percentage of the gap, so this is a floor on detail work, not a wall you hit.
        [SerializeField] private float minSurfaceGap = 0.001f;

        // Near plane as a fraction of the depth of the nearest surface in view. Small enough
        // that a surface sloping toward the lens across the screen, or one frame of zoom lag
        // (the depth is measured before this frame's move), still stays in front of it.
        private const float NearPlaneFraction = 0.02f;
        // Floor for the adaptive near plane - comfortably inside depth-buffer precision for the
        // 1000-unit far plane.
        private const float MinNearPlane = 0.0001f;
        // Viewport points sampled for the nearest surface: centre plus a ring halfway to the
        // corners, so a bump off to one side of the view counts as well as the one dead ahead.
        private static readonly Vector2[] DepthSamplePoints =
        {
            new Vector2(0.5f, 0.5f),
            new Vector2(0.25f, 0.25f), new Vector2(0.75f, 0.25f),
            new Vector2(0.25f, 0.75f), new Vector2(0.75f, 0.75f),
        };

        // How long an axis snap (see SnapToView) takes. Long enough to read as a rotation
        // rather than a teleport - which is the point of animating it at all, since a hard cut
        // to a new axis leaves you unsure which way the model turned - and short enough that
        // clicking through several views in a row never feels like waiting.
        private const float SnapDuration = 0.3f;
        // Extra distance the camera sits back at while orthographic. Sliding a camera along its
        // own forward axis changes nothing in an ortho projection except what the near plane
        // clips - and at close zooms the near plane WOULD clip, since the rig would otherwise
        // sit only _distance (as little as minPivotDistance) from the pivot with its near plane 0.3
        // in front of that, slicing the front off the model. Parking it a fixed distance beyond
        // maxDistance keeps the whole subject in front of the near plane at every zoom level.
        private const float OrthoPullback = 5f;

        private float _yaw;
        private float _pitch;
        private float _distance;
        private Vector3 _pivot;

        private Camera _cam;
        private bool _orthographic;
        // -1 when no snap is running, otherwise seconds elapsed into the current one.
        private float _snapElapsed = -1f;
        private float _snapFromYaw, _snapFromPitch, _snapToYaw, _snapToPitch;
        // The camera's authored near plane - the ceiling for the adaptive one, and what ortho
        // (which parks the camera OrthoPullback behind everything) keeps using.
        private float _defaultNearPlane = 0.3f;
        private SelectionManager _selection;

        private void Start()
        {
            if (Cam != null) _defaultNearPlane = Cam.nearClipPlane;

            if (target == null)
            {
                var sculptable = FindFirstObjectByType<SculptableMesh>();
                if (sculptable != null) target = sculptable.transform;
            }

            _pivot = target != null ? target.position : Vector3.zero;
            _distance = Vector3.Distance(transform.position, _pivot);

            Vector3 angles = transform.eulerAngles;
            _yaw = angles.y;
            _pitch = angles.x;

            if (Cam != null) Cam.orthographic = _orthographic;
            UpdateTransform();
        }

        /// The orbit rig's whole persistable state (see SceneSerializer). Exposed as one
        /// get/set pair rather than four properties because the four are only ever meaningful
        /// together - and because SetView has to call UpdateTransform to actually move the
        /// camera, which a plain auto-property set would silently skip.
        public void GetView(out float yaw, out float pitch, out float distance, out Vector3 pivot)
        {
            yaw = _yaw; pitch = _pitch; distance = _distance; pivot = _pivot;
        }

        public void SetView(float yaw, float pitch, float distance, Vector3 pivot)
        {
            _yaw = yaw;
            // Same clamps Update() applies to live input, so a hand-edited or corrupt save
            // can't put the rig somewhere the controls could never have reached.
            _pitch = Mathf.Clamp(pitch, -89f, 89f);
            _distance = Mathf.Clamp(distance, minPivotDistance, maxDistance);
            _pivot = pivot;
            UpdateTransform();
        }

        /// The rig's own camera. Resolved lazily rather than in Start, because both the
        /// projection toggle and the view gizmo can reach this component before its Start has
        /// run - script execution order between the camera and the UI builders is unspecified.
        private Camera Cam
        {
            get
            {
                if (_cam != null) return _cam;
                _cam = GetComponent<Camera>();
                if (_cam == null) _cam = Camera.main;
                return _cam;
            }
        }

        public bool Orthographic
        {
            get => _orthographic;
            set
            {
                if (_orthographic == value) return;
                _orthographic = value;
                if (Cam != null) Cam.orthographic = value;
                UpdateTransform();
            }
        }

        /// Where a running snap is HEADED, or the live angles when none is running. The view
        /// gizmo tests against these rather than the live _yaw/_pitch so that clicking the same
        /// axis twice flips to the opposite side (as Unity's gizmo does) even mid-animation,
        /// instead of comparing against a half-finished rotation and re-issuing the same snap.
        public float TargetYaw => _snapElapsed >= 0f ? _snapToYaw : _yaw;
        public float TargetPitch => _snapElapsed >= 0f ? _snapToPitch : _pitch;

        /// True while an axis snap is animating. The turntable holds off while one runs rather
        /// than fighting it over the same yaw.
        public bool IsSnapping => _snapElapsed >= 0f;

        /// True on frames where the user is Alt-dragging an orbit - the turntable yields the yaw
        /// to them for the length of the drag and carries on from wherever they leave it.
        public bool IsUserOrbiting { get; private set; }

        /// Turns the view about the pivot by `degrees` of yaw - the turntable's spin. Yaw is
        /// unbounded here exactly as it is for a live orbit drag.
        public void AddYaw(float degrees)
        {
            _yaw += degrees;
            UpdateTransform();
        }

        /// Animates the rig to an exact yaw/pitch - the axis views the corner gizmo offers.
        /// Pitch is NOT held to the +/-89 that live orbiting is: a top or bottom view is
        /// exactly +/-90, and refusing it would leave the gizmo unable to reach the two views
        /// it most obviously promises. The next orbit drag clamps back to 89 on its own.
        public void SnapToView(float yaw, float pitch)
        {
            _snapFromYaw = _yaw;
            _snapFromPitch = _pitch;
            // Rotate the short way round: yaw is unbounded and keeps accumulating as the user
            // orbits, so lerping from a yaw of 1000 to a raw 180 would spin the view twice.
            _snapToYaw = _yaw + Mathf.DeltaAngle(_yaw, yaw);
            _snapToPitch = Mathf.Clamp(pitch, -90f, 90f);
            _snapElapsed = 0f;
        }

        private void Update()
        {
            var mouse = Mouse.current;
            if (mouse == null) return;

            Vector2 delta = mouse.delta.ReadValue();
            var kb = Keyboard.current;
            bool altHeld = kb != null && kb.leftAltKey.isPressed;
            bool ctrlHeld = kb != null && (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed);

            // Measured once, from where the camera sat at the end of last frame: zoom and pan
            // both scale with it, and the near plane is set from it after this frame's move.
            float viewDepth = ViewDepth();

            IsUserOrbiting = altHeld && !ctrlHeld && mouse.leftButton.isPressed;

            if (altHeld && ctrlHeld && mouse.leftButton.isPressed)
            {
                Zoom(Mathf.Max(0.1f, 1f - delta.y * dragZoomSensitivity), viewDepth);
            }
            else if (altHeld && mouse.leftButton.isPressed)
            {
                // Orbiting is the one input that fights a running snap over the same two
                // values, so it takes them over. Pan and zoom move the pivot and the distance
                // instead, and compose with a snap in flight rather than cancelling it.
                _snapElapsed = -1f;
                _yaw += delta.x * orbitSpeed;
                _pitch -= delta.y * orbitSpeed;
                _pitch = Mathf.Clamp(_pitch, -89f, 89f);
            }

            if (mouse.middleButton.isPressed)
            {
                // Grab-style: the surface at viewDepth tracks the cursor exactly. A fixed world
                // step per pixel (the old 0.01, which matched this at the default ~10-unit
                // framing) flung the view across the model once zoomed in for detail work.
                float perPixel = WorldPerPixel(viewDepth);
                _pivot -= (transform.right * delta.x + transform.up * delta.y) * perPixel;
            }

            // Deferred to SculptController while the cursor is over the sculptable surface -
            // there, the same wheel resizes the active brush instead (see
            // SculptController.HandleBrushSizeScroll/IsHoveringSculptSurface) - and to
            // ZSphereController while the cursor is over a rig sphere, where it resizes that
            // sphere. Also skipped while the cursor is over a UI panel, so scrolling one of the
            // panel's own scrollbars (see UIFactory.CreateScrollingPanelCanvas) doesn't also zoom
            // the 3D view underneath it.
            if (!SculptController.IsHoveringSculptSurface && !ZSphereController.IsHoveringNode && !IsPointerOverUI())
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                    Zoom(1f - Mathf.Sign(scroll) * zoomPercentPerNotch, viewDepth);
            }

            AdvanceSnap();
            UpdateTransform();
            UpdateNearPlane(viewDepth);
        }

        /// Scales the distance to what the camera is looking at by `factor` (<1 zooms in).
        /// "What it's looking at" is the nearest surface in view when that is in front of the
        /// pivot, so the step shrinks as the surface approaches and the camera can never be
        /// zoomed through it; with nothing in between it's the pivot, as before.
        private void Zoom(float factor, float viewDepth)
        {
            if (_orthographic)
            {
                _distance = Mathf.Clamp(_distance * factor, minPivotDistance, maxDistance);
                return;
            }

            float target = viewDepth * factor;
            bool surfaceInFront = viewDepth < _distance;
            // Don't let a zoom-in step carry the camera closer than minSurfaceGap, but never
            // push it back out either if it's already inside that (e.g. after an orbit).
            if (surfaceInFront && factor < 1f) target = Mathf.Max(target, Mathf.Min(viewDepth, minSurfaceGap));
            _distance = Mathf.Clamp(_distance + (target - viewDepth), minPivotDistance, maxDistance);
        }

        /// View-space depth of the nearest visible sculpt surface across a few viewport sample
        /// points, capped at the pivot distance. Perspective only - ortho returns _distance,
        /// since there the camera's actual position (parked behind everything) frames nothing.
        private float ViewDepth()
        {
            Camera cam = Cam;
            if (cam == null || _orthographic) return _distance;

            if (_selection == null) _selection = FindFirstObjectByType<SelectionManager>();
            if (_selection == null) return _distance;

            float nearest = _distance;
            Vector3 origin = transform.position;
            Vector3 forward = transform.forward;
            for (int i = 0; i < DepthSamplePoints.Length; i++)
            {
                Ray ray = cam.ViewportPointToRay(DepthSamplePoints[i]);
                if (_selection.Raycast(ray, out float hitDistance, nearest * 4f) == null) continue;
                float depth = Vector3.Dot(ray.GetPoint(hitDistance) - origin, forward);
                if (depth > 0f && depth < nearest) nearest = depth;
            }
            return nearest;
        }

        /// World units per screen pixel at `viewDepth` - what makes panning grab the surface.
        private float WorldPerPixel(float viewDepth)
        {
            Camera cam = Cam;
            if (cam == null) return 0.01f;
            float halfHeight = _orthographic
                ? cam.orthographicSize
                : viewDepth * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            return 2f * halfHeight / Mathf.Max(1, cam.pixelHeight);
        }

        /// Keeps the near plane a small fraction of the way to the nearest surface, so zooming
        /// in close never slices the model open. The authored plane is the ceiling: far out, the
        /// view is unchanged.
        private void UpdateNearPlane(float viewDepth)
        {
            Camera cam = Cam;
            if (cam == null) return;
            cam.nearClipPlane = _orthographic
                ? _defaultNearPlane
                : Mathf.Clamp(viewDepth * NearPlaneFraction, MinNearPlane, _defaultNearPlane);
        }

        private void AdvanceSnap()
        {
            if (_snapElapsed < 0f) return;

            _snapElapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(_snapElapsed / SnapDuration);
            float eased = t * t * (3f - 2f * t);
            _yaw = Mathf.Lerp(_snapFromYaw, _snapToYaw, eased);
            _pitch = Mathf.Lerp(_snapFromPitch, _snapToPitch, eased);

            if (t < 1f) return;
            // Land exactly, not merely within lerp error: an axis view a hundredth of a degree
            // off is the difference between a clean silhouette and a shimmering one.
            _yaw = _snapToYaw;
            _pitch = _snapToPitch;
            _snapElapsed = -1f;
        }

        private static bool IsPointerOverUI() =>
            EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();

        private void UpdateTransform()
        {
            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            float back = _distance;

            if (_orthographic && Cam != null)
            {
                // Half-height of what a perspective camera at _distance would frame, so that
                // toggling projection holds the subject at the same on-screen size and the
                // wheel - which only moves _distance - still zooms.
                Cam.orthographicSize =
                    Mathf.Max(0.01f, _distance * Mathf.Tan(Cam.fieldOfView * 0.5f * Mathf.Deg2Rad));
                back = maxDistance + OrthoPullback; // see OrthoPullback
            }

            Vector3 pos = _pivot + rot * new Vector3(0f, 0f, -back);
            transform.SetPositionAndRotation(pos, rot);
        }
    }
}
