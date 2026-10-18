#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// THE EXHAUST of a RAMJET or a SOLID ROCKET NOZZLE on a CLIENT (2026-10-17; injected into their client prefabs: EntityInjector, by
/// ExhaustBlocks' block GUIDs through PropulsorBlocks.RamjetClientPrefabs / SolidRocketBlocks.NozzleClientPrefabs). Visual only; the law
/// is ExhaustLaw (game-free, Tests/ExhaustTests), the effects are the mod's own (the Blocks project's Assets/Aero/Propulsors/Exhaust,
/// written by tools/meshgen/gen_propulsor_defs.py gen_exhaust - copies of vanilla flames and smoke; nothing of the game's flame is used:
/// the blocks' ThrusterEffects definitions carry no particle effect). Every frame (after RenderSubmissionBegin):
///   RAMJET  its state from the block's own AeroPropulsorComponent (its client copy runs the same law on the client's air: Mach, its
///           thrust factor, its inlet's unstart state) and its thrust share (its AeroFlameComponent's - the controller's share the
///           server published, eased - else the game's own command in its direction, or a pilot's override) -> ExhaustLaw.Ramjet ->
///           the flame (strength = effect time, length = VelocityMultiplier, the pop's glow = ColorMultiplier) and up to
///           ExhaustLaw.DiamondMax shock-diamond knots down its axis (each its own effect, moved to the Mach's spacing).
///   SOLID   its AeroSolidNozzleComponent's client copy: ThrustNow (the game's overridden thrust: stack factor x level x steering x the
///           air) over one segment's thrust, and the air's pressure ratio -> ExhaustLaw.Solid -> the plume (+ its light), the vacuum
///           balloon and the smoke trail (world space: it lingers where it was laid; its box stretched along the flight).
/// The effects are spawned under the block's grid's render root at the block's exhaust (ExhaustBlocks.Row: the flame dummy's point in
/// the block's model space, turned down the plume's axis) - when the engine first lights; then held: emission stopped when dark (the
/// particles in flight live out - a smoke trail fades in its own time), resumed when lit, disposed after IdleDisposeSeconds dark, on
/// leaving the scene, or with the effects off. Caps: MaxEngines engines with effects alive, MaxSmokers smoke trails, every grid together.
/// No allocation per frame (ExhaustParticles: typed delegates; parameters written only when they move); no try/catch on the frame path.
/// Off with Enabled or the mod's every-effect switch (AeroEntryFx.AllEffects, setting Fx.Effects).
/// </summary>
public partial class AeroExhaustComponent : Component, IInSceneListener
{
    // ── knobs (harness: aeroset AeroExhaustComponent.X) ──
    public static bool Enabled = true;
    /// <summary>Engines with effects alive at once, and smoke trails, every grid together (a refused engine tries again later).</summary>
    public static int MaxEngines = 32, MaxSmokers = 8;
    /// <summary>Dark this long (s): its effects are disposed (its slot in MaxEngines freed).</summary>
    public static float IdleDisposeSeconds = 20f;
    /// <summary>Its place relative to the render root is re-read this often (s) - every frame when its grid is not the render root's.</summary>
    public static float PlaceEvery = 0.5f;
    /// <summary>Harness: hold every engine at this thrust share / ramjet Mach / pressure ratio (negative: its own); a new positive DevUnstart
    /// unstarts every ramjet's exhaust for DevUnstartSeconds (the pop and sputter, seen without flying it).</summary>
    public static float DevThrust = -1f, DevMach = -1f, DevPressure = -1f, DevUnstartSeconds = 2f;
    public static int DevUnstart;
    // ── diagnostics (aeroget AeroExhaustComponent.X) ──
    public static int Active, Smokers, Refused, Seen;
    public static string Status = "-";
    /// <summary>The last stepped ramjet's / nozzle's law input and output (aeroget AeroExhaustComponent.LastRamjet / LastSolid: the text
    /// is made when read - nothing per frame).</summary>
    static ExhaustLaw.RamjetIn _lri; static ExhaustLaw.RamjetOut _lro; static ExhaustLaw.SolidIn _lsi; static ExhaustLaw.SolidOut _lso;
    static string _lrn = "-", _lsn = "-"; static bool _lrs;
    public static string LastRamjet => $"{_lrn}: M {_lri.Mach:F2} share {_lri.Share:F2} factor {_lri.Factor:F2}{(_lri.Unstarted ? " UNSTARTED" : "")} -> flame {_lro.Flame:F3} len {_lro.FlameLength:F1} r glow {_lro.Glow:F1} diamonds {_lro.Diamonds} every {_lro.DiamondSpacing:F2} r strength {_lro.DiamondStrength:F2} size x{_lro.DiamondScale:F2}";
    public static string LastSolid => $"{_lsn}: ratio {_lsi.Ratio:F2} p {_lsi.Pressure:F2} v {_lsi.Speed:F0} -> plume {_lso.Plume:F3} len {_lso.PlumeLength:F1} r alpha {_lso.CoreAlpha:F2} balloon {_lso.Balloon:F2} x{_lso.BalloonScale:F2} smoke {_lso.Smoke:F2} alpha {_lso.SmokeAlpha:F2} stretch {_lso.SmokeStretch:F1}{(_lrs ? " SPUTTER" : "")}";

