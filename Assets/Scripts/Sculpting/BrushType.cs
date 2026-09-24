namespace Sculpting
{
    public enum BrushType
    {
        Move,
        Clay,
        Smooth,
        Crease,
        Inflate,
        Flatten,
        Pose,
        // Appended, never inserted: per-brush settings are saved as arrays indexed by this enum.
        Standard,
        Layer,
        SnakeHook
    }
}
