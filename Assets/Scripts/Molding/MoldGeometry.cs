using System.Collections.Generic;
using UnityEngine;

namespace Sculpting.Molding
{
    /// Builds the solids that get unioned into and subtracted out of the mold halves: pins,
    /// their sockets, and the sprue/vent channels.
    ///
    /// Everything here emits plain world-space vertex/index arrays, ready to hand straight to
    /// MeshBoolean.Operand. Nothing touches the scene, so the same builders serve the boolean,
    /// the live viewport preview of where a pin will go, and the tests.
    public static class MoldGeometry
    {
        /// Segments around a pin or channel. 24 is smooth enough that a printed pin does not
        /// read as faceted, and the voxel boolean rounds anything finer away regardless.
        public const int RadialSegments = 24;

        /// A closed cone/cylinder from p0 (radius r0) to p1 (radius r1), appended to the buffers.
        /// The addon's add_frustum, with the winding worked out for Unity rather than left to a
        /// recalculate pass - the boolean reads inside/outside from the winding, so an inverted
        /// pin would ADD a pin-shaped hole instead of a pin.
        public static void AddFrustum(List<Vector3> verts, List<int> tris,
                                      Vector3 p0, Vector3 p1, float r0, float r1, int segs = RadialSegments)
        {
            Vector3 axis = p1 - p0;
            float length = axis.magnitude;
            if (length < 1e-7f) return;
            axis /= length;

            // Any pair perpendicular to the axis will do - the frustum is rotationally
            // symmetric, so there is no orientation to preserve.
            Vector3 side = Mathf.Abs(axis.y) < 0.9f ? Vector3.up : Vector3.right;
            Vector3 x = VectorMath.NormalizeOr(Vector3.Cross(side, axis), Vector3.right);
            Vector3 y = Vector3.Cross(axis, x);

            int baseIndex = verts.Count;
            for (int i = 0; i < segs; i++)
            {
                float a = 2f * Mathf.PI * i / segs;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                Vector3 dir = x * c + y * s;
                verts.Add(p0 + dir * r0);
                verts.Add(p1 + dir * r1);
            }
            int capStart = verts.Count;
            verts.Add(p0);
            verts.Add(p1);

            for (int i = 0; i < segs; i++)
            {
                int j = (i + 1) % segs;
                int a0 = baseIndex + i * 2, a1 = a0 + 1;
                int b0 = baseIndex + j * 2, b1 = b0 + 1;

                // Side wall, outward. Walking the ring in +angle order with the axis pointing
                // p0 -> p1, (a0, b0, b1) has cross((b0 - a0), (b1 - a0)) pointing away from the
                // axis; MoldGeometryTests pins this down by signed volume.
                tris.Add(a0); tris.Add(b0); tris.Add(b1);
                tris.Add(a0); tris.Add(b1); tris.Add(a1);

                // Caps: the p0 end faces backwards along the axis, the p1 end forwards.
                tris.Add(capStart); tris.Add(b0); tris.Add(a0);
                tris.Add(capStart + 1); tris.Add(a1); tris.Add(b1);
            }
        }

