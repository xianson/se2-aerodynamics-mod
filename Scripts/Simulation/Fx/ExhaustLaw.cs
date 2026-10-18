#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;

namespace AeroMod;

/// <summary>
/// THE EXHAUST'S LAW (2026-10-17, the user's approved design) - game-free (Tests/ExhaustTests). What the ramjets' and solid rocket
/// nozzles' exhaust shows, from the engine's state; Fx/AeroExhaustComponent (client) feeds it and draws the result with the mod's own
/// particle effects (tools/meshgen/gen_propulsor_defs.py gen_exhaust; their authored numbers: Blocks/ExhaustBlocks.g.cs). Lengths are
/// in EXIT RADII (the block's nozzle: ExhaustBlocks.Row.ExitRadius); a "strength" is an effect time, 0..1 (the emission rate and the
/// brightness the effect was authored with along its timeline - as the game's own flame is driven).
///   RAMJET   an almost clear, blue-violet hydrogen flame. Nothing below RamjetMachMin (Mach 0.5): its strength is gated in by the Mach
///            (smoothstep to RamjetMachFull) and is the thrust it gives now as a share of its rating (the thrust share x its Mach /
///            air / inlet factor), so it lights up as the thrust builds; its length grows with it (RamjetLenMin..RamjetLenMax).
///            SHOCK DIAMONDS: none below DiamondMach0; then more of them with the Mach (up to DiamondMax at DiamondMach1), spaced
///            DiamondSpace0 + DiamondSpaceK x sqrt(M^2 - 1) radii (a jet's shock cells lengthen with its Mach), sharper (smaller knots)
///            and brighter with the Mach, each fainter down the plume (DiamondDecay); only while the flame is strong enough.
///            INLET UNSTART: a POP (the flame it had, PopGain bright, for PopSeconds), a short SPUTTER (UnstartSputterSeconds), then
///            out until the inlet relights - and it comes back with the thrust (the inlet spools up; the flame eases in).
///   SOLID    a big bright orange-white plume whose LENGTH follows the thrust (ThrustNow / one segment's sea-level thrust = the stack
///            factor x the power level x the steering x the air's +10 %): PlumeLenPerRadius x ratio^PlumeLenExp radii, capped. In thin
///            air or vacuum (pressure ratio p -> 0) the plume BALLOONS (a wide dim cone: strength x (1-p)^BalloonExp, BalloonWiden x
///            (1-p) wider) and its core dims to VacuumCoreAlpha; the SMOKE TRAIL thins away (strength x p^SmokeExp, alpha sqrt(p);
///            none below SmokeMinPressure). BURNOUT (lit -> not lit): a quick SPUTTER (BurnoutSputterSeconds) from the last thrust.
///   EASING   a strength rises with RiseTau and falls with FallTau (s) - a pop and a sputter are set directly (crisp).
/// No allocation: the states and outputs are structs.
/// </summary>
public static class ExhaustLaw
{
    // ── knobs (harness: aeroset ExhaustLaw.X) ──
    public static float RiseTau = 0.08f, FallTau = 0.2f;
    /// <summary>The ramjet's Mach gate: nothing below RamjetMachMin, full from RamjetMachFull (smoothstep between).</summary>
    public static float RamjetMachMin = 0.5f, RamjetMachFull = 0.65f;
    /// <summary>The ramjet flame's length (exit radii) at a whisker and at full strength.</summary>
    public static float RamjetLenMin = 1.5f, RamjetLenMax = 7f;
    /// <summary>Shock diamonds: none at or below DiamondMach0, all DiamondMax from DiamondMach1; only above DiamondMinStrength.</summary>
    public static float DiamondMach0 = 1.1f, DiamondMach1 = 4f, DiamondMinStrength = 0.15f;
    public const int DiamondMax = 5;
    /// <summary>Their spacing (exit radii) DiamondSpace0 + DiamondSpaceK x sqrt(M^2 - 1), the first DiamondFirst radii aft of the exit.</summary>
    public static float DiamondSpace0 = 1.2f, DiamondSpaceK = 0.5f, DiamondFirst = 1.0f;
    /// <summary>A knot's size multiplier just past DiamondMach0 (soft) and at DiamondMach1 (sharp), its strength there (x the flame's),
    /// and each next knot's strength x DiamondDecay.</summary>
    public static float DiamondSizeSoft = 1f, DiamondSizeSharp = 0.45f, DiamondBrightSoft = 0.5f, DiamondBrightSharp = 1f, DiamondDecay = 0.72f;
    /// <summary>The unstart pop (s, brightness x) and the sputter after it (s); the sputter's flicker rate (Hz).</summary>
    public static float PopSeconds = 0.07f, PopGain = 1.8f, UnstartSputterSeconds = 0.35f, SputterHz = 22f;
    /// <summary>The solid plume's length (exit radii) = PlumeLenPerRadius x ratio^PlumeLenExp, at most PlumeLenMaxRadii.</summary>
    public static float PlumeLenPerRadius = 7f, PlumeLenExp = 0.5f, PlumeLenMaxRadii = 16f;
    /// <summary>The thrust ratio of a full-throat stack in vacuum (SolidRocket.MaxStackFactor x (1 + VacuumBoost)): the plume's top strength.</summary>
    public static float RatioMax = 3.3f;
    /// <summary>Thin air: the balloon's widening at vacuum, its exponent on (1 - p), the core's alpha at vacuum.</summary>
    public static float BalloonWiden = 2.5f, BalloonExp = 0.7f, VacuumCoreAlpha = 0.55f;
    /// <summary>The smoke: its exponent on p, no smoke below SmokeMinPressure, its strength capped at SmokeMax (the LIFETIME cap: the
    /// smoke's life and rate grow with its strength - ExhaustBlocks.SmokeLifeMin..Max).</summary>
    public static float SmokeExp = 1f, SmokeMinPressure = 0.02f, SmokeMax = 1f;
    /// <summary>The burnout sputter (s).</summary>
    public static float BurnoutSputterSeconds = 0.7f;
    /// <summary>The smoke emitter's box is stretched along the flight to cover this many seconds of travel (no gaps at speed).</summary>
    public static float SmokeStretchSeconds = 0.05f, SmokeStretchMax = 40f;
    /// <summary>A plume's flicker (share of its strength) and rate (Hz).</summary>
    public static float FlickerAmp = 0.06f, FlickerHz = 20f;

