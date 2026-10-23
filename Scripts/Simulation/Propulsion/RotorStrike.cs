#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// ROTOR STRIKES (2026-10-17, the user's design: docs/PROPULSOR_SPECS.md "Rotor strikes"), game-free - the probes' pattern and
/// every law (Tests/RotorStrikeTests). The game side is AeroRotorStrikeComponent (the probes, the blades' state and save) and
/// RotorStrikeServer (each strike's consequences, once a tick).
///   PROBES     a spinning rotor or prop (above MinRpm) casts RaysPerTick rays a server tick, round-robin over its disc: ChordsPerTick
///              of the RingChords chords of its blade-tip circle (the whole tip circle every RingChords / ChordsPerTick ticks) and
///              one radial SPOKE from InnerFrac x R to the tip (the disc's inside, SpokeSteps spokes in a spread order). The pattern
///              is geometric: it covers the circle in the same ticks at any rpm (the blades sweep it many times between).
///   SPEED      what hits is the blade at the hit's radius r: v = rpm x TipSpeed x r / R (RotorKinematics.TipSpeed: every governed
///              propulsor's tips at 200 m/s at 100 %). Under MinStrikeSpeed it is no strike.
///   RATE       an obstacle that stays in the disc is struck once per blade pass (Blades blades), at least GapTicksMin ticks apart.
///   BLADE WEAR per strike: StrikeWearFull x the material's hardness x (v / TipSpeed)^2 (the strike's energy share), of the blades'
///              health 1. Rock 1, metal 0.6, a character 0.15. At 0 the blades are BROKEN: the rotor stops, no thrust, no blur.
///   THE BLOCK  the struck block loses BlockShareFull x (v / TipSpeed)^2 x min(m_eff / BlockRefMass, BlockMassCap) of its full
///              health a strike (a big rotor's blade cuts deeper). A character: (v / LethalSpeed)^2 of its full health - one strike
///              at LethalSpeed or more kills, a barely turning prop bruises.
///   THE SHIP   an impulse at the strike point against the blade's motion, J = m_eff x v x the material's speed loss (the blade
///              segment's share of its speed the obstacle takes), m_eff = the blade's mass seen at r (a uniform blade turning on
///              its hub: I_blade / r^2 = m R^2 / (3 r^2), at most m). A character takes the same impulse along the blade's
///              motion, capped at KnockMaxSpeed of its own mass (the ship takes what the character took). On breaking, the rotor's
///              angular momentum L = I_rotor x omega (I_rotor = Blades x m R^2 / 3) is dumped into the ship about the spin axis.
///   BLADE MASS m = BladeMassPerR2 x R^2 (a composite blade: ~27 kg/m^2 of planform, chord 0.065 R - the 1.5 m rotor's 7.5 m
///              blade 98 kg, a UH-60's 8.2 m one ~115 kg).
///   REPAIR     the blades are part of the block: each wear is dealt to the rotor block's integrity too (BladeShareOfBlock of it,
///              at least MinBlockWear of its full health a strike), and welding the block back to full integrity (RepairIntegrity)
///              restores the blades. A world that refuses block damage refuses the wear too.
/// Frames: the block's (VRage). A main or reversible rotor turns about -Y (clockwise seen from above, RotorKinematics), its disc
/// in X / Z; a turboprop's prop about (0, 0, -1) (Forward, AeroRotorAnimComponent.PropSpinAxis), its disc in X / Y. The hub is
/// the block's centre + the axis x the table's Hub offset.
/// </summary>
public static class RotorStrike
{
    /// <summary>Off: no probes, no strikes (harness: aeroset RotorStrike.Enabled false).</summary>
    public static bool Enabled = true;

