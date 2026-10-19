#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// ENVELOPE PROTECTION - the reference governor on every autopilot attitude command (engine-free: Tests/AutopilotSim). The
/// guidance asks for an attitude; this gives the part of the step from the attitude now toward it that stays inside what the
/// ship can fly, from the mod's own measures (never a guess, never a hand-entered number):
///  - ALPHA PROT: the whole ship's best lift is StallMarginDeg further nose up (LiftPeak, the stall warning's evaluation) -
///    the command turns the flow by at most that less Autopilot.AoaMarginDeg; already past it, the nose is pushed back to it.
///  - G LIMIT: the lift between now (StallLiftInUse of the best) and the best (StallG x the weight), linear in the angle - the
///    command pulls at most Autopilot.GLimit.
///  - LOSS OF CONTROL, PREDICTED (Loac.cs): the evaluation is asked for the attitude the guidance wants (ApCommand.Probe) and
///    answers the worst need / reach on the way there and holding the attitude now; past LoacWarning.Limit the step is cut to
///    the share that keeps it under (linear between the two), before the ship is ever asked to go where it cannot hold.
/// A slow ship (under StallWarning.MinSpeed) or thin air: no aero limits (a hover is held by the lean and thrust limits).
/// Allocation-free; a few quaternion products a tick.
/// </summary>
public sealed class EnvelopeGuard
{
    /// <summary>The flow and density under which the air limits do not apply (StallWarning.MinSpeed / MinDensity).</summary>
    public const float MinSpeed = 20f, MinDensity = 0.01f;
    /// <summary>The predicted need / reach the step is held under (LoacWarning.Limit).</summary>
    public static float LoacLimit = 0.9f;
    /// <summary>A prediction made for an attitude within this of the one wanted now still stands (rad).</summary>
    public const float ProbeMatch = 10f * ApMath.Deg;
    /// <summary>The loss-of-control share of the step rises back this fast (per s); it falls at once.</summary>
    public const float LoacRecover = 0.5f;
    /// <summary>The stall and g caps spend the nose-up the ship's rate carries it through in this long (s).</summary>
    public const float LeadSeconds = 0.25f;

    float _loacFrac = 1f;
    /// <summary>The share of the step the loss-of-control governor allows now (diagnostics).</summary>
    public float LoacShare => _loacFrac;
    /// <summary>The flow turn the command makes (rad, + nose up), and the most it may (rad) - last tick's (diagnostics).</summary>
    public float LastPull, LastPullCap = float.PositiveInfinity;

    public void Reset() { _loacFrac = 1f; LastPull = 0f; LastPullCap = float.PositiveInfinity; }

    /// <summary>The air limits apply (fast enough, thick enough, the stall evaluated along a lift direction).</summary>
    public static bool AirLimits(in ApSense s)
        => s.StallFresh && s.Density > MinDensity && s.AirVel.Length() > MinSpeed && s.LiftDir.LengthSquared() > 0.5f;

    /// <summary>How far an attitude q turns the flow nose-up from now (rad): the air's way now seen from q, against the
    /// stall evaluation's flow (v) and lift (L) directions - LiftPeak's v(theta) = cos v - sin L.</summary>
    public static float Pull(in ApSense s, Quaternion q)
    {
        Vector3 v = Vector3.Normalize(s.AirVel), L = s.LiftDir;
        Vector3 vq = Vector3.Transform(Vector3.Transform(v, s.Orient), Quaternion.Inverse(q));
        return MathF.Atan2(-Vector3.Dot(vq, L), Vector3.Dot(vq, v));
    }

    /// <summary>The flow's nose-up turn (rad) since the stall evaluation (its flow, StallFlow; 0 unknown).</summary>
    public static float Since(in ApSense s)
    {
        if (s.StallFlow.LengthSquared() < 0.5f || s.AirVel.LengthSquared() < 1f) return 0f;
        Vector3 v = Vector3.Normalize(s.AirVel), v0 = s.StallFlow;
        // (LiftPeak: the flow seen theta further nose-up is cos v0 - sin L)
        return MathF.Atan2(-Vector3.Dot(v, s.LiftDir), Vector3.Dot(v, v0));
    }

    /// <summary>The most the command may turn the flow nose-up (rad): the stall's margin and the g limit's.</summary>
    public static float PullCap(in ApSense s, out ApProt which)
    {
        which = ApProt.None;
        // (the margin now: the evaluation's, less the nose-up turn of the flow since it was made)
        float since = Since(in s);
        float margin = s.StallMarginDeg * ApMath.Deg - since, cap = margin - s.Lim.AoaMarginDeg * ApMath.Deg;
        which = ApProt.Alpha;
        // (the g limit: the lift linear from now's share of the best to the best at the margin)
        if (s.StallG > s.Lim.GLimit && margin > 1e-3f)
        {
            float m0 = MathF.Max(s.StallMarginDeg * ApMath.Deg, 1e-3f);
            float inUse = Math.Clamp(s.StallLiftInUse + (1f - s.StallLiftInUse) * since / m0, 0f, 0.999f);
            float g = margin * (s.Lim.GLimit / s.StallG - inUse) / (1f - inUse);
            if (g < cap) { cap = g; which = ApProt.GLimit; }
        }
        return cap;
    }

    /// <summary>The governed attitude for the one wanted (desired), and what it changed (prot).</summary>
    public Quaternion Govern(in ApSense s, Quaternion desired, ref ApProt prot) => Govern(in s, desired, ref prot, Vector3.Zero);

