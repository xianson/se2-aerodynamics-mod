#pragma warning disable
using System;
using System.Collections.Generic;

namespace AeroMod;

/// <summary>
/// SOLID ROCKET MOTORS (2026-10-17) - the game-free core (the game part: AeroSolidNozzleComponent / AeroSolidSegmentComponent;
/// compiled alone into Tests/SolidRocketTests). A motor is a NOZZLE and the continuous chain of SEGMENTS of its size stacked
/// straight on its top face (FindStack). Its numbers are the generated table's (SolidRocketBlocks.g.cs, gen_propulsor_defs.py):
///   thrust     T(p) = T_sea x (1 + VacuumBoost x (1 - p)), p = the air density over sea level's (0..1) - flat with speed,
///              ~10 % stronger in vacuum (ThrustAt)
///   propellant (the CONVEYOR version, 2026-10-17, the user: "do the conveyor version") a REAL resource of the game: "Solid
///              Propellant" (its own ResourceTypeDefinition, carried by conveyors, litres = kg). Every segment is a TANK of it
///              (ResourceContainerComponent: full when built - its server prefab's charge level 1 -, never refilled - charging rate
///              0 -, drained in creative too, saved by the game) with a conveyor port on its top and bottom face centres; the
///              nozzle has one port on its top face and draws the propellant like a hydrogen thruster draws hydrogen
///              (ConsumedResource SolidPropellant, ResourcesRequiredToThrust = GameRate). The ports are at a scale no vanilla port
///              has, one per motor size (SolidRocketBlocks.Row.PortScale), so the game links a solid block only to the solid
///              blocks of its size face to face (PortsLink: the game's own rule) - never to the vanilla conveyor network. One full
///              segment = T_sea x BurnPerSegment / (Isp g0) kg (the same total impulse whatever the stack).
///   the stack  (the user, 2026-10-17: "3" - more segments give some more thrust AND some more burn) every segment with propellant
///              burns at once (the game draws from all the tanks of a conveyor group together, in proportion - the grain's bore
///              surface adds up): n live tanks give StackFactor(n) = sqrt(n) x the thrust and burn sqrt(n) x faster (MassFlow x the
///              factor) - n full segments burn BurnPerSegment x sqrt(n) s at sqrt(n) x the thrust (4: 2x for 16 s; 9: 3x for 24 s).
///              The throat caps the factor at MaxStackFactor (3: 9 segments); a taller stack burns longer at 3x. The nozzle's
///              ThrustPower is its vacuum thrust x MaxStackFactor. The STACK is FindStack's chain: the ports sit on the face
///              centres, so two solid blocks link only co-axial, touching, the same size - the conveyor group of a nozzle IS that
///              chain (it cannot branch: a segment has two ports, each meets at most one other); a segment that is not working
///              (unbuilt, damaged) breaks the chain there for the game, and so for FindStack (IPiece.Feeds). A chain with a nozzle
///              at each end (OtherEnd) is ONE grain: the two LIT nozzles share its factor (ShareFactor: sqrt(n) / 2 each) - the
///              same gas through two throats; one lit nozzle takes it all.
///   the burn   the game charges ResourcesRequiredToThrust whole on every tick the nozzle fires (all or nothing); the mod lets it
///              burn on BurnDuty of the ticks (DutyCycle over FuelWindow ticks, as the ramjets' hydrogen: RamjetFuelBridge) - the
///              stack's factor x the share of full it thrusts now / MaxStackFactor - so the propellant used follows the thrust
///              exactly (level and steering). The game cuts the thrust itself when the tanks can not give one tick of GameRate
///              (its sink is all-or-nothing); the motor ends there too (Usable).
///   ignition   Step: once lit it burns until the stack is empty - no throttle, no shutdown. The game's thrust does the work: the
///              nozzle is switched OFF until lit (the game's movement, dampeners and every mod controller then never fire it), and
///              lit it is switched on and forced to its thrust with a thrust OVERRIDE (OverrideFor: the share of its definition's
///              ThrustPower - its vacuum thrust - that gives T(p)). The order of those writes keeps the game's books right (its
///              GridMovementCollectorComponent takes an overridden thruster's rating out of the grid's controllable maximum when
///              the override is ADDED and puts it back when it is REMOVED, from the rating it booked): switch on, THEN override.
///              At the end (2026-10-17: no more override-1 tick - up to 3x the vacuum thrust for 1/60 s) the override is removed
///              AND the switch turned off in ONE tick: the switch-off disables the block at once (Enabled -> Working -> DisabledTag,
///              one signal chain), so the game's OnThrustOverrideRemoved sees it disabled - it takes the overridden thrust out of
///              its books and puts nothing back into the controllable maximum; its OnThrustersChanged (stop working) then has
///              nothing booked to move. (Had the tag come a frame later, the removal would book the rating back and the stop
///              take it out again - balanced too.) When the game cut the supply first (spent), its stop-working runs
///              OnThrustOverrideChanged before OnThrustersChanged (the job order), which balances as well.
///   power      (the user, 2026-10-17: "once a solid is on it's on. It must be at least 50% power, which you can set in thrust
///              override prior. It does not respond to rotational control unless it's already on, in which case it reacts to
///              rotational demands") the player's thrust override slider on the idle nozzle is its POWER LEVEL (LevelFrom: unset 1,
///              else clamped to MinLevel..1), latched at ignition (Motor.Level, saved with the burn): its BASE thrust = level x
///              the stack's full thrust. While idle the slider is left as the player set it (Want.Keep - the switched-off thruster
///              books nothing from it); at ignition it is removed BEFORE the switch goes on (Clearing: an override already on a
///              thruster being switched on is booked into the game's controllable maximum - GridMovementCollectorComponent.
///              OnThrusterChanged - and its override thrust cancelled), then switch on, then the motor's own override, as before.
///              Lit, the attitude allocator may move its thrust within MinLevel..1 of full for ROTATION (ThrustTorque: the nozzle
///              a bounded slot of ThrustAllocator.Solve - rev + lo - preferring its base); the share it solved is written back
///              through the nozzle's override (the game's thrust). The burn follows the thrust (BurnDuty / BurnRate: mass flow x
///              the share of full), so the total impulse is the same at any level: half power burns twice as long.
/// </summary>
public static class SolidRocket
{
    /// <summary>The air density at sea level on the GAME's scale (p = density / this): a vanilla planet's atmosphere generator
    /// has Density 1 (GameData Vanilla PlanetAtmosphereGenerator.def), and AirData.Density - which the mod's
    /// AeroGridComponent.LastDensity is, plus its thin upper tail - is that x the generators' falloff. (2026-10-17: it was
    /// 1.225, the ISA's kg/m^3, while the no-aero path took the game's density as p directly: the two disagreed by 1.225 and
    /// p fell from 0.85 to 0.69 whenever a grid's force model switched on - 1.5 % more thrust; and sea level never reached
    /// p = 1. The same reference as the propulsors' PropulsorKinds.RhoRef.)</summary>
    public const float SeaLevelDensity = 1f;

