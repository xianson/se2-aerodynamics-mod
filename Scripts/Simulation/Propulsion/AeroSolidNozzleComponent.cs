#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// A SOLID ROCKET NOZZLE (2026-10-17; the law and its reasons: SolidRocket.cs). Underneath, a vanilla hydrogen thruster whose resource is
/// SOLID PROPELLANT (the conveyor version, 2026-10-17 - gen_propulsor_defs.py gen_solid: ConsumedResource SolidPropellant, its
/// ResourcesRequiredToThrust SolidRocket.GameRate, one conveyor port on its top face at its size's own scale; its ThrustPower is its
/// VACUUM thrust x MaxStackFactor). Injected into its server AND client prefabs (SolidRocketBlocks.Nozzle*Prefabs):
///   server  its MOTOR: the segments of its size stacked straight on its top face (SolidRocket.FindStack - the chain its conveyor port
///           links, searched again when a solid block comes, goes, moves, turns, breaks or is repaired - AeroSolidSegmentComponent.
///           Version - or every RecheckTicks), and a nozzle at the stack's far end (SolidRocket.OtherEnd: one grain, the two lit
///           nozzles share its factor); IGNITION (the block's own switch turned on while idle - 2026-10-23; DevIgnite from the
///           harness): SolidRocket.Step writes the block's switch (PowerableBlockComponent.Enabled - off until lit: the game's
///           movement, dampeners and hover never fire a switched-off thruster) and its thrust override (ThrusterComponent.
///           ThrustOverride, the game's synced property: the GAME's thrust, applied as any overridden thruster's - no force of ours), to
///           the share of its ThrustPower that gives SolidRocket.ThrustAt(the air: AeroGridComponent.LastDensity, the game's air
///           without aero) x its share of the stack's factor (SolidRocket.ShareFactor of the tanks still holding propellant). The
///           PROPELLANT is the game's: the tanks' charge is read (AeroSolidSegmentComponent.Propellant), never written; the game draws
///           it through the conveyors whenever the nozzle fires, ResourcesRequiredToThrust whole per tick - FuelTick lets it burn on
///           SolidRocket.BurnDuty of the ticks (DutyCycle; RamjetFuelBridge reaches the game's consumption buffer, as the ramjets'), so
///           the burn follows the thrust it commands (level and steering) exactly. Spent (SolidRocket.Usable: less than one tick of
///           the game's rate left - the game cut the thrust there itself), the override and the switch are handed back in one tick.
///           Its burning and power level are saved with the block (AeroBlockSave: "aero-solidnozzle:1 1 level"); the switch, override
///           and the tanks are the game's own.
///           POWER LEVEL (2026-10-17): idle, the player's thrust override slider is left alone - it is the level (SolidRocket.
///           LevelFrom: unset 1, else 0.5..1), latched at ignition; lit, the motor holds level x its stack's full thrust (its
///           BASE), or the share of full the attitude allocator solved for rotation this tick (Steer, from ThrustTorque - within
///           0.5..1; stale after SteerTicks: the base again), written through its override. After ignition the slider shows the
///           motor's override (the game's real thrust: a share of ThrustPower) and a player's change is put back - the level is
///           locked (the terminal shows it: PowerLevel).
///   both    ThrustNow (N): the game's overridden thrust on it (override x ThrustPower x its class's efficiency; 0 while switched off
///           or not supplied - DisabledTag) with its place and push in its grid - ThrustTorque takes it OUT of the thrust it shares
///           among the thrusters (the nozzle is out of the mod's thruster set: OffsetThrustJob.RebuildThrusterCache) and adds its own
///           offset torque; a lit server nozzle is also a bounded slot of its attitude solve (SteerSlots).
/// </summary>
public partial class AeroSolidNozzleComponent : Component, IInSceneListener
{
    /// <summary>Every nozzle, BOTH scenes (its _server flag).</summary>
    internal static readonly List<AeroSolidNozzleComponent> All = new();
    static volatile int _count;
    /// <summary>The stacks (a stack can be two nozzles' - one at each end; StackHolding reads them from the panels' thread).</summary>
    static readonly object StackLock = new();
    public const int RecheckTicks = 120, GeoTicks = 30, AirTicks = 10;
    /// <summary>Harness (aeroset AeroSolidNozzleComponent.DevIgnite n): a new positive value asks every server nozzle to ignite.</summary>
    public static int DevIgnite;
    /// <summary>Harness (aeroset AeroSolidNozzleComponent.DevGimbal x): a new value x &gt; 0 turns every server nozzle's thrust
    /// vectoring on at a cone of x deg (clamped 0.5..15), x &lt; 0 off (0: nothing) - the in-game check without the panel.</summary>
    public static float DevGimbal;

