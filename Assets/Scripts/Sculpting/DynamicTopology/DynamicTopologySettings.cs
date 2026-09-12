using UnityEngine;

namespace Sculpting.DynamicTopology
{
    /// What dynamic topology is allowed to do, and how often. Pure data - the algorithm reads this
    /// and never writes it, and SculptController owns the serialized copy (see its Settings
    /// partial) exactly as it owns remeshResolution.
    [System.Serializable]
    public class DynamicTopologySettings
    {
        /// Off by default, deliberately. It changes what every stroke does to the mesh, it costs
        /// memory that never comes back inside a session, and every existing brush was tuned
        /// against fixed topology - so it is something the user turns on, not something that
        /// happens to them.
        public bool Enabled;

        /// Target edge length in the object's LOCAL space.
        ///
        /// Constant, not scaled by brush radius. With a brush-relative target the same patch of
        /// surface is retessellated to a different density depending on which brush last passed
        /// over it, so a broad smoothing pass silently coarsens away detail a small brush just put
        /// in - the single most-reported complaint about Blender's relative-detail modes. Here the
        /// brush radius decides the AREA that gets refined and nothing else.
        public float DetailSize = 0.012f;

        /// Edges longer than DetailSize * this are split. Paired with CollapseFactor so the two
        /// operations cannot chase each other - see its remarks for the arithmetic.
        public float SplitFactor = 4f / 3f;

        /// Edges shorter than DetailSize * this are collapsed.
        ///
        /// The hysteresis that keeps a refine from oscillating. A split at SplitFactor produces
        /// halves of length >= SplitFactor/2 = 0.667 of the target; collapsing anything below 0.8
        /// would immediately undo those, so the two thresholds have to straddle a gap no operation
        /// can land in. 0.6 leaves that gap: nothing a split emits is short enough to collapse, and
        /// nothing a collapse emits (which lengthens edges, if anything) is long enough to split.
        public float CollapseFactor = 0.6f;

        /// How far past the brush radius the refined region reaches, as a fraction of the radius.
        /// A margin at all because the vertices at the very rim of a dab still move, and leaving
        /// their edges unrefined puts the density seam exactly where the falloff is steepest.
        public float RegionMargin = 0.25f;

        /// How far the brush must travel since the last refine before another one runs, as a
        /// fraction of the brush radius. This is the throttle: without it a refine would run on
        /// every dab of a held stroke, which is many times a frame for Clay.
        public float ThrottleDistanceFraction = 0.25f;

        /// Iterations of the split/collapse/flip/relax cycle per refine. Two is enough to converge
        /// a footprint that started at roughly the right density and is being stretched by a
        /// stroke; the throttle means a slow brush gets many refines over the same ground anyway.
        public int Iterations = 2;

        /// Hard cap on topology operations in ONE refine, so a first pass over a very coarse mesh
        /// (where every edge in the footprint wants splitting, and each split creates more) cannot
        /// turn a frame into a multi-second stall. Whatever is left over is picked up by the next
        /// refine.
        public int MaxOperationsPerRefine = 6000;

        /// Strength of the tangential relaxation pass, 0..1.
        public float RelaxStrength = 0.35f;

        /// A brush must be in here to refine WHILE it is being dragged. Move and Pose are absent
        /// and must stay absent: both capture an immutable index array at drag start and re-derive
        /// the whole drag from it every frame (SculptableMesh.SelectGrab/SelectPose), so changing
        /// topology under them aims those indices at different vertices. They refine at the edges
        /// of the gesture instead - see RefinesAtStrokeBounds. Smooth is absent for a different
        /// reason: its whole job is to REMOVE detail, so adding vertices underneath it is work the
        /// next stroke throws away.
        public bool AppliesTo(BrushType brush) =>
            brush == BrushType.Clay || brush == BrushType.Crease ||
            brush == BrushType.Inflate || brush == BrushType.Flatten;

        /// Brushes that refine once as the gesture starts and once as it ends, instead of
        /// continuously - the ones whose drag holds vertex indices captured up front.
        public bool RefinesAtStrokeBounds(BrushType brush) =>
            brush == BrushType.Move || brush == BrushType.Pose;

        public float TargetEdgeLength => Mathf.Max(DetailSize, 1e-5f);
        public float SplitLength => TargetEdgeLength * Mathf.Max(SplitFactor, 1.05f);
        public float CollapseLength => TargetEdgeLength * Mathf.Clamp(CollapseFactor, 0.05f, 0.9f);
    }
}
