using UnityEngine;

namespace Sculpting
{
    /// Drives the mask-extract workflow: hold a live translucent preview of what
    /// MeshExtractor would produce, let every setting be re-dialled against it, then either
    /// commit it as a real sculptable object or throw it away.
    ///
    /// The preview is the whole point. Extraction has seven interacting knobs and produces a
    /// brand new object, so committing blind means the only way to evaluate a guess is to
    /// create an object, look at it, delete it, and guess again. A preview collapses that to
    /// dragging a slider. It also stands in for undo on a operation that has nothing to undo -
    /// extract never modifies the source, so "cancel" before committing, plus the object list's
    /// own delete afterwards, covers every way of changing your mind.
    ///
    /// Lives on SceneSystems alongside the other controllers, found by SculptUIBuilder via
    /// FindFirstObjectByType, matching how every other controller in this project is reached.
    public class MaskExtractController : MonoBehaviour
    {
        // How often a live preview may rebuild while the mask is actively changing under a
        // held paint stroke. Extraction is O(source vertices + triangles) - a few milliseconds
        // on a normal sculpt, but tens on a multi-hundred-thousand-vertex import, which at every
        // frame of a stroke would make mask painting itself feel broken. ~12Hz keeps the preview
        // feeling live while leaving the brush the rest of the frame budget.
        private const float MaskRebuildInterval = 0.08f;

        private static readonly Color PreviewColor = new Color(0.30f, 0.75f, 1f, 0.45f);
        private static readonly Color PreviewRimColor = new Color(0.75f, 0.95f, 1f, 1f);

        private ExtractSettings _settings = ExtractSettings.Default;

        private SelectionManager _selection;
        private SelectionManager Selection =>
            _selection != null ? _selection : (_selection = FindFirstObjectByType<SelectionManager>());

        // The object the CURRENT preview was built from - not simply "whatever is selected now".
        // Selecting something else mid-preview has to invalidate it (the shell is expressed in
        // the old object's local space and describes its mask), and comparing against this is
        // how Update notices.
        private SculptableMesh _previewSource;
        private GameObject _previewGO;
        private Mesh _previewMesh;
        private Material _previewMaterial;

        private int _lastSeenMaskVersion = -1;
        private int _lastSeenSelectionVersion = -1;
        private float _nextAllowedMaskRebuild;

        /// Last extraction's outcome, for the UI's status line. Non-null `Error` means the
        /// preview is empty and Accept would do nothing.
        public string Error { get; private set; }
        public int PreviewTriangleCount { get; private set; }

        /// Whether a preview SESSION is open - deliberately keyed on the source rather than on
        /// the preview object existing. A session can legitimately have no geometry in it right
        /// now (the user erased the whole mask while it was open), and reporting that as "not
        /// previewing" would grey out the Cancel button that's the only way to close the
        /// session, leaving it silently armed to re-appear the next time any mask was painted.
        public bool IsPreviewing => _previewSource != null;

        /// Whether there is actually a shell to commit. Separate from IsPreviewing so the UI can
        /// keep Cancel live while greying out only Accept - see IsPreviewing.
        public bool HasPreviewGeometry => _previewGO != null && _previewMesh != null;

        // ------------------------------------------------------------------------- settings
        // Each setter rebuilds the live preview so a dragged slider shows its effect
        // immediately. Setting one while no preview is up just stores the value.

        public float ThicknessFraction
        {
            get => _settings.ThicknessFraction;
            set { _settings.ThicknessFraction = Mathf.Clamp(value, 0.002f, 0.5f); RefreshPreview(); }
        }

        public float OffsetFraction
        {
            get => _settings.OffsetFraction;
            set { _settings.OffsetFraction = Mathf.Clamp(value, -0.25f, 0.25f); RefreshPreview(); }
        }

        public float MaskThreshold
        {
            get => _settings.MaskThreshold;
            set { _settings.MaskThreshold = Mathf.Clamp01(value); RefreshPreview(); }
        }

        public int BorderSmoothing
        {
            get => _settings.BorderSmoothing;
            set { _settings.BorderSmoothing = Mathf.Clamp(value, 0, 20); RefreshPreview(); }
        }

