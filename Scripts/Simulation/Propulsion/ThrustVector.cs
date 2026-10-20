#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// THRUST VECTORING - a gimballed nozzle as a real attitude actuator (engine-free, allocation-free; 2026-10-19). Each nozzle's
/// thrust T pushes along its axis d; deflected by the angle vector D (across d, |D| the angle - at most MaxRad, changed at most
/// RateRad a second) it pushes T (cos|D| d + sin|D| D/|D|): a side force at the nozzle - its torque r x F about the centre of mass -
/// and a little less along d. The attitude law asks for a torque; Solve gives the deflections that come nearest it (the least
/// squares of every nozzle's side force for the torque, the smallest deflection among equals), each clipped to its cone and its
/// rate, and returns the force and torque that ARE applied (the thrust's change, nothing else). Reach: the torque the cones can
/// give per local axis - the attitude law's planning. Any thruster can be one (the solid rocket nozzles' "Gimbal" setting in game;
/// a SimShip's gimbals offline).
/// </summary>
public static class ThrustVector
{
    /// <summary>One gimballed nozzle: its place from the centre of mass (local m), its push (local unit), its thrust now (N), its
    /// limits (rad, rad/s) and its deflection now (local, across Dir; |Deflect| = the angle, rad).</summary>
    public struct Nozzle
    {
        public Vector3 Pos, Dir; public float Thrust, MaxRad, RateRad;
        public Vector3 Deflect;
    }

    /// <summary>A gimbal turns at most this fast when nothing says otherwise (deg/s).</summary>
    public const float DefaultRateDeg = 30f;
    /// <summary>The solve's regularisation (of the side forces' sizes, over the ask's): the smallest deflection among equals.</summary>
    public const float Ridge = 1e-6f;

    /// <summary>The torque the nozzles can give per local axis either way (N m): T sin(max) |(k x r) across d| summed.</summary>
    public static Vector3 Reach(Nozzle[] n, int count)
    {
        Vector3 r = Vector3.Zero;
        for (int i = 0; i < count && i < n.Length; i++)
        {
            var z = n[i];
            float s = MathF.Max(0f, z.Thrust) * MathF.Sin(MathF.Min(MathF.Max(z.MaxRad, 0f), 1.2f));
            if (!(s > 0f)) continue;
            r.X += s * ApMath.Flat(Vector3.Cross(Vector3.UnitX, z.Pos), z.Dir).Length();
            r.Y += s * ApMath.Flat(Vector3.Cross(Vector3.UnitY, z.Pos), z.Dir).Length();
            r.Z += s * ApMath.Flat(Vector3.Cross(Vector3.UnitZ, z.Pos), z.Dir).Length();
        }
        return r;
    }

