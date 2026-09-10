using UnityEngine;

namespace Sculpting
{
    /// How the timelapse camera decides where to point.
    public enum TimelapseFraming
    {
        /// Shoots the model from the artist's own viewing angle, leaning the framing towards
        /// whatever they are working on. The default - see TimelapseRig.FrameFollowingArtist.
        FollowArtist,

        /// A constant turntable orbit around the subject, ignoring the app's camera entirely.
        Orbit
    }

    /// Everything the timelapse recorder lets you set, in one place so the editor window can
    /// draw it and the runtime rig can read it without the two knowing about each other.
    ///
    /// Split into "fixed once recording starts" and "live" below, because that distinction is
    /// what the window's greyed-out fields are expressing: resolution and frame rate are baked
    /// into the video file and the encoder at Start, while framing and pacing are just read off
    /// this object every frame and can be nudged mid-recording.
    [System.Serializable]
    public class TimelapseOptions
    {
        // ------------------------------------------------------------ fixed at Start

        /// Output size. Rounded to even numbers before use - H.264 refuses odd dimensions.
        public int width = 1280;
        public int height = 720;

        /// Frames per second of the FINISHED video. Also the rate the Recorder encodes at, so
        /// every other timing below is expressed against it.
        public float frameRate = 30f;

        /// Throttles the whole app to `frameRate` while recording. OFF by default, and it should
        /// usually stay off.
        ///
        /// It was on at first, because Recorder pins Time.captureDeltaTime to 1/frameRate in
        /// constant-rate mode and the throttle is the obvious way to keep the app's clock honest
        /// against that. The cost turned out to be brutal: measured 291fps idle against 29.9fps
        /// while recording - a tenfold loss of responsiveness for the entire session, on a
        /// machine where the actual capture work costs only 1.1ms a frame.
        ///
        /// TimelapseRig.CorrectCaptureDeltaTime now keeps the clock honest without the throttle,
        /// which is strictly better (220fps recording, pacing still correct). This is left as an
        /// escape hatch for the case where that correction is fighting a future Recorder version,
        /// and for anyone who would rather the app ran in exact lockstep with the video.
        public bool capFrameRate;

        /// Drops the UI layer from the timelapse camera. Screen-space-OVERLAY canvases never
        /// reach a render texture anyway, so this only matters for canvases rendering through a
        /// camera - but when it matters, it is the difference between a clean turntable and one
        /// with a panel bolted to the side of it.
        public bool hideUiLayer = true;

        // ------------------------------------------------------------------- live

        /// Seconds of sculpting per second of video. 1 is real time; 8 means an eight-second
        /// stroke plays back in one second. This is the "timelapse" part - idle time is dropped
        /// entirely (that's the activity gate), and what's left is then compressed by this.
        ///
        /// The two do different jobs and both matter: the gate is what stops an hour at the desk
        /// producing an hour of video, and this is what stops the twenty minutes of actual
        /// sculpting that are left producing twenty minutes of video.
        public float speed = 8f;

        /// How long the camera takes to settle on a new framing, in seconds OF VIDEO. Applied as
        /// a critically-damped spring stepped once per captured frame, so the easing you set here
        /// is what you see in the file regardless of how fast the app was running or what `speed`
        /// is set to. 0 snaps; around a second reads as a deliberate camera move.
        public float cameraSmoothingSeconds = 0.8f;

        /// Where the camera points. FollowArtist is the default and usually the one to want: the
        /// artist has already orbited to face whatever they are sculpting, so their viewing angle
        /// IS the answer to "where is the interesting thing right now". A turntable can only
        /// guess, and spends as long behind the work as in front of it.
        public TimelapseFraming framing = TimelapseFraming.FollowArtist;

        // --------------------------------------------------- FollowArtist framing only

        /// How far to pull the shot's pivot from the artist's pivot towards the spot where
        /// geometry last actually moved.
        ///
        /// 0 reproduces their framing exactly - effectively a screen recording of the viewport.
        /// The problem with 0 is that an artist leaves the pivot at the middle of the model and
        /// then works on an ear at the edge of the frame; the detail they are putting in is the
        /// thing the video is about, and it spends the whole take in a corner. 1 centres the
        /// video on the brush instead, which is too much the other way - the model swings around
        /// a fixed point as the stroke moves. The default leans.
        public float focusStrength = 0.5f;

        /// Multiplier on the artist's own viewing distance, FollowArtist only. 1 shows what they
        /// see; below 1 pushes in on the work; above 1 gives the recentring above room to move
        /// without carrying the model out of frame.
        public float focusZoom = 1f;

        // ------------------------------------------------------------ Orbit framing only

        /// Degrees of orbit per second OF VIDEO - not per second of wall clock. Advancing the
        /// angle once per captured frame is what makes the turntable read as one smooth constant
        /// rotation in the finished file, however stop-start the actual session was.
        public float orbitDegreesPerSecond = 9f;

        /// Camera elevation above the subject, in degrees.
        public float orbitPitch = 15f;

        /// Orbit radius as a multiple of the distance that exactly fills the frame with the
        /// subject's bounding sphere. 1 is a tight fit; the default leaves a little air.
        public float distanceMultiplier = 1.25f;

        /// How long to keep capturing after the last change. Sculpting is not continuous - there
        /// is a gap between dabs, between a stroke ending and the next beginning, between
        /// deciding and doing - and cutting on the first idle frame would turn every one of those
        /// into a hard splice. Long enough to bridge the natural gaps, short enough that walking
        /// away costs less than a second of footage.
        public float idleGraceSeconds = 0.75f;

        /// Orbit framing only: keeps re-framing as the model grows or you switch objects, rather
        /// than holding whatever the first captured frame established. FollowArtist has no use
        /// for it - the artist's own zoom and pivot already say how the model should be framed.
        public bool followSubject = true;

        public TimelapseOptions Clone() => (TimelapseOptions)MemberwiseClone();
    }
}
