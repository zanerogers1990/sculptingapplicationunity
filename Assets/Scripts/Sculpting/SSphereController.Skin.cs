using UnityEngine;

namespace Sculpting
{
    /// The live skin, and Convert.
    ///
    /// Three tiers, each paying only for what its moment needs: Draft WHILE the artist drags,
    /// Preview once the rig has been still for a moment, Final only for Convert. The old
    /// controller refused to skin at all mid-drag and throttled to four rebuilds a second
    /// otherwise, so the skin always trailed the spheres - the opposite of the armature feeling
    /// alive. And a settled skin at Convert's full resolution cost a detailed creature almost half
    /// a second, which as a hitch on every mouse release would be its own kind of unresponsive.
    public partial class SSphereController
    {
        /// How long the rig must sit still before the Preview skin replaces the Draft. Short
        /// enough to feel immediate on release, long enough that a pause mid-gesture does not
        /// trigger a skin the next frame's movement throws away.
        private const float SettledSkinDelay = 0.18f;

        /// A Draft that takes longer than this has its successor delayed proportionally, so a huge
        /// rig degrades to a lower skin frame rate instead of dragging the whole viewport down.
        private const float DraftBudgetMs = 12f;

        private static readonly Color ShellColor = new Color(0.96f, 0.8f, 0.68f, 0.28f);
        private static readonly Color ShellRimColor = new Color(1f, 0.94f, 0.88f, 1f);
        private static readonly Color SolidColor = new Color(0.8f, 0.5f, 0.42f);

        private SSphereSkinner.SkinSettings _settings = SSphereSkinner.SkinSettings.Default;
        private int _settingsVersion;

        /// Draw the skin as a translucent shell over the armature while editing.
        public bool ShowSkin { get; set; } = true;

        /// Solid skin with the armature hidden (A) - judging the actual shape. Editing still works:
        /// picking is against the spheres, which lie inside the skin, so dragging a limb's surface
        /// drags that limb.
        public bool PreviewMode { get; set; }

        public float Density
        {
            get => _settings.Density;
            set => SetSetting(ref _settings.Density, Mathf.Clamp(value, SSphereSkinner.MinDensity, SSphereSkinner.MaxDensity));
        }

        public float Blend
        {
            get => _settings.Blend;
            set => SetSetting(ref _settings.Blend, Mathf.Clamp01(value));
        }

        public int Smoothing
        {
            get => _settings.Smoothing;
            set
            {
                int v = Mathf.Clamp(value, 0, 12);
                if (v == _settings.Smoothing) return;
                _settings.Smoothing = v;
                _settingsVersion++;
            }
        }

        private void SetSetting(ref float field, float value)
        {
            if (Mathf.Approximately(field, value)) return;
            field = value;
            _settingsVersion++;
        }

        /// Last skin attempt's failure, or null.
        public string Error { get; private set; }
        public int PreviewTriangleCount { get; private set; }

        /// Whether the skin on screen is the coarse mid-drag one.
        public bool SkinIsDraft { get; private set; }

        public SSphereSkinner.SkinStats LastSkinStats { get; private set; }

        public int EffectiveResolution => SSphereSkinner.PreviewResolution(_rig, _symmetryX, _settings);

        private GameObject _skinObject;
        private MeshRenderer _skinRenderer;
        private Mesh _skinMesh;
        private Material _shellMaterial;
        private Material _solidMaterial;
        private bool _hasSkin;

        // What the shown skin was built from, and what the rig looks like now - see RefreshSkin.
        private int _skinRigVersion = -1;
        private int _skinSettingsVersion = -1;
        private bool _skinSymmetry;
        private bool _skinAttemptWasSettled;
        private int _observedRigVersion = -1;
        private int _observedSettingsVersion = -1;
        private bool _observedSymmetry;
        private float _lastChangeTime;
        private float _nextDraftAllowed;

