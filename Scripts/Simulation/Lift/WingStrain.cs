#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// WING STRUCTURAL STRAIN - pure math, no game (unit-tested offline: Tests/WingStrainTests).
///
/// A wing is a cantilever: its load (the force across it, along its normal) bends it about its ROOT, the span end
/// nearest the grid's centre of mass. A wing the centre of mass lies across (one wing through the fuselage: the
/// detector merges the halves) is two cantilevers, one each side, rooted at the station over the centre of mass.
///  - Moment: the load spread evenly over the wing's cells (uniform loading). At each span station, the moment of the
///    cells at and outboard of it about the station's inboard face (at the root: the whole side's load times the aero
///    centre's distance from the root face).
///  - Capacity at a station: its section across the span - the grid's blocks in the station's slab, within the wing's
///    chord and within MarginBlocks (at least MarginMin m) of its thickness (so a fuselage the wing runs through counts) - per chord column of
///    width w and depth t, CellStrength * w * t^2 (bending strength goes as depth squared). For a wing one block deep
///    (t = b) that is CellStrength * b^3 per block-sized cell of root section, the strength scaling with block size
///    cubed. Heavy armour HeavyFactor times stronger.
///  - Strain = the worst station's moment / capacity: 1 is the limit (past it the root wears), BreakStrain snaps it
///    off. For a uniform wing the worst station IS the root; a wing through a fuselage breaks where it leaves it.
///
/// CALIBRATION (CellStrength): a light-armour wing 4 blocks out from its root, 1 block deep, snaps (strain 1.5) at a
/// wing loading of 40 kPa (lift / planform area); wear starts at 26.7 kPa. A wing L blocks long and 1 block deep has
/// strain = w L^2 / (2 CellStrength) whatever its chord (the chord cancels) and whatever the block size (L counted in
/// blocks): an 8-block wing snaps at 10 kPa, a 2-block one at 160 kPa, and a 2-block-deep wing takes 4x a 1-block one.
/// Heavy armour x4 (a gameplay choice, not read from the block definitions: a heavy wing takes 4x the load). For
/// scale: 40 kPa is a 100 t plane with 100 m2 of wing at 4 g, or q = 32 kPa (230 m/s at sea level) at a lift
/// coefficient of 1.2. (Long slender wings - dozens of small blocks, one deep - are far weaker than that: see the
/// AeroBench strain report, and Structure.WingStrength.)
/// </summary>
public static class WingStrain
{
    /// <summary>Strain at which a root wears (health lost in proportion to the overshoot and the time).</summary>
    public const float LimitStrain = 1f;
    /// <summary>Strain at which the root's blocks are destroyed (the game splits the wing off).</summary>
    public static float BreakStrain = 1.5f;
    /// <summary>Heavy armour's strength over light armour's.</summary>
    public const float HeavyFactor = 4f;
    /// <summary>Pa: a chord column w wide and t deep holds CellStrength * w * t^2 (N m) of bending moment (a block-sized
    /// cell of a one-block-deep section: CellStrength * b^3). Calibrated: 8 * 40 kPa / 1.5 (a 4-block light wing snaps
    /// at 40 kPa: see the class summary).</summary>
    public const float CellStrength = 8f * 40000f / 1.5f;
    /// <summary>Blocks above and below the wing (along its normal) that count in its section - a fuselage it runs through -
    /// and at least MarginMin metres of them (small-grid fuselages are many blocks deep).</summary>
    public const int MarginBlocks = 2;
    public const float MarginMin = 2.5f;
    /// <summary>Health lost per second (fraction of the block's full health) per unit of strain over the limit: at
    /// strain 1.2 a root lasts ~10 s, at 1.45 ~4.4 s.</summary>
    public static float WearPerSecond = 0.5f;

    /// <summary>One side's cantilever: its stations (span cells, root first), what one newton of the wing's normal
    /// force does at each, and - once its capacities are known (Finish) - the worst one.</summary>
    public sealed class Side
    {
        /// <summary>+1: the wing runs from its root toward +span; -1: toward -span.</summary>
        public int Dir;
        /// <summary>The root station (wing cells, along the span axis). Station j is at span index RootIndex + Dir * j.</summary>
        public int RootIndex;
        /// <summary>Stations, root to tip.</summary>
        public int Stations;
        /// <summary>Wing cells on this side, and per station.</summary>
        public int CellCount; public int[] Cells;
        /// <summary>m, per station: the moment there per newton of the WHOLE wing's normal force.</summary>
        public float[] Arm;
        /// <summary>N m, per station: the capacity from the wing's own cells (its skins: depth per chord column). The
        /// fallback when no blocks are known.</summary>
        public float[] CellCapacity;
        /// <summary>The worst station (Finish), its moment per newton (m) and capacity (N m), and their ratio (strain per
        /// newton of the wing's normal force).</summary>
        public int Critical; public float CriticalArm, CriticalCapacity, Compliance;