        public int SurfaceSmoothing
        {
            get => _settings.SurfaceSmoothing;
            set { _settings.SurfaceSmoothing = Mathf.Clamp(value, 0, 20); RefreshPreview(); }
        }

        public float FalloffAmount
        {
            get => _settings.FalloffAmount;
            set { _settings.FalloffAmount = Mathf.Clamp01(value); RefreshPreview(); }
        }

        public float Shrinkwrap
        {
            get => _settings.Shrinkwrap;
            set { _settings.Shrinkwrap = Mathf.Clamp01(value); RefreshPreview(); }
        }

        public bool InvertRegion
        {
            get => _settings.InvertRegion;
            set { _settings.InvertRegion = value; RefreshPreview(); }
        }

        // ---------------------------------------------------------------------------- flow

        /// Opens (or re-opens) the preview against the current selection. Safe to call while
        /// already previewing - it just rebuilds, which is what the UI's Preview button does
        /// when the user wants to re-aim it at a different object.
        public void BeginPreview()
        {
            SculptableMesh target = Selection != null ? Selection.PrimarySelection : null;
            if (target == null)
            {
                Error = "No object selected.";
                return;
            }

            _previewSource = target;
            _lastSeenMaskVersion = target.MaskVersion;
            _lastSeenSelectionVersion = Selection.SelectionVersion;
            Rebuild();
        }

        /// Turns the previewed shell into a real, independent sculptable object - the same
        /// "bake the geometry into a brand new object" contract MeshCloner and MeshMirror
        /// already established, so the result is immediately sculptable, maskable, joinable and
        /// savable with no special-casing anywhere downstream.
        ///
        /// The extracted object is SELECTED on commit, for two reasons: it's almost always what
        /// you want to work on next, and it makes the operation reversible without a dedicated
        /// undo - the object list's delete button is already pointed at the thing that was just
        /// created. (Extract never touches the source, so there is nothing else to revert.)
        public SculptableMesh Accept()
        {
            if (_previewGO == null || _previewMesh == null || _previewSource == null) return null;

            Transform srcT = _previewSource.transform;

            // A fresh copy rather than handing over _previewMesh itself: EndPreview destroys the
            // preview's mesh, and a committed object quietly sharing that instance would have
            // its geometry deleted out from under it the moment the preview closed.
            var mesh = new Mesh { name = _previewSource.name + " Extract (Source)" };
            if (_previewMesh.vertexCount > 65000)
                mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.vertices = _previewMesh.vertices;
            mesh.triangles = _previewMesh.triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var go = new GameObject(ObjectNaming.Unique(_previewSource.name + " Extract"),
                                    typeof(MeshFilter), typeof(MeshRenderer));
            // Same transform as the source, because MeshExtractor works entirely in the source's
            // local space - so the shell lands exactly where the preview showed it.
            go.transform.SetPositionAndRotation(srcT.position, srcT.rotation);
            go.transform.localScale = srcT.localScale;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;

            // AddComponent runs SculptableMesh.Awake synchronously, so the object is fully built
            // (working buffers, adjacency, blank mask) by the time this returns - the same
            // guarantee PrimitiveSpawner/MeshMirror/MeshCloner all rely on.
            SculptableMesh extracted = SculptableMesh.AddOwning(go, mesh);
            go.AddComponent<MirrorController>();

            FindFirstObjectByType<SculptMaterialController>()?.ApplyTo(go.GetComponent<Renderer>());

            EndPreview();
            Selection?.Select(extracted, false);
            return extracted;
        }

        /// Discards the preview, changing nothing. The source was never modified, so this is a
        /// true no-op rather than a rollback.
        public void Cancel() => EndPreview();

        private void EndPreview()
        {
            if (_previewGO != null)
            {
                // Deactivated before Destroy, deliberately - the same pattern (and the same
                // reason) as UIFactory.ShowModal's Dismiss. Destroy is deferred to the end of
                // the frame, so between Accept being clicked and the object actually going away
                // the translucent preview is still being rendered, sitting exactly on top of the
                // real object that was just committed in its place. SetActive(false) makes it
                // vanish on the click, with Destroy following to free it.
                _previewGO.SetActive(false);
                Destroy(_previewGO);
            }
            if (_previewMesh != null) Destroy(_previewMesh);
            if (_previewMaterial != null) Destroy(_previewMaterial);
            _previewGO = null;
            _previewMesh = null;
            _previewMaterial = null;
            _previewSource = null;
            PreviewTriangleCount = 0;
        }

