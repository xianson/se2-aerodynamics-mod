#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;
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
    /// <summary>The bow shock (the grid the sparks sit on - Mode 2 of the old mesh prototypes, whose other modes and knobs went
    /// 2026-10-17): a sheet standing off the hull's windward surface (the front-most hull point
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
    /// <summary>The rim (the shock's edge round the silhouette) smoothed RimSmooth passes.</summary>
    public static int RimSmooth = 0;
    /// <summary>The plasma spot light's tint (FlightField.Render.cs SpawnSpot).</summary>
    public static float[] LightTint = { 1f, 1f, 1f };

    private Vector3 _axis;
    /// <summary>The effects' brightness (Fx.Brightness).</summary>
    public static float Bright = 1f;

    private float _shapeR;

    private List<Vector3> _hull;
    private string _hullWhy;

    /// <summary>The chord ends at the first gap along the flow longer than this (metres).</summary>
    public static float ChordGap = 1.5f;
    // (the shock grid itself, its outputs and the rim: FlightField.Shock.cs)

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
    /// <summary>The plasma's light as ONE spot light (RenderContracts.CreateSpotLightEntity), SpotDist x R upwind of
    /// the front, aimed down the flow, its cone just covering SpotCover x R: the windward surface lit (with the hull's
    /// own shadows when SpotShadow), nothing below or behind - a "plasma sun". Spot: its strength (linear; a vanilla
    /// spotlight is 300-700), 0: none. SpotFlip: aim the other way (if the engine's spot faces +Z).</summary>
    public static float Spot = 24000000f;   // (x8: "much much brighter" - the user, 2026-10-09)
    public static float SpotDist = 2.5f, SpotCover = 1.3f, SpotFalloff = 2f;
    public static bool SpotShadow = true, SpotFlip;
    private object _spotRoot, _spot;
    private string _spotWhy;
    private readonly List<object> _gridLights = new List<object>();
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
    private float _weightMax = 1f;
    private Vector3 _flow;
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
    // (the effect handle's methods - _setParams, _fixTime, declared in FlightField.Render.cs - looked up once, not by name per spawn)
    private string _puffWhy;
    private long _puffWhyAt;   // (when the last spawn failure was seen: retried after 2 s)
}
