#pragma warning disable
using System;
using System.Collections.Generic;
using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Game.Systems;
namespace AeroMod;

/// <summary>
/// A grid's flight effects (client, visual only), placed from the aero model itself:
///  - PLASMA: sparks along the hull shadow's outline, drag-weighted by the force table's chunks (the game's explosion
///    spark recipe, orange held then magenta-violet), sprayed downwind, and one spot light ahead of the nose; strength
///    from Mach (onset AeroEntryFx.OnsetMach, full ~Mach 2.5) x density - or an entry source's (the Orbital Mod's
///    reentry on rails) when one owns the grid.
///  - VAPOUR: transonic sheets along the same outline half a chord back, snapped onto the hull, on the suction side
///    (the aero lift), in the Mach band EdgeBand.
///  - TIP TRAILS: from the aero mod's detected wing tips at every speed: strength from the lift, length from airspeed.
/// Only what the regime needs is spawned (tips flying; vapour near the band; plasma from just under the onset), each
/// group placed and dropped by itself; strengths follow live through each effect's fixed time. Tuning knobs are
/// static (aeroset FlightField.X); the field itself is per grid (AeroEntryFxComponent). Ported from the Orbital Mod's
/// dev spike (Render/PlasmaSpike.cs, modes 5-7); the effects: tools/particles/gen_plasma_field.py, gen_vapor.py.
/// </summary>
internal sealed partial class FlightField
{
    public static float Emissive = 20f, Length = 4f, Width = 0.35f, Flare = 0.6f, GlowSize = 2.2f, SpecA = 0f, Fresnel = 1f;
    public static int Count = 16, Every = 4;
    /// <summary>0: separate streamers (each its own cell); 1: one grid sheet - a sleeve off the frontal outline and a
    /// cap on the nose - whose UVs together cover ONE atlas cell, all of it stepping to the next cell (the animation).</summary>
    public static int Mode = 1, Around = 32, Along = 8;
    /// <summary>Mode 2, the bow shock: a sheet standing off the hull's windward surface (the front-most hull point
    /// along the flow in each cell of a Res x Res grid across it: the shadow map's lit surface), blurred Blur times so it
    /// follows the hull loosely, pushed upwind by Standoff x R, and past the silhouette swept back downwind
    /// (Sweep x d^2 / R) out to Margin x R. Its UVs span one atlas cell.</summary>
    public static float Standoff = 0.4f, Sweep = 1.0f, Margin = 0.8f;   // (0.4 and Blur 12: rounder, smoother)
    /// <summary>The shock never closer to the hull than MinStandoff x R (after the blur: a nose's lone peak was
    /// averaged down and poked through); the hull's peaks grown Dilate cells first; the sheet's outer band (past
    /// TrimPast of the margin: black texture, only the shader's grey sheen) not drawn.</summary>
    public static float MinStandoff = 0.12f, TrimPast = 0.85f;
    public static int Dilate = 2;
    public static int Res = 96, Blur = 12;   // (64 / 48 tried: finer, but the texture was the problem)
    /// <summary>Mode 3, the look prototyped offline (D:\aero\tools\plasma_proto\proto.py): the bow shock as Shells
    /// sheets ShellStep x R apart (the atlas in its own colours), and a trailing skirt - the shock continued past the
    /// silhouette downwind - in three overlapping sleeves (lengths SkirtLen x R x 0.45 / 0.75 / 1, hue-replaced orange,
    /// red, violet), each round the ship in SkirtTiles overlapping strips of one frame stretched along the flow.
    /// The frames flicker between FrameLo and FrameHi (ping-pong at FrameFps), not the atlas's whole sequence.</summary>
    public static int Shells = 4, SkirtTiles = 9, FrameLo = 10, FrameHi = 16, SkirtFrame = 12;
    /// <summary>Segments down each skirt strip; the rim (the skirt's start round the silhouette) smoothed RimSmooth
    /// passes (a ragged hull outline made a jagged skirt). Single-image textures (an emitter with a 1 x 1 "atlas"):
    /// TileU x TileV repeats over each sheet (the texture's own detail, not one 128 px cell stretched over the ship),
    /// scrolled downwind at Scroll repeats a second (the motion, in place of the atlas frames).</summary>
    public static int SkirtAlong = 12, RimSmooth = 0;
    public static float TileU = 3f, TileV = 2f, Scroll = 0.6f;
    public static float ShellStep = 0.1f, SkirtFlare = 0.35f, SkirtLen = 3f, SkirtOverlap = 0.6f, SkirtV0 = 0.25f, SkirtV1 = 0.97f, FrameFps = 8f;
    /// <summary>The skirts' colouring (hue, saturation replaced; value shifted): orange, red, violet.</summary>
    public static float[] SkirtHue = { 0.06f, 0.015f, 0.78f }, SkirtSat = { 0.75f, 0.85f, 0.75f }, SkirtDim = { 0f, -0.05f, -0.05f };
    /// <summary>The plasma's own light (the mesh is lit by the scene - PBRTransparent: glass - so at night only a light
    /// makes it, and the hull, glow): the aero mod's light-only effect (Assets/Particles/PlasmaLight_1000, 1000 at
    /// scale 1, orange; tools/particles/gen_plasma_light.py) between the hull's nose and the shock. Its intensity is
    /// 1000 x scale^2 (ParticleLightEntityComponent), so Light (intensity) sets the scale; extended radius on (the
    /// default cap would cut its reach); 0: none. LightAhead: where, as a fraction of the way from the hull to the shock.</summary>
    public static float Light = 16000f, LightAhead = 0.5f;
    public static float[] LightTint = { 1f, 1f, 1f };
    private Vector3 _lightAt;

