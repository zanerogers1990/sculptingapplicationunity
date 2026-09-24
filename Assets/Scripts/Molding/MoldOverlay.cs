using System.Collections.Generic;
using UnityEngine;

namespace Sculpting.Molding
{
    /// Everything the mold session draws into the viewport: the parting sheet, the model tinted
    /// by which half it lands in, the block's wire outline, and live previews of every pin,
    /// socket and channel.
    ///
    /// Owned by MoldController, which is a big enough class already - this keeps all the
    /// throwaway GameObject/Mesh/Material lifetime in one place with one Dispose. Every object
    /// here is root-level, HideFlags.DontSave, collider-free and NOT a SculptableMesh, for the
    /// same reasons MaskExtractController's preview is: it must never appear in the object list,
    /// never be selectable or sculptable, and never be saved with the scene.
    public sealed class MoldOverlay
    {
        private static readonly Color SurfaceColor = new Color(1f, 0.78f, 0.25f, 0.30f);
        private static readonly Color BoxColor = new Color(0.55f, 0.75f, 1f, 0.85f);
        private static readonly Color PegColor = new Color(0.35f, 0.9f, 0.55f, 0.75f);
        private static readonly Color SocketColor = new Color(0.45f, 0.65f, 1f, 0.45f);
        private static readonly Color SprueColor = new Color(1f, 0.45f, 0.35f, 0.65f);
        private static readonly Color VentColor = new Color(0.8f, 0.5f, 1f, 0.65f);
        private static readonly Color SelectedColor = new Color(1f, 1f, 1f, 0.95f);
        /// A channel that never reaches the model - it would cut a dead hole in the mold.
        private static readonly Color UnconnectedColor = new Color(1f, 0.15f, 0.1f, 0.9f);
        /// The sprue's runner, a shade off the funnel so the two parts read separately.
        private static readonly Color RunnerColor = new Color(1f, 0.7f, 0.35f, 0.75f);

        private GameObject _surfaceGO, _tintGO, _boxGO, _featureGO;
        private Mesh _surfaceMesh, _tintMesh, _boxMesh, _featureMesh;
        private Material _surfaceMat, _tintMat, _boxMat, _featureMat;

        // The tint mesh is a positional copy of the model that only its COLOURS change on. Kept
        // across updates and re-uploaded colour-only, because re-copying a million positions and
        // indices every time a slider moves is what would make the live readout stop being live.
        private Color32[] _tintColors;
        private int _tintVertexCount;
        private SculptableMesh _tintSource;
        private int _tintTopologyVersion = -1;
        /// Fingerprint of the vertex positions last uploaded to the tint copy. Positions only
        /// change if the model is sculpted, which a mold session cannot do - so re-uploading them
        /// (and re-walking them for bounds) on every update was 15ms per drag frame spent copying
        /// a million vertices that had not moved.
        private ulong _tintGeometryKey;

        public UndercutAnalysis.Report LastReport { get; private set; }

        // -------------------------------------------------------------------- parting sheet

        public void UpdateSurface(PartingField field, bool visible)
        {
            if (!visible || field == null)
            {
                SetActive(_surfaceGO, false);
                return;
            }

            if (!EnsureObject(ref _surfaceGO, ref _surfaceMat, "MoldPartingSurface",
                              zTest: UnityEngine.Rendering.CompareFunction.Always,
                              cull: UnityEngine.Rendering.CullMode.Off,
                              zWrite: false, tint: SurfaceColor))
            {
                SetActive(_surfaceGO, false);
                return;
            }

            if (_surfaceMesh != null) Object.Destroy(_surfaceMesh);
            _surfaceMesh = field.BuildSurfaceMesh();
            // Vertex colours drive the shader, so the sheet carries white and the material's
            // tint does the colouring - one less array to build per drag frame.
            _surfaceGO.GetComponent<MeshFilter>().sharedMesh = _surfaceMesh;
            SetActive(_surfaceGO, true);
        }

        // ------------------------------------------------------------------- undercut tint