    // ── probes ──
    /// <summary>Probe only above this share of the governed rpm (a parked rotor casts nothing).</summary>
    public static float MinRpm = 0.03f;
    /// <summary>The tip circle's chords (12: 30 deg each - a chord's middle is 3.4 % of R inside the tip) and how many a tick.</summary>
    public const int RingChords = 12, ChordsPerTick = 3;
    /// <summary>The radial spokes (16: 22.5 deg apart, one a tick in a spread order) and where they start (share of R).</summary>
    public const int SpokeSteps = 16;
    public static float InnerFrac = 0.3f;
    /// <summary>Rays a rotor casts a tick (the probe budget).</summary>
    public const int RaysPerTick = ChordsPerTick + 1;
    /// <summary>The blades every strike-capable rotor has (the main rotors' rig, the Reversible Rotor, the turboprops' blockout: 3).</summary>
    public const int Blades = 3;

    // ── speeds ──
    /// <summary>A hit slower than this (m/s) is no strike.</summary>
    public static float MinStrikeSpeed = 2f;
    /// <summary>Strikes on one rotor at least this many ticks apart (and a blade pass apart).</summary>
    public const int GapTicksMin = 4;

    // ── the blades ──
    public enum Material { Rock, Metal, Character }
    /// <summary>The blades' wear a strike at full tip speed on rock (3 break them).</summary>
    public static float StrikeWearFull = 0.34f;
    /// <summary>How hard each thing is on the blades (rock > metal > character).</summary>
    public static float Hardness(Material m) => m == Material.Rock ? 1f : m == Material.Metal ? 0.6f : 0.15f;
    /// <summary>The share of the striking blade segment's speed the obstacle takes (the ship's impulse).</summary>
    public static float SpeedLoss(Material m) => m == Material.Rock ? 1f : m == Material.Metal ? 0.7f : 0.15f;
    /// <summary>Blade mass per tip radius squared (kg / m^2).</summary>
    public static float BladeMassPerR2 = 1.75f;

    // ── the struck ──
    /// <summary>A struck block's health share a full-speed strike takes, for a blade of BlockRefMass at the hit (the 1.5 m rotor's
    /// tip), and the most a heavier blade multiplies it.</summary>
    public static float BlockShareFull = 0.15f, BlockRefMass = 30f, BlockMassCap = 3f;
    /// <summary>A character: one strike at this blade speed (m/s) takes its whole health.</summary>
    public static float LethalSpeed = 45f;
    /// <summary>The most a strike throws a character (m/s).</summary>
    public static float KnockMaxSpeed = 15f;
    /// <summary>A character's mass when its body has none to read (kg).</summary>
    public const float CharacterMass = 100f;

    // ── the rotor block ──
    /// <summary>The share of the rotor block's health the blades are (each wear x this), the least a strike takes of it, and the
    /// integrity that counts as welded back up.</summary>
    public static float BladeShareOfBlock = 0.5f, MinBlockWear = 0.01f, RepairIntegrity = 0.999f;

    // ═══ the probes ═══

    /// <summary>The disc's spin axis (the rotor's angular velocity's direction) in the block's frame.</summary>
    public static Vector3 SpinAxis(AeroPropulsorComponent.Kinds k) => k == AeroPropulsorComponent.Kinds.Turboprop ? new Vector3(0f, 0f, -1f) : new Vector3(0f, -1f, 0f);
    /// <summary>The way the hub sits from the block's centre (the Hub offset's direction) in the block's frame.</summary>
    public static Vector3 HubAxis(AeroPropulsorComponent.Kinds k) => k == AeroPropulsorComponent.Kinds.Turboprop ? new Vector3(0f, 0f, -1f) : Vector3.UnitY;
    /// <summary>Two unit vectors spanning the disc (block frame).</summary>
    public static void DiscBasis(AeroPropulsorComponent.Kinds k, out Vector3 u, out Vector3 w)
    {
        if (k == AeroPropulsorComponent.Kinds.Turboprop) { u = Vector3.UnitX; w = Vector3.UnitY; }
        else { u = Vector3.UnitX; w = Vector3.UnitZ; }
    }
    /// <summary>The kinds that strike (helicopter rotors, the Reversible Rotor, turboprops; the ducted fans and ramjets do not).</summary>
    public static bool Strikes(AeroPropulsorComponent.Kinds k) => k == AeroPropulsorComponent.Kinds.Rotor || k == AeroPropulsorComponent.Kinds.TailRotor || k == AeroPropulsorComponent.Kinds.Turboprop;