        /// A tube that FOLLOWS the parting surface from `fromRE` to `toRE` in the (r, e) plane,
        /// with its radius easing from rStart to rEnd along the way.
        ///
        /// A straight frustum between two points on a curved parting sheet cuts below the sheet
        /// in the middle and above it at the ends, so on anything but a flat split it leaves a
        /// channel that is half-round in one half and a slot in the other. Sweeping along the
        /// field keeps the channel centred on the seam for its whole length, which is what makes
        /// it come out as matching half-grooves that demould.
        public static void AddSweptChannel(List<Vector3> verts, List<int> tris, PartingField field,
                                           Vector2 fromRE, Vector2 toRE, float rStart, float rEnd,
                                           int lengthSegments = 16, int segs = RadialSegments)
        {
            lengthSegments = Mathf.Max(2, lengthSegments);
            var centres = new Vector3[lengthSegments + 1];
            var radii = new float[lengthSegments + 1];

            for (int s = 0; s <= lengthSegments; s++)
            {
                float t = s / (float)lengthSegments;
                Vector2 re = Vector2.Lerp(fromRE, toRE, t);
                centres[s] = field.Frame.ToWorld(new Vector3(re.x, field.Sample(re.x, re.y), re.y));
                radii[s] = Mathf.Lerp(rStart, rEnd, t);
            }

            // Ring frames are built from a single reference direction rather than propagated
            // along the curve: the path is near-straight in the (r, e) plane and only gently
            // curved in u, so there is no twist to accumulate, and a fixed reference keeps
            // consecutive rings in phase (a propagated frame that flips mid-sweep braids the
            // tube shut).
            Vector3 overall = VectorMath.NormalizeOr(centres[lengthSegments] - centres[0], field.Frame.Right);
            Vector3 refUp = Mathf.Abs(Vector3.Dot(overall, field.Frame.Up)) < 0.9f ? field.Frame.Up : field.Frame.Eye;
            Vector3 x = VectorMath.NormalizeOr(Vector3.Cross(refUp, overall), field.Frame.Eye);
            Vector3 y = Vector3.Cross(overall, x);

            int baseIndex = verts.Count;
            for (int s = 0; s <= lengthSegments; s++)
            {
                for (int i = 0; i < segs; i++)
                {
                    float a = 2f * Mathf.PI * i / segs;
                    verts.Add(centres[s] + (x * Mathf.Cos(a) + y * Mathf.Sin(a)) * radii[s]);
                }
            }
            int capStart = verts.Count;
            verts.Add(centres[0]);
            verts.Add(centres[lengthSegments]);

            for (int s = 0; s < lengthSegments; s++)
            {
                int r0 = baseIndex + s * segs, r1 = baseIndex + (s + 1) * segs;
                for (int i = 0; i < segs; i++)
                {
                    int j = (i + 1) % segs;
                    tris.Add(r0 + i); tris.Add(r0 + j); tris.Add(r1 + j);
                    tris.Add(r0 + i); tris.Add(r1 + j); tris.Add(r1 + i);
                }
            }

            int last = baseIndex + lengthSegments * segs;
            for (int i = 0; i < segs; i++)
            {
                int j = (i + 1) % segs;
                tris.Add(capStart); tris.Add(baseIndex + j); tris.Add(baseIndex + i);
                tris.Add(capStart + 1); tris.Add(last + i); tris.Add(last + j);
            }
        }

        /// The peg and the socket for one pin, as two separate solids.
        ///
        /// The socket is the peg grown by the fit clearance and started slightly lower, so the
        /// halves actually close instead of bottoming out on a peg that is exactly as big as the
        /// hole. Both are tapered (the tip is 75% of the base) which is what lets a printed pin
        /// find its socket instead of jamming on the lip - straight-sided pins are the usual
        /// reason a two-part print will not close.
        ///
        /// The peg's base is sunk to the LOWEST parting height within its own footprint, not to
        /// the height at its centre: on a sloping part of the sheet a peg started at the centre
        /// height floats free on the downhill side.
        public static void BuildPin(MoldFeature feature, PartingField field, MoldSettings settings,
                                    List<Vector3> pegVerts, List<int> pegTris,
                                    List<Vector3> socketVerts, List<int> socketTris)
        {
            float r = Mathf.Max(feature.RadiusInner, 1e-4f);
            float h = Mathf.Max(feature.Height, 1e-4f);
            float clearance = Mathf.Max(settings.PinClearance, 0f);

            float centre = field.Sample(feature.R, feature.E);
            float lowest = centre;
            for (int a = -1; a <= 1; a++)
                for (int b = -1; b <= 1; b++)
                    lowest = Mathf.Min(lowest, field.Sample(feature.R + a * r, feature.E + b * r));

            float embed = 0.5f * h;
            Vector3 pegBase = field.Frame.ToWorld(new Vector3(feature.R, lowest - embed, feature.E));
            Vector3 pegTip = field.Frame.ToWorld(new Vector3(feature.R, centre + h, feature.E));
            AddFrustum(pegVerts, pegTris, pegBase, pegTip, r, r * 0.75f);

            Vector3 sockBase = field.Frame.ToWorld(new Vector3(feature.R, lowest - 0.05f * h, feature.E));
            Vector3 sockTip = field.Frame.ToWorld(new Vector3(feature.R, centre + h + clearance, feature.E));
            AddFrustum(socketVerts, socketTris, sockBase, sockTip, r + clearance, r * 0.75f + clearance);
        }

