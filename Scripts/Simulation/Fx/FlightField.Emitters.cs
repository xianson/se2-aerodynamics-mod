#pragma warning disable
using System;
using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;
namespace AeroMod;

/// <summary>Where the plasma's and the vapour sheets' emitters go: the hull shadow's outline cut into line emitters, each
/// weighted by the local drag (the force table's chunks x the surface slope), sprayed along the hull's normal there.</summary>
internal sealed partial class FlightField
{
    /// <summary>The hull shadow's outline as closed chains of grid-space points (marching squares over _hullHas,
    /// outside the grid empty): each point midway between a hull node and an empty one, at the hull node's depth.</summary>
    private List<List<Vector3>> Outline(Vector3[,] P3, Vector3 f, int N)
    {
        bool Has(int i, int j) => i >= 0 && j >= 0 && i <= N && j <= N && _hullHas[i, j];
        Vector3 Plane(int i, int j)
        {
            int ci = Math.Clamp(i, 0, N), cj = Math.Clamp(j, 0, N);
            Vector3 q = P3[ci, cj] - f * Vector3.Dot(P3[ci, cj], f);
            // (outside the grid: one cell further on)
            if (i != ci || j != cj) q += (P3[Math.Min(N, ci + 1), cj] - P3[ci, cj]) * (i - ci) + (P3[ci, Math.Min(N, cj + 1)] - P3[ci, cj]) * (j - cj);
            return q - f * Vector3.Dot(q, f);
        }
        // an edge between node (i,j) and its +i (dir 0) or +j (dir 1) neighbour, crossing the outline: its point
        var pts = new Dictionary<long, Vector3>();
        long Key(int i, int j, int d) => ((long)(i + 2) * 1000 + (j + 2)) * 2 + d;
        Vector3 EdgePoint(int i, int j, int d)
        {
            int i2 = d == 0 ? i + 1 : i, j2 = d == 0 ? j : j + 1;
            var (hi, hj) = Has(i, j) ? (i, j) : (i2, j2);
            float front = Vector3.Dot(_hullSurf[hi, hj], f), back = _hullBack != null && _hullBack[hi, hj] < float.MaxValue ? _hullBack[hi, hj] : front;
            return (Plane(i, j) + Plane(i2, j2)) * 0.5f + f * (front - _chordShift * (front - back));
        }
        var adj = new Dictionary<long, List<long>>();
        void Link(long x, long y) { (adj.TryGetValue(x, out var lx) ? lx : adj[x] = new List<long>()).Add(y); (adj.TryGetValue(y, out var ly) ? ly : adj[y] = new List<long>()).Add(x); }
        for (int i = -1; i <= N; i++)
            for (int j = -1; j <= N; j++)
            {
                // the square's edges: bottom (i,j)-(i+1,j), right (i+1,j)-(i+1,j+1), top (i,j+1)-(i+1,j+1), left (i,j)-(i,j+1)
                bool c0 = Has(i, j), c1 = Has(i + 1, j), c2 = Has(i + 1, j + 1), c3 = Has(i, j + 1);
                var cut = new List<long>(4);
                if (c0 != c1) { long k = Key(i, j, 0); pts[k] = EdgePoint(i, j, 0); cut.Add(k); }
                if (c1 != c2) { long k = Key(i + 1, j, 1); pts[k] = EdgePoint(i + 1, j, 1); cut.Add(k); }
                if (c3 != c2) { long k = Key(i, j + 1, 0); pts[k] = EdgePoint(i, j + 1, 0); cut.Add(k); }
                if (c0 != c3) { long k = Key(i, j, 1); pts[k] = EdgePoint(i, j, 1); cut.Add(k); }
                if (cut.Count == 2) Link(cut[0], cut[1]);
                else if (cut.Count == 4) { Link(cut[0], cut[1]); Link(cut[2], cut[3]); }
            }
        var chains = new List<List<Vector3>>();
        var seen = new HashSet<long>();
        foreach (var start in adj.Keys)
        {
            if (seen.Contains(start)) continue;
            var chain = new List<Vector3>();
            long cur = start, prev = long.MinValue;
            while (cur != long.MinValue && seen.Add(cur))
            {
                chain.Add(pts[cur]);
                long next = long.MinValue;
                foreach (var nb in adj[cur]) if (nb != prev && !seen.Contains(nb)) { next = nb; break; }
                prev = cur; cur = next;
            }
            if (chain.Count >= 3) chains.Add(chain);
        }
        return chains;
    }