    /// <summary>Chord c (0..RingChords-1) of the tip circle of radius R, its ends in the disc's (u, w) plane.</summary>
    public static void Chord(int c, float R, out Vector2 a, out Vector2 b)
    {
        float step = 2f * MathF.PI / RingChords, a0 = c * step, a1 = a0 + step;
        a = new Vector2(MathF.Cos(a0), MathF.Sin(a0)) * R; b = new Vector2(MathF.Cos(a1), MathF.Sin(a1)) * R;
    }

    /// <summary>Spoke s's turn (rad): the bit-reversed order of the SpokeSteps directions (0, 180, 90, 270, 45 ... deg) - each
    /// new spoke halves the largest gap left.</summary>
    public static float SpokeAngle(int s)
    {
        int n = SpokeSteps, bits = 0; while ((1 << bits) < n) bits++;
        int i = s % n, r = 0;
        for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
        return r * 2f * MathF.PI / n;
    }

    /// <summary>Spoke s, from InnerFrac x R to the tip, in the disc's plane.</summary>
    public static void Spoke(int s, float R, out Vector2 a, out Vector2 b)
    {
        float t = SpokeAngle(s);
        var d = new Vector2(MathF.Cos(t), MathF.Sin(t));
        a = d * (InnerFrac * R); b = d * R;
    }

    /// <summary>This tick's rays (disc plane): ChordsPerTick chords from chord counter 'chord' and spoke 'spoke'; the counters move on.
    /// into: RaysPerTick pairs (start, end).</summary>
    public static void TickRays(ref int chord, ref int spoke, float R, Span<Vector2> from, Span<Vector2> to)
    {
        for (int k = 0; k < ChordsPerTick; k++) { Chord(chord, R, out from[k], out to[k]); chord = (chord + 1) % RingChords; }
        Spoke(spoke, R, out from[ChordsPerTick], out to[ChordsPerTick]); spoke = (spoke + 1) % SpokeSteps;
    }

    /// <summary>A hit on the rotor's own grid at this cell is the rotor's own block (its AABB in cells, one cell of slack: the hit is on
    /// its surface) - never a strike.</summary>
    public static bool IsOwnCell(Vector3I c, Vector3I min, Vector3I max)
        => c.X >= min.X - 1 && c.X <= max.X + 1 && c.Y >= min.Y - 1 && c.Y <= max.Y + 1 && c.Z >= min.Z - 1 && c.Z <= max.Z + 1;

    // ═══ the laws ═══

    /// <summary>The rotor's angular speed (rad/s) at this rpm (0..1 governed) and tip radius.</summary>
    public static float Omega(float rpm, float R) => R > 0f ? Math.Clamp(rpm, 0f, 1f) * RotorKinematics.TipSpeed / R : 0f;
    /// <summary>The blade's speed (m/s) at radius r.</summary>
    public static float SpeedAt(float rpm, float r, float R) => Omega(rpm, R) * Math.Clamp(r, 0f, R);
    /// <summary>The strike's energy share: (v / the full tip speed)^2.</summary>
    public static float Severity(float v) { float x = MathF.Max(v, 0f) / RotorKinematics.TipSpeed; return x * x; }
    /// <summary>A hit fast enough to be a strike.</summary>
    public static bool IsStrike(float v) => v >= MinStrikeSpeed;