    /// <summary>The motor's thrust (N) at this pressure ratio (0 vacuum .. 1 sea level and denser; NaN: sea level).</summary>
    public static float ThrustAt(float seaLevelThrust, float pressureRatio)
    {
        float p = float.IsFinite(pressureRatio) ? Math.Clamp(pressureRatio, 0f, 1f) : 1f;
        return seaLevelThrust * (1f + SolidRocketBlocks.VacuumBoost * (1f - p));
    }

    /// <summary>The pressure ratio of an air density (kg/m^3; the mod's AeroGridComponent.LastDensity).</summary>
    public static float PressureRatio(float density) => float.IsFinite(density) ? Math.Clamp(density / SeaLevelDensity, 0f, 1f) : 1f;

    /// <summary>A nozzle's pressure ratio from its air (AeroSolidNozzleComponent.ReadAir): its grid's own air density when aero
    /// has read it (aeroAir: AeroGridComponent.AirKnown, aero on - LastDensity, the game's plus the upper tail), else the game's
    /// (AirData.Density). ONE source per grid and ONE scale - never a choice on whether the force model ran this step (its
    /// speed), so p does not move while the air does not. A NaN game density is vacuum (0).</summary>
    public static float PressureRatioOf(bool aeroAir, float aeroDensity, float gameDensity)
        => PressureRatio(aeroAir ? aeroDensity : (float.IsFinite(gameDensity) ? gameDensity : 0f));

    /// <summary>The throat's cap on the stack's thrust factor (9 segments: 3x; a taller stack burns longer at 3x).</summary>
    public const float MaxStackFactor = 3f;

    /// <summary>The thrust (and burn-rate) factor of n segments burning together: sqrt(n), capped at MaxStackFactor; 0 for none.</summary>
    public static float StackFactor(int burning) => burning <= 0 ? 0f : MathF.Min(MathF.Sqrt(burning), MaxStackFactor);

