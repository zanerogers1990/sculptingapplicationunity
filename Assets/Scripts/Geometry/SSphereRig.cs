using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// The armature itself: a tree of spheres (position + radius + parent/child links) that
    /// SSphereSkinner turns into a mesh and SSphereController edits. Pure data - no GameObjects,
    /// no input, no rendering.
    ///
    /// Positions are in RIG-LOCAL space, so moving the rig root moves the whole blockout without
    /// touching a single node, and the mirror plane is always the rig's own x = 0.
    ///
    /// SYMMETRY IS DERIVED, NOT STORED. A node off the mirror plane has a reflection, but that
    /// reflection is not a node - it is computed, every time, by CollectGeometry. This is the
    /// single most important decision in the rebuild. The previous rig stored an explicit
    /// MirrorTwin index per node and had to keep the two halves in step through every add, move,
    /// scale, insert, delete and undo; each of those needed its own symmetric variant, each
    /// variant had edge cases (a twin with children, a pair descending from two different
    /// parents, a chain wandering across the plane and splitting), and any one of them getting it
    /// wrong left a rig in a state the others could not describe. Deriving the reflection instead
    /// makes every one of those cases disappear: there is only ever one half to edit, and the
    /// other half cannot drift out of sync because it does not exist until it is drawn.
    ///
    /// Deleted nodes are TOMBSTONED (Alive = false) rather than removed. Every link here is an
    /// index into _nodes, and so is the controller's selection and drag target; compacting would
    /// invalidate all of them at once on an operation (pruning a limb) that happens constantly.
    public class SSphereRig
    {
        public const int NoNode = -1;

        /// How close to the mirror plane, as a fraction of a sphere's own radius, counts as being
        /// ON it. Inside this band SnapToAxis pins x to exactly zero, so "is this node central?"
        /// is an exact test rather than a tolerance every caller has to agree on.
        ///
        /// Small (and not user-facing) because it is no longer load-bearing. In the old twin-based
        /// rig this band decided whether a drag CREATED a second sphere, so drifting across it
        /// silently forked a spine into a mirrored pair and the band had to be wide enough to stop
        /// that happening by accident. Here, crossing it only changes how far a derived reflection
        /// sits from its original - a continuous, reversible, harmless change.
        public const float AxisSnapFraction = 0.15f;

        public class Node
        {
            public Vector3 Position;
            public float Radius;
            public int Parent = NoNode;
            public bool Alive = true;
            public readonly List<int> Children = new List<int>();

            /// Whether this sphere sits on the mirror plane and so is its own reflection - a spine
            /// sphere, as against a shoulder. Exact rather than banded because SnapToAxis has
            /// already pinned anything close enough (see AxisSnapFraction).
            public bool OnAxis => Position.x == 0f;
        }

        /// One sphere as it appears in the world: a node, or a node's reflection. What the skinner
        /// unions, what the armature view draws, and what the cursor picks against all come from
        /// the same list, so what you see, what you click and what you get are the same geometry
        /// by construction.
        public readonly struct SphereInstance
        {
            public readonly int Node;
            public readonly bool Mirrored;
            public readonly Vector3 Centre;
            public readonly float Radius;

            public SphereInstance(int node, bool mirrored, Vector3 centre, float radius)
            {
                Node = node; Mirrored = mirrored; Centre = centre; Radius = radius;
            }
        }

        /// One tapered link as it appears in the world, named by its CHILD node - every non-root
        /// node has exactly one link running up to its parent, so the child names it uniquely.
        public readonly struct LinkInstance
        {
            public readonly int Child;
            public readonly bool Mirrored;
            public readonly Vector3 A, B;
            public readonly float RadiusA, RadiusB;

            public LinkInstance(int child, bool mirrored, Vector3 a, Vector3 b, float ra, float rb)
            {
                Child = child; Mirrored = mirrored; A = a; B = b; RadiusA = ra; RadiusB = rb;
            }
        }

        private readonly List<Node> _nodes = new List<Node>();

        public IReadOnlyList<Node> Nodes => _nodes;
        public int Count => _nodes.Count;

        /// Bumped by every mutation. Pollers (the armature view, the skinner, the UI) compare
        /// against their own last-seen value instead of subscribing to change events.
        public int Version { get; private set; }

        public int AliveCount { get; private set; }
        public bool IsEmpty => AliveCount == 0;

        public bool IsAlive(int index) => index >= 0 && index < _nodes.Count && _nodes[index].Alive;

        /// The live node at `index`, or null when that index is out of range or tombstoned.
        /// Returning null rather than throwing is deliberate: indices held across a frame (the
        /// selection, a drag target) can be deleted underneath their holder, and every one of
        /// those callers wants to quietly stand down rather than break.
        public Node Get(int index) => IsAlive(index) ? _nodes[index] : null;

        // ------------------------------------------------------------------------- building

        public int AddRoot(Vector3 position, float radius) =>
            Append(new Node { Position = position, Radius = Mathf.Max(radius, 0.0001f) });

        public int AddChild(int parent, Vector3 position, float radius)
        {
            if (!IsAlive(parent)) return NoNode;
            int index = Append(new Node
            {
                Position = position,
                Radius = Mathf.Max(radius, 0.0001f),
                Parent = parent
            });
            _nodes[parent].Children.Add(index);
            return index;
        }

        /// Splices a new sphere into the middle of an existing link, between `child` and its
        /// parent: the new node takes the parent's place above `child`, and `child` hangs off it.
        /// This is how a finished chain gains volume where it needs it - a forearm too thin is
        /// only apparent once the whole limb exists, and neither extruding a new tip nor scaling
        /// a joint sphere adds mass in the middle of a bone.
        public int InsertBetween(int child, Vector3 position, float radius)
        {
            if (!IsAlive(child)) return NoNode;

            int parent = _nodes[child].Parent;
            if (!IsAlive(parent)) return NoNode;

            int mid = Append(new Node
            {
                Position = position,
                Radius = Mathf.Max(radius, 0.0001f),
                Parent = parent
            });

            // The parent SWAPS `child` for `mid` rather than gaining a second entry, or the limb
            // would fork instead of lengthening.
            List<int> siblings = _nodes[parent].Children;
            int slot = siblings.IndexOf(child);
            if (slot >= 0) siblings[slot] = mid; else siblings.Add(mid);

            _nodes[mid].Children.Add(child);
            _nodes[child].Parent = mid;
            return mid;
        }

        private int Append(Node node)
        {
            _nodes.Add(node);
            AliveCount++;
            Version++;
            return _nodes.Count - 1;
        }

        /// Tombstones `index` and everything below it, unhooking it from its parent's child list.
        public void Remove(int index)
        {
            if (!IsAlive(index)) return;

            Node node = _nodes[index];
            if (IsAlive(node.Parent)) _nodes[node.Parent].Children.Remove(index);
            node.Parent = NoNode;

            var doomed = new List<int>();
            CollectSubtree(index, doomed);
            for (int i = 0; i < doomed.Count; i++)
            {
                Node n = _nodes[doomed[i]];
                n.Alive = false;
                n.Children.Clear();
                AliveCount--;
            }
            Version++;
        }

        public void Clear()
        {
            _nodes.Clear();
            AliveCount = 0;
            Version++;
        }

        // ------------------------------------------------------------------------ snapshots

        /// A deep copy of the whole tree, for undo. Deep because Node is a reference type with a
        /// mutable position and its own child list, so a shallow copy would hand back live nodes
        /// that keep changing as the rig is edited. Whole-tree rather than a delta: a rig is a few
        /// dozen nodes at ~60 bytes each, orders of magnitude below the mesh deltas history is
        /// already sized for.
        public Node[] Snapshot()
        {
            var copy = new Node[_nodes.Count];
            for (int i = 0; i < _nodes.Count; i++) copy[i] = CloneNode(_nodes[i]);
            return copy;
        }

        /// Replaces the whole tree with a snapshot. Indices in the snapshot are self-consistent
        /// (they were this list's own), so nothing needs remapping - the other reason deleted
        /// nodes are tombstoned rather than compacted.
        public void Restore(Node[] snapshot)
        {
            _nodes.Clear();
            AliveCount = 0;
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Length; i++)
                {
                    Node clone = CloneNode(snapshot[i]);
                    _nodes.Add(clone);
                    if (clone.Alive) AliveCount++;
                }
            }
            Version++;
        }

        private static Node CloneNode(Node source)
        {
            var clone = new Node
            {
                Position = source.Position,
                Radius = source.Radius,
                Parent = source.Parent,
                Alive = source.Alive
            };
            clone.Children.AddRange(source.Children);
            return clone;
        }

        /// Rough retained size of a snapshot, for EditHistory's memory budget.
        public static long SnapshotBytes(Node[] snapshot)
        {
            if (snapshot == null) return 0;
            long total = 0;
            for (int i = 0; i < snapshot.Length; i++)
                total += 64 + (long)snapshot[i].Children.Count * 4;
            return total;
        }

        // -------------------------------------------------------------------------- editing

        public void SetPosition(int index, Vector3 position)
        {
            Node node = Get(index);
            if (node == null) return;
            node.Position = position;
            Version++;
        }

        public void SetRadius(int index, float radius)
        {
            Node node = Get(index);
            if (node == null) return;
            node.Radius = Mathf.Max(radius, 0.0001f);
            Version++;
        }

        /// Slides `index` and its whole subtree by `delta` - a rigid translation, so every bone
        /// length below the dragged node survives. This is what Move mode does by default: a
        /// shoulder dragged across a chest should take the arm with it, not stretch it.
        public void TranslateSubtree(int index, Vector3 delta)
        {
            if (!IsAlive(index)) return;
            var subtree = new List<int>();
            CollectSubtree(index, subtree);
            for (int i = 0; i < subtree.Count; i++) _nodes[subtree[i]].Position += delta;
            Version++;
        }

        /// Swings `index` and its subtree about `pivot` - the Rotate primitive. A pure rotation
        /// about the parent joint is what makes a chain behave like a skeleton: every bone below
        /// keeps its length and its relative angle, so posing an arm carries the hand along.
        public void RotateSubtree(int index, Quaternion rotation, Vector3 pivot)
        {
            if (!IsAlive(index)) return;
            var subtree = new List<int>();
            CollectSubtree(index, subtree);
            for (int i = 0; i < subtree.Count; i++)
            {
                Node n = _nodes[subtree[i]];
                n.Position = pivot + rotation * (n.Position - pivot);
            }
            Version++;
        }

        /// Multiplies the radii of `index` and everything below it. Scaling a whole limb at once
        /// is the difference between thinning an arm and thinning one joint of it.
        public void ScaleSubtreeRadii(int index, float factor, float min, float max)
        {
            if (!IsAlive(index) || factor <= 0f) return;
            var subtree = new List<int>();
            CollectSubtree(index, subtree);
            for (int i = 0; i < subtree.Count; i++)
            {
                Node n = _nodes[subtree[i]];
                n.Radius = Mathf.Clamp(n.Radius * factor, min, max);
            }
            Version++;
        }

        /// Appends `index` and every live descendant to `into`, breadth-first. The list belongs to
        /// the caller, so a drag running this every frame can reuse one buffer.
        public void CollectSubtree(int index, List<int> into)
        {
            if (!IsAlive(index)) return;
            int start = into.Count;
            into.Add(index);
            for (int cursor = start; cursor < into.Count; cursor++)
            {
                List<int> children = _nodes[into[cursor]].Children;
                for (int i = 0; i < children.Count; i++)
                    if (IsAlive(children[i])) into.Add(children[i]);
            }
        }

        /// Every live node, parents before children.
        ///
        /// Walked from the roots rather than in index order, because index order does NOT
        /// guarantee it: InsertBetween appends the new middle node AFTER the child it adopts, so
        /// that child's parent has a higher index than the child does. Anything that maps parents
        /// onto something before mapping their children (BakeSymmetry) breaks on such a rig.
        public void CollectHierarchy(List<int> into)
        {
            for (int i = 0; i < _nodes.Count; i++)
                if (_nodes[i].Alive && !IsAlive(_nodes[i].Parent)) CollectSubtree(i, into);
        }

        // -------------------------------------------------------------------------- symmetry

        public static Vector3 Mirror(Vector3 p) => new Vector3(-p.x, p.y, p.z);

        /// Reflection of a rotation through the x = 0 plane. A reflected rotation turns about the
        /// reflected axis in the OPPOSITE direction, which for a quaternion is negating the two
        /// components perpendicular to the mirror normal - so swinging a left arm downward swings
        /// the right one downward too, not upward.
        public static Quaternion MirrorRotation(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

        /// Pins x to exactly zero when the position is within AxisSnapFraction of the plane, so a
        /// centre-line sphere is exactly central rather than a hair off it. Exactness is what lets
        /// Node.OnAxis be a plain equality test, which in turn is what stops a near-central sphere
        /// being drawn twice (itself and a reflection a thousandth of a unit away, z-fighting).
        public static Vector3 SnapToAxis(Vector3 position, float radius)
        {
            if (Mathf.Abs(position.x) <= radius * AxisSnapFraction) position.x = 0f;
            return position;
        }

        /// Every sphere and link the rig puts in the world, reflections included.
        ///
        /// The mirroring rules, in full, are these three lines - which is the whole benefit of
        /// deriving symmetry instead of storing it:
        ///   - a node off the plane contributes its reflection as well as itself;
        ///   - a node ON the plane is its own reflection, so it contributes once;
        ///   - a reflected link runs between the reflections of its two ends, and an on-axis end
        ///     reflects onto itself - which is what hangs both arms off the one chest sphere.
        public void CollectGeometry(bool symmetry, List<SphereInstance> spheres, List<LinkInstance> links)
        {
            spheres?.Clear();
            links?.Clear();

            for (int i = 0; i < _nodes.Count; i++)
            {
                if (!_nodes[i].Alive) continue;
                Node node = _nodes[i];
                bool nodeOnAxis = node.OnAxis;

                spheres?.Add(new SphereInstance(i, false, node.Position, node.Radius));
                if (symmetry && !nodeOnAxis)
                    spheres?.Add(new SphereInstance(i, true, Mirror(node.Position), node.Radius));

                if (!IsAlive(node.Parent)) continue;
                Node parent = _nodes[node.Parent];
                links?.Add(new LinkInstance(i, false, parent.Position, node.Position, parent.Radius, node.Radius));

                // A link with BOTH ends on the plane reflects onto itself; emitting it twice would
                // put two identical tubes in the same place.
                if (symmetry && !(nodeOnAxis && parent.OnAxis))
                {
                    links?.Add(new LinkInstance(i, true,
                        parent.OnAxis ? parent.Position : Mirror(parent.Position),
                        Mirror(node.Position), parent.Radius, node.Radius));
                }
            }
        }

        /// Turns the derived reflection into real nodes, so the rig keeps the shape it currently
        /// shows once symmetry stops being applied. Returns how many nodes were created.
        ///
        /// This is what makes the Symmetry toggle safe to switch off: without it, half the
        /// blockout would vanish the moment the toggle came off, which is the most alarming thing
        /// a toggle can do. Baking an already-symmetric rig and mirroring it again is harmless -
        /// the reflection lands exactly on top of the original - so turning the toggle back on
        /// after a bake is a visual no-op rather than a doubling.
        public int BakeSymmetry()
        {
            var order = new List<int>();
            CollectHierarchy(order);

            var mirrorOf = new int[_nodes.Count];
            for (int i = 0; i < mirrorOf.Length; i++) mirrorOf[i] = NoNode;

            int created = 0;
            for (int i = 0; i < order.Count; i++)
            {
                int index = order[i];
                Node node = _nodes[index];

                // On the plane: its own reflection, so children mirroring under it hang off the
                // original node rather than a copy.
                if (node.OnAxis) { mirrorOf[index] = index; continue; }

                int parentMirror = IsAlive(node.Parent) ? mirrorOf[node.Parent] : NoNode;
                Vector3 position = Mirror(node.Position);

                int made = parentMirror == NoNode
                    ? AddRoot(position, node.Radius)
                    : AddChild(parentMirror, position, node.Radius);

                mirrorOf[index] = made;
                if (made != NoNode) created++;
            }
            return created;
        }

        /// How far apart (as a fraction of radius) a node and a would-be reflection may be and still
        /// count as the same sphere when MergeMirrorPairs folds a baked half back up. Tight on
        /// purpose: a bake produces EXACT reflections (negating a float is exact), so anything
        /// further off than this is a deliberate asymmetric edit and must not be silently undone.
        private const float MergeTolerance = 0.05f;

        private static bool IsCentral(Node node) => Mathf.Abs(node.Position.x) <= node.Radius * AxisSnapFraction;

        /// Moves `index` (with its subtree) under `newParent`, or makes it a root for NoNode.
        public void Reparent(int index, int newParent)
        {
            if (!IsAlive(index) || index == newParent) return;
            Node node = _nodes[index];
            if (IsAlive(node.Parent)) _nodes[node.Parent].Children.Remove(index);
            node.Parent = IsAlive(newParent) ? newParent : NoNode;
            if (node.Parent != NoNode) _nodes[newParent].Children.Add(index);
            Version++;
        }

        /// The inverse of BakeSymmetry, for switching symmetry back ON: finds real nodes that are
        /// exact reflections of a node on the other side - same radius, and hanging off the
        /// reflection of that node's parent - and removes one of each pair, since the derived
        /// reflection is about to stand in for it. Returns how many nodes were removed.
        ///
        /// Without this, off-then-on doubled every limb: the baked copy and the new derived
        /// reflection land on top of each other, and every sphere added afterwards is mirrored
        /// under a phantom limb rather than the real one.
        ///
        /// A reflection that no longer matches - an arm moved while symmetry was off - is left
        /// alone, because which side "wins" is the artist's call, not this method's. Unmatched
        /// children of a removed node (fingers added to one hand only) are not lost: they are
        /// reflected across and re-hung under the surviving twin, so their derived reflection
        /// reappears exactly where they were.
        public int MergeMirrorPairs()
        {
            var order = new List<int>();
            CollectHierarchy(order);

            var roots = new List<int>();
            for (int i = 0; i < _nodes.Count; i++)
                if (_nodes[i].Alive && !IsAlive(_nodes[i].Parent)) roots.Add(i);

            var match = new int[_nodes.Count];
            var claimed = new bool[_nodes.Count];
            for (int i = 0; i < match.Length; i++) match[i] = NoNode;
            var pairs = new List<int>();

            for (int o = 0; o < order.Count; o++)
            {
                int i = order[o];
                Node node = _nodes[i];
                if (IsCentral(node)) { match[i] = i; continue; }
                if (node.Position.x < 0f || claimed[i]) continue;

                bool hasParent = IsAlive(node.Parent);
                int parentMatch = hasParent ? match[node.Parent] : NoNode;
                if (hasParent && parentMatch == NoNode) continue;

                List<int> candidates = hasParent ? _nodes[parentMatch].Children : roots;
                Vector3 target = Mirror(node.Position);
                float tolerance = node.Radius * MergeTolerance;
                int best = NoNode;
                float bestDistance = float.MaxValue;

                for (int c = 0; c < candidates.Count; c++)
                {
                    int candidate = candidates[c];
                    if (candidate == i || !IsAlive(candidate) || claimed[candidate]) continue;
                    Node other = _nodes[candidate];
                    if (IsCentral(other) || other.Position.x >= 0f) continue;
                    if (Mathf.Abs(other.Radius - node.Radius) > tolerance) continue;
                    float distance = (other.Position - target).magnitude;
                    if (distance > tolerance || distance >= bestDistance) continue;
                    best = candidate;
                    bestDistance = distance;
                }

                if (best == NoNode) continue;
                match[i] = best;
                match[best] = i;
                claimed[best] = true;
                pairs.Add(i);
            }

            // Deepest pairs first, so a node's unmatched children are re-homed before any removal
            // higher up the chain takes them down with it.
            var subtree = new List<int>();
            for (int p = pairs.Count - 1; p >= 0; p--)
            {
                int keep = pairs[p];
                List<int> children = _nodes[match[keep]].Children;
                for (int c = children.Count - 1; c >= 0; c--)
                {
                    int child = children[c];
                    if (!IsAlive(child) || claimed[child]) continue;
                    subtree.Clear();
                    CollectSubtree(child, subtree);
                    for (int s = 0; s < subtree.Count; s++)
                        _nodes[subtree[s]].Position = Mirror(_nodes[subtree[s]].Position);
                    Reparent(child, keep);
                }
            }

            int before = AliveCount;
            for (int p = 0; p < pairs.Count; p++) Remove(match[pairs[p]]);
            return before - AliveCount;
        }

        /// Pins every node inside the axis band exactly onto the plane - for nodes left a hair off
        /// centre while symmetry was off, which would otherwise be drawn twice, z-fighting.
        public int SnapCentralToAxis()
        {
            int snapped = 0;
            for (int i = 0; i < _nodes.Count; i++)
            {
                Node node = _nodes[i];
                if (!node.Alive || node.Position.x == 0f || !IsCentral(node)) continue;
                node.Position.x = 0f;
                snapped++;
            }
            if (snapped > 0) Version++;
            return snapped;
        }

        // --------------------------------------------------------------------------- queries

        /// Bounds enclosing every live sphere - centre AND radius - including reflections when
        /// symmetry is on. This is what the skinner sizes its voxel grid from, so leaving the
        /// mirrored half out would clip it away.
        public Bounds ComputeBounds(bool symmetry)
        {
            Vector3 min = Vector3.zero, max = Vector3.zero;
            bool any = false;

            for (int i = 0; i < _nodes.Count; i++)
            {
                if (!_nodes[i].Alive) continue;
                Vector3 p = _nodes[i].Position;
                float r = _nodes[i].Radius;
                Accumulate(p, r, ref min, ref max, ref any);
                if (symmetry && !_nodes[i].OnAxis) Accumulate(Mirror(p), r, ref min, ref max, ref any);
            }

            var bounds = new Bounds();
            if (any) bounds.SetMinMax(min, max);
            return bounds;
        }

        private static void Accumulate(Vector3 p, float r, ref Vector3 min, ref Vector3 max, ref bool any)
        {
            Vector3 lo = p - Vector3.one * r, hi = p + Vector3.one * r;
            if (!any) { min = lo; max = hi; any = true; }
            else { min = Vector3.Min(min, lo); max = Vector3.Max(max, hi); }
        }

        /// Smallest live radius - drives the skinner's resolution, which sizes voxels so the
        /// thinnest sphere in the rig still survives the grid.
        public float MinRadius()
        {
            float min = float.MaxValue;
            for (int i = 0; i < _nodes.Count; i++)
                if (_nodes[i].Alive) min = Mathf.Min(min, _nodes[i].Radius);
            return min == float.MaxValue ? 0f : min;
        }

        public float MeanRadius()
        {
            float sum = 0f;
            int n = 0;
            for (int i = 0; i < _nodes.Count; i++)
                if (_nodes[i].Alive) { sum += _nodes[i].Radius; n++; }
            return n == 0 ? 0f : sum / n;
        }
    }
}
