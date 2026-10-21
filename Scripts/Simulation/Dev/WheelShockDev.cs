#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// DEV (harness only, tools/ingame/wheelshock_test.sh): A TOUCHDOWN AT A KNOWN SINK RATE and its per-tick record, for a named grid on
/// its wheels. Run in the wheel shocks' apply job (AeroWheelShockComponent.ApplyAll: the server's step, single threaded, before the
/// physics step). Nothing happens unless DevDrop is bumped.
///   aeroset WheelShockDev.DevGrid Tupolev_Tu-134_Big   ('_' for spaces; "" the first grid with shocked wheels)
///   aeroset WheelShockDev.DevSink 3 ; DevForward 45 ; DevRise 1.2 ; DevNose 1 (the grid's nose: 1 +Z, -1 -Z, local)
///   aeroset WheelShockDev.DevDrop N   (any new value: a drop)  ->  aeroget WheelShockDev.DevPhase / DevResult
/// THE DROP: the grid (and its wheels' bodies, at the grid's velocity at their centres) thrown up to rise DevRise m and forward at
/// DevForward along its nose, level (no spin); from the top of the rise its velocity HELD at DevSink down and DevForward forward,
/// level, until any of its wheels or its hull touches - then let go and RECORDED for DevRecordSeconds: the touch speed, per wheel the
/// deepest compression of its stroke and the ticks at the limit (bottomed: 98 % or more), hull contacts (the grid's own contact list
/// against anything but its wheels), the vertical speed's highest rebound, bounces (every wheel off the ground 3+ ticks after the
/// touch), the settle time (vertical speed under 0.1 m/s and every wheel's compression rate under 0.05 m/s for 0.5 s), the strut's
/// peak force in rated weights, and the damage (the blocks' integrity summed, before / after; blocks lost). The hold IS a cheat - a
/// test fixture, harness only, like DevHop was for the gear - never in play.
/// </summary>
public static class WheelShockDev
{
    public static string DevGrid = "";
    public static float DevSink = 3f, DevForward = 0f, DevRise = 1.2f, DevRecordSeconds = 4f;
    public static int DevNose = 1;
    public static int DevDrop;
    public static string DevPhase = "idle", DevResult = "none";

    static int _seen, _phase;   // (0 idle, 1 rising, 2 held down, 3 recording)
    static Entity _grid;
    static int _ticks, _touchTick, _calm, _off, _bounces, _hull, _blocks0;
    static float _touchV, _rebound, _settle, _peakF, _maxSink;
    static double _integ0;
    static readonly Dictionary<AeroWheelShockComponent, (float maxU, int bottom)> _wheels = new();

    /// <summary>A drop asked or running (the wheels' jobs arm the apply job every tick then).</summary>
    internal static bool Active => _phase != 0 || DevDrop != _seen || _parkedAsked;

