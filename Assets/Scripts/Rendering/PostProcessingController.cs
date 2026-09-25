using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Sculpting.IO;

namespace Sculpting
{
    /// Thin wrapper around the scene's URP Global Volume profile so the UI can toggle and
    /// tune the post-processing effects used to show the sculpt off (bloom, vignette, depth
    /// of field, color grading, tonemapping) without touching the Volume asset by hand. Adds
    /// DepthOfField/ColorAdjustments overrides on demand if the profile doesn't already have
    /// them - Bloom/Vignette/Tonemapping are expected to already exist (see
    /// Assets/Settings/SampleSceneProfile.asset).
    public class PostProcessingController : MonoBehaviour
    {
        private Volume _volume;
        private Bloom _bloom;
        private Vignette _vignette;
        private Tonemapping _tonemapping;
        private DepthOfField _dof;
        private ColorAdjustments _colorAdjustments;

        public bool HasVolume => _volume != null && _volume.profile != null;

        private void Awake()
        {
            _volume = FindFirstObjectByType<Volume>();
            if (_volume == null || _volume.profile == null) return;
            VolumeProfile profile = _volume.profile;

            profile.TryGet(out _bloom);
            profile.TryGet(out _vignette);
            profile.TryGet(out _tonemapping);
            if (!profile.TryGet(out _dof)) _dof = profile.Add<DepthOfField>(true);
            if (!profile.TryGet(out _colorAdjustments)) _colorAdjustments = profile.Add<ColorAdjustments>(true);

            if (_dof != null)
            {
                _dof.mode.overrideState = true;
                _dof.mode.value = DepthOfFieldMode.Bokeh;
                _dof.active = false;
            }
            if (_colorAdjustments != null) _colorAdjustments.active = false;
        }

        public bool BloomEnabled { get => _bloom != null && _bloom.active; set { if (_bloom != null) _bloom.active = value; } }
        public float BloomIntensity
        {
            get => _bloom != null ? _bloom.intensity.value : 0f;
            set { if (_bloom != null) { _bloom.intensity.overrideState = true; _bloom.intensity.value = value; } }
        }
        public float BloomThreshold
        {
            get => _bloom != null ? _bloom.threshold.value : 0f;
            set { if (_bloom != null) { _bloom.threshold.overrideState = true; _bloom.threshold.value = value; } }
        }

        public bool VignetteEnabled { get => _vignette != null && _vignette.active; set { if (_vignette != null) _vignette.active = value; } }
        public float VignetteIntensity
        {
            get => _vignette != null ? _vignette.intensity.value : 0f;
            set { if (_vignette != null) { _vignette.intensity.overrideState = true; _vignette.intensity.value = value; } }
        }
        public float VignetteSmoothness
        {
            get => _vignette != null ? _vignette.smoothness.value : 0f;
            set { if (_vignette != null) { _vignette.smoothness.overrideState = true; _vignette.smoothness.value = value; } }
        }

        public bool DofEnabled { get => _dof != null && _dof.active; set { if (_dof != null) _dof.active = value; } }
        public float DofFocusDistance
        {
            get => _dof != null ? _dof.focusDistance.value : 2f;
            set { if (_dof != null) { _dof.focusDistance.overrideState = true; _dof.focusDistance.value = Mathf.Max(0.05f, value); } }
        }
        public float DofAperture
        {
            get => _dof != null ? _dof.aperture.value : 5.6f;
            set { if (_dof != null) { _dof.aperture.overrideState = true; _dof.aperture.value = value; } }
        }

        public bool ColorAdjustmentsEnabled { get => _colorAdjustments != null && _colorAdjustments.active; set { if (_colorAdjustments != null) _colorAdjustments.active = value; } }
        public float Saturation
        {
            get => _colorAdjustments != null ? _colorAdjustments.saturation.value : 0f;
            set { if (_colorAdjustments != null) { _colorAdjustments.saturation.overrideState = true; _colorAdjustments.saturation.value = value; } }
        }
        public float Contrast
        {
            get => _colorAdjustments != null ? _colorAdjustments.contrast.value : 0f;
            set { if (_colorAdjustments != null) { _colorAdjustments.contrast.overrideState = true; _colorAdjustments.contrast.value = value; } }
        }

        public TonemappingMode CurrentTonemappingMode
        {
            get => _tonemapping != null ? _tonemapping.mode.value : TonemappingMode.None;
            set { if (_tonemapping != null) { _tonemapping.mode.overrideState = true; _tonemapping.mode.value = value; } }
        }

        /// Writes this controller's part of a save file's environment block (see SceneSerializer).
        /// Nothing without a Volume: postAvailable then stays false, and a load of the file leaves
        /// the post settings alone.
        public void CaptureEnvironment(SculptSaveData.EnvironmentSettings env)
        {
            if (!HasVolume) return;
            env.postAvailable = true;
            env.bloomEnabled = BloomEnabled;
            env.bloomIntensity = BloomIntensity;
            env.bloomThreshold = BloomThreshold;
            env.vignetteEnabled = VignetteEnabled;
            env.vignetteIntensity = VignetteIntensity;
            env.vignetteSmoothness = VignetteSmoothness;
            env.dofEnabled = DofEnabled;
            env.dofFocusDistance = DofFocusDistance;
            env.dofAperture = DofAperture;
            env.colorAdjustmentsEnabled = ColorAdjustmentsEnabled;
            env.saturation = Saturation;
            env.contrast = Contrast;
        }

        /// Restores this controller's part of a save file's environment block. Skipped entirely
        /// when the file was saved without a Volume - otherwise loading such a file would stamp
        /// default zeros over a scene that does have one.
        public void ApplyEnvironment(SculptSaveData.EnvironmentSettings env)
        {
            if (!HasVolume || !env.postAvailable) return;
            BloomEnabled = env.bloomEnabled;
            BloomIntensity = env.bloomIntensity;
            BloomThreshold = env.bloomThreshold;
            VignetteEnabled = env.vignetteEnabled;
            VignetteIntensity = env.vignetteIntensity;
            VignetteSmoothness = env.vignetteSmoothness;
            DofEnabled = env.dofEnabled;
            DofFocusDistance = env.dofFocusDistance;
            DofAperture = env.dofAperture;
            ColorAdjustmentsEnabled = env.colorAdjustmentsEnabled;
            Saturation = env.saturation;
            Contrast = env.contrast;
        }
    }
}
