#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Physics.Data;
using Keen.VRage.Core.Game.GameSystems.Queries;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Queries;

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
/// MIDAIR (2026-10-21, the user: "spawn it midair and with relative velocities, measure change in momentum"; "no aero interference"):
/// DevApproach 1 - a rig above a STATIC deck high over the terrain: no rise; first FreeTicks free ticks (the gravity the bodies
/// really feel measured: the rig's whole momentum - the grid and its wheel bodies, internal forces cancel - over M dt), then the
/// approach: down fast while the lowest tyre is well clear of the deck (the grid's ground ray - it hits the deck), the last half
/// metre at exactly DevSink and DevForward, level - then free and recorded. DevNoAero 1: no air on the rig while it drops
/// (AeroGridComponent.DevNoAero: no model force or torque, no structure air loads, the game's wind push taken back; the shocks, the
/// gears and the ground ray stay on) - logged per tick (must be 0) and in the ledger. THE MOMENTUM LEDGER (DevImpact): per tick the
/// rig's linear momentum P (sum m v over the grid and its wheels, their real masses), the grid's angular momentum (I w, world); the
/// contact force F = dP/dt - M g - F_aero; over the impact (the first lobe of the normal force above 10 % of its peak) and the whole
/// window: dP along the normal, the gravity's impulse M g T, the contact impulse, the check M (v_in + v_out) against dP, the peak
/// force and its duration, the tangential impulse, and - independently - the rig's own contact impulses against the deck
/// (ContactImpulseData, along the normal).
/// </summary>
public static class WheelShockDev
{
    public static string DevGrid = "";
    public static float DevSink = 3f, DevForward = 0f, DevRise = 1.2f, DevRecordSeconds = 4f;
    public static int DevNose = 1;
    /// <summary>Midair drops: the approach onto a static deck (no rise), and no air on the rig while it drops.</summary>
    public static bool DevApproach, DevNoAero;
    /// <summary>The last drop's momentum ledger (midair or not): one line.</summary>
    public static string DevImpact = "none";
    public const int FreeTicks = 12;
    /// <summary>THE PER-TICK TRACE (2026-10-21, the shocks' diagnosis): every tick of the approach's last 8 and of the record's first
    /// DevTraceTicks, logged at the drop's end ("[AERO-SHOCK-TRACE] dN ..."): the grid's vertical speed, its hull's contact impulse
    /// against the deck, and per wheel the compression, its rate, the strut's force and its force before the rate cap (rated
    /// weights), the contact, the wheel body's and the ship's (at the mount) velocities along the axis, the wheel's own contact
    /// impulse against the deck (N s, along up, the previous step's), armed, the constraint's factors written.</summary>
    public static bool DevTrace;
    public static int DevTraceTicks = 100;
    /// <summary>Diagnosis overrides (NaN / 0: none): the wheel constraint's strength / damping factor written while the shocks own it,
    /// and its lower suspension limit (negative: the wheel hangs lower).</summary>
    public static float DevConstraintS = float.NaN, DevConstraintD = float.NaN, DevMinLimit = 0f;
    static readonly List<string> _trace = new();
    static int _traceRec;
    public static int DevDrop;
    public static string DevPhase = "idle", DevResult = "none";

    static int _seen, _phase;   // (0 idle, 1 rising / free, 2 held down / approach, 3 recording, 4 lifted clear - midair)
    static Entity _grid;
    static int _ticks, _touchTick, _calm, _off, _bounces, _hull, _blocks0;
    static float _touchV, _rebound, _settle, _peakF, _maxSink;
    static double _integ0;
    static readonly Dictionary<AeroWheelShockComponent, (float maxU, int bottom)> _wheels = new();

    /// <summary>A drop asked or running (the wheels' jobs arm the apply job every tick then).</summary>
    internal static bool Active => _phase != 0 || DevDrop != _seen || _parkedAsked;

    // -- the momentum ledger (2026-10-21) --
    static AeroGridComponent _aero;
    static float _M; static Vector3 _g; static int _gN; static Vector3 _pPrev; static bool _hasPrev; static Vector3 _fAeroPrev;
    static readonly List<float> _fn = new(), _vUp = new(), _jcid = new();
    static readonly List<Vector3> _ft = new(), _p = new();
    static float _lMax, _wMax, _aeroMax, _windMax, _lastClear = float.NaN;
    static readonly Dictionary<AeroGearComponent, (float tilt0, float tilt, Vector3 rate)> _feet = new();