    /// <summary>... pathRate: how fast the flight path itself turns (world rad/s: the across acceleration over the speed) - the
    /// ship turning with its path does not raise its angle of attack, only its rate past the path's does (the lead below).</summary>
    public Quaternion Govern(in ApSense s, Quaternion desired, ref ApProt prot, Vector3 pathRate)
    {
        LastPull = 0f; LastPullCap = float.PositiveInfinity;
        if (!s.Lim.Protection) { _loacFrac = 1f; return desired; }
        Quaternion now = s.Orient;
        float f = 1f;
        // ── LOSS OF CONTROL, predicted for the attitude wanted (the evaluation runs a few times a second) ──
        if (s.LoacFresh && ApMath.Angle(s.LoacProbeAt, desired) < ProbeMatch)
        {
            float want = 1f;
            if (s.LoacProbe > LoacLimit)
                want = s.LoacHere >= LoacLimit || s.LoacProbe <= s.LoacHere ? 0f : Math.Clamp((LoacLimit - s.LoacHere) / (s.LoacProbe - s.LoacHere), 0f, 1f);
            _loacFrac = want < _loacFrac ? want : MathF.Min(want, _loacFrac + LoacRecover * s.Dt);
        }
        else _loacFrac = MathF.Min(1f, _loacFrac + LoacRecover * s.Dt * 0.5f);   // (no prediction for this command yet: ease back)
        if (_loacFrac < 0.999f) { f = _loacFrac; prot |= ApProt.Loac; }
        Quaternion q = f < 1f ? Quaternion.Slerp(now, desired, f) : desired;
        // ── ALPHA PROT and the G LIMIT (the flow fast and the stall evaluated) ──
        if (AirLimits(in s))
        {
            float cap = PullCap(in s, out ApProt which);
            // (the turn already under way: the nose-up the ship's rate carries it through in LeadSeconds is spent from the cap -
            //  a heavy ship's pitch overshoots a target it was stopped at)
            Vector3 wRel = s.AngVel - Vector3.Transform(pathRate, Quaternion.Inverse(s.Orient));
            float w = wRel.Length();
            if (w > 1e-4f) { float lead = Pull(in s, Quaternion.Normalize(s.Orient * Quaternion.CreateFromAxisAngle(wRel / w, w * LeadSeconds))); if (lead > 0f) cap -= lead; }
            // (and the path turning: the attitude law follows a turning target a rate / LinearGain behind (it is given no target rate)
            //  - the angle of attack flown is the commanded less that, so the cap on the command is the cap plus it)
            Vector3 kPitch = Vector3.Cross(Vector3.Normalize(s.AirVel), s.LiftDir);
            if (kPitch.LengthSquared() > 1e-6f) cap += MathF.Abs(Vector3.Dot(Vector3.Transform(pathRate, Quaternion.Inverse(s.Orient)), Vector3.Normalize(kPitch))) / ThrustAllocator.LinearGain;
            float pull = Pull(in s, q);
            LastPull = pull; LastPullCap = cap;
            if (pull > cap)
            {
                prot |= which;
                // (the step split: its roll about the flow - which leaves the angle of attack as it is: a bank is never cut by the
                //  stall or g caps - and the rest, of which only the share that turns the flow by the cap is kept)
                Vector3 vL = Vector3.Normalize(s.AirVel);
                Quaternion rel = Quaternion.Normalize(Quaternion.Inverse(now) * q);
                Vector3 axis = new Vector3(rel.X, rel.Y, rel.Z);
                Quaternion twist = Quaternion.Normalize(new Quaternion(vL * Vector3.Dot(axis, vL), rel.W));
                if (!twist.IsValidAndRotationIsNormalized()) twist = Quaternion.Identity;
                Quaternion swing = Quaternion.Normalize(Quaternion.Inverse(twist) * rel), baseQ = Quaternion.Normalize(now * twist);
                if (cap <= 0f) q = Quaternion.Normalize(PushTo(in s, cap) * Quaternion.Inverse(now) * baseQ);   // (already past it: the nose back to the cap, about the lift's axis, the roll kept)
                else
                {
                    // (the share of the swing that turns the flow by the cap: Pull(base) = 0, Pull(q) > cap - bisection)
                    float lo = 0f, hi = 1f;
                    for (int i = 0; i < 12; i++) { float m = 0.5f * (lo + hi); if (Pull(in s, Quaternion.Normalize(baseQ * Quaternion.Slerp(Quaternion.Identity, swing, m))) > cap) hi = m; else lo = m; }
                    q = Quaternion.Normalize(baseQ * Quaternion.Slerp(Quaternion.Identity, swing, lo));
                }
            }
        }
        return Quaternion.Normalize(q);
    }

    /// <summary>The attitude now turned about the lift's axis so the flow is turned by cap (rad, negative: nose down).</summary>
    static Quaternion PushTo(in ApSense s, float cap)
    {
        Vector3 v = Vector3.Normalize(s.AirVel), k = Vector3.Cross(v, s.LiftDir);
        if (k.LengthSquared() < 1e-8f) return s.Orient;
        k = Vector3.Normalize(k);
        var probe = Quaternion.Normalize(s.Orient * Quaternion.CreateFromAxisAngle(k, 0.01f));
        if (Pull(in s, probe) < 0f) k = -k;   // (the way that pulls the nose up, so cap's sign gives the way)
        return Quaternion.Normalize(s.Orient * Quaternion.CreateFromAxisAngle(k, cap));
    }
}
