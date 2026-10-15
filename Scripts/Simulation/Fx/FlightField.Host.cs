#pragma warning disable
using System;
using System.Collections.Generic;
using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Game.Systems;
namespace AeroMod;

/// <summary>The host side of a grid's flight field: the frame update (from the aero flow), which groups the regime
/// needs (placed, eased, dropped, carried across a re-layout), the consistency recorder, and the aero model read directly
/// (hull points, chunk drag). The tips: FlightField.Tips.cs; the particle / light plumbing: FlightField.Render.cs.</summary>
internal sealed partial class FlightField
{
    // ── the regime: which groups are up (each placed and dropped by itself) ──
    /// <summary>Plasma placed from OnsetMach - PlasmaLead (so it is there when it lights), dropped under OnsetMach -
    /// PlasmaLead - 0.1 with no strength; vapour placed within VapourLead of the edge band; tips from TipMinSpeed.</summary>
    public static float PlasmaLead = 0.15f, VapourLead = 0.05f;
    /// <summary>Distance (m) past which a field is dropped (and the nearest FlightMax grids only - AeroEntryFx.Near).</summary>
    public static float FlightRange = 4000f;
    public static int FlightMax = 4;
    /// <summary>Off switch (aeroset FlightField.On 0) and a rebuild-everything bump (aeroset FlightField.Rebuild 1).</summary>
    public static bool On = true;
    public static int Rebuild;
    /// <summary>The groups, each on or off (AeroSettings "Fx.Plasma", "Fx.Vapour", "Fx.Tips", "Fx.SpotLight": client):
    /// a group switched off is dropped on the next frame, and placed again when switched back on.</summary>
    public static bool PlasmaOn = true, VapourOn = true, TipsOn = true, SpotOn = true;
    /// <summary>The cone (the transonic shock's cone round the body): off until tuned.</summary>
    public static bool ConeOn;

    private bool _plasmaUp, _edgesUp, _tipsUp;
    private int _genPlasma, _genEdges, _genTips, _rebuildSeen;
    private AeroGridComponent _server;
    private object _renderParent;
    /// <summary>The field's report (diagnostics). After a good frame it is BUILT WHEN READ from the field's numbers (2026-10-15,
    /// a profile: Update made the whole string every frame for every live field - 42 KB/s); a failure's or the first one's
    /// stands as set.</summary>
    public string Status
    {
        get { if (!_statusLive) return _status; try { return LiveStatus(); } catch (Exception e) { return "status unreadable: " + e.GetType().Name; } }
        set { _status = value; _statusLive = false; }
    }
    string _status = "new"; volatile bool _statusLive; bool _statusExt;

    /// <summary>Strength easing: up over RiseTau, down over FallTau (s). The flow axis over AxisTau.</summary>
    public static float RiseTau = 0.3f, FallTau = 1.5f, AxisTau = 0.3f;
    private long _easeAt; private Vector3 _axisSm;
    private float _coneSm, _edgeSm, _tipSm;
    static float Ease(float cur, float target, float dt, float fall = -1f)
    {
        if (dt <= 0f) return target;
        float tau = target > cur ? RiseTau : (fall > 0f ? fall : FallTau);
        return cur + (target - cur) * (1f - MathF.Exp(-dt / tau));
    }
    /// <summary>The vapour's own fall (s): condensation clears fast - on the plasma's 1.5 s it hung on ~9 s after leaving the
    /// band, then was switched off late (and on again at once when the next pass began).</summary>
    public static float VapourFallTau = 0.5f;

