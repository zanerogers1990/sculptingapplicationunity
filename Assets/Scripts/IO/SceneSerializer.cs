using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Sculpting.IO
{
    /// Saves and loads the whole sculpting session to a single file: brush settings (including
    /// per-brush memory), every object's geometry/transform/mask/mirror axes, material,
    /// lighting, background, post-processing and the camera. Undo history is deliberately NOT
    /// persisted - it can be many full mesh clones per object (see SculptHistory), it is
    /// meaningless once the app restarts, and the user asked for "everything that isn't undo
    /// history".
    ///
    /// FORMAT (.sculpt) - a JSON header followed by raw binary geometry:
    ///
    ///     magic   "SCLPTSV\0"      8 bytes ASCII
    ///     version int32            FormatVersion below
    ///     jsonLen int32            byte length of the UTF8 JSON that follows
    ///     json    byte[jsonLen]    SculptSaveData (settings + per-object metadata)
    ///     then, per object, in the same order as SculptSaveData.objects:
    ///         vertices  float32[vertexCount * 3]
    ///         normals   float32[vertexCount * 3]
    ///         triangles int32[triangleIndexCount]
    ///         mask      float32[vertexCount]        (only when entry.hasMask)
    ///
    /// The split is the point. Settings churn constantly and want JsonUtility's tolerance for
    /// added/removed fields; geometry never changes shape and would be ~10x larger and far
    /// slower as JSON text (a 500k-vertex mesh is ~6MB raw, but ~70MB as JSON floats). Bulk
    /// arrays move through one Buffer.BlockCopy each rather than per-element BinaryWriter
    /// calls, which matters at those sizes.
    public static class SceneSerializer
    {
        private const int FormatVersion = 1;
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("SCLPTSV\0");

        public const string FileExtension = ".sculpt";

        /// Where the path field starts, and what a bare filename resolves against. Chosen over
        /// dataPath because on Windows a built player's install directory is frequently not
        /// user-writable.
        public static string DefaultDirectory => Application.persistentDataPath;
        public static string DefaultPath => Path.Combine(DefaultDirectory, "sculpt-session" + FileExtension);

        // ------------------------------------------------------------------------------ save

        /// Returns true on success. On failure `error` explains why and NOTHING has been written
        /// over the target - the file is built in memory and only committed once every object
        /// has been read successfully, so a mid-save failure can't leave a truncated file where
        /// a good one used to be.
        public static bool Save(string path, out string error)
        {
            error = null;
            try
            {
                path = NormalizePath(path);

                var data = new SculptSaveData
                {
                    savedUtc = DateTime.UtcNow.ToString("o"),
                    unityVersion = Application.unityVersion,
                };

                var meshes = CollectObjects();
                CaptureSettings(data);

                // Geometry is staged here rather than streamed straight to disk, so a failure
                // partway through leaves the existing file untouched (see remarks above).
                var geometry = new List<byte[]>(meshes.Count * 4);

                foreach (SculptableMesh m in meshes)
                {
                    var entry = new SculptSaveData.ObjectEntry
                    {
                        name = m.name,
                        position = m.transform.position,
                        rotation = m.transform.rotation,
                        scale = m.transform.localScale,
                        visible = m.Visible,
                    };

                    var mirror = m.GetComponent<MirrorController>();
                    if (mirror != null)
                    {
                        entry.mirrorX = mirror.MirrorX;
                        entry.mirrorY = mirror.MirrorY;
                        entry.mirrorZ = mirror.MirrorZ;
                        entry.showMirrorPlanes = mirror.ShowPlanes;
                    }

                    // The CPU-side working arrays are authoritative; the Mesh's own getters do
                    // NOT reliably reflect compute-shader scatter writes to its Raw vertex
                    // buffer (this project has hit that twice - see SculptableMesh.Remesh and
                    // MeshJoiner). Saving from m.Mesh.vertices would silently persist the
                    // pre-sculpt shape.
                    Vector3[] verts = m.Vertices;
                    Vector3[] normals = m.Normals;
                    int[] tris = m.Triangles;
                    float[] mask = m.Mask;

                    entry.vertexCount = verts.Length;
                    entry.triangleIndexCount = tris.Length;
                    entry.hasMask = mask != null && mask.Length == verts.Length && HasAnyMask(mask);

                    geometry.Add(Vector3ArrayToBytes(verts));
                    geometry.Add(Vector3ArrayToBytes(NormalizeLength(normals, verts.Length)));
                    geometry.Add(IntArrayToBytes(tris));
                    if (entry.hasMask) geometry.Add(FloatArrayToBytes(mask));

                    data.objects.Add(entry);
                }

                data.primarySelectionIndex = PrimaryIndex(meshes);

                byte[] json = Encoding.UTF8.GetBytes(JsonUtility.ToJson(data));

                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var w = new BinaryWriter(fs))
                {
                    w.Write(Magic);
                    w.Write(FormatVersion);
                    w.Write(json.Length);
                    w.Write(json);
                    foreach (byte[] block in geometry) w.Write(block);
                }

                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------------------ load

        /// Replaces the current scene contents entirely - objects AND global settings (brush,
        /// material, lighting, camera). Returns true on success; on failure `error` explains why
        /// and the scene is left exactly as it was, because the file is fully parsed and
        /// validated into memory BEFORE a single existing object is destroyed. A corrupt file
        /// cannot cost the user their in-progress work.
        public static bool Load(string path, out string error)
        {
            if (!ReadFile(path, out ParsedFile file, out error)) return false;

            // --- past this line the file is known-good; only now touch the live scene ---

            var selection = UnityEngine.Object.FindFirstObjectByType<SelectionManager>();

            // Before DestroyExistingObjects, not after: history can be holding objects that are
            // NOT in SelectionManager's list and so would survive that sweep - an undone ZSphere
            // convert parks the object it made, deactivated and unregistered, for a possible redo
            // (see ZSphereController.RecordConvertUndo). Clearing first runs each step's discard
            // while those objects are still reachable, so the load starts from a genuinely empty
            // scene instead of leaving orphans behind. Import deliberately does NOT do this -
            // adding objects invalidates nothing that is already in history.
            EditHistory.Clear();
            DestroyExistingObjects(selection);

            var created = new List<SculptableMesh>(file.Meshes.Count);
            for (int i = 0; i < file.Data.objects.Count; i++)
                created.Add(CreateObject(file.Data.objects[i], file.Meshes[i], file.Masks[i], file.Data.objects[i].name));

            ApplySettings(file.Data);

            // Re-selected last: creating each object registers it and can move the selection,
            // so anything set earlier would be overwritten.
            if (selection != null && created.Count > 0)
            {
                int idx = Mathf.Clamp(file.Data.primarySelectionIndex, 0, created.Count - 1);
                selection.Select(created[idx], false);
            }

            return true;
        }

        /// Adds a file's OBJECTS to the current scene, keeping everything already there. Use for
        /// bringing a model in from another session; Load is for reopening a session.
        ///
        /// Deliberately ignores the file's global settings - brush, material, lighting,
        /// background, post and camera are all left alone. Importing one model should not
        /// reposition the user's camera or overwrite the brush they are working with; those
        /// belong to the session being worked in, not to the thing being brought into it.
        ///
        /// Imported objects keep the world transforms they were saved with, so a multi-object
        /// file arrives laid out exactly as it was authored rather than collapsed onto one
        /// point - move it with the Transpose gizmo afterward. `importedCount` reports how many
        /// objects arrived.
        public static bool Import(string path, out int importedCount, out string error)
        {
            importedCount = 0;
            if (!ReadFile(path, out ParsedFile file, out error)) return false;

            var selection = UnityEngine.Object.FindFirstObjectByType<SelectionManager>();

            // Snapshot the names ALREADY in the scene, then extend it as each object is created,
            // so an import is unique both against the existing scene and within its own batch
            // (a file containing two objects called "Sphere" still yields two distinct names).
            var takenNames = new HashSet<string>();
            if (selection != null)
                foreach (SculptableMesh m in selection.AllObjects) if (m != null) takenNames.Add(m.name);

            var created = new List<SculptableMesh>(file.Meshes.Count);
            for (int i = 0; i < file.Data.objects.Count; i++)
            {
                string name = UniqueName(file.Data.objects[i].name, takenNames);
                takenNames.Add(name);
                created.Add(CreateObject(file.Data.objects[i], file.Meshes[i], file.Masks[i], name));
            }

            // Select the first import so the gizmo is immediately pointed at what just arrived -
            // same courtesy PrimitiveSpawner does for a newly spawned primitive. Note this does
            // NOT disturb SelectionManager.AllObjects[0], the scene's "main object" anchor used
            // by PrimitiveSpawner's spawn position and MeshMirror's reflection center: imports
            // are appended, so that anchor stays whatever it already was.
            if (selection != null && created.Count > 0) selection.Select(created[0], false);

            importedCount = created.Count;
            return true;
        }

        /// A fully parsed, validated file held in memory, with its meshes already built but not
        /// yet attached to anything. Existing only so Load and Import can share every byte of
        /// parsing and validation and differ purely in what they do with the result.
        private class ParsedFile
        {
            public SculptSaveData Data;
            public List<Mesh> Meshes;
            public List<float[]> Masks;
        }

        private static bool ReadFile(string path, out ParsedFile file, out string error)
        {
            file = null;
            error = null;
            var meshes = new List<Mesh>();

            try
            {
                path = NormalizePath(path);
                if (!File.Exists(path)) { error = "No file at " + path; return false; }

                SculptSaveData data;
                var masks = new List<float[]>();

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                using (var r = new BinaryReader(fs))
                {
                    byte[] magic = r.ReadBytes(Magic.Length);
                    if (!MagicMatches(magic)) { error = "Not a " + FileExtension + " file."; return false; }

                    int version = r.ReadInt32();
                    if (version > FormatVersion)
                    {
                        error = $"File is format v{version}; this build understands up to v{FormatVersion}.";
                        return false;
                    }

                    int jsonLen = r.ReadInt32();
                    if (jsonLen <= 0 || jsonLen > fs.Length) { error = "Header is corrupt."; return false; }
                    data = JsonUtility.FromJson<SculptSaveData>(Encoding.UTF8.GetString(r.ReadBytes(jsonLen)));
                    if (data == null) { error = "Header could not be parsed."; return false; }

                    foreach (SculptSaveData.ObjectEntry entry in data.objects)
                    {
                        Vector3[] verts = ReadVector3Array(r, entry.vertexCount);
                        Vector3[] normals = ReadVector3Array(r, entry.vertexCount);
                        int[] tris = ReadIntArray(r, entry.triangleIndexCount);
                        masks.Add(entry.hasMask ? ReadFloatArray(r, entry.vertexCount) : null);

                        var mesh = new Mesh { name = entry.name };
                        // Must be set before the vertex buffer is populated, and is required
                        // above 65535 vertices - remeshing routinely produces far more.
                        if (verts.Length > 65000) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
                        mesh.vertices = verts;
                        mesh.normals = normals;
                        mesh.triangles = tris;
                        mesh.RecalculateBounds();
                        meshes.Add(mesh);
                    }
                }

                file = new ParsedFile { Data = data, Meshes = meshes, Masks = masks };
                return true;
            }
            catch (EndOfStreamException)
            {
                error = "File is truncated or corrupt.";
                DestroyOrphans(meshes);
                return false;
            }
            catch (Exception e)
            {
                error = e.Message;
                DestroyOrphans(meshes);
                return false;
            }
        }

        /// Meshes built before a mid-file failure are attached to nothing, and Unity's Mesh is
        /// an unmanaged-backed Object that garbage collection will not reclaim on its own -
        /// without this, every failed load of a truncated file would leak its geometry.
        private static void DestroyOrphans(List<Mesh> meshes)
        {
            foreach (Mesh m in meshes) if (m != null) UnityEngine.Object.Destroy(m);
            meshes.Clear();
        }

        /// "Torso" -> "Torso (2)" -> "Torso (3)". Matches PrimitiveSpawner's own naming shape so
        /// imported objects don't look foreign in the scene-graph list. Names are cosmetic
        /// (SelectionManager keys on object references), but two identical rows in that list are
        /// genuinely hard to tell apart.
        private static string UniqueName(string baseName, HashSet<string> taken)
        {
            if (string.IsNullOrEmpty(baseName)) baseName = "Object";
            if (!taken.Contains(baseName)) return baseName;
            for (int n = 2; ; n++)
            {
                string candidate = $"{baseName} ({n})";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        // ------------------------------------------------------------------ scene <-> data

        private static List<SculptableMesh> CollectObjects()
        {
            var selection = UnityEngine.Object.FindFirstObjectByType<SelectionManager>();
            if (selection != null && selection.AllObjects.Count > 0)
                return new List<SculptableMesh>(selection.AllObjects);

            // Fallback for a scene with no SelectionManager. FindObjectsByType's order is not
            // guaranteed, and index 0 is meaningful (PrimitiveSpawner.MainObject / MeshMirror's
            // reflection center both use it), so sort by name for at least a stable result.
            var all = new List<SculptableMesh>(
                UnityEngine.Object.FindObjectsByType<SculptableMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            all.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            return all;
        }

        private static int PrimaryIndex(List<SculptableMesh> meshes)
        {
            var selection = UnityEngine.Object.FindFirstObjectByType<SelectionManager>();
            if (selection == null) return -1;
            return meshes.IndexOf(selection.PrimarySelection);
        }

        private static void DestroyExistingObjects(SelectionManager selection)
        {
            var existing = new List<SculptableMesh>(
                UnityEngine.Object.FindObjectsByType<SculptableMesh>(FindObjectsInactive.Include, FindObjectsSortMode.None));

            selection?.ClearSelection();
            foreach (SculptableMesh m in existing)
            {
                if (m == null) continue;
                selection?.Unregister(m);
                // DestroyImmediate, not Destroy: the replacement objects are created later in
                // THIS same call, and deferred destruction would leave the old ones registered
                // with SelectionManager for the rest of the frame - so AllObjects[0] (the
                // "main object" anchor) would briefly point at a doomed object.
                UnityEngine.Object.DestroyImmediate(m.gameObject);
            }
        }

        /// `name` is passed separately rather than read off `entry` because Import may have had
        /// to uniquify it against names already in the scene (see UniqueName).
        private static SculptableMesh CreateObject(SculptSaveData.ObjectEntry entry, Mesh mesh, float[] mask, string name)
        {
            SculptableMesh sculptable = CreateSculptable(mesh, name, entry.position, entry.rotation, entry.scale);

            var mirror = sculptable.GetComponent<MirrorController>();
            mirror.MirrorX = entry.mirrorX;
            mirror.MirrorY = entry.mirrorY;
            mirror.MirrorZ = entry.mirrorZ;
            mirror.ShowPlanes = entry.showMirrorPlanes;

            if (mask != null) sculptable.SetMask(mask);
            if (!entry.visible) sculptable.SetVisible(false);

            return sculptable;
        }

        /// Turns a bare Mesh into a fully live, sculptable scene object. Public because model
        /// import (see ImportAny) needs the identical construction sequence, and getting that
        /// sequence wrong fails in a confusing way rather than an obvious one.
        public static SculptableMesh CreateSculptable(Mesh mesh, string name, Vector3 position, Quaternion rotation, Vector3 scale)
        {
            var go = new GameObject(name);
            go.transform.SetPositionAndRotation(position, rotation);
            go.transform.localScale = scale;

            // Order matters: SculptableMesh.Awake() reads meshFilter.sharedMesh and instantiates
            // it, so the mesh has to be in place BEFORE the component is added (AddComponent
            // runs Awake synchronously). Same sequencing PrimitiveSpawner relies on, which gets
            // it for free from GameObject.CreatePrimitive.
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>();

            var sculptable = go.AddComponent<SculptableMesh>();
            go.AddComponent<MirrorController>();

            UnityEngine.Object.FindFirstObjectByType<SculptMaterialController>()
                ?.ApplyTo(go.GetComponent<Renderer>());

            return sculptable;
        }

        // ------------------------------------------------------------------- model import

        /// Every extension the Import button accepts, for the file dialog's filter.
        public static readonly string[] ImportableExtensions = { "sculpt", "obj" };

        /// Import dispatch: a .sculpt file brings in a whole saved session's objects, anything
        /// else is treated as a model file. Keeps the UI free of format knowledge - the button
        /// just hands over a path.
        public static bool ImportAny(string path, out int importedCount, out string error)
        {
            importedCount = 0;
            error = null;
            if (string.IsNullOrWhiteSpace(path)) { error = "No file selected."; return false; }

            if (path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
                return Import(path, out importedCount, out error);

            if (path.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            {
                if (!ImportModel(path, out error)) return false;
                importedCount = 1;
                return true;
            }

            error = "Unsupported file type - expected .sculpt or .obj.";
            return false;
        }

        /// Brings a single model file in as one new sculptable object.
        ///
        /// The model's LOCAL vertex coordinates are used exactly as authored, and the object is
        /// placed by moving its TRANSFORM instead. That distinction matters: the mirror/symmetry
        /// plane sits at the local origin (see MirrorController), so rewriting vertices to
        /// re-centre the model would move its symmetry plane off the centreline the modeller
        /// built it around. Positioning via the transform keeps that intact while still
        /// guaranteeing the model lands somewhere visible.
        private static bool ImportModel(string path, out string error)
        {
            Mesh mesh = ObjImporter.Import(path, out error);
            if (mesh == null) return false;

            var selection = UnityEngine.Object.FindFirstObjectByType<SelectionManager>();
            SculptableMesh anchor = selection != null && selection.AllObjects.Count > 0 ? selection.AllObjects[0] : null;

            // Uniform scale fitting the model to the scene's existing size. An OBJ can just as
            // easily arrive 0.01 or 500 units across, and either extreme is unusable - a
            // hundred-unit model swallows the scene, a tiny one is invisible. Applied as
            // TRANSFORM scale rather than baked into vertices, so it's non-destructive and the
            // user can undo the guess with the Scale gizmo.
            float scale = 1f;
            Vector3 anchorPos = Vector3.zero;
            if (anchor != null && anchor.Mesh != null)
            {
                anchorPos = anchor.transform.position;
                Vector3 ae = anchor.Mesh.bounds.extents;
                float targetExtent = (ae.x + ae.y + ae.z) / 3f * 2f;
                Vector3 me = mesh.bounds.extents;
                float modelExtent = Mathf.Max(me.x, me.y, me.z);
                if (modelExtent > 1e-5f && targetExtent > 1e-5f) scale = targetExtent / modelExtent;
            }

            // Offset so the model's BOUNDS CENTRE lands on the anchor, not its local origin - a
            // model authored with its origin at the feet (or far off in space) would otherwise
            // arrive out of view even though its transform is nominally in the right place.
            Vector3 position = anchorPos - mesh.bounds.center * scale;

            var taken = new HashSet<string>();
            if (selection != null)
                foreach (SculptableMesh m in selection.AllObjects) if (m != null) taken.Add(m.name);

            SculptableMesh created = CreateSculptable(
                mesh, UniqueName(mesh.name, taken), position, Quaternion.identity, Vector3.one * scale);

            selection?.Select(created, false);
            return true;
        }

        private static void CaptureSettings(SculptSaveData data)
        {
            var controller = UnityEngine.Object.FindFirstObjectByType<SculptController>();
            if (controller != null) data.brush = controller.CaptureSettings();

            var mat = UnityEngine.Object.FindFirstObjectByType<SculptMaterialController>();
            if (mat != null)
            {
                data.material.baseColor = mat.BaseColor;
                data.material.metallic = mat.Metallic;
                data.material.smoothness = mat.Smoothness;
                data.material.normalStrength = mat.NormalStrength;
                data.material.normalNoiseScale = mat.NormalNoiseScale;
                data.material.flatShading = mat.FlatShading;
                data.material.cavityEnabled = mat.CavityEnabled;
                data.material.recessColor = mat.RecessColor;
                data.material.peakColor = mat.PeakColor;
                data.material.cavityIntensity = mat.CavityIntensity;
                data.material.cavityRange = mat.CavityRange;
                data.material.matcapEnabled = mat.MatcapEnabled;
                data.material.matcapName = mat.MatcapName;
                data.material.matcapIntensity = mat.MatcapIntensity;
                data.material.matcapTintStrength = mat.MatcapTintStrength;
            }

            var light = UnityEngine.Object.FindFirstObjectByType<LightingRigController>();
            if (light != null)
            {
                data.environment.studioLightingEnabled = light.StudioLightingEnabled;
                data.environment.lightingMode = (int)light.Mode;
            }

            var bg = UnityEngine.Object.FindFirstObjectByType<BackgroundController>();
            if (bg != null)
            {
                data.environment.backgroundMode = (int)bg.Mode;
                data.environment.backgroundColorA = bg.ColorA;
                data.environment.backgroundColorB = bg.ColorB;
                data.environment.gradientBias = bg.GradientBias;
            }

            // Existing, not Instance: saving a scene that never touched HDRI should not bring a
            // controller into being just to write its defaults.
            var hdri = HdriEnvironmentController.Existing;
            if (hdri != null)
            {
                data.environment.hdriEnabled = hdri.Enabled;
                data.environment.hdriPath = hdri.Path ?? string.Empty;
                data.environment.hdriRotation = hdri.Rotation;
                data.environment.hdriExposure = hdri.Exposure;
                data.environment.hdriAmbientIntensity = hdri.AmbientIntensity;
                data.environment.hdriReflectionIntensity = hdri.ReflectionIntensity;
            }

            var post = UnityEngine.Object.FindFirstObjectByType<PostProcessingController>();
            if (post != null && post.HasVolume)
            {
                data.environment.postAvailable = true;
                data.environment.bloomEnabled = post.BloomEnabled;
                data.environment.bloomIntensity = post.BloomIntensity;
                data.environment.bloomThreshold = post.BloomThreshold;
                data.environment.vignetteEnabled = post.VignetteEnabled;
                data.environment.vignetteIntensity = post.VignetteIntensity;
                data.environment.vignetteSmoothness = post.VignetteSmoothness;
                data.environment.dofEnabled = post.DofEnabled;
                data.environment.dofFocusDistance = post.DofFocusDistance;
                data.environment.dofAperture = post.DofAperture;
                data.environment.colorAdjustmentsEnabled = post.ColorAdjustmentsEnabled;
                data.environment.saturation = post.Saturation;
                data.environment.contrast = post.Contrast;
            }

            var cam = UnityEngine.Object.FindFirstObjectByType<CameraOrbitController>();
            if (cam != null)
            {
                cam.GetView(out float yaw, out float pitch, out float distance, out Vector3 pivot);
                data.camera.valid = true;
                data.camera.yaw = yaw;
                data.camera.pitch = pitch;
                data.camera.distance = distance;
                data.camera.pivot = pivot;
                data.camera.orthographic = cam.Orthographic;
            }
        }

        private static void ApplySettings(SculptSaveData data)
        {
            var controller = UnityEngine.Object.FindFirstObjectByType<SculptController>();
            if (controller != null && data.brush != null) controller.ApplySettings(data.brush);

            var mat = UnityEngine.Object.FindFirstObjectByType<SculptMaterialController>();
            if (mat != null && data.material != null)
            {
                mat.BaseColor = data.material.baseColor;
                mat.Metallic = data.material.metallic;
                mat.Smoothness = data.material.smoothness;
                mat.NormalStrength = data.material.normalStrength;
                mat.NormalNoiseScale = data.material.normalNoiseScale;
                mat.FlatShading = data.material.flatShading;
                mat.CavityEnabled = data.material.cavityEnabled;
                mat.RecessColor = data.material.recessColor;
                mat.PeakColor = data.material.peakColor;
                mat.CavityIntensity = data.material.cavityIntensity;
                mat.CavityRange = data.material.cavityRange;
                mat.MatcapIntensity = data.material.matcapIntensity;
                mat.MatcapTintStrength = data.material.matcapTintStrength;
                // Name before the toggle: MatcapEnabled with nothing selected picks the first
                // matcap in the library, which would override what the file actually asked for.
                mat.MatcapName = data.material.matcapName;
                // ...and only enable if that name actually resolved. MatcapEnabled with nothing
                // selected falls back to the first matcap in the library, which for a file
                // naming a matcap this machine doesn't have would silently substitute a
                // different one - lit shading is the honest answer there.
                mat.MatcapEnabled = data.material.matcapEnabled && mat.HasMatcap;
            }

            var env = data.environment;
            if (env != null)
            {
                var light = UnityEngine.Object.FindFirstObjectByType<LightingRigController>();
                if (light != null)
                {
                    light.StudioLightingEnabled = env.studioLightingEnabled;
                    light.Mode = (LightingMode)env.lightingMode;
                }

                // HDRI before the background: the background's Hdri mode is only honoured once
                // an image is actually loaded, so applying it the other way round would silently
                // fall back to the gradient.
                if (env.hdriEnabled || !string.IsNullOrEmpty(env.hdriPath))
                {
                    HdriEnvironmentController.Instance.ApplySaved(
                        env.hdriEnabled, env.hdriPath, env.hdriRotation, env.hdriExposure,
                        env.hdriAmbientIntensity, env.hdriReflectionIntensity);
                }
                else
                {
                    // A file saved with no HDRI has to switch off one that is currently running,
                    // otherwise loading it leaves the previous scene's environment lighting on.
                    HdriEnvironmentController.Existing?.Clear();
                }

                var bg = UnityEngine.Object.FindFirstObjectByType<BackgroundController>();
                if (bg != null)
                {
                    bg.Mode = (BackgroundMode)env.backgroundMode;
                    bg.ColorA = env.backgroundColorA;
                    bg.ColorB = env.backgroundColorB;
                    bg.GradientBias = env.gradientBias;
                }

                // Skipped entirely when the file was saved without a Volume - otherwise loading
                // such a file would stamp default zeros over a scene that does have one.
                var post = UnityEngine.Object.FindFirstObjectByType<PostProcessingController>();
                if (post != null && post.HasVolume && env.postAvailable)
                {
                    post.BloomEnabled = env.bloomEnabled;
                    post.BloomIntensity = env.bloomIntensity;
                    post.BloomThreshold = env.bloomThreshold;
                    post.VignetteEnabled = env.vignetteEnabled;
                    post.VignetteIntensity = env.vignetteIntensity;
                    post.VignetteSmoothness = env.vignetteSmoothness;
                    post.DofEnabled = env.dofEnabled;
                    post.DofFocusDistance = env.dofFocusDistance;
                    post.DofAperture = env.dofAperture;
                    post.ColorAdjustmentsEnabled = env.colorAdjustmentsEnabled;
                    post.Saturation = env.saturation;
                    post.Contrast = env.contrast;
                }
            }

            if (data.camera != null && data.camera.valid)
            {
                var orbit = UnityEngine.Object.FindFirstObjectByType<CameraOrbitController>();
                if (orbit != null)
                {
                    // Projection first: SetView derives the orthographic size from the distance
                    // it is given, so restoring the angles into the wrong projection would frame
                    // the subject at whatever size the previous projection left behind.
                    orbit.Orthographic = data.camera.orthographic;
                    orbit.SetView(data.camera.yaw, data.camera.pitch, data.camera.distance, data.camera.pivot);
                }
            }
        }

        // --------------------------------------------------------------------------- helpers

        /// Accepts a bare filename, a relative path, or a full path, and supplies the extension
        /// if missing - so the UI's path field is forgiving about what the user types.
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return DefaultPath;
            path = path.Trim().Trim('"');
            if (!Path.IsPathRooted(path)) path = Path.Combine(DefaultDirectory, path);
            if (!path.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase)) path += FileExtension;
            return path;
        }

        private static bool MagicMatches(byte[] candidate)
        {
            if (candidate == null || candidate.Length != Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++) if (candidate[i] != Magic[i]) return false;
            return true;
        }

        private static bool HasAnyMask(float[] mask)
        {
            for (int i = 0; i < mask.Length; i++) if (mask[i] > 0f) return true;
            return false;
        }

        /// Guards against a normals array that doesn't match the vertex count. Unity's own
        /// Mesh.normals returns an EMPTY array (not null, not a zero-filled one) for a mesh with
        /// no normals, which would otherwise write a zero-length block the reader then expects
        /// to be vertexCount long.
        private static Vector3[] NormalizeLength(Vector3[] normals, int vertexCount)
        {
            if (normals != null && normals.Length == vertexCount) return normals;
            var padded = new Vector3[vertexCount];
            if (normals != null) Array.Copy(normals, padded, Math.Min(normals.Length, vertexCount));
            return padded;
        }

        private static byte[] Vector3ArrayToBytes(Vector3[] a)
        {
            var floats = new float[a.Length * 3];
            for (int i = 0; i < a.Length; i++)
            {
                floats[i * 3] = a[i].x;
                floats[i * 3 + 1] = a[i].y;
                floats[i * 3 + 2] = a[i].z;
            }
            return FloatArrayToBytes(floats);
        }

        private static byte[] FloatArrayToBytes(float[] a)
        {
            var bytes = new byte[a.Length * sizeof(float)];
            Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static byte[] IntArrayToBytes(int[] a)
        {
            var bytes = new byte[a.Length * sizeof(int)];
            Buffer.BlockCopy(a, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        // ReadBytes can legitimately return fewer bytes than asked for at end-of-file; every
        // reader below turns that into the EndOfStreamException Load() reports as "truncated".
        private static byte[] ReadExactly(BinaryReader r, int byteCount)
        {
            if (byteCount < 0) throw new EndOfStreamException();
            byte[] bytes = r.ReadBytes(byteCount);
            if (bytes.Length != byteCount) throw new EndOfStreamException();
            return bytes;
        }

        private static float[] ReadFloatArray(BinaryReader r, int count)
        {
            byte[] bytes = ReadExactly(r, count * sizeof(float));
            var a = new float[count];
            Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length);
            return a;
        }

        private static int[] ReadIntArray(BinaryReader r, int count)
        {
            byte[] bytes = ReadExactly(r, count * sizeof(int));
            var a = new int[count];
            Buffer.BlockCopy(bytes, 0, a, 0, bytes.Length);
            return a;
        }

        private static Vector3[] ReadVector3Array(BinaryReader r, int count)
        {
            float[] floats = ReadFloatArray(r, count * 3);
            var a = new Vector3[count];
            for (int i = 0; i < count; i++)
                a[i] = new Vector3(floats[i * 3], floats[i * 3 + 1], floats[i * 3 + 2]);
            return a;
        }
    }
}
