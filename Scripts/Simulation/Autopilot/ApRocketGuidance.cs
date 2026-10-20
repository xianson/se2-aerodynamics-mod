#pragma warning disable CS0649, CS0169, CS8618, CS8604
using System;
namespace AeroMod;

public sealed class ApRocketGuidance
{
    public static bool Implemented => true;

    public readonly ApRocketState State = new();

    public void Begin(ApSlotCtx c, string saved)
    {
        State.Reset();
        if (!string.IsNullOrEmpty(saved)) State.Load(saved);
    }

    public float Score(ApSlotCtx c)
    {
        if (!c.HasTarget) return float.NaN;
        var T = c.Target;
        float range = T.Range;
        float rangeScore = MathF.Max(0f, 1f - range / (c.Panel.RangeM * 3f));
        float sizeScore = Math.Clamp(T.Radius / 30f, 0.3f, 1f);
        return rangeScore * sizeScore * 0.8f;
    }

    public bool Step(ApSlotCtx c, ref ApSlotOut o)
    {
        State.Time += c.Dt;

        // timeout
        if (c.Rocket.TimeoutS > 0f && State.Time > c.Rocket.TimeoutS)
        { o.LetGo = ApReason.TimedOut; return false; }

        // target lost
        if (!c.HasTarget || c.Target.Lost > ApTargetTrack.LoseSeconds)
        { o.LetGo = ApReason.LostTarget; return false; }

        var T = c.Target;
        var tr = c.Track;
        float range = T.Range;

        // arm check
        if (!State.Armed && c.Rocket.ArmM > 0f && range > c.Rocket.ArmM)
        {
            Vector3 toTarget = ApMath.Flat((Vector3)(tr.Predict(1f) - c.S.Pos), c.Up);
            float t = toTarget.Length();
            if (t < 1f) toTarget = -T.Los; else toTarget = Vector3.Normalize(toTarget);

            o.Goal.Kind = ApExternalKind.Lift;
            o.Goal.Lift = toTarget;
            o.Goal.Load = c.Rocket.MaxLatG > 0f ? c.Rocket.MaxLatG : 3f;
            o.Goal.Throttle = 1f;
            o.Status = $"Rocket arm {range:0}m";
            o.TargetClearM = MathF.Max(T.Radius, 5f);
            State.Dirty = true;
            return true;
        }
        State.Armed = true;

        // guidance params
        float navConst = Math.Clamp(c.Rocket.NavConst, 1f, 8f);
        float tgtAccelGain = Math.Clamp(c.Rocket.TargetAccelGain, 0f, 3f);
        float maxLatG = c.Rocket.MaxLatG > 0f ? c.Rocket.MaxLatG : 10f;
        bool gravComp = c.Rocket.GravityComp;

        // relative geometry
        Vector3 rW = (Vector3)(T.Pos - c.S.Pos);
        float r = rW.Length();
        if (r < 1f) { o.Goal.Kind = ApExternalKind.Attitude; return true; }
        Vector3 rHat = rW / r;

        Vector3 vRel = T.Vel - c.S.Vel;
        float closing = -Vector3.Dot(vRel, rHat);
        Vector3 omega = Vector3.Cross(rW, vRel) / MathF.Max(r * r, 1f);

        Vector3 at = tr.Valid ? tr.Acc : T.Acc;
        Vector3 atLat = at - rHat * Vector3.Dot(at, rHat);
        float vRelMag = vRel.Length();

        // lateral acceleration command
        Vector3 latax = Vector3.Zero;

        switch (c.Rocket.Law)
        {
            case ApGuidanceLaw.ProNav:
            case ApGuidanceLaw.AugmentedPN:
                latax = navConst * vRelMag * Vector3.Cross(omega, rHat);
                latax += tgtAccelGain * atLat;
                break;

            case ApGuidanceLaw.WhipNav:
                Vector3 vRelPerp = ApMath.Flat(vRel, rHat);
                latax = navConst * 0.1f * vRelPerp;
                latax += tgtAccelGain * atLat;
                break;

            case ApGuidanceLaw.HybridNav:
                Vector3 vRelPerpH = ApMath.Flat(vRel, rHat);
                latax = navConst * vRelMag * Vector3.Cross(omega, rHat);
                latax += navConst * 0.1f * vRelPerpH;
                latax += tgtAccelGain * atLat;
                break;

            case ApGuidanceLaw.QuadraticIntercept:
                float vm = c.AirSpeed;
                if (vm < 1f) { o.LetGo = ApReason.TooSlow; return false; }
                float tgoQ = SolveQuadraticIntercept(r, closing, vm);
                Vector3D interceptPt = T.Pos + (Vector3D)(T.Vel * tgoQ);
                Vector3 rInt = (Vector3)(interceptPt - c.S.Pos);
                float rIntMag = rInt.Length();
                if (rIntMag > 1f)
                {
                    Vector3 rIntHat = rInt / rIntMag;
                    Vector3 vRelInt = T.Vel - c.S.Vel;
                    Vector3 omegaInt = Vector3.Cross(rInt, vRelInt) / MathF.Max(rIntMag * rIntMag, 1f);
                    latax = navConst * vRelMag * Vector3.Cross(omegaInt, rIntHat);
                    latax += tgtAccelGain * atLat;
                }
                else
                {
                    latax = navConst * vRelMag * Vector3.Cross(omega, rHat);
                    latax += tgtAccelGain * atLat;
                }
                break;

            case ApGuidanceLaw.Pursuit:
                latax = rHat * navConst * closing;
                Vector3 vLatP = ApMath.Flat(vRel, rHat);
                latax -= vLatP * 0.5f;
                break;

            default:
                latax = navConst * vRelMag * Vector3.Cross(omega, rHat);
                latax += tgtAccelGain * atLat;
                break;
        }

        // lateral g cap
        float latMag = latax.Length();
        float maxLatAccel = maxLatG * c.G;
        if (latMag > maxLatAccel && latMag > 1e-6f)
            latax *= maxLatAccel / latMag;

        // output: Lift goal
        Vector3 myVel = c.S.Vel;
        float myVelMag = myVel.Length();

        if (myVelMag > 1f)
        {
            Vector3 aAcross = ApMath.Flat(latax, myVel);
            float aAcrossMag = aAcross.Length();
            if (aAcrossMag > 1f)
            {
                o.Goal.Kind = ApExternalKind.Lift;
                o.Goal.Lift = aAcross / aAcrossMag;
                o.Goal.Load = aAcrossMag / c.G;
            }
            else
            {
                o.Goal.Kind = ApExternalKind.Lift;
                o.Goal.Lift = c.Up;
                o.Goal.Load = 1f;
            }
        }
        else
        {
            Vector3 toTarget = ApMath.Flat(rHat, c.Up);
            float t = toTarget.Length();
            if (t < 1f) toTarget = -T.Los; else toTarget = Vector3.Normalize(toTarget);
            o.Goal.Kind = ApExternalKind.Lift;
            o.Goal.Lift = toTarget;
            o.Goal.Load = 1f;
        }

        o.Goal.Throttle = 1f;

        // terminal phase
        if (range < 3000f && !State.TerminalLocked)
        {
            State.TerminalLocked = true;
            State.TermRefDir = rHat;
        }

        if (State.TerminalLocked)
        {
            float bearingErr = MathF.Acos(Math.Clamp(Vector3.Dot(rHat, (Vector3)State.TermRefDir), -1f, 1f)) / ApMath.Deg;
            if (bearingErr > 100f || (State.EverClosed && range > State.MinGap + 300f))
            {
                State.Passes++;
                State.TerminalLocked = false;
                State.EverClosed = false;
                State.MinGap = float.MaxValue;
            }
            else
            {
                State.MinGap = MathF.Min(State.MinGap, range);
            }
            State.EverClosed = State.EverClosed || closing > 10f;
        }

        // status
        string lawName = c.Rocket.Law == ApGuidanceLaw.ProNav ? "APN"
            : c.Rocket.Law == ApGuidanceLaw.WhipNav ? "Whip"
            : c.Rocket.Law == ApGuidanceLaw.HybridNav ? "Hybrid"
            : c.Rocket.Law == ApGuidanceLaw.QuadraticIntercept ? "QuadInt"
            : c.Rocket.Law == ApGuidanceLaw.Pursuit ? "Pursuit"
            : "APN";
        string armedStr = State.Armed ? "armed" : "arming";
        o.Status = $"Rocket {lawName} {armedStr} {range:0}m";

        // aim report
        if (o.AimReport || o.AimNose)
        {
            float u = c.Panel.MuzzleSpeed > 0f ? c.Panel.MuzzleSpeed : c.S.MuzzleSpeed;
            if (u > 0f && c.Lead(u, out Vector3 aimDir, out State.Tof, false))
            {
                o.AimReport = true;
                State.AimErrDeg = c.GunErrorDeg(aimDir);
                o.AimNose = true;
                o.Goal.AimAxis = c.S.Nose.LengthSquared() > 0.5f ? c.S.Nose : -Vector3.UnitZ;
                o.Goal.AimDir = aimDir;
                o.Goal.Blend = 1f;
            }
        }

        o.TargetClearM = MathF.Max(T.Radius, 5f);
        State.Dirty = true;
        return true;
    }

