#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// PROPULSOR blocks (2026-10-16, the user: turboprops, helicopter rotors, ducted fans - "wire it all"): our blocks ARE vanilla
/// atmospheric thrusters (ThrusterComponent, thrust class Atmospheric: the game's controls, dampeners, hover hold, power
/// draw and the propeller's spin - ThrusterAnimationOverrideComponent turns the subpart with the commanded thrust - all as
/// vanilla; tools/meshgen/gen_propulsor_defs.py). This component (injected into their server AND client prefabs:
/// AeroBlockGuids.Propulsor*Prefabs) only reshapes each one's maximum with the air it flies through, every few ticks, by
/// writing its ThrustData.MaxThrustPower (the game's own rule then shares it: GridMovementCollectorComponent
/// OnThrusterChanged / RecalculateMaxThrusts; ThrustTorque reads the same number - LiveMax):
///   max = T_static (the definition's ThrustPower) x Speed(kind, v along the thrust, v, Mach) x DensityFactor(rho)
///         / (the game's own Atmospheric class efficiency at its air density - so the density curve is ours, not doubled)
/// where v is the grid's TRUE AIRSPEED (its velocity less the wind: AeroGridComponent.LastAirVel) and Mach that over the
/// local speed of sound (AeroGridComponent.LastMach - the one source the vanilla atmospherics' Jet curve reads too; 2026-10-17:
/// it was the ground speed over a fixed 330 m/s), and Speed (PropulsorKinds.cs, game-free) is
///   turboprop  min(1, V0 / v): constant power past V0 (T = min(T_static, P / v), P = T_static x V0), x a tip-Mach fade
///   fan        1 - v / FanVZero (a fan's static thrust falls off faster), never below 0
///   rotor      ThrustProfiles.HelicopterRotor of the Mach (translational lift up to ~M 0.2, retreating-blade stall after)
///   ramjet     (2026-10-17; a vanilla HYDROGEN thruster underneath - the same write, its class's efficiency) RamjetLaw.Factor:
///              ThrustProfiles.Ramjet of the Mach x the density x its inlet (InletAngle.Step, InletAngle.Ramjet, one unstart state
///              per engine); and its HYDROGEN burn follows that share of its rating (FuelTick: the game's flat burn held off on
///              the ticks RamjetLaw.Burn leaves out - RamjetFuelBridge reaches the game's consumption buffer)
/// Zero in vacuum (rho 0). With aero off (AeroSwitch), this component off (Enabled) or no aero grid, the game's rating is
/// written back (as JetCurve does for the vanilla atmospherics): plain vanilla thrusters - and again when it leaves the scene. The game's own zero - unpowered, switched off, damaged - is respected: when the game wrote the
/// value (it differs from ours), a 0 is left alone until the game writes the rating back (power returned).
/// The rotor's cyclic and anti-torque are RotorCyclic (ThrustTorque's attitude law).
/// A ROTOR'S RPM (2026-10-16) is governed (RotorKinematics.StepRpm, server, every step): on and powered it spins up and holds
/// 100 %, off / unpowered / damaged it coasts down; its maximum is x rpm^2 (no lift until it is spun up - lift is the blades'
/// pitch, never the rpm). A rotor found on and powered when it is first seen (a world loaded, a ship pasted) starts at 100 %:
/// a loaded helicopter in flight keeps flying. Its client animation reads it (RotorCyclic.Publish).
/// A TURBOPROP's rpm is governed too (2026-10-17): idle 75 % on, up to 100 % with its load (PropulsorKinds.TargetRpm; its thrust
/// is not gated by it). A REVERSIBLE block's thrust either way is its ReversibleThrustComponent's (not a kind's). Who gets what: PropulsorKinds.
/// ROTOR STRIKES (2026-10-17): a rotor's or prop's blades can break on what its disc hits (AeroRotorStrikeComponent, RotorStrike.cs):
/// broken, its rpm is 0 and its maximum is written as 0 (aero on or off) until it is welded back up.
/// </summary>
public partial class AeroPropulsorComponent : Component, IInSceneListener
{
    // (Kinds and who gets which mechanic: PropulsorKinds.cs, game-free)