        /// Rebuilds the tinted copy of the model. `worldVertices` are the model's vertices in
        /// world space (the controller already has them from the fit, so they are not
        /// recomputed here), and `geometryKey` changes whenever the model's vertex positions do -
        /// the positions are only re-uploaded then. The copy is rebuilt outright when the vertex
        /// or triangle count changes.
        public void UpdateTint(SculptableMesh model, Vector3[] worldVertices, int vertexCount, ulong geometryKey,
                               PartingField field, PullColumnMap columns, bool visible, float seamBand)
        {
            if (!visible || model == null || field == null || columns == null)
            {
                SetActive(_tintGO, false);
                return;
            }

            if (!EnsureObject(ref _tintGO, ref _tintMat, "MoldUndercutTint",
                              zTest: UnityEngine.Rendering.CompareFunction.LessEqual,
                              cull: UnityEngine.Rendering.CullMode.Back,
                              zWrite: true, tint: Color.white))
            {
                SetActive(_tintGO, false);
                return;
            }
            // Pulled toward the camera in depth so it wins against the model it is sitting
            // exactly on top of, without displacing any geometry (which would show through at
            // silhouettes).
            _tintMat.SetFloat("_OffsetFactor", -1f);
            _tintMat.SetFloat("_OffsetUnits", -2f);
            _tintMat.SetFloat("_Alpha", 0.95f);

            // The copy lives in the model's LOCAL space and the overlay object wears the model's
            // transform, so the index buffer and the normals are copied once and never touched
            // again. Only the colours - and the positions, which a sculpt stroke can move - are
            // re-uploaded per update.
            vertexCount = Mathf.Min(vertexCount, model.VertexCount);
            int topologyVersion = model.TriangleCount;
            bool rebuild = _tintMesh == null || _tintSource != model ||
                           _tintVertexCount != vertexCount || _tintTopologyVersion != topologyVersion;

            if (rebuild)
            {
                if (_tintMesh != null) Object.Destroy(_tintMesh);
                _tintMesh = new Mesh { name = "Mold Undercut Tint" };
                if (vertexCount > 65000) _tintMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                _tintMesh.SetVertices(model.VerticesExact());
                _tintMesh.SetNormals(model.NormalsExact());
                _tintMesh.SetTriangles(model.TrianglesExact(), 0);
                _tintMesh.RecalculateBounds();

                _tintColors = new Color32[vertexCount];
                _tintVertexCount = vertexCount;
                _tintSource = model;
                _tintTopologyVersion = topologyVersion;
                _tintGeometryKey = geometryKey;
                _tintGO.GetComponent<MeshFilter>().sharedMesh = _tintMesh;
            }
            else if (geometryKey != _tintGeometryKey)
            {
                _tintMesh.SetVertices(model.VerticesExact());
                _tintMesh.RecalculateBounds();
                _tintGeometryKey = geometryKey;
            }

            // The analysis runs in WORLD space, since that is where the field and the columns are.
            LastReport = UndercutAnalysis.Evaluate(worldVertices, vertexCount, field, columns, seamBand, _tintColors);
            _tintMesh.SetColors(_tintColors);

            Transform t = model.transform;
            _tintGO.transform.SetPositionAndRotation(t.position, t.rotation);
            _tintGO.transform.localScale = t.localScale;
            SetActive(_tintGO, true);
        }

        // ------------------------------------------------------------------- block outline

        public void UpdateBox(in MoldBlock block, MoldFrame frame, bool visible)
        {
            if (!visible)
            {
                SetActive(_boxGO, false);
                return;
            }

            if (!EnsureObject(ref _boxGO, ref _boxMat, "MoldBlockOutline",
                              zTest: UnityEngine.Rendering.CompareFunction.Always,
                              cull: UnityEngine.Rendering.CullMode.Off,
                              zWrite: false, tint: BoxColor))
            {
                SetActive(_boxGO, false);
                return;
            }

            var corners = new Vector3[8];
            block.Corners(frame, corners);

            if (_boxMesh == null) _boxMesh = new Mesh { name = "Mold Block Outline" };
            _boxMesh.Clear();
            _boxMesh.SetVertices(corners);
            // Zero normals mark these as unlit lines - see the shader's Frag.
            _boxMesh.SetNormals(new Vector3[8]);
            _boxMesh.SetColors(new[] { Color.white, Color.white, Color.white, Color.white,
                                       Color.white, Color.white, Color.white, Color.white });
            _boxMesh.SetIndices(new[]
            {
                0, 1, 1, 2, 2, 3, 3, 0,   // bottom face
                4, 5, 5, 6, 6, 7, 7, 4,   // top face
                0, 4, 1, 5, 2, 6, 3, 7,   // uprights
            }, MeshTopology.Lines, 0);
            _boxMesh.RecalculateBounds();

            _boxGO.GetComponent<MeshFilter>().sharedMesh = _boxMesh;
            SetActive(_boxGO, true);
        }

