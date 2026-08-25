using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Sculpting
{
    /// Raycasts from the camera into the sculptable mesh and deforms vertices under the
    /// cursor with the Move, Clay, or Smooth brush. Clay eases vertices toward a plateau along
    /// the hit normal - left mouse with the current positive/negative setting, right mouse
    /// inverted (same convention as most sculpting apps). Move instead grabs whatever the
    /// brush is touching on mouse-down and drags it 1:1 with the cursor along a plane facing
    /// the camera, tracked via screen-space delta rather than a live raycast each frame - so
    /// the grabbed region keeps following the cursor even once it drifts off the mesh's
    /// silhouette. Smooth relaxes vertices toward their mesh-neighbor average (see
    /// SculptableMesh.GetNeighborAverage). 1/2/3 switch brushes; holding S resizes the brush
    /// (drag horizontally) instead of sculpting, shown via a ZBrush-style popup gauge (see
    /// SculptUIBuilder). When MirrorController has any axis enabled, every brush application
    /// is repeated at each mirrored local-space position (see MirrorController.GetMirrorSigns)
    /// so strokes land symmetrically.
    [RequireComponent(typeof(SculptableMesh))]
    [RequireComponent(typeof(MirrorController))]
    public class SculptController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private Camera cam;
        [SerializeField] private SculptableMesh sculptableMesh;
        [SerializeField] private MirrorController mirrorController;
        public GameObject brushPreview;

        [Header("Brush Settings")]
        [SerializeField, Range(0.01f, 1f)] private float brushStrength = 0.1f;
        [SerializeField, Range(0.05f, 2f)] private float brushRadius = 0.5f;
        [SerializeField] private BrushType currentBrush = BrushType.Move;
        [SerializeField] private bool isPositive = true;

        [Header("Clay Brush")]
        [SerializeField, Range(0.1f, 1.5f)] private float clayHeightFactor = 0.6f;
        [SerializeField] private bool useAlpha;
        [SerializeField] private BrushAlphaType alphaType = BrushAlphaType.SoftCircle;
        [SerializeField, Range(0f, 360f)] private float alphaRotation;
        [SerializeField, Range(0.3f, 3f)] private float alphaScale = 1f;
        [SerializeField] private bool invertAlpha;

        [Header("Crease / Dam Standard Brush")]
        [SerializeField, Range(0f, 1f)] private float creasePinch = 0.6f;
        [SerializeField, Range(0.05f, 1f)] private float creaseDepthFactor = 0.35f;
        [SerializeField, Range(0f, 1f)] private float damLipHeight = 0.25f;

        [Header("Masking")]
        // 0 = smoothstep across the whole radius (soft, gradual edges), 1 = full weight
        // everywhere inside the radius with a hard cutoff (immediate, opaque) - see
        // SculptableMesh.PaintMask's hardness remarks.
        [SerializeField, Range(0f, 1f)] private float maskHardness = 0.5f;

        [Header("Remesh Settings")]
        [SerializeField, Range(4, 500)] private int remeshResolution = 24;

        [Header("Debug")]
        [SerializeField] private bool showWireframeGizmo = false;
        [SerializeField] private bool logRayHits = false;

        // Clay eases each vertex toward a plateau offset from the hit point so volume builds
        // up instead of spiking indefinitely; it scales with Time.deltaTime for frame-rate
        // independence. Move instead drags 1:1 with the cursor (see HandleMoveDrag), so it
        // has no speed/strength constant of its own - brushStrength only affects Clay.
        // (Plateau depth used to be a constant here too; it's now the serialized
        // clayHeightFactor field/ClayHeightFactor property above so it's tunable from the UI.)
        private const float ClaySpeed = 4f;
        // Smooth has no "amount" concept beyond how far it eases toward the neighbor
        // average each frame, so it gets its own speed constant rather than reusing Clay's.
        private const float SmoothSpeed = 4f;
        // Shared by Crease and Dam Standard, which reuse the same pinch+carve core.
        private const float CreaseSpeed = 4f;
        // Inflate pushes along each vertex's own normal at a constant rate (no target to
        // ease toward, unlike Clay/Crease/Smooth), so its factor is a plain velocity
        // multiplier rather than a lerp-fraction scale.
        private const float InflateSpeed = 4f;
        // Mask paint/erase rate range - reuses brushStrength/brushRadius rather than adding a
        // separate intensity slider, matching the "just a basic one" scope of the original
        // masking feature. maskHardness (see its own field) interpolates between these two:
        // at hardness 0 the rate matches the old constant (4) - a deliberately slow accumulation
        // so a soft brush stays a gentle, dwell-to-build-up wash, matching what "soft" means in
        // most sculpting apps. At hardness 1 the rate is high enough that a single ordinary
        // click-drag reaches full mask in a fraction of a second even at default brushStrength,
        // matching "hard is immediately dark" - hardness alone reshaping the falloff (see
        // SculptableMesh.PaintMask) wasn't enough on its own, since the per-frame accumulation
        // amount was the same tiny value at the brush center regardless of hardness.
        private const float MaskPaintSpeedSoft = 4f;
        private const float MaskPaintSpeedHard = 40f;

        // How many world units BrushRadius changes per pixel of horizontal mouse movement
        // while resizing (holding S). Tuned so a full-width drag across a ~1080p window
        // covers roughly the whole 0.05-2 range.
        private const float ResizeSensitivity = 0.0025f;
        public const float MinBrushRadius = 0.05f;
        public const float MaxBrushRadius = 2f;

        private static readonly Color PositiveColor = new Color(0.2f, 1f, 0.4f);
        private static readonly Color NegativeColor = new Color(1f, 0.3f, 0.3f);

        private bool _isHovering;
        private Vector3 _hoverPoint;
        private Vector3 _hoverNormal;
        private bool _previewPositive;
        private Renderer _brushPreviewRenderer;
        private bool _isOverUI;
        // Last position the preview was legitimately shown at (on the mesh, or floating along
        // a viewport mouse ray) - reused whenever the mouse is over a UI panel (e.g. dragging
        // the brush radius slider) so the preview stays put near the model instead of jumping
        // to wherever the panel happens to be on screen.
        private Vector3 _lastGoodPreviewPos;

        // Previous stroke sample in the mesh's local space, used only by Dam Standard to
        // derive a stroke-travel direction for its leading-edge lip; null between strokes
        // (mouse up / hover lost / brush switched) so a fresh stroke starts symmetric.
        private Vector3? _lastDamHoverLocal;

        private bool _isMoveDragging;
        private Vector3 _dragPlanePoint;
        private Vector3 _dragPlaneNormal;
        private Vector3 _lastDragPoint;
        // One selection per active mirror sign, paired with the sign used to make it, so a
        // drag delta can be re-mirrored before being applied to that selection.
        private List<(SculptableMesh.GrabSelection selection, Vector3 sign)> _grabSelections;

        private bool _isResizingBrush;
        private float _resizeStartRadius;
        private float _resizeStartMouseX;
        private Vector2 _resizeAnchorScreenPos;

        private bool _isShiftSmoothActive;
        private BrushType _preShiftBrush;

        // Toggled by tapping M - see HandleMaskPaintInput. A persistent mode switch (like the
        // 1-5 brush hotkeys) rather than a held modifier (like Shift-to-Smooth), since painting
        // a mask is typically its own multi-stroke pass, not a quick one-off tweak mid-sculpt.
        private bool _isMaskPaintMode;

        // Reusable scratch buffers for Clay's area-plane weights and Smooth's relaxation
        // weights - sized once and grown on demand rather than allocated fresh every frame a
        // stroke is held, matching the allocation-avoidance already applied to MeshRemesher
        // (see VertexSpatialGrid/EmitQuads history).
        private float[] _clayWeightScratch = System.Array.Empty<float>();
        private float[] _smoothWeightScratch = System.Array.Empty<float>();

        // Vertex indices actually moved by the current frame's brush application (across every
        // mirror sign) - cleared at the start of each Apply*Brush wrapper, filled in by the
        // matching *Local method(s), then handed to SculptableMesh.ApplyVerticesLocal so it only
        // has to update the triangle-raycast grid for triangles touching these vertices instead
        // of rescanning the whole mesh. See TriangleSpatialGrid for why this matters at higher
        // triangle counts.
        private readonly HashSet<int> _dirtyVertexScratch = new HashSet<int>();

        // Smooth's per-application relaxation strength: brushStrength scales how many
        // Laplacian relaxation passes get folded into one application (from a single partial
        // pass at minimum strength up to MaxSmoothIterations full passes at maximum), not just
        // how far a single pass blends toward the neighbor average. A single 1-ring average is
        // inherently weak - it only pulls in direct neighbors, so no per-pass blend factor
        // alone removes wider bumps in one shot. Repeated passes propagate influence from
        // further-out neighbors each time, which is what actually flattens noise - the same
        // reason ZBrush/Blender's Smooth intensity effectively controls a repeated-relaxation
        // count rather than a single lerp factor. At the default brushStrength (0.1) this
        // resolves to exactly 1 pass, matching the brush's old feel; only higher strength
        // ramps into genuinely stronger multi-pass smoothing.
        private const int MaxSmoothIterations = 10;

        public float BrushStrength { get => brushStrength; set => brushStrength = Mathf.Clamp(value, 0.01f, 1f); }
        public float BrushRadius { get => brushRadius; set => brushRadius = Mathf.Clamp(value, MinBrushRadius, MaxBrushRadius); }
        public bool IsResizingBrush => _isResizingBrush;
        public Vector2 ResizeAnchorScreenPosition => _resizeAnchorScreenPos;

        public bool IsMaskPaintMode
        {
            get => _isMaskPaintMode;
            set
            {
                if (_isMaskPaintMode == value) return;
                _isMaskPaintMode = value;
                if (_isMaskPaintMode) EndMoveDrag(); // don't leave a grab mid-drag while painting mask
            }
        }

        public BrushType CurrentBrush
        {
            get => currentBrush;
            set
            {
                if (currentBrush != value)
                {
                    EndMoveDrag();
                    _lastDamHoverLocal = null;
                }
                currentBrush = value;
            }
        }
        public bool IsPositive { get => isPositive; set => isPositive = value; }
        public float ClayHeightFactor { get => clayHeightFactor; set => clayHeightFactor = Mathf.Clamp(value, 0.1f, 1.5f); }
        public float CreasePinch { get => creasePinch; set => creasePinch = Mathf.Clamp01(value); }
        public float CreaseDepthFactor { get => creaseDepthFactor; set => creaseDepthFactor = Mathf.Clamp(value, 0.05f, 1f); }
        public float DamLipHeight { get => damLipHeight; set => damLipHeight = Mathf.Clamp01(value); }
        public float MaskHardness { get => maskHardness; set => maskHardness = Mathf.Clamp01(value); }
        public bool UseAlpha { get => useAlpha; set => useAlpha = value; }
        public BrushAlphaType AlphaType { get => alphaType; set => alphaType = value; }
        public float AlphaRotation { get => alphaRotation; set => alphaRotation = Mathf.Repeat(value, 360f); }
        public float AlphaScale { get => alphaScale; set => alphaScale = Mathf.Clamp(value, 0.3f, 3f); }
        public bool InvertAlpha { get => invertAlpha; set => invertAlpha = value; }
        public bool ShowWireframeGizmo { get => showWireframeGizmo; set => showWireframeGizmo = value; }
        public bool LogRayHits { get => logRayHits; set => logRayHits = value; }
        public int RemeshResolution { get => remeshResolution; set => remeshResolution = Mathf.Clamp(value, 4, 500); }

        // GetIndexCount/vertexCount rather than .triangles/.vertices - those copy the whole
        // index/vertex buffer on every access, which would be a real cost read every frame by
        // the UI's poly-count display at multi-million-triangle mesh sizes.
        public int TriangleCount => sculptableMesh != null && sculptableMesh.Mesh != null
            ? (int)sculptableMesh.Mesh.GetIndexCount(0) / 3 : 0;
        public int VertexCount => sculptableMesh != null && sculptableMesh.Mesh != null
            ? sculptableMesh.Mesh.vertexCount : 0;

        public bool CanUndo => sculptableMesh != null && sculptableMesh.CanUndo;
        public bool CanRedo => sculptableMesh != null && sculptableMesh.CanRedo;
        public void Undo() { EndMoveDrag(); sculptableMesh.Undo(); }
        public void Redo() { EndMoveDrag(); sculptableMesh.Redo(); }

        // Not wired into undo/redo, same deliberate scope call as PaintMask itself (see
        // SculptableMesh.PaintMask remarks) - masking doesn't move geometry.
        public void InvertMask() => sculptableMesh.InvertMask();

        // Lazily resolved (rather than relying on Awake) since SculptUIBuilder reads this
        // while building the HUD, and MonoBehaviour Awake order between separate components
        // isn't guaranteed - its own Awake may not have run first.
        public MirrorController Mirror => mirrorController != null ? mirrorController : (mirrorController = GetComponent<MirrorController>());

        private void Awake()
        {
            if (sculptableMesh == null) sculptableMesh = GetComponent<SculptableMesh>();
            if (mirrorController == null) mirrorController = GetComponent<MirrorController>();
            if (cam == null) cam = Camera.main;
            if (brushPreview == null) brushPreview = GameObject.Find("BrushPreview");
            if (brushPreview != null) _brushPreviewRenderer = brushPreview.GetComponent<Renderer>();

            // Overrides whatever material is authored on the BrushPreview GameObject with a
            // runtime one that always draws on top of the depth buffer (see
            // BrushPreviewOverlay.shader) - a normal depth-tested material gets swallowed by
            // the sculpted mesh whenever the preview's position lands even slightly behind its
            // surface, which happens easily during the S-drag resize gesture.
            if (_brushPreviewRenderer != null)
            {
                Shader overlayShader = Shader.Find("Custom/BrushPreviewOverlay");
                if (overlayShader != null) _brushPreviewRenderer.material = new Material(overlayShader);
            }

            if (sculptableMesh != null) _lastGoodPreviewPos = sculptableMesh.transform.position;
        }

        private void Update()
        {
            HandleBrushSwitchKeys();
            HandleBrushResizeKey();
            HandleUndoRedoKeys();
            HandleSculptInput();
            UpdateBrushPreview();
        }

        // Bare Z (not Ctrl+Z) is deliberate: this app runs inside the Unity Editor during
        // development, where Ctrl+Z is already bound to the EDITOR's own global Undo shortcut
        // and can fire instead of (or alongside) this one regardless of which window has
        // focus. A bare key isn't bound to anything Editor-level, so it reaches Keyboard.current
        // reliably - the same reasoning the existing S (resize) and M (remesh) shortcuts
        // already rely on.
        private void HandleUndoRedoKeys()
        {
            var kb = Keyboard.current;
            if (kb == null || _isResizingBrush) return;

            if (kb.zKey.wasPressedThisFrame)
            {
                if (kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed) Redo();
                else Undo();
            }
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

            if (kb.digit1Key.wasPressedThisFrame) CurrentBrush = BrushType.Move;
            else if (kb.digit2Key.wasPressedThisFrame) CurrentBrush = BrushType.Clay;
            else if (kb.digit3Key.wasPressedThisFrame) CurrentBrush = BrushType.Smooth;
            else if (kb.digit4Key.wasPressedThisFrame) CurrentBrush = BrushType.Crease;
            else if (kb.digit5Key.wasPressedThisFrame) CurrentBrush = BrushType.DamStandard;
            else if (kb.digit6Key.wasPressedThisFrame) CurrentBrush = BrushType.Inflate;

            // M used to trigger Remesh directly; moved to R (still reachable via the Remesh
            // button in the Brush panel either way) so M is free for the mask-paint toggle,
            // matching most sculpting apps' M-for-mask convention.
            if (kb.mKey.wasPressedThisFrame) IsMaskPaintMode = !IsMaskPaintMode;
            if (kb.rKey.wasPressedThisFrame) Remesh();
        }

        // Holding Shift temporarily switches to the Smooth brush, ZBrush/Blender-style,
        // reverting to whatever brush was active the moment Shift is released - lets you
        // smooth out a stroke without breaking flow to switch brushes and back. Guarded off
        // during the resize gauge for the same reason other input handlers are.
        private void HandleShiftSmoothOverride(Keyboard kb)
        {
            if (_isResizingBrush) return;
            bool shiftHeld = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;

            if (shiftHeld && !_isShiftSmoothActive)
            {
                _preShiftBrush = currentBrush;
                _isShiftSmoothActive = true;
                CurrentBrush = BrushType.Smooth;
            }
            else if (!shiftHeld && _isShiftSmoothActive)
            {
                _isShiftSmoothActive = false;
                CurrentBrush = _preShiftBrush;
            }
        }

        // Holding S enters a resize mode (instead of sculpting) where horizontal mouse
        // movement scrubs BrushRadius live, ZBrush/Blender-style, with the popup gauge
        // SculptUIBuilder draws at ResizeAnchorScreenPosition tracking the value.
        private void HandleBrushResizeKey()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            if (kb == null || mouse == null) return;

            if (kb.sKey.wasPressedThisFrame)
            {
                EndMoveDrag(); // don't leave a grab mid-drag while the resize gauge is up
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

        private void HandleSculptInput()
        {
            var mouse = Mouse.current;
            if (mouse == null || cam == null) return;

            // While the resize gauge is up, mouse movement scrubs brush size, not sculpting.
            // Force _isOverUI false too so UpdateBrushPreview follows the mouse ray (the
            // deliberate resize-gauge UX) rather than freezing at a stale over-UI position.
            if (_isResizingBrush)
            {
                _isHovering = false;
                _isOverUI = false;
                return;
            }

            bool overUI = UnityEngine.EventSystems.EventSystem.current != null &&
                          UnityEngine.EventSystems.EventSystem.current.IsPointerOverGameObject();
            _isOverUI = overUI;
            bool altHeld = Keyboard.current != null && Keyboard.current.leftAltKey.isPressed;

            // Mask painting is its own input mode, not one of the sculpting brushes - doesn't
            // move vertices, so it skips the undo snapshot/spatial-grid-rebuild-on-press below
            // entirely (mask isn't part of undo history - see HandleMaskPaintInput remarks).
            if (_isMaskPaintMode)
            {
                HandleMaskPaintInput(mouse, overUI, altHeld);
                return;
            }

            // Rebuild the vertex spatial index once at the start of every stroke (not every
            // frame - rebuilding is itself O(vertex count), so doing it per-frame would defeat
            // the point) so Clay/Smooth/Crease/Dam Standard/Move's per-stroke vertex lookups
            // don't have to scan the whole mesh. Cell size tracks the current brush radius so
            // the grid stays well-matched to typical query size. Also snapshots undo state
            // here, once per stroke rather than per frame for the same reason - a stroke that
            // turns out to be a click on empty space (missing the mesh) still pushes a
            // snapshot; undoing it is then just a harmless no-op, not worth the extra
            // complexity of pushing from inside every individual brush handler instead.
            if (!overUI && !altHeld && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
            {
                sculptableMesh.RebuildSpatialIndex(Mathf.Max(brushRadius * 0.5f, 0.01f));
                sculptableMesh.SnapshotForUndo();
            }

            switch (currentBrush)
            {
                case BrushType.Move:
                    HandleMoveDrag(mouse, overUI, altHeld);
                    break;
                case BrushType.Smooth:
                    HandleSmoothInput(mouse, overUI, altHeld);
                    break;
                case BrushType.Crease:
                    HandleCreaseInput(mouse, overUI, altHeld);
                    break;
                case BrushType.DamStandard:
                    HandleDamStandardInput(mouse, overUI, altHeld);
                    break;
                case BrushType.Inflate:
                    HandleInflateInput(mouse, overUI, altHeld);
                    break;
                default:
                    HandleClayInput(mouse, overUI, altHeld);
                    break;
            }
        }

        // Left mouse paints mask (protects the area from every brush - see
        // SculptableMesh.Mask/PaintMask), right mouse erases it, same LMB-apply/RMB-invert
        // convention as the sculpting brushes. Deliberately NOT part of undo history - masking
        // doesn't move geometry, and folding it into SculptHistory's vertex/triangle snapshot
        // format would be a larger change than this "just a basic one" ask called for; flagged
        // here rather than silently left out.
        private void HandleMaskPaintInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            bool rightHeld = mouse.rightButton.isPressed;
            _previewPositive = !rightHeld; // green while painting, red while erasing

            if (altHeld) return;
            if (mouse.leftButton.isPressed) ApplyMaskPaint(hitPoint, true);
            else if (rightHeld) ApplyMaskPaint(hitPoint, false);
        }

        private void ApplyMaskPaint(Vector3 worldPoint, bool applying)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            float speed = Mathf.Lerp(MaskPaintSpeedSoft, MaskPaintSpeedHard, maskHardness);
            float amount = (applying ? 1f : -1f) * brushStrength * speed * Time.deltaTime;

            foreach (Vector3 sign in Mirror.GetMirrorSigns())
                sculptableMesh.PaintMask(Vector3.Scale(localPoint, sign), brushRadius, amount, maskHardness);
        }

        private void HandleClayInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            bool rightHeld = mouse.rightButton.isPressed;
            _previewPositive = rightHeld ? !isPositive : isPositive;

            if (logRayHits && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                Debug.Log($"[Sculpt] Ray hit at {hitPoint}, normal {hitNormal}, distance {Vector3.Distance(ray.origin, hitPoint):F2}");

            // Alt+Left-drag is reserved for orbiting the camera (see CameraOrbitController),
            // so don't also sculpt while Alt is held. Right-drag sculpts with the sign
            // inverted, independent of Alt, matching most sculpting apps' invert convention.
            if (mouse.leftButton.isPressed && !altHeld)
                ApplyClayBrush(hitPoint, hitNormal, isPositive);
            else if (rightHeld)
                ApplyClayBrush(hitPoint, hitNormal, !isPositive);
        }

        private void ApplyClayBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            Vector3 localNormal = t.InverseTransformDirection(worldNormal).normalized;

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                Vector3 mirroredPoint = Vector3.Scale(localPoint, sign);
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                ApplyClayBrushLocal(mirroredPoint, mirroredNormal, positive);
            }

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        // Eases each vertex toward a point on the brush's tangent PLANE rather than toward
        // localPoint itself - so the whole footprint rises to a level plateau together, ZBrush
        // ClayBuildup/Blender Clay Strips style, instead of every vertex sagging toward one
        // shared target point. The plane's origin and normal are an area-weighted average of
        // the footprint's OWN current vertex positions/normals (same falloff weights used to
        // apply the brush), not the single raw raycast hit point/normal - a lone raycast hits
        // one triangle's flat face normal, which can differ noticeably from its neighbors on a
        // tessellated/previously-sculpted surface, so a plane built from it alone jitters
        // slightly frame to frame as the stroke crosses different triangles, stacking into a
        // lumpy, stair-stepped buildup instead of a coherent flat plateau. Averaging over the
        // footprint the brush is about to touch makes the plane immune to any single
        // triangle's noise - the same "area plane" approach ZBrush/Blender's own
        // Clay/Flatten-family brushes use. An optional alpha stamp (see BrushAlphaLibrary)
        // multiplies the same per-vertex weight to vary the plateau's surface detail.
        private void ApplyClayBrushLocal(Vector3 localPoint, Vector3 localNormal, bool positive)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            Vector3[] normals = sculptableMesh.Normals;
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float height = brushRadius * clayHeightFactor * sign;

            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            if (_clayWeightScratch.Length < candidates.Count) _clayWeightScratch = new float[candidates.Count];
            float[] weights = _clayWeightScratch;

            Vector3 planeOriginSum = Vector3.zero;
            Vector3 planeNormalSum = Vector3.zero;
            float planeWeightSum = 0f;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(verts[i], localPoint);
                if (dist > brushRadius) { weights[ci] = 0f; continue; }

                float t01 = 1f - dist / brushRadius;
                float w = t01 * t01 * (3f - 2f * t01) * (1f - sculptableMesh.Mask[i]); // smoothstep, masked-out
                weights[ci] = w;

                planeOriginSum += verts[i] * w;
                planeNormalSum += normals[i] * w;
                planeWeightSum += w;
            }

            if (planeWeightSum <= 1e-6f) return;

            Vector3 planeOrigin = planeOriginSum / planeWeightSum;
            Vector3 planeNormal = planeNormalSum.sqrMagnitude > 1e-8f
                ? planeNormalSum.normalized : localNormal;

            BuildTangentBasis(planeNormal, out Vector3 tangent, out Vector3 bitangent);
            float rot = alphaRotation * Mathf.Deg2Rad;
            float cosR = Mathf.Cos(rot), sinR = Mathf.Sin(rot);
            BrushAlphaLibrary.AlphaData alpha = useAlpha ? BrushAlphaLibrary.Get(alphaType) : default;
            float invStampRadius = 1f / Mathf.Max(0.0001f, brushRadius * alphaScale);

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                float weight = weights[ci];
                if (weight <= 0f) continue;
                int i = candidates[ci];

                if (useAlpha)
                {
                    Vector3 toVert = verts[i] - localPoint;
                    float u = Vector3.Dot(toVert, tangent) * invStampRadius;
                    float v = Vector3.Dot(toVert, bitangent) * invStampRadius;
                    float ru = u * cosR - v * sinR;
                    float rv = u * sinR + v * cosR;
                    if (ru < -1f || ru > 1f || rv < -1f || rv > 1f)
                    {
                        continue;
                    }

                    float a = BrushAlphaLibrary.Sample(alpha, ru * 0.5f + 0.5f, rv * 0.5f + 0.5f);
                    weight *= invertAlpha ? 1f - a : a;
                    if (weight <= 0f) continue;
                }

                Vector3 toPlane = verts[i] - planeOrigin;
                float alongNormal = Vector3.Dot(toPlane, planeNormal);
                Vector3 tangentialOffset = toPlane - planeNormal * alongNormal;
                Vector3 target = planeOrigin + tangentialOffset + planeNormal * height;

                Vector3 toTarget = target - verts[i];
                // Clamp01: this is a lerp fraction toward target, not a velocity - on a frame
                // hitch (large dt, e.g. during a heavy Remesh) an unclamped factor can exceed
                // 1 and overshoot past the target plane. Since Clay's target recomputes from
                // the vertex's own (now overshot) position next frame, an uncapped factor
                // compounds into a runaway explosion rather than settling - reproduced this
                // empirically while testing this brush (a synthetic large-dt stroke sent a
                // vertex from radius 0.5 to over 3.0 in 90 frames before this clamp existed).
                verts[i] += toTarget * Mathf.Clamp01(weight * brushStrength * ClaySpeed * dt);
                _dirtyVertexScratch.Add(i);
            }
        }

        private static void BuildTangentBasis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent)
        {
            Vector3 up = Mathf.Abs(Vector3.Dot(normal, Vector3.up)) > 0.99f ? Vector3.forward : Vector3.up;
            tangent = Vector3.Cross(up, normal).normalized;
            bitangent = Vector3.Cross(normal, tangent);
        }

        private void HandleCreaseInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            bool rightHeld = mouse.rightButton.isPressed;
            _previewPositive = rightHeld ? !isPositive : isPositive;

            if (logRayHits && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                Debug.Log($"[Sculpt] Ray hit at {hitPoint}, normal {hitNormal}, distance {Vector3.Distance(ray.origin, hitPoint):F2}");

            if (mouse.leftButton.isPressed && !altHeld)
                ApplyCreaseBrush(hitPoint, hitNormal, isPositive);
            else if (rightHeld)
                ApplyCreaseBrush(hitPoint, hitNormal, !isPositive);
        }

        private void ApplyCreaseBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            Vector3 localNormal = t.InverseTransformDirection(worldNormal).normalized;

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                Vector3 mirroredPoint = Vector3.Scale(localPoint, sign);
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                ApplyCreaseBrushLocal(mirroredPoint, mirroredNormal, positive);
            }

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        // Pinches the tangential footprint toward the stroke centerline while carving along
        // the normal with a depth that's scaled by the same per-vertex weight (unlike Clay's
        // constant plateau height), so the profile tapers to a sharp ridge/valley along the
        // stroke instead of a flat-topped dome.
        private void ApplyCreaseBrushLocal(Vector3 localPoint, Vector3 localNormal, bool positive)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float depth = brushRadius * creaseDepthFactor * sign;

            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 toVert = verts[i] - localPoint;
                float dist = toVert.magnitude;
                if (dist > brushRadius) continue;

                float t01 = 1f - dist / brushRadius;
                float weight = t01 * t01 * t01 * (1f - sculptableMesh.Mask[i]); // sharper falloff than Clay's smoothstep - a narrower peak

                float alongNormal = Vector3.Dot(toVert, localNormal);
                Vector3 tangentialOffset = toVert - localNormal * alongNormal;
                Vector3 pinched = tangentialOffset * (1f - creasePinch * weight);
                Vector3 target = localPoint + pinched + localNormal * (depth * weight);

                Vector3 toTarget = target - verts[i];
                verts[i] += toTarget * Mathf.Clamp01(weight * brushStrength * CreaseSpeed * dt); // see Clamp01 note on Clay
                _dirtyVertexScratch.Add(i);
            }
        }

        private void HandleDamStandardInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) { _lastDamHoverLocal = null; return; }

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) { _lastDamHoverLocal = null; return; }

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            bool rightHeld = mouse.rightButton.isPressed;
            _previewPositive = rightHeld ? !isPositive : isPositive;

            if (logRayHits && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                Debug.Log($"[Sculpt] Ray hit at {hitPoint}, normal {hitNormal}, distance {Vector3.Distance(ray.origin, hitPoint):F2}");

            bool sculpting = (mouse.leftButton.isPressed && !altHeld) || rightHeld;
            if (!sculpting) { _lastDamHoverLocal = null; return; }

            ApplyDamStandardBrush(hitPoint, hitNormal, rightHeld ? !isPositive : isPositive);
        }

        private void ApplyDamStandardBrush(Vector3 worldPoint, Vector3 worldNormal, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);
            Vector3 localNormal = t.InverseTransformDirection(worldNormal).normalized;

            // Stroke-travel direction in the tangent plane, used to bias a raised lip onto the
            // leading edge and leave a groove on the trailing edge - the asymmetry that
            // distinguishes Dam Standard from a plain symmetric Crease. No reliable direction
            // exists yet on the stroke's first sample or a stationary dab, so this falls back
            // to symmetric Crease-like carving in that case - an honest simplification rather
            // than full directional Dam Standard behavior.
            Vector3 dirLocal = Vector3.zero;
            if (_lastDamHoverLocal.HasValue)
            {
                Vector3 raw = localPoint - _lastDamHoverLocal.Value;
                Vector3 tangential = raw - localNormal * Vector3.Dot(raw, localNormal);
                if (tangential.sqrMagnitude > 1e-8f) dirLocal = tangential.normalized;
            }
            _lastDamHoverLocal = localPoint;

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                Vector3 mirroredPoint = Vector3.Scale(localPoint, sign);
                Vector3 mirroredNormal = Vector3.Scale(localNormal, sign).normalized;
                Vector3 mirroredDir = Vector3.Scale(dirLocal, sign);
                ApplyDamStandardBrushLocal(mirroredPoint, mirroredNormal, mirroredDir, positive);
            }

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        private void ApplyDamStandardBrushLocal(Vector3 localPoint, Vector3 localNormal, Vector3 dirLocal, bool positive)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;
            float depth = brushRadius * creaseDepthFactor * sign;
            float lip = brushRadius * damLipHeight * sign;
            bool hasDir = dirLocal.sqrMagnitude > 1e-6f;

            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                Vector3 toVert = verts[i] - localPoint;
                float dist = toVert.magnitude;
                if (dist > brushRadius) continue;

                float t01 = 1f - dist / brushRadius;
                float weight = t01 * t01 * t01 * (1f - sculptableMesh.Mask[i]);

                float alongNormal = Vector3.Dot(toVert, localNormal);
                Vector3 tangentialOffset = toVert - localNormal * alongNormal;
                Vector3 pinched = tangentialOffset * (1f - creasePinch * weight);

                float normalOffset = depth * weight;
                if (hasDir && Vector3.Dot(tangentialOffset, dirLocal) > 0f)
                    normalOffset += lip * weight;

                Vector3 target = localPoint + pinched + localNormal * normalOffset;
                Vector3 toTarget = target - verts[i];
                verts[i] += toTarget * Mathf.Clamp01(weight * brushStrength * CreaseSpeed * dt); // see Clamp01 note on Clay
                _dirtyVertexScratch.Add(i);
            }
        }

        private void HandleInflateInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;

            bool rightHeld = mouse.rightButton.isPressed;
            _previewPositive = rightHeld ? !isPositive : isPositive;

            if (logRayHits && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                Debug.Log($"[Sculpt] Ray hit at {hitPoint}, normal {hitNormal}, distance {Vector3.Distance(ray.origin, hitPoint):F2}");

            if (mouse.leftButton.isPressed && !altHeld)
                ApplyInflateBrush(hitPoint, isPositive);
            else if (rightHeld)
                ApplyInflateBrush(hitPoint, !isPositive);
        }

        private void ApplyInflateBrush(Vector3 worldPoint, bool positive)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
                ApplyInflateBrushLocal(Vector3.Scale(localPoint, sign), positive);

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        // Pushes each vertex outward along its OWN normal (the mesh's per-vertex normals,
        // not the single raycast hit normal or an averaged plane like Clay) so corners round
        // off and the whole footprint puffs up like a balloon - the ZBrush Inflate / Blender
        // Inflate-Deflate feel, distinct from Clay's flat plateau or Crease's pinch-to-ridge.
        // A constant per-frame push along a fixed direction rather than a lerp toward a
        // target, so unlike Clay/Crease/Smooth this doesn't need the Clamp01 overshoot guard
        // - there's no target position for a large dt to overshoot past.
        private void ApplyInflateBrushLocal(Vector3 localPoint, bool positive)
        {
            Vector3[] verts = sculptableMesh.Vertices;
            Vector3[] normals = sculptableMesh.Normals;
            float sign = positive ? 1f : -1f;
            float dt = Time.deltaTime;

            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(verts[i], localPoint);
                if (dist > brushRadius) continue;

                float t01 = 1f - dist / brushRadius;
                float weight = t01 * t01 * (3f - 2f * t01) * (1f - sculptableMesh.Mask[i]); // smoothstep, masked-out
                if (weight <= 0f) continue;

                verts[i] += normals[i] * (weight * sign * brushStrength * InflateSpeed * dt);
                _dirtyVertexScratch.Add(i);
            }
        }

        private void HandleSmoothInput(Mouse mouse, bool overUI, bool altHeld)
        {
            _isHovering = false;
            if (overUI) return;

            Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(ray, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);

            _isHovering = hasHit;
            if (!_isHovering) return;

            _hoverPoint = hitPoint;
            _hoverNormal = hitNormal;
            _previewPositive = true; // Smooth has no add/subtract direction - always neutral/green

            if (logRayHits && (mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame))
                Debug.Log($"[Sculpt] Ray hit at {hitPoint}, normal {hitNormal}, distance {Vector3.Distance(ray.origin, hitPoint):F2}");

            // Same Alt-reserved-for-orbit rule as Clay; either mouse button smooths since
            // there's no positive/negative to invert.
            if ((mouse.leftButton.isPressed && !altHeld) || mouse.rightButton.isPressed)
                ApplySmoothBrush(hitPoint);
        }

        private void ApplySmoothBrush(Vector3 worldPoint)
        {
            Transform t = sculptableMesh.transform;
            Vector3 localPoint = t.InverseTransformPoint(worldPoint);

            _dirtyVertexScratch.Clear();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
                ApplySmoothBrushLocal(Vector3.Scale(localPoint, sign));

            sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
        }

        private void ApplySmoothBrushLocal(Vector3 localPoint)
        {
            Vector3[] verts = sculptableMesh.Vertices;

            List<int> candidates = sculptableMesh.QueryNear(localPoint, brushRadius);
            if (candidates.Count == 0) return;

            if (_smoothWeightScratch.Length < candidates.Count) _smoothWeightScratch = new float[candidates.Count];
            float[] weights = _smoothWeightScratch;
            bool anyInRange = false;

            for (int ci = 0; ci < candidates.Count; ci++)
            {
                int i = candidates[ci];
                float dist = Vector3.Distance(verts[i], localPoint);
                if (dist > brushRadius) { weights[ci] = 0f; continue; }

                float t01 = 1f - dist / brushRadius;
                weights[ci] = t01 * t01 * (3f - 2f * t01) * (1f - sculptableMesh.Mask[i]); // smoothstep, masked-out
                anyInRange = true;
            }
            if (!anyInRange) return;

            float dt = Time.deltaTime;
            float iterAmount = brushStrength * MaxSmoothIterations;
            int fullIterations = Mathf.FloorToInt(iterAmount);
            float partialFactor = iterAmount - fullIterations;

            for (int pass = 0; pass < fullIterations; pass++)
                RunSmoothRelaxationPass(verts, candidates, weights, 1f, dt);
            if (partialFactor > 0.001f)
                RunSmoothRelaxationPass(verts, candidates, weights, partialFactor, dt);
        }

        private void RunSmoothRelaxationPass(Vector3[] verts, List<int> candidates, float[] weights, float passFactor, float dt)
        {
            for (int ci = 0; ci < candidates.Count; ci++)
            {
                float w = weights[ci];
                if (w <= 0f) continue;
                int i = candidates[ci];

                Vector3 toAverage = sculptableMesh.GetNeighborAverage(i) - verts[i];
                verts[i] += toAverage * Mathf.Clamp01(w * passFactor * SmoothSpeed * dt); // see Clamp01 note on Clay
                _dirtyVertexScratch.Add(i);
            }
        }

        // Grabs whatever's under the cursor on mouse-down and drags it with the cursor's
        // world-space movement along a camera-facing plane through the grab point, instead of
        // re-raycasting the mesh every frame. That's what makes it keep tracking once the
        // cursor moves past the mesh's silhouette, and gives 1:1 "pull" instead of a slow
        // per-frame nudge along a fixed normal.
        private void HandleMoveDrag(Mouse mouse, bool overUI, bool altHeld)
        {
            if (_isMoveDragging)
            {
                if (!mouse.leftButton.isPressed)
                {
                    EndMoveDrag();
                    return;
                }

                Ray dragRay = cam.ScreenPointToRay(mouse.position.ReadValue());
                if (RayPlaneIntersect(dragRay, _dragPlanePoint, _dragPlaneNormal, out Vector3 current))
                {
                    Vector3 worldDelta = current - _lastDragPoint;
                    if (worldDelta.sqrMagnitude > 1e-12f)
                    {
                        Vector3 localDelta = sculptableMesh.transform.InverseTransformVector(worldDelta);
                        _dirtyVertexScratch.Clear();
                        foreach (var (selection, sign) in _grabSelections)
                        {
                            sculptableMesh.ApplyGrabDelta(selection, Vector3.Scale(localDelta, sign));
                            foreach (int i in selection.Indices) _dirtyVertexScratch.Add(i);
                        }
                        sculptableMesh.ApplyVerticesLocal(_dirtyVertexScratch);
                    }
                    _lastDragPoint = current;
                }

                _isHovering = true;
                _hoverPoint = _lastDragPoint;
                _previewPositive = true;
                return;
            }

            // Not dragging: only start one on a fresh click while actually hovering the mesh.
            _isHovering = false;
            if (overUI || altHeld) return;

            Ray hoverRay = cam.ScreenPointToRay(mouse.position.ReadValue());
            bool hasHit = sculptableMesh.RaycastMesh(hoverRay, 1000f, out Vector3 hitPoint, out Vector3 hitNormal);
            _isHovering = hasHit;
            if (_isHovering)
            {
                _hoverPoint = hitPoint;
                _hoverNormal = hitNormal;
                _previewPositive = true;
            }

            if (!_isHovering || !mouse.leftButton.wasPressedThisFrame) return;

            Vector3 localHit = sculptableMesh.transform.InverseTransformPoint(hitPoint);
            var selections = new List<(SculptableMesh.GrabSelection, Vector3)>();
            foreach (Vector3 sign in Mirror.GetMirrorSigns())
            {
                var selection = sculptableMesh.SelectGrab(Vector3.Scale(localHit, sign), brushRadius);
                if (selection.IsValid) selections.Add((selection, sign));
            }
            if (selections.Count == 0) return;
            _grabSelections = selections;

            _isMoveDragging = true;
            _dragPlanePoint = hitPoint;
            _dragPlaneNormal = -cam.transform.forward;
            _lastDragPoint = hitPoint;

            if (logRayHits) Debug.Log($"[Sculpt] Move grab started at {hitPoint}");
        }

        private void EndMoveDrag()
        {
            if (!_isMoveDragging) return;
            _grabSelections = null;
            _isMoveDragging = false;
        }

        private static bool RayPlaneIntersect(Ray ray, Vector3 planePoint, Vector3 planeNormal, out Vector3 point)
        {
            float denom = Vector3.Dot(ray.direction, planeNormal);
            if (Mathf.Abs(denom) < 1e-6f) { point = default; return false; }

            float dist = Vector3.Dot(planePoint - ray.origin, planeNormal) / denom;
            if (dist < 0f) { point = default; return false; }

            point = ray.origin + ray.direction * dist;
            return true;
        }

        public void ResetMesh()
        {
            EndMoveDrag();
            sculptableMesh.SnapshotForUndo();
            sculptableMesh.ResetMesh();
        }

        public void Remesh()
        {
            sculptableMesh.SnapshotForUndo();
            sculptableMesh.Remesh(remeshResolution);
        }

        // Fixed destination rather than a save-file dialog - EditorUtility.SaveFilePanel only
        // exists in the Editor and would silently vanish once this ships as a standalone
        // build, whereas Environment.GetFolderPath is plain .NET and resolves the real
        // Desktop path in both. A proper save/load feature (with its own file-picker UX) is
        // planned as separate future work; this is just "get the current sculpt out to a
        // file I can open elsewhere" for now.
        public string Export()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string folder = Path.Combine(desktop, "SculptExports");
            string path = ObjExporter.Export(sculptableMesh, folder);
            if (path != null) Debug.Log($"[Sculpt] Exported to {path}");
            return path;
        }

        // Kept visible at all times (not just while hovering the mesh) so its size always
        // gives a visual read on the current brush radius - most useful while resizing (S)
        // off to the side of the model. Snaps to the sculpted surface when actually hovering
        // it; otherwise floats along the camera ray at the model's rough depth.
        private void UpdateBrushPreview()
        {
            if (brushPreview == null || cam == null) return;

            Vector3 previewPos;
            bool positive;

            if (_isHovering)
            {
                previewPos = _hoverPoint + _hoverNormal * 0.01f;
                positive = _previewPositive;
                _lastGoodPreviewPos = previewPos;
            }
            else if (_isOverUI)
            {
                // Mouse is over a panel (e.g. dragging the brush radius slider), not the
                // viewport - a fresh ray from there would send the preview flying off toward
                // the panel. Freeze at the last on-model/viewport position instead so its size
                // still reads clearly against the sculpt while the slider is being scrubbed.
                previewPos = _lastGoodPreviewPos;
                positive = true;
            }
            else
            {
                Mouse mouse = Mouse.current;
                if (mouse == null) { brushPreview.SetActive(false); return; }

                float fallbackDistance = Mathf.Max(1f, Vector3.Distance(cam.transform.position, sculptableMesh.transform.position));
                Ray ray = cam.ScreenPointToRay(mouse.position.ReadValue());
                previewPos = ray.GetPoint(fallbackDistance);
                positive = true; // neutral tint when just showing size, not actively sculpting
                _lastGoodPreviewPos = previewPos;
            }

            brushPreview.SetActive(true);
            float diameter = brushRadius * 2f * AverageScale();
            brushPreview.transform.position = previewPos;
            brushPreview.transform.localScale = Vector3.one * diameter;

            if (_brushPreviewRenderer != null)
            {
                Color c = positive ? PositiveColor : NegativeColor;
                c.a = 0.35f;
                _brushPreviewRenderer.material.color = c;
            }
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