    // ── tuning: the ONE place for the curves (the static thrusts are the definitions' ThrustPower: gen_propulsor_defs.py) ──
    // (the curves and their constants: Speed, DensityFactor, RhoRef / RhoMax in PropulsorKinds.cs; ThrustProfiles.TurbopropFraction /
    //  TurbopropV0 / TurbopropMachFade / TurbopropMachZero, ThrustProfiles.FanFraction / FanVZero)
    /// <summary>Off: the blocks are plain vanilla atmospheric thrusters (A/B tests) - the ratings written back.</summary>
    public static bool Enabled = true;
    /// <summary>A write when the maximum moved more than this fraction (each write re-sums the grid's thrust).</summary>
    public const float WriteTolerance = 0.005f;

    public Kinds Kind;
    /// <summary>The block's size label (050 / 150 / 250 / 750) and the rotor's radius (m), from its block GUID.</summary>
    public string Size = "";
    public float RotorRadius;
    /// <summary>The spinning disc's centre above the block's centre along the thrust (m): the rotor hub (RotorCyclic).</summary>
    public float HubOffset;

    /// <summary>Now: the rating (the definition's ThrustPower as the game wrote it), the factor and the maximum written.</summary>
    public float Rated, Factor = 1f, Written = -1f, Airspeed, Density, GameDensity, Efficiency = 1f, Mach;
    bool _known, _gameZero;
    /// <summary>A RAMJET (RamjetLaw): its inlet's angle to the airflow (deg), its inlet factor now and its unstart state; its
    /// hydrogen burn's schedule, the game's consumption buffer it holds off (ThrusterComponent's, by reflection once:
    /// RamjetFuelBridge), the duty now, and the server ticks it fired / burned (since load: the readout's ratio).</summary>
    public float InletDeg, Inlet = 1f, FuelDutyNow = 1f;
    InletAngle.State _inlet = InletAngle.NewState();
    /// <summary>A ramjet's inlet is unstarted now (this copy's state - the client's drives the exhaust's pop: AeroExhaustComponent).</summary>
    internal bool InletUnstarted => _inlet.Unstarted;
    RamjetLaw.FuelState _fuel;
    Keen.Game2.Simulation.WorldObjects.CubeBlocks.SingleFrameBinaryResourceConsumptionBuffer _buf; bool _bufLooked;
    public long FireTicks, BurnTicks;
    /// <summary>Its ReversibleThrustComponent, if it carries one (read on its own first tick: the Summary reads only fields).</summary>
    ReversibleThrustComponent _rev;
    /// <summary>Its rotor-strike side (AeroRotorStrikeComponent: server copies of rotors, the Reversible Rotor and turboprops), and
    /// whether its blades were broken on the last tick (a change writes at once).</summary>
    AeroRotorStrikeComponent _strike; bool _wasBroken;
    /// <summary>A main rotor's turn (2026-10-23): counter-clockwise seen from above - its reaction the other way (RotorCyclic,
    /// RotorLimiter), its strikes' blade motion (AeroRotorStrikeComponent). Server: its AeroRotorSpinComponent's switch, every tick;
    /// client: the server's published record (AeroRotorAnimComponent sets it).</summary>
    public bool CounterRotating;
    AeroRotorSpinComponent _spinSwitch;
    /// <summary>A governed propulsor (PropulsorKinds.Governed): its rpm (0..1, server), whether it is driven (on, powered,
    /// working), and its load last step (its thrust / its maximum now, 0..1: RotorCyclic.Publish, server) - a turboprop's rpm
    /// rises with it from idle.</summary>
    public float Rpm, Load; public bool SpinOn; bool _rpmInit; int _rpmWait;
    /// <summary>A main rotor's CONTROLLABILITY CAP (RotorCyclic.Limit / RotorLimiter, set every step by its grid's attitude job):
    /// the most actual thrust (N) the ship can hold against its reaction torque (+infinity: none), and the steps since it was
    /// set - a cap not refreshed for CapHoldTicks (the attitude job stopped: thrust torque off, the grid gone) is let go, never
    /// left standing. Uncapped: its maximum without the cap at the last write (N, actual thrust: the readout).</summary>
    public float TorqueCap = float.PositiveInfinity, Uncapped; public int CapAge = int.MaxValue;
    public const int CapHoldTicks = 30;
    /// <summary>Harness (aeroset AeroPropulsorComponent.DevPower 0 / 1, -1 to release): switch every rotor off / on, as the
    /// terminal's on / off does (PowerableBlockComponent.Enabled, server).</summary>
    public static int DevPower = -1;
    internal static readonly List<AeroPropulsorComponent> All = new();
    bool _server;