    /// <summary>Line emitters along the outline: each chain smoothed, cut into SegLen segments, one effect per segment.</summary>
    private void SpawnLines(Vector3[,] P3, Vector3 f, float R, int N, Quaternion turn)
    {
        float zFront = float.MinValue;
        for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (_hullHas[i, j]) zFront = Math.Max(zFront, Vector3.Dot(_hullSurf[i, j], f));
        Vector3 lift = f * (SurfaceLift * R);
        foreach (var raw in Outline(P3, f, N))
        {
            // smoothed (closed: the chain wraps)
            var c = raw;
            for (int k = 0; k < LineSmooth; k++)
            {
                var t = new List<Vector3>(c.Count);
                for (int i = 0; i < c.Count; i++) t.Add((c[(i - 1 + c.Count) % c.Count] + c[i] * 2f + c[(i + 1) % c.Count]) * 0.25f);
                c = t;
            }
            // cut into SegLen pieces along the (closed) chain
            Vector3 segStart = c[0]; float have = 0f;
            for (int i = 1; i <= c.Count; i++)
            {
                Vector3 a = c[i - 1], b = c[i % c.Count];
                float l = (b - a).Length();
                if (l < 1e-4f) continue;
                Vector3 dir = (b - a) / l;
                float at = 0f;
                float seg = SegLen / Math.Max(1, LineDensity);
                while (have + (l - at) >= seg)
                {
                    float take = seg - have;
                    at += take;
                    Vector3 segEnd = a + dir * at;
                    LineSegment(segStart, segEnd, f, R, zFront, lift);
                    if (_puffWhy != null) return;
                    segStart = segEnd; have = 0f;
                }
                have += l - at;
            }
        }
    }

    private bool _draining;
    private bool _spawnTip; private object _vapPrm;   // (a tip spawns on the grid's own root, not the swing's)