    public SolidRocketBlocks.Row Row;
    internal SolidRocket.Motor Motor;
    internal bool RestoredLit; internal float RestoredLevel = 1f;   // (from the block's saved data: AeroBlockSave)
    bool _server; LookRetry _look;
    int _devSeen, _stackVersion = -1, _recheckIn, _geoIn, _airIn; float _devGimbalSeen;
    readonly List<AeroSolidSegmentComponent> _stack = new(), _cands = new();
    SolidRocket.Geo _geo;
    /// <summary>The nozzle at the far end of its stack (SolidRocket.OtherEnd; null: none) - when it is lit too, they share the factor.</summary>
    AeroSolidNozzleComponent _partner;
    CubeBlockComponent _cb; PowerableBlockComponent _pb; ThrusterComponent _tc;
    /// <summary>Its definition's ThrustPower (N: the vacuum thrust).</summary>
    public float ThrustPower;
    /// <summary>Its grid, its place in it (m, the block's centre) and its push there (unit), cached in its own step.</summary>
    internal Entity GridEntity; internal Vector3 PosLocal, PushLocal;
    /// <summary>Its grid's aero component (the structure's: PublishLoad), looked up with the grid (GeoTicks).</summary>
    AeroGridComponent _gridAero;
    /// <summary>The game's overridden thrust on it now (N; its own step).</summary>
    internal float ThrustNow;
    /// <summary>The air's pressure ratio (0 vacuum .. 1 sea level), its class's efficiency, the override it wants and has.</summary>
    public float PressureRatio = 1f, Efficiency = 1f, TargetOverride, OverrideNow;
    /// <summary>Its motor: segments, the tanks still holding propellant, propellant left and full (kg), the lit nozzles on it (1, or 2
    /// with a lit partner) - its own step's (read by the panel, the summary). StackPropellant: what it can still burn (SolidRocket.Usable).</summary>
    public int StackCount, StackLive, Sharers = 1; public float StackPropellant, StackFull;
    /// <summary>The share of the game's burn ticks it lets burn (SolidRocket.BurnDuty; FuelTick), and the server ticks it fired / burned.</summary>
    public float FuelDutyNow; public long FireTicks, BurnTicks;
    DutyCycle.State _duty;
    Keen.Game2.Simulation.WorldObjects.CubeBlocks.SingleFrameBinaryResourceConsumptionBuffer _buf; bool _bufLooked;
    /// <summary>The stack's full thrust now (N: SolidRocket.FullThrust), its base (level x full) and the thrust it commands this
    /// tick (the base, or the allocator's steer) - its own step's (server).</summary>
    public float FullThrust, BaseThrust, CommandThrust;
    /// <summary>The share of full (0.5..1) it burns at this tick: the commanded thrust / full.</summary>
    public float ThrustFraction = 1f;
    /// <summary>The attitude allocator's share of full for this nozzle (ThrustTorque, Steer) and the ticks it stays good for.</summary>
    float _steerFrac = 1f; int _steerFresh;
    /// <summary>A steer older than this (ticks) is dropped: the motor goes back to its base (aero off, a far grid stepped slowly).</summary>
    public const int SteerTicks = 3;
    /// <summary>THRUST VECTORING (2026-10-20; ThrustVectorGame, ThrustVector): its panel's "Thrust Vectoring" switch and "Gimbal Cone
    /// (deg)" - lit and on, the attitude law swivels its thrust within the cone (at ThrustVector.DefaultRateDeg) for any ship's
    /// attitude control; saved with the block. GimbalDeflect: its deflection now (local rad, across its push - ThrustTorque's).</summary>
    public bool GimbalOn; public float GimbalDeg = GimbalLimits.DefaultDeg;
    internal Vector3 GimbalDeflect;