    /// <summary>The rig's linear momentum (the grid and its wheel bodies), its mass, the grid's angular momentum (world) and rate.</summary>
    static bool Momentum(List<AeroWheelShockComponent> all, out Vector3 p, out float m, out Vector3 l, out float w)
    {
        p = Vector3.Zero; m = 0f; l = Vector3.Zero; w = 0f;
        var gd = _grid.Data;
        if (!gd.TryGet<RigidBodyData>(out var rb) || !GridPhysics.TryGetMassAndInertia(gd, out float gm, out _, out var invI, out var major) || !(gm > 0f)) return false;
        p = rb.LinearVelocity * gm; m = gm; w = rb.AngularVelocity.Length();
        var rot = Quaternion.Normalize(gd.GetWorldTransform().Orientation * major);
        Vector3 wl = Vector3.Transform(rb.AngularVelocity, Quaternion.Inverse(rot));
        Vector3 iw = new Vector3(invI.X > 0f ? wl.X / invI.X : 0f, invI.Y > 0f ? wl.Y / invI.Y : 0f, invI.Z > 0f ? wl.Z / invI.Z : 0f);
        l = Vector3.Transform(iw, rot);
        foreach (var s in all)
        {
            if (!ReferenceEquals(s.Grid, _grid)) continue;
            var we = s.Module?.WheelEntity;
            if (we == null || !GridPhysics.Alive(we.Data) || !we.Data.TryGet<RigidBodyData>(out var wr) || !we.Data.TryGet<RigidBodyMassProperties>(out var wm) || !(wm.InvMass > 0f)) continue;
            p += wr.LinearVelocity / wm.InvMass; m += 1f / wm.InvMass;
        }
        return true;
    }

    /// <summary>This tick's contact impulses of the rig's bodies against anything not the rig (the deck): their sum along up (N s).</summary>
    static float ContactUp(List<AeroWheelShockComponent> all, Vector3 up)
    {
        float j = Cid(_grid.Data, all, up);
        foreach (var s in all)
        {
            if (!ReferenceEquals(s.Grid, _grid)) continue;
            var we = s.Module?.WheelEntity;
            if (we != null && GridPhysics.Alive(we.Data)) j += Cid(we.Data, all, up);
        }
        return j;
    }
    static float Cid(DEntityContext d, List<AeroWheelShockComponent> all, Vector3 up)
    {
        if (!d.TryGet<ContactImpulseData>(out var cid)) return 0f;
        float j = 0f;
        foreach (var c in cid.ImpulsesFromLastFrame)
        {
            if (!(c.ImpulseMagnitude > 0f) || c.OtherBody.Equals(_grid.DEntity)) continue;
            bool own = false;
            foreach (var s in all) { var w = s.Module?.WheelEntity; if (ReferenceEquals(s.Grid, _grid) && w != null && w.DEntity.Equals(c.OtherBody)) { own = true; break; } }
            if (!own) j += c.ImpulseMagnitude * MathF.Abs(Vector3.Dot(c.Normal, up));
        }
        return j;
    }

