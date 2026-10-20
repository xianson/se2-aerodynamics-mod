#pragma warning disable CS0649, CS0169, CS8618, CS8604
using System;
namespace AeroMod;

public sealed class ApDogfight
{
    public static bool Implemented => true;

    public readonly ApDogfightState State = new();

    public void Begin(ApSlotCtx c, string saved)
    {
        State.Reset();
        if (!string.IsNullOrEmpty(saved)) State.Load(saved);
    }

    public float Score(ApSlotCtx c)
    {
        if (!c.HasTarget) return float.NaN;
        var T = c.Target;
        float range = T.Range, tv = T.Vel.Length(), my = c.AirSpeed;
        if (my < 30f) return float.NaN;
        if (c.Style != ApStyle.Airplane) return float.NaN;

        float speedRatio = MathF.Min(tv, my) / MathF.Max(tv, my);
        float rangeNorm = Math.Clamp(range / (c.Panel.RangeM * 2f), 0f, 1f);
        float rangeScore = 1f - MathF.Abs(rangeNorm - 0.5f) * 2f;
        float airScore = tv > 0.3f * my ? 1f : 0.3f;
        return MathF.Max(0f, rangeScore * airScore * 0.9f);
    }

    public bool Step(ApSlotCtx c, ref ApSlotOut o)
    {
        State.Time += c.Dt;

        // bingo checks
        if (c.S.FuelShare >= 0f && c.Dogfight.BingoFuelPct > 0f && c.S.FuelShare < c.Dogfight.BingoFuelPct / 100f)
        { o.LetGo = ApReason.Bingo; return false; }
        if (c.S.IntegrityShare >= 0f && c.Dogfight.BingoDamagePct > 0f && c.S.IntegrityShare < c.Dogfight.BingoDamagePct / 100f)
        { o.LetGo = ApReason.Bingo; return false; }

        // height constraints
        float deck = c.Dogfight.DeckM, ceil = c.Dogfight.CeilingM;
        if (deck > 0f && c.Agl >= 0f && c.Agl < deck)
        { o.LetGo = ApReason.Upset; return false; }
        if (ceil > 0f && c.Height > ceil)
        { o.LetGo = ApReason.TooFast; return false; }

        // target lost
        if (!c.HasTarget || c.Target.Lost > ApTargetTrack.LoseSeconds)
        { o.LetGo = ApReason.LostTarget; return false; }

        var T = c.Target;
        var tr = c.Track;
        float range = T.Range, closing = T.Closing;
        float mySpeed = c.AirSpeed, myVc = c.Perf.CornerSpeed;
        float myNInst = c.Perf.NInst, myNSust = c.Perf.NSust;
        float g = c.G;

        // geometry
        Vector3 ourNoseW = Vector3.Transform(c.S.Nose.LengthSquared() > 0.5f ? c.S.Nose : -Vector3.UnitZ, c.S.Orient);
        Vector3 losW = T.Los;
        float ataDeg = MathF.Acos(Math.Clamp(Vector3.Dot(ourNoseW, losW), -1f, 1f)) / ApMath.Deg;
        float angleOffDeg = T.AngleOff;
        float tv = tr.Speed;
        float tEp = tr.SpecificPower;
        float eP = c.Perf.Ps;
        float dE = eP - tEp;
        float tTurnRate = tr.TurnRateMag;
        float myTurnRate = c.Perf.TurnRateInst;

        ApDogfightState.FightPhase phase = State.Phase;
        switch (phase)
        {
            case ApDogfightState.FightPhase.Neutral:
                phase = NeutralStep(c, ref o, range, closing, ataDeg, angleOffDeg, tv, mySpeed, myVc,
                    myNInst, myTurnRate, tTurnRate, dE, tr, T);
                break;
            case ApDogfightState.FightPhase.Offensive:
                phase = OffensiveStep(c, ref o, range, closing, ataDeg, angleOffDeg, tv, mySpeed, myVc,
                    myNInst, myNSust, myTurnRate, tTurnRate, dE, tr, T);
                break;
            case ApDogfightState.FightPhase.Defensive:
                phase = DefensiveStep(c, ref o, range, closing, ataDeg, angleOffDeg, tv, mySpeed, myVc,
                    myNInst, myNSust, tTurnRate, tr, T);
                break;
            case ApDogfightState.FightPhase.Extend:
                phase = ExtendStep(c, ref o, range, closing, ataDeg, angleOffDeg, tv, mySpeed, myVc,
                    myNInst, dE, tr, T);
                break;
        }
        State.Phase = phase;

        // aim report
        if (c.HasTarget && (o.AimReport || o.AimNose))
        {
            float u = c.Panel.MuzzleSpeed > 0f ? c.Panel.MuzzleSpeed : c.S.MuzzleSpeed;
            if (u > 0f && c.Lead(u, out Vector3 aimDir, out State.Tof, false))
            {
                o.AimReport = true;
                State.AimErrDeg = c.GunErrorDeg(aimDir);
                o.AimNose = true;
                float cone = c.Panel.ConeDeg;
                float reach = c.S.GunRange > 0f ? c.S.GunRange : MathF.Max(1.5f * c.Panel.RangeM, 1500f);
                if (range <= reach && State.AimErrDeg <= cone * 1.5f && State.LeadBlend < 1f)
                    State.LeadBlend = MathF.Min(1f, State.LeadBlend + c.Dt * 2f);
                if (State.LeadBlend > 0f && State.LeadBlend < 1f)
                {
                    o.Goal.AimAxis = c.S.Nose.LengthSquared() > 0.5f ? c.S.Nose : -Vector3.UnitZ;
                    o.Goal.AimDir = aimDir;
                    o.Goal.Blend = State.LeadBlend;
                }
            }
        }

        string phaseName = phase == ApDogfightState.FightPhase.Neutral ? "Neutral"
            : phase == ApDogfightState.FightPhase.Offensive ? "Offensive"
            : phase == ApDogfightState.FightPhase.Defensive ? "Defensive"
            : "Extend";
        o.Status = string.IsNullOrEmpty(T.Name)
            ? $"Dogfight {phaseName} {range:0.0} km"
            : $"Dogfight {T.Name} {phaseName} {range:0.0} km";
        o.TargetClearM = MathF.Max(T.Radius, 10f);
        State.Dirty = true;
        return true;
    }

