using System;
using System.Collections.Generic;
using UnityEngine;

namespace Sculpting.IO
{
    /// JsonUtility-serializable description of a whole scene, minus geometry. This is the part
    /// of a .sculpt file that is human-readable and version-tolerant: JsonUtility silently
    /// ignores fields it doesn't recognise and leaves missing ones at their C# defaults, so
    /// adding a setting here stays backward AND forward compatible without touching
    /// SceneSerializer's format version. The bulk data (vertices/normals/triangles/mask) is
    /// deliberately NOT here - see SceneSerializer's format remarks for why it's raw binary.
    [Serializable]
    public class SculptSaveData
    {
        // Stamped for diagnostics only - nothing branches on it. SceneSerializer.FormatVersion
        // is the field that actually gates parsing.
        public string savedUtc;
        public string unityVersion;

        public SculptController.Settings brush;
        public MaterialSettings material = new MaterialSettings();
        public EnvironmentSettings environment = new EnvironmentSettings();
        public CameraSettings camera = new CameraSettings();

        public List<ObjectEntry> objects = new List<ObjectEntry>();

        /// Index into `objects` of the object that was the primary selection. That object is
        /// the Join survivor and the owner of the mirror/symmetry plane (see
        /// SelectionManager.Select), so it is genuinely part of the document, not just UI state.
        public int primarySelectionIndex = -1;

        [Serializable]
        public class ObjectEntry
        {
            public string name;

            public Vector3 position;
            public Quaternion rotation;
            public Vector3 scale;

            public bool visible = true;

            public bool mirrorX, mirrorY, mirrorZ;
            public bool showMirrorPlanes = true;

            // Sizes of this object's binary block, in element counts (not bytes). Read back to
            // slice the geometry section - see SceneSerializer.
            public int vertexCount;
            public int triangleIndexCount;

            /// False when the mesh had no per-vertex mask worth storing, so an unmasked object
            /// costs no mask block at all. Checked instead of inferring from vertexCount.
            public bool hasMask;

            /// Set only on the ORIGINAL half of a live mirror pair (see MirrorLink): the index into
            /// `objects` of its twin, and the plane the two mirror across. The twin's own entry
            /// records nothing, so a load can never form the same pair twice.
            public bool mirrorLinked;
            public int mirrorLinkTwin;
            public Vector3 mirrorLinkCenter;
            public Vector3 mirrorLinkSigns;
        }

        [Serializable]
        public class MaterialSettings
        {
            public Color baseColor = Color.grey;
            public float metallic;
            public float smoothness = 0.4f;
            public float normalStrength;
            public float normalNoiseScale = 60f;
            public bool flatShading;
            // Screen-space cavity (Blender-style ridge/valley). Older files carry the retired
            // per-vertex tint's cavityEnabled/recessColor/cavityIntensity/cavityRange (and, older
            // still, peakColor); JsonUtility drops keys it has no member for, so those files load
            // with these defaults instead of carrying an unrelated on/off state across.
            public bool screenCavityEnabled = true;
            public float cavityRidge = 1f;
            public float cavityValley = 1f;

            // Matcap by file name rather than by path: the image lives in the app's own Matcaps
            // folder, so a name still resolves on a machine where the app is installed somewhere
            // else. A name this machine doesn't have falls back to lit shading (see
            // SculptMaterialController.MatcapName).
            public bool matcapEnabled;
            public string matcapName = string.Empty;
            public float matcapIntensity = 1f;
            public float matcapTintStrength;

            // Lure plastic by preset id (LurePlasticPresets). Older files have none of these and
            // load with it off. lureReferenceSize keeps the flakes exactly where they were; 0
            // re-measures the model.
            public bool lureEnabled;
            public string lurePresetId = string.Empty;
            public float lureFlakeSize = 1f;
            public float lureFlakeAmount = 1f;
            public float lureSparkle = 1f;
            public float lureTranslucency = 1f;
            public float lureGloss = 0.8f;
            public float lureReferenceSize;
        }

        [Serializable]
        public class EnvironmentSettings
        {
            // Lighting preset (see LightingPresetController). Files from before presets carry the
            // retired studio rig's studioLightingEnabled/lightingMode instead; JsonUtility drops
            // those, and an empty id resolves to the default preset.
            public string lightingPreset = string.Empty;
            public bool lightingFivePoint;
            public float lightingBrightness = 1f;
            public float lightingRotation;
            public bool lightingFollowCamera = true;
            public float lightingWorldYaw;
            public bool lightingShadows = true;

            public int backgroundMode;
            public Color backgroundColorA = Color.black;
            public Color backgroundColorB = Color.grey;
            public float gradientBias = 1f;

            // HDRI environment. The image itself is referenced by absolute path rather than
            // embedded: an HDRI is tens of megabytes and would dwarf the sculpt it is lighting,
            // and the same file is normally shared across every scene the user opens. A save
            // made on another machine therefore loads with its lighting settings intact and a
            // "not found" note where the image should be, rather than failing.
            public bool hdriEnabled;
            public string hdriPath = string.Empty;
            public float hdriRotation;
            // Defaulted to 1 rather than 0 so a file written before HDRI support existed loads
            // with sane values instead of a black environment the moment it is switched on.
            public float hdriExposure = 1f;
            public float hdriAmbientIntensity = 1f;
            public float hdriReflectionIntensity = 1f;

            // Post-processing. `postAvailable` records whether the saving scene actually had a
            // Volume - loading a file saved without one must not stamp zeros over a scene that
            // does have one (see PostProcessingController.HasVolume).
            public bool postAvailable;
            public bool bloomEnabled;
            public float bloomIntensity, bloomThreshold;
            public bool vignetteEnabled;
            public float vignetteIntensity, vignetteSmoothness;
            public bool dofEnabled;
            public float dofFocusDistance, dofAperture;
            public bool colorAdjustmentsEnabled;
            public float saturation, contrast;
        }

        [Serializable]
        public class CameraSettings
        {
            public bool valid;
            public float yaw, pitch, distance;
            public Vector3 pivot;
            // Added after the first files were written, so it defaults to false and older saves
            // load as perspective - which is what they were saved from.
            public bool orthographic;
        }
    }
}
