#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;
namespace AeroMod;

/// <summary>The transonic vapour (Mode 6): its Mach bands, the cone loop round the body, the wingtip lines and the
/// one-effect spawn every vapour emitter goes through.</summary>
internal sealed partial class FlightField
{
    // ── Mode 6: TRANSONIC VAPOUR - the condensation cone at the grid's widest station, and sheets off the outline half a
    //    chord downwind; driven by the aero mod's real Mach, density and flow direction (AeroEntryFx.TryGetFlow, by
    //    reflection - D:\aero\docs\REFLECTION_AND_WHITELIST.md). Effects: the aero mod's Vapour_* (tools/particles/gen_vapor.py).

    /// <summary>Mach bands (in, full .. full, out): the cone and the edge sheets; density factor (rho / VapourRho)^0.5,
    /// capped at 1. The flow axis follows the real travel (AoA); past AxisTol degrees the field is placed again.
    /// VapourEdgeScale: the edge sprays' size; ChordShift: how far
    /// back along the chord the edge emitters sit (0.5 = mid-chord).</summary>
    public static float[] ConeBand = { 0.90f, 0.97f, 1.02f, 1.08f }, EdgeBand = { 0.80f, 0.90f, 1.02f, 1.10f };
    public static float VapourRho = 1.0f, AxisTol = 5f, VapourEdgeScale = 0.75f, ChordShift = 0.5f, VapourSpeed = 20f;   // (VapourSpeed 2.5 -> 10 with a quarter of the life: fast and tight - the user, 2026-10-09)
    /// <summary>TEST: a fixed Mach (0: the real one) - the shape at any speed.</summary>
    public static float MachOverride;
    /// <summary>DIAGNOSTIC: EdgeCollide 0: the edge without collisions.</summary>
    public static int EdgeCollide = 1;
    /// <summary>The cone as a LOOP of emitters (the user's idea): 0 the single ellipsoid emitter (old), 1 the plane's whole
    /// frontal silhouette, 2 its convex hull (a rounder loop) - at ConeStation of the hull's length from the nose, pushed
    /// ConeMargin metres out from the body (from the fuselage axis: the shadow cell with the longest chord), an emitter every
    /// ConeSpacing metres, each spraying ConeFlare outward + the rest downwind, ConeEmitScale big, ConeSpeed fast.</summary>
    public static int ConeLoop = 3;
    /// <summary>ConeLoop 3 (the user's design): ConeRing emitters evenly round the fuselage axis at the station, each
    /// PROJECTED onto the plane's outer skin along its angle (the farthest hull point in that angular slice, within
    /// ConeSlab metres of the station) - a collar on the skin, fuselage all round, the wings and fin where they cross it -
    /// each the thick wide ConeSpray (Vapour_ConeSpray), outward-and-back.</summary>
    public static int ConeRing = 48;
    public static float ConeSlab = 1.5f, ConeOutlier = 1.3f;
    /// <summary>Wingtip lines: the tip vortex trail (Vapour_Tip) from every outline point that sticks out furthest from
    /// the fuselage axis - a local maximum of its distance, at least TipMinFrac of the farthest - at that tip's trailing
    /// edge. Not transonic: tip vortices condense whenever fast in humid air.</summary>
    public static float TipMinFrac = 0.7f, TipScale = 6f, TipSpeed = 1f;   // (6: the trail moving with the air reads at a distance - 3 vanished)
    /// <summary>Flight mode (7): the vapour's spray (the plasma keeps DownMix / Jitter); the plasma's held time = the aero
    /// mod's entry strength (AeroEntryFx.EntryStrength: Mach 2 onset, density-scaled) x each line's weight, the spot light
    /// x the strength (placed again when it moves a quarter).</summary>
    public static float VapourDownMix = 0.97f, VapourJitter = 4f;
    private int _sub; private bool _noStop; private Vector3 _lastF;
    private float _entryS, _spotMul = 1f, _spotUsed = -1f;
    private readonly List<(object h, float w)> _plasma = new List<(object, float)>();
    /// <summary>Tip trails at ALL speeds, by flight regime: on from TipMinSpeed (full by 3x it), strength TipBase + TipLiftGain x
    /// the lift strength (a slow, high-lift approach shows them strongest - the vortex is the lift), x (1 + TipMachBoost x the
    /// transonic band), x density.</summary>
    public static float TipMinSpeed = 60f, TipBase = 0.3f, TipLiftGain = 0.8f, TipMachBoost = 0.5f;   // (a floor: at 0 the trails came out sparse and dotted - read as separate pieces)   // (from lift only, and from 60 m/s: a ship cruising at 44 m/s drew three fat trails)
    private float _speed;
    private static readonly Guid VapourTip = new Guid("5d0c7a3e-0107-4000-8000-000000000001");
    private float _tipI;
    private static readonly Guid VapourConeSpray = new Guid("5d0c7a3e-0106-4000-8000-000000000001");
    public static float ConeStation = 0.5f, ConeMargin = 1.5f, ConeSpacing = 1.2f, ConeFlare = 0.35f, ConeEmitScale = 3f, ConeSpeed = 3f;
    private static readonly Guid VapourEdge = new Guid("5d0c7a3e-0102-4000-8000-000000000001");
    private float _chordShift;
    private readonly List<(object h, float w, int kind)> _vapour = new List<(object, float, int)>();   // (kind 0 edge, 1 cone, 2 tip)
    private float _mach, _rho, _coneI, _edgeI;
    private long _flowAt;

