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
    public class CameraOrbitController : MonoBehaviour
    {
        public Transform target;
        [SerializeField] private float orbitSpeed = 0.25f;
        [SerializeField] private float panSpeed = 0.01f;
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
        [SerializeField] private float minDistance = 0.5f;
        [SerializeField] private float maxDistance = 20f;

        // How long an axis snap (see SnapToView) takes. Long enough to read as a rotation
        // rather than a teleport - which is the point of animating it at all, since a hard cut
        // to a new axis leaves you unsure which way the model turned - and short enough that
        // clicking through several views in a row never feels like waiting.
        private const float SnapDuration = 0.3f;
        // Extra distance the camera sits back at while orthographic. Sliding a camera along its
        // own forward axis changes nothing in an ortho projection except what the near plane
        // clips - and at close zooms the near plane WOULD clip, since the rig would otherwise
        // sit only _distance (as little as minDistance) from the pivot with its near plane 0.3
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

        private void Start()
        {
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
            _distance = Mathf.Clamp(distance, minDistance, maxDistance);
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

            if (altHeld && ctrlHeld && mouse.leftButton.isPressed)
            {
                float dragFactor = Mathf.Max(0.1f, 1f - delta.y * dragZoomSensitivity);
                _distance = Mathf.Clamp(_distance * dragFactor, minDistance, maxDistance);
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
                _pivot -= transform.right * (delta.x * panSpeed) + transform.up * (delta.y * panSpeed);
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
                {
                    float factor = 1f - Mathf.Sign(scroll) * zoomPercentPerNotch;
                    _distance = Mathf.Clamp(_distance * factor, minDistance, maxDistance);
                }
            }

            AdvanceSnap();
            UpdateTransform();
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
