#pragma warning disable
using AeroMod;

/// <summary>
/// THE CONTROL SURFACE AND ITS CONTROL PATH AS THEY WERE BEFORE 2026-10-19 (git 435686f, Components/ControlSurface.cs Compute
/// and Kirchhoff verbatim; AeroGridComponent.Surfaces.cs's +-1/2 secant allocator and fixed-sign mixer) - the "before" of
/// every table here, run side by side with the mod's own code (the "after"). Kept only as the reference: nothing in the mod
/// calls it.
/// </summary>
static class Legacy
{
    const float OswaldE = 0.85f;

    public static ComponentForceResult Compute(ControlSurface cs, in LocalAeroConditions conditions)
    {
        if (conditions.Speed < 0.01f || conditions.Atmosphere.Density < 1e-8)
            return ComponentForceResult.Zero;

        Vector3 airflow = -conditions.Velocity;
        bool alongC = Vector3.Dot(airflow, cs.ChordDirection) >= 0f;
        Vector3 chord = alongC ? cs.ChordDirection : -cs.ChordDirection;

        float cf = cs.Extents != Vector3.Zero ? MathF.Abs(Vector3.Dot(cs.Extents, cs.ChordDirection)) : MathF.Sqrt(cs.Area / MathF.Max(0.1f, cs.AspectRatio));
        var g = cs.Surroundings;
        int lead = alongC ? 1 : 0;
        float ch = g != null ? g.HostChord[lead] : 0f;
        float c = cf + ch;
        float ar = g != null ? Math.Clamp(g.SectionSpan[lead] / c * (1 + g.EndPlates[lead]), 0.3f, 12f) : cs.AspectRatio;
        float E = cf / c;
        float tau = ControlSurfaceGeometry.FlapEffectiveness(E);
        float strip = cs.Area / E;

        Vector3 n0 = Vector3.Normalize(Vector3.Cross(cs.HingeAxis, chord));
        float alpha0 = MathF.Atan2(Vector3.Dot(airflow, n0), Vector3.Dot(airflow, chord));
        Vector3 dChord = Rodrigues(chord, cs.HingeAxis, cs.DeflectionAngle);
        Vector3 nD = Vector3.Normalize(Vector3.Cross(cs.HingeAxis, dChord));
        float dAlpha = WrapPi(MathF.Atan2(Vector3.Dot(airflow, nD), Vector3.Dot(airflow, dChord)) - alpha0);
        float alphaE = alpha0 + tau * dAlpha;

        float clAlpha = CompressibleWingModel.ComputeCLAlpha(conditions.Mach * MathF.Cos(cs.SweepAngle), ar, cs.SweepAngle, 0.85f, 1.15f);
        float alphaStall = ControlSurface.StallAngle(ar);
        float cl = Kirchhoff(alphaE, clAlpha, alphaStall);
        float f = MathF.Abs(alphaE) > 1e-4f && clAlpha > 0.01f ? cl / (clAlpha * alphaE) : 1f;

        float q = conditions.DynamicPressure;
        float liftOwn = q * f * clAlpha * alpha0 * cs.Area;
        float liftDefl = q * f * clAlpha * tau * dAlpha * strip;
        float phiG = 1f, geStrength = 1f + conditions.Landing;
        if (conditions.GroundHeight >= 0f)
        {
            float hG = conditions.GroundHeight + (conditions.GroundUp.LengthSquared() > 0.5f ? Vector3.Dot(cs.Position, conditions.GroundUp) : 0f);
            phiG = GroundEffect.Phi(hG, MathF.Sqrt(MathF.Max(ar, 0.1f) * strip));
            float boost = GroundEffect.LiftBoost(phiG, geStrength);
            liftOwn *= boost; liftDefl *= boost;
        }
        float lift = liftOwn + liftDefl;
        float clStrip = lift / (q * strip);
        float cdi = ar > 0.1f ? clStrip * clStrip / (MathF.PI * OswaldE * ar) : 0f;
        if (phiG < 1f) cdi *= MathF.Pow(phiG, geStrength);
        float sd = MathF.Sin(dAlpha);
        float drag = q * (cs.Cd0 * cs.Area + cdi * strip + 0.9f * MathF.Pow(E, 1.38f) * sd * sd * strip);

        Vector3 vHat = conditions.FlowDirection;
        Vector3 liftDir = n0 - Vector3.Dot(n0, vHat) * vHat;
        float liftDirLen = liftDir.Length();
        liftDir = liftDirLen > 1e-6f ? liftDir / liftDirLen : n0;
        Vector3 force = liftDir * lift - vHat * drag;
        Vector3 moment = ch > 0f ? Vector3.Cross(-chord * (0.5f * ch), liftDir * liftDefl) : Vector3.Zero;
        return new ComponentForceResult(force, cs.Position, moment);
    }

    public static float Kirchhoff(float alpha, float clAlpha, float alphaStall)
    {
        float absAlpha = MathF.Abs(alpha);
        if (absAlpha <= alphaStall) return clAlpha * alpha;
        float stallExcess = (absAlpha - alphaStall) / (alphaStall + 0.01f);
        float sqrtF0 = MathF.Sqrt(MathF.Exp(-2f * stallExcess));
        float kirchhoffFactor = MathF.Max(0.15f, 0.25f * (1f + sqrtF0) * (1f + sqrtF0));
        float clAttached = clAlpha * alphaStall * MathF.Sign(alpha);
        float blendToFlat = MathF.Min(1f, stallExcess * 0.5f);
        return clAttached * kirchhoffFactor * (1f - blendToFlat) + MathF.Sin(2f * alpha) * blendToFlat;
    }

    static float WrapPi(float a)
    {
        while (a > MathF.PI) a -= 2f * MathF.PI;
        while (a < -MathF.PI) a += 2f * MathF.PI;
        return a;
    }

    static Vector3 Rodrigues(Vector3 v, Vector3 k, float theta)
    {
        float cos = MathF.Cos(theta), sin = MathF.Sin(theta);
        return v * cos + Vector3.Cross(k, v) * sin + k * Vector3.Dot(k, v) * (1f - cos);
    }

    /// <summary>The old mixer's effectiveness: geometric r x (hinge x chord), flipped with the air over the trailing edge.</summary>
    public static Vector3 MixerEffect(ControlSurface cs, Vector3 velocityLocal, Vector3 com)
    {
        Vector3 liftDir = Vector3.Cross(cs.HingeAxis, cs.ChordDirection);
        if (Vector3.Dot(-velocityLocal, cs.ChordDirection) < 0f) liftDir = -liftDir;
        return Vector3.Cross(cs.Position - com, liftDir);
    }
}
