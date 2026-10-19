using System.Numerics;

namespace Aero.Core.Aero;

/// <summary>
/// Result of a panel method solve: Cp and force at each panel.
/// </summary>
public readonly struct PanelResult
{
    public readonly float[] Cp;
    public readonly Vector3 TotalForce;
    public readonly Vector3 TotalTorque;
    public readonly Vector3 CenterOfPressure;
    public readonly double SolveTimeMs;

    public PanelResult(float[] cp, Vector3 force, Vector3 torque, Vector3 cop, double solveMs)
    {
        Cp = cp;
        TotalForce = force;
        TotalTorque = torque;
        CenterOfPressure = cop;
        SolveTimeMs = solveMs;
    }
}

/// <summary>
/// 3D constant-source panel method (Hess-Smith type, sources only).
/// Solves the Laplace equation for incompressible potential flow around a
/// closed body defined by flat surface panels.
///
/// Physics:
///   - Each panel gets a constant source strength σ_i
///   - Boundary condition: zero normal velocity at each panel centroid
///   - Pressure from Bernoulli: Cp = 1 - |V_surface|² / |V_∞|²
///
/// Use case: offline accuracy reference for validating simplified drag models.
/// NOT for real-time — O(N²) build, O(N³) solve.
///
/// Limitations:
///   - Inviscid: no friction drag, no separation
///   - Incompressible: valid for M &lt; 0.3
///   - D'Alembert's paradox: total force ≈ 0 for closed body (expected!)
///     The value is in the Cp distribution, not the total force.
///   - Point-source panel approximation (accurate when panel spacing > panel size)
/// </summary>
public static class PanelMethodSolver
{
    /// <summary>
    /// Solve for the inviscid pressure distribution on the given surface faces.
    /// </summary>
    /// <param name="faces">Surface panels (position, normal, area).</param>
    /// <param name="freestream">Freestream velocity vector (m/s).</param>
    /// <param name="com">Center of mass for torque computation.</param>
    /// <returns>Cp at each panel + integrated forces.</returns>
    public static PanelResult Solve(IReadOnlyList<SurfaceFace> faces, Vector3 freestream, Vector3 com)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int N = faces.Count;
        float vInfSq = freestream.LengthSquared();
        if (N == 0 || vInfSq < 1e-8f)
            return new PanelResult(Array.Empty<float>(), Vector3.Zero, Vector3.Zero, com, 0);

        // ─── Build influence matrix ─────────────────────────────
        // A[i,j] = normal velocity at panel i due to unit source density on panel j
        // Diagonal: self-influence = 1/2 (half the source exits each side of flat panel)
        // Off-diagonal: point-source approximation

        var A = new double[N, N];
        var rhs = new double[N];
        const double INV_4PI = 1.0 / (4.0 * System.Math.PI);

        for (int i = 0; i < N; i++)
        {
            var ri = faces[i].Position;
            var ni = faces[i].Normal;

            // RHS: -V_∞ · n_i
            rhs[i] = -(freestream.X * ni.X + freestream.Y * ni.Y + freestream.Z * ni.Z);

            // Self-influence
            A[i, i] = 0.5;

            for (int j = 0; j < N; j++)
            {
                if (i == j) continue;

                var rj = faces[j].Position;
                float dx = ri.X - rj.X;
                float dy = ri.Y - rj.Y;
                float dz = ri.Z - rj.Z;
                double r2 = dx * dx + dy * dy + dz * dz;

                // Regularize: avoid singularity for very close panels
                if (r2 < 0.01) r2 = 0.01;

                double r = System.Math.Sqrt(r2);
                double r3 = r2 * r;

                // Normal velocity at i from point source at j:
                // V = σ * A_j * (r_i - r_j) / (4π |r|³)
                // V · n_i = σ * A_j * (r_i - r_j) · n_i / (4π |r|³)
                double dot = dx * ni.X + dy * ni.Y + dz * ni.Z;
                A[i, j] = INV_4PI * dot / r3 * faces[j].Area;
            }
        }

        // ─── Solve A · σ = rhs via Gauss elimination with partial pivoting ──
        var sigma = SolveLinearSystem(A, rhs, N);

