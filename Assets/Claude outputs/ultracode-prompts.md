# Ultracode prompts — sculpting app deformation work

Four scoped passes, in the order worth running them. Run **one per session**. Each assumes a clean
working tree and a passing test suite at the start.

Shared context block — paste at the top of whichever prompt you run:

> **Project:** standalone 3D sculpting app, Unity + C#, repo root `D:\Unity\sculptingapplication`.
> The deformation system lives in `Assets/Scripts/Sculpting/`, principally the `SculptController`
> partial class (`SculptController.cs`, `.Brushes.cs`, `.Jobs.cs`, `.Input.cs`, `.Settings.cs`) and
> `SculptableMesh.cs`.
>
> **This codebase is not a first draft.** The brush hot path has already been through several rounds
> of profiling and correctness work: drift-filtered dirty sets, footprint-scoped normal recompute
> over CSR adjacency, compute-shader scatter writes instead of full vertex-buffer reuploads, lazily
> reconciled spatial indices, Burst jobs behind parity tests. Many comments in these files document
> specific regressions that were found and fixed — the Clay `Clamp01` overshoot guard, the stroke
> pacing reference rescale, the mirror ordering rules. **Read the comments before changing the code
> they sit on.** If you think a comment describes an unnecessary precaution, say so and ask; do not
> quietly delete it.
>
> **Permanently off limits unless the task below names them:** `SculptableMesh.ApplyDirtyVertexList`
> and the drift/sync bookkeeping around it, `GpuVertexScatter`, the spatial index classes
> (`TriangleSpatialGrid`, `VertexSpatialGrid`), `MeshRemesher` / `SparseRemesher` /
> `SignedDistanceField`, and anything under `Assets/Scripts/UI/`.
>
> **Verification gate for every pass:** these must pass before you call the work done —
> `Assets/Tests/Editor/SculptControllerJobParityTests.cs`, `SymmetryDriftTests.cs`,
> `MirrorLinkTests.cs`, `RemeshNormalTests.cs`.

---

## Pass 1 — Collapse the Job/Managed duplication

Run this first. It is large, mechanical, and verifiable, and it removes the biggest maintenance
liability in the file.