    /// <summary>The burn's mass flow (kg/s) for ONE segment's worth (x StackFactor for the stack): flat - the air changes the
    /// thrust, never the burn time.</summary>
    public static float MassFlow(float seaLevelThrust) => seaLevelThrust / (SolidRocketBlocks.Isp * SolidRocketBlocks.G0);

    /// <summary>One full segment's propellant (kg): its BurnPerSegment seconds of MassFlow.</summary>
    public static float FullSegment(float seaLevelThrust) => MassFlow(seaLevelThrust) * SolidRocketBlocks.BurnPerSegment;

    /// <summary>The thrust override (0..1) that makes a thruster of this ThrustPower give 'want' N where its thrust class gives
    /// 'efficiency' of it (the game's overridden thrust = rating x override x class efficiency).</summary>
    public static float OverrideFor(float want, float thrustPower, float efficiency)
    {
        if (!(thrustPower > 0f) || !(want > 0f)) return 0f;
        float o = want / (thrustPower * MathF.Max(float.IsFinite(efficiency) ? efficiency : 1f, 0.05f));
        return float.IsFinite(o) ? Math.Clamp(o, 0f, 1f) : 0f;
    }

    // ── POWER LEVEL and the burn ──

    /// <summary>The least power level a lit motor runs at (share of its stack's full thrust): the player's floor, and the
    /// attitude allocator's lower bound.</summary>
    public const float MinLevel = 0.5f;

    /// <summary>The power level the player's thrust override slider (0..1) sets on an idle nozzle: unset (0, NaN) = 1, else
    /// clamped to MinLevel..1.</summary>
    public static float LevelFrom(float playerOverride)
        => float.IsFinite(playerOverride) && playerOverride > 0f ? Math.Clamp(playerOverride, MinLevel, 1f) : 1f;

    /// <summary>A latched level (Motor.Level; 0: not latched - a motor saved before levels existed) as the one it burns at.</summary>
    public static float LevelOr1(float level) => float.IsFinite(level) && level > 0f ? Math.Clamp(level, MinLevel, 1f) : 1f;

    /// <summary>The stack's full thrust (N) now: ThrustAt(the row's sea-level thrust x StackFactor, p), never above what its
    /// override can give (ThrustPower x the class efficiency).</summary>
    public static float FullThrust(float seaLevelThrust, float stackFactor, float pressureRatio, float thrustPower, float efficiency)
    {
        float t = ThrustAt(seaLevelThrust * MathF.Max(stackFactor, 0f), pressureRatio);
        float most = thrustPower * (float.IsFinite(efficiency) ? MathF.Max(efficiency, 0f) : 1f);
        return float.IsFinite(t) ? MathF.Max(0f, MathF.Min(t, most)) : 0f;
    }

    /// <summary>The burn's mass flow (kg/s) at this share of the stack's full thrust (MinLevel..1): MassFlow x StackFactor x the
    /// share - the propellant follows the thrust, so the total impulse is the same at any power level.</summary>
    public static float BurnRate(float seaLevelThrust, float stackFactor, float fraction)
        => MassFlow(seaLevelThrust) * MathF.Max(stackFactor, 0f) * (float.IsFinite(fraction) ? Math.Clamp(fraction, MinLevel, 1f) : 1f);

    // ── the GAME's burn (the conveyor version) ──

    /// <summary>The nozzle definition's ResourcesRequiredToThrust (L/s = kg/s): what the game charges on every tick it fires - one
    /// segment's MassFlow x MaxStackFactor (a full-throat stack at full power: BurnDuty 1).</summary>
    public static float GameRate(float seaLevelThrust) => MassFlow(seaLevelThrust) * MaxStackFactor;

    /// <summary>The lit nozzles that can draw on one stack at once (a chain has two ends: OtherEnd).</summary>
    public const int MaxSharers = 2;

    /// <summary>A segment tank's MaxDischargeRate (L/s): GameRate x MaxSharers - a lone tank under two nozzles firing in the same
    /// tick still gives both (the game's sink is all-or-nothing: a tank short of it would cut a nozzle's thrust for the tick).</summary>
    public static float TankDischargeRate(float seaLevelThrust) => GameRate(seaLevelThrust) * MaxSharers;