    // ── the consistency recorder: every frame of every field, judged on numbers, not screenshots ──
    /// <summary>Totals since the last Reset: frames seen; pops (the drawn plasma strength stepping more than PopStep in one
    /// frame); rebuilds while the plasma showed (over 0.05); frames lost (no flow found while the field lived); group toggles
    /// (a group up/down); the largest one-frame step; the peak strength.</summary>
    public static int RecFrames, RecPops, RecRebuilds, RecLost, RecToggles, RecFlicker, RecSpotMade;
    public static float RecMaxStep, RecPeak, PopStep = 0.05f, RecEdgeStep, RecTipStep;
    /// <summary>On/off changes per group (plasma, vapour, tips) and flickers: a group back on within FlickerSec of going
    /// off, or off again within FlickerSec of coming on.</summary>
    public static readonly int[] RecOn = new int[3], RecOff = new int[3];
    public static float FlickerSec = 2f;
    public static void RecReset() { PoolSpawned = PoolReused = 0; RecFrames = RecPops = RecRebuilds = RecLost = RecToggles = RecFlicker = RecSpotMade = RecRelayouts = RecShocks = PoolCarried = RecSwingMoves = RecBakes = RecBakeNow = RecBakedFrames = 0; RecBakedMaxMs = RecBakedSumMs = RecShockMs = 0; lock (_recDrops) _recDrops.Clear(); RecMaxStep = RecPeak = RecEdgeStep = RecTipStep = 0f; Array.Clear(RecOn); Array.Clear(RecOff); }
    /// <summary>Which line dropped a group (P plasma, V vapour edges, T tips) while lit, and how often: the rebuilds' causes.</summary>
    static readonly System.Collections.Generic.Dictionary<string, int> _recDrops = new();
    void RecDrop(string g, int line) { if (_entryS > 0.05f || _edgeSm > 0.05f || _tipSm > 0.05f) lock (_recDrops) { string k = g + line + ":" + (_entryS > 0.05f ? "lit" : "v"); _recDrops.TryGetValue(k, out int n); _recDrops[k] = n + 1; } }
    public static string RecDropsReport() { lock (_recDrops) { var r = string.Join(" ", System.Linq.Enumerable.Select(_recDrops, kv => kv.Key + "x" + kv.Value)); return r.Length == 0 ? "none" : r; } }
    public static string RecReport() =>
        $"spawned {PoolSpawned} reused {PoolReused} | fx frames {RecFrames} pops {RecPops} rebuilds-while-lit {RecRebuilds} lost {RecLost} toggles {RecToggles} flicker {RecFlicker} max-step {RecMaxStep:F3} edge-step {RecEdgeStep:F3} tip-step {RecTipStep:F3} peak {RecPeak:F2} spots-made {RecSpotMade} [{BakeProfile}] chunks {_lastChunkCount} bakes {RecBakes} bakes-on-demand {RecBakeNow} baked-ms avg {(RecBakedFrames > 0 ? RecBakedSumMs / RecBakedFrames : 0):F2} max {RecBakedMaxMs:F1} swing-moves {RecSwingMoves} relayouts {RecRelayouts} shocks {RecShocks} ({RecShockMs:F0} ms) carried {PoolCarried} drops [{RecDropsReport()}] on P{RecOn[0]} V{RecOn[1]} T{RecOn[2]} off P{RecOff[0]} V{RecOff[1]} T{RecOff[2]}";
    private float _recEdge = -1f, _recTip = -1f; private readonly double[] _recFlip = new double[3];
    private float _recS = -1f; private int _recGen = -1; private int _recGroups = -1;
    void Record()
    {
        RecFrames++;
        if (_recS >= 0f)
        {
            float step = MathF.Abs(_entryS - _recS);
            if (step > RecMaxStep) RecMaxStep = step;
            if (step > PopStep) RecPops++;
        }
        _recS = _entryS; RecPeak = MathF.Max(RecPeak, _entryS);
        int gen = _genPlasma + _genEdges * 1000;
        if (_recGen >= 0 && gen != _recGen && _entryS > 0.05f) RecRebuilds++;
        _recGen = gen;
        int groups = (_plasmaUp ? 1 : 0) | (_edgesUp ? 2 : 0) | (_tipsUp ? 4 : 0);
        if (_recGroups >= 0 && groups != _recGroups)
        {
            RecToggles++;
            double t = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
            for (int k = 0; k < 3; k++)
            {
                bool was = (_recGroups & (1 << k)) != 0, now = (groups & (1 << k)) != 0;
                if (was == now) continue;
                if (now) RecOn[k]++; else RecOff[k]++;
                if (_recFlip[k] > 0 && t - _recFlip[k] < FlickerSec) RecFlicker++;
                _recFlip[k] = t;
            }
        }
        _recGroups = groups;
        if (_recEdge >= 0f) RecEdgeStep = MathF.Max(RecEdgeStep, MathF.Abs(_edgeI - _recEdge));
        if (_recTip >= 0f) RecTipStep = MathF.Max(RecTipStep, MathF.Abs(_tipI - _recTip));
        _recEdge = _edgeI; _recTip = _tipI;
    }
    public static void RecLostFrame() => RecLost++;

    public FlightField(Keen.VRage.DCS.Components.Entity client, object renderParent)
    {
        _client = client; _renderParent = renderParent;
        _rebuildSeen = Rebuild;
        _easeAt = System.Diagnostics.Stopwatch.GetTimestamp();   // (the first frame eases from 0: a new field faded in, not popped in at full)
    }

