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
            public bool cavityEnabled;
            public Color recessColor = Color.black;
            public Color peakColor = Color.white;
            public float cavityIntensity = 1f;
            public float cavityRange = 0.25f;
        }

        [Serializable]
        public class EnvironmentSettings
        {
            public bool studioLightingEnabled = true;
            public int lightingMode;

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