    // ── helpers ──
    static float Clamp01(float x) => x > 0f ? (x < 1f ? x : 1f) : 0f;   // (NaN -> 0)
    static float Smooth01(float x) { x = Clamp01(x); return x * x * (3f - 2f * x); }
    /// <summary>A deterministic hash of k in 0..1 (the sputters' and flicker's pattern: no Random, no allocation).</summary>
    public static float Hash01(int k)
    {
        uint x = unchecked((uint)k * 0x9E3779B1u); x ^= x >> 15; x = unchecked(x * 0x85EBCA77u); x ^= x >> 13; x = unchecked(x * 0xC2B2AE3Du); x ^= x >> 16;
        return (x & 0xFFFF) / 65535f;
    }

    /// <summary>One easing step toward target (RiseTau up, FallTau down); NaN-safe, never overshoots.</summary>
    public static float Ease(float current, float target, float dt)
    {
        if (!float.IsFinite(target)) target = 0f;
        if (!float.IsFinite(current)) current = 0f;
        if (!(dt > 0f)) return current;
        float tau = target > current ? RiseTau : FallTau;
        if (!(tau > 0f)) return target;
        return current + (target - current) * (1f - MathF.Exp(-dt / tau));
    }

    /// <summary>A sputter t s into one that lasts T s: on / off (dark) at hz (a hashed pattern), the on-level decaying to 0 by T; 0 after T.</summary>
    public static float Sputter(float t, float T, float hz)
    {
        if (!(t >= 0f) || !(T > 0f) || t >= T) return 0f;
        int k = (int)(t * MathF.Max(1f, hz));
        float h = Hash01(k + 7919);
        float decay = 1f - t / T;
        return h > 0.4f ? decay * (0.55f + 0.45f * h) : 0f;   // (off: dark - the gaps of a sputter)
    }

    /// <summary>A plume's flicker at time t (s): 1 - FlickerAmp x a hashed value per 1 / FlickerHz (seed: per engine).</summary>
    public static float Flicker(double t, int seed)
    {
        if (!(FlickerAmp > 0f)) return 1f;
        int k = (int)(t * MathF.Max(1f, FlickerHz));
        return 1f - FlickerAmp * Hash01(k * 31 + seed);
    }