    /// <summary>Ticks to the next strike on the same rotor: a blade pass (2 pi / (Blades omega)), at least GapTicksMin.</summary>
    public static int GapTicks(float rpm, float R, float dt)
    {
        float w = Omega(rpm, R);
        if (!(w > 1e-3f) || !(dt > 0f)) return 60;
        float pass = 2f * MathF.PI / (Blades * w);
        return Math.Clamp((int)MathF.Ceiling(pass / dt), GapTicksMin, 60);
    }

    /// <summary>The blades' wear (share of their health) one strike at v on this material costs.</summary>
    public static float BladeWear(Material m, float v) => IsStrike(v) ? StrikeWearFull * Hardness(m) * Severity(v) : 0f;
    /// <summary>The blades' health after a wear (0..1).</summary>
    public static float AfterWear(float health, float wear) => Math.Clamp(health - MathF.Max(wear, 0f), 0f, 1f);
    /// <summary>Broken: no health left.</summary>
    public static bool IsBroken(float health) => health <= 0f;

    /// <summary>One blade's mass (kg) for tip radius R.</summary>
    public static float BladeMass(float R) => BladeMassPerR2 * R * R;
    /// <summary>The whole rotor's moment of inertia about its axis (kg m^2): Blades uniform blades from hub to tip.</summary>
    public static float RotorInertia(float R) => Blades * BladeMass(R) * R * R / 3f;
    /// <summary>The blade's mass seen at radius r (kg): I_blade / r^2, at most the blade's own.</summary>
    public static float EffectiveMass(float r, float R)
    {
        float m = BladeMass(R);
        if (!(r > 0f)) return m;
        return MathF.Min(m, m * R * R / (3f * r * r));
    }

    /// <summary>The impulse (N s) a strike puts on the ship at the strike point, against the blade's motion (not a character's:
    /// CharacterKnock).</summary>
    public static float StrikeImpulse(Material m, float r, float R, float v) => IsStrike(v) ? EffectiveMass(r, R) * v * SpeedLoss(m) : 0f;

    /// <summary>A character's knock-back (N s, along the blade's motion; the ship takes it back): the blade's impulse on a
    /// character, at most KnockMaxSpeed of its mass.</summary>
    public static float CharacterKnock(float r, float R, float v, float characterMass)
    {
        if (!IsStrike(v)) return 0f;
        float mc = characterMass > 1f ? characterMass : CharacterMass;
        return MathF.Min(EffectiveMass(r, R) * v * SpeedLoss(Material.Character), mc * KnockMaxSpeed);
    }

    /// <summary>A character's damage (health points) from one strike at v, of its full health maxHealth.</summary>
    public static float CharacterDamage(float v, float maxHealth)
    {
        if (!IsStrike(v) || !(maxHealth > 0f)) return 0f;
        float x = v / LethalSpeed;
        return maxHealth * MathF.Min(x * x, 2f);
    }

    /// <summary>A struck block's health share (of its full health) from one strike.</summary>
    public static float BlockDamageShare(float r, float R, float v)
    {
        if (!IsStrike(v)) return 0f;
        float mass = Math.Clamp(EffectiveMass(r, R) / BlockRefMass, 0f, BlockMassCap);
        return BlockShareFull * Severity(v) * mass;
    }

    /// <summary>The rotor's angular momentum (N m s) at this rpm - dumped into the ship when the blades break.</summary>
    public static float BreakAngularMomentum(float rpm, float R) => RotorInertia(R) * Omega(rpm, R);

    /// <summary>The rotor block's health share a wear takes (the blades are BladeShareOfBlock of it; at least MinBlockWear).</summary>
    public static float RotorBlockWear(float wear) => wear > 0f ? MathF.Max(wear * BladeShareOfBlock, MinBlockWear) : 0f;

    /// <summary>Welded back up: worn blades on a block at full integrity are whole again. The new health.</summary>
    public static float Repaired(float health, float integrity) => health < 1f && integrity >= RepairIntegrity ? 1f : health;