        public int SpanIndex(int j) => RootIndex + Dir * j;
    }

    /// <summary>A wing's root analysis: one side (A) or two (A and B, a wing across the centre of mass).</summary>
    public sealed class Root
    {
        public Side A, B;
        public float CellSize, CellOffset;
        public Vector3 SpanAxis, ChordAxis, NormalAxis;
        /// <summary>The wing's chord and normal extents (wing cells, along ChordAxis / NormalAxis).</summary>
        public int ChordMin, ChordMax, NormalMin, NormalMax;
        public int Sides => (A != null ? 1 : 0) + (B != null ? 1 : 0);
        public Side Get(int i) => i == 0 ? A : B;
    }

    static int Project(Vector3I c, Vector3 axis) => (int)MathF.Round(c.X * axis.X + c.Y * axis.Y + c.Z * axis.Z);

    /// <summary>
    /// The wing's root(s), for a centre of mass (grid-local m). cellSize / cellOffset: the wing detector's cells (their
    /// centre is (cell + offset) * size, m). Null for a wing without cells. Each side is finished on its own cells'
    /// capacity; Finish it again with the blocks' (Sections). Allocates: once per wing set, not per frame.
    /// </summary>
    public static Root Analyze(in LiftingSurface wing, float cellSize, float cellOffset, Vector3 com)
    {
        var cells = wing.Cells;
        if (cells == null || cells.Count == 0 || cellSize <= 0) return null;
        float h = cellSize;
        var r = new Root { CellSize = cellSize, CellOffset = cellOffset, SpanAxis = wing.SpanAxis, ChordAxis = wing.ChordAxis, NormalAxis = wing.Normal,
            ChordMin = int.MaxValue, ChordMax = int.MinValue, NormalMin = int.MaxValue, NormalMax = int.MinValue };
        float sc = Vector3.Dot(com, wing.SpanAxis);   // (the centre of mass, along the span)
        int minP = int.MaxValue, maxP = int.MinValue, minN = int.MaxValue, maxN = int.MinValue, nP = 0, nN = 0;
        foreach (var c in cells)
        {
            int s = Project(c, wing.SpanAxis), ch = Project(c, wing.ChordAxis), n = Project(c, wing.Normal);
            r.ChordMin = Math.Min(r.ChordMin, ch); r.ChordMax = Math.Max(r.ChordMax, ch);
            r.NormalMin = Math.Min(r.NormalMin, n); r.NormalMax = Math.Max(r.NormalMax, n);
            if ((s + cellOffset) * h >= sc) { nP++; minP = Math.Min(minP, s); maxP = Math.Max(maxP, s); }
            else { nN++; minN = Math.Min(minN, s); maxN = Math.Max(maxN, s); }
        }
        Side P = nP > 0 ? NewSide(1, minP, maxP - minP + 1, nP) : null;
        Side N = nN > 0 ? NewSide(-1, maxN, maxN - minN + 1, nN) : null;
        // per station and chord column: the cells' depth (lowest to highest along the normal: the detector keeps both skins)
        var lo = new Dictionary<long, int>(); var hi = new Dictionary<long, int>();
        foreach (var c in cells)
        {
            int s = Project(c, wing.SpanAxis), ch = Project(c, wing.ChordAxis) - r.ChordMin, n = Project(c, wing.Normal);
            bool pos = (s + cellOffset) * h >= sc;
            var side = pos ? P : N;
            int j = (s - side.RootIndex) * side.Dir;
            side.Cells[j]++;
            long key = ((long)(pos ? 0 : 1) << 40) | ((long)j << 20) | (uint)ch;
            if (!lo.TryGetValue(key, out int a) || n < a) lo[key] = n;
            if (!hi.TryGetValue(key, out int b) || n > b) hi[key] = n;
        }
        foreach (var kv in lo)
        {
            var side = (kv.Key >> 40) == 0 ? P : N;
            int j = (int)((kv.Key >> 20) & 0xFFFFF);
            float t = (hi[kv.Key] - kv.Value + 1) * h;
            side.CellCapacity[j] += CellStrength * h * t * t;
        }
        foreach (var side in new[] { P, N })
        {
            if (side == null) continue;
            // the moment per newton at each station: the cells at and outboard of it, about its inboard face
            for (int j = 0; j < side.Stations; j++)
            {
                double m = 0;
                for (int i = j; i < side.Stations; i++) m += side.Cells[i] * (i - j + 0.5) * h;
                side.Arm[j] = (float)(m / cells.Count);
            }
            Finish(side, side.CellCapacity);
        }
        r.A = P ?? N; r.B = P != null ? N : null;
        return r;
    }

