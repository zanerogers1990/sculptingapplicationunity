using UnityEngine;

namespace Sculpting.Molding
{
    /// How the pull direction is chosen.
    public enum PullMode
    {
        /// Try world X, Y and Z and keep whichever traps the least surface.
        Auto,
        /// Use the camera's up direction, optionally snapped to the nearest world axis.
        FromView,
        /// Always pull along the axis named by MoldSettings.Axis.
        FixedAxis,
        /// Whatever direction the user last rotated the gizmo to. Set by dragging rather than by
        /// the dropdown, so a hand-aimed pull survives every later recompute.
        Manual,
    }

    /// Which pieces the viewport shows while a mold session is open. Flipping between these is
    /// the "inspect the cavity without losing your camera" part of the workflow - the whole
    /// point of doing this inside a sculpting app rather than regenerating in a modeller.
    public enum MoldViewMode
    {
        /// The model on its own, overlays still live. What you fit the parting surface in.
        Model,
        /// Just the two halves, model hidden - lets you look straight into the cavity.
        Halves,
        /// Halves and model together, halves translucent.
        Assembled,
        /// Halves pulled apart along the pull axis by ExplodeGap.
        Exploded,
    }

    /// What a viewport click places next.
    public enum MoldPlacement
    {
        None,
        Pin,
        Sprue,
        Vent,
    }

    /// Every knob the mold pipeline reads. A plain class rather than a struct because the UI
    /// hands out setters and the controller keeps exactly one instance for the session.
    ///
    /// Sizes are in WORLD units, not fractions, because a mold is a physical object and the
    /// numbers a user wants to type are millimetres. They are seeded from the model's own
    /// bounds when a session opens (see InitialiseForBounds) so the defaults land somewhere
    /// sane whatever scale the sculpt happens to be at - the Blender addon's fixed 1.5-unit
    /// defaults assume a lure roughly ten units long, and a one-unit sculpt would have
    /// disappeared inside them.
    public class MoldSettings
    {
        // ---------------------------------------------------------------- pull + parting
        public PullMode PullMode = PullMode.Auto;
        /// World axis index (0/1/2) used by PullMode.FixedAxis.
        public int Axis = 1;
        public bool SnapPullToAxis = true;

        /// True for the auto-fitted curved parting surface, false for a plain flat split.
        /// The flat split is not just a fallback - it is what you want on anything that is
        /// genuinely convex along the pull axis, and it re-evaluates instantly, which makes it
        /// the mode to drag the plane around in before committing to a fit.
        public bool FittedSurface = true;

        /// How fine the parting grid is, as the size of one cell in MILLIMETRES.
        ///
        /// This is the resolution of the SURFACE, not of the boolean - a coarse grid still cuts
        /// a fine cavity, it just cuts it with a blunter parting line. What it does control is
        /// how closely the split follows the model's silhouette, and how exactly the flat split
        /// finds the model's centre (the same grid supplies the volume measurement).
        ///
        /// A size rather than a node count, for the reason FinalResolution was fixed for: a
        /// count means something different on every model, and TWO counts over two different
        /// spans also make the cells rectangular, so the old grid resolved one axis several
        /// times better than the other by an amount that depended on the model's proportions.
        /// One millimetre figure is the same instruction on every model and gives square cells.
        public float GridCellMm = 0.5f;

        /// Derive the grid from GridCellMm. Off falls back to the raw node counts below, which
        /// exist so a test can pin an exact grid; there is no reason to turn this off in use.
        public bool AutoGridDensity = true;

        /// Raw node counts along frame Right and frame Eye, used only when AutoGridDensity is
        /// off. PartingGrid rounds both up to ODD so a node lands on the model's centre line.
        public int GridAcross = 65;
        public int GridDepth = 41;

        /// Put the flat split at the model's true centre - the plane with the same VOLUME of
        /// material on each side - instead of at the midpoint of its bounding box.
        ///
        /// On by default because the bounding box is not the object: a tail, a fin or a single
        /// stray vertex stretches the box without adding material, and the old midpoint moved
        /// with it. See PartingCentre for how it is measured (it comes free off the column map).
        /// Turn it off to get the old bounding-box behaviour back.
        public bool CentreOnVolume = true;

        /// Box-blur passes over the fitted height field. Higher rounds the parting line so it
        /// prints and releases cleanly, at the cost of drifting off the ideal split.
        public int Rounding = 3;

        /// How many neighbouring grid cells each column pools surface samples from when it
        /// picks its split height. 0 is noisy, 2+ is smoother but blunter.
        public int BlendRadius = 1;