    // ═══ PER-BLADE BREAKS (2026-10-23, the user: "break the blade tips / scatter them, keep a broken hub turning"; "it should
    //     update collision and flight characteristics") - helicopter main rotors and the Reversible Rotor (PerBlade); the
    //     turboprops keep the one health above (their blades are one mesh) ═══

    /// <summary>A blade's state: whole, its tip gone (chunk B flew off), or broken to the stub (chunk A and B gone).</summary>
    public enum Cut : byte { Whole = 0, TipLost = 1, Stub = 2 }
    /// <summary>The kinds whose blades break one by one (the blade-break content: a TipLost and a Stub model per blade).</summary>
    public static bool PerBlade(AeroPropulsorComponent.Kinds k) => k == AeroPropulsorComponent.Kinds.Rotor || k == AeroPropulsorComponent.Kinds.TailRotor;
    /// <summary>One strike's wear on the ONE blade that took it (the rotor's BladeWear x Blades: the old law spread a strike over
    /// all three) at or above which that blade snaps at once (full-speed rock 1.02: snaps; metal snaps from ~0.9 of tip speed;
    /// a character, 0.45 at most, never does); smaller strikes chip it, and a blade worn to 1 snaps on the strike that does it.</summary>
    public static float SnapWear = 0.5f;
    /// <summary>The one blade's wear from a strike (share of its life).</summary>
    public static float BladeWearOne(Material m, float v) => BladeWear(m, v) * Blades;

    /// <summary>A blade's reach (m from the axis): its tip, the split radius once the tip is gone, the stub's end once broken.</summary>
    public static float Reach(Cut c, float R, float splitR, float stubR) => c == Cut.Whole ? R : c == Cut.TipLost ? MathF.Min(splitR, R) : MathF.Min(stubR, R);

    /// <summary>A strike of wear w at radius r on a blade: chips it, or snaps it there - outboard of the split radius a whole blade
    /// loses its tip, anywhere else (or a blade already without its tip) it breaks to the stub. The wear restarts on the piece left.
    /// Returns the blade's new state.</summary>
    public static Cut AfterStrike(Cut c, ref float wear, float r, float w, float splitR)
    {
        if (c == Cut.Stub || !(w > 0f)) return c;
        wear = MathF.Min(wear + w, 1f);
        if (w < SnapWear && wear < 1f) return c;
        wear = 0f;
        return c == Cut.Whole && r >= splitR ? Cut.TipLost : Cut.Stub;
    }

    /// <summary>A blade's share of a whole blade's lift: the lift of a blade element grows as r^2 (its speed squared), so a blade
    /// from the hinge h out to its reach e lifts (e^3 - h^3) / (R^3 - h^3) of a whole one. A stub lifts nothing.</summary>
    public static float LiftShare(Cut c, float R, float splitR, float stubR, float hinge)
    {
        if (c == Cut.Stub || !(R > hinge)) return c == Cut.Whole ? 1f : 0f;
        float e = Reach(c, R, splitR, stubR), h3 = hinge * hinge * hinge, full = R * R * R - h3;
        return Math.Clamp((e * e * e - h3) / full, 0f, 1f);
    }

    /// <summary>The rotor's lift share: the mean of its blades' (its thrust, reaction torque and rating scale with it).</summary>
    public static float RotorLiftShare(ReadOnlySpan<byte> cuts, float R, float splitR, float stubR, float hinge)
    {
        if (cuts.Length == 0) return 1f;
        float s = 0f;
        for (int i = 0; i < cuts.Length; i++) s += LiftShare((Cut)cuts[i], R, splitR, stubR, hinge);
        return s / cuts.Length;
    }

    /// <summary>A worn blade lifts less (2026-10-23, the user: "track blade health separately and scale the force output by it"):
    /// at wear w (0 new .. 1 about to snap) it keeps 1 - WearLiftLoss x w of its length's lift - a chipped, bent blade.</summary>
    public static float WearLiftLoss = 0.35f;

