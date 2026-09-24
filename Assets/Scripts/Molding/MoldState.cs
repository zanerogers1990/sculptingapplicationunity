using System.Collections.Generic;
using System.Reflection;

namespace Sculpting.Molding
{
    /// Everything a mold edit can change, captured as a value - the undo payload for every step
    /// the mold workspace records.
    ///
    /// Whole-state snapshots rather than per-operation inverses: the feature list is a handful of
    /// small objects and the settings are a few dozen numbers, so a copy costs nothing
    /// measurable, and one mechanism then covers every control in the workspace identically -
    /// a placed pin, a dragged surface, a slider, a toggle, a mirror button, the scale field.
    /// Per-operation inverses would be thirty chances to get an edge case wrong for no saving.
    ///
    /// A step restores only what IT changed. Undoing a pin placement must not also flip the view
    /// back to whatever it was before the pin went down, or put back a padding the user has
    /// since changed in a later step that is being kept - so Compare records which parts of the
    /// state differ between a step's before and after, and Apply writes only those. That is also
    /// what lets the controller do the least recompute an undo needs: a step that only moved a
    /// vent never re-fits the parting surface.
    internal sealed class MoldState
    {
        public MoldSettings Settings;
        public List<MoldFeature> Features;
        public float ManualOffset;
        public MoldFrame Frame;

        private static readonly FieldInfo[] SettingsFields =
            typeof(MoldSettings).GetFields(BindingFlags.Public | BindingFlags.Instance);

        public static MoldState Capture(MoldSettings settings, IReadOnlyList<MoldFeature> features,
                                        float manualOffset, MoldFrame frame)
        {
            var list = new List<MoldFeature>(features != null ? features.Count : 0);
            if (features != null)
                foreach (MoldFeature f in features)
                    if (f != null) list.Add(f.Clone());

            return new MoldState
            {
                Settings = settings.Clone(),
                Features = list,
                ManualOffset = manualOffset,
                Frame = frame,
            };
        }

        /// Which parts of the state differ between two snapshots.
        public sealed class Delta
        {
            public readonly List<FieldInfo> SettingsFields = new List<FieldInfo>();
            public bool Features;
            public bool Offset;
            public bool Frame;

            public bool Any => Features || Offset || Frame || SettingsFields.Count > 0;

            /// True when any changed setting is one of `names`.
            public bool TouchesAny(HashSet<string> names)
            {
                foreach (FieldInfo f in SettingsFields)
                    if (names.Contains(f.Name)) return true;
                return false;
            }

            /// True when every changed setting is one of `names` and nothing else changed.
            public bool OnlySettingsIn(HashSet<string> names)
            {
                if (Features || Offset || Frame) return false;
                foreach (FieldInfo f in SettingsFields)
                    if (!names.Contains(f.Name)) return false;
                return true;
            }
        }

        public static Delta Compare(MoldState a, MoldState b)
        {
            var delta = new Delta();
            foreach (FieldInfo field in SettingsFields)
                if (!Equals(field.GetValue(a.Settings), field.GetValue(b.Settings)))
                    delta.SettingsFields.Add(field);

            delta.Offset = a.ManualOffset != b.ManualOffset;
            delta.Frame = a.Frame.Right != b.Frame.Right || a.Frame.Up != b.Frame.Up || a.Frame.Eye != b.Frame.Eye;

            if (a.Features.Count != b.Features.Count)
            {
                delta.Features = true;
            }
            else
            {
                for (int i = 0; i < a.Features.Count; i++)
                {
                    if (a.Features[i].SameState(b.Features[i])) continue;
                    delta.Features = true;
                    break;
                }
            }
            return delta;
        }

        /// Writes this snapshot's value of every part named in `delta` into the live state.
        public void ApplyTo(Delta delta, MoldSettings settings, List<MoldFeature> features,
                            ref float manualOffset, ref MoldFrame frame)
        {
            foreach (FieldInfo field in delta.SettingsFields)
                field.SetValue(settings, field.GetValue(Settings));

            if (delta.Features)
            {
                features.Clear();
                foreach (MoldFeature f in Features) features.Add(f.Clone());
            }
            if (delta.Offset) manualOffset = ManualOffset;
            if (delta.Frame) frame = Frame;
        }

        /// Rough bytes held alive, for EditHistory's budget.
        public long ApproxBytes => 96L * (Features.Count + 1) + 512L;
    }
}
