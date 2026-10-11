#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// THRUST ALLOCATION - pure math, no game (unit-tested offline: Tests/ThrustTests). ThrustTorque's numbers:
///  - GameShare: the game's thrust shared among the thrusters facing each way, by their maximum (as the game does);
///  - RateCommand: the attitude law (error to a target rate, the angular acceleration to reach it);
///  - Solve: each thruster's force so the torque is the one wanted and the net force stays the game's.
/// Thrusters: arm = (position - centre of mass) x dir (torque per newton), dir = the way it pushes the ship, cap = its
/// maximum (N). All grid-local.
/// </summary>
public static class ThrustAllocator
{
    /// <summary>Which of the six axis directions a unit vector is (the largest component): +X 0, -X 1, +Y 2, -Y 3, +Z 4, -Z 5.</summary>
    public static int DirIndex(Vector3 d)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        if (ax >= ay && ax >= az) return d.X >= 0f ? 0 : 1;
        if (ay >= az) return d.Y >= 0f ? 2 : 3;
        return d.Z >= 0f ? 4 : 5;
    }

    /// <summary>The game's thrust gameF shared as the game shares it (f0: each thruster's force; capDir: the six
    /// directions' summed maxima). Returns the torque that sharing makes (the offset torque).</summary>
    public static Vector3 GameShare(Vector3 gameF, Vector3[] arms, Vector3[] dirs, float[] caps, float[] capDir, float[] f0, int n)
    {
        Vector3 torque = Vector3.Zero;
        for (int i = 0; i < n; i++)
        {
            float along = Vector3.Dot(gameF, dirs[i]);
            int k = DirIndex(dirs[i]);
            f0[i] = along > 0f && capDir[k] > 0f ? Math.Min(caps[i], along * caps[i] / capDir[k]) : 0f;
            torque += arms[i] * f0[i];
        }
        return torque;
    }

    /// <summary>The attitude law: the error from current to target (local axis-angle, rad), the rate wanted (kp per rad,
    /// at most maxRate), and the angular acceleration (local, rad/s^2) that reaches it in responseTime. Zero when resting
    /// (error and rate both under their rest thresholds).</summary>
    public static Vector3 RateCommand(Quaternion current, Quaternion target, Vector3 wLocal, float kp, float maxRate, float responseTime,
        float restAngle, float restRate, out Vector3 error)
    {
        Quaternion err = Quaternion.Normalize(Quaternion.Inverse(current) * target);
        if (err.W < 0f) err = new Quaternion(-err.X, -err.Y, -err.Z, -err.W);   // the short way round
        Vector3 axis = new Vector3(err.X, err.Y, err.Z);
        float s = axis.Length();
        error = s > 1e-6f ? axis * (2f * MathF.Atan2(s, err.W) / s) : Vector3.Zero;
        Vector3 wDes = error * kp;
        float wl = wDes.Length();
        if (wl > maxRate) wDes *= maxRate / wl;
        if (error.Length() <= restAngle && wLocal.Length() <= restRate) return Vector3.Zero;
        return (wDes - wLocal) / responseTime;
    }

    /// <summary>Each thruster's force f (0..cap) so that the torque is tauTarget and the net force stays gameF: minimises
    /// |sum a_i f_i - tauTarget|^2 + mu |sum d_i f_i - gameF|^2 (mu: the mean arm squared, so force and torque weigh
    /// alike) by accelerated projected gradient (FISTA) from f as given (the warm start), then KeepNetThrust. prev, y:
    /// scratch of n.</summary>
    /// <summary>The most net thrust attitude may take when keeping the game's thrust cannot give the torque wanted (share of
    /// the game's thrust): with a whole group of thrusters at full power (full throttle), nothing within it can be
    /// rebalanced, and its offset torque out-turned the steering (the Hercules at full forward thrust: 0.10 MN m of yaw
    /// left against 1.11 from its own thrusters - it could not stop a turn).</summary>
    public const float MaxThrustLoss = 0.25f;
    /// <summary>The cost on thrust used, as the share of the mean capacity drained per solver iteration (Fista).</summary>
    public static float Drain = 1e-4f;
    [ThreadStatic] static float[] _free;

    public static void Solve(Vector3 tauTarget, Vector3 gameF, Vector3[] arms, Vector3[] dirs, float[] caps, float[] f0, float[] f,
        float[] prev, float[] y, float armSq, int n, int iterations) => Solve(tauTarget, gameF, arms, dirs, caps, f0, f, prev, y, armSq, n, iterations, out _);

    /// <summary>... and how far it gave up the game's thrust for attitude (0: not at all; 1: the solver's own balance,
    /// within MaxThrustLoss). The sharing's total then differs from gameF: the caller applies the difference (a real
    /// thrust change, the thrusters' own).</summary>
    public static void Solve(Vector3 tauTarget, Vector3 gameF, Vector3[] arms, Vector3[] dirs, float[] caps, float[] f0, float[] f,
        float[] prev, float[] y, float armSq, int n, int iterations, out float attitudePriority)
    {
        attitudePriority = 0f;
        float mu = n > 0 ? Math.Max(armSq / n, 1f) : 1f;
        for (int i = 0; i < n; i++) f[i] = Math.Clamp(f[i], 0f, caps[i]);
        Fista(tauTarget, gameF, arms, dirs, caps, f, prev, y, mu, n, iterations);
        // f: the solver's balance of torque and force. Keep the game's net thrust (f -> kept); if that leaves the torque far
        // worse than an attitude-first sharing could (force weighed 50x less), move toward that one just as far as the torque
        // needs (the least-squares step along the line between them), at most MaxThrustLoss of the thrust.
        var free = _free != null && _free.Length >= n ? _free : (_free = new float[Math.Max(n, 16)]);
        Array.Copy(f, free, n);
        KeepNetThrust(f, f0, dirs, caps, n);
        Vector3 tKeep = Vector3.Zero, fKeep = Vector3.Zero;
        for (int i = 0; i < n; i++) { tKeep += arms[i] * f[i]; fKeep += dirs[i] * f[i]; }
        float need = tauTarget.Length(), eKeep = (tKeep - tauTarget).Length();
        if (need < 1f || eKeep < 0.5f * need) return;   // (the kept thrust gives at least half the torque wanted: nothing given up)
        Fista(tauTarget, gameF, arms, dirs, caps, free, prev, y, mu * 0.02f, n, iterations);
        Vector3 tFree = Vector3.Zero, fFree = Vector3.Zero;
        for (int i = 0; i < n; i++) { tFree += arms[i] * free[i]; fFree += dirs[i] * free[i]; }
        Vector3 dT = tFree - tKeep;
        float dT2 = dT.LengthSquared();
        if (dT2 < 1e-6f || (tFree - tauTarget).Length() > eKeep * 0.8f) return;   // (no better torque to be had)
        float a = Math.Clamp(Vector3.Dot(tauTarget - tKeep, dT) / dT2, 0f, 1f);
        float loss = (fFree - fKeep).Length(), maxLoss = MaxThrustLoss * gameF.Length();
        if (loss * a > maxLoss) a = loss > 0f ? maxLoss / loss : 0f;
        if (a > 0f) for (int i = 0; i < n; i++) f[i] += (free[i] - f[i]) * a;
        attitudePriority = a;
    }

    /// <summary>Accelerated projected gradient (FISTA) on |sum a_i f_i - tau|^2 + mu |sum d_i f_i - F|^2, from f.</summary>
    static void Fista(Vector3 tauTarget, Vector3 gameF, Vector3[] arms, Vector3[] dirs, float[] caps, float[] f, float[] prev, float[] y,
        float mu, int n, int iterations)
    {
        for (int i = 0; i < n; i++) { prev[i] = f[i]; y[i] = f[i]; }
        float lmax = LargestEigen(arms, dirs, mu, n);
        float step = lmax > 0f ? 1f / lmax : 0f;
        // + Drain x sum f_i: a cost on the thrust used. Couples (opposing thrusters firing against each other) make no force
        // and, balanced, no torque - free to the solve, so after a turn they stayed, warm-started frame to frame: every
        // thruster lit at a low glow, burning, the flames "stuck on" (2026-10-09). With it, thrust doing nothing drains
        // (Drain x the mean capacity per iteration: a 30% couple gone in about a second) and couples form only for torque.
        float meanCap = 0f; for (int i = 0; i < n; i++) meanCap += caps[i];
        float drain = n > 0 && step > 0f ? Drain * (meanCap / n) / step : 0f;
        float tk = 1f;
        for (int it = 0; it < iterations && step > 0f; it++)
        {
            Vector3 tr = -tauTarget, fr = -gameF;
            for (int i = 0; i < n; i++) { tr += arms[i] * y[i]; fr += dirs[i] * y[i]; }
            float tk1 = (1f + MathF.Sqrt(1f + 4f * tk * tk)) / 2f, mom = (tk - 1f) / tk1;
            for (int i = 0; i < n; i++)
            {
                float fi = Math.Clamp(y[i] - step * (Vector3.Dot(arms[i], tr) + mu * Vector3.Dot(dirs[i], fr) + drain), 0f, caps[i]);
                y[i] = Math.Clamp(fi + mom * (fi - prev[i]), 0f, caps[i]);
                prev[i] = fi; f[i] = fi;
            }
            tk = tk1;
        }
    }

    /// <summary>The largest eigenvalue of sum v v^T, v = (arm, sqrt(mu) dir) in R^6 (power iteration): the
    /// gradient step's bound. (The trace is a safe bound too, but far too timid with many thrusters.)</summary>
    static float LargestEigen(Vector3[] arms, Vector3[] dirs, float mu, int n)
    {
        Span<float> G = stackalloc float[36];
        float sm = MathF.Sqrt(mu);
        Span<float> v = stackalloc float[6];   // (once: stackalloc in the loop held stack per thruster until return)
        for (int i = 0; i < n; i++)
        {
            v[0] = arms[i].X; v[1] = arms[i].Y; v[2] = arms[i].Z; v[3] = sm * dirs[i].X; v[4] = sm * dirs[i].Y; v[5] = sm * dirs[i].Z;
            for (int r = 0; r < 6; r++) for (int c = 0; c < 6; c++) G[r * 6 + c] += v[r] * v[c];
        }
        Span<float> x = stackalloc float[6] { 1f, 0.9f, 0.8f, 0.7f, 0.6f, 0.5f };
        Span<float> yv = stackalloc float[6];
        float lambda = 0f;
        for (int it = 0; it < 30; it++)
        {
            float norm = 0f;
            for (int r = 0; r < 6; r++) { float acc = 0f; for (int c = 0; c < 6; c++) acc += G[r * 6 + c] * x[c]; yv[r] = acc; norm += acc * acc; }
            norm = MathF.Sqrt(norm);
            if (norm <= 0f) return 0f;
            for (int r = 0; r < 6; r++) x[r] = yv[r] / norm;
            lambda = norm;
        }
        return lambda * 1.05f;
    }

    [ThreadStatic] static float[] _grpS, _grpT, _grpH;
    /// <summary>The sharing f changed so that along each axis the net thrust (the +axis group's total minus the -axis
    /// group's) is the game's again (f0's): the excess comes off the side that has too much, the rest goes onto the other
    /// side (within each thruster's maximum). Steering then comes only from rebalancing within a side and from couples
    /// (both sides firing) - never from more or less net thrust.</summary>
    public static void KeepNetThrust(float[] f, float[] f0, Vector3[] dirs, float[] caps, int n)
    {
        var S = _grpS ??= new float[6]; var T = _grpT ??= new float[6]; var H = _grpH ??= new float[6];
        Array.Clear(S, 0, 6); Array.Clear(T, 0, 6); Array.Clear(H, 0, 6);
        for (int i = 0; i < n; i++) { int k = DirIndex(dirs[i]); S[k] += f[i]; T[k] += f0[i]; H[k] += caps[i] - f[i]; }
        for (int ax = 0; ax < 3; ax++)
        {
            int kp = 2 * ax, km = 2 * ax + 1;
            float delta = (S[kp] - S[km]) - (T[kp] - T[km]);   // >0: too much along +axis
            if (MathF.Abs(delta) < 1f) continue;
            int down = delta > 0f ? kp : km, up = delta > 0f ? km : kp;
            float need = MathF.Abs(delta);
            // first: less on the side with too much
            float cut = MathF.Min(need, S[down]);
            if (cut > 0f && S[down] > 0f)
            {
                float k = (S[down] - cut) / S[down];
                for (int i = 0; i < n; i++) if (DirIndex(dirs[i]) == down) f[i] *= k;
                need -= cut;
            }
            // then: more on the other side, by its headroom
            if (need > 0f && H[up] > 0f)
            {
                float add = MathF.Min(need, H[up]), k = add / H[up];
                for (int i = 0; i < n; i++) if (DirIndex(dirs[i]) == up) f[i] += (caps[i] - f[i]) * k;
            }
        }
    }
}