    // ── RAMJET ──
    public struct RamjetIn
    {
        /// <summary>The flight Mach, the thrust share (0..1 of its maximum now - the controller's or the game's), its thrust factor
        /// (its maximum now / its rating: RamjetLaw.Factor), its inlet unstarted, the mod's thrust shaping on (off: a plain hydrogen
        /// thruster - its share alone, no Mach gate).</summary>
        public float Mach, Share, Factor; public bool Unstarted, AeroOn;
    }
    public struct RamjetState { public float Shown, AtUnstart, SinceUnstart; public bool Unstarted; public int Unstarts; }
    public struct RamjetOut
    {
        /// <summary>Flame: its strength (effect time); FlameLength (exit radii); Glow (brightness x: 1, the pop's PopGain).
        /// Diamonds shown, their spacing and the first's distance (exit radii), the first's strength, the knots' size multiplier.</summary>
        public float Flame, FlameLength, Glow; public int Diamonds; public float DiamondSpacing, DiamondFirst, DiamondStrength, DiamondScale;
    }

    /// <summary>The ramjet's flame strength for its state now (before easing): gated in by the Mach (nothing below RamjetMachMin),
    /// the thrust it gives as a share of its rating (share x factor, 0..1). Aero off: its share (a plain hydrogen thruster).</summary>
    public static float RamjetStrength(float mach, float share, float factor, bool aeroOn)
    {
        if (!aeroOn) return Clamp01(share);
        if (!(mach >= RamjetMachMin)) return 0f;   // (NaN: none)
        float gate = RamjetMachFull > RamjetMachMin ? Smooth01((mach - RamjetMachMin) / (RamjetMachFull - RamjetMachMin)) : 1f;
        return gate * Clamp01(Clamp01(share) * (float.IsFinite(factor) ? MathF.Max(0f, factor) : 0f));
    }

    /// <summary>The flame's length (exit radii) at a strength: 0 at none, RamjetLenMin..RamjetLenMax.</summary>
    public static float RamjetLength(float strength) => strength > 0.002f ? RamjetLenMin + (RamjetLenMax - RamjetLenMin) * Clamp01(strength) : 0f;

    /// <summary>The shock diamonds' share of their full set at this Mach (0 at or below DiamondMach0, 1 from DiamondMach1).</summary>
    public static float DiamondMach01(float mach) => DiamondMach1 > DiamondMach0 ? Clamp01((mach - DiamondMach0) / (DiamondMach1 - DiamondMach0)) : (mach > DiamondMach0 ? 1f : 0f);
    /// <summary>How many diamonds at this Mach and flame strength: none at or below DiamondMach0 or under DiamondMinStrength; then
    /// ceil(DiamondMax x the Mach's share) - more with the Mach, all from DiamondMach1.</summary>
    public static int DiamondCount(float mach, float strength)
    {
        if (!(strength >= DiamondMinStrength)) return 0;
        float m = DiamondMach01(mach);
        if (!(m > 0f)) return 0;
        return Math.Clamp((int)MathF.Ceiling(DiamondMax * m - 1e-4f), 1, DiamondMax);
    }
    /// <summary>The shock-cell spacing (exit radii) at this Mach: DiamondSpace0 + DiamondSpaceK x sqrt(M^2 - 1) (subsonic: DiamondSpace0).</summary>
    public static float DiamondSpacing(float mach) => DiamondSpace0 + DiamondSpaceK * MathF.Sqrt(MathF.Max(0f, (float.IsFinite(mach) ? mach * mach : 0f) - 1f));
    /// <summary>A knot's size multiplier at this Mach (smaller = sharper with the Mach).</summary>
    public static float DiamondScale(float mach) { float m = DiamondMach01(mach); return DiamondSizeSoft + (DiamondSizeSharp - DiamondSizeSoft) * m; }
    /// <summary>The first knot's strength for a flame strength at this Mach (brighter with the Mach); knot k: x DiamondDecay^k.</summary>
    public static float DiamondStrength(float mach, float strength) { float m = DiamondMach01(mach); return Clamp01(strength * (DiamondBrightSoft + (DiamondBrightSharp - DiamondBrightSoft) * m)); }

    /// <summary>The unstart envelope t s after the inlet unstarted (x the flame it had): 1 through the pop, the sputter, then 0.</summary>
    public static float UnstartEnvelope(float t)
    {
        if (!(t >= 0f)) return 0f;
        if (t < PopSeconds) return 1f;
        return Sputter(t - PopSeconds, UnstartSputterSeconds, SputterHz);
    }