    private void LineSegment(Vector3 p0, Vector3 p1, Vector3 f, float R, float zFront, Vector3 lift)
    {
        if (!_draining && !_relayout)
        {
            // (queued: flight mode's pass (_sub) and the vapour's own spray captured now - they are restored after the pass)
            int s7 = _sub; float dm = DownMix, jt = Jitter; int g7 = s7 == 6 ? _genEdges : _genPlasma;
            _spawnQueue.Enqueue(() =>
            {
                if ((s7 == 6 ? _genEdges : _genPlasma) != g7) return;
                int o7 = _sub; float odm = DownMix, ojt = Jitter;
                _sub = s7; DownMix = dm; Jitter = jt;
                try { LineSegment(p0, p1, f, R, zFront, lift); } finally { _sub = o7; DownMix = odm; Jitter = ojt; }
            });
            return;
        }
        Vector3 sdir = p1 - p0; sdir -= f * Vector3.Dot(sdir, f);
        float len = sdir.Length();
        if (len < 0.05f * SegLen) return;   // (a run straight along the flow: no width across it)
        sdir /= len;
        // local X along the segment, +Z downwind (-f): forward (-Z) = f, right (X) = sdir, up = sdir x f
        var q = Quaternion.CreateFromOrthonormalBasis(f, Vector3.Cross(sdir, f), sdir);
        // (checked, not assumed: the first lines sprayed outward - the basis's convention is the inverse one here)
        if (Vector3.Dot(Vector3.Transform(Vector3.UnitZ, q), -f) < 0.99f || Vector3.Dot(Vector3.Transform(Vector3.UnitX, q), sdir) < 0.99f)
            q = Quaternion.Inverse(q);
        if (Vector3.Dot(Vector3.Transform(Vector3.UnitZ, q), -f) < 0.99f) return;
        Vector3 mid = (p0 + p1) * 0.5f + lift;
        bool front = zFront - Vector3.Dot(mid, f) < FrontDepth * R;
        // (a stretched spawn box cannot be a line: the GPU emitter sends each particle along normalize(its spawn offset) -
        //  Shaders/Transparent/Particles/ParticleEmission.hlsl - so a long box sprays along itself. Round point emitters,
        //  close together along the outline, instead.)
        Guid e = SparkColor == 3 && LineDensity > 1 ? RateGuid(front ? 3 : 4, LineRate) : front ? ShockSparks : TailSparks;
        float held = WeightTime(mid - lift, LineDensity);
        if (held == 0f) return;
        if (_sub == 6)
        {
            // (the vapour sheets: the same placement and spray direction, its own effect; the weight at heat 1 is kept and
            //  Strengths multiplies it by the band's eased strength every tenth of a second)
            float w = held > 0f ? held / Math.Max(1e-3f, Heat) : 1f;
            w *= SuctionSide(mid - lift, f);
            if (RateJitter > 0f) w *= 1f + RateJitter * ((float)_rng.NextDouble() * 2f - 1f);
            if (DownMix >= 0f)
            {
                Vector3 rad0 = mid - _stagMid; rad0 -= f * Vector3.Dot(rad0, f);
                Vector3 radU0 = rad0.LengthSquared() > 1e-4f ? Vector3.Normalize(rad0) : sdir;
                var hn0 = HullNormal(mid - lift, f);
                Vector3 along0 = hn0 != null ? radU0 - Vector3.Dot(radU0, hn0.Value) * hn0.Value : Vector3.Zero;
                along0 -= f * Vector3.Dot(along0, f);
                if (along0.LengthSquared() > 1f) along0 = Vector3.Normalize(along0);
                q = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, Vector3.Normalize(DownMix * -f + (1f - DownMix) * along0));
            }
            Vector3 at0 = mid;
            if (SnapToHull)
            {
                var hit = NearestHull(mid - lift, SnapRadius);
                if (hit == null) return;   // (no skin within SnapRadius: an emitter in the air would be a detached line)
                var hn1 = HullNormal(hit.Value, f);
                at0 = hit.Value + (hn1 ?? f) * SnapLift;
            }
            VapourEffect(EdgeCollide == 0 ? new Guid("5d0c7a3e-0105-4000-8000-000000000001") : VapourEdge, at0, q, VapourEdgeScale, w, false, -1f, false, VapourSpawn);
            return;
        }
        if (held > 0f && RateJitter > 0f) held = Math.Clamp(held * (1f + RateJitter * ((float)_rng.NextDouble() * 2f - 1f)), 0.001f, 0.999f);
        if (held > 0f) e = Recipe ? RecipeGuid(!front) : RateGuid(TexKind(!front), StreakField);
        if (DownMix >= 0f)
        {
            // ALONG the surface (in its plane, not off it): out from the ship's axis, flattened onto the hull there
            // (a wing edge: spanwise to the tip; the fuselage: round it); with no hull normal, along the outline
            Vector3 rad = mid - _stagMid; rad -= f * Vector3.Dot(rad, f);
            Vector3 radU = rad.LengthSquared() > 1e-4f ? Vector3.Normalize(rad) : sdir;
            var hn = HullNormal(mid - lift, f);
            Vector3 along = hn != null ? radU - Vector3.Dot(radU, hn.Value) * hn.Value : sdir * MathF.Sign(Vector3.Dot(sdir, radU) + 1e-6f);
            along -= f * Vector3.Dot(along, f);   // (across the flow: the downwind part is DownMix's)
            // NOT renormalised: where "outward" runs into the surface (a wing's underside: out = down, the surface
            // flat) almost nothing is left in its plane and the side push fades to none - renormalised (or falling
            // back to plain outward) it sprayed straight down off the wing in combs
            if (along.LengthSquared() > 1f) along = Vector3.Normalize(along);
            Vector3 dir = Vector3.Normalize(DownMix * -f + (1f - DownMix) * along);
            if (Jitter > 0f)
            {
                float t = MathF.Tan(Jitter * MathF.PI / 180f);
                var r = new Vector3((float)_rng.NextDouble() * 2 - 1, (float)_rng.NextDouble() * 2 - 1, (float)_rng.NextDouble() * 2 - 1);
                r -= dir * Vector3.Dot(r, dir);
                dir = Vector3.Normalize(dir + r * t);
            }
            q = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, dir);
        }
        else if (NormalSource == 1 && SurfaceSpray)
        {
            var hn = HullNormal(mid - lift, f);
            if (hn != null) q = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, SprayDir(mid - lift, f, hn.Value));
        }
        object h = SpawnEffect(_client, e, new RelativeTransform(mid, q), out object prm, out _puffWhy, FillSparks);
        if (h == null) return;
        _puffs.Add(h);
        try
        {
            SetParamsIfChanged(h, prm);
            { float w7 = held > 0f ? held / Math.Max(1e-3f, Heat) : 1f; _plasma.Add((h, w7)); held = Math.Clamp(_entryS * w7, 0.00001f, 0.999f); }
            if (held > 0f) { (_fixTime ??= h.GetType().GetMethod("FixEffectTime", AnyInst)).Invoke(h, new object[] { true, TimeSpan.FromSeconds(held) }); NoteFixed(h, held); }
        }
        catch (Exception ex) { _puffWhy = "set: " + Inner(ex); }
    }

    /// <summary>A plasma spark emitter's parameters (the line segments and the baked slots).</summary>
    private static void FillSparks(object prm)
    {
        var t = prm.GetType();
        t.GetField("EmitterScaleMultiplier").SetValue(prm, LineScale);
        t.GetField("VelocityMultiplier").SetValue(prm, Math.Max(0.01f, SparkSpeed));
        t.GetField("ColorMultiplier").SetValue(prm, new ColorSRGB(SparkBright, SparkBright, SparkBright, 1f));
        t.GetField("EmitterSizeMultiplier").SetValue(prm, SpawnSize);
        t.GetField("ParticleAspectRatio").SetValue(prm, SparkAspect);
        if (Strict) t.GetField("VelocityDirection").SetValue(prm, Vector3.UnitZ);
    }

    /// <summary>The local pressure at p: the chunks' drag over their frontal area, Gaussian-blended (sigma 0.6 chunk).</summary>
    private float LocalPressure(Vector3 p)
    {
        long tP = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return LocalPressure0(p); } finally { BkPressure += AeroCost.Ms(tP); }
    }
    private float LocalPressure0(Vector3 p)
    {
        if (_chunkDrag.Count == 0) return 1f;
        float sig2 = 2f * (0.6f * 8f) * (0.6f * 8f), sd = 0f, sf = 0f;
        foreach (var (c, drag, fr) in _chunkDrag)
        {
            float d2 = (c - p).LengthSquared();
            if (d2 > 9f * 64f) continue;
            float g = MathF.Exp(-d2 / sig2);
            sd += g * drag; sf += g * fr;
        }
        return sf > 1e-4f ? sd / sf : 0f;
    }

    /// <summary>cos^2 of the windward surface's slope at p (from the shadow grid's depth), 1 if no hull there.</summary>
    private float SurfaceCos2(Vector3 p, Vector3 f)
    {
        if (_hullHas == null) return 1f;
        int N = _hullHas.GetLength(0) - 1;
        int i = (int)MathF.Round((Vector3.Dot(p, _ga) - _gx0) / _gdx), j = (int)MathF.Round((Vector3.Dot(p, _gb) - _gy0) / _gdy);
        // the nearest hull cell (an outline point sits half a cell out)
        int bi = -1, bj = -1;
        for (int r = 0; r <= 2 && bi < 0; r++)
            for (int di = -r; di <= r && bi < 0; di++) for (int dj = -r; dj <= r; dj++)
            { int ii = i + di, jj = j + dj; if (ii >= 0 && jj >= 0 && ii <= N && jj <= N && _hullHas[ii, jj]) { bi = ii; bj = jj; break; } }
        if (bi < 0) return 1f;
        float Z(int ii, int jj) => Vector3.Dot(_hullSurf[ii, jj], f);
        bool H(int ii, int jj) => ii >= 0 && jj >= 0 && ii <= N && jj <= N && _hullHas[ii, jj];
        float gx = 0, gy = 0;
        if (H(bi + 1, bj) && H(bi - 1, bj)) gx = (Z(bi + 1, bj) - Z(bi - 1, bj)) / (2 * _gdx);
        else if (H(bi + 1, bj)) gx = (Z(bi + 1, bj) - Z(bi, bj)) / _gdx;
        else if (H(bi - 1, bj)) gx = (Z(bi, bj) - Z(bi - 1, bj)) / _gdx;
        else gx = 3f;   // (a lone cell across: a thin edge seen side on)
        if (H(bi, bj + 1) && H(bi, bj - 1)) gy = (Z(bi, bj + 1) - Z(bi, bj - 1)) / (2 * _gdy);
        else if (H(bi, bj + 1)) gy = (Z(bi, bj + 1) - Z(bi, bj)) / _gdy;
        else if (H(bi, bj - 1)) gy = (Z(bi, bj) - Z(bi, bj - 1)) / _gdy;
        else gy = 3f;
        return 1f / (1f + gx * gx + gy * gy);   // (cos^2 of the normal (-gx, -gy, 1) to the flow)
    }

    /// <summary>Surface emitters spray along the hull (SurfaceSpray), SprayOut of a normal outward, SurfaceOff x R off it.</summary>
    public static bool SurfaceSpray = true;
    public static float SprayOut = 0.35f, SurfaceOff = 0.01f;
    private Vector3 _stagMid;
    /// <summary>Where the spray's surface normal comes from: 0 the shadow grid's depth slope (front surface only),
    /// 1 the HULL - a plane fitted to its face centres within NormalRadius of the emitter (every emitter, the outline
    /// too: its spray flares out off the edges). Oriented to face the flow and away from the ship's axis.</summary>
    public static int NormalSource = 1;   // (the hull normals steer the spray; Accel bends it back downwind)
    /// <summary>(Accel: no longer used - the user GravityForce never reached the GPU (read once at spawn, and the gravity
    /// multiplier it needs attaches a planet-gravity probe that overwrote it, world-space, in the sparks' local frame:
    /// they fell sideways). The push is the effects' AccelerationFactor, along each spark's own downwind way.)
    /// SpawnSize: the spawn sphere x (EmitterSizeMultiplier - their size and speed unchanged): sparks born at the skin.</summary>
    public static float Accel = 60f, SpawnSize = 0.3f;
    /// <summary>The weighted sparks' drawn length: StreakMultiplier 4 (the original), 1, 0.3 or 0.1 (a spark is drawn
    /// 0.5 x speed x it long - pushed downwind they speed up: 4 made whiskers).</summary>
    public static float StreakLen = 0.3f;
    /// <summary>The sparks' length : width (ParticleAspectRatio: the streak's length x it, ParticleRenderingVertex.hlsl).
    /// A streak is (size x 0.5 x speed x StreakMultiplier) long but a whole size wide: short, slow sparks came out as
    /// wide as long - flecks across their motion.</summary>
    public static float SparkAspect = 0.15f;   // (fast sparks drawn compact: at 1 their speed stretched them into long whiskers - the user, 2026-10-09)
    /// <summary>The weighted sparks' texture: 0 FireSparks' (soft: splotches), 1 Thorn (the rain streaks': a point
    /// with a soft falloff - a long tapered diamond), 2 ThrusterFlame_Dot (a firmer dot).</summary>
    public static int SparkTex = 5;
    private int TexKind(bool tail) => SparkTex == 1 ? (tail ? 8 : 7) : SparkTex == 2 ? (tail ? 10 : 9) : (tail ? 6 : 5);
    /// <summary>SparkTex 3-6: the game's own spark emitters as they are (gen_plasma_field.py RECIPES) - 3 grinder main
    /// spark, 4 welder sparks, 5 explosion sparks, 6 directional sparks - in our colours, drag-weighted.</summary>
    private bool Recipe => SparkTex >= 3;
    private Guid RecipeGuid(bool tail) => new Guid($"5d0c7a3e-{(11 + 2 * (SparkTex - 3) + (tail ? 1 : 0)):x4}-4000-8000-000000000001");
    private int StreakField => StreakLen >= 3f ? 0 : (int)MathF.Round(StreakLen * 100f);
    public static float NormalRadius = 1.5f;
    /// <summary>Outline emitters' spray: DownMix of it downwind, the rest ALONG the surface there - in the hull's plane,
    /// out from the ship's axis (negative DownMix: off - the older modes); each tilted at random up to Jitter degrees, its rate varied up to
    /// +-RateJitter (a fixed pattern per spawn, so it doesn't flicker on respawn).</summary>
    public static float PlasmaDownMix = 0.7f, PlasmaJitter = 12f, RateJitter = 0.3f;
    private float DownMix = 0.7f, Jitter = 12f;   // (this pass's: PlasmaDownMix / VapourDownMix)
    /// <summary>Vapour edge emitters snapped onto the nearest real hull face centre within SnapRadius metres, SnapLift out
    /// along its surface normal; their spawn sphere x VapourSpawn (EmitterSizeMultiplier) - born on the skin.</summary>
    public static bool SnapToHull = true;
    /// <summary>The SUCTION side: vapour forms on the side the lift points to (low pressure), little on the other. Each
    /// edge emitter's weight x (1 - k) + k x clamp(0.5 + 1.5 n.lift), k = LiftSens x the lift strength (capped 1); the
    /// lift from the aero mod (its force across the travel), or LiftTest (along the grid's up across the flow) when > 0 -
    /// a held test grid has no real air, so no aero lift. Placed again when it changes much.</summary>
    public static float LiftSens = 1.5f, LiftTest;
    private Vector3 _liftDir; private float _liftCoef;
    public static float SnapRadius = 2f, SnapLift = 0.05f, VapourSpawn = 0.12f;   // (VapourEdgeScale 1.5 -> 0.75, VapourSpawn 0.3 -> 0.12: "way tighter" - the user, 2026-10-09)
    /// <summary>Every spark held on its emitter's spray direction, every frame (the instance's VelocityDirection: the GPU
    /// re-applies it - ParticleSimulation.hlsl); off: the emitter's cone and speed spread show.</summary>
    public static bool Strict;
    private Random _rng = new Random(1);
    private Dictionary<long, List<Vector3>> _hullHash;
    private List<Vector3> _hashFor;

    private long HKey(int x, int y, int z) => ((long)(x & 0x1FFFFF) << 42) | ((long)(y & 0x1FFFFF) << 21) | (long)(z & 0x1FFFFF);

    /// <summary>The suction-side factor at p: (1 - k) + k clamp(0.5 + 1.5 n.lift); 1 with no lift.</summary>
    private float SuctionSide(Vector3 p, Vector3 f)
    {
        Vector3 L; float coef;
        if (LiftTest > 0f) { L = Vector3.UnitY - f * Vector3.Dot(Vector3.UnitY, f); coef = LiftTest; }
        else { L = _liftDir; coef = _liftCoef; }
        if (L.LengthSquared() < 1e-6f || coef <= 0f) return 1f;
        L = Vector3.Normalize(L);
        var hit = NearestHull(p, SnapRadius);
        var n = HullNormal(hit ?? p, f);
        if (n == null) return 1f;
        float k = Math.Min(1f, LiftSens * coef);
        return (1f - k) + k * Math.Clamp(0.5f + 1.5f * Vector3.Dot(n.Value, L), 0f, 1f);
    }

    /// <summary>The nearest hull face centre to p within r metres (the hull hash of HullNormal), or null.</summary>
    private Vector3? NearestHull(Vector3 p, float r)
    {
        if (_hull == null) return null;
        if (!ReferenceEquals(_hashFor, _hull)) HullNormal(p, Vector3.UnitX);   // (builds the hash)
        if (_hullHash == null) return null;
        int cx = (int)MathF.Floor(p.X), cy = (int)MathF.Floor(p.Y), cz = (int)MathF.Floor(p.Z), ri = (int)MathF.Ceiling(r);
        Vector3? best = null; float bd = r * r;
        for (int x = -ri; x <= ri; x++) for (int y = -ri; y <= ri; y++) for (int z = -ri; z <= ri; z++)
            if (_hullHash.TryGetValue(HKey(cx + x, cy + y, cz + z), out var l))
                foreach (var q in l) { float d = (q - p).LengthSquared(); if (d < bd) { bd = d; best = q; } }
        return best;
    }

    /// <summary>The hull's normal at p from its face centres nearby (the smallest axis of their spread), or null.</summary>
    private Vector3? HullNormal(Vector3 p, Vector3 f)
    {
        var pts = _hull;
        if (pts == null) return null;
        if (!ReferenceEquals(_hashFor, pts))
        {
            _hullHash = new Dictionary<long, List<Vector3>>();
            foreach (var q in pts)
            {
                long k = HKey((int)MathF.Floor(q.X), (int)MathF.Floor(q.Y), (int)MathF.Floor(q.Z));
                if (!_hullHash.TryGetValue(k, out var l)) _hullHash[k] = l = new List<Vector3>();
                l.Add(q);
            }
            _hashFor = pts;
        }
        float r = NormalRadius, r2 = r * r;
        int cx = (int)MathF.Floor(p.X), cy = (int)MathF.Floor(p.Y), cz = (int)MathF.Floor(p.Z), ri = (int)MathF.Ceiling(r);
        Vector3 m = Vector3.Zero; int n = 0;
        var near = new List<Vector3>();
        for (int x = -ri; x <= ri; x++) for (int y = -ri; y <= ri; y++) for (int z = -ri; z <= ri; z++)
            if (_hullHash.TryGetValue(HKey(cx + x, cy + y, cz + z), out var l))
                foreach (var q in l) if ((q - p).LengthSquared() <= r2) { near.Add(q); m += q; n++; }
        if (n < 4) return null;
        m /= n;
        // covariance; its smallest eigenvector by inverse-free iteration: the largest of (trace*I - C)
        float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
        foreach (var q in near) { var d = q - m; xx += d.X * d.X; xy += d.X * d.Y; xz += d.X * d.Z; yy += d.Y * d.Y; yz += d.Y * d.Z; zz += d.Z * d.Z; }
        float tr = xx + yy + zz + 1e-4f;
        // M = tr*I - C
        float a11 = tr - xx, a12 = -xy, a13 = -xz, a22 = tr - yy, a23 = -yz, a33 = tr - zz;
        Vector3 v = Vector3.Normalize(f + new Vector3(0.11f, 0.07f, 0.05f));
        for (int it = 0; it < 24; it++)
        {
            var w = new Vector3(a11 * v.X + a12 * v.Y + a13 * v.Z, a12 * v.X + a22 * v.Y + a23 * v.Z, a13 * v.X + a23 * v.Y + a33 * v.Z);
            float len = w.Length(); if (len < 1e-8f) break;
            v = w / len;
        }
        // out of the local surface: p against its neighbours' mean (a thin plate's top face up, its bottom face down);
        // when p sits at the mean (inside a solid), facing the flow and away from the ship's axis as before
        Vector3 off = p - m;
        if (off.LengthSquared() > 0.04f * 0.04f) { if (Vector3.Dot(v, off) < 0) v = -v; return v; }
        Vector3 rad = p - _stagMid; rad -= f * Vector3.Dot(rad, f);
        Vector3 outward = f + (rad.LengthSquared() > 1e-4f ? Vector3.Normalize(rad) * 0.7f : Vector3.Zero);
        if (Vector3.Dot(v, outward) < 0) v = -v;
        return v;
    }

    /// <summary>The spray's direction at p: the flow deflected along the surface (normal nrm) plus SprayOut x nrm.</summary>
    private Vector3 SprayDir(Vector3 p, Vector3 f, Vector3 nrm)
    {
        Vector3 along = -f - Vector3.Dot(-f, nrm) * nrm;
        if (along.LengthSquared() < 0.01f)
        {
            Vector3 rad = p - _stagMid; rad -= f * Vector3.Dot(rad, f);
            along = rad.LengthSquared() > 1e-4f ? Vector3.Normalize(rad) : Vector3.Cross(f, Vector3.UnitY);
        }
        return Vector3.Normalize(Vector3.Normalize(along) + SprayOut * nrm);
    }

    /// <summary>The windward surface's outward normal at shadow cell (i, j), from the depth's slope.</summary>
    private Vector3 SurfaceNormal(int i, int j, Vector3 f)
    {
        int N = _hullHas.GetLength(0) - 1;
        bool H(int ii, int jj) => ii >= 0 && jj >= 0 && ii <= N && jj <= N && _hullHas[ii, jj];
        float Z(int ii, int jj) => Vector3.Dot(_hullSurf[ii, jj], f);
        float gx = 0f, gy = 0f;
        if (H(i + 1, j) && H(i - 1, j)) gx = (Z(i + 1, j) - Z(i - 1, j)) / (2 * _gdx);
        else if (H(i + 1, j)) gx = (Z(i + 1, j) - Z(i, j)) / _gdx;
        else if (H(i - 1, j)) gx = (Z(i, j) - Z(i - 1, j)) / _gdx;
        if (H(i, j + 1) && H(i, j - 1)) gy = (Z(i, j + 1) - Z(i, j - 1)) / (2 * _gdy);
        else if (H(i, j + 1)) gy = (Z(i, j + 1) - Z(i, j)) / _gdy;
        else if (H(i, j - 1)) gy = (Z(i, j) - Z(i, j - 1)) / _gdy;
        // z(x, y) = depth along f: the surface's normal facing the flow is f - gx a - gy b
        return Vector3.Normalize(f - _ga * gx - _gb * gy);
    }

    private float RawWeight(Vector3 p, Vector3 f) => LocalPressure(p) * (1f - CpMix + CpMix * SurfaceCos2(p, f));
}