    ApDogfightState.FightPhase NeutralStep(ApSlotCtx c, ref ApSlotOut o, float range, float closing,
        float ataDeg, float angleOffDeg, float tv, float mySpeed, float myVc,
        float myNInst, float myTurnRate, float tTurnRate, float dE, ApMotionTrack tr, ApTargetTrack T)
    {
        float aggression = c.Dogfight.Aggression;
        ApDogfightState.FightPhase next = ApDogfightState.FightPhase.Neutral;
        float gunRange = c.S.GunRange > 0f ? c.S.GunRange : 1500f;
        bool merging = closing > 10f && ataDeg < 60f && range < 3f * gunRange;

        Vector3 velW = c.S.Vel;
        float v = velW.Length();
        if (v < 1f) { o.LetGo = ApReason.TooSlow; return ApDogfightState.FightPhase.Extend; }

        Vector3 up = c.Up;
        Vector3 flatVel = ApMath.Flat(velW, up);
        if (flatVel.LengthSquared() < 1f) flatVel = velW; else flatVel = Vector3.Normalize(flatVel);

        if (merging)
        {
            // Lag pursuit to control overshoot
            float tgo = Math.Clamp(range / MathF.Max(MathF.Abs(closing), 30f), 0f, 6f);
            Vector3D aimP = tr.Predict(tgo);
            Vector3 toAim = ApMath.Flat((Vector3)(aimP - c.S.Pos), up);
            float t = toAim.Length();
            if (t < 1f) toAim = -T.Los; else toAim = Vector3.Normalize(toAim);

            float n = MathF.Min(myNInst, c.LiftNMax);
            if (mySpeed < myVc * 0.8f) n = MathF.Min(n, mySpeed / (myVc * 0.8f) * myNInst);
            n = Math.Clamp(n, 2f, MathF.Min(myNInst, c.LiftNMax));

            o.Goal.Kind = ApExternalKind.Lift;
            o.Goal.Lift = toAim;
            o.Goal.Load = n;
            o.Goal.Throttle = aggression > 0.6f ? 1f : 0.9f;

            // Transition to Offensive once behind 3-9 line
            if (angleOffDeg < 90f && range < 1.5f * gunRange)
                next = ApDogfightState.FightPhase.Offensive;
        }
        else
        {
            // Not merging: close on target
            Vector3 toTarget = ApMath.Flat((Vector3)(tr.Predict(2f) - c.S.Pos), up);
            float t = toTarget.Length();
            if (t < 1f) toTarget = -T.Los; else toTarget = Vector3.Normalize(toTarget);

            o.Goal.Kind = ApExternalKind.Lift;
            o.Goal.Lift = toTarget;
            o.Goal.Load = MathF.Min(2.5f, c.LiftNMax);
            o.Goal.Throttle = 1f;
        }

        return next;
    }