    /// <summary>The thrust (and burn) factor ONE lit nozzle gets from its stack: StackFactor(live tanks) / the lit nozzles on the stack
    /// (1 or 2 - the same gas through two throats).</summary>
    public static float ShareFactor(int live, int litSharers) => StackFactor(live) / Math.Clamp(litSharers, 1, MaxSharers);

    /// <summary>The share of the game's burn ticks (0..1) that burns BurnRate(factor, fraction): factor x the share of full (MinLevel..1;
    /// NaN 1) / MaxStackFactor - the propellant follows the thrust exactly (DutyCycle averages it over its window).</summary>
    public static float BurnDuty(float factor, float fraction)
    {
        float f = (float.IsFinite(factor) ? MathF.Max(factor, 0f) : 0f) * (float.IsFinite(fraction) ? Math.Clamp(fraction, MinLevel, 1f) : 1f) / MaxStackFactor;
        return Math.Clamp(f, 0f, 1f);
    }

    /// <summary>The window (ticks) of the burn's duty cycle (DutyCycle.Window - the ramjets' too: at most two switches per window).</summary>
    public const int FuelWindow = DutyCycle.Window;

    /// <summary>The propellant the motor can still burn (kg): the stack's total, or 0 once it is below one tick of GameRate - the game's
    /// all-or-nothing sink can not be fed from less (its thrust is cut there; the crumb stays in the tanks).</summary>
    public static float Usable(float total, float seaLevelThrust, float dt)
    {
        if (!(total > 0f)) return 0f;
        float tick = GameRate(seaLevelThrust) * (float.IsFinite(dt) && dt > 0f ? dt : 0f);
        return total > tick * 1.001f ? total : 0f;
    }

    /// <summary>A lit nozzle as a bounded slot of the attitude allocator (N): lo = MinLevel x full, hi = full, its base (the
    /// thrust it holds when no torque is asked) = level x full.</summary>
    public static void SteerBounds(float full, float level, out float lo, out float hi, out float baseThrust)
    {
        hi = float.IsFinite(full) ? MathF.Max(full, 0f) : 0f;
        lo = MinLevel * hi;
        baseThrust = LevelOr1(level) * hi;
    }

    /// <summary>The share of full (MinLevel..1) a solved slot force f gives - what the nozzle writes through its override
    /// (NaN / no full: the latched level).</summary>
    public static float FractionOf(float f, float full, float level)
        => float.IsFinite(f) && full > 0f ? Math.Clamp(f / full, MinLevel, 1f) : LevelOr1(level);

    /// <summary>The torque reach a lit nozzle adds about its base (N m per local axis): arm = its torque per newton, lo..hi its
    /// bounds, b its base. Summed over the nozzles into plus / minus (the most torque each way), the reach is Vector3.Min(plus,
    /// minus) - a nozzle at full power can only throttle down, so alone it turns the ship one way only; two opposite ones turn
    /// it both ways.</summary>
    public static void AddSteerReach(Vector3 arm, float lo, float hi, float b, ref Vector3 plus, ref Vector3 minus)
    {
        Vector3 up = arm * (hi - b), down = arm * (lo - b);
        plus += Vector3.Max(Vector3.Max(up, down), Vector3.Zero);
        minus += Vector3.Max(Vector3.Max(-up, -down), Vector3.Zero);
    }

    // ── the STACK ──

    /// <summary>A block's place for the stack: its cell box in its grid (0.25 m cells, inclusive), the grid axis of its Up (0 X,
    /// 1 Y, 2 Z) and that Up's sign there, its size label, and whether it is known yet.</summary>
    public struct Geo
    {
        public Vector3I Min, Max; public int Axis, Sign; public string Size; public bool Valid;
        public static Geo Of(Vector3I min, Vector3I max, Vector3 up, string size)
        {
            var g = new Geo { Min = min, Max = max, Size = size };
            float ax = MathF.Abs(up.X), ay = MathF.Abs(up.Y), az = MathF.Abs(up.Z);
            if (ax > 0.5f) { g.Axis = 0; g.Sign = up.X > 0f ? 1 : -1; }
            else if (ay > 0.5f) { g.Axis = 1; g.Sign = up.Y > 0f ? 1 : -1; }
            else if (az > 0.5f) { g.Axis = 2; g.Sign = up.Z > 0f ? 1 : -1; }
            else return g;   // (no axis: not valid)
            g.Valid = size != null;
            return g;
        }
        public bool Same(in Geo o) => Valid == o.Valid && Min == o.Min && Max == o.Max && Axis == o.Axis && Sign == o.Sign && Size == o.Size;
    }

