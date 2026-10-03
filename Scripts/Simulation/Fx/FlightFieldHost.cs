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
    /// <summary>The cone (the transonic shock's cone round the body): off until tuned.</summary>
    public static bool ConeOn;

    private bool _plasmaUp, _edgesUp, _tipsUp;
    private int _genPlasma, _genEdges, _genTips, _rebuildSeen;
    private AeroGridComponent _server;
    private object _renderParent;
    public string Status = "new";

    public FlightField(Keen.VRage.DCS.Components.Entity client, object renderParent)
    {
        _client = client; _renderParent = renderParent;
        _rebuildSeen = Rebuild;
    }

    /// <summary>A frame: the grid's published flow (from its server copy), its world transform. False: nothing to show
    /// (the caller may drop the field).</summary>
    public bool Update(AeroEntryFx.FlowPub p, in WorldTransform wt)
    {
        try
        {
            if (_rebuildSeen != Rebuild) { _rebuildSeen = Rebuild; DropAll(); _shapeKey = null; _hull = null; _hullWhy = null; }
            _server = p.Aero;
            Vector3 travel = p.TravelLocal;
            _mach = p.Mach; _rho = p.Density; _speed = p.Speed; _liftDir = p.LiftLocal; _liftCoef = p.LiftCoef; _flowFound = true;
            // the strength: an entry source owns it when there is one (the Orbital Mod's reentry), else Mach and density
            _entryS = p.ExtStrength >= 0f ? p.ExtStrength : AeroEntryFx.EntryStrength(_mach, _rho);
            if (travel.LengthSquared() < 0.5f) return false;
            travel = Vector3.Normalize(travel);
            if (_axis == default || Vector3.Dot(travel, _axis) < MathF.Cos(AxisTol * MathF.PI / 180f))
            {
                _axis = travel;   // (the AoA moved: the shadow, outline, weights and every group again)
                DropAll(); _shapeKey = null;
            }
            if (!Shape(out var f, out float R, out int N, out var pts)) return false;

            // the live strengths (each effect's fixed time), four times a second
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _flowAt >= System.Diagnostics.Stopwatch.Frequency / 4)
            {
                _flowAt = now;
                Strengths();
            }

            // the groups the regime needs
            bool wantPlasma = _entryS > 0.001f || _mach > AeroEntryFx.OnsetMach - PlasmaLead;
            bool dropPlasma = _entryS <= 0.001f && _mach < AeroEntryFx.OnsetMach - PlasmaLead - 0.1f;
            bool wantEdges = _mach > EdgeBand[0] - VapourLead && _mach < EdgeBand[3] + VapourLead && _rho > 0f;
            bool dropEdges = _mach < EdgeBand[0] - VapourLead - 0.05f || _mach > EdgeBand[3] + VapourLead + 0.05f;
            bool wantTips = _speed > TipMinSpeed;
            if (wantPlasma && !_plasmaUp) PlacePlasma(f, R, N);
            else if (dropPlasma && _plasmaUp) DropPlasma();
            if (wantEdges && !_edgesUp) PlaceEdges(f, R, N, pts);
            else if (dropEdges && _edgesUp) DropEdges();
            if (wantTips && !_tipsUp) PlaceTips(f, N);
            else if (!wantTips && _tipsUp && _speed < 0.5f * TipMinSpeed) DropTips();
            else if (_tipsUp && _tipSpeedUsed > 0f && MathF.Abs(_speed - _tipSpeedUsed) > TipRespawn * _tipSpeedUsed) { DropTips(); PlaceTips(f, N); }   // (their length is fixed at spawn)

            // the spot light follows the grid (its own render root)
            if (_spotRoot != null) UpdateRootTransform(_spotRoot, wt);
            // spawning a few a frame
            if (_spawnQueue.Count > 0)
            {
                _draining = true;
                try { for (int k = 0; k < SpawnPerTick && _spawnQueue.Count > 0 && _puffWhy == null; k++) _spawnQueue.Dequeue()(); }
                finally { _draining = false; }
            }
            Status = $"M {_mach:F2} rho {_rho:F2} plasma {_entryS:F2}{(p.ExtStrength >= 0f ? " (entry source)" : "")} edge {_edgeI:F2} tip {_tipI:F2}"
                   + $" | groups {(_plasmaUp ? "P" : "-")}{(_edgesUp ? "V" : "-")}{(_tipsUp ? "T" : "-")} effects {_puffs.Count} queued {_spawnQueue.Count}"
                   + (_puffWhy != null ? " | " + _puffWhy : "") + (_spotWhy != null ? " | spot: " + _spotWhy : "") + (_hullWhy != null ? " | " + _hullWhy : "");
            return true;
        }
        catch (Exception e) { Status = "failed: " + Inner(e); DropAll(); return false; }
    }

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
        string key = string.Join(",", _axis, pts.Count, Res, Blur, Standoff, Sweep, Margin, MinStandoff, Dilate, RimSmooth, ChordGap);
        if (key != _shapeKey)
        {
            DropAll();
            ShockGrid(f, a, b, R, pts, out _P3, out _past);
            _ring = SmoothRing(Rim(_P3, _past, a, b, out _ringC), RimSmooth);
            _shapeKey = key;
        }
        N = _P3.GetLength(0) - 1;
        if (_lightAt == default) LightPlace(_P3, pts, f);
        return true;
    }

    /// <summary>The live strengths: vapour band x density, tips by lift and speed, plasma lines and the spot by _entryS.</summary>
    private void Strengths()
    {
        float m = MachOverride > 0f ? MachOverride : _mach;
        float dens = MathF.Min(1f, MathF.Sqrt(Math.Max(0f, _rho) / Math.Max(0.01f, VapourRho)));
        _coneI = ConeOn ? Band(ConeBand, m) * dens : 0f;
        _edgeI = Band(EdgeBand, m) * dens;
        float on = Math.Clamp((_speed - TipMinSpeed) / (2f * TipMinSpeed), 0f, 1f); on = on * on * (3f - 2f * on);
        float coef = LiftTest > 0f ? LiftTest : _liftCoef;
        _tipI = Math.Min(1f, on * Math.Clamp(TipBase + TipLiftGain * coef, 0f, 1f) * (1f + TipMachBoost * Band(EdgeBand, m)) * dens);
        foreach (var (h, w, kind) in _vapour)
            FixTime(h, Math.Clamp((kind == 1 ? _coneI : kind == 2 ? _tipI : _edgeI) * w, 0.001f, 0.999f));
        foreach (var (h, w) in _plasma)
            FixTime(h, Math.Clamp(_entryS * w, 0.00001f, 0.999f));
        // the spot: off below 2 %, else Spot x strength, placed again when it moved a quarter
        if (Spot > 0f && _P3 != null)
        {
            bool want = _plasmaUp && _entryS > 0.02f;
            if (!want) { if (_spotUsed > 0f) StopSpot(); _spotUsed = -1f; }
            else if (_spotUsed < 0f || MathF.Abs(_entryS - _spotUsed) > 0.25f * _spotUsed)
            {
                StopSpot(); _spotMul = _entryS; _spotUsed = _entryS;
                SpawnSpot(_axis, _shapeR, _P3.GetLength(0) - 1);
            }
        }
    }

    // ── the groups ──

    private void PlacePlasma(Vector3 f, float R, int N)
    {
        _genPlasma++; _noStop = true; _lastF = f;
        DownMix = PlasmaDownMix; Jitter = PlasmaJitter;
        try { _sub = 5; SpawnPuffs(_P3, _past, _ring, f, R); }   // (the spot is the strength's: Strengths places it)
        finally { _sub = 0; _noStop = false; }
        _spotUsed = -1f;
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
        WingtipLines(f, N, Vector3.Zero);
        _tipsUp = true;
    }

    private void DropPlasma()
    {
        _genPlasma++;
        foreach (var (h, _) in _plasma) { _puffs.Remove(h); DisposeEffect(h); }
        _plasma.Clear(); StopSpot(); _spotUsed = -1f; _plasmaUp = false;
    }

    private void DropEdges() { _genEdges++; DropVapour(k => k != 2); _edgesUp = false; }
    private void DropTips() { _genTips++; DropVapour(k => k == 2); _tipsUp = false; _tipSpeedUsed = -1f; }

    private void DropVapour(Func<int, bool> which)
    {
        for (int i = _vapour.Count - 1; i >= 0; i--)
        {
            if (!which(_vapour[i].kind)) continue;
            var h = _vapour[i].h; _vapour.RemoveAt(i); _puffs.Remove(h); DisposeEffect(h);
        }
    }

    /// <summary>Everything off (a new axis or shape, out of range, the grid gone).</summary>
    public void DropAll()
    {
        _genPlasma++; _genEdges++; _genTips++;
        _noStop = false; StopPuffs();
        _plasmaUp = _edgesUp = _tipsUp = false; _tipSpeedUsed = -1f; _lightAt = default;
    }

    // ── the aero model, read directly ──

    /// <summary>The hull's face centres (grid space, m): every chunk's occluder points of the force table.</summary>
    private List<Vector3> HullPoints()
    {
        var chunks = _server?.FxChunks;
        if (chunks == null) { _hullWhy = "the grid has no chunk table yet"; return null; }
        if (_hull != null && ReferenceEquals(_hullFor, chunks)) return _hull;
        var pts = new List<Vector3>();
        foreach (var list in chunks.Occluders) foreach (var p16 in list) pts.Add(p16.V);
        if (pts.Count == 0) { _hullWhy = "no hull points"; return null; }
        _hull = pts; _hullFor = chunks; _hullWhy = null;
        return pts;
    }
    private object _hullFor;

    /// <summary>Every chunk of the force table: its centre, Newtonian drag along the flow f and frontal area (its share
    /// for f, at unit dynamic pressure; bilinear over the direction table's face, as ForceTable.Locate).</summary>
    private void ChunkDrag(Vector3 f)
    {
        _chunkDrag.Clear(); _dragWhy = null;
        var table = _server?.FxChunks;
        if (table == null) { _dragWhy = "no chunk table"; return; }
        int n = table.N, count = table.ChunkCount;
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
            var sh = table.GetShare(c);
            float f1x = 0, f1y = 0, f1z = 0, fr = 0;
            foreach (var (k, wt) in corners) { int o = k * 13; f1x += sh[o + 3] * wt; f1y += sh[o + 4] * wt; f1z += sh[o + 5] * wt; fr += sh[o + 12] * wt; }
            float drag = -(f1x * f.X + f1y * f.Y + f1z * f.Z);
            var (lo, hi) = table.Bounds[c];
            if (fr > 1e-3f) _chunkDrag.Add(((lo + hi) * 0.5f, Math.Max(0f, drag), fr));
        }
        if (_chunkDrag.Count == 0) _dragWhy = "no windward chunks";
    }

    /// <summary>One tip line at each of the aero mod's wing tips (trailing edge), weighted by how much the wing lifts along
    /// the lift (|normal . lift|: a fin at zero sideslip makes no tip vortex); length from airspeed. -1: no wings.</summary>
    private int AeroWingTips(Vector3 f)
    {
        var e = _server?.Entity;
        if (e == null) { TipWhy = "no grid"; return -1; }
        int n = AeroEntryFx.WingTips(e, _tipPts, _tipNs);
        if (n == 0) { TipWhy = "no wings"; return -1; }
        Vector3 L = LiftTest > 0f ? Vector3.UnitY - f * Vector3.Dot(Vector3.UnitY, f) : _liftDir;
        bool haveL = L.LengthSquared() > 1e-6f; if (haveL) L = Vector3.Normalize(L);
        var q = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, -f);
        float spd = MachOverride > 0f ? MachOverride * 340f : _speed;
        _tipSpeedUsed = Math.Max(1f, spd);
        float len = Math.Clamp(TipLenBase + TipLenPerMs * spd, 5f, TipLenMax);
        float vm = TipSpeed * len / TipLenAtOne;
        int placed = 0;
        for (int i = 0; i < n; i++)
        {
            float w = haveL ? Math.Clamp(MathF.Abs(Vector3.Dot(_tipNs[i], L)), 0f, 1f) : 1f;
            if (w < 0.2f) continue;
            VapourEffect(VapourTip, _tipPts[i], q, TipScale, w, false, vm, true);
            placed++;
        }
        TipWhy = $"{placed}/{n} wing tips, {len:F0} m at {spd:F0} m/s";
        return placed;
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
            if (!DefinitionManager.Instance.TryGetDefinition(effect, out Definition def) || def == null) { why = "effect not loaded: " + effect; return null; }
            prm = Activator.CreateInstance(_tParams);
            init?.Invoke(prm);   // (a boxed struct: the sets stick)
            var p = _spawn.GetParameters();
            var args = new object[p.Length];
            for (int i = 0; i < args.Length; i++) args[i] = p[i].HasDefaultValue ? p[i].DefaultValue : null;
            args[0] = at; args[1] = def; args[2] = prm; args[3] = _renderParent;
            for (int i = 4; i < args.Length; i++) if (p[i].ParameterType == typeof(string)) args[i] = "AeroFlight";
            object h = _spawn.Invoke(_fxSession, args);
            if (h == null) why = "spawn refused";
            return h;
        }
        catch (Exception e) { why = "spawn: " + Inner(e); return null; }
    }

    private static void FixTime(object h, float seconds)
    {
        _fixTime ??= h.GetType().GetMethod("FixEffectTime", AnyInst);
        _fixTime.Invoke(h, new object[] { true, TimeSpan.FromSeconds(seconds) });
    }

    private static void DisposeEffect(object h)
    {
        if (h == null) return;
        try
        {
            _dispose ??= h.GetType().GetMethod("Dispose", AnyInst, null, new[] { typeof(bool) }, null);
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
            float zFront = float.MinValue; Vector3 mid = Vector3.Zero; int cnt = 0;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
                if (_hullHas[i, j]) { var q = _hullSurf[i, j]; zFront = Math.Max(zFront, Vector3.Dot(q, f)); mid += q - f * Vector3.Dot(q, f); cnt++; }
            if (cnt == 0) { _spotWhy = "no hull"; return; }
            mid /= cnt;
            float d = SpotDist * R;
            Vector3 at = mid + f * (zFront + d);
            float cone = 2f * MathF.Atan(SpotCover / Math.Max(0.1f, SpotDist));
            var q2 = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, SpotFlip ? -f : f);   // (the engine's forward is -Z)
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
            _spotWhy = null;
        }
        catch (Exception e) { _spotWhy = "spot: " + Inner(e); StopSpot(); }
    }

    private void StopSpot()
    {
        try { (_spot as IDisposable)?.Dispose(); } catch (Exception) { }
        DisposeRender(_spotRoot);
        _spot = null; _spotRoot = null;
    }
}