    ExhaustBlocks.Row _row; LookRetry _look;
    AeroPropulsorComponent _prop; AeroSolidNozzleComponent _noz; AeroFlameComponent _flame; AeroGridComponent _aero;
    ExhaustLaw.RamjetState _rs; ExhaustLaw.RamjetOut _ro;
    ExhaustLaw.SolidState _ss; ExhaustLaw.SolidOut _so;
    ExhaustSlot _main, _balloon, _smoke;
    readonly List<ExhaustSlot> _knots = new();
    bool _holding, _smoking; float _dark; long _last, _placeAt, _retryAt, _devUntil; int _devSeen, _seed;
    Entity _grid; object _parent; bool _gridIsRoot;
    Vector3 _bp; Quaternion _bq = Quaternion.Identity;

    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene() { EndAll(); _look.Reset(); _row = null; _prop = null; _noz = null; _flame = null; _aero = null; _grid = null; _parent = null; }

    [After(typeof(RenderSubmissionBegin))]
    private class OnAeroExhaust : JobGroup;

    [OnAeroExhaust]
    [MustHave(typeof(AeroExhaustComponent))]
    private static void ExhaustJob(AeroExhaustComponent c)
    {
        if (!GridPhysics.Alive(c.Data)) return;
        c.Step();
    }

    /// <summary>Its row and its block's engine components - tried again every LookRetry.RetryTicks frames while they cannot be
    /// read (2026-10-17: once, for good - an engine missed then never had an exhaust).</summary>
    void Look()
    {
        var cb = Entity?.TryGet<CubeBlockComponent>();
        var g = cb != null ? BlockComponentFactory.GetDefinitionGuid(cb) : null;
        _row = g.HasValue ? ExhaustBlocks.RowOf(g.Value) : null;
        _look.Done = _row != null;
        if (_row == null) return;
        _prop = Entity.TryGet<AeroPropulsorComponent>(); _noz = Entity.TryGet<AeroSolidNozzleComponent>(); _flame = Entity.TryGet<AeroFlameComponent>();
        _seed = Entity.GetHashCode() & 0x7fff;
        _devSeen = DevUnstart;
        System.Threading.Interlocked.Increment(ref Seen);
    }