    static Side NewSide(int dir, int root, int stations, int count) => new Side
    {
        Dir = dir, RootIndex = root, Stations = stations, CellCount = count,
        Cells = new int[stations], Arm = new float[stations], CellCapacity = new float[stations],
    };

    /// <summary>The side's worst station for these capacities (N m per station): the most strain per newton. Stations
    /// with no wing cells (the gap a merged wing has over a fuselage) or no capacity are skipped.</summary>
    public static void Finish(Side side, float[] capacity)
    {
        side.Critical = 0; side.Compliance = 0f; side.CriticalArm = side.Arm.Length > 0 ? side.Arm[0] : 0f; side.CriticalCapacity = 0f;
        for (int j = 0; j < side.Stations; j++)
        {
            if (side.Cells[j] == 0 || capacity[j] <= 1e-3f) continue;
            float k = side.Arm[j] / capacity[j];
            if (k > side.Compliance) { side.Compliance = k; side.Critical = j; side.CriticalArm = side.Arm[j]; side.CriticalCapacity = capacity[j]; }
        }
    }

    /// <summary>
    /// The sections of a wing's stations from the grid's blocks: Add every block's occupied box (the game's 0.25 m
    /// cells), then Capacity per station. k: wing cells per game cell side (the detector's cell size / 0.25 m: 2 on
    /// large grids); wing cell c covers game cells c*k .. c*k+k-1. blockCells: a block's side in game cells (10 large,
    /// 2 small). Allocates (per chord column per station): once per wing set.
    /// </summary>
    public sealed class Sections
    {
        readonly Root _r; readonly int _k;
        readonly int _c0, _cols, _n0, _n1;            // the window: chord columns, normal range (game cells)
        readonly int[][] _lo, _hi; readonly float[][] _heavy, _len;   // [side * stations + j][column]
        readonly int[] _stations0;

        public Sections(Root r, int k, int blockCells)
        {
            _r = r; _k = Math.Max(1, k);
            _c0 = r.ChordMin * _k; _cols = (r.ChordMax - r.ChordMin + 1) * _k;
            int m = Math.Max(MarginBlocks * Math.Max(1, blockCells), (int)(MarginMin / 0.25f));
            _n0 = r.NormalMin * _k - m; _n1 = r.NormalMax * _k + _k - 1 + m;
            int sa = r.A?.Stations ?? 0, sb = r.B?.Stations ?? 0;
            _stations0 = new[] { 0, sa };
            _lo = new int[sa + sb][]; _hi = new int[sa + sb][]; _heavy = new float[sa + sb][]; _len = new float[sa + sb][];
        }

        /// <summary>Where a box lies along an axis (game cells: lowest, highest).</summary>
        static void Range(Vector3 axis, Vector3I min, Vector3I max, out int a, out int b)
        {
            int p = Project(min, axis), q = Project(max, axis);
            a = Math.Min(p, q); b = Math.Max(p, q);
        }

        /// <summary>The stations of side s a box covers (none: from > to), and its part of the chord / normal window.</summary>
        bool Cover(int s, Vector3I min, Vector3I max, out int from, out int to, out int c0, out int c1, out int n0, out int n1)
        {
            from = 1; to = 0; c0 = c1 = n0 = n1 = 0;
            var side = _r.Get(s);
            if (side == null) return false;
            Range(_r.ChordAxis, min, max, out c0, out c1); Range(_r.NormalAxis, min, max, out n0, out n1);
            if (c1 < _c0 || c0 > _c0 + _cols - 1 || n1 < _n0 || n0 > _n1) return false;
            c0 = Math.Max(c0, _c0) - _c0; c1 = Math.Min(c1, _c0 + _cols - 1) - _c0; n0 = Math.Max(n0, _n0); n1 = Math.Min(n1, _n1);
            Range(_r.SpanAxis, min, max, out int s0, out int s1);
            // station j covers span game cells (RootIndex + Dir j) k .. +k-1
            int j0 = side.Dir > 0 ? FloorDiv(s0, _k) - side.RootIndex : side.RootIndex - FloorDiv(s1, _k);
            int j1 = side.Dir > 0 ? FloorDiv(s1, _k) - side.RootIndex : side.RootIndex - FloorDiv(s0, _k);
            from = Math.Max(0, j0); to = Math.Min(side.Stations - 1, j1);
            return from <= to;
        }

