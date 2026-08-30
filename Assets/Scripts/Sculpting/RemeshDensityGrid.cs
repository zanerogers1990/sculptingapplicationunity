using UnityEngine;

namespace Sculpting
{
    /// Camera-facing grid that previews RemeshResolution while the R-hold gauge
    /// (SculptController.HandleRemeshDensityKey) is armed - the sculpting equivalent of
    /// ZBrush's dynamic-subdivision density readout. Billboards in front of the selected
    /// object at a fixed footprint, spaced at the SAME cell size MeshRemesher would actually
    /// use, so what's on screen is a real preview of the next Remesh(), not a decorative
    /// stand-in - only the LINE SPACING changes with density, never the covered area, so the
    /// gauge reads as "this many cells across the object" at every density instead of the
    /// patch of grid itself growing or shrinking.
    ///
    /// Self-installing and self-driving, same idiom as RegionSelectTool: SculptController adds
    /// this component the first time DensityGrid is asked for (no scene wiring reaches it
    /// through Unity MCP - see [[feedback_unity_mcp_object_refs]]), and this class then reads
    /// the controller/selection itself every frame rather than being pushed state from outside.
    public class RemeshDensityGrid : MonoBehaviour
    {
        // How far past the object's own footprint the grid extends, so the silhouette doesn't
        // butt right up against the grid's edge. Matches MirrorController's PlanePadding - same
        // "visibly bigger than the thing it's describing" feel. Fixed to the object's size
        // alone, NOT to the cell size, so the covered area stays constant as density changes -
        // only the line spacing (and therefore line count) moves.
        private const float FootprintPadding = 1.4f;
        // Defensive ceiling on rendered line count per axis - guards against a pathological
        // cellSize without ever engaging at any resolution RemeshResolution can actually reach
        // (4-500 tops out around 700 divisions at this padding), so the footprint above stays
        // genuinely constant in practice rather than quietly shrinking past some resolution.
        private const int MaxDivisions = 2000;
        private const int MinDivisions = 2;

        // Exponential smoothing rates (per second) for spacing and fade - see Update. Spacing
        // is slower than fade so a big density jump visibly grows/shrinks into place instead of
        // snapping the instant the mouse moves, which is the "never pops" ask; fade is quick
        // enough that arming/releasing R still feels responsive.
        private const float CellSizeSmoothing = 12f;
        private const float AlphaSmoothing = 10f;

        private static readonly Color GridColor = new Color(0.4f, 0.85f, 1f);

        private SculptController _controller;
        private SculptController Controller =>
            _controller != null ? _controller : (_controller = FindFirstObjectByType<SculptController>());

        private SelectionManager _selection;
        private SelectionManager Selection =>
            _selection != null ? _selection : (_selection = FindFirstObjectByType<SelectionManager>());

        private MeshFilter _meshFilter;
        private MeshRenderer _meshRenderer;
        private Mesh _mesh;
        private Material _material;

        // Reused across rebuilds instead of allocated fresh every frame the gauge is held -
        // divisions can run into the hundreds at high density, and this mesh is rebuilt most
        // frames of a drag (see Update). Grown on demand, same idiom as SculptController's own
        // per-brush scratch buffers.
        private Vector3[] _vertScratch = System.Array.Empty<Vector3>();
        private int[] _indexScratch = System.Array.Empty<int>();

        private float _displayedCellSize;
        private float _alpha;
        // True once _displayedCellSize has ever been assigned - lets the FIRST time the grid
        // appears snap straight to the target spacing (faded in via alpha instead) rather than
        // visibly growing from whatever size a fresh 0 would smooth from.
        private bool _initialized;
        // Half the grid's on-screen extent along its own local up axis, from the most recent
        // RebuildMesh - reused to anchor the label under the grid's bottom edge without
        // duplicating the footprint math there.
        private float _halfExtent;

        /// True once the fade has actually reached (near enough) visible - SculptUIBuilder gates
        /// the density label on this rather than on SculptController.ShowRemeshDensityGrid
        /// directly, so the label fades in step with the grid instead of a frame ahead of it.
        public bool IsVisible => _alpha > 0.02f;

        /// Where SculptUIBuilder should anchor the density label, in screen pixels - projected
        /// from the grid's bottom edge (its own local "down", after billboarding to face the
        /// camera). Only meaningful while IsVisible.
        public Vector2 LabelScreenPosition { get; private set; }

        private void Awake()
        {
            var go = new GameObject("DensityGridMesh");
            go.transform.SetParent(transform, false);

            _meshFilter = go.AddComponent<MeshFilter>();
            _meshRenderer = go.AddComponent<MeshRenderer>();
            _mesh = new Mesh { name = "RemeshDensityGrid" };
            _meshFilter.sharedMesh = _mesh;

            // Same shader MirrorController's planes use - already proven to render correctly
            // (transparent, unlit) under this project's render pipeline.
            _material = new Material(Shader.Find("Sprites/Default"));
            _meshRenderer.sharedMaterial = _material;
            _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _meshRenderer.receiveShadows = false;

            go.SetActive(false);
        }