    internal static void Step(List<AeroWheelShockComponent> all)
    {
        if (_parkedAsked) Parked(all);
        if (DevDrop != _seen) { _seen = DevDrop; Begin(all); }
        if (_phase == 0) return;
        if (_grid == null || !GridPhysics.Alive(_grid.Data)) { End("the grid is gone"); return; }
        var gd = _grid.Data;
        ref RigidBodyData rb = ref gd.TryGetWritePtr<RigidBodyData>();
        if (Unsafe.IsNullRef(in rb) || !gd.TryGet<RigidBodyMassProperties>(out var mass)) { End("no body"); return; }
        var wt = gd.GetWorldTransform();
        Vector3 g = GridPhysics.GetGravityDirection(gd);
        float gl = g.Length();
        if (!(gl > 0.1f)) { End("no gravity"); return; }
        Vector3 up = -g / gl;
        Vector3 nose = Vector3.Transform(new Vector3(0, 0, DevNose >= 0 ? 1 : -1), wt.Orientation);
        Vector3 fwd = nose - up * Vector3.Dot(nose, up);
        fwd = fwd.LengthSquared() > 1e-4f ? Vector3.Normalize(fwd) * DevForward : Vector3.Zero;
        bool anyWheel = false, anyHull = HullContact(gd, all);
        foreach (var s in all) if (ReferenceEquals(s.Grid, _grid) && s.Contact) anyWheel = true;
        float vUp = Vector3.Dot(rb.LinearVelocity, up);
        _ticks++;
        switch (_phase)
        {
            case 1:   // rising: free until the top
                if (vUp <= 0f) { _phase = 2; DevPhase = "held down"; }
                if (_ticks > 600) End("never topped out");
                break;
            case 2:   // held at the sink rate, level, until it touches
                if (anyWheel || anyHull)
                {
                    _phase = 3; DevPhase = "recording"; _touchTick = _ticks; _touchV = -vUp;
                    break;
                }
                Hold(ref rb, in mass, in wt, all, fwd - up * DevSink);
                if (_ticks > 1200) End("never touched (20 s held)");
                break;
            case 3:   // free: the record
            {
                float t = (_ticks - _touchTick) * AeroPhysics.Dt;
                _rebound = MathF.Max(_rebound, vUp); _maxSink = MathF.Max(_maxSink, -vUp);
                if (anyHull) _hull++;
                if (!anyWheel) { _off++; if (_off == 3) _bounces++; } else _off = 0;
                bool still = MathF.Abs(vUp) < 0.1f && anyWheel;
                foreach (var s in all)
                {
                    if (!ReferenceEquals(s.Grid, _grid) || !s.Rating.Valid) continue;
                    float u = s.X / s.Rating.Stroke;
                    _wheels.TryGetValue(s, out var w);
                    _wheels[s] = (MathF.Max(w.maxU, u), w.bottom + (u >= 0.98f ? 1 : 0));
                    if (MathF.Abs(s.V) >= 0.05f) still = false;
                    _peakF = MathF.Max(_peakF, s.F / s.Rating.RatedWeight);
                }
                _calm = still ? _calm + 1 : 0;
                if (_calm == 30 && float.IsNaN(_settle)) _settle = t - 0.5f;
                if (t >= DevRecordSeconds) End(null);
                break;
            }
        }
    }

    /// <summary>The named grid's wheels at rest (harness: read it, read it again - built on the server step after it was asked):
    /// "wheels N ground G maxcomp U" - how many shocked wheels, how many on the ground, the deepest compression of a stroke.</summary>
    public static string DevParked { get { _parkedAsked = true; return _parked; } }
    static volatile bool _parkedAsked; static string _parked = "asked";

    static void Parked(List<AeroWheelShockComponent> all)
    {
        _parkedAsked = false;
        string want = (DevGrid ?? "").Replace('_', ' ').Trim();
        int n = 0, ground = 0; float maxU = 0f; Entity grid = null;
        foreach (var s in all)
        {
            if (s.Grid == null || !s.Rating.Valid) continue;
            if (grid == null && (want.Length == 0 || string.Equals(Name(s.Grid), want, StringComparison.OrdinalIgnoreCase))) grid = s.Grid;
            if (!ReferenceEquals(s.Grid, grid)) continue;
            n++; if (s.Contact) ground++; maxU = MathF.Max(maxU, s.X / s.Rating.Stroke);
        }
        _parked = grid == null ? $"no grid '{want}'" : $"'{Name(grid)}' wheels {n} ground {ground} maxcomp {maxU:F2} rated {(n > 0 ? all.Find(x => ReferenceEquals(x.Grid, grid) && x.Rating.Valid).Rating.RatedMass / 1000f : 0f):F1} t";
    }

    static void Begin(List<AeroWheelShockComponent> all)
    {
        _grid = null;
        string want = (DevGrid ?? "").Replace('_', ' ').Trim();
        foreach (var s in all)
        {
            if (s.Grid == null) continue;
            if (want.Length == 0 || string.Equals(Name(s.Grid), want, StringComparison.OrdinalIgnoreCase)) { _grid = s.Grid; break; }
        }
        if (_grid == null) { DevResult = $"drop {_seen}: no grid '{want}' with shocked wheels"; DevPhase = "idle"; _phase = 0; return; }
        _wheels.Clear(); _ticks = 0; _touchTick = 0; _calm = 0; _off = 0; _bounces = 0; _hull = 0;
        _touchV = 0f; _rebound = 0f; _settle = float.NaN; _peakF = 0f; _maxSink = 0f;
        (_integ0, _blocks0) = Integrity(_grid);
        var gd = _grid.Data;
        ref RigidBodyData rb = ref gd.TryGetWritePtr<RigidBodyData>();
        if (Unsafe.IsNullRef(in rb) || !gd.TryGet<RigidBodyMassProperties>(out var mass)) { DevResult = "no body"; return; }
        var wt = gd.GetWorldTransform();
        Vector3 g = GridPhysics.GetGravityDirection(gd); float gl = g.Length();
        if (!(gl > 0.1f)) { DevResult = "no gravity"; return; }
        Vector3 up = -g / gl;
        Vector3 nose = Vector3.Transform(new Vector3(0, 0, DevNose >= 0 ? 1 : -1), wt.Orientation);
        Vector3 fwd = nose - up * Vector3.Dot(nose, up);
        fwd = fwd.LengthSquared() > 1e-4f ? Vector3.Normalize(fwd) * DevForward : Vector3.Zero;
        Hold(ref rb, in mass, in wt, all, fwd + up * MathF.Sqrt(2f * gl * MathF.Max(0.05f, DevRise)));
        _phase = 1; DevPhase = "rising";
        DevResult = $"drop {_seen}: running ({Name(_grid)}, sink {DevSink} m/s, forward {DevForward} m/s)";
    }