    /// <summary>
    /// The deflections for the torque ask (local N m) this tick (dt s): written into each nozzle's Deflect; the force (local N: the
    /// thrust's change - side forces and the axial loss) and torque (local N m, about the centre of mass) they give.
    /// </summary>
    public static void Solve(Vector3 ask, Nozzle[] n, int count, float dt, out Vector3 force, out Vector3 torque)
    {
        force = torque = Vector3.Zero;
        count = Math.Min(count, n.Length);
        // the side force s_i (across d_i) giving the torque r_i x s_i: minimum-norm least squares - s_i = P_i (r_i x lambda) with
        // (sum of the M_i) lambda = ask, M_i v = r_i x P_i (v x r_i)... written out as a 3x3 normal matrix
        // (the side force's torque: tau = r x s = -[r]x s, s = P (-[r]x)^T lambda = P ([r]x lambda)... with P = I - d d^T)
        float a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, scale = 0;
        for (int i = 0; i < count; i++)
        {
            var z = n[i];
            if (!(z.Thrust > 0f) || !(z.MaxRad > 0f)) continue;
            // columns of A_i = [r]x P: the torque per unit side force along each world axis (projected across d)
            Vector3 c0 = Vector3.Cross(z.Pos, ApMath.Flat(Vector3.UnitX, z.Dir));
            Vector3 c1 = Vector3.Cross(z.Pos, ApMath.Flat(Vector3.UnitY, z.Dir));
            Vector3 c2 = Vector3.Cross(z.Pos, ApMath.Flat(Vector3.UnitZ, z.Dir));
            // A A^T += sum of the columns' outer products
            a00 += c0.X * c0.X + c1.X * c1.X + c2.X * c2.X; a01 += c0.X * c0.Y + c1.X * c1.Y + c2.X * c2.Y; a02 += c0.X * c0.Z + c1.X * c1.Z + c2.X * c2.Z;
            a11 += c0.Y * c0.Y + c1.Y * c1.Y + c2.Y * c2.Y; a12 += c0.Y * c0.Z + c1.Y * c1.Z + c2.Y * c2.Z; a22 += c0.Z * c0.Z + c1.Z * c1.Z + c2.Z * c2.Z;
            scale += z.Pos.LengthSquared();
        }
        if (scale <= 0f) { for (int i = 0; i < count; i++) n[i].Deflect = Rate(n[i].Deflect, Vector3.Zero, n[i].RateRad, dt); return; }
        float rg = Ridge * scale; a00 += rg; a11 += rg; a22 += rg;
        // lambda = (A A^T)^-1 ask (a symmetric 3x3: the adjugate)
        float d0 = a11 * a22 - a12 * a12, d1 = a02 * a12 - a01 * a22, d2 = a01 * a12 - a02 * a11;
        float det = a00 * d0 + a01 * d1 + a02 * d2;
        Vector3 lam = Vector3.Zero;
        if (MathF.Abs(det) > 1e-12f)
        {
            float i00 = d0, i01 = d1, i02 = d2, i11 = a00 * a22 - a02 * a02, i12 = a01 * a02 - a00 * a12, i22 = a00 * a11 - a01 * a01;
            lam = new Vector3(i00 * ask.X + i01 * ask.Y + i02 * ask.Z, i01 * ask.X + i11 * ask.Y + i12 * ask.Z, i02 * ask.X + i12 * ask.Y + i22 * ask.Z) / det;
        }
        for (int i = 0; i < count; i++)
        {
            var z = n[i];
            Vector3 want = Vector3.Zero;
            if (z.Thrust > 0f && z.MaxRad > 0f)
            {
                // s_i = A_i^T lambda = P ([r]x)^T lambda = P (lambda x r)
                Vector3 s = ApMath.Flat(Vector3.Cross(lam, z.Pos), z.Dir);
                float sl = s.Length(), sMax = z.Thrust * MathF.Sin(z.MaxRad);
                // (the angle for that side force, within the cone)
                if (sl > 1e-6f) want = s / sl * MathF.Asin(MathF.Min(sl, sMax) / z.Thrust);
            }
            z.Deflect = Rate(ApMath.Flat(z.Deflect, z.Dir), want, z.RateRad, dt);
            float ang = z.Deflect.Length();
            if (ang > z.MaxRad && ang > 0f) { z.Deflect *= z.MaxRad / ang; ang = z.MaxRad; }
            n[i] = z;
            if (!(z.Thrust > 0f)) continue;
            Vector3 f = ang > 1e-7f ? z.Deflect / ang * (z.Thrust * MathF.Sin(ang)) - z.Dir * (z.Thrust * (1f - MathF.Cos(ang))) : Vector3.Zero;
            force += f; torque += Vector3.Cross(z.Pos, f);
        }
    }

    /// <summary>The deflection moved from 'now' toward 'want' at most rate x dt (rad).</summary>
    static Vector3 Rate(Vector3 now, Vector3 want, float rate, float dt)
    {
        float step = (rate > 0f ? rate : DefaultRateDeg * ApMath.Deg) * MathF.Max(dt, 0f);
        Vector3 d = want - now; float l = d.Length();
        return l > step && l > 0f ? now + d * (step / l) : want;
    }
}