> **Task: eliminate the duplicated managed brush implementations in favour of a single
> job-struct implementation per brush.**
>
> **Current state.** Six brushes each carry two hand-maintained implementations of the same
> displacement math, in `SculptController.Brushes.cs`:
>
> | Brush | Job path | Managed path |
> |---|---|---|
> | Surface relax | `ApplySurfaceRelaxLocalJob` (~1443) | `ApplySurfaceRelaxLocalManaged` (~1564) |
> | Clay | `ApplyClayBrushLocalJob` (~1726) | `ApplyClayBrushLocalManaged` (~1807) |
> | Carve/Crease | `ApplyCarveDabLocalJob` (~2216) | `ApplyCarveDabLocalManaged` (~2254) |
> | Inflate | `ApplyInflateBrushLocalJob` (~2420) | `ApplyInflateBrushLocalManaged` (~2449) |
> | Flatten | `ApplyFlattenBrushLocalJob` (~2543) | `ApplyFlattenBrushLocalManaged` (~2615) |
> | Smooth | `ApplySmoothBrushLocalJob` (~2749) | `ApplySmoothBrushLocalManaged` (~2852) |
>
> Each dispatcher picks between them on `useBurstJobs && candidates.Count >= MinJobVertexCount`
> (`MinJobVertexCount = 256`, `SculptController.Jobs.cs:16`). That is roughly 900 lines of doubled
> math kept honest only by `SculptControllerJobParityTests`.
>
> **Goal.** One implementation per brush — the `IJobParallelFor` struct in `SculptController.Jobs.cs`.
> Below the threshold, run that same struct with `.Run()` (main-thread execution, still fully
> Burst-compiled, no scheduling overhead) instead of dispatching to a hand-written C# twin. Delete
> the managed twins and their private scratch arrays (`_clayWeightScratch`, `_smoothWeightScratch`,
> `_relaxWeightScratch`, and any others left with no reader).
>
> **Before you delete anything — capture golden values.** The parity tests currently work by
> comparing the two paths against each other, and that comparison stops existing the moment one path
> is gone. So first: add a test that drives each brush over a fixed seeded mesh with fixed inputs and
> writes the resulting vertex positions to a committed golden file. Confirm both the current managed
> and current job paths reproduce it. Only then start removing code. That golden file is what proves
> the refactor changed nothing.
>
> **Requirements.**
> 1. Every path continues to funnel through `GatherCandidatesNative` and `ScatterJobResults`
>    (`SculptController.Jobs.cs:217` / `:240`), so `RecordUndoBeforeIfNeeded`, `_dirtyVertexScratch`
>    marking, and `MarkPositionMirrorStale` keep identical semantics.
> 2. Each job's `AppliedOut` flag becomes the *sole* definition of "this vertex moved, mark it
>    dirty." Verify against each managed path's `continue` conditions before you delete it, and
>    preserve the per-brush remarks documenting what each condition mirrors.
> 3. Native scratch stays `Allocator.Persistent` and sized by `EnsureNativeScratch`. A small-footprint
>    dab must not allocate. Confirm with the Profiler that GC alloc on a small-radius stroke is zero.
> 4. `[BurstCompile(CompileSynchronously = true)]` stays on every struct, for the reason documented
>    at `SculptController.Jobs.cs:262`.
>
> **Two real behaviour changes you must surface, not bury.**
> - **Smooth changes convergence style below 256 candidates.** The managed path is Gauss-Seidel
>   (in-place, candidate N sees N−1's updated position); `SmoothRelaxJob` is Jacobi (ping-ponged
>   buffers). Today the brush silently switches between them by footprint size — which is itself a
>   bug. After this pass everything is Jacobi. Measure the difference on a small-radius stroke at
>   maximum strength and report it. If it is visible, say so and propose the fix (more passes, or a
>   red-black ordering) rather than shipping it quietly.
> - **Alpha sampling may differ.** The managed path calls `BrushAlphaLibrary.Sample`; the job path
>   uses `ClayDisplacementJob.SampleAlphaBilinear` over `_nativeAlphaSamples`. Check whether these
>   agree numerically. If they don't, keep the job's and report the delta.
>
> **Decide and state explicitly:** what happens to the `useBurstJobs` field
> (`SculptController.cs:257`) and `MinJobVertexCount`. Either keep them as a `Schedule()`-vs-`Run()`
> debug toggle with a threshold, or remove both. Do not leave a half-dead serialized field wired to
> a UI control that no longer does anything — check `SculptUIBuilder.cs` for its binding either way.
>
> **Performance requirement.** A small-radius stroke (footprint under 256 vertices) must not regress.
> Report before/after timings using the existing `ProfilerMarker`s, over a stroke of at least 100
> frames, on a mesh of at least 200k triangles.
>
> **Process.** One brush per commit, simplest first: Inflate → Flatten → Carve → Clay → SurfaceRelax
> → Smooth. Full test suite green after each. Do not batch them into one commit.

---

## Pass 2 — Put every brush on distance-spaced dabs

> **Task: finish the migration from per-frame time-based deposition to distance-spaced dabs.**
>
> **Current state.** Clay (`ApplyClayStroke`) and Crease (`ApplyCarveStroke`) place a fixed quantum
> of material every N units of cursor travel. Inflate, Flatten, Smooth and Move still deposit once
> per rendered frame, scaled by `Time.deltaTime`. To compensate, `SculptController.Brushes.cs` carries
> a stack of speed-pacing heuristics — `AccumulateSpeedFactor`, `StrokePacingReference`,
> `StrokePacingCeiling`, `StrokePacingGain`, `AccumulateSpeedFloor`,
> `EffectiveBrushStrengthPlateau`, and the `buildUpOnHold` branch through all of them. The comment
> at roughly line 311 states plainly that distance-based spacing is the real fix and this is the
> mitigation.
>
> **Goal.** One stroke stepper, shared by every brush: accumulate cursor travel in local space, emit
> a dab each time travel crosses that brush's spacing, carry the remainder into the next frame.
> Frame rate stops affecting what a stroke deposits. Read `ApplyClayStroke` and `ApplyCarveStroke`
> first — they are the two working examples, and the shared stepper should be a generalisation of
> them, not a third invention.
>
> **Requirements.**
> 1. Deposit per unit of travel must be identical at 30fps and 144fps. Prove it with a test that
>    drives the same synthetic stroke path at both fixed timesteps and compares resulting vertex
>    positions within tolerance.
> 2. Spacing is per-brush and expressed in brush diameters, not world units — scene scale and brush
>    size must not change stroke character (see the `StrokePacingReference` remarks for why).
> 3. `buildUpOnHold` keeps working: a stationary cursor feeds the stepper virtual travel, exactly as
>    Crease already does, rather than being special-cased in the strength math.
> 4. Per-frame dab counts stay capped (see `ClayMaxDabsPerFrame` / `CreaseMaxDabsPerFrame`) so a
>    cursor teleport or a frame hitch cannot emit hundreds of dabs at once.
> 5. Smooth and Move deliberately keep "holding in place keeps working the area" behaviour — see the
>    existing remarks on why they were excluded from speed pacing. Getting them onto the stepper
>    without losing that is part of the task, not a reason to skip them.
> 6. Delete every pacing heuristic that has no reader once the migration is done. If a constant
>    survives, justify it in a comment.
>
> **Report** which brushes changed feel and how, with the tuning constants you landed on. This is a
> subjective change; give me the knobs, don't just declare it done.