        private void RefreshSkin()
        {
            bool wanted = (ShowSkin || PreviewMode) && !_rig.IsEmpty;
            if (!wanted)
            {
                SetSkinVisible(false);
                if (_rig.IsEmpty)
                {
                    _hasSkin = false;
                    PreviewTriangleCount = 0;
                    Error = null;
                }
                return;
            }

            float now = Time.unscaledTime;
            if (_rig.Version != _observedRigVersion || _settingsVersion != _observedSettingsVersion ||
                _symmetryX != _observedSymmetry)
            {
                _observedRigVersion = _rig.Version;
                _observedSettingsVersion = _settingsVersion;
                _observedSymmetry = _symmetryX;
                _lastChangeTime = now;
            }

            bool current = _skinRigVersion == _observedRigVersion &&
                           _skinSettingsVersion == _observedSettingsVersion &&
                           _skinSymmetry == _observedSymmetry;
            bool settled = _drag == DragKind.None && now - _lastChangeTime >= SettledSkinDelay;

            if (settled && (!current || !_skinAttemptWasSettled))
                RunSkin(SSphereSkinner.SkinQuality.Preview, now);
            else if (!settled && !current && now >= _nextDraftAllowed)
                RunSkin(SSphereSkinner.SkinQuality.Draft, now);

            SetSkinVisible(_hasSkin);
            if (_skinRenderer != null)
            {
                Material look = PreviewMode ? _solidMaterial : _shellMaterial;
                if (_skinRenderer.sharedMaterial != look) _skinRenderer.sharedMaterial = look;
            }
        }

        private void RunSkin(SSphereSkinner.SkinQuality quality, float now)
        {
            EnsureSkinObject();

            bool ok = SSphereSkinner.SkinInto(_rig, _symmetryX, _settings, quality, _skinMesh,
                                              out SSphereSkinner.SkinStats stats, out string error);

            // Recorded even on failure, so a rig that cannot skin is not retried every frame.
            _skinRigVersion = _observedRigVersion;
            _skinSettingsVersion = _observedSettingsVersion;
            _skinSymmetry = _observedSymmetry;
            _skinAttemptWasSettled = quality != SSphereSkinner.SkinQuality.Draft;

            if (ok)
            {
                _hasSkin = true;
                Error = null;
                PreviewTriangleCount = stats.TriangleCount;
                SkinIsDraft = !_skinAttemptWasSettled;
                LastSkinStats = stats;
            }
            else
            {
                Error = error;
            }

            if (quality == SSphereSkinner.SkinQuality.Draft)
                _nextDraftAllowed = stats.Milliseconds > DraftBudgetMs ? now + stats.Milliseconds * 0.0015f : now;
        }