    private Vector3 _nose, _axis;
    /// <summary>Mode 4, calibration: eight flat patches beside the ship, each one uniform (every corner on the same
    /// texel, CalibUV, of the glow atlas), white at CalibLevels (sRGB) - a screenshot gives the game's curve.</summary>
    public static float[] CalibLevels = { 1f, 0.7f, 0.5f, 0.35f, 0.25f, 0.18f, 0.12f, 0.08f };
    public static Vector2 CalibUV = new Vector2(0.125f, 0.25f);
    /// <summary>The patches' colour (x each level; live: CalR CalG CalB) - white, or a hue to see how the game keeps it.</summary>
    public static float[] CalibTint = { 1f, 1f, 1f };
    public static float CalibSize = 8f;
    /// <summary>The colours (sRGB, packed 8-bit): the shock, then the skirts - calibrated so the game shows the offline
    /// prototype's look (tools/plasma_proto): measured, the game shows glow g as Reinhard(1.77 g) at night, and the
    /// material's strength is 4; Bright scales them all (live).</summary>
    public static float[][] GlowColor = { new[] { 0.278f, 0.181f, 0.074f }, new[] { 0.254f, 0.195f, 0.118f }, new[] { 0.241f, 0.111f, 0.079f }, new[] { 0.207f, 0.111f, 0.241f } };
    public static float Bright = 1f;
    /// <summary>Mode 3's material: false (default) the game's own particle atlas as it is (the meteor fire, on the
    /// transparent material: the first in-game look); true the generated glow textures on the safe-zone material.</summary>
    public static bool UseGlow = false;
    /// <summary>Saturation (live): each colour pushed from its grey (linear) by this - the game's tone map pulls bright
    /// colours to white, so they need more than the prototype's.</summary>
    public static float Sat = 1f;

    private float _shapeR;

    private List<Vector3> _hull;
    private string _hullWhy;

    /// <summary>A grid of points, its UVs from uv(i, j), on the current buffers.</summary>
    private Func<int, int, bool> _skipQuad;

    /// <summary>The light: on the axis through the shock's front-most point, LightAhead of the way from hull to shock.</summary>
    private void LightPlace(Vector3[,] P3, List<Vector3> pts, Vector3 f)
    {
        Vector3 front = P3[0, 0];
        foreach (var q in P3) if (Vector3.Dot(q, f) > Vector3.Dot(front, f)) front = q;
        float hullFront = float.MinValue;
        foreach (var q in pts) hullFront = Math.Max(hullFront, Vector3.Dot(q, f));
        float zs = Vector3.Dot(front, f);
        _lightAt = front - f * ((zs - hullFront) * (1f - LightAhead));
    }

    /// <summary>The chord ends at the first gap along the flow longer than this (metres).</summary>
    public static float ChordGap = 1.5f;
    // (the shock grid itself, its outputs and the rim: FlightFieldShock.cs)

