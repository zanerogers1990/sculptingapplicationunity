using UnityEngine;

namespace Sculpting
{
    /// Places the single dual vertex a Surface Nets / Dual Contouring cell owns, given the
    /// points where the surface crosses that cell's edges and the surface NORMAL at each of
    /// those points.
    ///
    /// Why this exists: plain Surface Nets puts the vertex at the AVERAGE of the crossing
    /// points. That average is a low-pass filter over the cell, and it chamfers every sharp
    /// feature by a fixed fraction of a voxel no matter how fine the grid is. Measured on a
    /// box rotated off the lattice (31,17,9 degrees) so its edges cross the grid diagonally,
    /// the averaged reconstruction sat 0.42 cells inside the true crease on average and up to
    /// 0.91 cells at the corners - and those numbers were CONSTANT in cells across resolutions
    /// 32/64/96/128. Constant-in-cells is the signature of a systematic bias rather than a
    /// sampling limit: raising the resolution buys smaller voxels but the same proportional
    /// rounding, which is exactly why "remesh smooths my detail away" does not go away when
    /// you turn the density up.
    ///
    /// What this does instead: each crossing point p with surface normal n says the true
    /// surface locally satisfies n . (x - p) = 0. Minimising the sum of squares of those
    /// residuals - the quadratic error function (QEF) - puts the vertex on the intersection of
    /// the planes, which for a cell straddling a crease is the crease itself, and for a cell
    /// straddling a corner is the corner point. Flat and smoothly curved regions are
    /// UNDERDETERMINED by design, and that is handled by truncating the pseudo-inverse (below)
    /// rather than by regularising toward the average, so they stay exactly as smooth as
    /// Surface Nets made them while creases become sharp.
    ///
    /// Pure math over plain floats - no Unity object touched - so it is safe to call from the
    /// worker threads the remesher parallelises over.
    internal static class DualContourSolver
    {
        /// How far outside its own cell the solved vertex may land, in cell-fractions. A QEF
        /// whose planes are nearly parallel has a solution far up the shared near-null
        /// direction, and letting it fly produces spikes and self-intersections. Clamping to
        /// slightly outside the cell (rather than exactly to it) lets a crease that genuinely
        /// passes just past a corner still be represented, which visibly matters on chamfers
        /// and on creases running close to a cell boundary.
        private const float ClampMargin = 0.25f;

        /// Regularisation strength, as a fraction of the largest eigenvalue. This is the whole
        /// reason a plain 3x3 solve is not enough: on a flat patch the normal matrix has rank 1,
        /// on a crease rank 2, and only at a corner rank 3, so inverting it directly divides by
        /// something arbitrarily close to zero.
        ///
        /// Applied as a DAMPED inverse - lambda / (lambda^2 + eps^2) in place of 1 / lambda -
        /// rather than by discarding eigenvalues below a cutoff. Both suppress the near-null
        /// directions, but discarding is a step function of the input, and this solve is
        /// genuinely ill-conditioned near that step: measured over 200,000 randomised cells,
        /// perturbing the input normals by 1e-5 (the scale of the difference between two equally
        /// valid nearest-triangle picks on a smooth surface) moved the truncated solution by up
        /// to 0.51 of a cell. Damping is continuous in the eigenvalue, so a negligible change to
        /// the input can only make a negligible change to the answer, and it bounds the
        /// amplification at 1/(2*eps) instead of leaving it unbounded just above the cutoff.
        ///
        /// This matters well beyond tidiness. A discontinuous placement rule makes neighbouring
        /// cells on the same smooth surface disagree, and it broke MIRROR SYMMETRY: the solver
        /// itself is exactly mirror-equivariant (worst case 6e-7 of a cell over those same
        /// trials), but the nearest-triangle tie-breaks that feed it are not, and truncation
        /// turned those seven-decimal differences into quarter-cell ones on 11% of the vertices
        /// of a symmetric model - past the 5% unmatched guard SymmetryOps refuses to mirror
        /// through.
        private const float DampingFraction = 0.03f;

        /// How aligned the crossing normals have to be for the cell to be treated as locally
        /// planar. cos 5.7 degrees, and the value is not delicate: swept from cos 1.8 degrees
        /// down to cos 26 degrees, the sharp-feature numbers on a box rotated off the lattice do
        /// not move at all (a real crease is far above any of them), while tightening it past
        /// this point starts sending merely-CURVED cells through the general solve, where they
        /// gain nothing and lose a little mirror symmetry. So: loose enough that only genuine
        /// creases take the long path, tight enough that they all do.
        private const float PlanarCosine = 0.995f;