        /// Where every part of one channel goes, in (r, e) on the parting sheet - the single
        /// place that decides it, so the build, the viewport preview and the picker cannot
        /// disagree about where a channel is.
        public struct ChannelLayout
        {
            /// Start of the vent, or of the sprue's runner: ChannelOverlap inside the model.
            public Vector2 Inner;
            /// Where the channel crosses the model's outline on its way out - the gate.
            public Vector2 Gate;
            /// Sprue only: where the runner meets the funnel. Equal to Gate on a vent.
            public Vector2 Hub;
            /// Just past the block wall, so the channel opens to the outside.
            public Vector2 Exit;
            /// False when the channel's line never meets the model at the sheet's height - it
            /// would cut a hole in the mold that does not reach the cavity.
            public bool Connected;

            /// Vent radius, or the sprue's runner radius.
            public float ChannelRadius;
            /// Sprue funnel radius at the hub and at the outside of the block.
            public float FunnelInner, FunnelOuter;

            public bool HasFunnel;
        }

        /// Lays out one channel.
        ///
        /// A channel is a straight line in the parting plane through its anchor, running out
        /// through the block wall in its own direction. Its inner end is found rather than
        /// stored: walking in from the wall along that line, the first point inside the model
        /// is the gate, and the channel reaches ChannelOverlap past it. That is what lets a vent
        /// be dragged around freely without ever being left short of the cavity - which in a
        /// real mold is a sealed pocket of air and a failed pour, with nothing on screen to say so.
        ///
        /// A sprue is split in two, which is the addon's hub-and-runner design: the funnel runs
        /// from the hub out through the wall, and a thin runner joins the hub to the gate. The
        /// old single bore started INSIDE the model at its full 14mm, so its plug of material
        /// came out fused across the lure's head; the runner keeps that plug a padding's width
        /// away and leaves a neck that snaps off clean. The hub is held at least RunnerGap clear
        /// of the model and short of the wall, whatever the anchor says, so the funnel can never
        /// be dragged back into the head.
        ///
        /// A null column map (a caller with none) runs the channel from its anchor exactly as
        /// placed.
        public static ChannelLayout LayoutChannel(MoldFeature feature, PartingField field, in MoldBlock block,
                                                  PullColumnMap columns, MoldSettings settings)
        {
            var layout = new ChannelLayout();
            Vector2 d = feature.Direction;
            var a = new Vector2(Mathf.Clamp(feature.R, block.R0, block.R1), Mathf.Clamp(feature.E, block.E0, block.E1));

            float tExit = RayExit(a, d, block);
            float tBack = RayExit(a, -d, block);
            float margin = 0.005f * Mathf.Max(block.Width, block.Depth);
            layout.Exit = a + d * (tExit + margin);

            float overlap = Mathf.Max(settings.ChannelOverlap, 0f);
            layout.Connected = FindGate(a, d, tExit, tBack, field, columns, out float tGate);
            if (!layout.Connected) tGate = 0f;

            if (feature.Kind != MoldFeatureKind.Sprue)
            {
                layout.Gate = a + d * tGate;
                layout.Hub = layout.Gate;
                layout.Inner = a + d * (tGate - overlap);
                float r = Mathf.Max(feature.RadiusInner, 1e-4f);
                layout.ChannelRadius = Mathf.Min(r, ChannelCap(field, block, layout.Gate));
                return layout;
            }

            // The hub is the anchor, held clear of the model and short of the wall.
            float runner = Mathf.Max(feature.RunnerRadius, 1e-4f);
            float gap = RunnerGap(runner, settings);
            float tHub;
            if (layout.Connected)
            {
                float lo = tGate + gap;
                float hi = tExit - gap;
                // No room for both a gap and a funnel (a very thin padding): split the difference
                // rather than let the hub land inside the model or past the wall.
                tHub = lo <= hi ? Mathf.Clamp(0f, lo, hi) : 0.5f * (tGate + tExit);
            }
            else
            {
                tHub = Mathf.Min(0f, tExit - gap);
            }

            layout.Gate = a + d * tGate;
            layout.Hub = a + d * tHub;
            layout.Inner = a + d * (tGate - overlap);
            layout.HasFunnel = true;

            float hubCap = ChannelCap(field, block, layout.Hub);
            float funnelIn = Mathf.Max(feature.RadiusInner, 1e-4f);
            float funnelOut = Mathf.Max(feature.RadiusOuter, funnelIn);
            layout.FunnelInner = Mathf.Min(funnelIn, hubCap);
            layout.FunnelOuter = Mathf.Min(funnelOut, hubCap);
            // Never wider than the funnel it feeds, and never through the floor at the gate.
            layout.ChannelRadius = Mathf.Min(runner, Mathf.Min(layout.FunnelInner, ChannelCap(field, block, layout.Gate)));
            return layout;
        }

