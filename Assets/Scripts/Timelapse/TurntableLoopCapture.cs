using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Sculpting
{
    /// Output container for a 360 loop. MP4 needs the editor's encoder (see LoopVideoSink);
    /// a PNG sequence works everywhere, including a standalone build.
    public enum TurntableLoopFormat
    {
        Mp4,
        PngSequence
    }

    /// What a 360 loop recording is asked to produce. Filled in by TurntableController from
    /// the turntable's own speed, so the file spins at exactly the rate the viewport did.
    public struct TurntableLoopSettings
    {
        public int width;
        public int height;
        public int frameRate;
        /// Frames in ONE revolution. The loop is frames 0..frameCount-1 at yaw
        /// start + 360 * i / frameCount - frame `frameCount` would be frame 0 again, so it is
        /// never rendered, and playback wraps from the last frame to the first with exactly one
        /// step of rotation between them, the same as between any other two frames.
        public int frameCount;
        public TurntableLoopFormat format;
        public string outputFolder;
    }

    /// The editor half of MP4 output, registered by TimelapseRecorderService at editor load.
    ///
    /// Unity's video encoder (UnityEditor.Media.MediaEncoder) is editor-only, so the runtime
    /// cannot call it and a standalone build has nothing registered here - TurntableController
    /// falls back to a PNG sequence in that case rather than offering a button that can't work.
    ///
    /// MediaEncoder rather than the Unity Recorder the timelapse uses, on purpose: a perfect
    /// loop needs EXACTLY frameCount frames in the file, and Recorder decides for itself which
    /// frames it grabs (the timelapse measured 120 in the file for 121 gated open). Here every
    /// frame is handed over explicitly, so the count is whatever this class was given.
    public static class LoopVideoSink
    {
        /// Opens an encoder and returns the path of the file it will write, or null on failure.
        public static Func<TurntableLoopSettings, string> Begin;
        public static Action<Texture2D> AddFrame;
        /// `completed` false means the recording was cancelled; the sink deletes the partial file.
        public static Action<bool> End;

        public static bool Available => Begin != null && AddFrame != null && End != null;
    }

    /// Renders one full revolution of the app's camera into a texture at the output
    /// resolution, one frame per app frame, and hands each frame to the chosen output.
    ///
    /// Owned and stepped by TurntableController, which drives the orbit rig to each frame's
    /// exact yaw. This component only renders what the app camera sees from there - a separate
    /// camera rather than a screen grab so the resolution and aspect are the file's, not the
    /// window's, and so overlay UI can never be in it (ScreenSpaceOverlay canvases don't reach a
    /// render texture at all).
    ///
    /// The frame handshake is: TurntableController sets frame i's pose in Update and calls
    /// RenderFrame; the camera renders it at the end of that frame; the NEXT Update calls
    /// CollectFrame, which reads it back. Reading back a frame late costs nothing and avoids
    /// having to hook the render pipeline's end-of-camera callbacks.
    public class TurntableLoopCapture : MonoBehaviour
    {
        public TurntableLoopSettings Settings { get; private set; }

        /// The file (MP4) or folder (PNG sequence) being written.
        public string OutputPath { get; private set; }

        public int FramesWritten { get; private set; }

        private Camera _camera;
        private Camera _source;
        private RenderTexture _output;
        private Texture2D _readback;
        private bool _pending;
        private bool _videoOpen;

        public static TurntableLoopCapture Create(TurntableLoopSettings settings, Camera source)
        {
            var go = new GameObject("Turntable Loop Capture") { hideFlags = HideFlags.DontSave };
            var capture = go.AddComponent<TurntableLoopCapture>();
            if (!capture.Build(settings, source))
            {
                Destroy(go);
                return null;
            }
            return capture;
        }

        private bool Build(TurntableLoopSettings settings, Camera source)
        {
            // H.264 refuses odd dimensions.
            settings.width = Mathf.Max(64, settings.width) & ~1;
            settings.height = Mathf.Max(64, settings.height) & ~1;
            settings.frameRate = Mathf.Max(1, settings.frameRate);
            settings.frameCount = Mathf.Max(2, settings.frameCount);
            if (settings.format == TurntableLoopFormat.Mp4 && !LoopVideoSink.Available)
                settings.format = TurntableLoopFormat.PngSequence;
            Settings = settings;
            _source = source;

            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            try
            {
                Directory.CreateDirectory(settings.outputFolder);
                if (settings.format == TurntableLoopFormat.Mp4)
                {
                    OutputPath = LoopVideoSink.Begin(settings);
                    if (string.IsNullOrEmpty(OutputPath)) return false;
                    _videoOpen = true;
                }
                else
                {
                    OutputPath = Path.Combine(settings.outputFolder, $"Turntable_{stamp}");
                    Directory.CreateDirectory(OutputPath);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[Turntable] Could not open the output: {e.Message}");
                return false;
            }

            // sRGB so the bytes read back are already display-encoded, which is what both the
            // H.264 encoder and a PNG expect.
            _output = new RenderTexture(settings.width, settings.height, 24, RenderTextureFormat.ARGB32,
                RenderTextureReadWrite.sRGB)
            {
                name = "Turntable Loop Output",
                // The app camera's own AA (copied below) still applies; this is the texture's MSAA,
                // which the readback would otherwise have to resolve.
                antiAliasing = 1
            };
            _output.Create();
            _readback = new Texture2D(settings.width, settings.height, TextureFormat.RGBA32, false, false);

            _camera = gameObject.AddComponent<Camera>();
            if (source != null)
            {
                _camera.CopyFrom(source);
                CopyUrpSettings(source, _camera);
            }
            _camera.targetTexture = _output;
            _camera.rect = new Rect(0f, 0f, 1f, 1f);
            _camera.ResetAspect();
            _camera.enabled = false;
            return true;
        }

        private static void CopyUrpSettings(Camera source, Camera destination)
        {
            UniversalAdditionalCameraData from = source.GetUniversalAdditionalCameraData();
            UniversalAdditionalCameraData to = destination.GetUniversalAdditionalCameraData();
            if (from == null || to == null) return;

            // Base, never Overlay - see TimelapseRig.CopyUrpSettings.
            to.renderType = CameraRenderType.Base;
            to.renderPostProcessing = from.renderPostProcessing;
            to.antialiasing = from.antialiasing;
            to.antialiasingQuality = from.antialiasingQuality;
            to.volumeLayerMask = from.volumeLayerMask;
            to.volumeTrigger = from.volumeTrigger;
            to.renderShadows = from.renderShadows;
        }

        /// Renders the app camera's CURRENT pose this frame. Call after the orbit rig has been
        /// placed for the frame.
        public void RenderFrame()
        {
            MatchSourceCamera();
            _camera.enabled = true;
            _pending = true;
        }

        /// Reads back the frame RenderFrame asked for last frame and writes it out. Returns false
        /// if the output failed, in which case the caller should cancel.
        public bool CollectFrame()
        {
            _camera.enabled = false;
            if (!_pending) return true;
            _pending = false;

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _output;
            _readback.ReadPixels(new Rect(0, 0, _output.width, _output.height), 0, 0, false);
            _readback.Apply(false);
            RenderTexture.active = previous;

            try
            {
                if (Settings.format == TurntableLoopFormat.Mp4)
                    LoopVideoSink.AddFrame(_readback);
                else
                    File.WriteAllBytes(Path.Combine(OutputPath, $"frame_{FramesWritten:0000}.png"),
                        _readback.EncodeToPNG());
            }
            catch (Exception e)
            {
                Debug.LogError($"[Turntable] Writing frame {FramesWritten} failed: {e.Message}");
                return false;
            }

            FramesWritten++;
            return true;
        }

        /// Finalises (completed) or abandons (cancelled) the output.
        public void Finish(bool completed)
        {
            if (_videoOpen)
            {
                _videoOpen = false;
                LoopVideoSink.End(completed);
            }
            else if (!completed && !string.IsNullOrEmpty(OutputPath) && Directory.Exists(OutputPath))
            {
                // A partial PNG sequence is not a loop, and leaving it behind invites someone to
                // import it as one.
                try { Directory.Delete(OutputPath, true); } catch (Exception) { }
            }
        }

        /// Pose, projection and clip planes from the app camera, with the field of view widened
        /// when the output is narrower than the window - so a square or portrait file never
        /// crops away what the viewport was showing at the sides. When the output is wider, the
        /// vertical framing is kept, and the extra width is simply more of the background.
        private void MatchSourceCamera()
        {
            if (_source == null) return;

            transform.SetPositionAndRotation(_source.transform.position, _source.transform.rotation);
            _camera.orthographic = _source.orthographic;
            _camera.nearClipPlane = _source.nearClipPlane;
            _camera.farClipPlane = _source.farClipPlane;

            float sourceAspect = Mathf.Max(0.01f, _source.aspect);
            float outputAspect = (float)_output.width / _output.height;
            float widen = outputAspect < sourceAspect ? sourceAspect / outputAspect : 1f;

            if (_source.orthographic)
            {
                _camera.orthographicSize = _source.orthographicSize * widen;
            }
            else
            {
                float halfV = _source.fieldOfView * 0.5f * Mathf.Deg2Rad;
                _camera.fieldOfView = 2f * Mathf.Atan(Mathf.Tan(halfV) * widen) * Mathf.Rad2Deg;
            }
        }

        private void OnDestroy()
        {
            // Destroyed without Finish (scene unload, leaving Play) is a cancellation.
            if (_videoOpen) Finish(false);

            if (_camera != null) _camera.targetTexture = null;
            if (_output != null)
            {
                _output.Release();
                Destroy(_output);
                _output = null;
            }
            if (_readback != null)
            {
                Destroy(_readback);
                _readback = null;
            }
        }
    }
}
