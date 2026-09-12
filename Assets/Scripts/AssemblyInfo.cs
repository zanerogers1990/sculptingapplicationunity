using System.Runtime.CompilerServices;

// The EditMode tests live in Unity's predefined Assembly-CSharp-Editor, which is a separate
// assembly from the runtime one - so the spatial grids, which are deliberately `internal` (nothing
// outside the sculpting pipeline has any business holding one), are invisible to them without
// this. They are worth testing directly rather than only through SculptableMesh: both are now
// MUTABLE - dynamic topology appends vertices and triangles to them mid-stroke - and a bucket left
// in the wrong cell shows up as a brush that silently skips geometry rather than as an exception.
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]
