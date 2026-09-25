using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Sculpting
{
    /// Runs a per-vertex pass across the thread pool when the footprint is big enough to be worth
    /// it, and inline on the calling thread when it is not.
    ///
    /// Exists for the two passes at the bottom of every brush frame - the normal recompute and
    /// the cavity recompute in SculptableMesh - which together were 29 ms of a 52 ms apply on a
    /// 335k-triangle mesh under a brush wide enough to cover the whole model. Both are pure
    /// gathers: each entry reads shared, unchanging arrays and writes only its own slot, so
    /// splitting them across cores gives bit-identical output. Measured on a 32-core machine at
    /// that footprint: normals 14.5 ms -> 1.3 ms, cavity 14.5 ms -> 1.8 ms.
    ///
    /// Also used for the other per-vertex passes on that path that have the same shape: the GPU
    /// scatter's staging (GpuVertexScatter) and the drift-filter baseline update.
    ///
    /// NOT Burst jobs, which is what every BRUSH in this project uses. Those want NativeArrays,
    /// and the data these passes walk (MeshAdjacency's neighbour and incident-triangle arrays, the
    /// working positions, normals and cavity buffers) lives in managed arrays that every other
    /// system reads and writes directly - mirroring all of it into native memory and keeping the
    /// copies in step with every edit would be a large, permanent cost for the same win
    /// Parallel.For gets over the arrays that already exist.
    ///
    /// THREAD SAFETY IS THE CALLER'S. Nothing here checks it: `body` must not touch any Unity
    /// API, must not write anything two blocks could both reach, and must not read anything
    /// another block writes. Every current caller writes strictly to `[i]` for the distinct
    /// indices in its own range.
    internal static class ParallelPass
    {
        /// Below this the pool's own wake-and-join costs more than the loop it is splitting.
        /// Measured against the normal recompute on a 32-core machine: 500 entries came out 0.84x
        /// (slower), 1000 at 1.6x, 2000 at 2.6x. 1024 sits just past the crossover, so an
        /// ordinary small-brush dab - which is most of them - never pays for a thread it did not
        /// need.
        private const int MinItemsToSplit = 1024;

        /// Blocks per core. More than one so a core that draws an unlucky (denser, higher
        /// valence) block is not the one everything waits on; not so many that per-block
        /// overhead starts to show.
        private const int BlocksPerCore = 4;
        private const int MinBlockSize = 512;

        private static int _cores;
        // Rebuilt from zero rather than cached across a domain reload - a static int survives one
        // anyway, but reading it from SystemInfo on first use keeps this file free of any
        // reload-ordering assumption (see the domain-reload note on SculptableMesh's own caches).
        private static int Cores => _cores != 0 ? _cores : (_cores = Mathf.Max(1, SystemInfo.processorCount));

        /// Calls `body(start, end)` over consecutive half-open blocks covering [0, count).
        /// Single-threaded for a small count, or on a machine with too few cores to gain from
        /// splitting - in which case `body` is called exactly once, with the whole range.
        public static void ForRange(int count, Action<int, int> body)
        {
            if (count <= 0) return;

            int cores = Cores;
            if (count < MinItemsToSplit || cores < 3)
            {
                body(0, count);
                return;
            }

            int blockSize = Mathf.Max(MinBlockSize, count / (cores * BlocksPerCore));
            int blocks = (count + blockSize - 1) / blockSize;

            Parallel.For(0, blocks, block =>
            {
                int start = block * blockSize;
                body(start, Mathf.Min(start + blockSize, count));
            });
        }
    }
}