        /// Solves for the dual vertex of one cell, in the cell's own local [0,1]^3 coordinates.
        ///
        /// `points` and `normals` hold `count` crossing samples, also in cell-local
        /// coordinates. Returns the position to place the vertex at.
        public static Vector3 Solve(Vector3[] points, Vector3[] normals, int count)
        {
            Vector3 mass = Vector3.zero;
            Vector3 normalSum = Vector3.zero;
            for (int i = 0; i < count; i++) { mass += points[i]; normalSum += normals[i]; }
            mass /= count;

            // Flat and smoothly curved cells - the overwhelming majority of any model, sharp
            // or not - have all their crossing normals pointing essentially the same way. There
            // the normal matrix is rank 1 and the general solve below reduces to sliding the
            // mass point along that one direction, so compute that directly instead of running
            // an eigen-decomposition to be told the same thing. This is what keeps the
            // feature-preserving placement from costing anything on the parts of the model that
            // have no feature to preserve; the full path still runs wherever normals disagree,
            // which is exactly where creases are.
            float sumLength = normalSum.magnitude;
            if (sumLength >= count * PlanarCosine)
            {
                Vector3 axis = normalSum / sumLength;
                float along = 0f;
                for (int i = 0; i < count; i++) along += Vector3.Dot(axis, points[i] - mass);
                // Carries the same damping factor the general path applies to its dominant
                // direction, so the two agree where they meet and the fast path is a shortcut
                // rather than a second, slightly different rule.
                Vector3 planar = mass + axis * (along / count / (1f + DampingFraction * DampingFraction));
                return new Vector3(
                    Mathf.Clamp(planar.x, -ClampMargin, 1f + ClampMargin),
                    Mathf.Clamp(planar.y, -ClampMargin, 1f + ClampMargin),
                    Mathf.Clamp(planar.z, -ClampMargin, 1f + ClampMargin));
            }

            // Normal equations of the least-squares system, accumulated about the MASS POINT
            // rather than about the origin. Same minimiser, far better conditioned: the
            // residuals are then all small, so the sums below do not lose precision to
            // catastrophic cancellation between large nearly-equal terms.
            float axx = 0f, axy = 0f, axz = 0f, ayy = 0f, ayz = 0f, azz = 0f;
            float bx = 0f, by = 0f, bz = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector3 n = normals[i];
                float d = Vector3.Dot(n, points[i] - mass);
                axx += n.x * n.x; axy += n.x * n.y; axz += n.x * n.z;
                ayy += n.y * n.y; ayz += n.y * n.z; azz += n.z * n.z;
                bx += n.x * d; by += n.y * d; bz += n.z * d;
            }

            SymmetricEigen(axx, axy, axz, ayy, ayz, azz,
                           out Vector3 e0, out Vector3 e1, out Vector3 e2,
                           out float l0, out float l1, out float l2);

            float largest = Mathf.Max(Mathf.Abs(l0), Mathf.Abs(l1), Mathf.Abs(l2));
            if (largest <= 1e-12f) return mass; // no usable normal information at all

            float eps = largest * DampingFraction;
            float epsSqr = eps * eps;
            var b = new Vector3(bx, by, bz);

            // x = mass + V * diag(lambda / (lambda^2 + eps^2)) * V^T * b.
            Vector3 offset = e0 * (Vector3.Dot(e0, b) * l0 / (l0 * l0 + epsSqr))
                           + e1 * (Vector3.Dot(e1, b) * l1 / (l1 * l1 + epsSqr))
                           + e2 * (Vector3.Dot(e2, b) * l2 / (l2 * l2 + epsSqr));

            Vector3 result = mass + offset;

            return new Vector3(
                Mathf.Clamp(result.x, -ClampMargin, 1f + ClampMargin),
                Mathf.Clamp(result.y, -ClampMargin, 1f + ClampMargin),
                Mathf.Clamp(result.z, -ClampMargin, 1f + ClampMargin));
        }