    /// <summary>One frame of a ramjet's exhaust (dt s).</summary>
    public static void Ramjet(ref RamjetState s, in RamjetIn i, float dt, ref RamjetOut o)
    {
        float target = RamjetStrength(i.Mach, i.Share, i.Factor, i.AeroOn);
        if (i.Unstarted && !s.Unstarted) { s.AtUnstart = MathF.Max(s.Shown, target); s.SinceUnstart = 0f; s.Unstarts++; }
        if (!i.Unstarted && s.Unstarted) s.Shown = 0f;   // (relit: it comes back from nothing with the thrust)
        s.Unstarted = i.Unstarted;
        o.Glow = 1f;
        if (s.Unstarted)
        {
            s.SinceUnstart += float.IsFinite(dt) && dt > 0f ? dt : 0f;
            float env = UnstartEnvelope(s.SinceUnstart);
            s.Shown = s.AtUnstart * env;
            if (s.SinceUnstart < PopSeconds) o.Glow = PopGain;
            o.Flame = Clamp01(s.Shown); o.FlameLength = RamjetLength(o.Flame);
            o.Diamonds = 0; o.DiamondStrength = 0f; o.DiamondSpacing = DiamondSpacing(i.Mach); o.DiamondFirst = DiamondFirst; o.DiamondScale = DiamondScale(i.Mach);
            return;
        }
        s.Shown = Ease(s.Shown, target, dt);
        float f = s.Shown < 0.002f ? 0f : Clamp01(s.Shown);
        o.Flame = f; o.FlameLength = RamjetLength(f);
        o.Diamonds = i.AeroOn ? DiamondCount(i.Mach, f) : 0;
        o.DiamondSpacing = DiamondSpacing(i.Mach); o.DiamondFirst = DiamondFirst;
        o.DiamondStrength = o.Diamonds > 0 ? DiamondStrength(i.Mach, f) : 0f;
        o.DiamondScale = DiamondScale(i.Mach);
    }

    /// <summary>Knot k's strength (0: none) for an output.</summary>
    public static float KnotStrength(in RamjetOut o, int k) => k < o.Diamonds && k >= 0 ? o.DiamondStrength * MathF.Pow(DiamondDecay, k) : 0f;
    /// <summary>Knot k's distance aft of the exit (exit radii).</summary>
    public static float KnotDistance(in RamjetOut o, int k) => o.DiamondFirst + k * o.DiamondSpacing;

    // ── SOLID ROCKET ──
    public struct SolidIn
    {
        /// <summary>Ratio: the thrust on it now over one segment's sea-level thrust (ThrustNow / Row.Thrust: stack factor x level x
        /// steering x the air's boost); Pressure: the air's pressure ratio (0 vacuum .. 1 sea level); Speed (m/s, the grid's) and Scale
        /// (exit radius / ExhaustBlocks.RefRadius) for the smoke's stretch.</summary>
        public float Ratio, Pressure, Speed, Scale;
    }
    public struct SolidState { public float Shown, LastLit, SinceBurnout; public bool Burning, Sputtering; public int Burnouts; }
    public struct SolidOut
    {
        /// <summary>Plume: strength; PlumeLength (exit radii); CoreAlpha (the plume's alpha x); Balloon: strength, BalloonScale (its size
        /// x); Smoke: strength, SmokeAlpha, SmokeStretch (its emitter box along the flight x).</summary>
        public float Plume, PlumeLength, CoreAlpha, Balloon, BalloonScale, Smoke, SmokeAlpha, SmokeStretch;
    }