        /// Rebuilds the preview if one is open; does nothing otherwise. Every settings setter
        /// funnels through here, so a slider dragged with no preview up costs nothing.
        public void RefreshPreview()
        {
            if (_previewSource == null) return;
            Rebuild();
        }

        private void Update()
        {
            if (_previewSource == null) return;

            // The source can be deleted (or the whole scene replaced by a load) while a preview
            // is open - the shell would then be describing geometry that no longer exists.
            if (_previewSource.Equals(null))
            {
                EndPreview();
                return;
            }

            // Selecting a different object abandons the preview rather than silently re-aiming
            // it: the settings were tuned against one object's size and mask, and quietly
            // re-running them somewhere else would produce a result nobody asked for.
            if (Selection != null && Selection.SelectionVersion != _lastSeenSelectionVersion)
            {
                _lastSeenSelectionVersion = Selection.SelectionVersion;
                if (Selection.PrimarySelection != _previewSource)
                {
                    EndPreview();
                    return;
                }
            }

            // Follow the brush: painting more mask (or erasing some) with the preview open
            // reshapes the extract live, which is the fastest way to dial in a border. Throttled
            // - see MaskRebuildInterval.
            if (_previewSource.MaskVersion != _lastSeenMaskVersion &&
                Time.unscaledTime >= _nextAllowedMaskRebuild)
            {
                _lastSeenMaskVersion = _previewSource.MaskVersion;
                _nextAllowedMaskRebuild = Time.unscaledTime + MaskRebuildInterval;
                Rebuild();
            }
        }

        private void Rebuild()
        {
            if (_previewSource == null) return;

            Mesh mesh = MeshExtractor.Extract(_previewSource, _settings, out int triangleCount, out string error);
            Error = error;
            PreviewTriangleCount = triangleCount;

            if (mesh == null)
            {
                // Keeps _previewSource - the SESSION stays open (see IsPreviewing) so the
                // preview reappears by itself the moment the mask becomes non-empty again.
                // Erasing a mask back to nothing mid-tweak and repainting it should just carry
                // on showing a result, not require pressing Preview a second time; Cancel stays
                // available throughout for actually closing the session.
                if (_previewGO != null) { _previewGO.SetActive(false); Destroy(_previewGO); _previewGO = null; }
                if (_previewMesh != null) { Destroy(_previewMesh); _previewMesh = null; }
                return;
            }

            if (_previewMesh != null) Destroy(_previewMesh);
            _previewMesh = mesh;

            EnsurePreviewObject();
            _previewGO.GetComponent<MeshFilter>().sharedMesh = _previewMesh;

            Transform srcT = _previewSource.transform;
            _previewGO.transform.SetPositionAndRotation(srcT.position, srcT.rotation);
            _previewGO.transform.localScale = srcT.localScale;
        }

        private void EnsurePreviewObject()
        {
            if (_previewGO != null) return;

            // Root-level and NOT a SculptableMesh: it must not register with SelectionManager
            // (it would show up in the object list, be selectable, sculptable and savable
            // despite being a transient overlay), and it must not carry a collider the brush
            // could raycast against.
            _previewGO = new GameObject("ExtractPreview", typeof(MeshFilter), typeof(MeshRenderer));
            _previewGO.hideFlags = HideFlags.DontSave;

            var renderer = _previewGO.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            Shader shader = Shader.Find("Custom/ExtractPreview");
            if (shader == null)
            {
                Debug.LogWarning("[MaskExtract] Custom/ExtractPreview shader not found; preview will render opaque.");
                return;
            }

            _previewMaterial = new Material(shader) { name = "Extract Preview (Runtime)" };
            _previewMaterial.SetColor("_Color", PreviewColor);
            _previewMaterial.SetColor("_RimColor", PreviewRimColor);
            renderer.material = _previewMaterial;
        }

        private void OnDisable() => EndPreview();
    }
}
