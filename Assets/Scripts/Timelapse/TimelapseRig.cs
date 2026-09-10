using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Sculpting
{
    /// The runtime half of the timelapse feature: a private orbit camera rendering into a render
    /// texture, plus the once-per-frame decision of whether this frame belongs in the video.
    ///
    /// It is a separate camera on purpose even though the default framing now FOLLOWS the app's
    /// camera. Rendering through the real one would tie the video to the game view's resolution
    /// and aspect, put the tool panels in the frame, and - the reason that matters most - hand
    /// the file the artist's raw input: every nudge, every half-orbit while they think, all of it
    /// compressed eight or twenty times over. This camera copies the main camera's LOOK (field of
    /// view, clear flags, culling, post-processing) and takes its POSE from the artist's orbit
    /// state through a spring stepped in video time, which is what turns that input into a shot.
    ///
    /// Ordering matters and is why this runs at execution order 1000. Everything here happens in
    /// Update: it must see the sculpting SculptController already did THIS frame (default order,
    /// so earlier), and it must publish the capture decision before Unity Recorder's own
    /// component reads it in LateUpdate. Update-after-Update, then LateUpdate, gets both.
    ///
    /// Created and owned by the editor window (see SculptTimelapseWindow) - there is no reason
    /// for this to sit in the scene when nothing is recording, and it is marked DontSave so it
    /// can never be saved into one by accident.
    [DefaultExecutionOrder(1000)]
    public class TimelapseRig : MonoBehaviour
    {
        public TimelapseOptions Options = new TimelapseOptions();

        /// Called every frame with this frame's capture decision. The editor window puts the
        /// Recorder's frame gate behind it; nothing here knows the Recorder exists, which is what
        /// lets this whole file live in the runtime assembly.
        public System.Action<bool> GateCapture;

        /// What the Recorder is pointed at.
        public RenderTexture Output => _output;

        /// Frames actually written to the video so far. Divided by frameRate, this is the length
        /// of the finished file.
        public int CapturedFrames { get; private set; }

        /// True while the model is changing (or within the idle grace period of having changed).
        public bool IsSculpting { get; private set; }

        /// Wall-clock seconds spent sculpting since the recording started - i.e. how much of the
        /// session survived the activity gate.
        public float SculptingSeconds => _sculptingSeconds;

        private Camera _camera;
        private RenderTexture _output;
        private SelectionManager _selection;
        private CameraOrbitController _artistRig;

        private float _sculptingSeconds;
        private float _nextCaptureAt;

        private float _yaw;
        private Vector3 _pivot;
        private float _distance;
        private float _pitch;
        private bool _framed;

        // Critically-damped spring state for the three values that chase a moving target.
        // SmoothDamp rather than a lerp because a lerp restarts its ease on every new target and
        // reads as a series of little lunges; a spring carries its velocity across, which is what
        // makes a camera following a growing model look like one continuous move.
        private Vector3 _pivotVelocity;
        private float _distanceVelocity;
        private float _pitchVelocity;
        private float _yawVelocity;

        /// Clearing out rigs left over from a previous recording is TimelapseRecorderService's
        /// job, not this method's - see its DestroyStrayRigs. It has to be: an orphaned rig has
        /// no scene at all, which from runtime code is indistinguishable from a prefab asset, and
        /// telling those apart needs EditorUtility.IsPersistent.
        public static TimelapseRig Create(TimelapseOptions options)
        {
            var go = new GameObject("Timelapse Rig") { hideFlags = HideFlags.DontSave };
            var rig = go.AddComponent<TimelapseRig>();
            rig.Options = options;
            rig.Build();
            return rig;
        }

        /// Deliberately not Awake: AddComponent runs Awake before Create can hand over Options,
        /// and every dimension below comes from those.
        private void Build()
        {
            int width = EvenAtLeast(Options.width, 320);
            int height = EvenAtLeast(Options.height, 240);

            // sRGB rather than the linear default: the encoder wants sRGB, and handing Recorder a
            // linear texture just makes it blit a conversion pass in front of every frame.
            _output = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)
            {
                name = "Timelapse Output",
                antiAliasing = 1
            };
            // RenderTextureInput errors out on a texture that was never created, and nothing else
            // here forces creation before the first captured frame.
            _output.Create();

            _camera = gameObject.AddComponent<Camera>();
            Camera source = ResolveSourceCamera();
            if (source != null)
            {
                // Copies clear flags, background/skybox, field of view, clip planes and culling -
                // i.e. makes the timelapse look like the app rather than like an empty scene.
                _camera.CopyFrom(source);
                CopyUrpSettings(source, _camera);
            }

            // After CopyFrom, which would otherwise have copied the source camera's own target
            // (usually none, meaning "draw to the screen" - which would put this camera's output
            // on top of the app's).
            _camera.targetTexture = _output;
            _camera.rect = new Rect(0f, 0f, 1f, 1f);

            if (Options.hideUiLayer)
            {
                int ui = LayerMask.NameToLayer("UI");
                if (ui >= 0) _camera.cullingMask &= ~(1 << ui);
            }

            // Only ever on for a frame that is being captured - see Update. A turntable camera
            // rendering a million-triangle sculpt on every frame of a session it mostly isn't
            // recording is a straight tax on the sculpting itself.
            _camera.enabled = false;
        }

        private static Camera ResolveSourceCamera()
        {
            if (Camera.main != null) return Camera.main;
            var orbit = FindFirstObjectByType<CameraOrbitController>();
            if (orbit != null)
            {
                var cam = orbit.GetComponent<Camera>();
                if (cam != null) return cam;
            }
            return FindFirstObjectByType<Camera>();
        }

        private static void CopyUrpSettings(Camera source, Camera destination)
        {
            UniversalAdditionalCameraData from = source.GetUniversalAdditionalCameraData();
            UniversalAdditionalCameraData to = destination.GetUniversalAdditionalCameraData();
            if (from == null || to == null) return;

            // Base, never Overlay: an overlay camera renders into another camera's stack and
            // would draw nothing at all on its own render texture.
            to.renderType = CameraRenderType.Base;
            to.renderPostProcessing = from.renderPostProcessing;
            to.antialiasing = from.antialiasing;
            to.antialiasingQuality = from.antialiasingQuality;
            to.volumeLayerMask = from.volumeLayerMask;
            to.volumeTrigger = from.volumeTrigger;
            to.renderShadows = from.renderShadows;
            // Not copied: which of the pipeline asset's renderers to use. URP exposes a setter
            // for it but no getter, so there is nothing to read off the source camera - this one
            // takes the pipeline's default, which is what the app's camera uses too unless
            // someone has deliberately pointed it at a second renderer.
        }

        private void Update()
        {
            if (_camera == null) return;

            CorrectCaptureDeltaTime();

            float dt = Time.unscaledDeltaTime;

            // The whole activity rule, in one line: something changed within the grace window.
            // Undo and redo never get here - SculptActivity suppresses them at the source.
            IsSculpting = SculptActivity.SecondsSinceLastEdit <= Mathf.Max(0f, Options.idleGraceSeconds);

            bool capture = false;
            if (IsSculpting)
            {
                _sculptingSeconds += dt;
                if (_sculptingSeconds >= _nextCaptureAt)
                {
                    capture = true;
                    float interval = CaptureIntervalSeconds();

                    // Advanced from the previous TARGET, not from now. Scheduling from now adds
                    // however long the frame took to every interval, which at low speeds is most
                    // of the interval - a 60fps app at speed 1 would capture every 50ms instead
                    // of every 33ms and play back half again too fast.
                    _nextCaptureAt += interval;

                    // ...but a long stall (a heavy Remesh) must not leave an unbounded backlog
                    // behind it. Resuming at most one interval back means what follows is
                    // "capture the next frame or two" rather than "capture every frame for as
                    // long as the stall lasted". Only one frame is ever captured per Update, so
                    // this can't produce a burst either way.
                    _nextCaptureAt = Mathf.Max(_nextCaptureAt, _sculptingSeconds - interval);
                }
            }

            // Camera work happens ONLY on captured frames, and advances by exactly one frame of
            // VIDEO time each time. This is the whole reason the motion comes out smooth.
            //
            // Chasing the subject in real time instead looks fine live and jerks badly in the
            // file: a re-frame that takes a comfortable half-second of wall clock is compressed
            // by the timelapse speed along with everything else, so at 8x it lands inside two
            // video frames and reads as a snap. Stepping the springs once per captured frame by
            // 1/frameRate makes the camera's easing a property of the video, identical whether
            // the app was running at 30fps or 400, and unaffected by the speed setting.
            //
            // The frames in between are not rendered (the camera is off), so there is nothing to
            // be gained from moving it on them either.
            if (capture)
            {
                CapturedFrames++;
                UpdateFraming(1f / Mathf.Max(1f, Options.frameRate));
            }

            // Both of these have to be settled before any LateUpdate runs: the Recorder's own
            // component asks for the frame there, and URP renders enabled cameras after that.
            _camera.enabled = capture;
            GateCapture?.Invoke(capture);
        }

        /// Hands the app back its real clock while recording.
        ///
        /// Recorder's constant-frame-rate mode pins Time.captureDeltaTime to 1/frameRate, and
        /// that pins Time.deltaTime with it. Which is correct only if the app is ALSO running at
        /// frameRate - exactly what "Cap App To Video FPS" buys, by throttling a 290fps app down
        /// to 30. With the cap off the app stays fast but its clock does not: measured at 7.3x
        /// too fast, so everything paced off deltaTime - brush stroke speed above all - behaves
        /// as though the user were stroking seven times quicker than they really are.
        ///
        /// Writing the real frame time back each frame fixes that without the throttle, giving
        /// both the framerate and honest pacing. Measured: 291fps idle, 29.9 recording with the
        /// cap, 220 recording without it.
        ///
        /// Safe for the video, because constant-rate mode counts FRAMES rather than reading
        /// timestamps (MovieRecorder.ComputeMediaTime returns an invalid MediaTime there), so the
        /// file comes out identical either way. And no feedback loop: Time.unscaledDeltaTime
        /// stays REAL elapsed time even while captureDeltaTime is pinned - verified directly,
        /// 0.0043 against a pinned 0.0333.
        ///
        /// Recorder writes this value too, and the overlap is benign both ways. Its
        /// PrepareNewFrame only resets captureDeltaTime when it finds a value >= the frame
        /// interval, which ours is only when the app is running at or below the video rate - and
        /// there the interval IS the honest answer. Recorder zeroes captureFramerate when the
        /// session ends, so nothing needs restoring here. If a future Recorder version does start
        /// fighting over it, the failure mode is simply the distorted pacing we had before.
        private void CorrectCaptureDeltaTime()
        {
            // With the cap on, the user has asked for lockstep and the throttle already delivers
            // it - real frame time and the interval are the same thing.
            if (Options.capFrameRate) return;

            // Clamped because the first frame after PrepareRecording is a long one (encoder
            // setup, render texture allocation) and handing that to deltaTime would jolt every
            // dt-paced thing in the app on the frame recording starts.
            float real = Time.unscaledDeltaTime;
            if (real > 0f) Time.captureDeltaTime = Mathf.Clamp(real, 0.001f, 0.1f);
        }

        /// Seconds of sculpting between captured frames. speed=1 at 30fps captures every 33ms of
        /// activity (real time); speed=8 captures every 267ms (eight times faster). Floored so a
        /// silly speed can't ask for a capture every frame forever.
        private float CaptureIntervalSeconds() =>
            Mathf.Max(1f / 240f, Mathf.Max(0.01f, Options.speed) / Mathf.Max(1f, Options.frameRate));

        /// `videoDeltaTime` is one frame of finished video, not one frame of wall clock - see the
        /// remarks at the call site.
        private void UpdateFraming(float videoDeltaTime)
        {
            // Zero smoothing means "snap", which SmoothDamp cannot express (it divides by the
            // smooth time), so it is floored just above zero and the springs handle the rest.
            float smoothing = Mathf.Max(0.001f, Options.cameraSmoothingSeconds);

            // The fallback is not decoration: a scene driven by something other than the orbit
            // rig still gets a usable video out of the turntable rather than a fixed stare.
            if (Options.framing == TimelapseFraming.FollowArtist && TryGetArtistView(out ArtistView view))
                FrameFollowingArtist(view, smoothing, videoDeltaTime);
            else
                FrameOrbit(smoothing, videoDeltaTime);
        }

        /// The app camera's orbit state, read once per captured frame.
        ///
        /// Taken from the CONTROLLER rather than off the camera transform because the controller
        /// keeps yaw, pitch, distance and pivot separately, and those four are the ones that can
        /// be smoothed sensibly. A spring on a world position cuts the corner on a fast orbit and
        /// drags the camera straight through the model; a spring on the angles takes the arc the
        /// artist took, just later and gentler.
        private struct ArtistView
        {
            public float yaw;
            public float pitch;
            public float distance;
            public Vector3 pivot;
            public bool orthographic;
        }

        private bool TryGetArtistView(out ArtistView view)
        {
            // Re-resolved whenever it goes missing rather than cached once: loading a scene
            // replaces the orbit rig, and a recording is expected to survive that.
            if (_artistRig == null) _artistRig = FindFirstObjectByType<CameraOrbitController>();
            if (_artistRig == null)
            {
                view = default;
                return false;
            }

            _artistRig.GetView(out float yaw, out float pitch, out float distance, out Vector3 pivot);
            view = new ArtistView
            {
                yaw = yaw,
                pitch = pitch,
                distance = distance,
                pivot = pivot,
                orthographic = _artistRig.Orthographic
            };
            return true;
        }

        /// Shoots the model from wherever the artist is looking at it from, leaning the framing
        /// towards the part they are actually working on. The default, and the ZBrush-style shot.
        ///
        /// The premise is that an artist orbits to face whatever they are sculpting, continuously
        /// and without being asked - so their viewing angle is already the best available answer
        /// to "where is the interesting thing right now". A turntable can only guess at that, and
        /// by construction spends as long behind the work as in front of it.
        ///
        /// Two things are then added on top of their view, and both are the point:
        ///
        /// SMOOTHING. This is emphatically not a mirror of their camera transform. Raw orbit
        /// input played back at 8x is unwatchable, and the small constant corrections of someone
        /// lining up a stroke turn into a permanent shake. Everything below goes through the same
        /// video-time springs the turntable uses, so a half-second re-frame stays a half-second
        /// re-frame in the FILE rather than being compressed along with everything else.
        ///
        /// CENTRING. Their view supplies the angle but not the subject: the pivot sits at the
        /// middle of the model while they detail an ear at the edge of the frame, and the detail
        /// is what the video is about. So the pivot is pulled from theirs towards the last place
        /// geometry actually moved - see SculptActivity.TryGetEditPoint, which reports the
        /// centroid of the brush footprint, not the cursor, so it is silent for a stroke that
        /// touched nothing.
        private void FrameFollowingArtist(ArtistView view, float smoothing, float videoDeltaTime)
        {
            Vector3 wantedPivot = view.pivot;
            if (Options.focusStrength > 0f)
            {
                // Bounds centre rather than their pivot as the fallback: before the first stroke
                // of a session there is no edit point, and the model's middle is the better guess
                // of the two (the pivot can have been panned anywhere).
                Vector3 focus;
                if (SculptActivity.TryGetEditPoint(out Vector3 editPoint)) focus = editPoint;
                else if (TryGetSubjectBounds(out Bounds bounds)) focus = bounds.center;
                else focus = view.pivot;

                wantedPivot = Vector3.Lerp(view.pivot, focus, Mathf.Clamp01(Options.focusStrength));
            }

            float wantedDistance = view.distance * Mathf.Max(0.2f, Options.focusZoom);

            // The short way round. The controller's yaw is unbounded and keeps accumulating as
            // the artist orbits, so easing from a yaw of 1000 towards a raw 20 would spin the
            // camera through most of three revolutions to arrive at the same place.
            float wantedYaw = _yaw + Mathf.DeltaAngle(_yaw, view.yaw);

            if (!_framed)
            {
                // The first captured frame establishes the shot rather than easing into it from
                // wherever an uninitialised transform happened to sit.
                _pivot = wantedPivot;
                _distance = wantedDistance;
                _yaw = wantedYaw;
                _pitch = view.pitch;
                _framed = true;
            }
            else
            {
                _pivot = Vector3.SmoothDamp(_pivot, wantedPivot, ref _pivotVelocity, smoothing, Mathf.Infinity, videoDeltaTime);
                _distance = Mathf.SmoothDamp(_distance, wantedDistance, ref _distanceVelocity, smoothing, Mathf.Infinity, videoDeltaTime);
                _yaw = Mathf.SmoothDamp(_yaw, wantedYaw, ref _yawVelocity, smoothing, Mathf.Infinity, videoDeltaTime);
                _pitch = Mathf.SmoothDamp(_pitch, view.pitch, ref _pitchVelocity, smoothing, Mathf.Infinity, videoDeltaTime);
            }

            PlaceCamera(view.orthographic);
        }

        /// The turntable: a constant orbit around the subject that ignores the app camera.
        private void FrameOrbit(float smoothing, float videoDeltaTime)
        {
            // Degrees per second OF VIDEO, which is why it is advanced here with the rest of the
            // camera work rather than in Update - one constant rotation in the finished file
            // however stop-start the session was.
            _yaw += Options.orbitDegreesPerSecond / Mathf.Max(1f, Options.frameRate);

            Vector3 wantedPivot = _pivot;
            float wantedDistance = _distance;

            if ((Options.followSubject || !_framed) && TryGetSubjectBounds(out Bounds bounds))
            {
                float radius = Mathf.Max(0.05f, bounds.extents.magnitude);

                // Distance at which the subject's bounding sphere exactly fills the SMALLER of
                // the two view angles, so a wide render texture frames it by height and a tall
                // one by width instead of cropping either.
                float halfVertical = _camera.fieldOfView * 0.5f * Mathf.Deg2Rad;
                float halfHorizontal = Mathf.Atan(Mathf.Tan(halfVertical) * Mathf.Max(0.01f, _camera.aspect));
                float fitted = radius / Mathf.Sin(Mathf.Min(halfVertical, halfHorizontal));

                wantedPivot = bounds.center;
                wantedDistance = fitted * Mathf.Max(0.2f, Options.distanceMultiplier);
            }

            if (!_framed)
            {
                _pivot = wantedPivot;
                _distance = wantedDistance;
                _pitch = Options.orbitPitch;
                _framed = true;
            }
            else
            {
                _pivot = Vector3.SmoothDamp(_pivot, wantedPivot, ref _pivotVelocity, smoothing, Mathf.Infinity, videoDeltaTime);
                _distance = Mathf.SmoothDamp(_distance, wantedDistance, ref _distanceVelocity, smoothing, Mathf.Infinity, videoDeltaTime);
                // Pitch is smoothed for one reason only: the Height slider is live, and dragging
                // it mid-recording would otherwise put a hard jump in the middle of the video.
                _pitch = Mathf.SmoothDamp(_pitch, Options.orbitPitch, ref _pitchVelocity, smoothing, Mathf.Infinity, videoDeltaTime);
            }

            // Always perspective. CopyFrom would otherwise leave this camera orthographic when
            // the artist happened to be in that projection at Start, with a fixed orthographicSize
            // copied from their zoom - which the turntable's own distance then has no effect on
            // at all, giving a shot framed by whatever they last did with the wheel.
            PlaceCamera(false);
        }

        /// Puts the camera at the smoothed pivot/distance/angles, reproducing the app's
        /// orthographic rule when the shot is following an artist who is in that projection.
        private void PlaceCamera(bool orthographic)
        {
            Quaternion rotation = Quaternion.Euler(_pitch, _yaw, 0f);
            float back = _distance;

            _camera.orthographic = orthographic;
            if (orthographic)
            {
                // The same derivation CameraOrbitController.UpdateTransform uses - half the height
                // a perspective camera at this distance would frame - so the subject comes out the
                // size the artist sees it at, and so the zoom they do with the wheel (which only
                // moves distance) still reads as zoom in the video.
                float size = Mathf.Max(0.01f, _distance * Mathf.Tan(_camera.fieldOfView * 0.5f * Mathf.Deg2Rad));
                _camera.orthographicSize = size;

                // Sliding an orthographic camera along its own forward axis changes nothing except
                // what the near plane clips - and at close zooms it WOULD clip, leaving the rig
                // only `distance` from the pivot with its near plane in front of that, slicing the
                // front off the model. Scaled off the framed size rather than the app's fixed
                // pullback, which is expressed in terms of a max orbit distance this rig has no
                // equivalent of.
                back = _distance + size * 8f + 10f;
            }

            // A subject far larger than the app's camera was set up for would otherwise sit past
            // the copied far plane and render as nothing at all.
            _camera.farClipPlane = Mathf.Max(_camera.farClipPlane, (back + _distance) * 4f);

            transform.SetPositionAndRotation(_pivot + rotation * new Vector3(0f, 0f, -back), rotation);
        }

        /// What the camera should be looking at: whatever was sculpted most recently, then the
        /// selection, then whatever is in the scene. Last-edited comes first deliberately - it is
        /// the only one of the three that answers "the object I'm sculpting" rather than "the
        /// object I last clicked".
        private bool TryGetSubjectBounds(out Bounds bounds)
        {
            SculptableMesh subject = SculptActivity.LastEdited;

            if (subject == null)
            {
                if (_selection == null) _selection = FindFirstObjectByType<SelectionManager>();
                if (_selection != null) subject = _selection.PrimarySelection;
            }

            if (subject == null) subject = FindFirstObjectByType<SculptableMesh>();

            if (subject == null)
            {
                bounds = default;
                return false;
            }

            var renderer = subject.GetComponent<Renderer>();
            bounds = renderer != null
                ? renderer.bounds
                : new Bounds(subject.transform.position, subject.transform.lossyScale);
            return true;
        }

        private static int EvenAtLeast(int value, int minimum) => Mathf.Max(minimum, value) & ~1;

        private void OnDestroy()
        {
            // The camera has to let go of the texture before it is released, or Unity spends the
            // rest of the session complaining about a destroyed render target.
            if (_camera != null) _camera.targetTexture = null;

            if (_output != null)
            {
                _output.Release();
                Destroy(_output);
                _output = null;
            }
        }
    }
}
