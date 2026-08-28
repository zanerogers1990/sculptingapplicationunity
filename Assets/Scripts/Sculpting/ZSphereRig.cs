using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// The ZSphere graph itself: a tree of spheres (position + radius + parent/children links)
    /// that ZSphereSkinner turns into a mesh and ZSphereController edits. Pure data - no
    /// GameObjects, no input, no rendering - so the tree can be reasoned about and (later)
    /// serialized without dragging a scene along with it.
    ///
    /// Positions are in RIG-LOCAL space (ZSphereController parents every handle under a single
    /// rig root transform), so moving that root moves the whole blockout without touching a
    /// single node.
    ///
    /// Deleted nodes are TOMBSTONED (Alive=false) rather than removed from the list. Every link
    /// in this structure - Parent, Children, MirrorTwin - is an index into _nodes, and so is the
    /// selection ZSphereController holds and the per-node handle GameObject it pools; compacting
    /// the list would invalidate all of them at once on an operation (deleting a limb) that
    /// happens constantly during a blockout. A tombstone costs one bool check per iteration and
    /// keeps every index stable forever.
    public class ZSphereRig
    {
        public const int NoNode = -1;

        public class Node
        {
            public Vector3 Position;
            public float Radius;
            public int Parent = NoNode;

            /// The symmetric counterpart on the other side of the mirror plane, or NoNode for an
            /// unmirrored node and for one sitting ON the plane (which is its own mirror). Kept
            /// as a mutual pair - see LinkTwins/Unlink.
            public int MirrorTwin = NoNode;

            public bool Alive = true;
            public readonly List<int> Children = new List<int>();
        }

        private readonly List<Node> _nodes = new List<Node>();

        /// Every node ever created, tombstones included - check IsAlive(i) before use. Callers
        /// walking the live rig do `for (i..Count) if (!IsAlive(i)) continue;`, which is what the
        /// skinner and the handle pool both do.
        public IReadOnlyList<Node> Nodes => _nodes;
        public int Count => _nodes.Count;

        /// Bumped by every mutation. ZSphereController polls it to know when to rebuild its
        /// handles and re-skin the preview - the same cheap version-poll idiom SelectionManager
        /// and SculptableMesh.MaskVersion already use instead of change events.
        public int Version { get; private set; }

        public int AliveCount { get; private set; }
        public bool IsEmpty => AliveCount == 0;

        public bool IsAlive(int index) => index >= 0 && index < _nodes.Count && _nodes[index].Alive;

        /// Live node at `index`, or null if that index is out of range or tombstoned. Returning
        /// null rather than throwing is deliberate: indices held across a frame (the selection, a
        /// drag target, a handle's tag) can be deleted underneath their holder at any time, and
        /// every one of those callers wants to quietly skip rather than break.
        public Node Get(int index) => IsAlive(index) ? _nodes[index] : null;

        // ------------------------------------------------------------------------- building

        public int AddRoot(Vector3 position, float radius)
        {
            return Append(new Node { Position = position, Radius = Mathf.Max(radius, 0.0001f) });
        }

        /// Adds a child under `parent`. The radius is the caller's business - the "blends with
        /// the parent" rule lives in ZSphereController.ChildTaper, since it is a feel decision
        /// the user tunes, not a property of the tree.
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
        ///
        /// This is how a finished chain gains volume where it needs it. Building a limb is a
        /// shoulder-elbow-wrist sequence of extrusions, and the forearm being too thin is only
        /// apparent once all three exist - at which point the only tools that existed were
        /// extruding a NEW tip or scaling a sphere that is already doing a job at the joint.
        /// Neither adds mass in the middle of a segment. Returns NoNode for a root (nothing above
        /// it to insert between) or a dead node.
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

            // Re-parent in place: the parent swaps `child` for `mid` in its child list rather than
            // gaining a second branch, or the limb would fork instead of lengthening.
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
        /// Mirror twins of the removed nodes are UNLINKED but not themselves removed - deleting
        /// the other side too is a symmetry policy decision, and it lives in ZSphereController
        /// where the symmetry toggle does.
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
                Unlink(doomed[i]);
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

        /// A deep copy of the whole tree, for undo. Deep because every Node is a reference type
        /// with a mutable position and its own children list - a shallow copy would hand back
        /// live nodes that keep changing as the rig is edited, so restoring it later would
        /// restore whatever the rig looks like THEN rather than what it looked like when the
        /// snapshot was taken.
        ///
        /// Whole-tree rather than a delta: a rig is a few dozen nodes at ~60 bytes each, so even
        /// a large blockout snapshots in a few kilobytes - orders of magnitude below the mesh
        /// deltas history is already sized for, and not worth the machinery a delta would need.
        public Node[] Snapshot()
        {
            var copy = new Node[_nodes.Count];
            for (int i = 0; i < _nodes.Count; i++)
            {
                Node source = _nodes[i];
                var clone = new Node
                {
                    Position = source.Position,
                    Radius = source.Radius,
                    Parent = source.Parent,
                    MirrorTwin = source.MirrorTwin,
                    Alive = source.Alive
                };
                clone.Children.AddRange(source.Children);
                copy[i] = clone;
            }
            return copy;
        }

        /// Replaces the whole tree with a snapshot. Indices in the snapshot are self-consistent
        /// (they were this list's own), so nothing needs remapping - which is the other reason
        /// deleted nodes are tombstoned rather than compacted.
        public void Restore(Node[] snapshot)
        {
            _nodes.Clear();
            AliveCount = 0;
            if (snapshot != null)
            {
                for (int i = 0; i < snapshot.Length; i++)
                {
                    Node source = snapshot[i];
                    var clone = new Node
                    {
                        Position = source.Position,
                        Radius = source.Radius,
                        Parent = source.Parent,
                        MirrorTwin = source.MirrorTwin,
                        Alive = source.Alive
                    };
                    clone.Children.AddRange(source.Children);
                    _nodes.Add(clone);
                    if (clone.Alive) AliveCount++;
                }
            }
            Version++;
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
        /// length below the dragged node is preserved.
        public void TranslateSubtree(int index, Vector3 delta)
        {
            if (!IsAlive(index)) return;
            var subtree = new List<int>();
            CollectSubtree(index, subtree);
            for (int i = 0; i < subtree.Count; i++) _nodes[subtree[i]].Position += delta;
            Version++;
        }

        /// Swings `index` and its subtree about `pivot` - the Pose-mode primitive. A pure
        /// rotation about the parent joint is exactly what makes a ZSphere chain behave like a
        /// skeleton: every bone below keeps its length and its relative angle, so posing an arm
        /// carries the hand along instead of stretching it.
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

        /// Appends `index` and every live descendant to `into` (breadth-first). The list belongs
        /// to the caller, so a drag that runs this every frame can reuse one buffer.
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

        // -------------------------------------------------------------------------- symmetry

        /// Makes a and b each other's mirror twin, dropping whatever either was paired with
        /// before. Mutual by construction, so an edit applied to one can always find the other
        /// regardless of which side the user grabbed.
        public void LinkTwins(int a, int b)
        {
            if (!IsAlive(a) || !IsAlive(b) || a == b) return;
            Unlink(a);
            Unlink(b);
            _nodes[a].MirrorTwin = b;
            _nodes[b].MirrorTwin = a;
            Version++;
        }

        public void Unlink(int index)
        {
            if (index < 0 || index >= _nodes.Count) return;
            int twin = _nodes[index].MirrorTwin;
            if (twin >= 0 && twin < _nodes.Count && _nodes[twin].MirrorTwin == index)
                _nodes[twin].MirrorTwin = NoNode;
            _nodes[index].MirrorTwin = NoNode;
        }

        public int TwinOf(int index)
        {
            Node node = Get(index);
            if (node == null) return NoNode;
            return IsAlive(node.MirrorTwin) ? node.MirrorTwin : NoNode;
        }

        // --------------------------------------------------------------------------- queries

        /// Bounds enclosing every live sphere - its centre AND its radius - or a zero-size box at
        /// the origin for an empty rig. This is what the skinner sizes its voxel grid from.
        public Bounds ComputeBounds()
        {
            Vector3 min = Vector3.zero, max = Vector3.zero;
            bool any = false;
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (!_nodes[i].Alive) continue;
                Vector3 p = _nodes[i].Position;
                float r = _nodes[i].Radius;
                Vector3 lo = p - Vector3.one * r, hi = p + Vector3.one * r;
                if (!any) { min = lo; max = hi; any = true; }
                else { min = Vector3.Min(min, lo); max = Vector3.Max(max, hi); }
            }
            var bounds = new Bounds();
            if (any) bounds.SetMinMax(min, max);
            return bounds;
        }

        /// Smallest live radius - drives the skinner's Adaptive resolution, which sizes voxels so
        /// the thinnest sphere in the rig still survives the grid.
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