        // ------------------------------------------------------------------ feature preview

        /// Draws every placed feature as the ACTUAL solid it will cut or add - the peg, the
        /// socket it mates with, the funnel of the sprue. Building these from MoldGeometry rather
        /// than from stand-in markers is what makes the preview trustworthy: what is on screen is
        /// literally the operand the boolean is going to use, so a pin that will punch through
        /// the floor is visibly punching through the floor before anything is built.
        public void UpdateFeatures(IReadOnlyList<MoldFeature> features, PartingField field, in MoldBlock block,
                                   PullColumnMap columns, MoldSettings settings, MoldFeature selected)
        {
            if (features == null || features.Count == 0 || field == null)
            {
                SetActive(_featureGO, false);
                return;
            }

            if (!EnsureObject(ref _featureGO, ref _featureMat, "MoldFeaturePreview",
                              zTest: UnityEngine.Rendering.CompareFunction.LessEqual,
                              cull: UnityEngine.Rendering.CullMode.Back,
                              zWrite: false, tint: Color.white))
            {
                SetActive(_featureGO, false);
                return;
            }
            _featureMat.SetFloat("_OffsetFactor", -1f);
            _featureMat.SetFloat("_OffsetUnits", -4f);

            var verts = new List<Vector3>();
            var tris = new List<int>();
            var colors = new List<Color>();

            // Same expansion the build uses, so a mirrored vent is visible before it is built
            // rather than appearing only in the finished half.
            foreach (MoldFeature f in MoldGeometry.ExpandAll(features, block, settings))
            {
                if (f == null || !f.Enabled) continue;
                int from = verts.Count;

                if (f.Kind == MoldFeatureKind.Pin)
                {
                    var socketVerts = new List<Vector3>();
                    var socketTris = new List<int>();
                    MoldGeometry.BuildPin(f, field, settings, verts, tris, socketVerts, socketTris);
                    bool pinSelected = f == selected || (f.IsMirror && f.Source == selected && selected != null);
                    Fill(colors, verts.Count - from, Derived(pinSelected ? SelectedColor : PegColor, f));

                    int socketFrom = verts.Count;
                    foreach (Vector3 v in socketVerts) verts.Add(v);
                    foreach (int i in socketTris) tris.Add(i + socketFrom);
                    Fill(colors, verts.Count - socketFrom, SocketColor);
                }
                else
                {
                    bool isSelected = f == selected || (f.IsMirror && f.Source == selected && selected != null);
                    MoldGeometry.ChannelLayout layout = MoldGeometry.LayoutChannel(f, field, block, columns, settings);
                    bool dead = columns != null && !layout.Connected;

                    if (f.Kind == MoldFeatureKind.Sprue && !dead && layout.Connected)
                    {
                        // Drawn as its two parts - runner, then funnel - so it is plain on screen
                        // that the funnel stands off the model and only the runner touches it.
                        int runnerFrom = verts.Count;
                        MoldGeometry.AddSweptChannel(verts, tris, field, layout.Inner, MoldGeometry.RunnerEnd(layout, f),
                                                     layout.ChannelRadius, layout.ChannelRadius);
                        Fill(colors, verts.Count - runnerFrom, Derived(isSelected ? SelectedColor : RunnerColor, f));

                        int funnelFrom = verts.Count;
                        MoldGeometry.AddSweptChannel(verts, tris, field, layout.Hub, layout.Exit,
                                                     layout.FunnelInner, layout.FunnelOuter);
                        Fill(colors, verts.Count - funnelFrom, Derived(isSelected ? SelectedColor : SprueColor, f));
                    }
                    else
                    {
                        MoldGeometry.BuildChannel(f, field, block, columns, settings, verts, tris);
                        Color c = dead ? UnconnectedColor
                                : isSelected ? SelectedColor
                                : f.Kind == MoldFeatureKind.Sprue ? SprueColor : VentColor;
                        Fill(colors, verts.Count - from, Derived(c, f));
                    }
                }
            }

            if (verts.Count == 0)
            {
                SetActive(_featureGO, false);
                return;
            }

            if (_featureMesh == null) _featureMesh = new Mesh { name = "Mold Feature Preview" };
            _featureMesh.Clear();
            if (verts.Count > 65000) _featureMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            _featureMesh.SetVertices(verts);
            _featureMesh.SetTriangles(tris, 0);
            _featureMesh.SetColors(colors);
            _featureMesh.RecalculateNormals();
            _featureMesh.RecalculateBounds();

            _featureGO.GetComponent<MeshFilter>().sharedMesh = _featureMesh;
            SetActive(_featureGO, true);
        }