    void IInSceneListener.OnAddedToScene()
    {
        try { _server = !string.Equals(Data.Scene?.DebugName, "Client", StringComparison.Ordinal); } catch { _server = true; }
        lock (All) All.Add(this);
    }
    void IInSceneListener.OnBeforeRemovedFromScene() { lock (All) All.Remove(this); Restore(); }

    /// <summary>Leaving the scene (removed, the grid cut, the mod unloading it): the rating written back, so a block is never
    /// left with a shaped maximum nobody tends - one that came back would be read as the game's rating (as RestoreJet).</summary>
    void Restore()
    {
        if (!(Rated > 0f) || _gameZero || Written < 0f || Entity == null || !GridPhysics.Alive(Data)) return;
        if (!Data.TryGet<ThrustData>(out var td) || td.MaxThrustPower == Rated) return;
        td.MaxThrustPower = Rated; Written = -1f;
        Data.Set(td);
    }

    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    internal class OnPropulsor : JobGroup;   // (ReversibleThrustComponent's step runs after it: both write ThrustData)

    [OnPropulsor]
    [MustHave(typeof(AeroPropulsorComponent))]
    private static void PropulsorJob(AeroPropulsorComponent p) => p.Tick();

    /// <summary>A RAMJET's hydrogen burn (RamjetLaw FUEL), server, every tick, BEFORE the game's resource distributor charges the
    /// tick (GridUpdateOrder.ResourceDistributorUpdate) - so after the last tick's firing was observed (ThrusterComponent
    /// OnStartFiring, in the grid's UpdateSystems): a burn the game switched on is held off before it is ever charged.</summary>
    [Before(typeof(Keen.Game2.Simulation.GridUpdateOrder.ResourceDistributorUpdate))]
    internal class OnRamjetFuel : JobGroup;

    [OnRamjetFuel]
    [MustHave(typeof(AeroPropulsorComponent))]
    private static void RamjetFuelJob(AeroPropulsorComponent p) { if (p._server && p._known && IsRamjet(p.Kind)) p.FuelTick(); }

    /// <summary>The game burns a hydrogen thruster's whole rate on every tick it fires (its consumption buffer on while IsThrusting -
    /// the grid asking thrust its way - whatever its maximum); this holds that burn off on the ticks RamjetLaw.Burn leaves out of
    /// each window, so the hydrogen follows the thrust share it has now (its Factor). Never switched on unless the game fires it
    /// and the buffer is supplied; the game's own zero (off, out of hydrogen, damaged) and aero off: its burn as the game has it.</summary>
    void FuelTick()
    {
        if (Entity == null || !GridPhysics.Alive(Data)) return;
        if (!_bufLooked) { _bufLooked = true; _buf = RamjetFuelBridge.BufferOf(Entity); }
        var b = _buf;
        if (b == null) return;
        bool firing = Data.Has<IsThrusting>();
        float duty = RamjetLaw.FuelFollowsThrust && !_gameZero && Written >= 0f && !Data.Has<DisabledTag>() ? RamjetLaw.FuelDuty(Factor) : 1f;
        FuelDutyNow = duty;
        bool burn = RamjetLaw.Burn(ref _fuel, duty);
        if (burn) { if (firing && b.CurrentState == Keen.Game2.Simulation.WorldObjects.CubeBlocks.SingleFrameBinaryResourceConsumptionBuffer.State.IdleAndSupplied) b.OnConsuming(); }
        else if (b.IsActive) b.OnStopConsuming();
        if (firing) { FireTicks++; if (b.IsActive) BurnTicks++; }
    }