        private void EnsureSkinObject()
        {
            if (_skinObject != null) return;
            EnsureRigRoot();

            _skinMesh = new Mesh { name = "SSphere Skin Preview", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _skinMesh.MarkDynamic();

            // Under the rig root, so the rig-local skin lands on the spheres with no transform maths.
            // No collider and no SculptableMesh: it is a view, not an object.
            _skinObject = new GameObject("SSphereSkinPreview", typeof(MeshFilter), typeof(MeshRenderer))
            {
                hideFlags = HideFlags.DontSave
            };
            _skinObject.transform.SetParent(_rigRoot, false);
            _skinObject.GetComponent<MeshFilter>().sharedMesh = _skinMesh;

            _skinRenderer = _skinObject.GetComponent<MeshRenderer>();
            _skinRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _skinRenderer.receiveShadows = false;

            _shellMaterial = SSphereArmatureView.CreateTranslucent("SSphere Skin Shell", ShellColor, ShellRimColor);
            _solidMaterial = SSphereArmatureView.CreateLit("SSphere Skin Solid", SolidColor, 0.3f);
            _skinRenderer.sharedMaterial = _shellMaterial;
        }

        private void SetSkinVisible(bool visible)
        {
            if (_skinObject != null && _skinObject.activeSelf != visible) _skinObject.SetActive(visible);
        }

        private void DestroySkin()
        {
            if (_skinObject != null)
            {
                // Deactivated first: Destroy is deferred to end of frame, and a preview still drawing
                // over the object that just replaced it reads as the conversion doing nothing.
                _skinObject.SetActive(false);
                Destroy(_skinObject);
            }
            if (_skinMesh != null) Destroy(_skinMesh);
            if (_shellMaterial != null) Destroy(_shellMaterial);
            if (_solidMaterial != null) Destroy(_solidMaterial);
            _skinObject = null;
            _skinRenderer = null;
            _skinMesh = null;
            _shellMaterial = null;
            _solidMaterial = null;
            _hasSkin = false;
            _skinRigVersion = -1;
        }

        // ------------------------------------------------------------------------- convert

        /// Bakes the Final skin into a real, independent SculptableMesh - the same "brand new
        /// object" contract MeshCloner, MeshMirror and MaskExtractController use, so the result is
        /// immediately sculptable, maskable, joinable and savable. Null (with Error set) when there
        /// is nothing to skin.
        public SculptableMesh ConvertToSculptMesh()
        {
            EndDrag();
            CommitRigEdit();

            Mesh skin = SSphereSkinner.Skin(_rig, _symmetryX, _settings, SSphereSkinner.SkinQuality.Final,
                                            out _, out string error);
            Error = error;
            if (skin == null) return null;

            SSphereRig.Node[] rigBefore = _rig.Snapshot();
            bool symmetryBefore = _symmetryX;

            // Re-origin about the mesh's own centre and put the offset into the Transform: an
            // object whose geometry sits far from its pivot mirrors through the wrong plane and gets
            // a mis-sized gizmo. Under symmetry the centre is pinned to x = 0, so the new object's
            // pivot lies exactly on the plane the rig was built about.
            Vector3 centre = skin.bounds.center;
            if (_symmetryX) centre.x = 0f;
            Vector3[] verts = skin.vertices;
            for (int i = 0; i < verts.Length; i++) verts[i] -= centre;
            skin.vertices = verts;
            skin.RecalculateBounds();

            SculptableMesh sculptable = SceneObjectFactory.Create(skin, ObjectNaming.Unique("SSphere Mesh"),
                _rigRoot.TransformPoint(centre), _rigRoot.rotation, _rigRoot.lossyScale);

            bool clearedRig = !KeepRigOnConvert;
            if (clearedRig) ClearRigInternal();
            PreviewMode = false;

            Selection?.Select(sculptable, false);
            // Straight into sculpting on what was just made - a rig-editing mode with no rig in it
            // is a dead end.
            Gizmo?.SetMode(GizmoMode.Sculpt);

            RecordConvertUndo(sculptable, rigBefore, symmetryBefore, clearedRig);
            return sculptable;
        }

        /// Makes Convert one undo press. Undo PARKS the created object (unregistered, deactivated)
        /// rather than destroying it, so redoing the convert still finds the object - and any
        /// strokes made on it - intact. The rig is only put back if Convert actually cleared it.
        private void RecordConvertUndo(SculptableMesh created, SSphereRig.Node[] rigBefore, bool symmetryBefore, bool clearedRig)
        {
            Mesh createdMesh = created.Mesh;
            long bytes = SSphereRig.SnapshotBytes(rigBefore);
            if (createdMesh != null)
                bytes += (long)createdMesh.vertexCount * 12 + (long)createdMesh.triangles.Length * 4;

            EditHistory.RecordSceneAction("Skin SSpheres",
                undo: () =>
                {
                    if (created != null)
                    {
                        // SculptableMesh registers in OnEnable but only unregisters in OnDestroy,
                        // so deactivating alone would leave a ghost row in the scene list.
                        Selection?.Unregister(created);
                        created.gameObject.SetActive(false);
                    }
                    if (clearedRig)
                    {
                        _drag = DragKind.None;
                        _rig.Restore(rigBefore);
                        _symmetryX = symmetryBefore;
                        SelectedNode = NoNode;
                    }
                    Gizmo?.SetMode(GizmoMode.SSphere);
                },
                redo: () =>
                {
                    if (created != null)
                    {
                        created.gameObject.SetActive(true);
                        Selection?.Select(created, false);
                    }
                    if (clearedRig) ClearRigInternal();
                    Gizmo?.SetMode(GizmoMode.Sculpt);
                },
                discard: () =>
                {
                    // Only a PARKED object is freed - one still active is real, in the scene, and
                    // possibly sculpted on since.
                    if (created != null && !created.gameObject.activeSelf) Destroy(created.gameObject);
                },
                approxBytes: bytes);
        }
    }
}