    ApDogfightState.FightPhase OffensiveStep(ApSlotCtx c, ref ApSlotOut o, float range, float closing,
        float ataDeg, float angleOffDeg, float tv, float mySpeed, float myVc,
        float myNInst, float myNSust, float myTurnRate, float tTurnRate, float dE, ApMotionTrack tr, ApTargetTrack T)
    {
        ApDogfightState.FightPhase next = ApDogfightState.FightPhase.Offensive;
        float gunRange = c.S.GunRange > 0f ? c.S.GunRange : 1500f;

        // Check for defensive transition
        bool threatened = angleOffDeg > 120f || (closing < -5f && range < gunRange);
        if (threatened) return ApDogfightState.FightPhase.Defensive;

        bool overshoot = closing > 0.25f * mySpeed && range < 0.6f * gunRange;
        bool highClosure = closing > 0.2f * mySpeed;

        Vector3 velW = c.S.Vel;
        float v = velW.Length();
        if (v < 1f) { o.LetGo = ApReason.TooSlow; return ApDogfightState.FightPhase.Extend; }
        Vector3 up = c.Up;

        if (overshoot || highClosure)
        {
            // Lag pursuit: aim behind target's tail
            float tgo = Math.Clamp(range / MathF.Max(MathF.Abs(closing), 30f), 0f, 6f);
            Vector3D aimP = tr.Predict(tgo);
            Vector3 behind = -Vector3.Normalize(tr.Vel);
            behind = ApMath.Flat(behind, up);
            if (behind.LengthSquared() < 0.1f) behind = -T.Los;
            aimP = tr.Pos + (Vector3D)(behind * MathF.Max(-0.5f * tgo * tv, 200f));

            Vector3 toAim = ApMath.Flat((Vector3)(aimP - c.S.Pos), up);
            float t = toAim.Length();
            if (t < 1f) toAim = -T.Los; else toAim = Vector3.Normalize(toAim);

            float n = MathF.Min(myNSust, c.LiftNMax);
            if (mySpeed < myVc * 0.9f) n = MathF.Min(n, 1.5f);

            o.Goal.Kind = ApExternalKind.Lift;
            o.Goal.Lift = toAim;
            o.Goal.Load = n;
            o.Goal.Throttle = mySpeed < myVc ? 1f : 0.8f;
        }
        else if (range > gunRange)
        {
            // Lead pursuit
            float tgo = Math.Clamp(range / MathF.Max(MathF.Abs(closing), 30f), 0f, 6f);
            Vector3D aimP = tr.Predict(tgo);
            Vector3 toAim = ApMath.Flat((Vector3)(aimP - c.S.Pos), up);
            float t = toAim.Length();
            if (t < 1f) toAim = -T.Los; else toAim = Vector3.Normalize(toAim);

            o.Goal.Kind = ApExternalKind.Lift;
            o.Goal.Lift = toAim;
            o.Goal.Load = MathF.Min(myNInst, c.LiftNMax);
            o.Goal.Throttle = 1f;
        }
        else
        {
            // In gun range: lead to guns solution
            float tgo = Math.Clamp(range / MathF.Max(MathF.Abs(closing), 30f), 0f, 4f);
            Vector3D aimP = tr.Predict(tgo);
            Vector3 toAim = ApMath.Flat((Vector3)(aimP - c.S.Pos), up);
            float t = toAim.Length();
            if (t < 1f) toAim = -T.Los; else toAim = Vector3.Normalize(toAim);

            o.Goal.Kind = ApExternalKind.Lift;
            o.Goal.Lift = toAim;
            o.Goal.Load = MathF.Min(myNSust, c.LiftNMax);
            o.Goal.Throttle = 1f;
        }

        // Stall detection
        if (State.Time > 30f)
            next = ApDogfightState.FightPhase.Extend;

        return next;
    }