        /// How far the sprue's hub is kept from the model: at least a runner's width, so there is
        /// always a neck to snap the casting off, and never less than a millimetre.
        public static float RunnerGap(float runnerRadius, MoldSettings settings) =>
            Mathf.Max(2f * runnerRadius, settings.ToUnits(1f));

        /// Widest a channel may be at (r, e) without breaking out through the floor or the roof:
        /// most of the thinner of the two walls the parting surface leaves there.
        private static float ChannelCap(PartingField field, in MoldBlock block, Vector2 at)
        {
            float here = field.Sample(at.x, at.y);
            float room = Mathf.Min(here - block.UBottom, block.UTop - here);
            return Mathf.Max(0.85f * room, 1e-4f);
        }

        /// Distance along d from a (inside the footprint) to the edge of the block footprint.
        private static float RayExit(Vector2 a, Vector2 d, in MoldBlock block)
        {
            float t = float.MaxValue;
            if (d.x > 1e-6f) t = Mathf.Min(t, (block.R1 - a.x) / d.x);
            else if (d.x < -1e-6f) t = Mathf.Min(t, (block.R0 - a.x) / d.x);
            if (d.y > 1e-6f) t = Mathf.Min(t, (block.E1 - a.y) / d.y);
            else if (d.y < -1e-6f) t = Mathf.Min(t, (block.E0 - a.y) / d.y);
            return t == float.MaxValue ? 0f : Mathf.Max(t, 0f);
        }

        /// Walks in from the wall along the channel's line and returns the first point inside
        /// the model - the outermost place the channel meets the cavity - as a distance along
        /// d from a. Stepped at half a column and then bisected, so the gate is found to a small
        /// fraction of the parting grid.
        private static bool FindGate(Vector2 a, Vector2 d, float tExit, float tBack, PartingField field,
                                     PullColumnMap columns, out float tGate)
        {
            tGate = 0f;
            if (columns == null || columns.CrossingCount == 0 || field == null) return false;

            float span = tExit + tBack;
            float step = 0.5f * Mathf.Max(Mathf.Min(columns.Dr, columns.De), 1e-6f);
            int steps = Mathf.Clamp(Mathf.CeilToInt(span / step), 1, 1 << 16);
            step = span / steps;

            float prev = tExit;
            for (int k = 0; k <= steps; k++)
            {
                float t = tExit - k * step;
                if (InsideAtSheet(a + d * t, field, columns))
                {
                    if (k == 0) { tGate = t; return true; }
                    // The boundary lies between the previous (outside) sample and this one.
                    float lo = t, hi = prev;
                    for (int b = 0; b < 12; b++)
                    {
                        float mid = 0.5f * (lo + hi);
                        if (InsideAtSheet(a + d * mid, field, columns)) lo = mid; else hi = mid;
                    }
                    tGate = lo;
                    return true;
                }
                prev = t;
            }
            return false;
        }

        private static bool InsideAtSheet(Vector2 re, PartingField field, PullColumnMap columns) =>
            columns.InsideAt(re.x, field.Sample(re.x, re.y), re.y);

        /// One channel's solids, appended to the buffers: a vent's single tube, or a sprue's
        /// runner plus funnel. Both follow the parting surface (see AddSweptChannel) so each
        /// comes out as matching half-grooves in the two halves.
        public static void BuildChannel(MoldFeature feature, PartingField field, in MoldBlock block,
                                        PullColumnMap columns, MoldSettings settings,
                                        List<Vector3> verts, List<int> tris)
        {
            ChannelLayout c = LayoutChannel(feature, field, block, columns, settings);

            if (!c.HasFunnel)
            {
                AddSweptChannel(verts, tris, field, c.Inner, c.Exit, c.ChannelRadius, c.ChannelRadius);
                return;
            }

            if (c.Connected)
                AddSweptChannel(verts, tris, field, c.Inner, RunnerEnd(c, feature), c.ChannelRadius, c.ChannelRadius);
            AddSweptChannel(verts, tris, field, c.Hub, c.Exit, c.FunnelInner, c.FunnelOuter);
        }