    /// <summary>The world transform of the thruster's OWN grid (ThrustData.ParentGrid - its direction's frame; a subgrid's on a
    /// rotor or hinge head), the top grid's if that one is gone.</summary>
    WorldTransform OwnGridTransform(in ThrustData td, Entity top)
    {
        var own = new DEntityContext(Data.Scene, td.ParentGrid);
        return GridPhysics.Alive(own) ? own.GetWorldTransform() : top.Data.GetWorldTransform();
    }

    int _skip;
    LookRetry _look;
    /// <summary>The game's MaxThrustPower as last seen (-1: not yet) - the reference for "the game wrote it" while nothing of
    /// ours is written (Written -1).</summary>
    float _seen = -1f;
    void Tick()
    {
        // (its kind and parts - tried again every LookRetry.RetryTicks while they cannot be read: it was once, for good)
        if (!_look.Done)
        {
            if (!_look.Due()) return;
            _known = PropulsorBlocks.Describe(this); _rev = Entity?.TryGet<ReversibleThrustComponent>(); _strike = Entity?.TryGet<AeroRotorStrikeComponent>();
            _spinSwitch = Entity?.TryGet<AeroRotorSpinComponent>();
            _look.Done = _known;
            if (!_known) return;
        }
        if (Entity == null || !GridPhysics.Alive(Data)) return;
        if (_spinSwitch != null) CounterRotating = _spinSwitch.CounterRotating;
        if (Governed(Kind) && _server) StepRotor();
        // ROTOR STRIKES (server: AeroRotorStrikeComponent - its own job probes, repairs and publishes; RotorStrike.cs): broken blades
        bool broken = _strike != null && _strike.Broken, brokeNow = broken != _wasBroken;
        _wasBroken = broken;
        if (CapAge < int.MaxValue) CapAge++;
        if (!Data.TryGet<ThrustData>(out var td)) return;
        float cur = td.MaxThrustPower;
        // the game wrote it (load, power on / off, damage): its value is the rating, or a zero we leave alone - seen on the
        // tick it happens (the pass below at once, not up to 3 ticks later: a block switched on books its full rating with the
        // game until it is shaped again - a ramjet at rest, 430 kN for 3 ticks)
        // (against what we wrote, else the game's value as last seen - so a block never written yet, a disabled one, is not
        //  taken as "the game wrote it" on every tick)
        float expect = Written >= 0f ? Written : _seen;
        bool gameWrote = expect < 0f || MathF.Abs(cur - expect) > 1e-3f * MathF.Max(1f, expect);
        if (++_skip < 3 && !gameWrote && !brokeNow) return;   // (every third tick: airspeed and density change slowly; a break at once)
        _skip = 0;
        if (gameWrote)
        {
            _seen = cur;
            if (cur > 0f) { Rated = cur; _gameZero = false; }
            else { _gameZero = true; Written = 0f; }
        }
        // THE AIR, every pass, whatever the block's state (2026-10-17: it was read only after the gates below - a block the game
        // disabled (a lone ramjet: no hydrogen tank, no power), or one it zeroed, never read its air: "v 0 rho 0 x1.000 max -1"
        // in the readout, a stale Mach / factor in its client copy's exhaust - and its max was first shaped only once it worked)
        // (shaping only with aero on: off, the rating written back - the Jet curve's rule, JetCurve.Step - not a shaped
        //  maximum left standing; the attitude job and its torque cap stop with aero too). Its grid's air once aero has read it
        //  (AeroGridComponent.AirKnown): before that its air is zeros, not a vacuum - the rating meanwhile.
        var top = Entity.GetTopLevelParent();
        var grid = Enabled && AeroSwitch.Enabled ? top : null;
        var aero = grid?.TryGet<AeroGridComponent>();
        if (aero != null && !aero.AirKnown) aero = null;
        GameDensity = top != null && top.Data.TryGet<Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Atmosphere.AirData>(out var ad) ? ad.Density : 0f;
        if (aero == null) { Factor = 1f; Uncapped = Rated; Airspeed = 0f; Density = 0f; Mach = 0f; Inlet = 1f; InletDeg = 0f; _inlet = InletAngle.NewState(); Efficiency = 1f; }
        else
        {
            // (in its OWN grid's frame - ThrustData.Direction is there: a subgrid's propulsor in its subgrid's, as the Jet
            //  curve's inlet angle; 2026-10-17, it took the top grid's frame, wrong on a rotor / hinge head)
            var gwt = OwnGridTransform(in td, grid);
            // the TRUE airspeed along the thrust (the grid's air velocity - the wind out - in its frame . the push
            // direction: ThrustData.Direction) and the flight Mach over the local speed of sound (AeroGridComponent.SetAir)
            Vector3 vLocal = WorldTransform.TransformDirectionInv(aero.LastAirVel, gwt);
            Vector3 dir = Base6Directions.GetVector(td.Direction);
            Airspeed = Vector3.Dot(vLocal, dir);
            float speed = vLocal.Length();
            Density = aero.LastDensity;
            Mach = aero.LastMach;
            if (IsRamjet(Kind))
            {
                // a RAMJET (RamjetLaw): its Mach curve x the density x its inlet - the angle between its intake (its push) and
                // the true airspeed, through its unstart state (InletAngle.Ramjet: one State per engine, stepped every pass)
                InletDeg = InletAngle.AngleDeg(dir, vLocal);
                Inlet = InletAngle.Step(InletAngle.Ramjet, ref _inlet, InletDeg, speed, 3f * AeroPhysics.Dt);
                Factor = RamjetLaw.Factor(Mach, Density, Inlet);
            }
            else Factor = Speed(Kind, Airspeed, speed, aero.LastMach) * DensityFactor(Density);
            Efficiency = ThrustTorque.SeaLevelEfficiency(Entity, GameDensity);
        }
        // the write: none while the game's own zero stands (unpowered by its fuel, damaged) or the block is disabled (the game
        // books nothing for it whatever is written - and writes its rating back itself when it works again: shaped here then)
        if (_gameZero || Rated <= 0f || Data.Has<DisabledTag>()) return;
        float want = Rated;
        if (aero != null)
        {
            // (never above the rating x RhoMax: where the game's own air is too thin for its class - efficiency ~0 - it gives
            //  nothing whatever we write, and a big number written there would stand until the next write once air returns)
            want = MathF.Min(Rated * Factor / MathF.Max(Efficiency, 0.05f), Rated * RhoMax);
            if (LiftNeedsRpm(Kind)) want *= RotorKinematics.ThrustFactor(Rpm);   // (the governed rpm: no lift until spun up)
            if (!float.IsFinite(want)) want = 0f;
            // a main rotor: no more than the ship can hold against its torque (the cap is actual thrust: the game multiplies
            // what is written by its class efficiency)
            float eff = MathF.Max(Efficiency, 0.05f);
            Uncapped = want * eff;
            if (HasReaction(Kind) && CapAge <= CapHoldTicks && TorqueCap >= 0f && float.IsFinite(TorqueCap)) want = MathF.Min(want, TorqueCap / eff);
        }
        // BROKEN BLADES (rotor strikes): no thrust at all, aero on or off - a stub turns nothing (Uncapped too: the readout)
        if (broken) { want = 0f; Uncapped = 0f; }
        // (back to the rating exactly when lifted - no tolerance left standing, as JetCurve.Step)
        if (broken ? cur == want : aero == null ? cur == want : MathF.Abs(want - cur) <= WriteTolerance * Rated) return;
        td.MaxThrustPower = want; Written = want;
        Data.Set(td);
    }