    /// <summary>What the shock grid was laid out for: the flow axis, the hull's point count and the knobs it reads
    /// (_shapeOk false: none yet, or thrown away - a re-layout, a rebuild).</summary>
    private ShapeKey _shapeKey; private bool _shapeOk;
    // (a value, compared field by field: it was a string joined every frame - ten boxed values, an array and the string,
    //  ~35-50 KB/s. Compared as that string compared them: each float by its exact value (the default formatting round-
    //  trips), NaN equal to NaN, -0 apart from 0; the axis is only ever assigned - at a re-layout, or once when baked -
    //  never computed per frame, so exact is what the string did)
    private readonly struct ShapeKey
    {
        readonly Vector3 _axis; readonly int _pts, _res, _blur, _dilate, _rimSmooth; readonly float _standoff, _sweep, _margin, _minStandoff, _chordGap;
        public ShapeKey(Vector3 axis, int pts, int res, int blur, float standoff, float sweep, float margin, float minStandoff, int dilate, int rimSmooth, float chordGap)
        {
            _axis = axis; _pts = pts; _res = res; _blur = blur; _standoff = standoff; _sweep = sweep; _margin = margin;
            _minStandoff = minStandoff; _dilate = dilate; _rimSmooth = rimSmooth; _chordGap = chordGap;
        }
        static bool Same(float x, float y) => (float.IsNaN(x) && float.IsNaN(y)) || (x == y && float.IsNegative(x) == float.IsNegative(y));
        public bool Same(in ShapeKey o) =>
            Same(_axis.X, o._axis.X) && Same(_axis.Y, o._axis.Y) && Same(_axis.Z, o._axis.Z) && _pts == o._pts && _res == o._res && _blur == o._blur
            && Same(_standoff, o._standoff) && Same(_sweep, o._sweep) && Same(_margin, o._margin) && Same(_minStandoff, o._minStandoff)
            && _dilate == o._dilate && _rimSmooth == o._rimSmooth && Same(_chordGap, o._chordGap);
    }

    private const BindingFlags AnyInst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>The client's copy of a (server) grid - the one with a render component: same name, nearest.</summary>
    private Keen.VRage.DCS.Components.Entity _client;

    // ── Mode 5: the plasma as particles over the shock (the aero mod's PlasmaField effects, tools/particles/gen_plasma_field.py)