    /// <summary>A segment for FindStack (the game's AeroSolidSegmentComponent; the tests' fakes): its place, its tank's propellant (kg -
    /// the game's charge, read only: the game drains it), a full tank (kg) and whether it feeds the chain (working: the game drops a
    /// non-working block's tank and conveyor link).</summary>
    public interface IPiece
    {
        Geo Geo { get; }
        /// <summary>Its propellant left (kg = L: the tank's charge).</summary>
        float Propellant { get; }
        /// <summary>A full tank (kg).</summary>
        float Full { get; }
        /// <summary>It links the chain on (working): false - the chain stops before it.</summary>
        bool Feeds { get; }
    }

    static int C(Vector3I v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

    /// <summary>The nozzle's motor: the segments of ITS size, along ITS Up axis (either way up - a segment is the same both ways),
    /// with ITS footprint (the same box across the axis: co-axial, the same cross-section), each touching the last one's top face
    /// (the nozzle's first) - in 'stack', nearest first. Candidates are one grid's segments (the caller's). Stops at the first gap,
    /// wrong size, turned, offset or non-working segment - exactly where the game's conveyor link stops (the ports are on the face
    /// centres at the size's own scale: PortsLink). Returns the count. No allocation (List enumerators; stack cleared and refilled).</summary>
    public static int FindStack<T>(in Geo nozzle, List<T> candidates, List<T> stack) where T : class, IPiece
    {
        stack.Clear();
        if (!nozzle.Valid || candidates == null) return 0;
        int a = nozzle.Axis, s = nozzle.Sign, b = (a + 1) % 3, c = (a + 2) % 3;
        Vector3I lo = nozzle.Min, hi = nozzle.Max;
        for (int guard = candidates.Count; guard >= 0; guard--)
        {
            T next = null;
            foreach (var seg in candidates)
            {
                var g = seg.Geo;
                if (!g.Valid || g.Axis != a || !string.Equals(g.Size, nozzle.Size, StringComparison.Ordinal)) continue;
                if (C(g.Min, b) != C(lo, b) || C(g.Max, b) != C(hi, b) || C(g.Min, c) != C(lo, c) || C(g.Max, c) != C(hi, c)) continue;
                bool touches = s > 0 ? C(g.Min, a) == C(hi, a) + 1 : C(g.Max, a) + 1 == C(lo, a);
                if (!touches || stack.Contains(seg)) continue;
                next = seg; break;
            }
            if (next == null || !next.Feeds) break;
            stack.Add(next);
            var ng = next.Geo; lo = ng.Min; hi = ng.Max;
        }
        return stack.Count;
    }

    /// <summary>A nozzle at the FAR end of this one's stack (its last segment 'last'), facing back along it - the same size and
    /// footprint, its top face on the last segment's far face: the chain between them is one grain (ShareFactor).</summary>
    public static bool OtherEnd(in Geo nozzle, in Geo last, in Geo other)
    {
        if (!nozzle.Valid || !last.Valid || !other.Valid || other.Axis != nozzle.Axis || other.Sign != -nozzle.Sign) return false;
        if (!string.Equals(other.Size, nozzle.Size, StringComparison.Ordinal)) return false;
        int a = nozzle.Axis, b = (a + 1) % 3, c = (a + 2) % 3;
        if (C(other.Min, b) != C(nozzle.Min, b) || C(other.Max, b) != C(nozzle.Max, b) || C(other.Min, c) != C(nozzle.Min, c) || C(other.Max, c) != C(nozzle.Max, c)) return false;
        return nozzle.Sign > 0 ? C(other.Min, a) == C(last.Max, a) + 1 : C(other.Max, a) + 1 == C(last.Min, a);
    }

    /// <summary>The stack's propellant left (kg).</summary>
    public static float Total<T>(List<T> stack) where T : class, IPiece
    {
        float sum = 0f;
        for (int i = 0; i < stack.Count; i++) sum += MathF.Max(stack[i].Propellant, 0f);
        return sum;
    }

    /// <summary>The segments still holding propellant (the ones burning - the game draws from every tank with any charge, so any counts:
    /// a threshold here could leave a stack Usable with no live tank - factor 0, lit forever at no thrust).</summary>
    public static int Live<T>(List<T> stack) where T : class, IPiece
    {
        int n = 0;
        for (int i = 0; i < stack.Count; i++) if (stack[i].Propellant > 0f) n++;
        return n;
    }

    // ── the PORTS (the game's link rule: ConveyorElementTranslator.GetConveyorPath, decompiled 2.4) ──

    /// <summary>Two pipeable port dummies (grid space, m) link face to face when they are within PortReach of each other and their
    /// scales match across the face (X and Y within PortScaleTolerance; Z - the depth - ignored). The dummy type need only be a
    /// pipeable one (never compared); no size class, mount point or facing check. So a port scale no vanilla block uses never links
    /// to a vanilla conveyor.</summary>
    public const float PortReach = 0.0625f, PortScaleTolerance = 1e-4f;
    public static bool PortsLink(Vector3 posA, Vector3 scaleA, Vector3 posB, Vector3 scaleB)
        => Vector3.DistanceSquared(posA, posB) <= PortReach * PortReach
           && MathF.Abs(scaleA.X - scaleB.X) <= PortScaleTolerance && MathF.Abs(scaleA.Y - scaleB.Y) <= PortScaleTolerance;

    // ── IGNITION ──

    /// <summary>(2026-10-17: EndFull / EndRemove retired - the burnout hands the block back in one tick.)</summary>
    public enum Phase : byte { Idle, Enabling, Lit, Clearing }

    /// <summary>A nozzle's ignition state (Request: Ignite was asked since its last step; Level: the power level latched at
    /// ignition, MinLevel..1 - 0 until then).</summary>
    public struct Motor { public Phase Phase; public bool Request; public float Level; }

    /// <summary>What the nozzle's block should be this tick: switched on, and its thrust override (0: none). Keep: leave the
    /// override as the player set it (an idle nozzle: the slider is its power level).</summary>
    public struct Want { public bool Enabled; public float Override; public bool Keep; }

    /// <summary>Step with no player's override (level 1).</summary>
    public static Want Step(ref Motor m, float propellant, bool working, float target) => Step(ref m, propellant, working, target, 0f);

    /// <summary>
    /// One server tick ('propellant': what it can still burn - Usable): Idle - off, the player's override left alone (Keep: it is the
    /// power level); an Ignite with propellant on a working block latches the level (LevelFrom the player's override) and, when an
    /// override is set, removes it first (Clearing, still off), then switches it on (Enabling), and the next tick overrides it to
    /// 'target' (Lit). Lit - on, overridden to 'target' (whatever the player did to its switch or its override: put back), until the
    /// stack is empty; then, in ONE tick, the override removed and the switch off (Idle - see the class text: the game's books stay
    /// right, no spike). A Lit motor on a broken nozzle stays lit (the game gives it no thrust and draws no propellant while it is
    /// broken; repaired, it burns on) - a lit solid motor cannot be put out.
    /// </summary>
    public static Want Step(ref Motor m, float propellant, bool working, float target, float playerOverride)
    {
        bool ask = m.Request; m.Request = false;
        switch (m.Phase)
        {
            case Phase.Idle:
                if (ask && propellant > 0f && working)
                {
                    m.Level = LevelFrom(playerOverride);
                    if (float.IsFinite(playerOverride) && playerOverride > 0f) { m.Phase = Phase.Clearing; return new Want(); }   // (the player's override off first, still switched off)
                    m.Phase = Phase.Enabling; return new Want { Enabled = true };
                }
                return new Want { Keep = true };
            case Phase.Clearing:
                if (!working) { m.Phase = Phase.Idle; return new Want { Keep = true }; }   // (it broke before it lit: nothing burned)
                m.Phase = Phase.Enabling;
                return new Want { Enabled = true };
            case Phase.Enabling:
                if (!working) { m.Phase = Phase.Idle; return new Want { Keep = true }; }   // (it broke before it lit: nothing burned)
                m.Phase = Phase.Lit;
                return new Want { Enabled = true, Override = MathF.Max(target, 1e-3f) };
            default:   // Lit
                if (!(propellant > 0f)) { m.Phase = Phase.Idle; return new Want(); }   // (spent: override off AND switched off, one tick)
                return new Want { Enabled = true, Override = MathF.Max(target, 1e-3f) };
        }
    }

    /// <summary>Burning: its propellant is being drained this tick (Lit; Enabling has not lit yet).</summary>
    public static bool Burning(Phase p) => p == Phase.Lit;

    /// <summary>Saved as burning (the block's data): lit, or switched on to light - not Idle, not Clearing (still off with the
    /// player's override going: loaded, it is simply not lit).</summary>
    public static bool SavedLit(Phase p) => p == Phase.Enabling || p == Phase.Lit;
}