        /// Where a sprue's runner stops: a little way INTO the funnel, so the two solids overlap
        /// rather than meeting end to end - two caps touching exactly would leave the voxel
        /// boolean a zero-thickness wall to resolve. The addon's sink.
        public static Vector2 RunnerEnd(in ChannelLayout c, MoldFeature feature)
        {
            float sink = Mathf.Min(1.5f * c.ChannelRadius, 0.5f * Vector2.Distance(c.Hub, c.Exit));
            return c.Hub + feature.Direction * sink;
        }

        /// One straight piece of a feature, for picking: a world-space segment and its radius.
        public struct Spine
        {
            public Vector3 A, B;
            public float Radius;
        }

        /// The segments that make up a feature on screen - a pin's axis, a vent's tube, a
        /// sprue's runner and funnel - so a click anywhere on the visible body selects it rather
        /// than only a click on its anchor point.
        public static void Spines(MoldFeature feature, PartingField field, in MoldBlock block,
                                  PullColumnMap columns, MoldSettings settings, List<Spine> into)
        {
            MoldFrame frame = field.Frame;

            if (feature.Kind == MoldFeatureKind.Pin)
            {
                Vector3 b = feature.WorldAnchor(field);
                into.Add(new Spine { A = b, B = b + frame.Up * feature.Height, Radius = feature.RadiusInner });
                return;
            }

            ChannelLayout c = LayoutChannel(feature, field, block, columns, settings);
            if (!c.HasFunnel)
            {
                into.Add(new Spine { A = OnSheet(field, c.Gate), B = OnSheet(field, c.Exit), Radius = c.ChannelRadius });
                return;
            }
            if (c.Connected)
                into.Add(new Spine { A = OnSheet(field, c.Gate), B = OnSheet(field, c.Hub), Radius = c.ChannelRadius });
            into.Add(new Spine { A = OnSheet(field, c.Hub), B = OnSheet(field, c.Exit), Radius = Mathf.Max(c.FunnelInner, c.FunnelOuter) });
        }

        /// World position of the sheet at (r, e).
        public static Vector3 OnSheet(PartingField field, Vector2 re) =>
            field.Frame.ToWorld(new Vector3(re.x, field.Sample(re.x, re.y), re.y));

        /// The four corner pins the addon places automatically, as suggestions the user can then
        /// move or delete. Set in from the block corners by half the padding so they sit in the
        /// middle of the material rather than on its edge.
        public static List<MoldFeature> SuggestPins(in MoldBlock block, MoldSettings settings, PartingField field)
        {
            var list = new List<MoldFeature>(4);
            float inset = 0.5f * settings.Padding;
            float r = Mathf.Min(settings.PinRadius, 0.42f * settings.Padding);

            var corners = new[]
            {
                new Vector2(block.R0 + inset, block.E0 + inset),
                new Vector2(block.R0 + inset, block.E1 - inset),
                new Vector2(block.R1 - inset, block.E0 + inset),
                new Vector2(block.R1 - inset, block.E1 - inset),
            };

            // Capped so a pin never punches through the floor or the roof of the half it belongs
            // to, whatever the parting surface is doing at that corner.
            float headroom = float.MaxValue;
            foreach (Vector2 c in corners)
            {
                float h = field.Sample(c.x, c.y);
                headroom = Mathf.Min(headroom, Mathf.Min(h - block.UBottom, block.UTop - h));
            }
            float height = Mathf.Min(settings.PinHeight, 0.6f * Mathf.Max(headroom, 1e-4f));

            foreach (Vector2 c in corners)
                list.Add(new MoldFeature(MoldFeatureKind.Pin, c.x, c.y)
                {
                    RadiusInner = r,
                    Height = height,
                    Suggested = true,
                });

            return list;
        }