        /// Columns with fewer pooled samples than this are left unknown and filled in by
        /// diffusion from their neighbours (which is what covers the padding around the model).
        public int MinSamplesPerCell = 6;

        /// Flat-split only: shifts the plane off the model's mid-height.
        public float SplitOffset;

        // ------------------------------------------------------------- physical scale
        /// Millimetres per world unit - the addon's `export_scale`, and the one number that
        /// ties every size below to a real object.
        ///
        /// A mold is a physical thing: a 14mm sprue is 14mm because that is what pours, not
        /// because it is some fraction of the model. So every size here is stored in
        /// MILLIMETRES and converted through this on read, which is what makes "this lure is
        /// 90mm long" rescale the whole mold around it for free - set the scale and the
        /// padding, walls, pins, sprue and vents all land at their real sizes at once.
        public float MillimetresPerUnit = 10f;

        /// What a model's longest axis is taken to be when a session opens and nothing else is
        /// known. Only a starting point - SetModelLengthMm is the real answer.
        public const float DefaultModelLengthMm = 100f;

        public float ToUnits(float mm) => mm / Mathf.Max(MillimetresPerUnit, 1e-6f);
        public float ToMillimetres(float units) => units * MillimetresPerUnit;

        // ------------------------------------------------------------------- mold block
        // Defaults throughout are the Blender addon's, converted at its own export_scale of
        // 10mm per unit: padding/wall 1.5 units = 15mm, pins 0.35/0.7/0.03 = 3.5/7/0.3mm,
        // channel overlap 0.4 = 4mm. The sprue and vent come from the addon's millimetre
        // properties directly (sprue_d_mm 14, vent_d_mm 1).
        public float PaddingMm = 15f;
        public float WallMm = 15f;

        /// Mold material around the model, in the parting plane, in WORLD units.
        public float Padding { get => ToUnits(PaddingMm); set => PaddingMm = ToMillimetres(value); }
        /// Mold thickness behind the model on each half, along the pull axis, in WORLD units.
        public float Wall { get => ToUnits(WallMm); set => WallMm = ToMillimetres(value); }

        // ------------------------------------------------------------ registration pins
        public bool Pins = true;
        public float PinRadiusMm = 3.5f;
        public float PinHeightMm = 7f;
        /// Extra room cut into the sockets so the halves actually go together.
        public float PinClearanceMm = 0.3f;

        public float PinRadius { get => ToUnits(PinRadiusMm); set => PinRadiusMm = ToMillimetres(value); }
        public float PinHeight { get => ToUnits(PinHeightMm); set => PinHeightMm = ToMillimetres(value); }
        public float PinClearance { get => ToUnits(PinClearanceMm); set => PinClearanceMm = ToMillimetres(value); }

        // ------------------------------------------------------------- sprue and vents
        public bool Sprue = true;
        /// Width of the pour hole at its HUB, where the runner joins it - the addon's
        /// sprue_d_mm. The funnel no longer meets the cavity directly: a thin runner does (see
        /// RunnerDiameterMm and MoldGeometry.LayoutChannel), which is what keeps the pour's plug
        /// of material off the model instead of fused across its head.
        public float SprueDiameterMm = 14f;
        /// Larger than the hub makes the pour hole a funnel. The addon's sprue_r_out of 0.9
        /// units at 10mm per unit.
        public float SprueFunnelDiameterMm = 18f;
        /// The runner carrying the pour from the sprue's hub into the cavity - the addon's
        /// runner_d_mm. Thin on purpose: it is the neck the casting is snapped off at.
        public float RunnerDiameterMm = 3f;
        /// The addon's vent_d_mm.
        public float VentDiameterMm = 1f;
        /// How far a channel reaches INSIDE the model, so it actually breaks into the cavity
        /// instead of stopping a hair short of it and leaving a sealed mold. The addon's inset.
        public float ChannelOverlapMm = 4f;

        // Radii, in world units - what the geometry builders want. Diameters are what a mold
        // maker actually specifies, which is why the stored values are diameters.
        public float SprueRadiusInner
        {
            get => ToUnits(0.5f * SprueDiameterMm);
            set => SprueDiameterMm = 2f * ToMillimetres(value);
        }
        public float SprueRadiusOuter
        {
            get => ToUnits(0.5f * SprueFunnelDiameterMm);
            set => SprueFunnelDiameterMm = 2f * ToMillimetres(value);
        }
        public float VentRadius
        {
            get => ToUnits(0.5f * VentDiameterMm);
            set => VentDiameterMm = 2f * ToMillimetres(value);
        }
        public float RunnerRadius
        {
            get => ToUnits(0.5f * RunnerDiameterMm);
            set => RunnerDiameterMm = 2f * ToMillimetres(value);
        }
        public float ChannelOverlap
        {
            get => ToUnits(ChannelOverlapMm);
            set => ChannelOverlapMm = ToMillimetres(value);
        }