        /// Dims a mirror copy so it reads as something following the original rather than a
        /// second feature the user placed - a mirror cannot be selected or dragged on its own,
        /// and it should not look like it can.
        private static Color Derived(Color c, MoldFeature f) =>
            f != null && f.IsMirror ? new Color(c.r * 0.65f, c.g * 0.65f, c.b * 0.65f, c.a * 0.8f) : c;

        private static void Fill(List<Color> colors, int count, Color c)
        {
            for (int i = 0; i < count; i++) colors.Add(c);
        }

        // ------------------------------------------------------------------------ lifetime

        public void HideAll()
        {
            SetActive(_surfaceGO, false);
            SetActive(_tintGO, false);
            SetActive(_boxGO, false);
            SetActive(_featureGO, false);
        }

        public void Dispose()
        {
            DestroyObject(ref _surfaceGO, ref _surfaceMesh, ref _surfaceMat);
            DestroyObject(ref _tintGO, ref _tintMesh, ref _tintMat);
            DestroyObject(ref _boxGO, ref _boxMesh, ref _boxMat);
            DestroyObject(ref _featureGO, ref _featureMesh, ref _featureMat);
            _tintColors = null;
            _tintSource = null;
            _tintGeometryKey = 0;
            _tintVertexCount = 0;
            _tintTopologyVersion = -1;
        }

        private static void SetActive(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }

        /// Creates the overlay object and its material, and returns false if the material could
        /// not be made.
        ///
        /// The object and the material are checked INDEPENDENTLY, on purpose. Shader.Find can come
        /// back empty on the first call of a session - the shader is not always resident the
        /// instant Play starts - and the earlier version of this bailed out after creating the
        /// GameObject, so the overlay was stranded material-less for the whole session: every
        /// later call saw a non-null object and returned straight away without ever retrying, and
        /// the callers that configure the material afterwards dereferenced null. Retrying the
        /// material on each call costs one null check and makes that self-healing.
        private static bool EnsureObject(ref GameObject go, ref Material mat, string name,
                                         UnityEngine.Rendering.CompareFunction zTest,
                                         UnityEngine.Rendering.CullMode cull, bool zWrite, Color tint)
        {
            if (go == null)
            {
                go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer)) { hideFlags = HideFlags.DontSave };
                var created = go.GetComponent<MeshRenderer>();
                created.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                created.receiveShadows = false;
            }

            if (mat != null) return true;

            Shader shader = Shader.Find("Custom/MoldOverlay");
            if (shader == null)
            {
                Debug.LogWarning("[Mold] Custom/MoldOverlay shader not found - overlay hidden until it loads.");
                return false;
            }

            mat = new Material(shader) { name = name + " (Runtime)" };
            mat.SetColor("_Tint", tint);
            mat.SetFloat("_Alpha", tint.a);
            mat.SetFloat("_ZTest", (float)zTest);
            mat.SetFloat("_Cull", (float)cull);
            mat.SetFloat("_ZWrite", zWrite ? 1f : 0f);
            go.GetComponent<MeshRenderer>().material = mat;
            return true;
        }

        private static void DestroyObject(ref GameObject go, ref Mesh mesh, ref Material mat)
        {
            // Deactivated before Destroy for the same reason MaskExtractController's preview is:
            // Destroy is deferred to the end of the frame, and a ghost sheet still drawing over
            // the finished halves for a frame reads as the session not having closed.
            if (go != null) { go.SetActive(false); Object.Destroy(go); go = null; }
            if (mesh != null) { Object.Destroy(mesh); mesh = null; }
            if (mat != null) { Object.Destroy(mat); mat = null; }
        }
    }
}