        /// A sprue at one end of the model and a vent at the other - the addon's default pair -
        /// both aimed at the CENTRE of that end's cross-section rather than at the bounding box's
        /// midline.
        ///
        /// This is the addon's channel_specs rule: find the model's tip along the long axis at
        /// the parting height, gather everything within 6% of the length of it, and take the
        /// median across. On a lure whose head is off the box's centre line - most of them, once
        /// a tail curls - the midline misses the head and the sprue feeds the cheek. The column
        /// map already knows which cells of the sheet are inside the model, so this costs one
        /// pass over the grid.
        ///
        /// The sprue's hub is seeded halfway between the head and the block wall, the addon's
        /// hub_t = (far + edge) / 2, so a fresh mold starts with the funnel clear of the model.
        public static List<MoldFeature> SuggestChannels(in MoldBlock block, MoldSettings settings,
                                                        PartingField field, PullColumnMap columns)
        {
            var list = new List<MoldFeature>(2);
            bool alongR = block.ModelExtent.x >= block.ModelExtent.z;

            Vector2 head = EndCentre(block, field, columns, alongR, +1);
            Vector2 tail = EndCentre(block, field, columns, alongR, -1);
            float headAngle = alongR ? 0f : 90f;
            float tailAngle = alongR ? 180f : 270f;

            if (settings.Sprue)
            {
                var sprue = new MoldFeature(MoldFeatureKind.Sprue, head.x, head.y)
                {
                    AngleDeg = headAngle,
                    RadiusInner = settings.SprueRadiusInner,
                    RadiusOuter = settings.SprueRadiusOuter,
                    RunnerRadius = settings.RunnerRadius,
                    Suggested = true,
                };
                SeatSprueHub(sprue, field, block, columns, settings);
                list.Add(sprue);
            }

            list.Add(new MoldFeature(MoldFeatureKind.Vent, tail.x, tail.y)
            {
                AngleDeg = tailAngle,
                RadiusInner = settings.VentRadius,
                RadiusOuter = settings.VentRadius,
                Suggested = true,
            });

            return list;
        }

        /// Moves a sprue whose anchor was put at its GATE (a click on the model's edge, or the
        /// suggester's tip) out to its hub, halfway between the model and the block wall.
        public static void SeatSprueHub(MoldFeature sprue, PartingField field, in MoldBlock block,
                                        PullColumnMap columns, MoldSettings settings)
        {
            Vector2 d = sprue.Direction;
            var a = new Vector2(Mathf.Clamp(sprue.R, block.R0, block.R1), Mathf.Clamp(sprue.E, block.E0, block.E1));
            float tExit = RayExit(a, d, block);
            float tBack = RayExit(a, -d, block);
            float tGate = FindGate(a, d, tExit, tBack, field, columns, out float g) ? g : 0f;

            float gap = RunnerGap(Mathf.Max(sprue.RunnerRadius, 1e-4f), settings);
            float tHub = 0.5f * (tGate + tExit);
            if (tExit - tGate > 2f * gap) tHub = Mathf.Clamp(tHub, tGate + gap, tExit - gap);

            Vector2 hub = a + d * tHub;
            sprue.R = hub.x;
            sprue.E = hub.y;
        }

        /// Centre of the model's cross-section at one end, at the parting height: the tip along
        /// the long axis, and the median of everything within 6% of the length of it across.
        /// Falls back to the bounding box when the sheet misses the model entirely.
        private static Vector2 EndCentre(in MoldBlock block, PartingField field, PullColumnMap columns,
                                         bool alongR, int sign)
        {
            float midR = 0.5f * (block.ModelMin.x + block.ModelMax.x);
            float midE = 0.5f * (block.ModelMin.z + block.ModelMax.z);
            Vector2 fallback = alongR
                ? new Vector2(sign > 0 ? block.ModelMax.x : block.ModelMin.x, midE)
                : new Vector2(midR, sign > 0 ? block.ModelMax.z : block.ModelMin.z);
            if (columns == null || columns.CrossingCount == 0 || field == null) return fallback;

            // Every column centre whose sheet point is inside the model.
            var along = new List<float>();
            var across = new List<float>();
            for (int i = 0; i < columns.Nr; i++)
            {
                float r = columns.ColumnCentreR(i);
                for (int j = 0; j < columns.Ne; j++)
                {
                    if (columns.CrossingsIn(i, j) < 2) continue;
                    float e = columns.ColumnCentreE(j);
                    if (!columns.InsideAt(r, field.Sample(r, e), e)) continue;
                    along.Add(alongR ? r : e);
                    across.Add(alongR ? e : r);
                }
            }
            if (along.Count == 0) return fallback;

            float tip = sign > 0 ? float.MinValue : float.MaxValue;
            foreach (float v in along) tip = sign > 0 ? Mathf.Max(tip, v) : Mathf.Min(tip, v);

            float length = alongR ? block.ModelExtent.x : block.ModelExtent.z;
            float slab = 0.06f * Mathf.Max(length, 1e-6f);
            var inSlab = new List<float>();
            for (int k = 0; k < along.Count; k++)
                if (along[k] * sign >= tip * sign - slab) inSlab.Add(across[k]);
            inSlab.Sort();
            float centre = inSlab.Count > 0 ? inSlab[inSlab.Count / 2] : (alongR ? midE : midR);

            // The tip is a column CENTRE; the outline sits up to half a cell further out, which
            // FindGate recovers exactly when the channel is laid out. Starting the anchor on the
            // tip column itself guarantees it is inside.
            return alongR ? new Vector2(tip, centre) : new Vector2(centre, tip);
        }