    void IInSceneListener.OnAddedToScene()
    {
        try { _server = !string.Equals(Data.Scene?.DebugName, "Client", StringComparison.Ordinal); } catch { _server = true; }
        lock (All) { All.Add(this); _count = All.Count; }
        AeroSolidSegmentComponent.Bump();
    }

    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        lock (All) { All.Remove(this); _count = All.Count; }
        ThrustNow = 0f;
        AeroSolidSegmentComponent.Bump();
    }

    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    internal class OnSolidNozzle : JobGroup;

    [OnSolidNozzle]
    [MustHave(typeof(AeroSolidNozzleComponent))]
    private static void NozzleJob(AeroSolidNozzleComponent n) => n.Tick();

    /// <summary>The terminal / toolbar's Ignite (server copy; nothing while it burns or has no propellant).</summary>
    public void Ignite() { if (_server) Motor.Request = true; }

    /// <summary>The game's propellant burn (SolidRocket "the burn"), server, every tick, BEFORE the game's resource distributor charges the
    /// tick (as the ramjets' OnRamjetFuel): a burn the game switched on is held off before it is charged.</summary>
    [Before(typeof(Keen.Game2.Simulation.GridUpdateOrder.ResourceDistributorUpdate))]
    internal class OnSolidFuel : JobGroup;

    [OnSolidFuel]
    [MustHave(typeof(AeroSolidNozzleComponent))]
    private static void SolidFuelJob(AeroSolidNozzleComponent n) { if (n._server && n.Row != null) n.FuelTick(); }

    /// <summary>The game burns ResourcesRequiredToThrust (SolidRocket.GameRate) whole on every tick the nozzle fires; this holds that
    /// burn off on the ticks DutyCycle leaves out of each window, so the propellant follows the thrust it commands (FuelDutyNow: its
    /// share of the stack's factor x the share of full / MaxStackFactor). Never switched on unless the game fires it and the buffer is
    /// supplied. Not lit (or the buffer not reached - RamjetFuelBridge's warning): the game's burn as it has it.</summary>
    void FuelTick()
    {
        if (Entity == null || !GridPhysics.Alive(Data)) return;
        if (!_bufLooked) { _bufLooked = true; _buf = RamjetFuelBridge.BufferOf(Entity); }
        var b = _buf;
        if (b == null || Motor.Phase != SolidRocket.Phase.Lit) return;
        bool firing = Data.Has<IsThrusting>();
        bool burn = DutyCycle.Step(ref _duty, FuelDutyNow, SolidRocket.FuelWindow);
        if (burn) { if (firing && b.CurrentState == Keen.Game2.Simulation.WorldObjects.CubeBlocks.SingleFrameBinaryResourceConsumptionBuffer.State.IdleAndSupplied) b.OnConsuming(); }
        else if (b.IsActive) b.OnStopConsuming();
        if (firing) { FireTicks++; if (b.IsActive) BurnTicks++; }
    }

    /// <summary>It can be ignited now (idle, a motor with propellant).</summary>
    public bool CanIgnite => _server && Motor.Phase == SolidRocket.Phase.Idle && StackPropellant > 0f;
    public bool Burning => Motor.Phase != SolidRocket.Phase.Idle;
    /// <summary>The burn left at the rate it burns now (s): its level (or idle, the slider's) - the steer moves it a little.</summary>
    public float BurnTimeLeft
    {
        get
        {
            if (Row == null || !(Row.Thrust > 0f) || !(StackFactor > 0f)) return 0f;   // (StackFactor: its share - SolidRocket.ShareFactor)
            float rate = SolidRocket.BurnRate(Row.Thrust, StackFactor, Motor.Phase == SolidRocket.Phase.Lit ? ThrustFraction : PowerLevel);
            return rate > 0f ? StackPropellant / rate : 0f;
        }
    }
    /// <summary>Its power level (0.5..1): latched at ignition - while idle, what the player's override slider would set.</summary>
    public float PowerLevel => Motor.Phase == SolidRocket.Phase.Idle ? SolidRocket.LevelFrom(_tc?.ThrustOverride ?? 0f) : SolidRocket.LevelOr1(Motor.Level);
    /// <summary>Its thrust and burn-rate factor now: its share of the stack's (SolidRocket.ShareFactor of the tanks still holding propellant
    /// and the lit nozzles on the stack).</summary>
    public float StackFactor;

    void Tick()
    {
        if (Entity == null || !GridPhysics.Alive(Data)) return;
        if (!_look.Done)
        {
            // (its row and parts - tried again every LookRetry.RetryTicks while they cannot be read: it was once, for good)
            if (!_look.Due()) return;
            _cb = Entity.TryGet<CubeBlockComponent>(); _pb = Entity.TryGet<PowerableBlockComponent>(); _tc = Entity.TryGet<ThrusterComponent>();
            Row = SolidRocketBlocks.RowOf(_cb);
            if (Row != null && !Row.Nozzle) Row = null;
            _look.Done = Row != null && _cb != null;
            if (!_look.Done) return;
            ThrustPower = Row.Thrust * (1f + SolidRocketBlocks.VacuumBoost) * SolidRocket.MaxStackFactor;   // (the definition's: gen_propulsor_defs.py)
            if (RestoredLit) { Motor.Phase = SolidRocket.Phase.Lit; Motor.Level = SolidRocket.LevelOr1(RestoredLevel); }   // (a burning motor saved: burning again at its level - the game restored its switch and override)
            _devSeen = DevIgnite; _devGimbalSeen = DevGimbal;
        }
        if (Row == null || _cb == null) return;
        if (--_geoIn <= 0)
        {
            _geoIn = GeoTicks;
            var bb = _cb.AABB;
            var g = SolidRocket.Geo.Of(bb.Min, bb.Max, Base6Directions.GetVector(_cb.BlockOrientation.TransformDirection(Base6Directions.Direction.Up)), Row.Size);
            var grid = _cb.Grid?.Entity;
            if (!g.Same(in _geo) || !ReferenceEquals(grid, GridEntity)) { _geo = g; GridEntity = grid; AeroSolidSegmentComponent.Bump(); }
            PosLocal = CellCoords.BlockCentre(bb.Min, bb.Max);
            _gridAero = _server && grid != null && GridPhysics.Alive(grid.Data) ? grid.TryGet<AeroGridComponent>() : null;   // (again each look: the grid's component may come after the nozzle)
        }
        if (--_airIn <= 0) { _airIn = AirTicks; ReadAir(); }
        // the game's thrust on it now: its override x its rating x its class's efficiency (0 while switched off)
        float o = Data.TryGet<ThrusterOverrideData>(out var od) ? od.OverridePower : 0f;
        OverrideNow = o;
        if (Data.TryGet<ThrustData>(out var td)) PushLocal = Base6Directions.GetVector(td.Direction);
        ThrustNow = Data.Has<DisabledTag>() || !(o > 0f) ? 0f : o * ThrustPower * Efficiency;
        if (!_server) return;
        PublishLoad();

        if (DevIgnite != _devSeen) { _devSeen = DevIgnite; if (DevIgnite > 0) Motor.Request = true; }
        if (DevGimbal != _devGimbalSeen) { _devGimbalSeen = DevGimbal; if (DevGimbal > 0f) { GimbalOn = true; GimbalDeg = GimbalLimits.Cone(DevGimbal); } else if (DevGimbal < 0f) GimbalOn = false; }
        int ver = System.Threading.Volatile.Read(ref AeroSolidSegmentComponent.Version);
        if (ver != _stackVersion || --_recheckIn <= 0) Rebuild(ver);
        float total; int live;
        lock (StackLock) { total = SolidRocket.Total(_stack); live = SolidRocket.Live(_stack); }
        var partner = _partner;
        int sharers = partner != null && partner.Motor.Phase == SolidRocket.Phase.Lit && Motor.Phase == SolidRocket.Phase.Lit ? 2 : 1;
        StackLive = live; Sharers = sharers;
        StackFactor = SolidRocket.ShareFactor(live, sharers);
        // the thrust it commands: its base (level x its full thrust), or - lit, the allocator's share for rotation this tick
        // (ThrustTorque: within 0.5..1 of full; dropped after SteerTicks) - through its override
        float full = SolidRocket.FullThrust(Row.Thrust, StackFactor, PressureRatio, ThrustPower, Efficiency);
        float level = SolidRocket.LevelOr1(Motor.Level);
        float frac = _steerFresh > 0 && Motor.Phase == SolidRocket.Phase.Lit ? Math.Clamp(_steerFrac, SolidRocket.MinLevel, 1f) : level;
        if (_steerFresh > 0) _steerFresh--;
        FullThrust = full; BaseThrust = level * full; CommandThrust = frac * full; ThrustFraction = frac;
        TargetOverride = SolidRocket.OverrideFor(CommandThrust, ThrustPower, Efficiency);
        // the propellant is the game's: what it can still burn (less than one tick of its rate: spent - the game cut the thrust there)
        float usable = SolidRocket.Usable(total, Row.Thrust, AeroPhysics.Dt);
        // THE SWITCH IS THE IGNITION (2026-10-23, the user: "get rid of ignite, just have on/off"): idle, the mod holds the block
        // switched off (the game's movement and dampeners never fire it), so a switch found ON while idle is the player turning it on -
        // it asks to light (SolidRocket.Step: no propellant or broken - it is put back off; else the player's override off and the
        // switch off in one tick, then on, then the motor's own override - the same writes as an Ignite)
        if (Motor.Phase == SolidRocket.Phase.Idle && _pb != null && _pb.Enabled) Motor.Request = true;
        var want = SolidRocket.Step(ref Motor, usable, _pb == null || _pb.Functional, TargetOverride, _tc?.ThrustOverride ?? 0f);
        // the burn follows the thrust (the same impulse at any level): the share of the game's burn ticks FuelTick lets through
        FuelDutyNow = SolidRocket.Burning(Motor.Phase) ? SolidRocket.BurnDuty(StackFactor, frac) : 0f;
        StackPropellant = usable;
        Apply(want);
    }

    /// <summary>The block's switch and override as SolidRocket.Step wants them (written only when they differ: each write is a
    /// synced property - and the game re-books the grid's thrust on it; the switch first, so a hand-back finds the block already
    /// disabled - SolidRocket "ignition"). Keep: the override is the player's (idle: the level).</summary>
    void Apply(SolidRocket.Want w)
    {
        if (_pb != null && _pb.Enabled != w.Enabled) _pb.Enabled = w.Enabled;
        if (_tc == null) return;
        if (w.Keep)
        {
            // idle: the override is the player's power level - unset (0) shown as its meaning, 100 % (the user, 2026-10-23: "default
            // thrust override 100"); written only while switched off (Keep is idle: off), which books nothing
            if (!(_tc.ThrustOverride > 0f) && (_pb == null || !_pb.Enabled)) _tc.ThrustOverride = 1f;
            return;
        }
        bool has = Data.Has<ThrusterOverrideData>();
        float cur = _tc.ThrustOverride;
        if (!(w.Override > 0f)) { if (has || cur > 0f) _tc.ThrustOverride = 0f; return; }
        if (!has || MathF.Abs(cur - w.Override) > 0.002f) _tc.ThrustOverride = w.Override;
    }

    /// <summary>The air: the mod's density where aero has read its grid's air (AeroGridComponent.AirKnown: LastDensity), else the
    /// game's air - both on the game's scale (sea level 1: SolidRocket.PressureRatioOf); and the game's thrust class efficiency
    /// there (hydrogen: 1 in any air). (2026-10-17: the source was picked on HasResult - the force model ran this step, only
    /// above AeroConfig.MinSpeed - over two scales: p 0.85 idle, 0.69 lit on a held grid at 300 m.)</summary>
    void ReadAir()
    {
        var top = Entity.GetTopLevelParent();
        float game = top != null && top.Data.TryGet<Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Atmosphere.AirData>(out var ad) ? ad.Density : 0f;
        var aero = AeroSwitch.Enabled ? top?.TryGet<AeroGridComponent>() : null;
        bool aeroAir = aero != null && aero.AirKnown;
        PressureRatio = SolidRocket.PressureRatioOf(aeroAir, aeroAir ? aero.LastDensity : 0f, game);
        Efficiency = ThrustTorque.SeaLevelEfficiency(Entity, game);
    }

    /// <summary>Its motor searched again: one grid's segments (the registry's cached places - no engine reads), then FindStack; and the
    /// nozzle at the stack's far end (OtherEnd - the same grid's server nozzles' cached places).</summary>
    void Rebuild(int ver)
    {
        _stackVersion = ver; _recheckIn = RecheckTicks;
        _cands.Clear();
        var grid = GridEntity;
        if (grid != null)
            lock (AeroSolidSegmentComponent.All)
                foreach (var s in AeroSolidSegmentComponent.All)
                    if (ReferenceEquals(s.GridEntity, grid) && !s.OnNozzle) _cands.Add(s);
        var own = Entity?.TryGet<AeroSolidSegmentComponent>();   // (its own grain, 2026-10-23: first in its stack - a lone nozzle is a motor)
        int segments;
        lock (StackLock)
        {
            SolidRocket.FindStack(in _geo, _cands, _stack);
            segments = _stack.Count;
            if (own != null) _stack.Insert(0, own);
            StackCount = _stack.Count;
            float full = 0f;
            for (int i = 0; i < _stack.Count; i++) full += _stack[i].Full;
            StackFull = full;
        }
        _cands.Clear();
        AeroSolidNozzleComponent partner = null;
        if (grid != null && segments > 0)   // (a nozzle at the far end of its SEGMENTS: its own grain is not on the chain)
        {
            var last = _stack[_stack.Count - 1].Geo;
            lock (All)
                foreach (var n in All)
                    if (n != this && n._server && ReferenceEquals(n.GridEntity, grid) && SolidRocket.OtherEnd(in _geo, in last, in n._geo)) { partner = n; break; }
        }
        _partner = partner;
    }

    /// <summary>
    /// ITS THRUST IN THE STRUCTURE (2026-10-17; server, every tick it thrusts): the game's overridden thrust on it now (ThrustNow -
    /// its base, or the attitude allocator's steer written through its override: the whole of what the game applies, steering
    /// included) along its push, at its block (AeroGridComponent.PublishBlockLoad). The nozzle is out of the thruster cache
    /// (OffsetThrustJob.RebuildThrusterCache), so the structure's thruster loads (ThrF / ThrF0: the cache's thrusters only, their
    /// shares of the game's thrust LESS the rockets' - ThrustTorque.Apply's gameF -= solidF) never hold it: counted once, here.
    /// The thrusters' own answer to its steering (KeepNetThrust) is theirs, in ThrF. Its offset torque (ThrustTorque, solidTau) is
    /// this force's moment - the structure finds it from the point. isEngine: rigid like a thruster (it is one: the game's
    /// ThrusterComponent - no rebuild), off with Structure.ThrustLoads. Not thrusting: nothing (the entry lapses in two ticks).
    /// </summary>
    void PublishLoad()
    {
        var aero = _gridAero;
        float t = ThrustNow;
        if (aero == null || !(t > 0f) || Entity == null) return;
        // (swivelled: its thrust along the deflected axis - the side force the gimbal gives is this block's load too)
        Vector3 push = GimbalOn && GimbalDeflect != Vector3.Zero ? ThrustVector.Pushed(PushLocal, GimbalDeflect) : PushLocal;
        aero.PublishBlockLoad(Entity, push * t, PosLocal, Vector3.Zero, true);
    }

    /// <summary>ThrustTorque: the attitude allocator's share of full (0.5..1) for this lit nozzle - its override from its next step.
    /// (Not finite: ignored - it holds its base.)</summary>
    internal void Steer(float fraction)
    {
        if (!float.IsFinite(fraction)) return;
        _steerFrac = Math.Clamp(fraction, SolidRocket.MinLevel, 1f);
        _steerFresh = SteerTicks;
    }

    /// <summary>ThrustTorque: this grid's lit SERVER nozzles the attitude allocator may steer (into, cleared first - no
    /// allocation): burning, thrusting now (the game's override on it), its full thrust known. A nozzle not yet lit is never
    /// one: the controllers never fire it.</summary>
    internal static int SteerSlots(Entity grid, System.Collections.Generic.List<AeroSolidNozzleComponent> into)
    {
        into.Clear();
        if (_count == 0 || grid == null) return 0;
        lock (All)
            foreach (var n in All)
                if (n._server && ReferenceEquals(n.GridEntity, grid) && n.Motor.Phase == SolidRocket.Phase.Lit && n.ThrustNow > 0f && n.FullThrust > 0f)
                    into.Add(n);
        return into.Count;
    }

    /// <summary>The server nozzle whose motor holds this segment (null: inert). For the panels (any thread).</summary>
    internal static AeroSolidNozzleComponent StackHolding(AeroSolidSegmentComponent seg)
    {
        if (seg == null || _count == 0) return null;
        lock (All)
            foreach (var n in All)
            {
                if (!n._server || !ReferenceEquals(n.GridEntity, seg.GridEntity)) continue;
                lock (StackLock) if (n._stack.Contains(seg)) return n;
            }
        return null;
    }

    /// <summary>ThrustTorque: the overridden thrust of this grid's solid rocket nozzles (grid-local N) and its torque about 'com'
    /// (N m). False: none burning. (Mod fields only - each nozzle's own step wrote them; this grid's scene's copies only, by
    /// their grid.)</summary>
    internal static bool GridThrust(Entity grid, Vector3 com, out Vector3 force, out Vector3 torque)
    {
        force = Vector3.Zero; torque = Vector3.Zero;
        if (_count == 0 || grid == null) return false;
        bool any = false;
        lock (All)
            foreach (var n in All)
            {
                if (!ReferenceEquals(n.GridEntity, grid)) continue;
                float t = n.ThrustNow;
                if (!(t > 0f)) continue;
                Vector3 f = n.PushLocal * t;
                force += f; torque += Vector3.Cross(n.PosLocal - com, f);
                any = true;
            }
        return any;
    }

    /// <summary>Harness (aeroget AeroSolidNozzleComponent.Summary): every server nozzle - size, phase, segments, propellant, the
    /// thrust it wants and has. (Plain fields, a snapshot.)</summary>
    public static string Summary
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            lock (All)
            {
                int n = 0; foreach (var z in All) if (z._server) n++;
                sb.Append(n).Append(" solid nozzles");
                foreach (var z in All)
                    if (z._server)
                        sb.Append($" | {z.Row?.Name ?? "?"} {z.Motor.Phase} level {z.PowerLevel:F2} stack {z.StackCount} (live {z.StackLive}, nozzles {z.Sharers}{(z._partner != null ? ", partner" : "")}) factor {z.StackFactor:F2} propellant {z.StackPropellant:F0}/{z.StackFull:F0} kg burn left {z.BurnTimeLeft:F1} s p {z.PressureRatio:F2} full {z.FullThrust:F0} base {z.BaseThrust:F0} cmd {z.CommandThrust:F0} N (steer {(z._steerFresh > 0 ? "on" : "off")}, gimbal {(z.GimbalOn ? $"on {z.GimbalDeg:F1} deg, at {z.GimbalDeflect.Length() / ApMath.Deg:F2}" : "off")}) override want {z.TargetOverride:F3} has {z.OverrideNow:F3} thrust {z.ThrustNow:F0} N fuel duty {z.FuelDutyNow:F3} burned {z.BurnTicks} of {z.FireTicks} firing ticks{(z._bufLooked && z._buf == null ? " (no buffer: the game's flat burn)" : "")}");
            }
            return sb.ToString();
        }
    }
}
