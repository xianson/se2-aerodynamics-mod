using AeroMod;

/// <summary>
/// A SMALL FINNED ROCKET's air (sim): a slender body along its nose (-Z) - axial drag, a normal force across the flow growing with
/// the angle of attack (the body and its four fins; past FinStallDeg the fins' share falls away), acting at the centre of pressure
/// CpBehind metres behind the centre of mass (weathercock stable), rotation damping, and the fins as control surfaces: SurfPerQ
/// (N m per Pa per local axis - pitch, yaw, a little roll).
/// </summary>
sealed class RocketAir : IAir
{
    public float CdA = 0.035f, CnA = 0.9f, CpBehind = 0.5f, FinStallDeg = 25f;
    public Vector3 Damp = new(0.15f, 0.15f, 0.01f), SurfPerQ = new(0.012f, 0.012f, 0.003f);
    public bool Lifts => true;
    public Vector3 SurfaceReach(float q) => SurfPerQ * q;
    public void Forces(Vector3 a, Vector3 w, float rho, out Vector3 f, out Vector3 t)
    {
        f = t = Vector3.Zero;
        float sp = a.Length(); if (sp < 1e-3f) return;
        float q = 0.5f * rho * sp * sp;
        Vector3 nose = -Vector3.UnitZ, d = a / sp;   // (d: the ship's motion through the air)
        float along = Vector3.Dot(d, nose);
        Vector3 cross = d - nose * along;
        float sa = cross.Length(), alpha = MathF.Atan2(sa, MathF.Max(along, -1f));
        // the normal force: against the crossflow, CnA sin(2a)/2 for the body plus the fins' part (falling off past their stall)
        float fin = alpha < FinStallDeg * ApMath.Deg ? 1f : MathF.Max(0.2f, 1f - (alpha / ApMath.Deg - FinStallDeg) / 20f);
        float cn = CnA * (0.4f * MathF.Sin(2f * alpha) * 0.5f + 0.6f * fin * MathF.Sin(alpha));
        Vector3 fn = sa > 1e-6f ? -cross / sa * (q * cn) : Vector3.Zero;
        Vector3 fa = -d * (q * CdA * (1f + 2f * sa));
        f = fn + fa;
        t = Vector3.Cross(new Vector3(0f, 0f, CpBehind), fn) - Damp * w * q / MathF.Max(sp, 1f);
    }
}

static class Rockets
{
    /// <summary>A 150 kg finned rocket (4 m long), one solid motor at the tail (thrust tw x its weight) on a gimbal (gimbalDeg, 30 deg/s;
    /// 0 none), fins (finsOn) - small grid, no gyros.</summary>
    public static SimShip Finned(float tw = 3.5f, float gimbalDeg = 5f, bool finsOn = true, string name = null)
    {
        var s = new SimShip { Name = name ?? $"rocket tw {tw:0.0} gimbal {gimbalDeg:0} deg{(finsOn ? " fins" : "")}", Mass = 150f, I = new Vector3(200f, 200f, 3f), PlanetG = 9.81f, Gm = 1f };
        int k = s.T.Count;
        s.Add("motor", new Vector3(0f, 0f, 2f), -Vector3.UnitZ, tw * s.Mass * s.PlanetG);
        if (gimbalDeg > 0f) s.Gimbal(k, gimbalDeg);
        var air = new RocketAir(); if (!finsOn) air.SurfPerQ = Vector3.Zero;
        s.Air = air; s.Gyro = Vector3.Zero; s.Bottom = 0.3f; s.Radius = 2f;
        s.Nose = -Vector3.UnitZ;
        return s;
    }
}
