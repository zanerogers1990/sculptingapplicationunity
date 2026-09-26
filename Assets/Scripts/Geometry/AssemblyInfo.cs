using System.Runtime.CompilerServices;

// Sculpting.Geometry is the pure mesh-algorithm layer: remeshing (SDF, surface nets, dual
// contouring), trim and boolean, adjacency, the spatial grids, the symmetry map and repair, lathe
// and SSphere skin generation, and the vector/brush math the brushes share. It is its own assembly
// so that layering is enforced by the compiler rather than by convention: nothing in here can
// reach a MonoBehaviour, the scene, the UI or IO, because Assembly-CSharp - where all of those
// live - is not something an asmdef assembly can reference. It is also no longer recompiled on
// every edit to the rest of the app.
//
// Several types here are `internal` and used by the app (SculptController's Burst jobs call
// BrushMath; SculptableMesh owns the spatial grids), and the editor tests reach internals too.
[assembly: InternalsVisibleTo("Assembly-CSharp")]
[assembly: InternalsVisibleTo("Assembly-CSharp-Editor")]