    static float SolveQuadraticIntercept(float r, float closing, float vm)
    {
        float bCoeff = -2f * closing;
        float cCoeff = -vm * vm;
        float disc = bCoeff * bCoeff - 4f * cCoeff;
        if (disc < 0f) return 1f;
        float t = (MathF.Sqrt(disc) - bCoeff) / 2f;
        return MathF.Max(0.05f, t);
    }
}

public sealed class ApRocketState
{
    public float Time;
    public bool Armed;
    public bool TerminalLocked;
    public Vector3D TermRefDir;
    public bool EverClosed;
    public float MinGap;
    public int Passes;
    public float Tof;
    public float AimErrDeg;

    public bool Dirty;

    public void Reset()
    {
        Time = 0f;
        Armed = false;
        TerminalLocked = false;
        TermRefDir = Vector3D.Zero;
        EverClosed = false;
        MinGap = float.MaxValue;
        Passes = 0;
        Tof = 0f;
        AimErrDeg = 0f;
        Dirty = false;
    }

    public string Save()
    {
        return $"{(Armed ? 1 : 0)},{Time:0.0},{Passes},{MinGap:0.0}";
    }

    public void Load(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        var parts = s.Split(',');
        if (parts.Length >= 4)
        {
            Armed = parts[0] == "1";
            Time = float.TryParse(parts[1], out var t) ? t : 0f;
            Passes = int.TryParse(parts[2], out var p) ? p : 0;
            MinGap = float.TryParse(parts[3], out var mg) ? mg : float.MaxValue;
        }
    }
}