---

## Pass 3 — Volume-preserving smooth

Small, cheap, immediately visible. Good candidate to run right after Pass 1.

> **Task: stop the Smooth brush from shrinking the model.**
>
> `SmoothRelaxJob` (`SculptController.Jobs.cs:823`) lerps each vertex toward the raw average of its
> neighbours — a plain umbrella-operator Laplacian, which removes volume along with the noise. Sharp
> forms flatten and the silhouette pulls inward under repeated passes.
>
> **Goal.** Project the relaxation move onto the vertex's tangent plane, so vertices slide across the
> surface to even out topology without moving along the normal:
> `delta -= normal * dot(delta, normal)`. Expose the split as a brush parameter (0 = pure tangential
> relax, 1 = current full Laplacian), defaulting to mostly-tangential.
>
> **Notes.**
> - `SculptController.Brushes.cs` already has the good version of this idea for Clay's `surfaceRelax`
>   pass — read `RelaxWeightJob` and `ApplySurfaceRelaxBatched` first and reuse their shape rather
>   than inventing a parallel mechanism.
> - Vertex normals must be current when the projection runs. Check the ordering against
>   `SculptableMesh.RefreshNormalsAndCurvature` — relaxing against stale normals will drift.
> - Add a test that smooths a UV sphere to convergence and asserts enclosed volume stays within a
>   small tolerance of the original. That test is the whole point of the pass.
> - Wire the new parameter into `SculptUIBuilder` alongside the existing brush sliders, with a
>   tooltip, matching how `SurfaceRelax` is already presented.

---

## Pass 4 — Local adaptive topology (scoping brief, not a one-shot)

Do **not** hand this to a single autonomous run. Ask for a written plan first, review it, then
implement in separate passes.

> **Task: produce an implementation plan (no code yet) for local adaptive retopology during a stroke.**
>
> Today `Remesh()` (`SculptController.Settings.cs:19` → `SculptableMesh.Remesh`) is a global, manual,
> uniform voxel rebuild at a chosen resolution. Nothing tessellates locally during a stroke. The
> consequences: you cannot pull a spike or a long extrusion, detail is capped by current local vertex
> density, and adding detail anywhere means remeshing the entire model back to uniform density and
> losing density gradients everywhere else.
>
> **Produce a written plan covering:**
> 1. Options assessment — local remesh of the dirty footprint via the existing `SparseRemesher` /
>    `SurfaceNets` path, versus dynamic edge split/collapse on the triangle mesh directly. Trade-offs
>    for each against this codebase specifically, not in the abstract.
> 2. How the chosen approach interacts with each of: the delta-based undo history
>    (`SculptHistory` / `EditHistory` — mid-stroke topology changes resize the mesh under indices
>    gathered earlier, see the note at `SculptController.Brushes.cs:800`), `MirrorLink` and
>    `SymmetryMap` (vertex correspondence across a topology change), the mask array, hidden-triangle
>    visibility state, and the spatial indices.
> 3. Where it runs in the frame, and the budget: what happens when the retopo step exceeds it.
> 4. A staged implementation order where each stage is independently shippable and testable.
> 5. Honest assessment of what this breaks and what it costs. If your conclusion is that the undo
>    system has to be rebuilt first, say that.
>
> Read `SparseRemesher.cs`, `SurfaceNetsTopology.cs`, `MeshRemesher.cs`, `SculptHistory.cs` and
> `SymmetryMap.cs` before writing anything.

---

## Also worth queuing (not blocking any of the above)

`BrushType` currently has seven entries: Move, Clay, Smooth, Crease, Inflate, Flatten, Pose. Pinch,
Snake Hook, Layer and Scrape/Fill all sit naturally on the existing dab + falloff + mirror
framework and buy more perceived depth than any single item above. Best run *after* Pass 1, so each
new brush is one implementation rather than two.