    /// <summary>A blade's lift share with its wear: LiftShare x (1 - WearLiftLoss x wear).</summary>
    public static float BladeLift(Cut c, float wear, float R, float splitR, float stubR, float hinge)
        => LiftShare(c, R, splitR, stubR, hinge) * (1f - WearLiftLoss * Math.Clamp(wear, 0f, 1f));

    /// <summary>The rotor's lift share with every blade's wear: the mean of BladeLift.</summary>
    public static float RotorLiftShare(ReadOnlySpan<byte> cuts, ReadOnlySpan<float> wear, float R, float splitR, float stubR, float hinge)
    {
        if (cuts.Length == 0) return 1f;
        float s = 0f;
        for (int i = 0; i < cuts.Length; i++) s += BladeLift((Cut)cuts[i], i < wear.Length ? wear[i] : 0f, R, splitR, stubR, hinge);
        return s / cuts.Length;
    }

    /// <summary>Where a blade's lift acts (m from the axis): the centroid of an r^2 load from the hinge h to its reach e,
    /// 3/4 (e^4 - h^4) / (e^3 - h^3). A stub: its hinge (no lift to place).</summary>
    public static float LiftCentroid(Cut c, float R, float splitR, float stubR, float hinge)
    {
        float e = Reach(c, R, splitR, stubR);
        if (c == Cut.Stub || !(e > hinge)) return hinge;
        float e3 = e * e * e, h3 = hinge * hinge * hinge;
        return 0.75f * (e3 * e - h3 * hinge) / (e3 - h3);
    }

    /// <summary>The UNEVEN LIFT (2026-10-23, the user: "add wobble if blades are unbalanced"): each blade's thrust less the mean,
    /// T x (lift_k / sum - 1 / n), along the thrust axis at its lift centroid. Those forces sum to zero - a pure moment that turns
    /// once a revolution with the blades. Into excess[k] (N, + along the thrust); their moment's size is capped at
    /// LiftImbalanceMaxShare of thrust x radius. All zero when every blade lifts alike.</summary>
    public static void LiftImbalance(ReadOnlySpan<float> lift, ReadOnlySpan<float> centroid, float thrust, float R, Span<float> excess)
    {
        int n = lift.Length; float sum = 0f;
        for (int k = 0; k < n; k++) sum += MathF.Max(lift[k], 0f);
        if (!(sum > 0f) || !(thrust > 0f)) { for (int k = 0; k < n; k++) excess[k] = 0f; return; }
        float mx = 0f, my = 0f;
        for (int k = 0; k < n; k++)
        {
            excess[k] = thrust * (MathF.Max(lift[k], 0f) / sum - 1f / n);
            float a = 2f * MathF.PI * k / n;
            mx += excess[k] * centroid[k] * MathF.Cos(a); my += excess[k] * centroid[k] * MathF.Sin(a);
        }
        float m = MathF.Sqrt(mx * mx + my * my), cap = LiftImbalanceMaxShare * thrust * MathF.Max(R, 0f);
        if (m > cap && m > 0f) for (int k = 0; k < n; k++) excess[k] *= cap / m;
    }
    /// <summary>The most the uneven lift tilts the ship, as a share of thrust x rotor radius.</summary>
    public static float LiftImbalanceMaxShare = 0.25f;