    void Step()
    {
        if (!_look.Done && _look.Due()) Look();
        if (_row == null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp(); double freq = System.Diagnostics.Stopwatch.Frequency;
        float dt = _last == 0 ? 0f : (float)Math.Min(0.1, (now - _last) / freq);
        _last = now;
        if (!Enabled || !AeroEntryFx.AllEffects) { EndAll(); return; }
        if (_row.Kind == ExhaustBlocks.Kinds.Ramjet) StepRamjet(dt, now, freq); else StepSolid(dt, now, freq);
    }

    // ── RAMJET ──
    void StepRamjet(float dt, long now, double freq)
    {
        var p = _prop;
        ExhaustLaw.RamjetIn i = default;
        i.AeroOn = p != null && AeroSwitch.Enabled && AeroPropulsorComponent.Enabled;
        i.Mach = DevMach >= 0f ? DevMach : (p != null ? p.Mach : 0f);
        i.Factor = DevMach >= 0f ? RamjetLaw.Factor(DevMach, p != null && p.Density > 0f ? p.Density : AeroPropulsorComponent.RhoRef, 1f) : (p != null ? p.Factor : 1f);
        if (DevUnstart != _devSeen) { _devSeen = DevUnstart; if (DevUnstart > 0) _devUntil = now + (long)(freq * DevUnstartSeconds); }
        i.Unstarted = (p != null && p.InletUnstarted) || now < _devUntil;
        i.Share = DevThrust >= 0f ? DevThrust : Share();
        ExhaustLaw.Ramjet(ref _rs, in i, dt, ref _ro);
        bool lit = _ro.Flame > 0.002f || _ro.Diamonds > 0;
        _lrn = _row.Name; _lri = i; _lro = _ro;   // (the readout's: plain copies)
        if (!Hold(lit, dt, now, freq)) return;
        float sc = _row.ExitRadius / ExhaustBlocks.RefRadius;
        float flick = ExhaustLaw.Flicker(now / freq, _seed);
        if (ExhaustParticles.Alive(_main))
        {
            ExhaustParticles.Time(_main, _ro.Flame * flick);
            ExhaustParticles.Params(_main, sc, MathF.Max(0.05f, _ro.FlameLength / ExhaustBlocks.RamjetLenAuthored), 1f, _ro.Glow, _ro.Glow, _ro.Glow, 1f);
        }
        for (int k = 0; k < _knots.Count; k++)
        {
            var kn = _knots[k];
            if (!ExhaustParticles.Alive(kn)) continue;
            float s = ExhaustLaw.KnotStrength(in _ro, k);
            ExhaustParticles.Time(kn, s);
            if (s <= 0f) continue;
            ExhaustParticles.Params(kn, sc * _ro.DiamondScale, 1f, 1f, 1f, 1f, 1f, 1f);
            ExhaustParticles.Move(kn, Place(_row.Exhaust + _row.Axis * (_row.ExitRadius * ExhaustLaw.KnotDistance(in _ro, k))), _bq * _row.Turn);
        }
    }

    /// <summary>The thrust share it shows: its flame component's (the share the controller gives it, eased; the game's own value when
    /// that one lets go), else the game's command in its direction (a pilot's override: its power) - 0..1 of its maximum now.</summary>
    float Share()
    {
        var f = _flame;
        if (f != null && f.Shown >= 0f) return f.Shown;
        var data = Data;
        if (data.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.ThrusterOverrideData>(out var po)) return Math.Clamp(po.OverridePower, 0f, 1f);
        if (!data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.ThrustData>(out var td)) return 0f;
        var gridData = new DEntityContext(data.Scene, td.ParentGrid);
        if (!GridPhysics.Alive(gridData) || !gridData.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.VoluntaryThrustData>(out var vt)) return 0f;
        var v = vt.VoluntaryThrust; float n;
        switch (td.Direction)
        {
            case Base6Directions.Direction.Backward: n = v.Z; break;
            case Base6Directions.Direction.Forward: n = -v.Z; break;
            case Base6Directions.Direction.Right: n = v.X; break;
            case Base6Directions.Direction.Left: n = -v.X; break;
            case Base6Directions.Direction.Up: n = v.Y; break;
            default: n = -v.Y; break;
        }
        return Math.Clamp(n, 0f, 1f);
    }

    // ── SOLID ROCKET ──
    void StepSolid(float dt, long now, double freq)
    {
        var n = _noz;
        float full = n?.Row != null && n.Row.Thrust > 0f ? n.Row.Thrust : 0f;
        ExhaustLaw.SolidIn i = default;
        i.Ratio = DevThrust >= 0f ? DevThrust : (full > 0f ? n.ThrustNow / full : 0f);
        i.Pressure = DevPressure >= 0f ? DevPressure : (n != null ? n.PressureRatio : 1f);
        i.Scale = _row.ExitRadius / ExhaustBlocks.RefRadius;
        i.Speed = Speed(now, freq);
        ExhaustLaw.Solid(ref _ss, in i, dt, ref _so);
        bool lit = _so.Plume > 0.002f || _so.Smoke > 0.002f || _so.Balloon > 0.002f;
        _lsn = _row.Name; _lsi = i; _lso = _so; _lrs = _ss.Sputtering;   // (the readout's: plain copies)
        if (!Hold(lit, dt, now, freq)) return;
        float sc = i.Scale, flick = ExhaustLaw.Flicker(now / freq, _seed);
        if (ExhaustParticles.Alive(_main))
        {
            ExhaustParticles.Time(_main, _so.Plume * flick);
            ExhaustParticles.Params(_main, sc, MathF.Max(0.05f, _so.PlumeLength / ExhaustBlocks.SolidLenAuthored), 1f, 1f, 1f, 1f, _so.CoreAlpha);
        }
        if (ExhaustParticles.Alive(_balloon))
        {
            ExhaustParticles.Time(_balloon, _so.Balloon * flick);
            if (_so.Balloon > 0f) ExhaustParticles.Params(_balloon, sc * _so.BalloonScale, 1f, 1f, 1f, 1f, 1f, 1f);
        }
        if (ExhaustParticles.Alive(_smoke))
        {
            ExhaustParticles.Time(_smoke, _so.Smoke);
            if (_so.Smoke > 0f) ExhaustParticles.Params(_smoke, sc, 1f, _so.SmokeStretch, 1f, 1f, 1f, _so.SmokeAlpha);
        }
    }

    /// <summary>The grid's speed (m/s): its aero record's (the client copy's sim), else 0.</summary>
    float Speed(long now, double freq)
    {
        if (_aero == null || now >= _placeAt) _aero = Entity.GetTopLevelParent()?.TryGet<AeroGridComponent>();
        var a = _aero;
        return a != null && float.IsFinite(a.LastSpeed) ? a.LastSpeed : 0f;
    }

    // ── the effects: spawned on the first light, held, stopped / resumed, disposed ──

    /// <summary>The effects ready for a lit engine (spawned / resumed, placed); dark: stopped, disposed after IdleDisposeSeconds.
    /// True: drive them this frame.</summary>
    bool Hold(bool lit, float dt, long now, double freq)
    {
        if (!lit)
        {
            if (!_holding) return false;
            _dark += dt;
            if (_dark >= IdleDisposeSeconds) { EndAll(); return false; }
            // (the last frame's strengths written as 0 first - then emission stopped: what is in flight lives out)
            if (_dark > 0.25f) { Stop(_main); Stop(_balloon); Stop(_smoke); for (int k = 0; k < _knots.Count; k++) Stop(_knots[k]); return false; }
            return true;
        }
        _dark = 0f;
        if (!_holding || Dead())
        {
            if (now < _retryAt) return false;
            if (!Spawn(now, freq)) { _retryAt = now + (long)(freq * 2.0); return false; }
        }
        Resume(_main); Resume(_balloon); Resume(_smoke); for (int k = 0; k < _knots.Count; k++) Resume(_knots[k]);
        if (now >= _placeAt || !_gridIsRoot) Replace(now, freq);
        return true;
    }

    static void Stop(ExhaustSlot s) { if (ExhaustParticles.Alive(s)) { ExhaustParticles.Time(s, 0f); ExhaustParticles.Stop(s); } }
    static void Resume(ExhaustSlot s) { if (ExhaustParticles.Alive(s)) ExhaustParticles.Resume(s); }
    /// <summary>Its main effect was disposed by the game (or never made): spawn again.</summary>
    bool Dead() => !ExhaustParticles.Alive(_main);

    /// <summary>The block's pose in its render root's frame (the vanilla flame's: the block's world transform relative to the grid's).</summary>
    bool Pose()
    {
        var cb = Entity.TryGet<CubeBlockComponent>();
        var top = Entity.GetTopLevelParent();
        var grid = cb?.Grid?.Entity ?? top;
        if (grid == null || !GridPhysics.Alive(grid.Data)) return false;
        if (!ReferenceEquals(grid, _grid) || _parent == null)
        {
            _grid = grid; _parent = EntryFxBridge.RenderParent(grid);
            if (_parent == null && top != null && !ReferenceEquals(top, grid)) { _grid = top; _parent = EntryFxBridge.RenderParent(top); }
            _gridIsRoot = ReferenceEquals(_grid, cb?.Grid?.Entity ?? top);
        }
        if (_parent == null) return false;
        var rel = (RelativeTransform)WorldTransform.GetRelativeTransform(Entity.Data.GetWorldTransform(), _grid.Data.GetWorldTransform());
        _bp = rel.Position; _bq = rel.Orientation;
        return true;
    }

    /// <summary>A point of the block's model space in the render root's frame.</summary>
    Vector3 Place(Vector3 local) => _bp + _bq * local;

    void Replace(long now, double freq)
    {
        _placeAt = now + (long)(freq * PlaceEvery);
        if (!Pose()) return;
        var rot = _bq * _row.Turn;
        var at = Place(_row.Exhaust);
        if (ExhaustParticles.Alive(_main)) ExhaustParticles.Move(_main, at, rot);
        if (ExhaustParticles.Alive(_balloon)) ExhaustParticles.Move(_balloon, at, rot);
        if (ExhaustParticles.Alive(_smoke)) ExhaustParticles.Move(_smoke, at, rot);
    }

    bool Spawn(long now, double freq)
    {
        EndAll();
        if (System.Threading.Volatile.Read(ref Active) >= MaxEngines) { Refused++; Status = $"{MaxEngines} engines already have exhaust"; return false; }
        if (!Pose()) { Status = "no render parent for " + _row.Name; return false; }
        _placeAt = now + (long)(freq * PlaceEvery);
        float sc = _row.ExitRadius / ExhaustBlocks.RefRadius;
        var rot = _bq * _row.Turn;
        var at = new RelativeTransform(Place(_row.Exhaust), rot);
        string why;
        if (_row.Kind == ExhaustBlocks.Kinds.Ramjet)
        {
            _main = ExhaustParticles.Spawn(_grid, _parent, ExhaustBlocks.RamjetFlame, at, sc, 1f, 1f, 1f, 1f, 1f, 1f, out why);
            if (_main == null) { Status = $"{_row.Name}: flame: {why}"; return false; }
            for (int k = 0; k < ExhaustLaw.DiamondMax; k++)
            {
                var kat = new RelativeTransform(Place(_row.Exhaust + _row.Axis * (_row.ExitRadius * (ExhaustLaw.DiamondFirst + k * ExhaustLaw.DiamondSpace0))), rot);
                var kn = ExhaustParticles.Spawn(_grid, _parent, ExhaustBlocks.RamjetDiamond, kat, sc, 1f, 1f, 1f, 1f, 1f, 1f, out why);
                if (kn == null) { Status = $"{_row.Name}: diamond {k}: {why}"; break; }
                _knots.Add(kn);
            }
        }
        else
        {
            _main = ExhaustParticles.Spawn(_grid, _parent, ExhaustBlocks.SolidPlume, at, sc, 1f, 1f, 1f, 1f, 1f, 1f, out why);
            if (_main == null) { Status = $"{_row.Name}: plume: {why}"; return false; }
            _balloon = ExhaustParticles.Spawn(_grid, _parent, ExhaustBlocks.SolidBalloon, at, sc, 1f, 1f, 1f, 1f, 1f, 1f, out why);
            if (_balloon == null) Status = $"{_row.Name}: balloon: {why}";
            if (System.Threading.Interlocked.Increment(ref Smokers) <= MaxSmokers)
            {
                _smoke = ExhaustParticles.Spawn(_grid, _parent, ExhaustBlocks.SolidSmoke, at, sc, 1f, 1f, 1f, 1f, 1f, 1f, out why);
                if (_smoke == null) { Status = $"{_row.Name}: smoke: {why}"; System.Threading.Interlocked.Decrement(ref Smokers); }
                else _smoking = true;
            }
            else { System.Threading.Interlocked.Decrement(ref Smokers); Status = $"{MaxSmokers} smoke trails already: {_row.Name} without one"; }
        }
        _holding = true;
        System.Threading.Interlocked.Increment(ref Active);
        if (_main != null && Status.StartsWith(_row.Name, StringComparison.Ordinal) == false) Status = $"{_row.Name}: exhaust up";
        return true;
    }

    /// <summary>Every effect ended (disposed once its particles die - a smoke trail fades on its own) and its caps freed.</summary>
    void EndAll()
    {
        ExhaustParticles.End(_main); ExhaustParticles.End(_balloon); ExhaustParticles.End(_smoke);
        for (int k = 0; k < _knots.Count; k++) ExhaustParticles.End(_knots[k]);
        _knots.Clear(); _main = null; _balloon = null; _smoke = null;
        if (_smoking) { _smoking = false; System.Threading.Interlocked.Decrement(ref Smokers); }
        if (_holding) { _holding = false; System.Threading.Interlocked.Decrement(ref Active); }
        _dark = 0f;
    }

    /// <summary>Harness (aeroget AeroExhaustComponent.Summary): counts, caps, the API, the last outputs.</summary>
    public static string Summary =>
        $"exhaust {(Enabled ? "on" : "off")}: {Seen} engines seen, {Active}/{MaxEngines} with effects, {Smokers}/{MaxSmokers} smoking, {Refused} refused; api {ExhaustParticles.Why}; " +
        $"spawns {ExhaustParticles.Spawns} (failed {ExhaustParticles.SpawnFails}), writes: time {ExhaustParticles.TimeWrites} params {ExhaustParticles.ParamWrites} moves {ExhaustParticles.Moves}; " +
        $"status {Status} | {LastRamjet} | {LastSolid}";
}
