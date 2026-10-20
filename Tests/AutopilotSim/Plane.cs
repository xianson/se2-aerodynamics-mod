using AeroMod;

/// <summary>
/// AN ANALYTIC PLANE's air (sim; no dumped polar needed - Tests/data/polars is local): a conventional wing and tail in closed form,
/// nose -Z, up +Y, right +X. Lift CL0 + CLa alpha up to the stall (StallDeg), then falling away; drag CD0 + k CL^2 + the body's
/// across the flow; a side force against the sideslip; pitch and weathercock stability (the trim near TrimDeg); rotation damping
/// (q S c^2 / 2V per axis); the control surfaces as SurfPerQ (N m per Pa per local axis). The stall sweep (LiftPeak) and the
/// flight performance read it as they read a polar.
/// </summary>
sealed class AnalyticAir : IAir
{
    public float S = 25f, Span = 12f, Chord = 2.2f;
    public float CL0 = 0.25f, CLa = 5f, StallDeg = 14f, CD0 = 0.025f, K = 0.05f, CYb = 1f;
    public float Cma = 0.6f, TrimDeg = 3f, Cnb = 0.12f, Cmq = 12f, Cnr = 0.3f, Clp = 0.5f;
    public Vector3 SurfPerQ = new(8f, 8f, 12f);
    public bool Lifts => true;
    public Vector3 SurfaceReach(float q) => SurfPerQ * q;

    public float CL(float alpha)
    {
        float a = alpha, st = StallDeg * ApMath.Deg;
        if (a <= st) return CL0 + CLa * MathF.Max(a, -st);
        float peak = CL0 + CLa * st;
        return MathF.Max(0.5f * peak, peak * (1f - (a - st) / (12f * ApMath.Deg) * 0.5f));
    }

    public void Forces(Vector3 a, Vector3 w, float rho, out Vector3 f, out Vector3 t)
    {
        f = t = Vector3.Zero;
        float sp = a.Length(); if (sp < 1e-3f) return;
        Vector3 d = a / sp, nose = -Vector3.UnitZ, up = Vector3.UnitY, right = Vector3.UnitX;
        float alpha = MathF.Atan2(-Vector3.Dot(d, up), Vector3.Dot(d, nose));
        float beta = MathF.Asin(Math.Clamp(Vector3.Dot(d, right), -1f, 1f));
        float q = 0.5f * rho * sp * sp, cl = CL(alpha);
        Vector3 liftDir = Vector3.Cross(right, d); float ll = liftDir.Length();
        liftDir = ll > 1e-4f ? liftDir / ll : up;
        Vector3 sideDir = right - d * Vector3.Dot(right, d); float sl = sideDir.Length();
        sideDir = sl > 1e-4f ? sideDir / sl : right;
        float sa = MathF.Sin(alpha);
        float cd = CD0 + K * cl * cl + 1.2f * sa * sa;
        f = q * S * (liftDir * cl - d * cd - sideDir * (CYb * beta));
        float pitch = q * S * Chord * Cma * (TrimDeg * ApMath.Deg - alpha);
        float yaw = -q * S * Span * Cnb * beta;
        float damp = q * S / MathF.Max(sp, 1f);
        t = new Vector3(pitch - damp * Chord * Chord * Cmq * 0.5f * w.X, yaw - damp * Span * Span * Cnr * 0.5f * w.Y, -damp * Span * Span * Clp * 0.5f * w.Z);
    }
}

static class Planes
{
    /// <summary>A 10 t analytic plane (AnalyticAir: 25 m2 wing, stall near 93 m/s at its weight), one engine along the nose (thrust
    /// tw x its real weight), gyros (N m per local axis), forward guns along the nose (none fired by anything here).</summary>
    public static SimShip Analytic(float tw = 0.5f, float gyro = 2e5f, string name = null)
    {
        var s = new SimShip { Name = name ?? "analytic plane 10 t", Mass = 10000f, I = new Vector3(1.0e5f, 1.6e5f, 7e4f), PlanetG = 9.81f, Gm = 2f, Air = new AnalyticAir(), Gyro = new Vector3(gyro), Bottom = 2f };
        s.Add("engine", Vector3.Zero, -Vector3.UnitZ, tw * s.Mass * s.PlanetG * s.Gm);
        s.Nose = -Vector3.UnitZ; s.Radius = 7f;
        return s;
    }

    /// <summary>A plane trimmed level at 'speed' heading 'headingDeg' (0 north: -Z; 90 east: +X) at 'at', dampeners off, the
    /// attitude held 1.5 s (the stall sweep in) - FlightPack.PlaneAt's, for any SimShip and any place.</summary>
    public static Run TrimmedAt(string name, SimShip s, Vector3D at, float speed, float headingDeg = 0f)
    {
        var r = new Run(name, s);
        r.P.Damp = false; r.P.Rho = 1.225f; r.P.P = at;
        float weight = s.Mass * s.PlanetG * s.Gm, trim = 0f;
        for (float a = 0f; a < 20f * ApMath.Deg; a += 0.05f * ApMath.Deg)
        {
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, a);
            s.Air.Forces(Vector3.Transform(-Vector3.UnitZ * speed, Quaternion.Inverse(q)), Vector3.Zero, 1.225f, out var f, out _);
            trim = a; if (Vector3.Transform(f, q).Y >= weight) break;
        }
        // (the heading: a turn about the vertical - compass 90 is +X: the nose -Z turned right, about -Y)
        var hq = Quaternion.CreateFromAxisAngle(Vector3.UnitY, -headingDeg * ApMath.Deg);
        r.P.Q = Quaternion.Normalize(hq * Quaternion.CreateFromAxisAngle(Vector3.UnitX, trim));
        r.P.V = Vector3.Transform(-Vector3.UnitZ * speed, hq);
        r.P.PilotKeys = -Vector3.UnitZ * 0.5f; r.P.Move = -Vector3.UnitZ * 0.5f;
        var sn = r.P.SenseNow();
        r.P.Fd.Engage(ApGoal.Attitude, in sn, out _);
        r.Fly(1.5f);
        return r;
    }
}