    /// <summary>The lowest tyre's clearance over the deck (m; NaN: nothing under it): a ray straight down from each wheel's centre,
    /// the rig's own bodies ignored, less the wheel's radius - cast every call (dev: the approach only). (The grid's own ground ray -
    /// aero.GroundHeight, from the grid's origin, refreshed every 3-10 ticks - read metres high over the deck in the first midair run:
    /// the approach came in at 3.7-8 m/s where 1 and 3 were asked.)</summary>
    static readonly GridPhysics.GroundProbe _ray = new();
    const float RayLength = 60f;
    /// <summary>Midair: the deck by name (DevDeck: WSDECK - build_wheelbrick.py's, its top face DeckTop m over its origin), found at
    /// each drop's start; the clearance is the wheels' height over its top plane (the rays did not see it in game: a ray from a wheel
    /// with the rig's own bodies ignored found nothing in 60 m - 2026-10-21).</summary>
    public static string DevDeck = "WSDECK";
    public const float DeckTop = 1.25f;
    static Entity _deck;
    /// <summary>One closest-hit ray, the rig's own bodies (the grid and its wheels) ignored - and only those: the engine's
    /// GetDirectlyConnectedBodies (the grid ground ray's ignore list) took in the deck the rig was resting on, so the lift never saw
    /// it (2026-10-21, "never lifted clear (clear NaN)"). The hit distance, or -1.</summary>
    static float Ray(List<AeroWheelShockComponent> all, Vector3D from, Vector3 ray)
    {
        var physics = GridPhysics.Physics;
        if (physics == null) return -1f;
        _ray.Hits.Clear(); _ray.Ignore.Clear();
        _ray.Ignore.Add(_grid.DEntity);
        foreach (var s in all)
        {
            var we = s.Module?.WheelEntity;
            if (ReferenceEquals(s.Grid, _grid) && we != null && GridPhysics.Alive(we.Data)) _ray.Ignore.Add(we.DEntity);
        }
        var args = new RayCastArgs(in from, ray);
        args.IgnoredEntities = _ray.Ignore;
        physics.CastRay(_ray.Hits, in args, CollisionPreset.Closest);
        return _ray.Hits.Count > 0 ? (float)(_ray.Hits[0].Fraction * ray.Length()) : -1f;
    }
    static float Clearance(List<AeroWheelShockComponent> all, in WorldTransform gwt, Vector3 up)
    {
        float h = float.MaxValue;
        foreach (var s in all)
        {
            if (!ReferenceEquals(s.Grid, _grid) || s.Module == null) continue;
            var we = s.Module.WheelEntity;
            if (we == null || !GridPhysics.Alive(we.Data)) continue;
            Vector3D wp = we.Data.GetWorldTransform().Position;
            float d;
            if (_deck != null && GridPhysics.Alive(_deck.Data))
            {
                // (the deck's top plane: its origin + DeckTop along its own up - the deck is static and flat)
                var dwt = _deck.Data.GetWorldTransform();
                Vector3 dup = Vector3.Transform(Vector3.Up, dwt.Orientation);
                d = (float)Vector3D.Dot(wp - dwt.Position, (Vector3D)dup) - DeckTop;
            }
            else d = Ray(all, wp, -up * RayLength);
            if (d >= 0f || _deck != null) h = MathF.Min(h, d - s.Module.WheelRadius);
        }
        return h < float.MaxValue ? h : float.NaN;
    }

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
        float dt = AeroPhysics.Dt;
        bool hasP = Momentum(all, out Vector3 P, out float M, out Vector3 Lw, out float wNow);
        if (DevTrace && (_phase == 2 || _phase == 3)) TraceTick(all, up, vUp);
        if (_aero != null && _phase == 3) { _aeroMax = MathF.Max(_aeroMax, _aero.LastAppliedForceW.Length()); _windMax = MathF.Max(_windMax, _aero.LastWindUndone.Length()); }
        switch (_phase)
        {
            case 4:   // midair, resting on the deck: lifted (4 m/s up, level) until the lowest tyre is 1.5 m clear - then free
            {
                float h = Clearance(all, in wt, up); _lastClear = h;
                if ((_ticks & 7) == 0) { int nw = 0; foreach (var s in all) if (ReferenceEquals(s.Grid, _grid) && s.Module?.WheelEntity != null) nw++; DevPhase = $"lift clear {h:F2} m wheels {nw} deck {(_deck != null ? 1 : 0)} contact {(anyWheel ? 1 : 0)}{(anyHull ? 1 : 0)}"; }
                if (float.IsFinite(h) && h >= 1.5f && !anyWheel && !anyHull) { _phase = 1; _ticks = 0; DevPhase = "free"; break; }
                Hold(ref rb, in mass, in wt, all, up * 4f);
                if (_ticks > 300) End("never lifted clear");
                break;
            }
            case 1:   // free: rising to the top (a drop) - or FreeTicks ticks (midair); the gravity measured
                if (hasP && _hasPrev && _ticks > 2) { _g += (P - _pPrev) / (M * dt) - _fAeroPrev / M; _gN++; }
                if (DevApproach ? _ticks >= FreeTicks : vUp <= 0f) { _phase = 2; DevPhase = DevApproach ? "approach" : "held down"; }
                if (_ticks > 600) End("never topped out");
                break;
            case 2:   // held at the sink rate, level, until it touches (midair: down faster while the tyres are well clear)
            {
                if (anyWheel || anyHull)
                {
                    _phase = 3; DevPhase = "recording"; _touchTick = _ticks; _touchV = -Vector3.Dot(_pPrev, up) / MathF.Max(_M, 1f);
                    goto case 3;
                }
                float sink = DevSink;
                if (DevApproach)
                {
                    float h = Clearance(all, in wt, up); _lastClear = h;
                    if ((_ticks & 7) == 0) DevPhase = $"approach clear {h:F2} m vUp {vUp:F2} contact {(anyWheel ? 1 : 0)}{(anyHull ? 1 : 0)}";
                    if (float.IsFinite(h) && h > 0.6f) sink = MathF.Min(8f, DevSink + 2f * (h - 0.5f));
                }
                Vector3 vh = fwd - up * sink;
                Hold(ref rb, in mass, in wt, all, vh);
                _M = M; _pPrev = vh * M; _hasPrev = hasP; _fAeroPrev = Vector3.Zero;
                if (_ticks > 1200) End("never touched (20 s held)");
                return;
            }
            case 3:   // free: the record
            {
                if (hasP)
                {
                    Vector3 gEff = _gN > 0 ? _g / _gN : g;
                    Vector3 f = (P - _pPrev) / dt - M * gEff - _fAeroPrev;
                    float fn = Vector3.Dot(f, up);
                    _fn.Add(fn); _ft.Add(f - up * fn); _p.Add(P); _vUp.Add(Vector3.Dot(P, up) / M); _jcid.Add(ContactUp(all, up));
                    _lMax = MathF.Max(_lMax, Lw.Length()); _wMax = MathF.Max(_wMax, wNow); _M = M;
                }
                foreach (var s in all)
                {
                    var gr = s.Gear;
                    if (!ReferenceEquals(s.Grid, _grid) || gr == null || !gr.FootAttitude(out float tl, out _) || !gr.FootRates(out var rr)) continue;
                    (float tilt0, float tilt, Vector3 rate) e = _feet.TryGetValue(gr, out var fe) ? fe : (tl, 0f, Vector3.Zero);
                    _feet[gr] = (e.tilt0, MathF.Max(e.tilt, MathF.Abs(tl - e.tilt0)), Vector3.Max(e.rate, Vector3.Abs(rr)));
                }
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
        if (hasP) { _pPrev = P; _M = M; _hasPrev = true; _fAeroPrev = _aero != null ? _aero.LastAppliedForceW : Vector3.Zero; }
    }

    static void TraceTick(List<AeroWheelShockComponent> all, Vector3 up, float vUp)
    {
        if (_phase == 2) { if (_trace.Count >= 8) _trace.RemoveAt(0); }
        else if (_traceRec++ >= DevTraceTicks) return;
        var sb = new System.Text.StringBuilder(260);
        sb.Append(_phase == 2 ? "a " : "r ").Append(_phase == 3 ? _ticks - _touchTick : _ticks);
        sb.Append(($" vu {vUp:F3} hull {Cid(_grid.Data, all, up):F0}"));
        foreach (var s in all)
        {
            if (!ReferenceEquals(s.Grid, _grid) || !s.Rating.Valid) continue;
            var we = s.Module?.WheelEntity;
            float jc = we != null && GridPhysics.Alive(we.Data) ? Cid(we.Data, all, up) : 0f;
            float w = s.Rating.RatedWeight;
            sb.Append(($" | {s.X:F4} {s.V:F3} {s.F / w:F3} {s.FRaw / w:F3} {(s.Contact ? 1 : 0)} {s.Wv:F3} {s.Sv:F3} {jc:F0} {(s.ShockArmed ? 1 : 0)} {s.WrittenS:F2} {s.WrittenD:F2}"));
        }
        _trace.Add(sb.ToString());
    }

    /// <summary>The ledger. THE IMPACT: from the touch to the first tick the rig's normal velocity is back to zero or up (the
    /// compression - all of the sink taken out): its length T, the peak normal contact force and how long it stays over half of it,
    /// dPn (the momentum's change along the normal: M (v_in + v_out) - the rig's own velocities), the gravity's impulse M g T, the
    /// contact impulse from the momentum Jn = dPn + M g T (the aero's taken out - zero here), the tangential one Jt, and - an
    /// independent measure - Jcid: the rig's own contact impulses against the deck (ContactImpulseData, along the normal). Then the
    /// whole recorded window the same way (Jn = dPn + M g T: at rest the deck carries the weight).</summary>
    static string Ledger()
    {
        int n = _fn.Count; float dt = AeroPhysics.Dt;
        if (n < 2 || _gN == 0) return "no momentum record";
        Vector3 gEff = _g / _gN; float gl = gEff.Length();
        Vector3 up = gl > 0.1f ? -gEff / gl : Vector3.UnitY;
        int i1 = 0;
        while (i1 < n - 1 && _vUp[i1] < 0f) i1++;
        float T = (i1 + 1) * dt, jn = 0f, jc = 0f, peak = 0f; Vector3 jt = Vector3.Zero; int ip = 0;
        for (int i = 0; i <= i1; i++) { jn += _fn[i] * dt; jt += _ft[i] * dt; jc += _jcid[i]; if (_fn[i] > peak) { peak = _fn[i]; ip = i; } }
        int half = 0; for (int i = 0; i <= i1; i++) if (_fn[i] > 0.5f * peak) half++;
        float vIn = _touchV, vOut = _vUp[i1];
        float dPn = Vector3.Dot(_p[i1], up) + vIn * _M, G = _M * gl * T;
        float jnAll = 0f, jcAll = 0f; for (int i = 0; i < n; i++) { jnAll += _fn[i] * dt; jcAll += _jcid[i]; }
        float Tall = n * dt, dPnAll = Vector3.Dot(_p[n - 1], up) + vIn * _M;
        return $"M {_M / 1000f:F2} t g {gl:F3} m/s2 ({_gN} free ticks) | impact {T:F3} s peakFn {peak / 1000f:F1} kN ({peak / (_M * gl):F2} W) at {ip * dt:F3} s over half {half * dt:F3} s"
             + $" | dPn {dPn / 1000f:F2} M(vin+vout) {_M * (vIn + vOut) / 1000f:F2} (vin {vIn:F2} vout {vOut:F2}) G {G / 1000f:F2} Jn {jn / 1000f:F2} Jcid {jc / 1000f:F2} Jt {jt.Length() / 1000f:F2} kNs"
             + $" | window {Tall:F2} s dPn {dPnAll / 1000f:F2} G {_M * gl * Tall / 1000f:F2} Jn {jnAll / 1000f:F2} Jcid {jcAll / 1000f:F2} kNs"
             + $" | Lmax {_lMax / 1000f:F1} kNms wmax {_wMax:F2} rad/s | aero {_aeroMax:F1} N wind {_windMax:F2} Ns";
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
        _trace.Clear(); _traceRec = 0;
        _fn.Clear(); _ft.Clear(); _p.Clear(); _vUp.Clear(); _jcid.Clear(); _feet.Clear();
        _g = Vector3.Zero; _gN = 0; _hasPrev = false; _lastClear = float.NaN; _fAeroPrev = Vector3.Zero; _lMax = _wMax = _aeroMax = _windMax = 0f; DevImpact = "running";
        _aero = _grid.TryGet<AeroGridComponent>();
        _deck = null;
        if (DevApproach && !string.IsNullOrEmpty(DevDeck))
            foreach (var e in _grid.GetSession().GetEntitiesOfType<CubeGridComponent>())
                if (string.Equals(Name(e), DevDeck, StringComparison.OrdinalIgnoreCase)) { _deck = e; break; }
        if (_aero != null) _aero.DevNoAero = DevNoAero;
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
        if (DevApproach) Level(all, in wt, up);
        if (!DevApproach) Hold(ref rb, in mass, in wt, all, fwd + up * MathF.Sqrt(2f * gl * MathF.Max(0.05f, DevRise)));
        _phase = DevApproach ? 4 : 1; DevPhase = DevApproach ? "lift" : "rising";
        string where = "";
        if (_deck != null) { var dwt = _deck.Data.GetWorldTransform(); var rel = Vector3D.Transform(wt.Position - dwt.Position, QuaternionD.Inverse((QuaternionD)dwt.Orientation)); where = $", at ({rel.X:F1}, {rel.Y:F1}, {rel.Z:F1}) m in the deck's frame, the deck's up . gravity's {Vector3.Dot(Vector3.Transform(Vector3.Up, dwt.Orientation), up):F3}"; }
        DevResult = $"drop {_seen}: running ({Name(_grid)}, sink {DevSink} m/s, forward {DevForward} m/s{where})";
    }

    /// <summary>Midair: the rig turned level (its top along the gravity's up, its heading kept) about its origin - the grid and its
    /// wheel bodies together, at rest. A rig spawned from the seated camera takes the camera's pitch (~8-11 deg); the harness's
    /// gridlevel moved a fresh grid ~0.8 km to a stale place (2026-10-21), so the drop levels it itself.</summary>
    static void Level(List<AeroWheelShockComponent> all, in WorldTransform wt, Vector3 up)
    {
        Vector3 f = Vector3.Transform(Vector3.Forward, wt.Orientation);
        Vector3 h = f - up * Vector3.Dot(f, up);
        if (h.LengthSquared() < 1e-6f) return;
        var target = Quaternion.CreateFromForwardUp(Vector3.Normalize(h), up);
        var turn = Quaternion.Normalize(target * Quaternion.Inverse(wt.Orientation));
        if (MathF.Abs(turn.W) > 0.99999f) return;
        Vector3D c = wt.Position;
        void Move(Entity e)
        {
            var t = e.Data.GetWorldTransform();
            Vector3D rel = t.Position - c;
            e.Data.SetWorldTransform(new WorldTransform(c + Vector3D.Transform(rel, (QuaternionD)turn), Quaternion.Normalize(turn * t.Orientation)));
            ref RigidBodyData rb = ref e.Data.TryGetWritePtr<RigidBodyData>();
            if (!Unsafe.IsNullRef(in rb)) { rb.LinearVelocity = Vector3.Zero; rb.AngularVelocity = Vector3.Zero; }
        }
        Move(_grid);
        foreach (var s in all)
        {
            var w = s.Module?.WheelEntity;
            if (ReferenceEquals(s.Grid, _grid) && w != null && GridPhysics.Alive(w.Data)) Move(w);
        }
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
        if (_aero != null) _aero.DevNoAero = false;   // (the air back on the rig: a drop's fixture only)
        DevImpact = why != null ? "aborted" : $"drop {_seen}: " + Ledger();
        if (why != null) { DevResult = $"drop {_seen}: aborted - {why} (clear {_lastClear:F2} m)"; return; }
        var (integ, blocks) = Integrity(_grid);
        var sb = new System.Text.StringBuilder();
        int bottomed = 0; float maxU = 0f;
        foreach (var kv in _wheels) { if (kv.Value.bottom > 0) bottomed++; maxU = MathF.Max(maxU, kv.Value.maxU); }
        sb.Append($"drop {_seen}: '{Name(_grid)}' mode {WheelShockSettings.Mode} touch {_touchV:F2} m/s forward {DevForward:F0} | maxcomp {maxU:F2} bottomed {bottomed} hull {_hull} rebound {_rebound:F2} bounces {_bounces} settle {(float.IsNaN(_settle) ? -1f : _settle):F2} peakF {_peakF:F2} W");
        sb.Append($" | damage {(_integ0 > 0 ? 1.0 - integ / _integ0 : 0):F4} lost {_blocks0 - blocks} blocks | clear {_lastClear:F2} m before contact");
        foreach (var kv in _wheels) sb.Append($" | wheel {kv.Key.Rating.Diameter:F1} m comp {kv.Value.maxU:F2} bottom {kv.Value.bottom}");
        foreach (var kv in _feet) sb.Append($" | foot {kv.Key.Variant?.Name} tilt {kv.Value.tilt:F2} rate {kv.Value.rate.X:F3} {kv.Value.rate.Y:F3} {kv.Value.rate.Z:F3}");
        DevResult = sb.ToString();
        Log.Default?.Info("[AERO-SHOCK] " + DevResult);
        Log.Default?.Info("[AERO-SHOCK] " + DevImpact);
        if (DevTrace)
        {
            float rw = 0f; foreach (var k in _wheels.Keys) { rw = k.Rating.RatedWeight; break; }
            Log.Default?.Info($"[AERO-SHOCK-TRACE] d{_seen} mode {WheelShockSettings.Mode} sink {DevSink} M {_M:F0} kg rated {rw:F0} N S {DevConstraintS} D {DevConstraintD} min {DevMinLimit} | cols: x v F/W Fraw/W contact Wv Sv Jc armed S D");
            foreach (var line in _trace) Log.Default?.Info($"[AERO-SHOCK-TRACE] d{_seen} {line}");
        }
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