    /// <summary>Mode 5: a Shock puff every PuffEvery cells of the shock grid (inside TrimPast), a Stream every
    /// StreamEvery rim angles (of 99); each scaled by the spacing x PuffScale / StreamScale (their sizes and speeds are
    /// authored per metre of it), turned so its +Z runs downwind.</summary>
    public static int PuffEvery = 4, StreamEvery = 6;
    public static float PuffScale = 2f, StreamScale = 0.6f;   // (the user's pick: small, many - rows E/F)
    /// <summary>The cap ahead of the nose denser: shock points within FrontDepth x R of the front-most one every
    /// FrontEvery cells (finer sparks there: their scale is the spacing); 0: no extra.</summary>
    public static int FrontEvery = 2;
    /// <summary>Where the sparks come from: 0 the shock sheet, 1 the hull's windward surface (every PuffEvery cells),
    /// 2 its silhouette edge (lit cells beside unlit ones, every EdgeEvery of them), 3 surface and edge. Hull
    /// emitters sit SurfaceLift x R upwind of the hull.</summary>
    public static int PuffSource = 4, EdgeEvery = 2;   // (the outline only: the user's pick)
    /// <summary>PuffSource bit 4: LINE emitters along the hull shadow's outline (marching squares over the windward
    /// mask, smoothed LineSmooth passes, cut into SegLen metre segments): each a spawn box along its segment, spraying
    /// downwind - continuous along the outline. Sparks LineScale big; within FrontDepth x R of the front the short
    /// spray, behind it the long one.</summary>
    public static float SegLen = 1.5f, LineScale = 3f;   // (SegLen: the spacing of the emitters along the outline; LineScale 20 -> 3: very tiny sparks - the user, 2026-10-09)
    public static int LineSmooth = 2;
    public static float SurfaceLift = 0.01f;   // (was 0.05: tighter to the skin)
    /// <summary>Lights over the windward surface, every LightGrid cells (0: the one light at the nose), each LightEach
    /// strong, LightLift x R off the hull; at most LightMax of them.</summary>
    public static int LightGrid = 0, LightMax = 24;   // (point lights spilled everywhere: the spot below instead)
    /// <summary>The plasma's light as ONE spot light (RenderContracts.CreateSpotLightEntity), SpotDist x R upwind of
    /// the front, aimed down the flow, its cone just covering SpotCover x R: the windward surface lit (with the hull's
    /// own shadows when SpotShadow), nothing below or behind - a "plasma sun". Spot: its strength (linear; a vanilla
    /// spotlight is 300-700), 0: none. SpotFlip: aim the other way (if the engine's spot faces +Z).</summary>
    public static float Spot = 24000000f;   // (x8: "much much brighter" - the user, 2026-10-09)
    public static float SpotDist = 2.5f, SpotCover = 1.3f, SpotFalloff = 2f;
    public static bool SpotShadow = true, SpotFlip;
    private object _spotRoot, _spot;
    private string _spotWhy;
    public static float LightEach = 6000f, LightLift = 0.08f;
    private readonly List<object> _gridLights = new List<object>();
    private string _gridLightWhy;
    public static float FrontDepth = 0.5f;
    /// <summary>Sparks' brightness (their colour multiplier: the emissive glow too) and colour: 0 orange to red,
    /// 1 orange through magenta to violet.</summary>
    public static float SparkBright = 1f;   // (the colour multiplier barely dims them: SparkLevel does)
    public static int SparkColor = 3;
    private static readonly Guid ShockPuff = new Guid("f492f164-c71f-59da-be2d-c43dca9738f3"), StreamPuff = new Guid("48e61a78-3bed-50f7-8547-bca713518ce6");
    /// <summary>What each point spews: 0 fire puffs, 1 sparks (a streaking spray downwind), 2 both.</summary>
    public static int Sparks = 1;
    /// <summary>The sparks' speed x (on top of the spacing the effects are authored per metre of).</summary>
    public static float SparkSpeed = 16f;   // (~96 m/s off the hull, a quarter of the life: crazy fast and tight - the user, 2026-10-09; was 0.05)
    /// <summary>TEST: one spark effect at the nose only - 1 scale 9 turned downwind, 2 scale 1 turned, 3 scale 1 unturned.</summary>
    public static int PuffTest;
    private static readonly Guid ShockSparksOrange = new Guid("88d92e33-42d6-57d6-92e5-6c1b6e145b75"), TailSparksOrange = new Guid("a5dffd10-c970-5b4b-b93c-60fb8b1eb3fe");
    private static readonly Guid ShockSparksViolet = new Guid("65e9fe79-ae22-5e17-9378-95aab9f9beb7"), TailSparksViolet = new Guid("8d41a37a-bc8a-53d5-9e6a-e8822ba2d124");
    /// <summary>SparkColor 2: sparks born the spot light's colour (PlasmaColor), cooling to violet, at SparkLevel
    /// 1-5 (emissivity 3000 / 1000 / 330 / 165 / 80 at birth: the aero mod's PlasmaField_*Matched*) - the spot and the sparks balanced.</summary>
    public static int SparkLevel = 4;
    public static readonly float[] PlasmaColor = { 1f, 0.55f, 0.25f };   // (sRGB; = PLASMA in gen_plasma_field.py)
    /// <summary>Sparks per emitter, % of the authored rate: 100 / 75 / 50 / 25 (the emitters as dense; fewer sparks
    /// each - gen_plasma_field.py's rate variants, Guid 5d0c7a3e-000K-4LLL-8RRR-000000000001).</summary>
    public static int SparkRate = 75;
    private Guid RateGuid(int kind, int rate = -1) => new Guid($"5d0c7a3e-{kind:x4}-4{Math.Clamp(SparkLevel, 1, 5):x3}-8{(rate < 0 ? SparkRate : rate):x3}-000000000001");
    /// <summary>Outline emitters LineDensity x closer (SegLen / it), each at SparkRate / it (the nearest built rate:
    /// 100 50 25 12 6 - deep sparks only) - the same sparks in all, finer along the outline. Sizes and speeds unchanged.</summary>
    public static int LineDensity = 4;
    /// <summary>LOCAL DRAG drives each emitter (Weighted: the aero mod's PlasmaField_Weighted* effects, rate = the
    /// emitter's held effect time): weight = the hull's local pressure there - each 8 m chunk of the aero force table's
    /// Newtonian drag along the flow over its frontal area (its share for this flow direction, shadowing included),
    /// blended over neighbouring chunks - times (1 - CpMix + CpMix cos^2) of the local surface slope (the face model's
    /// own Newtonian term, finer than a chunk); normalised to the hottest emitter, ^WeightGamma. Rate = Heat x weight
    /// (/ LineDensity on the outline). Emitters under WeightMin not spawned.</summary>
    public static bool Weighted = true;
    public static float Heat = 1f, CpMix = 0.6f, WeightGamma = 1f, WeightMin = 0.03f;
    private readonly List<(Vector3 c, float drag, float frontal)> _chunkDrag = new();
    private string _dragWhy;
    private float _weightMax = 1f;
    private Vector3 _flow;
    private int _weightSkipped;
    private int LineRate
    {
        get
        {
            float want = SparkRate / (float)Math.Max(1, LineDensity); int best = 100;
            foreach (int r in new[] { 100, 50, 25, 12, 6 }) if (Math.Abs(r - want) < Math.Abs(best - want)) best = r;
            return best;
        }
    }
    /// <summary>SparkColor 3: DEEP - born saturated orange (DeepColor; the spot the same), thinner; levels 2-5, rates 100/50/25.</summary>
    public static readonly float[] DeepColor = { 1f, 0.35f, 0.06f };   // (= DEEP in gen_plasma_field.py)
    private float[] SparkBirth => SparkColor == 3 ? DeepColor : PlasmaColor;
    private Guid ShockSparks => SparkColor == 3 ? RateGuid(3) : SparkColor == 2 ? RateGuid(1) : SparkColor == 1 ? ShockSparksViolet : ShockSparksOrange;
    private Guid TailSparks => SparkColor == 3 ? RateGuid(4) : SparkColor == 2 ? RateGuid(2) : SparkColor == 1 ? TailSparksViolet : TailSparksOrange;
    private readonly List<object> _puffs = new List<object>();
    // (the effect handle's methods - _setParams, _fixTime, declared in FlightFieldHost - looked up once, not by name per spawn)
    private string _puffWhy;
    private long _puffWhyAt;   // (when the last spawn failure was seen: retried after 2 s)

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

