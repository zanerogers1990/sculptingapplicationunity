using UnityEngine;

namespace Sculpting.Molding
{
    public enum MoldFeatureKind
    {
        /// A peg on the lower half with a matching socket in the upper one, so the halves line
        /// up the same way every time they are closed.
        Pin,
        /// The pour hole: a funnel from the outside of the block, fed into the cavity through a
        /// thin runner so the funnel itself never touches the model.
        Sprue,
        /// A thin channel letting air out of the far end of the cavity while the pour goes in.
        Vent,
    }

    /// One placed feature, anchored to the parting surface.
    ///
    /// The anchor is stored in FRAME coordinates as (r, e) only - the height is always read back
    /// from the parting field. That is deliberate: a pin is a thing standing on the parting
    /// sheet, so when the sheet is re-fitted or dragged, every feature has to follow it, and
    /// storing a height would mean a stale one to reconcile. The gizmo writes r and e; the sheet
    /// supplies u.
    ///
    /// What the anchor MEANS depends on the kind, and each meaning is the point the user most
    /// naturally grabs:
    /// - Pin: the centre of the peg.
    /// - Vent: any point on the vent's line. Where it actually breaks into the cavity (its gate)
    ///   is found at build time - see MoldGeometry.LayoutChannel - so a vent dragged off the
    ///   model's edge still reaches the model instead of silently becoming a dead hole.
    /// - Sprue: the HUB, where the funnel ends and the runner begins. The funnel runs from here
    ///   out through the block wall and the runner runs from here back to the model, which is
    ///   what keeps the pour's fat plug of material off the model instead of fused to its nose.
    public sealed class MoldFeature
    {
        public MoldFeatureKind Kind;

        /// Anchor on the parting surface, in frame coordinates.
        public float R;
        public float E;

        /// Channels only: the way the channel runs OUT towards the block wall, as an angle in
        /// the parting plane - 0 is frame +Right, 90 is frame +Eye. Stored explicitly (seeded
        /// from the nearest wall when the channel is placed) so that dragging a channel around
        /// never flips it to leave through a different wall halfway through the drag, and so the
        /// gizmo's rotate ring has something to turn.
        public float AngleDeg;

        /// Pin: the peg's radius. Vent: the vent's radius. Sprue: the funnel's radius at the hub.
        public float RadiusInner = 0.03f;
        /// Sprue only: the funnel's radius at the outside of the block, which is what makes the
        /// pour hole a funnel rather than a straight bore. Vents keep it equal to RadiusInner.
        public float RadiusOuter = 0.06f;
        /// Pin only: how far the peg stands above the parting surface.
        public float Height = 0.06f;
        /// Sprue only: the radius of the runner that carries the pour from the hub into the
        /// cavity. The addon's runner_d_mm - thin on purpose, so the casting snaps off it cleanly.
        public float RunnerRadius = 0.015f;

        /// True for anything the suggester put there rather than the user. Suggestions are
        /// ordinary features in every other respect - they can be dragged, resized and deleted -
        /// this only drives how they are drawn and whether re-running the suggester replaces
        /// them.
        public bool Suggested;

        public bool Enabled = true;

        /// Stable identity, carried through Clone. Undo restores features as fresh copies, and
        /// this is how the selection survives that - see MoldController.Restore.
        public int Id;

        /// True on a derived mirror copy - never on anything the user placed. Set by
        /// MoldGeometry.ExpandMirrors; these live only for the length of a build or a preview
        /// pass and are never added to the controller's own list, so a mirror cannot be
        /// selected, dragged or deleted independently of the feature it follows.
        public bool IsMirror;

        /// For a mirror copy, the feature it was derived from - so clicking a mirror selects the
        /// original rather than nothing. Null on everything else.
        public MoldFeature Source;

        public MoldFeature(MoldFeatureKind kind, float r, float e)
        {
            Kind = kind;
            R = r;
            E = e;
        }

        public MoldFeature Clone() => (MoldFeature)MemberwiseClone();

        public bool IsChannel => Kind != MoldFeatureKind.Pin;

        /// Unit direction the channel leaves in, in (r, e).
        public Vector2 Direction
        {
            get
            {
                float a = AngleDeg * Mathf.Deg2Rad;
                return new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            }
        }

        /// World-space version of Direction for a given frame.
        public Vector3 WorldDirection(MoldFrame frame)
        {
            Vector2 d = Direction;
            return (frame.Right * d.x + frame.Eye * d.y).normalized;
        }

        /// Where this feature's anchor sits right now, in world space, given the live parting
        /// surface. For a sprue that is its hub; see the class remarks.
        public Vector3 WorldAnchor(PartingField field) =>
            field.Frame.ToWorld(new Vector3(R, field.Sample(R, E), E));

        /// The block wall nearest (r, e), as a channel angle - 0, 90, 180 or 270.
        ///
        /// Chosen from where the point sits relative to the block's centre, normalised against
        /// each half-extent so a long thin mold does not always send its channels out the long
        /// way just because that offset is numerically bigger. Axis-aligned rather than radial
        /// because a channel that leaves through a flat wall at a right angle is the one that
        /// drills cleanly and demoulds. Only used to SEED a new channel's angle - after that the
        /// angle is the user's, see AngleDeg.
        public static float NearestWallAngle(float r, float e, in MoldBlock block)
        {
            float cr = 0.5f * (block.R0 + block.R1);
            float ce = 0.5f * (block.E0 + block.E1);
            float nr = (r - cr) / Mathf.Max(0.5f * block.Width, 1e-6f);
            float ne = (e - ce) / Mathf.Max(0.5f * block.Depth, 1e-6f);

            if (Mathf.Abs(nr) >= Mathf.Abs(ne)) return nr >= 0f ? 0f : 180f;
            return ne >= 0f ? 90f : 270f;
        }

        /// Wraps an angle into [0, 360). Mirroring and gizmo rotation both produce angles
        /// outside that range, and undo compares stored values - two spellings of one direction
        /// would read as a change that is not there.
        public static float WrapAngle(float deg)
        {
            deg %= 360f;
            if (deg < 0f) deg += 360f;
            // -0.0 and 360 both mean 0; keep one spelling.
            return deg >= 360f - 1e-4f ? 0f : deg;
        }

        /// Value equality over everything the user can change - what undo compares to decide
        /// whether a step touched the feature list at all.
        public bool SameState(MoldFeature other)
        {
            if (other == null) return false;
            return Kind == other.Kind && Id == other.Id &&
                   R == other.R && E == other.E && AngleDeg == other.AngleDeg &&
                   RadiusInner == other.RadiusInner && RadiusOuter == other.RadiusOuter &&
                   Height == other.Height && RunnerRadius == other.RunnerRadius &&
                   Suggested == other.Suggested && Enabled == other.Enabled;
        }

        /// Scales every size by `factor`, leaving the position alone. What a change of the
        /// session's millimetre scale does to a feature that is already placed: a 14mm sprue
        /// has to stay 14mm, so its size in world units moves with the scale.
        public void ScaleSizes(float factor)
        {
            RadiusInner *= factor;
            RadiusOuter *= factor;
            Height *= factor;
            RunnerRadius *= factor;
        }
    }
}