        static int FloorDiv(int a, int k) => a >= 0 ? a / k : -((-a + k - 1) / k);

        /// <summary>Whether a block's box counts in any station's section (Add would take it).</summary>
        public bool Covers(Vector3I min, Vector3I max)
            => Cover(0, min, max, out _, out _, out _, out _, out _, out _) || Cover(1, min, max, out _, out _, out _, out _, out _, out _);

        /// <summary>A block's box (game cells), light or heavy armour.</summary>
        public void Add(Vector3I min, Vector3I max, bool heavy)
        {
            for (int s = 0; s < 2; s++)
            {
                if (!Cover(s, min, max, out int from, out int to, out int c0, out int c1, out int n0, out int n1)) continue;
                float len = n1 - n0 + 1;
                for (int j = from; j <= to; j++)
                {
                    int idx = _stations0[s] + j;
                    if (_lo[idx] == null)
                    {
                        _lo[idx] = new int[_cols]; _hi[idx] = new int[_cols]; _heavy[idx] = new float[_cols]; _len[idx] = new float[_cols];
                        Array.Fill(_lo[idx], int.MaxValue); Array.Fill(_hi[idx], int.MinValue);
                    }
                    var lo = _lo[idx]; var hi = _hi[idx]; var hv = _heavy[idx]; var ln = _len[idx];
                    for (int c = c0; c <= c1; c++)
                    {
                        if (n0 < lo[c]) lo[c] = n0;
                        if (n1 > hi[c]) hi[c] = n1;
                        ln[c] += len; if (heavy) hv[c] += len;
                    }
                }
            }
        }

        /// <summary>Whether a block's box lies in side s's station j (its slab, in the window): a root block, there.</summary>
        public bool AtStation(int s, int j, Vector3I min, Vector3I max)
            => Cover(s, min, max, out int from, out int to, out _, out _, out _, out _) && j >= from && j <= to;

        /// <summary>N m: the capacity of side s's station j (0: no block there), times multiplier. Per chord column (a game
        /// cell, 0.25 m wide): CellStrength * w * t^2, t its depth (lowest to highest block in the window), heavy armour's
        /// share of it HeavyFactor times stronger.</summary>
        public float Capacity(int s, int j, float multiplier = 1f)
        {
            if (_r.Get(s) == null) return 0f;
            int idx = _stations0[s] + j;
            if (idx < 0 || idx >= _lo.Length || _lo[idx] == null) return 0f;
            var lo = _lo[idx]; var hi = _hi[idx]; var hv = _heavy[idx]; var ln = _len[idx];
            double cap = 0;
            for (int c = 0; c < _cols; c++)
            {
                if (hi[c] < lo[c]) continue;
                float t = (hi[c] - lo[c] + 1) * 0.25f;
                float heavyShare = ln[c] > 0 ? MathF.Min(1f, hv[c] / ln[c]) : 0f;
                cap += 0.25 * t * t * (1f + (HeavyFactor - 1f) * heavyShare);
            }
            return (float)(cap * CellStrength * multiplier);
        }

        /// <summary>Every station's capacity on side s (a new array: Finish's input).</summary>
        public float[] Capacities(int s, float multiplier = 1f)
        {
            var side = _r.Get(s);
            var a = new float[side?.Stations ?? 0];
            for (int j = 0; j < a.Length; j++) a[j] = Capacity(s, j, multiplier);
            return a;
        }
    }

    /// <summary>The wing's force across it (N, along its normal): its lift and induced drag, the part along the normal.</summary>
    public static float NormalForce(in LiftingSurface wing, Vector3 lift, Vector3 inducedDrag) => Vector3.Dot(lift + inducedDrag, wing.Normal);

    /// <summary>The side's strain for the wing's normal force (N, either sign) and a strength multiplier.</summary>
    public static float Strain(float normalForce, Side side, float strength = 1f)
        => side == null || strength <= 0f ? 0f : MathF.Abs(normalForce) * side.Compliance / strength;

    /// <summary>The side's worst bending moment (N m) for the wing's normal force (N, either sign).</summary>
    public static float Moment(float normalForce, Side side) => side == null ? 0f : MathF.Abs(normalForce) * side.CriticalArm;

    /// <summary>Health to take off (fraction of full health) for overSeconds = the integral over a window of (strain -
    /// limit) where positive (s).</summary>
    public static float Wear(float overSeconds) => overSeconds > 0 ? WearPerSecond * overSeconds : 0f;

    /// <summary>Strain at which a root snaps.</summary>
    public static bool Breaks(float strain) => strain >= BreakStrain;
}