    /// <summary>A frame: the grid's published flow (from its server copy), its world transform. False: nothing to show
    /// (the caller may drop the field).</summary>
    public bool Update(AeroEntryFx.FlowPub p, in WorldTransform wt)
    {
        try
        {
            if (_rebuildSeen != Rebuild) { _rebuildSeen = Rebuild; DropAll(); _shapeOk = false; _hull = null; _hullWhy = null; }
            _server = p.Aero;
            Vector3 travel = p.TravelLocal;
            _mach = p.Mach; _rho = p.Density; _speed = p.Speed; _liftDir = p.LiftLocal; _liftCoef = p.LiftCoef;
            // the strength: an entry source owns it when there is one (the Orbital Mod's reentry), else Mach and density
            float rawS = p.ExtStrength >= 0f ? p.ExtStrength : AeroEntryFx.EntryStrength(_mach, _rho);
            // CONSISTENCY: every strength eased frame by frame (rising over RiseTau, falling over FallTau) - it was the raw value,
            // written four times a second: steps, pops, and a plasma that went out the moment you slowed
            long tsNow = System.Diagnostics.Stopwatch.GetTimestamp();
            float fdt = _easeAt == 0 ? 0f : (float)Math.Min(0.25, (tsNow - _easeAt) / (double)System.Diagnostics.Stopwatch.Frequency);
            _easeAt = tsNow;
            if (fdt > 0f) _frameDt += (fdt - _frameDt) * (1f - MathF.Exp(-fdt / 0.5f));   // (the frame's length, smoothed over 0.5 s: the tips' spawn line)
            _entryS = Ease(_entryS, rawS, fdt);
            EaseBands(fdt);   // (the vapour and the tips eased every frame too: at 10 Hz their rises stepped)
            if (travel.LengthSquared() < 0.5f) return false;
            travel = Vector3.Normalize(travel);
            // the flow axis eased too, and the field placed again only past AxisTol (twice it while the plasma is strong): a
            // 5 degree turn threw every group away and drew it in again, in a new pattern, over 20-40 frames
            // (BAKED: the live flow itself - no easing, no re-layout: the bake is blended to it every frame)
            if (BakedOn) { _axisSm = travel; if (_axis == default) _axis = travel; }
            else _axisSm = _axisSm == default ? travel : Vector3.Normalize(Vector3.Lerp(_axisSm, travel, Math.Clamp(fdt / AxisTau, 0f, 1f)));
            float tol = _entryS > 0.1f ? 2f * AxisTol : AxisTol;
            bool past = !BakedOn && (_axis == default || Vector3.Dot(_axisSm, _axis) < MathF.Cos(tol * MathF.PI / 180f));
            if (!past) _axisPastAt = 0;
            else if (_axis == default || (_axisPastAt != 0 && tsNow - _axisPastAt > 0.3 * System.Diagnostics.Stopwatch.Frequency))
            {
                // (the AoA moved, for 0.3 s: the shadow, outline, weights and the plasma and sheets again - not the tips, not the
                //  spot; the old emitters stop and their particles live out their lives, the new ones fill in fast)
                _axis = _axisSm; _axisPastAt = 0; _swingDone = Quaternion.Identity;   // (laid out for the live flow again: no swing)
                Carry(); _shapeOk = false;
            }
            else if (_axisPastAt == 0) _axisPastAt = tsNow;
            if (!Shape(out var f, out float R, out int N, out var pts)) return false;

            // the live strengths (each effect's fixed time), ten times a second, from the eased values
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _flowAt >= System.Diagnostics.Stopwatch.Frequency / 10)
            {
                _flowAt = now;
                Strengths();
            }

            // the groups the regime needs
            bool wantPlasma = !BakedOn && PlasmaOn && (_entryS > 0.001f || _mach > AeroEntryFx.OnsetMach - PlasmaLead);
            bool dropPlasma = BakedOn || !PlasmaOn || (_entryS <= 0.001f && _mach < AeroEntryFx.OnsetMach - PlasmaLead - 0.1f);
            bool wantEdges = !BakedOn && VapourOn && _mach > EdgeBand[0] - VapourLead && _mach < EdgeBand[3] + VapourLead && _rho > 0f;
            bool dropEdges = BakedOn || !VapourOn || ((_mach < EdgeBand[0] - VapourLead - 0.05f || _mach > EdgeBand[3] + VapourLead + 0.05f) && _edgeSm < 0.02f);   // (faded out first - a hard brake cut them at ~20 % - and under 2 %, invisible: dropped then)
            bool owner = _server == null || _server.FxTipOwner;
            if (!owner) TipWhyHere = "not the ship's main grid";
            bool wantTips = TipsOn && owner && _speed > TipMinSpeed;
            _relayout = _carrying;   // (a re-layout: placed in this frame from the carried emitters, not queued over frames)
            try
            {
                if (wantPlasma && !_plasmaUp) PlacePlasma(f, R, N);
                else if (dropPlasma && _plasmaUp) DropPlasma();
                if (wantEdges && !_edgesUp) PlaceEdges(f, R, N, pts);
                else if (dropEdges && _edgesUp) DropEdges();
            }
            finally { _relayout = false; }
            if (_carrying) ParkCarried();
            if (wantTips && !_tipsUp) PlaceTips(f, N);
            else if (!wantTips && _tipsUp && (!TipsOn || !owner || _speed < 0.5f * TipMinSpeed)) DropTips();   // (no longer the ship's main grid: its trails go)
            else if (_tipsUp && _tipF != default)
            {
                // the tips were chosen for the grid axis the air came along then (forward); flying along another axis for a
                // whole second - a launch sideways, a vertical take-off, then nose first - they are chosen again (kept for
                // good, a sideways start put the trails at the nose and the tail for the rest of the flight)
                if (Major(f) != Major(_tipF) && MathF.Abs(Comp(f, Major(f))) > MathF.Abs(Comp(f, Major(_tipF))) + 0.2f)
                {
                    if (_axisFlipAt == 0) _axisFlipAt = tsNow;
                    else if (tsNow - _axisFlipAt > System.Diagnostics.Stopwatch.Frequency) { _axisFlipAt = 0; DropTips(); }
                }
                else _axisFlipAt = 0;
            }

            // the spot light follows the grid (its own render root), eased onto its aim; the field swung onto the live flow
            BakedFrame(travel, fdt);   // (the plasma and the sheets: the bake blended to the live flow)
            SpotFollow(wt);
            SwingFollow();
            TipLead_(fdt);
            // spawning a few a frame
            // (a refused spawn stopped the field for good while its queue kept growing: drop the queue, retry after 2 s)
            if (_puffWhy != null)
            {
                long nowTs = System.Diagnostics.Stopwatch.GetTimestamp();
                if (_puffWhyAt == 0) _puffWhyAt = nowTs;
                _spawnQueue.Clear();
                if (nowTs - _puffWhyAt > 2 * System.Diagnostics.Stopwatch.Frequency) { _puffWhy = null; _puffWhyAt = 0; }
            }
            if (_spawnQueue.Count > 0)
            {
                _draining = true;
                try { for (int k = 0; k < SpawnPerTick && _spawnQueue.Count > 0 && _puffWhy == null; k++) _spawnQueue.Dequeue()(); }
                finally { _draining = false; }
            }
            Record();
            _statusExt = p.ExtStrength >= 0f; _statusLive = true;   // (Status: built when read - LiveStatus)
            return true;
        }
        catch (Exception e) { Status = "failed: " + Inner(e); DropAll(); return false; }
    }

    /// <summary>The report a good frame wrote (as Update made it every frame until 2026-10-15), from the field's numbers now.</summary>
    string LiveStatus()
        => BakedStatus + $" | spot {(_spot != null ? $"on at {_spotPos.X:F1},{_spotPos.Y:F1},{_spotPos.Z:F1} (hull centre {_pivot.X:F1},{_pivot.Y:F1},{_pivot.Z:F1}, R {_shapeR:F1}) x{_spotSetS:F2} aim {Vector3.Transform(Vector3.UnitZ, _spotQ).X:F2},{Vector3.Transform(Vector3.UnitZ, _spotQ).Y:F2},{Vector3.Transform(Vector3.UnitZ, _spotQ).Z:F2} flow {_axisSm.X:F2},{_axisSm.Y:F2},{_axisSm.Z:F2}" : "off")}" + " | " + $"M {_mach:F2} rho {_rho:F2} plasma {_entryS:F2}{(_statusExt ? " (entry source)" : "")} edge {_edgeI:F2} tip {_tipI:F2}"
           + $" | groups {(_plasmaUp ? "P" : "-")}{(_edgesUp ? "V" : "-")}{(_tipsUp ? "T" : "-")} effects {_puffs.Count} queued {_spawnQueue.Count}"
           + (_puffWhy != null ? " | " + _puffWhy : "") + (_spotWhy != null ? " | spot: " + _spotWhy : "") + (_hullWhy != null ? " | " + _hullWhy : "");

    /// <summary>The hull's shadow along the flow (rebuilt when the axis, the hull or the knobs change).</summary>
    private bool Shape(out Vector3 f, out float R, out int N, out List<Vector3> pts)
    {
        f = _axis; R = 0f; N = 0;
        Vector3 a = Vector3.Normalize(Vector3.Cross(f, Math.Abs(f.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY)), b = Vector3.Cross(f, a);
        pts = HullPoints();
        if (pts == null) return false;
        float xa = float.MaxValue, xb = float.MinValue, ya = float.MaxValue, yb = float.MinValue;
        foreach (var q in pts) { float x = Vector3.Dot(q, a), y = Vector3.Dot(q, b); xa = Math.Min(xa, x); xb = Math.Max(xb, x); ya = Math.Min(ya, y); yb = Math.Max(yb, y); }
        R = 0.5f * Math.Max(xb - xa, yb - ya);
        _shapeR = R;
        var key = new ShapeKey(_axis, pts.Count, Res, Blur, Standoff, Sweep, Margin, MinStandoff, Dilate, RimSmooth, ChordGap);
        if (!_shapeOk || !key.Same(in _shapeKey))
        {
            { Vector3 c = Vector3.Zero; foreach (var q in pts) c += q; _pivot = pts.Count > 0 ? c / pts.Count : Vector3.Zero; }   // (the swing's pivot: the hull's centre)
            Carry();   // (carried onto the new layout, not dropped; the tips do not hang on the shadow: kept)
            long tS = System.Diagnostics.Stopwatch.GetTimestamp();
            ShockGrid(f, a, b, R, pts, out _P3, out _past);
            RecShocks++; RecShockMs += AeroCost.Ms(tS);
            _ring = SmoothRing(Rim(_P3, _past, a, b, out _ringC), RimSmooth);
            _shapeKey = key; _shapeOk = true;
        }
        N = _P3.GetLength(0) - 1;
        return true;
    }

    /// <summary>The vapour band x density and the tips by lift and speed, eased every frame on the real frame time.</summary>
    private void EaseBands(float fdt)
    {
        float m = MachOverride > 0f ? MachOverride : _mach;
        float dens = MathF.Min(1f, MathF.Sqrt(Math.Max(0f, _rho) / Math.Max(0.01f, VapourRho)));
        _coneI = _coneSm = Ease(_coneSm, ConeOn ? Band(ConeBand, m) * dens : 0f, fdt, VapourFallTau);
        _edgeI = _edgeSm = Ease(_edgeSm, Band(EdgeBand, m) * dens, fdt, VapourFallTau);
        float on = Math.Clamp((_speed - TipMinSpeed) / (2f * TipMinSpeed), 0f, 1f); on = on * on * (3f - 2f * on);
        float coef = LiftTest > 0f ? LiftTest : _liftCoef;
        _tipI = _tipSm = Ease(_tipSm, Math.Min(1f, on * Math.Clamp(TipBase + TipLiftGain * coef, 0f, 1f) * (1f + TipMachBoost * Band(EdgeBand, m)) * dens), fdt);
    }

    /// <summary>A lost or culled field (no published flow, or out of the nearest few): it FADES - every strength eased to 0,
    /// still written, the spot still following - and reports when it has (the caller then drops it). It was frozen, then
    /// cut at whatever strength it had.</summary>
    public bool FadeOut(in WorldTransform wt)
    {
        long tsNow = System.Diagnostics.Stopwatch.GetTimestamp();
        float fdt = _easeAt == 0 ? 0f : (float)Math.Min(0.25, (tsNow - _easeAt) / (double)System.Diagnostics.Stopwatch.Frequency);
        _easeAt = tsNow;
        _entryS = Ease(_entryS, 0f, fdt);
        _coneI = _coneSm = Ease(_coneSm, 0f, fdt); _edgeI = _edgeSm = Ease(_edgeSm, 0f, fdt); _tipI = _tipSm = Ease(_tipSm, 0f, fdt);
        if (tsNow - _flowAt >= System.Diagnostics.Stopwatch.Frequency / 10) { _flowAt = tsNow; Strengths(); }
        BakedFrame(_axisSm, fdt);
        SpotFollow(wt);
        SwingFollow();
        TipLead_(fdt);
        return _entryS < 0.002f && _coneSm < 0.002f && _edgeSm < 0.002f && _tipSm < 0.002f;
    }

    /// <summary>The live strengths: each effect's fixed time written from the eased values, and the spot.</summary>
    private void Strengths()
    {
        foreach (var (h, w, kind) in _vapour)
            FixTime(h, Math.Clamp((kind == 1 ? _coneI : kind == 2 ? _tipI : _edgeI) * w, 0.001f, 0.999f), kind == 2 ? FixTimeStep : FixTimeFine);
        TipLine();
        foreach (var (h, w) in _plasma)
            FixTime(h, Math.Clamp(_entryS * w, 0.00001f, 0.999f), FixTimeFine);
        // the spot: off below 2 %, else Spot x strength, placed again when it moved a quarter
        if (Spot > 0f && _P3 != null)
        {
            // (a rebuild drops the plasma for a moment: the light made stays through it - it was destroyed and made again
            //  at every rebuild, blinking and jumping whenever the angle of attack moved, 2026-10-09)
            // (one light a ship: its main grid's - a ship of six grids made six, FxTipOwner as the tips)
            bool want = SpotOn && (_server == null || _server.FxTipOwner) && (_plasmaUp || BakedOn || _spot != null) && _entryS > (_spotUsed > 0f ? 0.015f : 0.03f);   // (on at 3 %, off under 1.5 %: no blink at the edge)
            if (!want) { if (_spotUsed > 0f) StopSpot(); _spotUsed = -1f; }
            else if (_spotUsed < 0f || _spot == null)
            {
                StopSpot(); _spotMul = 1f; _spotUsed = _entryS;   // (made ONCE, at full brightness: the strength is its distance)
                SpawnSpot(_axis, _shapeR, _P3.GetLength(0) - 1);
            }
            else
            {
                // (NOT SetParameters: measured 2026-10-09, a spot dimmed that way lit nothing - the renderer re-binds its cookie
                //  from the handle passed, and there is none to pass. The strength sets its DISTANCE instead: SpotFollow)
                _spotUsed = _entryS;
            }
        }
    }

    // ── the groups ──

    private void PlacePlasma(Vector3 f, float R, int N)
    {
        _rng = new Random(1);   // (one pattern every time it is placed again: a new random one each rebuild)
        _genPlasma++; _noStop = true; _lastF = f;
        DownMix = PlasmaDownMix; Jitter = PlasmaJitter;
        try { _sub = 5; SpawnPuffs(_P3, _past, _ring, f, R); }   // (the spot is the strength's: Strengths places it)
        finally { _sub = 0; _noStop = false; }
        if (_spot == null) _spotUsed = -1f;   // (a light already there is kept: re-aimed, not made again)
        _plasmaUp = true;
    }

    private void PlaceEdges(Vector3 f, float R, int N, List<Vector3> pts)
    {
        _genEdges++; _noStop = true; _lastF = f;
        DownMix = VapourDownMix; Jitter = VapourJitter;
        try
        {
            _sub = 6;
            var turn = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, -f);
            Vector3 mid = Vector3.Zero; foreach (var q in pts) mid += q - f * Vector3.Dot(q, f); mid /= Math.Max(1, pts.Count);
            if (ConeOn && ConeLoop > 0) ConeLoopEmitters(_P3, f, N, pts);
            if (Weighted)
            {
                ChunkDrag(f);
                _weightMax = 1e-6f;
                for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (_hullHas[i, j]) _weightMax = Math.Max(_weightMax, RawWeight(_hullSurf[i, j], f));
            }
            _flow = f; _stagMid = mid; _chordShift = ChordShift;
            try { SpawnLines(_P3, f, R, N, turn); }
            finally { _chordShift = 0f; }
        }
        finally { _sub = 0; _noStop = false; }
        _edgesUp = true;
    }

    private void PlaceTips(Vector3 f, int N)
    {
        _genTips++; _lastF = f;
        ShipTips(f);
        _tipsUp = true;
    }

    private void DropPlasma(bool keepSpot = false, [System.Runtime.CompilerServices.CallerLineNumber] int why = 0)
    {
        _genPlasma++; RecDrop("P", why);
        foreach (var (h, _) in _plasma) { _puffs.Remove(h); Park(h); }
        _plasma.Clear(); _plasmaUp = false;
        if (!keepSpot) { StopSpot(); _spotUsed = -1f; }
    }

    private void DropEdges([System.Runtime.CompilerServices.CallerLineNumber] int why = 0) { _genEdges++; RecDrop("V", why); DropVapour(k => k != 2); _edgesUp = false; }
    private void DropTips([System.Runtime.CompilerServices.CallerLineNumber] int why = 0) { _genTips++; RecDrop("T", why); DropVapour(k => k == 2); _tipsUp = false; _tipSpeedUsed = -1f; _tipF = default; _tipPrm.Clear(); _tipAt.Clear(); _tipOrder.Clear(); _tipShown.Clear(); }

    private void DropVapour(Func<int, bool> which)
    {
        for (int i = _vapour.Count - 1; i >= 0; i--)
        {
            if (!which(_vapour[i].kind)) continue;
            var h = _vapour[i].h; _vapour.RemoveAt(i); _puffs.Remove(h); Park(h);
        }
    }

    // -- RE-LAYOUT (the flow turned past AxisTol, or the shadow changed): the plasma and the vapour sheets' emitters are
    //    CARRIED onto the new layout in the same frame - each moved to its new place (UpdateTransform), its parameters set
    //    only when they differ (SetParameters wipes its live particles) - still emitting; only the ones left over are
    //    parked, only the missing spawned; the spot untouched. They were parked and handed straight back from the pool
    //    with their particles wiped and refilled 48 a frame: the field blanked and stepped at every re-layout, ~2.5 times
    //    a second while turning (the user, 2026-10-09: "rotating very fast makes the particle emissions very
    //    discontinuous", "the reentry particles update at 1hz") --
    private readonly Dictionary<Guid, Queue<object>> _carried = new();
    private bool _carrying, _relayout;
    public static int RecRelayouts, PoolCarried;
    /// <summary>Shock grids laid out (Shape: a new field, axis, hull or knob) and their time: how often it really runs.</summary>
    public static int RecShocks; public static double RecShockMs;
    private void Carry()
    {
        if (!_plasmaUp && !_edgesUp && _plasma.Count == 0) return;
        _carrying = true; RecRelayouts++;
        _genPlasma++; _genEdges++;   // (anything still queued for the old layout is void)
        foreach (var (h, _) in _plasma) { _puffs.Remove(h); CarryOne(h); }
        _plasma.Clear(); _plasmaUp = false;
        for (int i = 0; i < _vapour.Count; i++)
        {
            if (_vapour[i].kind == 2) continue;
            var h = _vapour[i].h; _vapour.RemoveAt(i--); _puffs.Remove(h); CarryOne(h);
        }
        _edgesUp = false;
        _spawnQueue.Clear();
    }
    private void CarryOne(object h)
    {
        if (!_effectOf.TryGetValue(h, out Guid eff)) { DisposeEffect(h); return; }
        if (!_carried.TryGetValue(eff, out var q)) _carried[eff] = q = new Queue<object>();
        q.Enqueue(h);
    }
    private void ParkCarried()
    {
        foreach (var q in _carried.Values) while (q.Count > 0) Park(q.Dequeue());
        _carrying = false;
    }
    /// <summary>SetParameters only when they differ from what this emitter has (a set wipes its live particles).</summary>
    private void SetParamsIfChanged(object h, object prm)
    {
        if (_prmOf.TryGetValue(h, out var had) && had.Equals(prm)) return;
        (_setParams ??= h.GetType().GetMethod("SetParameters", AnyInst)).Invoke(h, new[] { prm });
        _prmOf[h] = prm;
    }
    private readonly Dictionary<object, object> _prmOf = new();

    /// <summary>Everything off (a new axis or shape, out of range, the grid gone).</summary>
    public void DropAll()
    {
        _genPlasma++; _genEdges++; _genTips++;
        _noStop = false; StopPuffs(); BakedDrop();
        _plasmaUp = _edgesUp = _tipsUp = false; _tipSpeedUsed = -1f; _tipF = default; _tipPrm.Clear(); _tipAt.Clear(); _tipOrder.Clear();
    }

    // ── the aero model, read directly ──

    /// <summary>The hull's face centres (grid space, m): every chunk's occluder points of the force table.</summary>
    private List<Vector3> HullPoints()
    {
        var chunks = _server?.FxChunks;
        if (chunks == null) { _hullWhy = "the grid has no chunk table yet"; return null; }
        if (_hull != null && ReferenceEquals(_hullFor, chunks)) return _hull;
        // (two lists in turn, the one not _hull refilled; the hull hash goes with the old one - HullNormal knew a new hull
        //  by a new list, and a list comes round again)
        var pts = ReferenceEquals(_hull, _hullA) ? _hullB : _hullA; pts.Clear();
        foreach (var list in chunks.Occluders) foreach (var p16 in list) pts.Add(p16.V);
        if (pts.Count == 0) { _hullWhy = "no hull points"; return null; }
        _hull = pts; _hullFor = chunks; _hullWhy = null; _hashFor = null;
        return pts;
    }
    private object _hullFor;
    private readonly List<Vector3> _hullA = new List<Vector3>(), _hullB = new List<Vector3>();

    /// <summary>Every chunk of the force table: its centre, Newtonian drag along the flow f and frontal area (its share
    /// for f, at unit dynamic pressure; bilinear over the direction table's face, as ForceTable.Locate).</summary>
    public static int _lastChunkCount;
    private void ChunkDrag(Vector3 f)
    {
        _chunkDrag.Clear();
        var table = _server?.FxChunks;
        if (table == null) return;
        int n = table.N, count = table.ChunkCount; _lastChunkCount = count;
        Vector3 d = f; float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z), aa, bb; int face;
        if (ax >= ay && ax >= az) { face = d.X >= 0 ? 0 : 1; aa = d.Y / ax; bb = d.Z / ax; }
        else if (ay >= az) { face = d.Y >= 0 ? 2 : 3; aa = d.X / ay; bb = d.Z / ay; }
        else { face = d.Z >= 0 ? 4 : 5; aa = d.X / az; bb = d.Y / az; }
        float u = Math.Clamp((aa + 1f) * 0.5f * n, 0, n), w = Math.Clamp((bb + 1f) * 0.5f * n, 0, n);
        int i0 = Math.Min((int)u, n - 1), j0 = Math.Min((int)w, n - 1);
        float fu = u - i0, fw = w - j0;
        int Idx(int i, int j) => (face * (n + 1) + i) * (n + 1) + j;
        var corners = new[] { (Idx(i0, j0), (1 - fu) * (1 - fw)), (Idx(i0 + 1, j0), fu * (1 - fw)), (Idx(i0, j0 + 1), (1 - fu) * fw), (Idx(i0 + 1, j0 + 1), fu * fw) };
        for (int c = 0; c < count; c++)
        {
            if (!table.HasShare(c)) continue;
            // (the four corners read alone: GetShare decoded the chunk's whole share, per chunk, per direction - ~15 ms a
            //  direction on the Hercules; the baked field bakes 642 of them)
            float f1x = 0, f1y = 0, f1z = 0, fr = 0;
            foreach (var (k, wt) in corners) { f1x += table.ShareAt(c, k, 3) * wt; f1y += table.ShareAt(c, k, 4) * wt; f1z += table.ShareAt(c, k, 5) * wt; fr += table.ShareAt(c, k, 12) * wt; }
            float drag = -(f1x * f.X + f1y * f.Y + f1z * f.Z);
            var (lo, hi) = table.Bounds[c];
            if (fr > 1e-3f) _chunkDrag.Add(((lo + hi) * 0.5f, Math.Max(0f, drag), fr));
        }
    }

    /// <summary>
    /// The tip trails kept: at most two along each of the ship's axes across the airflow - the point furthest out in + and the
    /// one furthest out in - (the user, 2026-10-07: a trail from every jutting corner was far too many). Indices into the
    /// candidates, without repeats: 4 at most.
    /// </summary>
    internal static List<int> Extremes(List<int> cand, Func<int, Vector3> at, Vector3 f)
    {
        var keep = new List<int>();
        if (cand.Count == 0) return keep;
        // the two grid axes most across the flow
        var axes = new List<Vector3> { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        axes.Sort((a, b) => MathF.Abs(Vector3.Dot(a, f)).CompareTo(MathF.Abs(Vector3.Dot(b, f))));
        for (int k = 0; k < 2; k++)
        {
            Vector3 u = axes[k];
            int hi = -1, lo = -1; float hv = float.MinValue, lv = float.MaxValue;
            foreach (int c in cand) { float d = Vector3.Dot(at(c), u); if (d > hv) { hv = d; hi = c; } if (d < lv) { lv = d; lo = c; } }
            if (hi >= 0 && !keep.Contains(hi)) keep.Add(hi);
            if (lo >= 0 && !keep.Contains(lo)) keep.Add(lo);
        }
        return keep;
    }
}
