using System.Collections.Generic;
using UnityEngine;

namespace Sculpting
{
    /// Picks a scene-unique name for a newly created sculptable object. Names are what the
    /// Scene panel's object list shows, and the only way to tell two identically-shaped objects
    /// apart, so a duplicate name defeats the point of an operation you intend to keep both
    /// halves of (Clone, Extract).
    ///
    /// Shared rather than reimplemented per feature - MeshCloner had this logic first, and
    /// MaskExtractController needed exactly the same "append (n) until it's free" rule with a
    /// different suffix, which is precisely the kind of thing that drifts into two subtly
    /// different implementations if copied.
    public static class ObjectNaming
    {
        /// "Head Extract" -> "Head Extract" if free, else "Head Extract (2)", "(3)"... Gives up
        /// after a sane number of attempts and returns the plain desired name rather than
        /// looping forever - a scene with 1000 same-named objects has bigger problems than a
        /// duplicate label, and returning something is better than hanging.
        public static string Unique(string desired)
        {
            var taken = new HashSet<string>();
            SelectionManager selection = Object.FindFirstObjectByType<SelectionManager>();
            if (selection != null)
                foreach (SculptableMesh obj in selection.AllObjects)
                    if (obj != null) taken.Add(obj.name);

            if (!taken.Contains(desired)) return desired;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = $"{desired} ({i})";
                if (!taken.Contains(candidate)) return candidate;
            }
            return desired;
        }
    }
}