    ApDogfightState.FightPhase DefensiveStep(ApSlotCtx c, ref ApSlotOut o, float range, float closing,
        float ataDeg, float angleOffDeg, float tv, float mySpeed, float myVc,
        float myNInst, float myNSust, float tTurnRate, ApMotionTrack tr, ApTargetTrack T)
    {
        ApDogfightState.FightPhase next = ApDogfightState.FightPhase.Defensive;
        float gunRange = c.S.GunRange > 0f ? c.S.GunRange : 1500f;

        Vector3 velW = c.S.Vel;
        float v = velW.Length();
        if (v < 1f) { o.LetGo = ApReason.TooSlow; return ApDogfightState.FightPhase.Extend; }

        // Break turn: max g toward threat
        Vector3 toThreat = ApMath.Flat((Vector3)(T.Pos - c.S.Pos), c.Up);
        float t = toThreat.Length();
        if (t < 1f) toThreat = T.Los; else toThreat = Vector3.Normalize(toThreat);

        float n = MathF.Min(myNInst, c.LiftNMax);
        float throttle = mySpeed < myVc ? 1f : 0.3f;

        o.Goal.Kind = ApExternalKind.Lift;
        o.Goal.Lift = toThreat;
        o.Goal.Load = n;
        o.Goal.Throttle = throttle;

        // Reversal check
        if (closing < -10f && range < 2f * gunRange)
            next = ApDogfightState.FightPhase.Offensive;

        // Extend out
        if (ataDeg > 60f && range > gunRange)
            next = ApDogfightState.FightPhase.Extend;

        return next;
    }

    ApDogfightState.FightPhase ExtendStep(ApSlotCtx c, ref ApSlotOut o, float range, float closing,
        float ataDeg, float angleOffDeg, float tv, float mySpeed, float myVc,
        float myNInst, float dE, ApMotionTrack tr, ApTargetTrack T)
    {
        float gunRange = c.S.GunRange > 0f ? c.S.GunRange : 1500f;

        Vector3 velW = c.S.Vel;
        float v = velW.Length();
        if (v < 1f) { o.LetGo = ApReason.TooSlow; return ApDogfightState.FightPhase.Neutral; }

        // Heading away from target with slight random offset
        Vector3 away = ApMath.Flat(-T.Los, c.Up);
        float a = away.Length();
        if (a < 0.1f) away = -ApMath.Flat(velW, c.Up);
        else away = away / a;

        float seed = MathF.Sin(State.Time * 0.7f) * 0.3f;
        away = away * MathF.Cos(seed) + ApMath.Flat(Vector3.Cross(away, c.Up), c.Up) * MathF.Sin(seed);

        float n = MathF.Min(1.5f, myNInst);
        if (n < 1f) n = 1f;

        o.Goal.Kind = ApExternalKind.Lift;
        o.Goal.Lift = away;
        o.Goal.Load = n;
        o.Goal.Throttle = 1f;

        // Reset condition
        float turnR = mySpeed * mySpeed / (c.G * MathF.Max(MathF.Sqrt(myNInst * myNInst - 1f), 0.1f));
        float resetRange = gunRange + 2f * turnR;
        bool ready = range > resetRange && mySpeed >= 0.9f * myVc;

        ApDogfightState.FightPhase next = ApDogfightState.FightPhase.Neutral;
        if (ready)
            next = ApDogfightState.FightPhase.Neutral;
        else if (closing > -10f && State.Time > 5f)
            next = ApDogfightState.FightPhase.Neutral;

        return next;
    }
}

public sealed class ApDogfightState
{
    public enum FightPhase : byte { Neutral = 0, Offensive = 1, Defensive = 2, Extend = 3 }
    public FightPhase Phase = FightPhase.Neutral;
    public float Time;
    public float LeadBlend;
    public float Tof;
    public float AimErrDeg;

    public bool Dirty;

    public void Reset()
    {
        Phase = FightPhase.Neutral;
        Time = 0f;
        LeadBlend = 0f;
        Tof = 0f;
        AimErrDeg = 0f;
        Dirty = false;
    }

    public string Save()
    {
        return $"{(byte)Phase},{Time:0.0},{LeadBlend:0.0}";
    }

    public void Load(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        var parts = s.Split(',');
        if (parts.Length >= 3)
        {
            byte b = byte.TryParse(parts[0], out byte pb) ? pb : (byte)0;
            Phase = (FightPhase)b;
            Time = float.TryParse(parts[1], out var t) ? t : 0f;
            LeadBlend = float.TryParse(parts[2], out var lb) ? lb : 0f;
        }
    }
}
