#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>The wing strain's scale (the API's numbers: AeroApi.TryGetWingStrain / GetMaxWingStrain).</summary>
/// (2026-10-17 release prep: the legacy wing-root wear / breaks - off by default, superseded by Structure.Damage - and
///  its settings Structure.WingStrain / Structure.WingStrength went; a settings file that still has them loads: unknown
///  names are skipped. The strain itself stays: the API reports it.)
public static class WingStructure
{
    /// <summary>The wings' strength, times this (2: half the strain).</summary>
    /// (25: calibrated so real ships - long, one-block-deep wings: the jetliner, 63 m - hold ~6 g; at 1 the jetliner
    ///  snapped in level flight. Tests/AeroBench strain mode reports a ship's limits.)
    public static float Strength = 25f;
}

/// <summary>
/// WING STRAIN, SERVER (the model: Lift/WingStrain.cs) - reported through the API only, no damage (the structure,
/// Structure.Damage, breaks wings). Each simulation step, per wing: its force across it (lift and induced drag along its
/// normal, times what is left of it and the world's lift multiplier) bends it; strain = its worst station's moment /
/// capacity, smoothed over ~0.15 s (no single-frame spikes).
///
/// The roots - every span station's section from the blocks there (light or heavy armour) and the worst station - are
/// found once per wing set (a rebuild, a wing lost), not per frame. Per frame: a few multiplies per wing, no allocation,
/// no try/catch.
/// </summary>
public partial class AeroGridComponent
{
    private List<LiftingSurface> _strainWings;
    private WingStrain.Root[] _strainRoots = Array.Empty<WingStrain.Root>();
    private float[] _strainRaw = Array.Empty<float>(), _strainFilt = Array.Empty<float>(), _strainMoment = Array.Empty<float>(),
        _strainCapNow = Array.Empty<float>();

    /// <summary>The highest wing strain on the grid (smoothed), last step. 0: no wings.</summary>
    internal float MaxWingStrain;

    /// <summary>The strain's smoothing time (s).</summary>
    const float StrainSmoothing = 0.15f;

    /// <summary>The API's read: wing i's strain (smoothed), its root moment (N m) and capacity (N m), the worse side's.</summary>
    internal bool TryGetWingStrain(int i, out float strain, out float moment, out float capacity)
    {
        var f = _strainFilt; var m = _strainMoment; var c = _strainCapNow;
        strain = moment = capacity = 0f;
        if (i < 0 || i >= f.Length || i >= m.Length || i >= c.Length) return false;
        strain = f[i]; moment = m[i]; capacity = c[i];
        return true;
    }

    /// <summary>The server's step (AeroSimJob, after the forces). dt: the simulation step (s).</summary>
    internal void TickWingStrain(float dt)
    {
        var model = _model;
        var wings = model?.Wings; var forces = model?.LastWingForces;
        if (wings == null || forces == null || wings.Count == 0 || forces.Count != wings.Count || !HasResult)
        {
            // (no load without a result - parked, vacuum: the filter falls back to rest; it froze, the API kept a landed
            //  ship's 0.9 and the next takeoff started from it)
            MaxWingStrain = 0f;
            float k = MathF.Min(1f, dt / StrainSmoothing);
            for (int i = 0; i < _strainFilt.Length; i++) { _strainFilt[i] -= _strainFilt[i] * k; if (i < _strainRaw.Length) _strainRaw[i] = 0f; }
            return;
        }
        if (!ReferenceEquals(wings, _strainWings) || _strainRoots.Length != wings.Count) RebuildStrainRoots(wings);
        int n = wings.Count;
        if (_strainFilt.Length != n) return;   // (the rebuild failed: none this time)

        float lm = AeroTuning.LiftMultiplier, mult = WingStructure.Strength;
        float a = MathF.Min(1f, dt / StrainSmoothing), max = 0f;
        for (int i = 0; i < n; i++)
        {
            var root = _strainRoots[i];
            if (root == null) { _strainRaw[i] = _strainFilt[i] = 0f; continue; }
            var wf = forces[i];
            float fn = WingStrain.NormalForce(wings[i], wf.LiftForce, wf.InducedDrag) * model.WingRemaining(i) * lm;
            float worst = 0f, wm = 0f, wc = 0f;
            for (int s = 0; s < 2; s++)
            {
                var side = root.Get(s);
                if (side == null) continue;
                float st = WingStrain.Strain(fn, side, mult);
                if (st >= worst) { worst = st; wm = WingStrain.Moment(fn, side); wc = side.CriticalCapacity * mult; }
            }
            _strainRaw[i] = worst; _strainMoment[i] = wm; _strainCapNow[i] = wc;
            float f = _strainFilt[i] += (worst - _strainFilt[i]) * a;
            if (f > max) max = f;
        }
        MaxWingStrain = max;
    }