        // ------------------------------------------------------------------- mirroring
        /// Mirror placed vents and pins across the parting plane's Right axis / Eye axis.
        ///
        /// The addon's click_mirror_x/y/z, restricted to the two axes that mean anything here:
        /// a feature is anchored ON the parting sheet, so mirroring along the pull axis would
        /// just move it off the sheet. Copies are DERIVED at build and preview time rather than
        /// stored, so dragging or resizing the original carries its mirror with it and there is
        /// no second object to keep in step - see the SSphere symmetry work for why stored twins
        /// are the wrong shape for this.
        ///
        /// Not applied to the sprue: there is one pour hole, and two would feed the cavity from
        /// both ends and trap the air between them.
        public bool MirrorAcrossRight;
        public bool MirrorAcrossEye;

        // ---------------------------------------------------------------------- boolean
        /// Voxels along the longest axis of a mold half for the quick first build. Low enough
        /// to come back in well under a second on a normal sculpt, which is what lets pins and
        /// sprues be placed against real halves instead of against a preview.
        public int DraftResolution = 96;
        /// Voxels for the final build. This is the feature-size floor of the cavity: detail
        /// finer than one cell rounds away.
        ///
        /// Seeded by MatchModelResolution when a session opens rather than left at a constant,
        /// because a constant cannot mean anything here: the count is along the longest axis of
        /// the BLOCK, so on a long flat model (a lure) it is spread over an axis seventeen
        /// times the one the detail lives on, and the same number that is generous on a sphere
        /// rounds every scale and gill line off a lure before it can transfer.
        public int FinalResolution = 256;

        /// Re-derive FinalResolution from the model's own triangle size whenever the model is
        /// measured, instead of leaving whatever the slider last held.
        ///
        /// On by default because the right value is a property of the mesh, not a preference:
        /// remesh the model denser and the cavity needs a finer grid to carry the detail that
        /// now exists, and nothing else in the session knows that happened.
        public bool AutoResolution = true;

        /// Rebuild the halves automatically, at draft resolution, shortly after anything that
        /// changes them.
        ///
        /// Without this the halves on screen are whatever the last explicit build produced, so
        /// placing a vent and then looking at the halves shows a mold that does not have the
        /// vent in it - and in Exploded view, where the halves are the only thing visible, there
        /// is nothing at all to say the placement landed.
        public bool LiveHalves = true;

        // ----------------------------------------------------------------- presentation
        public MoldViewMode ViewMode = MoldViewMode.Model;
        public float ExplodeGap;
        /// Draws the model tinted by which half it lands in, and flags trapped surface in red.
        public bool ShowUndercutTint = true;
        public bool ShowPartingSurface = true;
        public bool ShowBlockOutline = true;

        /// Establishes the session's scale from the model's own extent.
        ///
        /// Only the SCALE is derived here, not the sizes: the sizes are the addon's physical
        /// defaults and stay exactly what they are. This used to size every feature as a
        /// fraction of the model's bounds, which quietly produced a different mold for every
        /// object - on a 2-unit lure the sprue came out 1.3mm across, about a tenth of the
        /// 14mm the addon pours through, and no amount of resolution makes a 1.3mm sprue fill
        /// a cavity. A fraction cannot know what a sprue is for; a millimetre can.
        ///
        /// The assumed length is a placeholder until the user says otherwise - SetModelLengthMm
        /// is the real answer and the panel asks for it directly.
        public void InitialiseForBounds(Bounds bounds)
        {
            Vector3 s = bounds.size;
            float longest = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
            SetModelLengthMm(DefaultModelLengthMm, longest);
            ExplodeGap = 0f;
        }

        /// "This model is `lengthMm` millimetres along its longest axis." Sets the scale every
        /// physical size is read through, so the whole mold resizes around the model at once.
        public void SetModelLengthMm(float lengthMm, float modelLongestAxisUnits)
        {
            if (lengthMm <= 1e-4f || modelLongestAxisUnits <= 1e-6f) return;
            MillimetresPerUnit = lengthMm / modelLongestAxisUnits;
        }

        /// What the model's longest axis currently measures, in millimetres.
        public float ModelLengthMm(float modelLongestAxisUnits) => ToMillimetres(modelLongestAxisUnits);

        public MoldSettings Clone() => (MoldSettings)MemberwiseClone();
    }
}