        /// Eigen-decomposition of a real symmetric 3x3 matrix by cyclic Jacobi rotations.
        /// Chosen over a closed-form cubic-root solution because this runs once per output
        /// vertex - hundreds of thousands to millions of times per remesh - on matrices that
        /// are routinely rank-deficient, and Jacobi stays accurate on exactly the degenerate
        /// (flat patch, straight crease) cases that dominate the input, where the closed form
        /// loses precision to cancellation under the cube root.
        ///
        /// Six sweeps is comfortably past convergence for 3x3 - the off-diagonal mass falls
        /// quadratically - and a fixed count keeps every worker thread on the same code path.
        private static void SymmetricEigen(
            float axx, float axy, float axz, float ayy, float ayz, float azz,
            out Vector3 e0, out Vector3 e1, out Vector3 e2,
            out float l0, out float l1, out float l2)
        {
            // Working copy of the matrix (m01 == m10 etc. by symmetry, so only six entries).
            float m00 = axx, m01 = axy, m02 = axz, m11 = ayy, m12 = ayz, m22 = azz;
            // Accumulated rotation; its columns end up as the eigenvectors.
            float v00 = 1f, v01 = 0f, v02 = 0f;
            float v10 = 0f, v11 = 1f, v12 = 0f;
            float v20 = 0f, v21 = 0f, v22 = 1f;

            for (int sweep = 0; sweep < 6; sweep++)
            {
                float off = m01 * m01 + m02 * m02 + m12 * m12;
                if (off <= 1e-20f) break;

                // (0,1)
                if (Mathf.Abs(m01) > 1e-20f)
                {
                    float theta = (m11 - m00) / (2f * m01);
                    float t = Mathf.Sign(theta) / (Mathf.Abs(theta) + Mathf.Sqrt(theta * theta + 1f));
                    float c = 1f / Mathf.Sqrt(t * t + 1f), s = t * c;
                    float n00 = c * c * m00 - 2f * s * c * m01 + s * s * m11;
                    float n11 = s * s * m00 + 2f * s * c * m01 + c * c * m11;
                    float n02 = c * m02 - s * m12;
                    float n12 = s * m02 + c * m12;
                    m00 = n00; m11 = n11; m01 = 0f; m02 = n02; m12 = n12;
                    float t00 = v00, t01 = v01; v00 = c * t00 - s * t01; v01 = s * t00 + c * t01;
                    float t10 = v10, t11 = v11; v10 = c * t10 - s * t11; v11 = s * t10 + c * t11;
                    float t20 = v20, t21 = v21; v20 = c * t20 - s * t21; v21 = s * t20 + c * t21;
                }

                // (0,2)
                if (Mathf.Abs(m02) > 1e-20f)
                {
                    float theta = (m22 - m00) / (2f * m02);
                    float t = Mathf.Sign(theta) / (Mathf.Abs(theta) + Mathf.Sqrt(theta * theta + 1f));
                    float c = 1f / Mathf.Sqrt(t * t + 1f), s = t * c;
                    float n00 = c * c * m00 - 2f * s * c * m02 + s * s * m22;
                    float n22 = s * s * m00 + 2f * s * c * m02 + c * c * m22;
                    float n01 = c * m01 - s * m12;
                    float n12 = s * m01 + c * m12;
                    m00 = n00; m22 = n22; m02 = 0f; m01 = n01; m12 = n12;
                    float t00 = v00, t02 = v02; v00 = c * t00 - s * t02; v02 = s * t00 + c * t02;
                    float t10 = v10, t12 = v12; v10 = c * t10 - s * t12; v12 = s * t10 + c * t12;
                    float t20 = v20, t22 = v22; v20 = c * t20 - s * t22; v22 = s * t20 + c * t22;
                }

                // (1,2)
                if (Mathf.Abs(m12) > 1e-20f)
                {
                    float theta = (m22 - m11) / (2f * m12);
                    float t = Mathf.Sign(theta) / (Mathf.Abs(theta) + Mathf.Sqrt(theta * theta + 1f));
                    float c = 1f / Mathf.Sqrt(t * t + 1f), s = t * c;
                    float n11 = c * c * m11 - 2f * s * c * m12 + s * s * m22;
                    float n22 = s * s * m11 + 2f * s * c * m12 + c * c * m22;
                    float n01 = c * m01 - s * m02;
                    float n02 = s * m01 + c * m02;
                    m11 = n11; m22 = n22; m12 = 0f; m01 = n01; m02 = n02;
                    float t01 = v01, t02 = v02; v01 = c * t01 - s * t02; v02 = s * t01 + c * t02;
                    float t11 = v11, t12 = v12; v11 = c * t11 - s * t12; v12 = s * t11 + c * t12;
                    float t21 = v21, t22 = v22; v21 = c * t21 - s * t22; v22 = s * t21 + c * t22;
                }
            }

            l0 = m00; l1 = m11; l2 = m22;
            e0 = new Vector3(v00, v10, v20);
            e1 = new Vector3(v01, v11, v21);
            e2 = new Vector3(v02, v12, v22);
        }
    }
}
