using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Hotkeys and drag gauges, pen pressure, stroke speed, Lazy Mouse, double-click picking, and
    /// the on-screen brush cursor.
    public partial class SculptController
    {
        // Where the rope's near end currently sits, in screen pixels - only meaningful while
        // _lazyMouseActive is true (see GetStrokeScreenPosition).
        private Vector2 _lazyMouseScreenPos;
        private bool _lazyMouseActive;
        // The rope's FAR end - the raw cursor, recorded on the same frame _lazyMouseScreenPos
        // was last advanced, so the two describe one consistent rope rather than two positions
        // sampled at different moments. Read only through LazyMouseTetherFrom, for the tether
        // line SculptUIBuilder draws between them.
        private Vector2 _lazyMouseRawScreenPos;

        // See CurrentPressure/UpdatePenPressure remarks (near the BrushStrength property) for
        // why pressure is smoothed and curved rather than applied raw.
        private const float PressureSmoothingSpeed = 20f;
        private float _smoothedPenPressure = 1f;

        // How many world units BrushRadius changes per pixel of horizontal mouse movement
        // while resizing (holding S). Tuned so a full-width drag across a ~1080p window
        // covers roughly the whole MinBrushRadius-MaxBrushRadius range - scale this together
        // with MaxBrushRadius (same ratio) if that range ever changes again, or a full-width
        // drag stops reaching the new max.
        private const float ResizeSensitivity = 0.01f;
        // Half ResizeSensitivity, matching that BrushStrength's range (0.01-1) is about half
        // the width of BrushRadius's (0.01-2) - same "full-width drag covers roughly the whole
        // range" feel, scaled to the smaller range.
        private const float StrengthAdjustSensitivity = 0.00125f;
        // Same "full-width drag covers roughly the whole range" tuning, for RemeshResolution's
        // 4-1024 span - see HandleRemeshDensityKey. Scaled up with the range so a full-width
        // drag still spans it rather than stopping halfway.
        private const float RemeshDensityDragSensitivity = 0.62f;
        // How long R must stay down before it arms the density gauge instead of firing a plain
        // Remesh() - long enough that the existing tap-to-remesh shortcut still lands cleanly
        // without a drag attached, short enough that reaching for the gauge on purpose doesn't
        // feel laggy.
        private const float RemeshHoldThreshold = 0.35f;

        private static readonly Color PositiveColor = new Color(0.2f, 1f, 0.4f);
        private static readonly Color NegativeColor = new Color(1f, 0.3f, 0.3f);
        // Smooth has no add/subtract polarity (see _previewPositive's "always neutral" comment
        // in each brush handler) - blue instead of green/red reads as its own third state
        // rather than looking like an ordinary positive dab, matching the dashed ring
        // (BrushCursorDashed) SculptUIBuilder swaps in for the same reason.
        private static readonly Color SmoothColor = new Color(0.3f, 0.65f, 1f);

        // 2D screen-space brush cursor (replaces the old world-space BrushPreview sphere) - a
        // ZBrush/Blender-style ring that tracks the mouse directly rather than a 3D object
        // positioned via raycast hit point, so it reads correctly even when hovering empty
        // space beside the model. SculptUIBuilder polls these every frame to position/size/tint
        // its ring Image, same delegation pattern as IsAdjustingStrength below.
        // The OS cursor is hidden directly here (see UpdateBrushCursor) whenever this
        // ring is shown, and restored whenever it isn't - over a UI panel, over no sculptable
        // target, or while another tool (Transpose/Scale/ZSphere) owns the viewport.
        private bool _showBrushCursor;
        private Vector2 _brushCursorScreenPos;
        private float _brushCursorScreenDiameter;
        private Color _brushCursorColor;
        private bool _brushCursorDashed;
        private const float MinCursorScreenDiameterPx = 14f;

        // Brief "stroke committed" pulse: blinks the cursor out and eases it back in over
        // StrokeEndFadeDuration starting the instant a stroke ends (see HandleStrokeEndCommit),
        // so releasing the mouse gives an unmistakable beat of feedback distinct from just
        // continuing to hover in the same spot. Counts DOWN from StrokeEndFadeDuration to 0;
        // BrushCursorFadeAlpha (read by SculptUIBuilder) turns that into a 0->1 ramp.
        private float _strokeEndFadeTimer;
        private const float StrokeEndFadeDuration = 0.1f;

        private bool _isResizingBrush;
        private float _resizeStartRadius;
        private float _resizeStartMouseX;
        // Where the S-drag started - UpdateBrushCursor freezes the ring here instead of
        // following the live mouse position while resizing, since the drag scrubs BrushRadius
        // by horizontal delta alone and the ring flying across the screen with the mouse would
        // otherwise fight the "grow/shrink in place" feedback the gesture is meant to give.
        private Vector2 _resizeAnchorScreenPos;

        // Same S-drag pattern as above (see HandleBrushStrengthKey), but for per-brush
        // BrushStrength instead of the shared BrushRadius.
        private bool _isAdjustingStrength;
        private float _strengthAdjustStartValue;
        private float _strengthAdjustStartMouseX;
        // Same freeze-in-place reasoning as _resizeAnchorScreenPos above, for the F-drag.
        private Vector2 _strengthAdjustAnchorScreenPos;

        // Same S-drag pattern again (see HandleRemeshDensityKey), but gated behind a hold
        // threshold rather than starting the instant R goes down - R already has a meaning on a
        // plain tap (Remesh at whatever resolution is set), so the gauge only arms once the key
        // has been held long enough to tell a drag-in-progress from that tap.
        private bool _isAdjustingRemeshDensity;
        private float _remeshDensityStartValue;
        private float _remeshDensityStartMouseX;
        // Where the density gauge (label + slider) freezes, same idiom as
        // _resizeAnchorScreenPos/_strengthAdjustAnchorScreenPos above.
        private Vector2 _remeshDensityAnchorScreenPos;
        // -1 while R is up; the time it went down otherwise, so the hold threshold and the
        // tap-vs-hold decision on release can both be measured off it.
        private float _rKeyDownTime = -1f;

        private bool _isShiftSmoothActive;
        private BrushType _preShiftBrush;

        // Windows Ink (and any other Input System pen backend) exposes a stylus as
        // Pen.current with a 0-1 pressure axis. CurrentPressure reads _smoothedPenPressure
        // (updated once/frame by UpdatePenPressure - see its remarks) rather than the raw
        // control directly, so this stays safe to read more than once per frame (Mirror can
        // call each brush's apply path once per mirrored plane).
        //
        // Two shaping steps turn the raw 0-1 axis into something that feels like ZBrush/
        // Blender rather than "underwhelming and jittery" (reported after wiring pressure in
        // directly): most tablets rarely report raw pressure anywhere near 1.0 even under a
        // firm press, so brush strengths tuned for a constant mouse click (always 1) read as
        // underpowered; and sensor noise in raw pressure was showing up frame-to-frame as a
        // visibly uneven stroke instead of an evenly built-up ridge. pressureFloor guarantees
        // even the lightest touch still applies a meaningful fraction of full strength, and
        // pressureCurve reshapes how the stylus's travel maps onto that remaining range.
        //
        // The curve used to be a fixed sqrt, which overcorrected the "underpowered" complaint
        // into an oversensitive one: sqrt is steepest at zero (its slope there is unbounded),
        // so the lightest touches - the noisiest part of the sensor, and the part used for
        // delicate passes - produced the LARGEST strength swings, on top of a 0.35 floor that
        // already started the response at over a third power. Both are now serialized fields
        // with an exponent >1 by default; see their remarks for the numbers.
        private float CurrentPressure
        {
            get
            {
                var pen = Pen.current;
                if (pen == null || !pen.tip.isPressed) return 1f;
                float shaped = Mathf.Pow(Mathf.Clamp01(_smoothedPenPressure), pressureCurve);
                return pressureFloor + (1f - pressureFloor) * shaped;
            }
        }

        // Real tablet pressure sensors are noisy enough that reading Pen.current.pressure raw
        // every frame produces a visibly jittery, stair-stepped stroke rather than the smooth,
        // evenly-building ridge ZBrush/Blender strokes have - this exponentially chases the raw
        // value instead of tracking it 1:1, filtering that noise out before CurrentPressure's
        // curve is applied. Deliberately updated exactly once per frame (from Update(), not from
        // inside CurrentPressure's getter) so its smoothing rate doesn't scale with how many
        // times a brush's apply path runs this frame (once per Mirror plane).
        private void UpdatePenPressure()
        {
            var pen = Pen.current;
            // Deliberately leaves _smoothedPenPressure untouched while not pressed, rather than
            // resetting it to 1 (full strength) - resetting meant every new touch-down eased
            // DOWN from full strength for its first few frames instead of picking up from
            // wherever pressure actually was, which read as a strength spike right at the start
            // of each stroke (most visible when toggling Accumulate - that click lifts the pen to
            // tap the UI checkbox, then touches back down to resume, so the very next stroke got
            // the spike). Holding the last value means a fresh touch continues smoothing from a
            // realistic starting point instead of a synthetic one.
            if (pen == null || !pen.tip.isPressed) return;

            float raw = pen.pressure.ReadValue();
            _smoothedPenPressure = Mathf.Lerp(_smoothedPenPressure, raw, Mathf.Clamp01(Time.deltaTime * PressureSmoothingSpeed));
        }

        private const float StrokeSpeedSmoothingSpeed = 15f;
        private Vector3? _lastStrokeHitPointWorld;
        private float _strokeSpeed;

        // Called once per brush application (not per Mirror copy - see UpdatePenPressure's own
        // remarks on why that matters) from each accumulate-capable brush's Handle*Input, right
        // after that frame's raycast hit is known. Smoothed for the same reason pressure is -
        // raw per-frame speed is noisy (frame-time jitter, small hand tremor), and feeding that
        // straight into the accumulate rate would just trade "blob at the stop" for "flicker
        // mid-stroke".
        private void UpdateStrokeSpeed(Vector3 worldHitPoint)
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            // No prior point on a stroke's first frame - every stroke's first touch is
            // inherently a stationary point sample, not yet a stroke, so treat it as speed 0
            // (gentle first dab) rather than assuming full speed.
            float instant = _lastStrokeHitPointWorld.HasValue
                ? Vector3.Distance(worldHitPoint, _lastStrokeHitPointWorld.Value) / dt
                : 0f;
            _lastStrokeHitPointWorld = worldHitPoint;
            _strokeSpeed = Mathf.Lerp(_strokeSpeed, instant, Mathf.Clamp01(dt * StrokeSpeedSmoothingSpeed));
        }

        public bool IsAdjustingStrength => _isAdjustingStrength;

        /// True while the R-hold gauge is armed - SculptUIBuilder reads this to know when to
        /// show the density label/slider, and UpdateBrushCursor reads it to suppress the
        /// ordinary brush ring for the same reason it already suppresses it for a region
        /// gesture or a non-Sculpt gizmo.
        public bool IsAdjustingRemeshDensity => _isAdjustingRemeshDensity;
        /// Where the density gauge should anchor itself, frozen at the moment R armed the gauge -
        /// same frozen-anchor idiom as _resizeAnchorScreenPos/_strengthAdjustAnchorScreenPos.
        public Vector2 RemeshDensityAnchorScreenPosition => _remeshDensityAnchorScreenPos;

        // Polled by SculptUIBuilder every frame to draw the 2D ring cursor (see
        // UpdateBrushCursor) - same read-only delegation pattern as IsAdjustingStrength
        // above.
        // The crosshair drawn in place of the ring while a region gesture is armed - see
        // UpdateBrushCursor for why the ring alone left the viewport looking cursorless.
        private bool _showRegionCrosshair;
        private Vector2 _regionCrosshairScreenPos;
        public bool ShowRegionCrosshair => _showRegionCrosshair;
        public Vector2 RegionCrosshairScreenPosition => _regionCrosshairScreenPos;

        public bool ShowBrushCursor => _showBrushCursor;
        public Vector2 BrushCursorScreenPosition => _brushCursorScreenPos;
        public float BrushCursorScreenDiameter => _brushCursorScreenDiameter;
        public Color BrushCursorColor => _brushCursorColor;
        public bool BrushCursorDashed => _brushCursorDashed;

        // The Lazy Mouse rope, for the tether line SculptUIBuilder draws while a stabilized
        // stroke is running (ZBrush/Nomad both draw the same thing). Without it the stabilizer
        // is invisible: the brush is acting somewhere the pointer is not, with nothing on screen
        // to say so, and a lag you cannot see reads as the app dropping input rather than as the
        // smoothing you asked for. The line also makes the rope's LENGTH legible, which is what
        // the Radius slider is actually setting.
        //
        // `To` is where the brush is really working (and where UpdateBrushCursor now puts the
        // ring, so the ring never lies about where a dab is about to land); `From` is the raw
        // pointer. Only meaningful while Active - the two lag one frame behind a brush handler
        // having run, which is invisible at frame rate and is why both come from the same
        // recorded pair rather than one being re-read live here.
        public bool LazyMouseTetherActive => _lazyMouseActive && _showBrushCursor;
        public Vector2 LazyMouseTetherFrom => _lazyMouseRawScreenPos;
        public Vector2 LazyMouseTetherTo => _lazyMouseScreenPos;

        // 0 right on stroke-release, easing back to 1 over StrokeEndFadeDuration - see
        // _strokeEndFadeTimer. SculptUIBuilder multiplies every cursor layer's own alpha by this.
        public float BrushCursorFadeAlpha => _strokeEndFadeTimer <= 0f
            ? 1f
            : Mathf.Clamp01(1f - _strokeEndFadeTimer / StrokeEndFadeDuration);

        // Double-click-in-viewport object switching (see HandleObjectPickDoubleClick) - lets
        // you make a different scene object the sculpt target without hunting for its row in
        // the Scene Graph panel. Tracked here rather than via EventSystem's PointerClick
        // clickCount (what SceneGraphUIBuilder's row double-click uses) because the viewport
        // isn't a uGUI element - there's no PointerClick event to read a clickCount off of, so
        // this measures the same thing by hand against consecutive wasPressedThisFrame presses.
        private float _lastLeftClickTime = -1f;
        private Vector2 _lastLeftClickScreenPos;
        private const float DoubleClickMaxInterval = 0.35f;
        private const float DoubleClickMaxPixelDist = 12f;

        // Bare Z (not Ctrl+Z) is deliberate: this app runs inside the Unity Editor during
        // development, where Ctrl+Z is already bound to the EDITOR's own global Undo shortcut
        // and can fire instead of (or alongside) this one regardless of which window has
        // focus. A bare key isn't bound to anything Editor-level, so it reaches Keyboard.current
        // reliably - the same reasoning the existing S (resize) and M (remesh) shortcuts
        // already rely on.
        private void HandleUndoRedoKeys()
        {
            var kb = Keyboard.current;
            if (kb == null || _isResizingBrush || _isAdjustingStrength || _isAdjustingRemeshDensity) return;
            if (!kb.zKey.wasPressedThisFrame) return;

            bool redo = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            // The ZSphere tool keeps its own history for the rig (a scaffold, not a scene object -
            // see ZSphereController's rig-undo remarks), and takes the key while it is the active
            // tool and has something left to step through. Asking IT rather than duplicating the
            // condition here is what guarantees exactly one of the two answers a given press, and
            // that Z falls back to scene history the moment the rig's own runs out.
            if (_zsphereForUndo == null) _zsphereForUndo = FindFirstObjectByType<ZSphereController>();
            if (_zsphereForUndo != null && _zsphereForUndo.HandlesUndoKey(redo)) return;

            if (redo) Redo(); else Undo();
        }

        // Only ever looked up on a frame Z is actually pressed, so the find costs nothing in a
        // scene that has no ZSphereController at all.
        private ZSphereController _zsphereForUndo;

        // Ctrl+S / Ctrl+Shift+S, matching the quick-save/save-as split most creative software
        // uses (see SceneGraphUIBuilder.Save/SaveAs). Routed via SendMessage rather than a
        // direct reference - same "invoke a private MonoBehaviour method without reflection"
        // idiom RebuildOtherPanels already uses - because this controller has no other reason
        // to depend on the scene-file UI panel.
        private void HandleSaveKeys()
        {
            var kb = Keyboard.current;
            if (kb == null || !CtrlHeld) return;
            if (!kb.sKey.wasPressedThisFrame) return;

            bool saveAs = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            if (_sceneGraphPanel == null) _sceneGraphPanel = FindFirstObjectByType<SceneGraphUIBuilder>();
            if (_sceneGraphPanel == null) return;

            _sceneGraphPanel.gameObject.SendMessage(saveAs ? "SaveAs" : "Save", SendMessageOptions.DontRequireReceiver);
        }

        // Delete on the selected scene object - prompts before deleting (see
        // SceneGraphUIBuilder.ShowDeleteSelectedConfirm), routed via SendMessage for the same
        // reason as HandleSaveKeys above. Skipped while the ZSphere tool is active: there,
        // Delete/Backspace already means "delete the selected RIG NODE" (see
        // ZSphereController.Input.HandleKeys), and letting both fire off one press would delete
        // a node AND the object it belongs to. Also skipped while a uGUI text field has focus
        // (typing in the rename box, editing a scene-graph row) so Delete edits text instead of
        // reaching for the object underneath it.
        private void HandleDeleteObjectKey()
        {
            var kb = Keyboard.current;
            if (kb == null || !kb.deleteKey.wasPressedThisFrame) return;
            if (IsTypingInUI()) return;
            if (Gizmo != null && Gizmo.Mode == GizmoMode.ZSphere) return;

            if (_sceneGraphPanel == null) _sceneGraphPanel = FindFirstObjectByType<SceneGraphUIBuilder>();
            if (_sceneGraphPanel == null) return;

            _sceneGraphPanel.gameObject.SendMessage("ShowDeleteSelectedConfirm", SendMessageOptions.DontRequireReceiver);
        }

        // Only ever looked up on a frame Ctrl+S or Delete is actually pressed, so the find costs
        // nothing otherwise - same reasoning as _zsphereForUndo above.
        private SceneGraphUIBuilder _sceneGraphPanel;

        private static bool IsTypingInUI()
        {
            var focused = UnityEngine.EventSystems.EventSystem.current != null
                ? UnityEngine.EventSystems.EventSystem.current.currentSelectedGameObject : null;
            if (focused == null) return false;
            var field = focused.GetComponent<UnityEngine.UI.InputField>();
            return field != null && field.isFocused;
        }

        private void HandleBrushSwitchKeys()
        {
            var kb = Keyboard.current;
            if (kb == null) return;

            HandleShiftSmoothOverride(kb);

            // Suppressed while the Shift-to-Smooth override is active (see
            // HandleShiftSmoothOverride) - switching brushes mid-hold would fight with what
            // Shift is about to restore on release, same reasoning ZBrush/Blender's own
            // hold-to-smooth doesn't let other brush hotkeys interrupt it either.
            if (_isShiftSmoothActive) return;

            // Also suppressed while the strength gauge (F) is up - CurrentBrush's setter swaps
            // brushStrength out from under _strengthAdjustStartValue's mid-drag baseline
            // (BrushRadius has no such per-brush swap, which is why HandleBrushResizeKey's S
            // doesn't need this same guard), so switching brushes mid-drag would let the next
            // mouse-move frame stomp the NEWLY-switched-to brush's stored strength with a value
            // computed from the OLD brush's baseline.
            if (_isAdjustingStrength) return;

            if (kb.digit1Key.wasPressedThisFrame) CurrentBrush = BrushType.Move;
            else if (kb.digit2Key.wasPressedThisFrame) CurrentBrush = BrushType.Clay;
            else if (kb.digit3Key.wasPressedThisFrame) CurrentBrush = BrushType.Smooth;
            else if (kb.digit4Key.wasPressedThisFrame) CurrentBrush = BrushType.Crease;
            else if (kb.digit5Key.wasPressedThisFrame) CurrentBrush = BrushType.Inflate;
            else if (kb.digit6Key.wasPressedThisFrame) CurrentBrush = BrushType.Flatten;
            else if (kb.digit7Key.wasPressedThisFrame) CurrentBrush = BrushType.Pose;

            // M used to trigger Remesh directly; moved to R (still reachable via the Remesh
            // button in the Brush panel either way, or a plain R tap - see
            // HandleRemeshDensityKey) so M is free for the mask-paint toggle, matching most
            // sculpting apps' M-for-mask convention.
            if (kb.mKey.wasPressedThisFrame) IsMaskPaintMode = !IsMaskPaintMode;

            // X toggles Mirror X on the selected object, Blender/ZBrush-style. Mirror resolves
            // against the live selection (see its getter) and self-heals a missing
            // MirrorController, so this is safe the moment anything is selected.
            if (kb.xKey.wasPressedThisFrame)
            {
                MirrorController mirror = Mirror;
                if (mirror != null) mirror.MirrorX = !mirror.MirrorX;
            }
        }

        // Holding Shift temporarily switches to the Smooth brush, ZBrush/Blender-style,
        // reverting to whatever brush was active the moment Shift is released - lets you
        // smooth out a stroke without breaking flow to switch brushes and back. Guarded off
        // during the resize gauge for the same reason other input handlers are.
        private void HandleShiftSmoothOverride(Keyboard kb)
        {
            if (_isResizingBrush || _isAdjustingStrength || _isAdjustingRemeshDensity) return;
            bool shiftHeld = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            // Shift means "act on everything outside the region" while a region gesture is armed
            // (see RegionSelectTool), so it must not also swap the brush underneath it - that
            // would repaint the brush row and leave the panel highlighting Smooth for the whole
            // gesture. The release branch is deliberately still reachable, so an override that
            // was already running when the mode was armed still gets unwound.
            if (shiftHeld && !_isShiftSmoothActive && RegionSelectActive) return;

            // Same shape of guard for mask painting, and needed twice over: the brush is not used
            // at all while masking (the click paints mask - see HandleMaskPaintInput), so swapping
            // it under Shift only mislabels the panel; and selecting a brush now leaves mask paint
            // mode (see CurrentBrush's setter), so without this a stray Shift would silently drop
            // the user out of masking mid-selection. As above, the release branch stays reachable so
            // an override already running when masking was entered still unwinds.
            if (shiftHeld && !_isShiftSmoothActive && _isMaskPaintMode) return;

            if (shiftHeld && !_isShiftSmoothActive)
            {
                _preShiftBrush = currentBrush;
                _isShiftSmoothActive = true;
                CurrentBrush = BrushType.Smooth;
            }
            else if (!shiftHeld && _isShiftSmoothActive)
            {
                _isShiftSmoothActive = false;
                // Unwinding a held modifier is not the user choosing a tool, so it must not carry
                // that choice's side effect of leaving mask paint mode (see CurrentBrush's setter).
                // The guard above keeps the override from starting while masking, but the Mask
                // BUTTON is still clickable mid-hold, and dropping the mode on the next Shift
                // release would look like the button simply failed.
                bool maskWasOn = _isMaskPaintMode;
                CurrentBrush = _preShiftBrush;
                IsMaskPaintMode = maskWasOn;
            }
        }

        // Holding S enters a resize mode (instead of sculpting) where horizontal mouse
        // movement scrubs BrushRadius live, ZBrush/Blender-style - the ring cursor itself
        // (UpdateBrushCursor/SculptUIBuilder) grows and shrinks with it, so there's no
        // separate popup readout to keep in sync.
        private void HandleBrushResizeKey()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            // CtrlHeld excluded: Ctrl+S is the save hotkey (see HandleSaveKeys) and must not
            // also drop the brush into resize mode.
            if (kb.sKey.wasPressedThisFrame && !CtrlHeld)
            {
                EndActiveDrags(); // don't leave a grab mid-drag while resizing
                _isResizingBrush = true;
                _resizeStartRadius = brushRadius;
                _resizeStartMouseX = mouse.position.ReadValue().x;
                _resizeAnchorScreenPos = mouse.position.ReadValue();
            }
            else if (_isResizingBrush && !kb.sKey.isPressed)
            {
                _isResizingBrush = false;
            }

            if (!_isResizingBrush) return;

            float deltaX = mouse.position.ReadValue().x - _resizeStartMouseX;
            BrushRadius = _resizeStartRadius + deltaX * ResizeSensitivity;
        }

        // Holding F enters a strength-adjust mode (instead of sculpting) where horizontal
        // mouse movement scrubs BrushStrength live - same S-drag UX as HandleBrushResizeKey
        // above, but for the CURRENT brush's own strength (see _brushStrengthPerType) rather
        // than the shared BrushRadius. UpdateBrushCursor/SculptUIBuilder show a red inner
        // circle inside the ring cursor for the duration, scaled to the live strength value -
        // see IsAdjustingStrength.
        private void HandleBrushStrengthKey()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (kb.fKey.wasPressedThisFrame)
            {
                EndActiveDrags(); // don't leave a grab mid-drag while adjusting strength
                _isAdjustingStrength = true;
                _strengthAdjustStartValue = brushStrength;
                _strengthAdjustStartMouseX = mouse.position.ReadValue().x;
                _strengthAdjustAnchorScreenPos = mouse.position.ReadValue();
            }
            else if (_isAdjustingStrength && !kb.fKey.isPressed)
            {
                _isAdjustingStrength = false;
            }

            if (!_isAdjustingStrength) return;

            float deltaX = mouse.position.ReadValue().x - _strengthAdjustStartMouseX;
            BrushStrength = _strengthAdjustStartValue + deltaX * StrengthAdjustSensitivity;
        }

        // R is dual-purpose: a plain tap still fires the old immediate Remesh() at whatever
        // resolution is already set, but holding it past RemeshHoldThreshold instead arms a
        // density gauge - same S/F-drag feel as the two handlers above (horizontal mouse
        // movement scrubs the value), except the changed value isn't applied to the geometry
        // live. Remeshing is too expensive to run on every mouse-move frame of a drag (see
        // MeshRemesher's own hitch-at-high-resolution remarks), so the drag only scrubs
        // RemeshResolution and the DensityGrid preview; the actual rebuild happens once, on
        // release.
        private void HandleRemeshDensityKey()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (kb.rKey.wasPressedThisFrame)
            {
                _rKeyDownTime = Time.unscaledTime;
            }
            else if (_rKeyDownTime >= 0f && kb.rKey.isPressed && !_isAdjustingRemeshDensity
                     && !_isResizingBrush && !_isAdjustingStrength
                     && Time.unscaledTime - _rKeyDownTime >= RemeshHoldThreshold)
            {
                EndActiveDrags(); // don't leave a grab mid-drag while adjusting density
                _isAdjustingRemeshDensity = true;
                _remeshDensityStartValue = remeshResolution;
                _remeshDensityStartMouseX = mouse.position.ReadValue().x;
                _remeshDensityAnchorScreenPos = mouse.position.ReadValue();
            }
            else if (_rKeyDownTime >= 0f && !kb.rKey.isPressed)
            {
                // A tap that never crossed the hold threshold keeps the pre-existing shortcut
                // (Remesh at whatever resolution was already set); a completed drag commits the
                // gauge's final value the same way the Remesh button does. Same call either way.
                _isAdjustingRemeshDensity = false;
                _rKeyDownTime = -1f;
                Remesh();
            }

            if (!_isAdjustingRemeshDensity) return;

            float deltaX = mouse.position.ReadValue().x - _remeshDensityStartMouseX;
            RemeshResolution = Mathf.RoundToInt(_remeshDensityStartValue + deltaX * RemeshDensityDragSensitivity);
        }

        // Holding Space arms the radial tool-select menu (RadialMenuUIBuilder) - frozen at the
        // position the key went down, ZBrush/Blender-style, so the ring of tool icons doesn't
        // slide around under the cursor while picking (same anchor idiom as the S/F gauges
        // above). Space is otherwise unused by this app, so a bare key is safe. Closed either
        // by releasing Space here (cancel, nothing selected) or by RadialMenuUIBuilder calling
        // CloseRadialMenu after a wedge click (confirm) - both just clear the same flag, so
        // UpdateBrushCursor/HandleSculptInput only ever need to check the one bool below.
        private bool _radialMenuOpen;
        private Vector2 _radialMenuScreenPos;

        public bool IsRadialMenuOpen => _radialMenuOpen;
        public Vector2 RadialMenuScreenPosition => _radialMenuScreenPos;

        private void HandleRadialMenuKey()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            // Shift+Space is the OTHER radial menu (see HandleRegionRadialMenuKey) - excluded
            // here so holding Shift first and tapping Space doesn't pop both at once.
            bool shiftHeld = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            // Suppressed while another drag gauge or a region gesture already owns the mouse -
            // same guard list HandleShiftSmoothOverride uses, for the same reason: two input
            // modes both scrubbing off mouse position/clicks at once would fight each other.
            if (kb.spaceKey.wasPressedThisFrame && !shiftHeld && !_isResizingBrush && !_isAdjustingStrength &&
                !_isAdjustingRemeshDensity && !RegionSelectActive && !_regionRadialMenuOpen)
            {
                EndActiveDrags(); // don't leave a grab mid-drag while the menu is up
                _radialMenuOpen = true;
                _radialMenuScreenPos = mouse.position.ReadValue();
            }
            // Shift coming down mid-hold also bails out (rather than waiting for Space to
            // release too) so it can't get stuck open while HandleRegionRadialMenuKey below
            // declines to take over an already-armed menu.
            else if (_radialMenuOpen && (!kb.spaceKey.isPressed || shiftHeld))
            {
                _radialMenuOpen = false;
            }
        }

        /// Called by RadialMenuUIBuilder the instant a wedge is clicked, so picking a tool
        /// closes the menu right away instead of waiting for Space to also come up.
        public void CloseRadialMenu() => _radialMenuOpen = false;

        // Shift+Space arms the box/lasso region radial menu (RegionRadialMenuUIBuilder) - same
        // frozen-position-on-down idiom as HandleRadialMenuKey above, just gated on Shift so the
        // two menus never both try to open from the same Space press.
        private bool _regionRadialMenuOpen;
        private Vector2 _regionRadialMenuScreenPos;

        public bool IsRegionRadialMenuOpen => _regionRadialMenuOpen;
        public Vector2 RegionRadialMenuScreenPosition => _regionRadialMenuScreenPos;

        private void HandleRegionRadialMenuKey()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            bool shiftHeld = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            if (kb.spaceKey.wasPressedThisFrame && shiftHeld && !_isResizingBrush && !_isAdjustingStrength &&
                !_isAdjustingRemeshDensity && !RegionSelectActive && !_radialMenuOpen)
            {
                EndActiveDrags();
                _regionRadialMenuOpen = true;
                _regionRadialMenuScreenPos = mouse.position.ReadValue();
            }
            else if (_regionRadialMenuOpen && !kb.spaceKey.isPressed)
            {
                _regionRadialMenuOpen = false;
            }
        }

        /// Called by RegionRadialMenuUIBuilder the instant a wedge is clicked, mirroring
        /// CloseRadialMenu above.
        public void CloseRegionRadialMenu() => _regionRadialMenuOpen = false;

        // Shared by every brush handler's invert check below - Ctrl mirrors Blender's
        // hold-to-invert sculpt convention, alongside this app's pre-existing right-mouse-
        // inverts convention (kept for parity with users already used to that scheme).
        private static bool CtrlHeld => Keyboard.current != null &&
            (Keyboard.current.leftCtrlKey.isPressed || Keyboard.current.rightCtrlKey.isPressed);

        // Lets CameraOrbitController skip its own scroll-zoom while the cursor is over the
        // sculptable surface, so the same wheel resizes the active brush there instead (see
        // HandleBrushSizeScroll) and zooms the camera everywhere else.
        public static bool IsHoveringSculptSurface { get; private set; }

        // Scroll-to-resize: adjusts BrushRadius by a percentage per notch, same feel as
        // CameraOrbitController's own scroll-zoom (see its zoomPercentPerNotch remarks), so
        // brush size can be tuned without reaching for the S-drag resize gauge. Runs after
        // HandleSculptInput so _isHovering/_isOverUI already reflect this frame's raycast.
        private const float ScrollResizePercentPerNotch = 0.1f;

        private void HandleBrushSizeScroll()
        {
            var mouse = Mouse.current;
            IsHoveringSculptSurface = mouse != null && _isHovering && !_isOverUI
                && !_isResizingBrush && !_isAdjustingStrength && !_isAdjustingRemeshDensity;
            if (!IsHoveringSculptSurface) return;

            float scroll = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(scroll) < 0.01f) return;

            BrushRadius = brushRadius * (1f + Mathf.Sign(scroll) * ScrollResizePercentPerNotch);
        }

        /// Double-clicking anywhere in the viewport (that isn't UI, and isn't the Alt-orbit
        /// gesture) selects whichever registered SculptableMesh is under the cursor, making it
        /// the new sculpt target - the same effect as clicking its row in the Scene Graph
        /// panel. Deliberately raycasts every registered object rather than just the current
        /// Target: a click that misses the current target already has no sculpting side effect
        /// (see the individual brush handlers below, which just return on a miss), so this only
        /// ever adds behavior on an otherwise-inert click. Double-clicking the ALREADY-primary
        /// object still leaves two ordinary brush dabs at the click point from ProcessInput's
        /// own handling of those two presses - accepted as a minor quirk rather than adding the
        /// extra latency of holding every click for a double-click window before sculpting.
        private void HandleObjectPickDoubleClick(Mouse mouse, bool overUI, bool altHeld)
        {
            if (overUI || altHeld || !mouse.leftButton.wasPressedThisFrame) return;

            Vector2 pos = mouse.position.ReadValue();
            bool isDoubleClick = Time.unscaledTime - _lastLeftClickTime <= DoubleClickMaxInterval &&
                                  Vector2.Distance(pos, _lastLeftClickScreenPos) <= DoubleClickMaxPixelDist;

            if (isDoubleClick)
            {
                PickObjectUnderCursor(pos);
                _lastLeftClickTime = -1f; // consume - a third quick click starts a fresh pair, not another pick
            }
            else
            {
                _lastLeftClickTime = Time.unscaledTime;
                _lastLeftClickScreenPos = pos;
            }
        }

        private void PickObjectUnderCursor(Vector2 screenPos)
        {
            if (Selection == null || cam == null) return;

            // Shares SelectionManager.Raycast with TransformGizmo's own click-to-select rather
            // than keeping a second copy of the same visible-objects-only hit test.
            SculptableMesh closest = Selection.Raycast(cam.ScreenPointToRay(screenPos));

            // Only flash on an actual switch - double-clicking the object that's already
            // primary shouldn't flash, since nothing about the sculpt target changed.
            if (closest != null && closest != Selection.PrimarySelection)
            {
                Selection.Select(closest, false);
                SelectionFlashEffect.Play(closest.gameObject);
            }
        }

        // Returns the screen position a paint/sculpt stroke should raycast from this frame -
        // the raw cursor, or (while Lazy Mouse is on and a stroke is actively being drawn) a
        // point trailing behind it on a taut "rope" of lazyMouseRadius pixels (see that field's
        // remarks). Used by every brush's input handler and by mask painting; deliberately NOT
        // used by Move's grab-drag (documented as intentionally 1:1 with the cursor) or by the
        // resize/strength drag gauges (S/F - those want raw, instant tracking).
        //
        // Bypasses straight to the raw position whenever no paint button is held, so ordinary
        // hovering (and the brush-size preview it drives) is never laggy - only an actual
        // stroke engages the rope. The rope resets to the raw position on the first frame of
        // every new stroke, so a fresh click always starts exactly under the cursor rather than
        // inheriting wherever a previous, unrelated stroke left it.
        private Vector2 GetStrokeScreenPosition(Mouse mouse)
        {
            Vector2 raw = mouse.position.ReadValue();
            _lazyMouseRawScreenPos = raw;
            if (!lazyMouseEnabled) { _lazyMouseActive = false; return raw; }

            bool pressed = mouse.leftButton.isPressed || mouse.rightButton.isPressed;
            if (!pressed) { _lazyMouseActive = false; return raw; }

            if (!_lazyMouseActive)
            {
                _lazyMouseActive = true;
                _lazyMouseScreenPos = raw;
                return _lazyMouseScreenPos;
            }

            Vector2 delta = raw - _lazyMouseScreenPos;
            float dist = delta.magnitude;
            if (dist > lazyMouseRadius)
                _lazyMouseScreenPos += delta.normalized * ((dist - lazyMouseRadius) * lazyMouseStrength);

            return _lazyMouseScreenPos;
        }

        // Drives the 2D screen-space ring cursor (see ShowBrushCursor/BrushCursorScreenPosition/
        // BrushCursorScreenDiameter/BrushCursorColor - SculptUIBuilder polls these to actually
        // draw it) and owns Cursor.visible alongside it: the OS pointer is hidden whenever the
        // ring is shown, and restored the instant it isn't, so a UI panel or another tool always
        // gets the ordinary pointer back. Sized/tinted every frame the same way the old
        // world-space BrushPreview sphere was - snapped to the sculpted surface while hovering
        // it, floating along the camera ray at the model's rough depth otherwise - just measured
        // in screen pixels instead of world units.
        private void UpdateBrushCursor()
        {
            bool show = false;
            Color color = PositiveColor;

            // Same "another tool owns the cursor" carve-out the old preview had - Transpose/
            // Scale drag the transform, ZSpheres place and grow rig spheres, and each shows its
            // own affordance instead - the R-hold density gauge joins them here, its grid/label
            // being its own affordance the same way (see DensityGrid).
            bool sculptToolActive = sculptableMesh != null && cam != null && !RegionSelectActive
                                    && !_isAdjustingRemeshDensity && !_radialMenuOpen && !_regionRadialMenuOpen
                                    && (Gizmo == null || Gizmo.Mode == GizmoMode.Sculpt);

            if (sculptToolActive && !_isOverUI)
            {
                Mouse mouse = Mouse.current;
                if (mouse != null)
                {
                    // Frozen at the drag's start point while resizing/adjusting strength,
                    // instead of following the live mouse - the S/F drags scrub their value off
                    // horizontal mouse DELTA alone (see HandleBrushResizeKey/
                    // HandleBrushStrengthKey), so the ring only needs to grow/shrink in place,
                    // not chase the mouse across the screen while the gesture plays out.
                    // While Lazy Mouse has the rope taut the ring follows the ROPE's near end,
                    // not the pointer: that is where the dab is actually landing (it is the
                    // position every brush handler just raycast from, and the position
                    // _hoverPoint below was derived from), so a ring left under the pointer
                    // would be drawing the brush somewhere it is not about to touch. The
                    // pointer end is not lost - SculptUIBuilder draws the tether line back to
                    // it, see LazyMouseTetherActive.
                    Vector2 screenPos = _isResizingBrush ? _resizeAnchorScreenPos
                        : _isAdjustingStrength ? _strengthAdjustAnchorScreenPos
                        : _lazyMouseActive ? _lazyMouseScreenPos
                        : mouse.position.ReadValue();
                    Vector3 worldPoint;
                    bool positive;

                    if (_isHovering)
                    {
                        worldPoint = _hoverPoint + _hoverNormal * 0.01f;
                        positive = _previewPositive;
                    }
                    else
                    {
                        float fallbackDistance = Mathf.Max(1f, Vector3.Distance(cam.transform.position, sculptableMesh.transform.position));
                        Ray ray = cam.ScreenPointToRay(screenPos);
                        worldPoint = ray.GetPoint(fallbackDistance);
                        positive = true; // neutral tint when just showing size, not actively sculpting
                    }

                    float diameterPx = ProjectDiameterToScreenPixels(worldPoint, brushRadius * AverageScale());
                    if (diameterPx > 0f)
                    {
                        show = true;
                        _brushCursorScreenPos = screenPos;
                        // Floored so a tiny brush viewed from far away still reads as a visible
                        // ring rather than shrinking past legibility - the projected size is
                        // otherwise unbounded in both directions.
                        _brushCursorScreenDiameter = Mathf.Max(diameterPx, MinCursorScreenDiameterPx);

                        // Smooth gets its own blue/dashed look (see SmoothColor/
                        // BrushCursorDashed) since it has no add/subtract polarity at all -
                        // showing it as an ordinary "positive" green dab would suggest it adds
                        // material the way Clay/Inflate/etc. do. The outer ring keeps this same
                        // tint while adjusting strength (F) too - it's still showing brush
                        // radius/polarity, unchanged - and SculptUIBuilder layers a separate red
                        // inner circle on top for the strength readout (see IsAdjustingStrength).
                        bool isSmooth = currentBrush == BrushType.Smooth;
                        color = isSmooth ? SmoothColor
                            : positive ? PositiveColor : NegativeColor;
                        _brushCursorDashed = isSmooth;
                    }
                }
            }

            // An armed region gesture suppresses the ring (sculptToolActive above) - but leaving
            // the viewport with NOTHING under the pointer is what made arming a mode read as
            // "my cursor is gone". Unity's own arrow does come back, and over a dark viewport at
            // the moment the familiar ring disappears that is not something anyone notices. So
            // the tool gets its own affordance instead: a crosshair, which every marquee tool in
            // every app draws, and which doubles as the signal that a region mode is armed at
            // all. Drawn by SculptUIBuilder from the two properties below, exactly like the ring.
            Mouse regionMouse = Mouse.current;
            _showRegionCrosshair = RegionSelectActive && !_isOverUI && regionMouse != null;
            if (_showRegionCrosshair) _regionCrosshairScreenPos = regionMouse.position.ReadValue();

            _showBrushCursor = show;
            if (show) _brushCursorColor = color;
            Cursor.visible = !show && !_showRegionCrosshair;
        }

        // Measures how many screen pixels `worldRadius` covers at `worldCenter` by projecting
        // both the center and a point one radius away (along the camera's own right vector,
        // always perpendicular to view direction) and comparing their screen positions - works
        // unmodified for perspective and orthographic cameras alike, unlike computing it from
        // FOV/distance by hand.
        private float ProjectDiameterToScreenPixels(Vector3 worldCenter, float worldRadius)
        {
            Vector3 centerScreen = cam.WorldToScreenPoint(worldCenter);
            if (centerScreen.z <= 0f) return 0f; // behind the camera
            Vector3 edgeScreen = cam.WorldToScreenPoint(worldCenter + cam.transform.right * worldRadius);
            return Vector2.Distance(centerScreen, edgeScreen) * 2f;
        }

        private float AverageScale()
        {
            Vector3 s = sculptableMesh.transform.lossyScale;
            return (s.x + s.y + s.z) / 3f;
        }

        private void OnDrawGizmos()
        {
            if (sculptableMesh == null) return;

            if (showWireframeGizmo && sculptableMesh.Mesh != null)
            {
                Gizmos.color = new Color(1f, 1f, 1f, 0.3f);
                Gizmos.DrawWireMesh(sculptableMesh.Mesh, sculptableMesh.transform.position,
                    sculptableMesh.transform.rotation, sculptableMesh.transform.lossyScale);
            }

            if (_isHovering)
            {
                Gizmos.color = _previewPositive ? PositiveColor : NegativeColor;
                Gizmos.DrawWireSphere(_hoverPoint, brushRadius * AverageScale());
                Gizmos.DrawLine(_hoverPoint, _hoverPoint + _hoverNormal * 0.2f);
            }
        }
    }
}