    /// <summary>A governed rpm, one server step: driven while its block is on, powered and working - toward 100 % (a rotor) or
    /// from idle up with its load (a turboprop: PropulsorKinds.TargetRpm).</summary>
    void StepRotor()
    {
        if (DevPower >= 0 && LiftNeedsRpm(Kind))   // (the rotors only, as before the turboprops were governed)
        {
            var pb = Entity.TryGet<PowerableBlockComponent>();
            if (pb != null && pb.Enabled != (DevPower == 1)) pb.Enabled = DevPower == 1;
        }
        // (broken blades - rotor strikes: stopped dead, never driven until repaired)
        if (_strike != null && _strike.Broken) { Rpm = 0f; SpinOn = false; _rpmInit = true; return; }
        bool on = Rated > 0f && !_gameZero && !Data.Has<DisabledTag>();
        if (!_rpmInit)
        {
            // (its rating is read on the first write pass: a few ticks; then as found - running if on)
            if (Rated <= 0f && !_gameZero && ++_rpmWait < 30) return;
            Rpm = on ? IdleRpm(Kind) : 0f; _rpmInit = true;
        }
        SpinOn = on;
        Rpm = RotorKinematics.StepRpm(Rpm, on, AeroPhysics.Dt, TargetRpm(Kind, Load));
    }