    private int _lineBad;

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
        if (Vector3.Dot(Vector3.Transform(Vector3.UnitZ, q), -f) < 0.99f) { _lineBad++; return; }
        Vector3 mid = (p0 + p1) * 0.5f + lift;
        bool front = zFront - Vector3.Dot(mid, f) < FrontDepth * R;
        // (a stretched spawn box cannot be a line: the GPU emitter sends each particle along normalize(its spawn offset) -
        //  Shaders/Transparent/Particles/ParticleEmission.hlsl - so a long box sprays along itself. Round point emitters,
        //  close together along the outline, instead.)
        Guid e = SparkColor == 3 && LineDensity > 1 ? RateGuid(front ? 3 : 4, LineRate) : front ? ShockSparks : TailSparks;
        float held = WeightTime(mid - lift, LineDensity);
        if (held == 0f) { _weightSkipped++; return; }
        if (_sub == 6)
        {
            // (the vapour sheets: the same placement and spray direction, its own effect; the weight at heat 1 is kept and
            //  VapourFlow multiplies it by the Mach band every quarter second)
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
        void FillLine(object prm)
        {
            var t = prm.GetType();
            t.GetField("EmitterScaleMultiplier").SetValue(prm, LineScale);
            t.GetField("VelocityMultiplier").SetValue(prm, Math.Max(0.01f, SparkSpeed));
            t.GetField("ColorMultiplier").SetValue(prm, new ColorSRGB(SparkBright, SparkBright, SparkBright, 1f));
            t.GetField("EmitterSizeMultiplier").SetValue(prm, SpawnSize);
            t.GetField("ParticleAspectRatio").SetValue(prm, SparkAspect);
            if (Strict) t.GetField("VelocityDirection").SetValue(prm, Vector3.UnitZ);
        }
        object h = SpawnEffect(_client, e, new RelativeTransform(mid, q), out object prm, out _puffWhy, FillLine);
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

    private bool _fieldUp;

    /// <summary>The local pressure at p: the chunks' drag over their frontal area, Gaussian-blended (sigma 0.6 chunk).</summary>
    private float LocalPressure(Vector3 p)
    {
        long tP = System.Diagnostics.Stopwatch.GetTimestamp();
        try { return LocalPressure0(p); } finally { BkPressure += Ms(tP); }
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

    /// <summary>The hull's normal at p from its face centres nearby (the smallest axis of their spread), or null.</summary>
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

    // ── Mode 6: TRANSONIC VAPOUR - the condensation cone at the grid's widest station, and sheets off the outline half a
    //    chord downwind; driven by the aero mod's real Mach, density and flow direction (AeroEntryFx.TryGetFlow, by
    //    reflection - D:\aero\docs\REFLECTION_AND_WHITELIST.md). Effects: the aero mod's Vapour_* (tools/particles/gen_vapor.py).

    /// <summary>Mach bands (in, full .. full, out): the cone and the edge sheets; density factor (rho / VapourRho)^0.5,
    /// capped at 1. The flow axis follows the real travel (AoA); past AxisTol degrees the field is placed again.
    /// ConeScale: the cone ring x the grid's widest radius; VapourEdgeScale: the edge sprays' size; ChordShift: how far
    /// back along the chord the edge emitters sit (0.5 = mid-chord).</summary>
    public static float[] ConeBand = { 0.90f, 0.97f, 1.02f, 1.08f }, EdgeBand = { 0.80f, 0.90f, 1.02f, 1.10f };
    public static float VapourRho = 1.0f, AxisTol = 5f, ConeScale = 1.0f, VapourEdgeScale = 0.75f, ChordShift = 0.5f, VapourSpeed = 20f;   // (VapourSpeed 2.5 -> 10 with a quarter of the life: fast and tight - the user, 2026-10-09)
    /// <summary>TEST: a fixed Mach (0: the real one) - the shape at any speed.</summary>
    public static float MachOverride;
    /// <summary>DIAGNOSTIC: 0 the cone as built, 1 without collisions, 2 without collisions or the inner-cone ring;
    /// EdgeCollide 0: the edge without collisions.</summary>
    public static int ConeVariant, EdgeCollide = 1;
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
    /// edge; on from TipBand (Mach), x density. Not transonic: tip vortices condense whenever fast in humid air.</summary>
    public static float TipMinFrac = 0.7f, TipScale = 6f, TipSpeed = 1f;   // (6: the trail moving with the air reads at a distance - 3 vanished)
    public static float[] TipBand = { 0.25f, 0.45f, 5f, 6f };   // (unused since the tips run at all speeds; kept for the knob)
    /// <summary>Tip trails at ALL speeds, by flight regime: on from TipMinSpeed (full by 3x it), strength TipBase + TipLiftGain x
    /// the lift strength (a slow, high-lift approach shows them strongest - the vortex is the lift), x (1 + TipMachBoost x the
    /// transonic band), x density. Length grows with airspeed: TipLenBase + TipLenPerMs x speed metres, up to TipLenMax
    /// (placed again when the speed moves TipRespawn of itself - the length is fixed at spawn).</summary>
    /// <summary>Flight mode (7): the vapour's spray (the plasma keeps DownMix / Jitter); the plasma's held time = the aero
    /// mod's entry strength (AeroEntryFx.EntryStrength: Mach 2 onset, density-scaled) x each line's weight, the spot light
    /// x the strength (placed again when it moves a quarter).</summary>
    public static float VapourDownMix = 0.97f, VapourJitter = 4f;
    private int _sub; private bool _noStop; private Vector3 _lastF;
    private float _entryS, _spotMul = 1f, _spotUsed = -1f;
    private readonly List<(object h, float w)> _plasma = new List<(object, float)>();
    public static float TipMinSpeed = 60f, TipBase = 0.3f, TipLiftGain = 0.8f, TipMachBoost = 0.5f;   // (a floor: at 0 the trails came out sparse and dotted - read as separate pieces)   // (from lift only, and from 60 m/s: a ship cruising at 44 m/s drew three fat trails)
    public static float TipLenBase = 20f, TipLenPerMs = 0.25f, TipLenMax = 150f, TipRespawn = 1.0f;   // (respawned only when the speed doubles or halves: at 25 % the trails vanished and regrew every few seconds)
    private float _speed, _tipSpeedUsed = -1f;
    private static readonly Guid VapourTip = new Guid("5d0c7a3e-0107-4000-8000-000000000001");
    private float _tipI;
    private static readonly Guid VapourConeSpray = new Guid("5d0c7a3e-0106-4000-8000-000000000001");
    public static float ConeStation = 0.5f, ConeMargin = 1.5f, ConeSpacing = 1.2f, ConeFlare = 0.35f, ConeEmitScale = 3f, ConeSpeed = 3f;
    private static readonly Guid VapourEdge = new Guid("5d0c7a3e-0102-4000-8000-000000000001");
    private float _chordShift;
    private readonly List<(object h, float w, int kind)> _vapour = new List<(object, float, int)>();   // (kind 0 edge, 1 cone, 2 tip)
    private float _mach, _rho, _coneI, _edgeI;
    private bool _flowFound;
    private long _flowAt;
    private string _vapourStatus = "";

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
            int n = 0, culled = 0;
            for (int k = 0; k < ConeRing; k++)
            {
                if (far[k] < 0f) continue;   // (no hull at this angle near the station)
                if (far[k] > ConeOutlier * median) { culled++; continue; }
                float ang = (k + 0.5f) / ConeRing * 2f * MathF.PI - MathF.PI;
                Vector3 outv = _ga * MathF.Cos(ang) + _gb * MathF.Sin(ang);
                Vector3 pos = at[k] - f * (Vector3.Dot(at[k], f) - zSt) + outv * ConeMargin;
                Vector3 dir = Vector3.Normalize(ConeFlare * outv + (1f - ConeFlare) * -f);
                VapourEffect(VapourConeSpray, pos, Quaternion.CreateFromTwoVectors(Vector3.UnitZ, dir), ConeEmitScale, 1f, true, ConeSpeed);
                n++;
            }
            int tips = WingtipLines(f, N, axis);
            _vapourStatus = $"vapour placed: {tips} tip lines ({TipWhy}); cone ring {n}/{ConeRing} ({culled} outliers past {ConeOutlier:F1}x the median {median:F1} m) on the skin at {ConeStation:P0} of {zMax - zMin:F0} m, axis chord {bestChord:F1} m";
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
        int placed = 0;
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
                    placed++;
                }
                have += l - at;
            }
        }
        _vapourStatus = $"vapour placed: cone loop {(ConeLoop == 2 ? "hull" : "silhouette")} {placed} emitters at {ConeStation:P0} of {zMax - zMin:F0} m, axis chord {bestChord:F1} m";
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

    /// <summary>One vapour effect: scale (sizes, ring, speeds), its local weight (rate x it), held by VapourFlow.</summary>
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

    private void StopPuffs()
    {
        if (_noStop) return;   // (flight mode: the plasma and the vapour spawned as one field)
        _plasma.Clear(); _spotMul = 1f; _spotUsed = -1f;
        _vapour.Clear();
        _rng = new Random(1);
        _spawnQueue.Clear(); _fieldUp = false; _weightSkipped = 0;
        foreach (var h in _puffs) Park(h);   // (parked for reuse, not disposed)
        _puffs.Clear(); _puffWhy = null;
        foreach (var h in _gridLights) DisposeEffect(h);
        _gridLights.Clear(); _gridLightWhy = null;
        StopSpot(); _spotWhy = null;
    }

    private void SpawnPuffs(Vector3[,] P3, float[,] past, Vector3[] ring, Vector3 f, float R)
    {
        StopPuffs();
        int N = P3.GetLength(0) - 1, step = Math.Max(1, PuffEvery);
        var turn = Quaternion.CreateFromTwoVectors(Vector3.UnitZ, -f);
        if (PuffTest > 0)
        {
            Puff(ShockSparks, _nose, PuffTest == 3 ? Quaternion.Identity : turn, PuffTest == 1 ? 9f : 1f);
            return;
        }
        float cell = (P3[N, 0] - P3[0, 0]).Length() / N;
        if (Weighted)
        {
            ChunkDrag(f);
            _weightMax = 1e-6f;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (_hullHas[i, j]) _weightMax = Math.Max(_weightMax, RawWeight(_hullSurf[i, j], f));
        }
        _flow = f;
        {
            _stagMid = Vector3.Zero; int sc0 = 0;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (_hullHas[i, j]) { _stagMid += _hullSurf[i, j]; sc0++; }
            if (sc0 > 0) _stagMid /= sc0;
        }
        if ((PuffSource & 4) != 0) { SpawnLines(P3, f, R, N, turn); if (_puffWhy != null) return; }
        if ((PuffSource & 3) != 0)
        {
            var H = _hullHas; var S = _hullSurf; Vector3 up = f * (SurfaceLift * R);
            _stagMid = Vector3.Zero; int sc = 0;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (H[i, j]) { _stagMid += S[i, j]; sc++; }
            if (sc > 0) _stagMid /= sc;
            float hz = float.MinValue;
            for (int i = 0; i <= N; i++) for (int j = 0; j <= N; j++) if (H[i, j]) hz = Math.Max(hz, Vector3.Dot(S[i, j], f));
            int fs2 = FrontEvery > 0 ? Math.Min(FrontEvery, step) : step, edgeN = 0;
            for (int i = 0; i <= N; i++)
                for (int j = 0; j <= N; j++)
                {
                    if (!H[i, j]) continue;
                    bool edge = i == 0 || j == 0 || i == N || j == N || !H[i - 1, j] || !H[i + 1, j] || !H[i, j - 1] || !H[i, j + 1];
                    if ((PuffSource & 2) != 0 && edge && edgeN++ % Math.Max(1, EdgeEvery) == 0)
                        Puff(TailSparks, S[i, j] + up, turn, cell * EdgeEvery * StreamScale);
                    else if ((PuffSource & 1) != 0)
                    {
                        bool front = FrontEvery > 0 && hz - Vector3.Dot(S[i, j], f) < FrontDepth * R && i % fs2 == 0 && j % fs2 == 0;
                        if (!(i % step == 0 && j % step == 0) && !front) continue;
                        var nrm = (NormalSource == 1 ? HullNormal(S[i, j], f) : null) ?? SurfaceNormal(i, j, f);
                        Vector3 dir = SprayDir(S[i, j], f, nrm);
                        var turnHere = SurfaceSpray ? Quaternion.CreateFromTwoVectors(Vector3.UnitZ, dir) : turn;
                        Vector3 at = SurfaceSpray ? S[i, j] + nrm * (SurfaceOff * R) : S[i, j] + up;
                        Puff(ShockSparks, at, turnHere, cell * (front ? fs2 : step) * PuffScale);
                    }
                    if (_puffWhy != null) return;
                }
            return;
        }
        if (PuffSource != 0) return;
        float zFront = float.MinValue;
        foreach (var q in P3) zFront = Math.Max(zFront, Vector3.Dot(q, f));
        int fs = FrontEvery > 0 ? Math.Min(FrontEvery, step) : step;
        for (int i = 0; i <= N; i++)
            for (int j = 0; j <= N; j++)
            {
                if (past[i, j] > TrimPast) continue;
                bool onGrid = i % step == 0 && j % step == 0;
                bool front = FrontEvery > 0 && zFront - Vector3.Dot(P3[i, j], f) < FrontDepth * R && i % fs == 0 && j % fs == 0;
                if (!onGrid && !front) continue;
                float sc = cell * (front ? fs : step) * PuffScale;
                if (Sparks != 1) Puff(ShockPuff, P3[i, j], turn, sc);
                if (Sparks >= 1) Puff(ShockSparks, P3[i, j], turn, sc);
                if (_puffWhy != null) return;
            }
        int around = ring.Length - 1, rs = Math.Max(1, StreamEvery);
        float arc = 0f;
        for (int k = 0; k < around; k++) arc += (ring[k + 1] - ring[k]).Length();
        for (int k = 0; k < around; k += rs)
        {
            if (Sparks != 1) Puff(StreamPuff, ring[k], turn, arc / around * rs * StreamScale);
            if (Sparks >= 1) Puff(TailSparks, ring[k], turn, arc / around * rs * StreamScale);
            if (_puffWhy != null) return;
        }
    }

    private readonly Queue<Action> _spawnQueue = new Queue<Action>();
    public static int SpawnPerTick = 48;   // (was 12: a rebuild filled in over 20-40 frames - the gap showed)

    /// <summary>The emitter's held time: heat x its local weight (normalised, ^WeightGamma) / density; -1 unweighted.</summary>
    private float WeightTime(Vector3 at, float density)
    {
        if (!Weighted) return -1f;
        float w = MathF.Pow(Math.Clamp(RawWeight(at, _flow) / _weightMax, 0f, 1f), WeightGamma);
        if (w < WeightMin) return 0f;
        return Math.Clamp(Heat * w / Math.Max(1f, density), 0.001f, 0.999f);
    }

    private void Puff(Guid effect, Vector3 at, Quaternion turn, float scale)
    {
        if (!_draining && !_relayout) { _spawnQueue.Enqueue(() => Puff(effect, at, turn, scale)); return; }
        bool spark0 = effect != ShockPuff && effect != StreamPuff;
        float held = spark0 ? WeightTime(at, 1f) : -1f;
        if (held == 0f) { _weightSkipped++; return; }
        if (held > 0f) effect = Recipe ? RecipeGuid(effect == TailSparks) : RateGuid(TexKind(effect == TailSparks), StreakField);
        bool spark = effect != ShockPuff && effect != StreamPuff;
        void Fill(object prm)
        {
            var t = prm.GetType();
            t.GetField("EmitterScaleMultiplier").SetValue(prm, Math.Max(0.1f, scale));
            if (spark) t.GetField("ColorMultiplier").SetValue(prm, new ColorSRGB(SparkBright, SparkBright, SparkBright, 1f));
            // (measured: a local-space effect's particle speed is already x its EmitterScaleMultiplier - x the spacing
            //  again sent the sparks 9x too fast, starting a plane-length behind their point; puffs kept as tuned)
            t.GetField("VelocityMultiplier").SetValue(prm, spark ? Math.Max(0.01f, SparkSpeed) : Math.Max(0.1f, scale));
            if (spark)
            {
                t.GetField("EmitterSizeMultiplier").SetValue(prm, SpawnSize);
                t.GetField("ParticleAspectRatio").SetValue(prm, SparkAspect);
                if (Strict) t.GetField("VelocityDirection").SetValue(prm, Vector3.UnitZ);
            }
        }
        object h = SpawnEffect(_client, effect, new RelativeTransform(at, turn), out object prm, out _puffWhy, Fill);
        if (h == null) return;
        _puffs.Add(h);
        try
        {
            SetParamsIfChanged(h, prm);
            { float w7 = held > 0f ? held / Math.Max(1e-3f, Heat) : 1f; _plasma.Add((h, w7)); held = Math.Clamp(_entryS * w7, 0.00001f, 0.999f); }
            if (held > 0f) { (_fixTime ??= h.GetType().GetMethod("FixEffectTime", AnyInst)).Invoke(h, new object[] { true, TimeSpan.FromSeconds(held) }); NoteFixed(h, held); }
        }
        catch (Exception e) { _puffWhy = "set: " + Inner(e); }
    }
}