        private void Update()
        {
            SculptController controller = Controller;
            SculptableMesh target = Selection != null ? Selection.PrimarySelection : null;
            Renderer targetRenderer = target != null ? target.GetComponent<Renderer>() : null;
            Camera cam = controller != null ? controller.ActiveCamera : null;

            bool wantVisible = controller != null && controller.ShowRemeshDensityGrid
                                && targetRenderer != null && cam != null;

            float targetAlpha = wantVisible ? 1f : 0f;
            _alpha = Mathf.MoveTowards(_alpha, targetAlpha, AlphaSmoothing * Time.unscaledDeltaTime);

            if (_alpha <= 0.001f)
            {
                if (_meshFilter.gameObject.activeSelf) _meshFilter.gameObject.SetActive(false);
                _initialized = false; // next appearance snaps spacing again, see field remarks
                return;
            }

            if (!_meshFilter.gameObject.activeSelf) _meshFilter.gameObject.SetActive(true);

            Bounds worldBounds = targetRenderer.bounds;
            float maxExtent = Mathf.Max(worldBounds.size.x, worldBounds.size.y, worldBounds.size.z, 0.0001f);
            int resolution = controller.RemeshResolution;
            float targetCellSize = maxExtent / resolution;

            _displayedCellSize = _initialized
                ? Mathf.Lerp(_displayedCellSize, targetCellSize, 1f - Mathf.Exp(-CellSizeSmoothing * Time.unscaledDeltaTime))
                : targetCellSize;
            _initialized = true;

            RebuildMesh(maxExtent, _displayedCellSize);

            // Billboard: centered on the object, facing straight back at the camera - "up" comes
            // from the camera too, so the grid stays upright (not spirit-level-upright) as the
            // camera orbits, the same convention a HUD readout would use.
            Transform t = _meshFilter.transform;
            t.position = worldBounds.center;
            t.rotation = Quaternion.LookRotation(cam.transform.forward, cam.transform.up);

            Color c = GridColor;
            c.a = _alpha * 0.8f;
            _material.color = c;

            Vector3 labelWorld = t.position - t.up * _halfExtent;
            Vector3 screen = cam.WorldToScreenPoint(labelWorld);
            LabelScreenPosition = screen.z > 0f ? new Vector2(screen.x, screen.y) : LabelScreenPosition;
        }

        private void RebuildMesh(float maxExtent, float cellSize)
        {
            // Fixed footprint (object size only) rather than footprint = divisions * cellSize -
            // that would make the covered area shrink as density rises to keep the line count
            // down, which is exactly the "grid shrinks when density changes" behavior this is
            // meant to avoid. Only the division COUNT scales with density now; the area doesn't.
            float footprint = maxExtent * FootprintPadding;
            int divisions = Mathf.Clamp(Mathf.RoundToInt(footprint / cellSize), MinDivisions, MaxDivisions);
            _halfExtent = divisions * cellSize * 0.5f;
            float half = _halfExtent;

            int lineCount = (divisions + 1) * 2;
            int vertCount = lineCount * 2;
            if (_vertScratch.Length < vertCount)
            {
                _vertScratch = new Vector3[vertCount];
                _indexScratch = new int[vertCount];
                for (int i = 0; i < vertCount; i++) _indexScratch[i] = i; // identity, never changes
            }

            // Built in the mesh's own local XY plane (Z=0) - Update orients this transform so
            // local X/Y line up with the camera's right/up, which is what makes the grid face
            // the viewer rather than lying flat under the object.
            int v = 0;
            for (int i = 0; i <= divisions; i++)
            {
                float offset = -half + i * cellSize;

                // Line running along Y at this X.
                _vertScratch[v] = new Vector3(offset, -half, 0f);
                _vertScratch[v + 1] = new Vector3(offset, half, 0f);
                v += 2;

                // Line running along X at this Y.
                _vertScratch[v] = new Vector3(-half, offset, 0f);
                _vertScratch[v + 1] = new Vector3(half, offset, 0f);
                v += 2;
            }

            // Explicit (start, length) overloads rather than assigning the arrays wholesale -
            // the scratch arrays are sized to the LARGEST divisions seen so far and only ever
            // grow, so a rebuild at a lower division count still writes/draws exactly vertCount
            // entries instead of trailing off into stale data from a denser previous rebuild.
            _mesh.Clear();
            _mesh.SetVertices(_vertScratch, 0, vertCount);
            _mesh.SetIndices(_indexScratch, 0, vertCount, MeshTopology.Lines, 0);
            // Generous bounds rather than an exact recalculation - the mesh is rebuilt most
            // frames while the gauge is up, and RecalculateBounds walking every vertex for a
            // flat grid that never moves off local Z=0 buys nothing a fixed box doesn't already
            // cover.
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(half * 2f, half * 2f, 0.01f));
        }
    }
}