    /// <summary>The wings' roots for a new wing set: their analysis (Lift/WingStrain), one pass over the blocks for every
    /// station's section (light / heavy armour). Once per wing set. Rare: guarded.</summary>
    private void RebuildStrainRoots(List<LiftingSurface> wings)
    {
        _strainWings = wings;
        int n = wings.Count;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        WingStrain.Root[] roots = new WingStrain.Root[n];
        try
        {
            var (cs, co) = FxCellGeo;
            int k = Math.Max(1, (int)MathF.Round(cs / 0.25f));
            float b = _blockSize > 0 ? _blockSize : AeroConfig.LargeBlockSize;
            int blockCells = Math.Max(1, (int)MathF.Round(b / 0.25f));
            WingStrain.Sections[] secs = new WingStrain.Sections[n];
            for (int i = 0; i < n; i++)
            {
                roots[i] = WingStrain.Analyze(wings[i], cs, co, LastCoM);
                if (roots[i] != null) secs[i] = new WingStrain.Sections(roots[i], k, blockCells);
            }
            var octree = _octree;
            if (octree != null)
            {
                foreach (var block in octree.GetAllCubeBlocks())
                {
                    if (block == null) continue;
                    int heavyFlag = 0;   // (0 unknown, 1 light, 2 heavy: looked up once a block counts somewhere)
                    foreach (var g in block.GetTransformedOccupiedCellGroups())
                        for (int i = 0; i < n; i++)
                        {
                            var sc = secs[i];
                            if (sc == null || !sc.Covers(g.Min, g.Max)) continue;
                            // (the DEFINITION's name - BlockName, cached per definition: the entity's is "CubeBlock_x:y:z", never
                            //  "Heavy" - every heavy-armour wing had light armour's strength, 2026-10-15)
                            if (heavyFlag == 0) heavyFlag = BlockName(block).IndexOf("Heavy", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 1;
                            sc.Add(g.Min, g.Max, heavyFlag == 2);
                        }
                }
                // each side's worst station on the blocks' sections (none found - a wing from an old table cache, say: its
                // own cells' section, as Analyze left it)
                for (int i = 0; i < n; i++)
                    for (int s = 0; s < 2; s++)
                    {
                        var side = roots[i]?.Get(s);
                        if (side == null) continue;
                        var caps = secs[i].Capacities(s);
                        bool any = false; foreach (var c in caps) if (c > 0f) { any = true; break; }
                        if (any) WingStrain.Finish(side, caps);
                    }
            }
        }
        catch (Exception e)
        {
            Log.Default?.Warning($"[AERO-STRAIN] grid {Entity?.DebugName}: root analysis failed ({e.Message})");
            // (kept for this wing set, all empty: no strain, and no retry - so no exception - each frame)
            _strainRoots = new WingStrain.Root[n]; _strainFilt = Array.Empty<float>(); MaxWingStrain = 0f;
            return;
        }
        if (AeroLog.Detail && System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds > 5)
            Log.Default?.Info($"[AERO-STRAIN] grid {Entity?.DebugName}: roots of {n} wings in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} ms");
        _strainRoots = roots;
        _strainRaw = new float[n]; _strainMoment = new float[n]; _strainCapNow = new float[n];
        _strainFilt = new float[n];   // (last: the API reads it as the "ready" mark)
    }
}