    /// <summary>The visible WOBBLE of an unbalanced rotor (rad of disc tilt): the lost pieces' first moment, as a share of a whole
    /// blade set's (n x the whole blade's), x the rpm^2 (the centrifugal pull), at most WobbleMaxDeg. Into (x, y) in the disc's
    /// plane, turning with psi (blade k at psi + 2 pi k / n).</summary>
    public static Vector2 Wobble(ReadOnlySpan<float> lostMoment, float bladeMoment, float psi, float rpm)
    {
        if (!(bladeMoment > 0f)) return Vector2.Zero;
        float x = 0f, y = 0f;
        for (int k = 0; k < lostMoment.Length; k++)
        {
            float a = psi + 2f * MathF.PI * k / lostMoment.Length;
            x += lostMoment[k] * MathF.Cos(a); y += lostMoment[k] * MathF.Sin(a);
        }
        float r = Math.Clamp(rpm, 0f, 1f), s = WobbleMaxDeg * MathF.PI / 180f * r * r / bladeMoment;
        var v = new Vector2(-x, -y) * s;
        float l = v.Length(), cap = WobbleMaxDeg * MathF.PI / 180f;
        return l > cap && l > 0f ? v * (cap / l) : v;
    }
    /// <summary>The visible wobble's most (deg).</summary>
    public static float WobbleMaxDeg = 3f;

    /// <summary>The mass a blade has lost (kg) and its first moment about the axis (kg m: mass x the lost pieces' centre radius).</summary>
    public static void Lost(Cut c, float massA, float comA, float massB, float comB, out float mass, out float moment)
    {
        mass = moment = 0f;
        if (c == Cut.Whole) return;
        mass += massB; moment += massB * comB;
        if (c == Cut.Stub) { mass += massA; moment += massA * comA; }
    }

    /// <summary>The rotor's IMBALANCE (N, in the disc's (u, w) plane): what the lost pieces no longer pull - the opposite of their
    /// centripetal pull - omega^2 x the sum of each blade's lost first moment along its direction (blade k at psi + 2 pi k / 3),
    /// at most ImbalanceMaxShare of the rotor's rating. Zero when every blade is alike (whole, or all three broken the same way).</summary>
    public static Vector2 Imbalance(ReadOnlySpan<float> lostMoment, float psi, float omega, float rated)
    {
        float x = 0f, y = 0f;
        for (int k = 0; k < lostMoment.Length; k++)
        {
            float a = psi + 2f * MathF.PI * k / lostMoment.Length;
            x += lostMoment[k] * MathF.Cos(a); y += lostMoment[k] * MathF.Sin(a);
        }
        // (the whole rotor pulled the hub nowhere; without those pieces the rest pulls it toward the side opposite them)
        var f = new Vector2(-x, -y) * (omega * omega);
        float cap = ImbalanceMaxShare * MathF.Max(rated, 0f), l = f.Length();
        return l > cap && l > 0f ? f * (cap / l) : f;
    }
    /// <summary>The most the imbalance shakes the ship, as a share of the rotor's rated thrust.</summary>
    public static float ImbalanceMaxShare = 0.5f;

    /// <summary>The three blades' states packed in one int (c0 + 3 c1 + 9 c2: 0..26) for the client copy's record.</summary>
    public static int Pack(ReadOnlySpan<byte> cuts) { int v = 0, m = 1; for (int i = 0; i < cuts.Length && i < Blades; i++) { v += Math.Min((int)cuts[i], 2) * m; m *= 3; } return v; }
    public static Cut Unpack(int packed, int blade) { for (int i = 0; i < blade; i++) packed /= 3; return (Cut)Math.Clamp(packed % 3, 0, 2); }

    /// <summary>The blade's direction of motion at a point d from the hub (in the disc's plane), for spin axis s (unit): s x d, unit.</summary>
    public static Vector3 BladeMotion(Vector3 spinAxis, Vector3 fromHub)
    {
        var t = Vector3.Cross(spinAxis, fromHub);
        float l = t.Length();
        return l > 1e-6f ? t / l : Vector3.Zero;
    }

    /// <summary>A point's radius in the disc (its distance from the axis through the hub).</summary>
    public static float RadiusOf(Vector3 fromHub, Vector3 axis)
    {
        var inPlane = fromHub - axis * Vector3.Dot(fromHub, axis);
        return inPlane.Length();
    }
}