    private float Smooth(float a, float b, float x) { float t = Math.Clamp((x - a) / Math.Max(1e-4f, b - a), 0f, 1f); return t * t * (3f - 2f * t); }
    private float Band(float[] k, float m) => Smooth(k[0], k[1], m) * (1f - Smooth(k[2], k[3], m));

    /// <summary>The cone as a loop: the frontal silhouette (or its convex hull) at one station along the hull, pushed out
    /// from the fuselage axis, an emitter every ConeSpacing metres spraying outward-and-back.</summary>
    private void ConeLoopEmitters(Vector3[,] P3, Vector3 f, int N, List<Vector3> pts)
    {
        // the station: ConeStation of the hull's length from the nose
        float zMax = float.MinValue, zMin = float.MaxValue;
        foreach (var q in pts) { float z = Vector3.Dot(q, f); zMax = Math.Max(zMax, z); zMin = Math.Min(zMin, z); }
        float zSt = zMax - ConeStation * (zMax - zMin);
        // the fuselage axis: the shadow cell with the longest chord (front - back)
        Vector3 axis = Vector3.Zero; float bestChord = -1f;
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++)
            if (_hullHas[i, j] && _hullBack[i, j] < float.MaxValue)
            {
                float ch = Vector3.Dot(_hullSurf[i, j], f) - _hullBack[i, j];
                if (ch > bestChord) { bestChord = ch; axis = _hullSurf[i, j] - f * Vector3.Dot(_hullSurf[i, j], f); }
            }
        if (ConeLoop == 3)
        {
            // n angles round the axis; each projected out onto the skin: the farthest hull point in its angular slice,
            // within ConeSlab of the station
            var far = new float[ConeRing]; var at = new Vector3[ConeRing];
            for (int k = 0; k < ConeRing; k++) far[k] = -1f;
            foreach (var q in pts)
            {
                float z = Vector3.Dot(q, f);
                if (Math.Abs(z - zSt) > ConeSlab) continue;
                Vector3 r = q - f * z - axis;
                float x = Vector3.Dot(r, _ga), y = Vector3.Dot(r, _gb), d = MathF.Sqrt(x * x + y * y);
                int k = (int)MathF.Floor((MathF.Atan2(y, x) + MathF.PI) / (2f * MathF.PI) * ConeRing) % ConeRing;
                if (d > far[k]) { far[k] = d; at[k] = q; }
            }
            // outliers culled: angles whose skin is far beyond the ring's median (the wings and fin crossing the station
            // pulled the ring out along them - it shrink-wrapped the plane; now they just pass through the cone)
            var radii = new List<float>(); for (int k = 0; k < ConeRing; k++) if (far[k] >= 0f) radii.Add(far[k]);
            radii.Sort(); float median = radii.Count > 0 ? radii[radii.Count / 2] : 0f;
            for (int k = 0; k < ConeRing; k++)
            {
                if (far[k] < 0f) continue;   // (no hull at this angle near the station)
                if (far[k] > ConeOutlier * median) continue;
                float ang = (k + 0.5f) / ConeRing * 2f * MathF.PI - MathF.PI;
                Vector3 outv = _ga * MathF.Cos(ang) + _gb * MathF.Sin(ang);
                Vector3 pos = at[k] - f * (Vector3.Dot(at[k], f) - zSt) + outv * ConeMargin;
                Vector3 dir = Vector3.Normalize(ConeFlare * outv + (1f - ConeFlare) * -f);
                VapourEffect(VapourConeSpray, pos, Quaternion.CreateFromTwoVectors(Vector3.UnitZ, dir), ConeEmitScale, 1f, true, ConeSpeed);
            }
            WingtipLines(f, N, axis);
            return;
        }
        // the silhouette, in the plane across the flow (a, b coordinates)
        var chains = Outline(P3, f, N);
        var loops = new List<List<Vector2>>();
        Vector2 P2(Vector3 v) => new Vector2(Vector3.Dot(v, _ga), Vector3.Dot(v, _gb));
        Vector3 P3d(Vector2 v) => _ga * v.X + _gb * v.Y;
        if (ConeLoop == 2)
        {
            // convex hull of every outline point (monotone chain)
            var all = new List<Vector2>();
            foreach (var c in chains) foreach (var v in c) all.Add(P2(v));
            all.Sort((u, w) => u.X != w.X ? u.X.CompareTo(w.X) : u.Y.CompareTo(w.Y));
            float Cross(Vector2 o, Vector2 u, Vector2 w) => (u.X - o.X) * (w.Y - o.Y) - (u.Y - o.Y) * (w.X - o.X);
            var hull = new List<Vector2>();
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                for (int k = 0; k < all.Count; k++)
                {
                    var pt = pass == 0 ? all[k] : all[all.Count - 1 - k];
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], pt) <= 0) hull.RemoveAt(hull.Count - 1);
                    hull.Add(pt);
                }
                hull.RemoveAt(hull.Count - 1);
            }
            loops.Add(hull);
        }
        else foreach (var c in chains) { var l = new List<Vector2>(); foreach (var v in c) l.Add(P2(v)); if (l.Count >= 3) loops.Add(l); }
        Vector2 ax = P2(axis);
        foreach (var loop in loops)
        {
            // along the closed loop every ConeSpacing metres
            float have = 0f;
            for (int k = 0; k < loop.Count; k++)
            {
                Vector2 u = loop[k], w = loop[(k + 1) % loop.Count];
                float l = (w - u).Length(); if (l < 1e-4f) continue;
                float at = 0f;
                while (have + (l - at) >= ConeSpacing)
                {
                    at += ConeSpacing - have; have = 0f;
                    Vector2 pt = u + (w - u) * (at / l);
                    Vector2 outv = pt - ax; outv = outv.LengthSquared() > 1e-4f ? Vector2.Normalize(outv) : new Vector2(0, 1);
                    Vector3 out3 = P3d(outv);
                    Vector3 pos = P3d(pt) + out3 * ConeMargin + f * zSt;
                    Vector3 dir = Vector3.Normalize(ConeFlare * out3 + (1f - ConeFlare) * -f);
                    VapourEffect(EdgeCollide == 0 ? new Guid("5d0c7a3e-0105-4000-8000-000000000001") : VapourEdge, pos,
                                 Quaternion.CreateFromTwoVectors(Vector3.UnitZ, dir), ConeEmitScale, 1f, true, ConeSpeed);
                }
                have += l - at;
            }
        }
    }

    public static string TipWhy = "";

    /// <summary>Wingtip lines: outline points (the frontal silhouette) furthest from the fuselage axis locally (a maximum over
    /// +-6 neighbours) and at least TipMinFrac of the farthest; each at its cell's rearmost hull point (the trailing edge).</summary>
    private int WingtipLines(Vector3 f, int N, Vector3 axis)
    {
        // the jutting points of the outline we already have (the user, 2026-10-07: not the literal wings) - one set per ship,
        // from its main grid (the largest frontal area of its joined grids; the others draw none)
        if (_server != null && !_server.FxTipOwner) { TipWhy = "not the ship's main grid"; return 0; }
        var chains = Outline(_P3, f, N);
        float rFar = 0f;
        foreach (var c in chains) foreach (var v in c) rFar = Math.Max(rFar, (v - f * Vector3.Dot(v, f) - axis).Length());
        var tips = new List<Vector3>();   // (every jutting point first; only the extremes are spawned - Extremes)
        foreach (var c in chains)
        {
            int cnt = c.Count;
            for (int i = 0; i < cnt; i++)
            {
                Vector3 pi = c[i] - f * Vector3.Dot(c[i], f);
                float r = (pi - axis).Length();
                if (r < TipMinFrac * rFar) continue;
                bool max = true;
                for (int d = -6; d <= 6 && max; d++)
                {
                    if (d == 0) continue;
                    var q = c[((i + d) % cnt + cnt) % cnt];
                    if ((q - f * Vector3.Dot(q, f) - axis).Length() > r) max = false;
                }
                if (!max) continue;
                // the trailing edge there: the rearmost hull point in its shadow cell
                int ci = Math.Clamp((int)MathF.Round((Vector3.Dot(c[i], _ga) - _gx0) / _gdx), 0, N);
                int cj = Math.Clamp((int)MathF.Round((Vector3.Dot(c[i], _gb) - _gy0) / _gdy), 0, N);
                float back = _hullBack[ci, cj] < float.MaxValue ? _hullBack[ci, cj] : Vector3.Dot(c[i], f);
                tips.Add(pi + f * back);
                i += 6;   // (one per tip)
            }
        }
        var idx = new List<int>(); for (int k = 0; k < tips.Count; k++) idx.Add(k);
        int n = 0;
        foreach (int k in Extremes(idx, j => tips[j], f))
        {
            // (world-space particles: the trail is laid in the air by the ship's own motion, no push backwards)
            VapourEffect(VapourTip, tips[k], Quaternion.CreateFromTwoVectors(Vector3.UnitZ, -f), TipScale, 1f, false, 0.01f, true);
            n++;
        }
        TipWhy = $"{n} of {tips.Count} jutting outline points (the extremes)";
        return n;
    }

    /// <summary>One vapour effect: scale (sizes, ring, speeds), its local weight (rate x it), held by Strengths.</summary>
    private void VapourEffect(Guid effect, Vector3 at, Quaternion q, float scale, float weight, bool cone, float speed = -1f, bool tip = false, float spawn = -1f)
    {
        if (!_draining && !(_relayout && !tip))
        {
            int g = tip ? _genTips : _genEdges;
            _spawnQueue.Enqueue(() => { if ((tip ? _genTips : _genEdges) == g) VapourEffect(effect, at, q, scale, weight, cone, speed, tip, spawn); });
            return;
        }
        void Fill(object prm)
        {
            var t = prm.GetType();
            if (spawn > 0f) t.GetField("EmitterSizeMultiplier").SetValue(prm, spawn);
            t.GetField("EmitterScaleMultiplier").SetValue(prm, Math.Max(0.1f, scale));
            t.GetField("VelocityMultiplier").SetValue(prm, Math.Max(0.01f, speed > 0f ? speed : VapourSpeed));
        }
        _spawnTip = tip;
        object h;
        try { h = SpawnEffect(_client, effect, new RelativeTransform(at, q), out object prm0, out _puffWhy, Fill); _vapPrm = prm0; }
        finally { _spawnTip = false; }
        object prm = _vapPrm;
        if (h == null) return;
        _puffs.Add(h);
        int kind = tip ? 2 : cone ? 1 : 0;
        _vapour.Add((h, weight, kind));
        if (tip && prm != null) { _tipPrm[h] = (prm, speed); _tipAt[h] = (at, q, -1f); _tipOrder.Add(h); }   // (its airspeed as sent: no rebuild at once)   // (its spawn line's length and its lead kept live: TipLine)
        float held = Math.Clamp((kind == 1 ? _coneI : kind == 2 ? _tipI : _edgeI) * weight, 0.001f, 0.999f);
        try { (_fixTime ??= h.GetType().GetMethod("FixEffectTime", AnyInst)).Invoke(h, new object[] { true, TimeSpan.FromSeconds(held) }); NoteFixed(h, held); }
        catch (Exception e) { _puffWhy = "vapour time: " + Inner(e); }
    }
}
