#pragma warning disable
using System;
using System.Collections.Generic;
using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Game.Systems;
namespace AeroMod;

/// <summary>The host side of a grid's flight field: the frame update (from the aero flow), which groups the regime
/// needs, the aero model read directly (hull points, chunk drag, wings), and the particle / light plumbing.</summary>
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
            _mach = p.Mach; _rho = p.Density; _speed = p.Speed; _liftDir = p.LiftLocal; _liftCoef = p.LiftCoef; _flowFound = true;
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
            RecShocks++; RecShockMs += (System.Diagnostics.Stopwatch.GetTimestamp() - tS) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _ring = SmoothRing(Rim(_P3, _past, a, b, out _ringC), RimSmooth);
            _shapeKey = key; _shapeOk = true;
        }
        N = _P3.GetLength(0) - 1;
        if (_lightAt == default) LightPlace(_P3, pts, f);
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

    /// <summary>
    /// The ship's two wing tips, once per flight (the trail audit, 2026-10-08): from the hull of EVERY grid of the ship (pods
    /// and nacelles included, in this grid's frame), measured from the hull's centre - not the grid origin - along the SPAN
    /// axis only (the across-flow grid axis the hull is widest on: no fin or belly trails), each at the rearmost hull point
    /// of its tip. Not tied to the angle of attack: a pull-up no longer drops and re-places them.
    /// </summary>
    // ── the tips' spawn line: a particle is born only where its emitter is, once a frame - at 150 m/s and 60 fps the tip
    //    moves 2.5 m between frames, so each frame's particles came out as one clump with a gap behind it (the user,
    //    2026-10-08). The tip emitter is a thin line along the trail (Vapour_Tip EmitterSize 0.01 x 0.01 x 1 m, local +Z =
    //    backwards), its length (EmitterSizeMultiplier) one frame's travel: each frame's particles fill the stretch the tip
    //    covered. Kept live at 10 Hz as the speed changes. ──
    private readonly Dictionary<object, (object prm, float len)> _tipPrm = new Dictionary<object, (object, float)>(ReferenceEqualityComparer.Instance);
    private float _frameDt = 1f / 60f;
    private long _tipLineAt;
    /// <summary>This field's own tip report (TipWhy is shared - the last field to place wrote it); NearTipWhy: the one
    /// nearest the camera (your ship).</summary>
    public string TipWhyHere = "not placed";
    public static string NearTipWhy = "";
    public static float TipLineMin = 0.05f, TipLineMax = 40f;
    /// <summary>The tip particles' backward speed multiplier (x the effect's 4 m/s, +-25 %): each frame's clump smears along
    /// the trail and fills the gap behind it. 0.01: none (laid still in the air).</summary>
    public static float TipSpread = 0.01f;   // (no push: with 90 % inheritance alone the trail is continuous from the tip - the 2026-10-08 sweep; a push opened a gap at the tip and broke it up)
    // (the tips inherit TipInherit of the ship's motion - Vapour_Tip EmitterMotionInheritance - and are pushed back at
    //  4 m/s x TipSpread: relative to the ship they drift back at (1 - TipInherit) x airspeed + that push - the spacing of one
    //  frame's batch from the next, and the spawn line that fills it)
    // WIND (the user, 2026-10-09: "they're not following the windspeed"): the tips inherit ALL of the ship's motion
    // (Vapour_Tip EmitterMotionInheritance 1) and are emitted backwards at the AIRSPEED - so each particle moves with the air
    // itself: still in the world in real flight, streaming back at the airflow past a held (puppeted) ship. At 90 % and no
    // push they drifted back at a tenth of the airspeed - and not at all past a held ship.
    public static float TipInherit = 1f;
    private float TipDrift => _speed;
    /// <summary>The tips' VelocityMultiplier for the airspeed: 4 m/s x TipScale (the effect's speed, scaled by its
    /// EmitterScaleMultiplier) x it = the airspeed.</summary>
    private float TipWindVm => MathF.Max(0.01f, _speed / (4f * MathF.Max(0.1f, TipScale)));
    private float TipLineLen() => Math.Clamp(TipDrift * Math.Max(1f / 240f, _frameDt), TipLineMin, TipLineMax);
    /// <summary>
    /// The tip emitters' LEAD (s x airspeed upwind of the tip): a trail moving with the air showed nothing for its first
    /// ~0.07 s - a gap of ~30 m at 440 m/s, ~10 m at 150 (it scales with the airspeed: a time, not a place) - so each emitter
    /// sits that far upwind of its tip, kept live, and the visible trail begins at the tip.
    /// </summary>
    public static float TipLead = 0f;   // (0: the emitter AT the tip. The 'invisible first 0.1 s' was the spawn line - 6x too long through the
                                        //  effect scale, its particles born at its far end - not a delay; with no line the trail starts at the tip)
    private readonly Dictionary<object, (Vector3 at, Quaternion q, float lead)> _tipAt = new Dictionary<object, (Vector3, Quaternion, float)>(ReferenceEqualityComparer.Instance);
    // ── the tips re-found across the LIVE wind (the user, 2026-10-09): twice a second the hull's extremes along the span axis
    //    taken across the actual airflow, each at its downwind (trailing) edge; an emitter moved (UpdateTransform) when its
    //    tip moved more than 0.3 m - never re-spawned. Placement still decides WHETHER there are tips (thin and sharp). ──
    private Vector3 _tipSpan, _tipC; private List<Vector3> _hullTips; private long _windTipsAt;
    private readonly List<Vector3> _tipPtsA = new List<Vector3>(), _tipPtsB = new List<Vector3>();
    private readonly List<object> _tipOrder = new List<object>();
    private readonly Vector3[] _wtFound = new Vector3[2]; private readonly float[] _wtBest = new float[2];
    private void WindTips(Vector3 flow)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_hullTips == null || _tipOrder.Count == 0 || now - _windTipsAt < System.Diagnostics.Stopwatch.Frequency / 10) return;
        _windTipsAt = now;
        Vector3 u = _tipSpan - flow * Vector3.Dot(_tipSpan, flow);
        if (u.LengthSquared() < 1e-4f) return;
        u = Vector3.Normalize(u);
        float hi = float.MinValue, lo = float.MaxValue;
        foreach (var p in _hullTips) { float d = Vector3.Dot(p - _tipC, u); hi = MathF.Max(hi, d); lo = MathF.Min(lo, d); }
        var found = _wtFound; var best = _wtBest; found[0] = found[1] = default; best[0] = best[1] = float.MinValue;   // (kept: ten times a second)
        foreach (var p in _hullTips)
        {
            float d = Vector3.Dot(p - _tipC, u), down = -Vector3.Dot(p, flow);
            if (MathF.Abs(d - hi) <= 0.5f && down > best[0]) { best[0] = down; found[0] = p; }
            if (MathF.Abs(d - lo) <= 0.5f && down > best[1]) { best[1] = down; found[1] = p; }
        }
        for (int k = 0; k < _tipOrder.Count && k < 2; k++)
        {
            var h = _tipOrder[k];
            if (best[k] == float.MinValue || !_tipAt.TryGetValue(h, out var cur)) continue;
            if ((cur.at - found[k]).LengthSquared() < 0.0025f) continue;
            _tipAt[h] = (found[k], cur.q, -1f);   // (moved by TipLead_ below, this tick)
            if (k < _tipLocal.Count) _tipLocal[k] = found[k];
        }
    }

    /// <summary>How fast a tip emitter follows its target place and the wind direction (s).</summary>
    public static float TipEaseTau = 0f;   // (0: on the live target every frame - the user, 2026-10-09: "no lag")
    private readonly Dictionary<object, (Vector3 at, Quaternion q)> _tipShown = new();
    private readonly List<object> _tipKeys = new List<object>(), _tipPrmKeys = new List<object>();
    /// <summary>EVERY FRAME (it was 10 Hz with a 1 degree / 0.5 m step, and the tip points 2 Hz with 0.3 m: turning fast the
    /// trails' origins and directions stepped - the user, 2026-10-09: "make sure everything smoothly flows"): each tip
    /// emitter eased onto its tip point (WindTips, 10 Hz) and the live wind direction, moved when it moved 2 cm or 0.2 deg.</summary>
    private void TipLead_(float fdt)
    {
        float lead = TipLead * _speed;
        if (_axisSm != default) WindTips(_axisSm);
        // DOWNWIND (the user, 2026-10-09: "not reacting to the actual wind direction"): each tip emitter sprays along the LIVE
        // airflow (the eased flow axis _axisSm, the grid's frame)
        Vector3 flow = _axisSm != default ? _axisSm : _tipF;
        if (flow == default || _tipAt.Count == 0) return;
        var qw = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, -flow);
        Vector3 back = -flow;
        float k = fdt <= 0f ? 1f : 1f - MathF.Exp(-fdt / Math.Max(0.01f, TipEaseTau));
        var keys = _tipKeys; keys.Clear(); keys.AddRange(_tipAt.Keys);   // (a copy - an emitter that fails is removed - kept: it was a new list every frame)
        foreach (var h in keys)
        {
            var (at, q, was) = _tipAt[h];
            Vector3 goal = at - back * lead;
            Vector3 pos; Quaternion rot;
            if (_tipShown.TryGetValue(h, out var shown) && was >= 0f) { pos = Vector3.Lerp(shown.at, goal, k); rot = Quaternion.Normalize(Quaternion.Slerp(shown.q, qw, k)); }
            else { pos = goal; rot = qw; }
            if (was >= 0f && (pos - shown.at).LengthSquared() < 0.0004f && MathF.Abs(Quaternion.Dot(rot, shown.q)) > 0.9999985f) continue;
            try
            {
                (_updTransform ??= h.GetType().GetMethod("UpdateTransform", AnyInst, null, new[] { typeof(RelativeTransform) }, null))
                    .Invoke(h, new object[] { new RelativeTransform(pos, rot) });
                _tipShown[h] = (pos, rot);
                _tipAt[h] = (at, rot, lead);
            }
            catch (Exception e) { _puffWhy = "tip lead: " + Inner(e); _tipAt.Remove(h); _tipShown.Remove(h); }
        }
    }

    private void TipLine()
    {
        if (_tipPrm.Count == 0) return;
        float want = TipLineLen();
        var keys = _tipPrmKeys; keys.Clear(); keys.AddRange(_tipPrm.Keys);
        foreach (var h in keys)
        {
            var (prm, len) = _tipPrm[h];
            // (EVERY SetParameters rebuilds the GPU emitter and deletes its live particles - the trail vanished for ~0.3 s at each:
            //  the airspeed is sent only on a 15 % change and at most every 3 s; there is no spawn line any more to keep up)
            want = TipWindVm;
            if (len > 0f && (MathF.Abs(want - len) < 0.15f * len || System.Diagnostics.Stopwatch.GetTimestamp() - _tipLineAt < 3 * System.Diagnostics.Stopwatch.Frequency)) continue;
            _tipLineAt = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                prm.GetType().GetField("VelocityMultiplier").SetValue(prm, want);   // (the airspeed)
                (_setParams ??= h.GetType().GetMethod("SetParameters", AnyInst)).Invoke(h, new[] { prm }); _prmOf[h] = prm;
                _tipPrm[h] = (prm, want);
            }
            catch (Exception e) { _puffWhy = "tip line: " + Inner(e); _tipPrm.Remove(h); }
        }
    }

    private void ShipTips(Vector3 f)
    {
        // (the forward axis is chosen once per flight - kept while the tips are up - so the span never swaps to the
        //  nose-tail axis when the ship pitches or falls belly-first)
        if (_tipF == default) _tipF = f;
        var tips = ComputeTips(_tipF, out var a, out float wid, out int count);
        _tipLocal.Clear(); _tipHist.Clear();
        if (tips == null) { TipWhy = TipWhyHere = "no hull"; return; }
        var q = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, -a);
        foreach (var t in tips)
        {
            VapourEffect(VapourTip, t, q, TipScale, 1f, false, TipWindVm, true);   // (a point at the tip: no spawn line - see TipLine)
            _tipLocal.Add(t); _tipHist.Add(new List<(double t, Vector3D p)>());
        }
        TrkPlaced += tips.Count;
        TipWhy = TipWhyHere = $"{tips.Count} tips at the span's ends ({wid:F1} m wide, {count} hull points of {Math.Max(1, _server?.FxGroup.Length ?? 1)} grid(s)){TipShape}";
    }

    /// <summary>The ship's two span-end tips (this grid's frame), from the hull of every grid of the ship: measured from the
    /// hull's centre along the span axis only, each at the rearmost hull point of its tip. Null: no hull.</summary>
    private List<Vector3> ComputeTips(Vector3 f, out Vector3 a, out float wid, out int count)
    {
        a = default; wid = 0f; count = 0;
        // (two lists in turn: the one WindTips is not reading (_hullTips) filled - it was a fresh list of every hull point
        //  of the ship twice a second; kept only on success, as before)
        var pts = ReferenceEquals(_hullTips, _tipPtsA) ? _tipPtsB : _tipPtsA; pts.Clear();
        var e = _server?.Entity;
        if (_server != null && e != null && PhysicsHack.Alive(e.Data))
        {
            var wtO = e.Data.GetWorldTransform();
            foreach (var m in _server.FxGroup.Length > 0 ? _server.FxGroup : new[] { _server })
            {
                var ch = m?.FxChunks; var me = m?.Entity;
                // (a grid of the group gone since the last group search - split off, closed: its transform threw)
                if (ch == null || me == null || !PhysicsHack.Alive(me.Data)) continue;
                var wtM = me.Data.GetWorldTransform();
                bool self = ReferenceEquals(m, _server);
                foreach (var list in ch.Occluders)
                    foreach (var p16 in list)
                    {
                        Vector3 v = p16.V;
                        if (!self)
                        {
                            Vector3D w = WorldTransform.Transform(new Vector3D(v.X, v.Y, v.Z), wtM);
                            Vector3D l = WorldTransform.TransformInv(w, wtO);
                            v = new Vector3((float)l.X, (float)l.Y, (float)l.Z);
                        }
                        pts.Add(v);
                    }
            }
        }
        count = pts.Count;
        if (pts.Count < 2) return null;
        Vector3 c = Vector3.Zero; foreach (var p in pts) c += p; c /= pts.Count;
        // forward: the grid axis most along the flow (into the air); the span: of the other two, the one the hull is widest on
        var axes = new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ };
        int fi = 0; for (int k = 1; k < 3; k++) if (MathF.Abs(Vector3.Dot(axes[k], f)) > MathF.Abs(Vector3.Dot(axes[fi], f))) fi = k;
        a = axes[fi] * MathF.Sign(Vector3.Dot(axes[fi], f));
        Vector3 u = default; wid = -1f;
        for (int k = 0; k < 3; k++)
        {
            if (k == fi) continue;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var p in pts) { float d = Vector3.Dot(p - c, axes[k]); lo = MathF.Min(lo, d); hi = MathF.Max(hi, d); }
            if (hi - lo > wid) { wid = hi - lo; u = axes[k]; }
        }
        float hiS = float.MinValue, loS = float.MaxValue;
        foreach (var p in pts) { float d = Vector3.Dot(p - c, u); hiS = MathF.Max(hiS, d); loS = MathF.Min(loS, d); }
        _tipSpan = u; _hullTips = pts; _tipC = c;   // (kept: the tips re-found across the live wind - WindTips)
        // ONLY A REAL WING TIP (the user, 2026-10-08: "only if the outline is truly sharp and thin"; a brick has none): the
        // outer TipZone of the span on that side must be thin - under TipThinFrac of the ship's thickest - and sharp - its
        // chord under TipSharpFrac of the ship's longest
        Vector3 t3 = Vector3.Normalize(Vector3.Cross(a, u));
        float Ext(Func<Vector3, bool> sel, Vector3 ax)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var p in pts) if (sel(p)) { float d = Vector3.Dot(p, ax); lo = MathF.Min(lo, d); hi = MathF.Max(hi, d); }
            return hi > lo ? hi - lo : 0f;
        }
        float thickAll = Ext(_ => true, t3), chordAll = Ext(_ => true, a);
        var tips = new List<Vector3>();
        var why = new System.Text.StringBuilder();
        bool pairFail = false;
        foreach (float side in new[] { hiS, loS })
        {
            float zone = TipZone * wid;
            Func<Vector3, bool> outer = p => MathF.Abs(Vector3.Dot(p - c, u) - side) <= zone;
            float thick = Ext(outer, t3), chord = Ext(outer, a);
            bool thin = thick <= TipThinFrac * thickAll, sharp = chord <= TipSharpFrac * chordAll;
            why.Append($" [{(side > 0 ? "+" : "-")} thick {thick:F1}/{thickAll:F1} chord {chord:F1}/{chordAll:F1}{(thin && sharp ? "" : " - not a tip")}]");
            if (!thin || !sharp) { tips.Clear(); pairFail = true; continue; }   // (a pair or none: one trail on a symmetric ship looked broken)
            if (pairFail) continue;
            Vector3 best = default; float bestBack = float.MaxValue; bool any = false;
            foreach (var p in pts)
            {
                if (MathF.Abs(Vector3.Dot(p - c, u) - side) > 0.5f) continue;
                float back = Vector3.Dot(p, a);
                if (back < bestBack) { bestBack = back; best = p; any = true; }
            }
            if (any) tips.Add(best);
        }
        TipShape = why.ToString();
        if (pairFail) tips.Clear();
        return tips;
    }
    /// <summary>A tip: the outer TipZone of the span, thinner than TipThinFrac of the ship's thickness and with a chord under
    /// TipSharpFrac of its longest.</summary>
    public static float TipZone = 0.1f, TipThinFrac = 0.6f, TipSharpFrac = 0.6f;
    public static string TipShape = "";

    // ── the trail tracker: where each tip emitter is every frame, and the trail its particles must leave ──
    // (the particles live on the GPU - only counts come back - so we track what we own: the emitters. With no motion
    //  inheritance and no emission speed, a tip particle stays where it was born: the emitter's path over the last TrailLife
    //  seconds IS the trail.)
    private readonly List<Vector3> _tipLocal = new List<Vector3>();
    private readonly List<List<(double t, Vector3D p)>> _tipHist = new List<List<(double, Vector3D)>>();
    private double _tipCheckAt; private int _tipGenSeen = -1;
    private Vector3 _tipF;   // (the flow the tips were chosen with: kept while the air comes along the same grid axis)
    private long _axisFlipAt;
    /// <summary>The grid axis (0 X, 1 Y, 2 Z) most along a direction.</summary>
    static int Major(Vector3 v) { float x = MathF.Abs(v.X), y = MathF.Abs(v.Y), z = MathF.Abs(v.Z); return x >= y && x >= z ? 0 : y >= z ? 1 : 2; }
    static float Comp(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
    private long _axisPastAt;
    /// <summary>Draw each tip emitter's predicted trail (yellow), the emitter (red) and the hull's true tip now (green).</summary>
    public static bool TrailDebug;
    public static float TrailLife = 2f;
    /// <summary>Since the last reset: frames tracked, emitters placed, re-placements while flying (a tip moved to a new
    /// point), the largest gap between an emitter and the hull's true tip (m, checked twice a second), the largest one-frame
    /// emitter jump beyond the ship's own motion (m), the longest predicted trail (m), tips seen on a frame.</summary>
    public static int TrkFrames, TrkPlaced, TrkReplaced, TrkMaxTips;
    public static float TrkMaxGap, TrkMaxJump, TrkMaxLen;
    public static void TrkReset() { TrkFrames = TrkPlaced = TrkReplaced = TrkMaxTips = 0; TrkMaxGap = TrkMaxJump = TrkMaxLen = 0f; }
    public static string TrkReport() =>
        $"trail frames {TrkFrames} placed {TrkPlaced} replaced {TrkReplaced} tips {TrkMaxTips} max-gap {TrkMaxGap:F2} max-jump {TrkMaxJump:F2} max-len {TrkMaxLen:F0}";

    /// <summary>Client, every frame after Update: the tip emitters' world positions recorded, judged, and drawn on request.</summary>
    public void TrackTips(in WorldTransform wt)
    {
        if (!_tipsUp || _tipLocal.Count == 0) { _tipHist.Clear(); return; }
        double now = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        if (_tipGenSeen >= 0 && _tipGenSeen != _genTips) TrkReplaced++;
        _tipGenSeen = _genTips;
        TrkFrames++; TrkMaxTips = Math.Max(TrkMaxTips, _tipLocal.Count);
        // the hull's true tips now, twice a second: how far the emitters sit from them
        List<Vector3> truth = null;
        if (now - _tipCheckAt > 0.5 && _tipF != default)
        {
            _tipCheckAt = now;
            truth = ComputeTips(_tipF, out _, out _, out _);
            if (truth != null && truth.Count > 0)
                foreach (var t in _tipLocal)
                {
                    float g = float.MaxValue; foreach (var h in truth) g = MathF.Min(g, (t - h).Length());
                    TrkMaxGap = MathF.Max(TrkMaxGap, g);
                }
        }
        if (truth != null) { _truth = truth; }
        for (int k = 0; k < _tipLocal.Count && k < _tipHist.Count; k++)
        {
            Vector3D w = WorldTransform.Transform(new Vector3D(_tipLocal[k].X, _tipLocal[k].Y, _tipLocal[k].Z), wt);
            var h = _tipHist[k];
            h.Add((now, w));
            while (h.Count > 0 && now - h[0].t > TrailLife) h.RemoveAt(0);
            float len = 0f;
            for (int i = 1; i < h.Count; i++) len += (float)(h[i].p - h[i - 1].p).Length();
            TrkMaxLen = MathF.Max(TrkMaxLen, len);
        }
    }
    private List<Vector3> _truth;

    /// <summary>The overlay: each emitter's predicted trail (yellow), the emitter (red), the hull's true tip (green).</summary>
    public void DrawTrails(in WorldTransform wt, Keen.VRage.Core.Game.RuntimeSystems.DebugDraw.IDebugDrawProvider ddp)
    {
        var dd = ddp?.GlobalBuilder; if (dd == null || !_tipsUp) return;
        for (int k = 0; k < _tipLocal.Count && k < _tipHist.Count; k++)
        {
            var h = _tipHist[k];
            for (int i = 1; i < h.Count; i++) dd.AddLine(h[i - 1].p, h[i].p, new ColorSRGB(1f, 0.85f, 0.2f));
            Vector3D w = WorldTransform.Transform(new Vector3D(_tipLocal[k].X, _tipLocal[k].Y, _tipLocal[k].Z), wt);
            dd.AddLine(w - new Vector3D(0.5, 0, 0), w + new Vector3D(0.5, 0, 0), new ColorSRGB(1f, 0.2f, 0.2f));
            dd.AddLine(w - new Vector3D(0, 0.5, 0), w + new Vector3D(0, 0.5, 0), new ColorSRGB(1f, 0.2f, 0.2f));
        }
        if (_truth != null)
            foreach (var t in _truth)
            {
                Vector3D w = WorldTransform.Transform(new Vector3D(t.X, t.Y, t.Z), wt);
                dd.AddLine(w - new Vector3D(0, 0, 0.7), w + new Vector3D(0, 0, 0.7), new ColorSRGB(0.2f, 1f, 0.3f));
            }
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
        _plasmaUp = _edgesUp = _tipsUp = false; _tipSpeedUsed = -1f; _lightAt = default; _tipF = default; _tipPrm.Clear(); _tipAt.Clear(); _tipOrder.Clear();
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
        _chunkDrag.Clear(); _dragWhy = null;
        var table = _server?.FxChunks;
        if (table == null) { _dragWhy = "no chunk table"; return; }
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
        if (_chunkDrag.Count == 0) _dragWhy = "no windward chunks";
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

    // ── particles and the spot light (the game's render types are outside what mods compile against: late-bound,
    //    resolved once; see EntryFxBridge and docs/REFLECTION_AND_WHITELIST.md) ──

    private static MethodInfo _spawn, _fixTime, _setParams, _dispose;
    private static Type _tParams, _tSession;
    private object _fxSession;
    private static object _contracts;
    private static MethodInfo _createRoot, _createSpot, _rootUpdate;
    private static bool _renderResolved;

    private static string Inner(Exception e) => (e.InnerException ?? e).Message;

    private static Type FindType(string asm, string name)
    {
        foreach (var x in AppDomain.CurrentDomain.GetAssemblies()) if (x.GetName().Name == asm) return x.GetType(name);
        return null;
    }

    /// <summary>An effect under the grid's render root at a grid-space transform (it follows the grid), its user
    /// parameters filled by init before the spawn; null and why on failure.</summary>
    private object SpawnEffect(Keen.VRage.DCS.Components.Entity grid, Guid effect, RelativeTransform at, out object prm, out string why, Action<object> init = null)
    {
        prm = null; why = null;
        try
        {
            if (grid == null || _renderParent == null) { why = "no render parent"; return null; }
            if (_spawn == null)
            {
                _tSession = FindType("VRage.Game.Client", "Keen.VRage.Game.Client.Effects.Components.ParticleEffectsSessionComponent");
                _tParams = FindType("VRage.Render", "Keen.VRage.Render.Data.ParticleEffectUserParameters");
                if (_tSession == null || _tParams == null) { why = "particle types not found"; return null; }
                foreach (var m in _tSession.GetMethods(AnyInst))
                    if (m.Name == "TrySpawnEffect") { var ps = m.GetParameters(); if (ps.Length > 3 && ps[0].ParameterType == typeof(RelativeTransform)) { _spawn = m; break; } }
                if (_spawn == null) { why = "TrySpawnEffect not found"; return null; }
            }
            if (_fxSession == null)
            {
                var comps = grid.GetSession()?.SessionComponents;
                if (comps != null) foreach (var c in comps.Components) if (c != null && _tSession.IsInstanceOfType(c)) { _fxSession = c; break; }
                if (_fxSession == null) { why = "no particle session"; return null; }
            }
            // REUSE (the user, 2026-10-09: "spawning new emitters is unacceptable - we should be reusing"): a parked emitter of
            // this effect is moved to the new place, given the new parameters and set emitting again; a new one only when
            // none is parked. Parked: stopped emitting (its particles in flight live out their lives), not disposed.
            RelativeTransform place = _spawnTip ? at : Swung(at, _swingDone);   // (the plasma and the sheets: where the swing has them now)
            if (_carrying && _carried.TryGetValue(effect, out var carried) && carried.Count > 0)
            {
                object hc = carried.Dequeue();
                prm = Activator.CreateInstance(_tParams);
                init?.Invoke(prm);
                SetParamsIfChanged(hc, prm);
                (_updTransform ??= hc.GetType().GetMethod("UpdateTransform", AnyInst, null, new[] { typeof(RelativeTransform) }, null)).Invoke(hc, new object[] { place });
                if (!_spawnTip) _baseOf[hc] = at;
                PoolCarried++;
                return hc;
            }
            if (_pool.TryGetValue(effect, out var parked) && parked.Count > 0)
            {
                object hp = parked.Pop();
                prm = Activator.CreateInstance(_tParams);
                init?.Invoke(prm);
                var ht = hp.GetType();
                (_setParams ??= ht.GetMethod("SetParameters", AnyInst)).Invoke(hp, new[] { prm }); _prmOf[hp] = prm;
                (_updTransform ??= ht.GetMethod("UpdateTransform", AnyInst, null, new[] { typeof(RelativeTransform) }, null)).Invoke(hp, new object[] { place });
                if (!_spawnTip) _baseOf[hp] = at;
                (_resume ??= ht.GetMethod("ResumeEmitting", AnyInst, null, Type.EmptyTypes, null)).Invoke(hp, null);
                PoolReused++;
                return hp;
            }
            if (!DefinitionManager.Instance.TryGetDefinition(effect, out Definition def) || def == null) { why = "effect not loaded: " + effect; return null; }
            prm = Activator.CreateInstance(_tParams);
            init?.Invoke(prm);   // (a boxed struct: the sets stick)
            var p = _spawn.GetParameters();
            var args = new object[p.Length];
            for (int i = 0; i < args.Length; i++) args[i] = p[i].HasDefaultValue ? p[i].DefaultValue : null;
            args[0] = place; args[1] = def; args[2] = prm; args[3] = _renderParent;
            for (int i = 4; i < args.Length; i++) if (p[i].ParameterType == typeof(string)) args[i] = "AeroFlight";
            object h = _spawn.Invoke(_fxSession, args);
            if (h == null) why = "spawn refused";
            else { _effectOf[h] = effect; _prmOf[h] = prm; PoolSpawned++; if (!_spawnTip) _baseOf[h] = at; }
            return h;
        }
        catch (Exception e) { why = "spawn: " + Inner(e); return null; }
    }

    private static void FixTime(object h, float seconds) => FixTime(h, seconds, FixTimeStep);
    /// <summary>Plasma and vapour sheets: written on a 0.01 change (fine - no 5 % steps, no residual hanging on); the tips
    /// keep FixTimeStep (their clumping A/B). The floor is always written.</summary>
    public static float FixTimeFine = 0.01f;
    internal static void NoteFixed(object h, float seconds) { lock (_fixedAt) _fixedAt[h] = seconds; }
    private static void FixTime(object h, float seconds, float step)
    {
        // (written only when it moved: rewritten ten times a second whether or not it changed, the trails came out in
        //  clumps - beads every ~15 m at 150 m/s; FixTimeEvery > 0 forces the old cadence, for the A/B)
        lock (_fixedAt)
        {
            if (FixTimeEvery == 0 && _fixedAt.TryGetValue(h, out float was) && MathF.Abs(was - seconds) < step && !(seconds <= 0.0011f && was > seconds)) return;
            _fixedAt[h] = seconds;
        }
        _fixTime ??= h.GetType().GetMethod("FixEffectTime", AnyInst);
        _fixTime.Invoke(h, new object[] { true, TimeSpan.FromSeconds(seconds) });
        FixTimeWrites++;
    }
    private static readonly Dictionary<object, float> _fixedAt = new Dictionary<object, float>(ReferenceEqualityComparer.Instance);
    /// <summary>An effect's time is written again only when it moved by this; FixTimeEvery 1: every call (the old way).</summary>
    public static float FixTimeStep = 0.05f;
    public static int FixTimeEvery, FixTimeWrites;

    private static MethodInfo _stopEmitting, _disposeAfter, _updTransform, _resume;
    /// <summary>Parked emitters by effect, and each emitter's effect: what Park keeps and SpawnEffect reuses.</summary>
    private readonly Dictionary<Guid, Stack<object>> _pool = new Dictionary<Guid, Stack<object>>();
    private readonly Dictionary<object, Guid> _effectOf = new Dictionary<object, Guid>(ReferenceEqualityComparer.Instance);
    /// <summary>Since the last reset: emitters really spawned, and reused from the pool.</summary>
    public static int PoolSpawned, PoolReused;

    /// <summary>The most emitters of one effect kept parked per field (the rest disposed).</summary>
    public static int MaxParked = 48;
    /// <summary>An emitter no longer needed here: PARKED for reuse - its emission stopped (the particles in flight finish
    /// their life) and its effect time at the floor - not disposed. Only an emitter this field did not spawn is disposed.</summary>
    private void Park(object h)
    {
        if (h == null) return;
        Unswing(h);
        if (!_effectOf.TryGetValue(h, out Guid eff)) { DisposeEffect(h); return; }
        try
        {
            var t = h.GetType();
            _stopEmitting ??= t.GetMethod("StopEmitting", AnyInst, null, new[] { typeof(bool) }, null);
            _stopEmitting?.Invoke(h, new object[] { false });
            FixTime(h, 0.00001f, 0f);
            if (!_pool.TryGetValue(eff, out var st)) _pool[eff] = st = new Stack<object>();
            // (capped: the pool only grew - 433 parked vapour emitters on one grid (the Orbital Mod's particle census,
            //  2026-10-12); past MaxParked per effect an emitter goes for good, its particles finishing their life)
            if (st.Count >= MaxParked) { _effectOf.Remove(h); _prmOf.Remove(h); _baseOf.Remove(h); DisposeEffect(h); return; }
            st.Push(h);
        }
        catch (Exception) { _effectOf.Remove(h); DisposeEffect(h); }
    }

    /// <summary>The field is over (out of range, the grid gone, the effects off): everything really disposed, the pool too.</summary>
    public void Destroy()
    {
        DropAll();
        foreach (var st in _pool.Values) foreach (var h in st) DisposeEffect(h);
        foreach (var q in _carried.Values) foreach (var h in q) DisposeEffect(h);
        _carried.Clear(); _carrying = false;
        _pool.Clear(); _effectOf.Clear(); _prmOf.Clear(); _baseOf.Clear();
    }

    /// <summary>An effect goes GRACEFULLY: it stops emitting and disposes itself once its particles have died (a
    /// dropped group - tips re-placed for a new length, vapour leaving the band, plasma cooling - no longer pops: its
    /// sparks and puffs in flight finish their life). Falls back to an immediate dispose.</summary>
    private static void DisposeEffect(object h)
    {
        lock (_fixedAt) _fixedAt.Remove(h);   // (its last written time forgotten with it)
        if (h == null) return;
        try
        {
            var t = h.GetType();
            _stopEmitting ??= t.GetMethod("StopEmitting", AnyInst, null, new[] { typeof(bool) }, null);
            _disposeAfter ??= t.GetMethod("DisposeAfterParticlesDie", AnyInst, null, Type.EmptyTypes, null);
            if (_stopEmitting != null && _disposeAfter != null)
            {
                _stopEmitting.Invoke(h, new object[] { false });
                _disposeAfter.Invoke(h, null);
                return;
            }
            _dispose ??= t.GetMethod("Dispose", AnyInst, null, new[] { typeof(bool) }, null);
            _dispose?.Invoke(h, new object[] { true });
        }
        catch (Exception) { }
    }

    private static bool ResolveRender()
    {
        if (_renderResolved) return _contracts != null && _createRoot != null && _createSpot != null;
        _renderResolved = true;
        try
        {
            Assembly render = null;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "VRage.Render") { render = a; break; }
            var engine = render?.GetType("Keen.VRage.Render.EngineComponents.RenderEngineComponent");
            object instance = engine?.GetField("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            _contracts = instance?.GetType().GetProperty("RenderContracts")?.GetValue(instance);
            if (_contracts == null) return false;
            foreach (var m in _contracts.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name == "CreateRootEntity" && m.GetParameters().Length == 3) _createRoot = m;
                if (m.Name == "CreateSpotLightEntity") _createSpot = m;
            }
        }
        catch (Exception) { _contracts = null; }
        return _contracts != null && _createRoot != null && _createSpot != null;
    }

    private static void UpdateRootTransform(object root, WorldTransform at)
    {
        if (root == null) return;
        _rootUpdate ??= root.GetType().GetMethod("UpdateTransform");
        _rootUpdate?.Invoke(root, new object[] { at });
    }

    private static void DisposeRender(object o)
    {
        if (o == null) return;
        try { o.GetType().GetMethod("Dispose", Type.EmptyTypes)?.Invoke(o, null); } catch (Exception) { }
    }

    /// <summary>The plasma's light: ONE spot light SpotDist x R upwind of the front, aimed down the flow, its cone just
    /// covering SpotCover x R - the windward surface lit, nothing behind; its own render root, moved with the grid.</summary>
    private void SpawnSpot(Vector3 f, float R, int N)
    {
        try
        {
            if (!ResolveRender()) { _spotWhy = "render contracts not available"; return; }
            if (!SpotAim(f, out var at, out var q2)) { _spotWhy = "no hull"; return; }
            float cone = 2f * MathF.Atan(SpotCover / Math.Max(0.1f, SpotDist));
            // (the light sits at its root's origin; the ROOT carries the aim - moved every frame, so the light is re-aimed in
            //  place: a light's own transform is fixed when it is made)
            _spotPos = at; _spotQ = q2; _spotEase = 0; _spotMovedAt = at; _spotMovedQ = q2;
            _spotRoot = _createRoot.Invoke(_contracts, new object[] { "AeroPlasmaSpotRoot", _client.Data.GetWorldTransform(), true });
            if (_spotRoot == null) { _spotWhy = "no root"; return; }
            var ps = _createSpot.GetParameters();
            var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
            var pc = SparkBirth;   // (the sparks' birth colour, sRGB, in linear)
            float lr = MathF.Pow(pc[0] * LightTint[0], 2.2f), lg = MathF.Pow(pc[1] * LightTint[1], 2.2f), lb = MathF.Pow(pc[2] * LightTint[2], 2.2f);
            float s = Spot * _spotMul;
            for (int i = 0; i < ps.Length; i++)
                switch (ps[i].Name)
                {
                    case "debugName": args[i] = "AeroPlasmaSpot"; break;
                    case "lightIntensityRGB": args[i] = new ColorLinear(lr * s, lg * s, lb * s, 1f); break;
                    case "outerConeAngle": args[i] = cone; break;
                    case "localLightTransform": args[i] = new RelativeTransform(at, q2); break;
                    case "rootEntity": args[i] = _spotRoot; break;
                    case "tryForceShadowMapAlwaysAllocated": args[i] = SpotShadow; break;
                    case "falloffMultiplier": args[i] = SpotFalloff; break;
                }
            _spot = _createSpot.Invoke(_contracts, args);
            RecSpotMade++;
            _spotWhy = null;
            _spotSetS = _spotMul;
        }
        catch (Exception e) { _spotWhy = "spot: " + Inner(e); StopSpot(); }
    }

    // ── the spot's aim: SpotDist x R upwind of the hull's front along the flow f, pointing down it (grid-local) ──
    private Vector3 _spotPos; private Quaternion _spotQ = Quaternion.Identity; private long _spotEase;
    /// <summary>How fast the light follows a new aim (s).</summary>
    public static float SpotAimTau = 0f;
    /// <summary>How far upwind of the front the light sits: SpotDist x R at full strength, further as the entry weakens -
    /// the light reaching the hull goes as 1 / distance^2, so it is the strength (made at full brightness; never dimmed
    /// by SetParameters).</summary>
    private float SpotDistance() => SpotDist * _shapeR / MathF.Sqrt(Math.Clamp(_entryS * _spotFlick, 0.01f, 1.5f));
    /// <summary>The flicker: the light's strength x (1 + SpotFlicker x a fire-like wobble - three unrelated frequencies,
    /// SpotFlickerHz the fastest), through its distance like the strength (the user, 2026-10-09: "slightly flickery").</summary>
    public static float SpotFlicker = 0.2f, SpotFlickerHz = 17f;
    private float _spotFlick = 1f;
    private static float Flick(double t)
    {
        double w = 2 * Math.PI * SpotFlickerHz;
        double n = 0.5 * Math.Sin(w * t) + 0.3 * Math.Sin(w * 0.537 * t + 1.3) + 0.2 * Math.Sin(w * 0.291 * t + 4.1);
        return 1f + SpotFlicker * (float)n;
    }
    private bool SpotAim(Vector3 f, out Vector3 at, out Quaternion q)
    {
        at = default; q = Quaternion.Identity;
        if (BakedOn && _bakedSpotOk)
        {
            at = _bakedSpotMid + f * (_bakedSpotFront + SpotDistance());
            q = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, SpotFlip ? -f : f);
            return true;
        }
        if (_hullHas == null || _hullSurf == null || f == default) return false;
        int N = _hullHas.GetLength(0) - 1;
        float zFront = float.MinValue; Vector3 mid = Vector3.Zero; int cnt = 0;
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
            if (_hullHas[i, j]) { var p = _hullSurf[i, j]; zFront = Math.Max(zFront, Vector3.Dot(p, f)); mid += p - f * Vector3.Dot(p, f); cnt++; }
        if (cnt == 0) return false;
        mid /= cnt;
        at = mid + f * (zFront + SpotDistance());
        q = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, SpotFlip ? -f : f);   // (the engine's forward is -Z)
        return true;
    }
    private static MethodInfo _spotMove;
    private Vector3 _spotMovedAt; private Quaternion _spotMovedQ;
    /// <summary>Every frame: the root with the grid; the light itself (SpotLightEntity.UpdateTransform) on the live aim -
    /// SpotAimTau 0: no easing (the user, 2026-10-09: "no lag").</summary>
    private void SpotFollow(WorldTransform wt)
    {
        if (_spotRoot == null) return;
        UpdateRootTransform(_spotRoot, wt);
        _spotFlick = SpotFlicker > 0f ? Flick(System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency) : 1f;
        if (_spot == null || _axisSm == default || !SpotAim(_axisSm, out var goal, out var goalQ)) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _spotEase == 0 ? 0f : (float)Math.Min(0.25, (now - _spotEase) / (double)System.Diagnostics.Stopwatch.Frequency);
        _spotEase = now;
        float k = SpotAimTau <= 0f || dt <= 0f ? 1f : 1f - MathF.Exp(-dt / SpotAimTau);
        _spotPos = Vector3.Lerp(_spotPos, goal, k);
        _spotQ = Quaternion.Normalize(Quaternion.Slerp(_spotQ, goalQ, k));
        if ((_spotPos - _spotMovedAt).LengthSquared() < 0.0004f && MathF.Abs(Quaternion.Dot(_spotQ, _spotMovedQ)) > 0.9999996f) return;
        try
        {
            (_spotMove ??= _spot.GetType().GetMethod("UpdateTransform", AnyInst, null, new[] { typeof(RelativeTransform) }, null))
                ?.Invoke(_spot, new object[] { new RelativeTransform(_spotPos, _spotQ) });
            _spotMovedAt = _spotPos; _spotMovedQ = _spotQ;
        }
        catch (Exception e) { _spotWhy = "spot move: " + Inner(e); }
    }

    // ── the spot light's brightness, changed IN PLACE (ISpotLightEntity.SetParameters: intensity, cone, cookie, flare) ──
    private float _spotSetS = -1f;
    private void StopSpot()
    {
        _spotSetS = -1f;
        try { (_spot as IDisposable)?.Dispose(); } catch (Exception) { }
        DisposeRender(_spotRoot);
        _spot = null; _spotRoot = null;
    }
}