        // ------------------------------------------------------------------------ mirroring

        /// Appends `feature` and every mirrored copy of it the settings ask for.
        ///
        /// The addon's click_mirror: identity plus one entry per mirror combination, so both
        /// axes on gives four. Copies are derived here rather than stored as extra features, so
        /// dragging or resizing the original carries its mirrors with it - there is no second
        /// object that can drift out of step, and nothing to clean up when a mirror is switched
        /// off. Every caller that consumes features (the build, the preview) goes through this,
        /// which is what keeps the two showing the same thing.
        ///
        /// Mirrored about the MODEL's own centre, not the block's: the object is what has a
        /// symmetry to respect, and the block can be padded off-centre by a dragged surface.
        ///
        /// A sprue is never mirrored - one pour hole is the point, and two would feed the cavity
        /// from both ends and trap the air between them.
        public static void ExpandMirrors(MoldFeature feature, in MoldBlock block, MoldSettings settings,
                                         List<MoldFeature> into)
        {
            into.Add(feature);
            if (feature == null || feature.Kind == MoldFeatureKind.Sprue) return;
            if (!settings.MirrorAcrossRight && !settings.MirrorAcrossEye) return;

            float midR = 0.5f * (block.ModelMin.x + block.ModelMax.x);
            float midE = 0.5f * (block.ModelMin.z + block.ModelMax.z);

            // A copy landing on top of its source - which is what mirroring a feature that sits
            // ON the mirror plane does - is skipped rather than emitted. The winding-number
            // boolean would union the duplicate away harmlessly, but the preview would draw two
            // coincident tubes and z-fight, and the build would pay for a channel twice.
            float epsilon = 1e-4f * Mathf.Max(block.Width, block.Depth);

            for (int combo = 1; combo < 4; combo++)
            {
                bool flipR = (combo & 1) != 0;
                bool flipE = (combo & 2) != 0;
                if (flipR && !settings.MirrorAcrossRight) continue;
                if (flipE && !settings.MirrorAcrossEye) continue;

                float r = flipR ? 2f * midR - feature.R : feature.R;
                float e = flipE ? 2f * midE - feature.E : feature.E;
                if (Mathf.Abs(r - feature.R) < epsilon && Mathf.Abs(e - feature.E) < epsilon) continue;

                MoldFeature copy = feature.Clone();
                copy.R = Mathf.Clamp(r, block.R0, block.R1);
                copy.E = Mathf.Clamp(e, block.E0, block.E1);
                // A channel's direction mirrors with it, or a vent leaving through the right wall
                // would come back as one leaving through the right wall on the left side too.
                float angle = feature.AngleDeg;
                if (flipR) angle = 180f - angle;
                if (flipE) angle = -angle;
                copy.AngleDeg = MoldFeature.WrapAngle(angle);
                copy.IsMirror = true;
                copy.Source = feature;
                into.Add(copy);
            }
        }

        /// Every enabled feature plus its mirrors, in one list - what the build and the preview
        /// both iterate so neither can show something the other does not.
        public static List<MoldFeature> ExpandAll(IReadOnlyList<MoldFeature> features,
                                                  in MoldBlock block, MoldSettings settings)
        {
            var all = new List<MoldFeature>(features != null ? features.Count * 2 : 0);
            if (features == null) return all;

            foreach (MoldFeature f in features)
            {
                if (f == null || !f.Enabled) continue;
                ExpandMirrors(f, block, settings, all);
            }
            return all;
        }
    }
}