        // ─── Compute surface velocity and Cp at each panel ──────
        var cpArr = new float[N];
        Vector3 totalForce = Vector3.Zero;
        Vector3 totalTorque = Vector3.Zero;
        float forceWeightedX = 0, forceWeightedY = 0, forceWeightedZ = 0;
        float totalForceMag = 0;

        for (int i = 0; i < N; i++)
        {
            var ri = faces[i].Position;
            var ni = faces[i].Normal;

            // Tangential velocity = freestream + induced (skip self)
            double vx = freestream.X, vy = freestream.Y, vz = freestream.Z;

            for (int j = 0; j < N; j++)
            {
                if (i == j) continue;

                var rj = faces[j].Position;
                float dx = ri.X - rj.X;
                float dy = ri.Y - rj.Y;
                float dz = ri.Z - rj.Z;
                double r2 = dx * dx + dy * dy + dz * dz;
                if (r2 < 0.01) r2 = 0.01;
                double r3 = System.Math.Sqrt(r2) * r2;
                double coeff = INV_4PI * sigma[j] * faces[j].Area / r3;

                vx += coeff * dx;
                vy += coeff * dy;
                vz += coeff * dz;
            }

            // Remove normal component to get surface-tangential velocity
            double vDotN = vx * ni.X + vy * ni.Y + vz * ni.Z;
            vx -= vDotN * ni.X;
            vy -= vDotN * ni.Y;
            vz -= vDotN * ni.Z;

            double vSurfSq = vx * vx + vy * vy + vz * vz;
            float cp = (float)(1.0 - vSurfSq / vInfSq);
            cpArr[i] = cp;

            // Force on panel: F = -Cp * q * A * n̂  (q cancels in normalization)
            // For force comparison, compute using Cp directly
            float forceMag = cp * faces[i].Area; // proportional to actual force / q
            Vector3 F = faces[i].Normal * (-forceMag);
            totalForce += F;
            totalTorque += Vector3.Cross(faces[i].Position - com, F);

            float absForceMag = MathF.Abs(forceMag);
            forceWeightedX += faces[i].Position.X * absForceMag;
            forceWeightedY += faces[i].Position.Y * absForceMag;
            forceWeightedZ += faces[i].Position.Z * absForceMag;
            totalForceMag += absForceMag;
        }

        Vector3 cop = totalForceMag > 0
            ? new Vector3(forceWeightedX, forceWeightedY, forceWeightedZ) / totalForceMag
            : com;

        sw.Stop();
        return new PanelResult(cpArr, totalForce, totalTorque, cop, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>
    /// Gauss elimination with partial pivoting. Modifies A and rhs in place.
    /// </summary>
    private static double[] SolveLinearSystem(double[,] A, double[] b, int N)
    {
        var x = new double[N];

        // Forward elimination
        for (int k = 0; k < N; k++)
        {
            // Partial pivoting
            int maxRow = k;
            double maxVal = System.Math.Abs(A[k, k]);
            for (int i = k + 1; i < N; i++)
            {
                double v = System.Math.Abs(A[i, k]);
                if (v > maxVal) { maxVal = v; maxRow = i; }
            }

            if (maxRow != k)
            {
                for (int j = k; j < N; j++)
                    (A[k, j], A[maxRow, j]) = (A[maxRow, j], A[k, j]);
                (b[k], b[maxRow]) = (b[maxRow], b[k]);
            }

            if (System.Math.Abs(A[k, k]) < 1e-15) continue;

            for (int i = k + 1; i < N; i++)
            {
                double factor = A[i, k] / A[k, k];
                for (int j = k + 1; j < N; j++)
                    A[i, j] -= factor * A[k, j];
                b[i] -= factor * b[k];
                A[i, k] = 0;
            }
        }

        // Back substitution
        for (int i = N - 1; i >= 0; i--)
        {
            double sum = b[i];
            for (int j = i + 1; j < N; j++)
                sum -= A[i, j] * x[j];
            x[i] = System.Math.Abs(A[i, i]) > 1e-15 ? sum / A[i, i] : 0;
        }

        return x;
    }
}