    /// <summary>The grid's velocity set, level (no spin), and its wheels' bodies at the grid's velocity at their centres.</summary>
    static void Hold(ref RigidBodyData rb, in RigidBodyMassProperties mass, in WorldTransform wt, List<AeroWheelShockComponent> all, Vector3 v)
    {
        rb.LinearVelocity = v; rb.AngularVelocity = Vector3.Zero;
        foreach (var s in all)
        {
            if (!ReferenceEquals(s.Grid, _grid)) continue;
            var w = s.Module?.WheelEntity;
            if (w == null || !GridPhysics.Alive(w.Data)) continue;
            ref RigidBodyData wr = ref w.Data.TryGetWritePtr<RigidBodyData>();
            if (Unsafe.IsNullRef(in wr)) continue;
            wr.LinearVelocity = v; wr.AngularVelocity = Vector3.Zero;
        }
    }

    static bool HullContact(DEntityContext gd, List<AeroWheelShockComponent> all)
    {
        if (!gd.TryGet<ContactImpulseData>(out var cid)) return false;
        foreach (var c in cid.ImpulsesFromLastFrame)
        {
            if (!(c.ImpulseMagnitude > 0f)) continue;
            bool own = false;
            foreach (var s in all)
            {
                var w = s.Module?.WheelEntity;
                if (ReferenceEquals(s.Grid, _grid) && w != null && w.DEntity.Equals(c.OtherBody)) { own = true; break; }
            }
            if (!own) return true;
        }
        return false;
    }

    static void End(string why)
    {
        _phase = 0; DevPhase = "idle";
        if (why != null) { DevResult = $"drop {_seen}: aborted - {why}"; return; }
        var (integ, blocks) = Integrity(_grid);
        var sb = new System.Text.StringBuilder();
        int bottomed = 0; float maxU = 0f;
        foreach (var kv in _wheels) { if (kv.Value.bottom > 0) bottomed++; maxU = MathF.Max(maxU, kv.Value.maxU); }
        sb.Append($"drop {_seen}: '{Name(_grid)}' mode {WheelShockSettings.Mode} touch {_touchV:F2} m/s forward {DevForward:F0} | maxcomp {maxU:F2} bottomed {bottomed} hull {_hull} rebound {_rebound:F2} bounces {_bounces} settle {(float.IsNaN(_settle) ? -1f : _settle):F2} peakF {_peakF:F2} W");
        sb.Append($" | damage {(_integ0 > 0 ? 1.0 - integ / _integ0 : 0):F4} lost {_blocks0 - blocks} blocks");
        foreach (var kv in _wheels) sb.Append($" | wheel {kv.Key.Rating.Diameter:F1} m comp {kv.Value.maxU:F2} bottom {kv.Value.bottom}");
        DevResult = sb.ToString();
        Log.Default?.Info("[AERO-SHOCK] " + DevResult);
    }

    static (double integ, int blocks) Integrity(Entity grid)
    {
        double integ = 0; int n = 0;
        var h = grid?.TryGet<HierarchyComponent>();
        if (h?.Children == null) return (0, 0);
        foreach (var e in h.Children)
        {
            var cb = e?.TryGet<CubeBlockComponent>();
            if (cb != null) { integ += cb.HealthIntegrity; n++; }
        }
        return (integ, n);
    }

    static string Name(Entity grid)
    {
        var cg = grid?.TryGet<CubeGridComponent>();
        if (cg == null) return "";
        try { return cg.DisplayName.EvaluateText() ?? ""; } catch { return ""; }   // (dev, once a drop)
    }
}