    /// <summary>Harness (aeroget AeroPropulsorComponent.Summary): every propulsor in the server scene - kind, size, airspeed,
    /// density, factor, rating and the maximum written. (Read from the harness thread: plain fields, a snapshot.)</summary>
    public static string Summary
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            lock (All)
            {
                int n = 0; foreach (var p in All) if (p._server) n++;
                sb.Append(n).Append(" propulsors");
                foreach (var p in All)
                    if (p._server)
                        sb.Append($" | {p.Kind} {p.Size} v {p.Airspeed:F1} rho {p.Density:F2} (game {p.GameDensity:F2}, class eff {p.Efficiency:F2}) x{p.Factor:F3} rated {p.Rated:F0} N max {p.Written:F0} N{(p._gameZero ? " (game zero)" : "")}{(Governed(p.Kind) ? $" rpm {p.Rpm * 100f:F1}% load {p.Load:F2} {(p.SpinOn ? "on" : "off")}" : "")}{(HasReaction(p.Kind) ? (p.CapAge <= CapHoldTicks && float.IsFinite(p.TorqueCap) ? $" torque cap {p.TorqueCap:F0} N (max without it {p.Uncapped:F0} N)" : " torque cap none") : "")}{(p._rev is { } rv ? $" reversible: side {(rv.Side < 0 ? "-" : "+")} asked {rv.Demand:F0} N flips {rv.State.Flips} push {(rv.PushSide < 0 ? "-" : "+")}" : "")}{(IsRamjet(p.Kind) ? $" ramjet: M {p.Mach:F2} inlet {p.InletDeg:F1} deg x{p.Inlet:F2}{(p._inlet.Unstarted ? " UNSTARTED" : "")} unstarts {p._inlet.Unstarts} fuel duty {p.FuelDutyNow:F2} burned {p.BurnTicks} of {p.FireTicks} firing ticks{(p._bufLooked && p._buf == null ? " (no buffer: the game's flat burn)" : "")}" : "")}");
            }
            return sb.ToString();
        }
    }
}