    /// <summary>The plume's length (exit radii) at a thrust ratio: PlumeLenPerRadius x ratio^PlumeLenExp, at most PlumeLenMaxRadii; 0 at none.</summary>
    public static float PlumeLength(float ratio)
    {
        if (!(ratio > 0f)) return 0f;
        float l = PlumeLenPerRadius * MathF.Pow(ratio, PlumeLenExp);
        return float.IsFinite(l) ? MathF.Min(l, PlumeLenMaxRadii) : PlumeLenMaxRadii;
    }
    /// <summary>The plume's strength at a thrust ratio: 0.3 + 0.7 sqrt(ratio / RatioMax), 0 at none.</summary>
    public static float PlumeStrength(float ratio) => ratio > 0.001f ? Clamp01(0.3f + 0.7f * MathF.Sqrt(Clamp01(ratio / MathF.Max(0.01f, RatioMax)))) : 0f;
    /// <summary>The smoke's strength: the plume's x p^SmokeExp (none below SmokeMinPressure), capped at SmokeMax.</summary>
    public static float SmokeStrength(float plume, float pressure)
    {
        float p = Clamp01(pressure);
        if (!(p >= SmokeMinPressure)) return 0f;
        return MathF.Min(Clamp01(SmokeMax), Clamp01(plume) * MathF.Pow(p, SmokeExp));
    }
    /// <summary>The balloon's strength: the plume's x (1 - p)^BalloonExp.</summary>
    public static float BalloonStrength(float plume, float pressure) => Clamp01(plume) * MathF.Pow(1f - Clamp01(pressure), BalloonExp);
    /// <summary>The smoke particles' life (s) at a smoke strength - as the effect was authored (ExhaustBlocks: SmokeLifeMin at 0 to
    /// SmokeLifeMax at 1, linear): never more than SmokeLifeMax.</summary>
    public static float SmokeLife(float smoke) => ExhaustBlocks.SmokeLifeMin + (ExhaustBlocks.SmokeLifeMax - ExhaustBlocks.SmokeLifeMin) * Clamp01(smoke);
    /// <summary>The smoke trail's rate (per s) at a smoke strength - as authored: 0, 10 % at 0.05, linear to SmokePpsMax at 1.</summary>
    public static float SmokeRate(float smoke)
    {
        float s = Clamp01(smoke);
        if (s <= 0f) return 0f;
        if (s < 0.05f) return ExhaustBlocks.SmokePpsMax * 0.1f * (s / 0.05f);
        return ExhaustBlocks.SmokePpsMax * (0.1f + 0.9f * (s - 0.05f) / 0.95f);
    }
    /// <summary>The most smoke particles one nozzle has alive at a smoke strength (trail rate x life + the billow's).</summary>
    public static float SmokeParticles(float smoke) => SmokeRate(smoke) * SmokeLife(smoke) + (smoke > 0f ? ExhaustBlocks.BillowPpsMax * Clamp01(smoke) * ExhaustBlocks.BillowLife : 0f);

    /// <summary>One frame of a solid rocket nozzle's exhaust (dt s).</summary>
    public static void Solid(ref SolidState s, in SolidIn i, float dt, ref SolidOut o)
    {
        float ratio = float.IsFinite(i.Ratio) ? MathF.Max(0f, i.Ratio) : 0f;
        bool lit = ratio > 0.001f;
        float step = float.IsFinite(dt) && dt > 0f ? dt : 0f;
        float shown;
        if (lit)
        {
            s.Burning = true; s.Sputtering = false; s.LastLit = ratio;
            s.Shown = Ease(s.Shown, ratio, step); shown = s.Shown;
        }
        else
        {
            if (s.Burning) { s.Burning = false; s.Sputtering = true; s.SinceBurnout = 0f; s.Burnouts++; }   // (burnout: lit -> not lit)
            if (s.Sputtering)
            {
                s.SinceBurnout += step;
                float env = Sputter(s.SinceBurnout, BurnoutSputterSeconds, SputterHz);
                if (s.SinceBurnout >= BurnoutSputterSeconds) { s.Sputtering = false; env = 0f; }
                s.Shown = s.LastLit * env;
            }
            else s.Shown = Ease(s.Shown, 0f, step);
            shown = s.Shown < 0.001f ? 0f : s.Shown;
        }
        float p = Clamp01(i.Pressure);
        float plume = PlumeStrength(shown);
        o.Plume = plume;
        o.PlumeLength = PlumeLength(shown);
        o.CoreAlpha = VacuumCoreAlpha + (1f - VacuumCoreAlpha) * p;
        o.Balloon = BalloonStrength(plume, p);
        o.BalloonScale = 1f + BalloonWiden * (1f - p);
        o.Smoke = SmokeStrength(plume, p);
        o.SmokeAlpha = MathF.Sqrt(p);
        float sc = i.Scale > 0.01f ? i.Scale : 1f;
        float sp = float.IsFinite(i.Speed) ? MathF.Abs(i.Speed) : 0f;
        o.SmokeStretch = MathF.Min(SmokeStretchMax, MathF.Max(1f, sp * SmokeStretchSeconds / sc));
    }
}
