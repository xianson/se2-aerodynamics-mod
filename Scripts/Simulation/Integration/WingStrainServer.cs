#pragma warning disable
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Damage;
namespace AeroMod;

/// <summary>The world's wing-strain settings (AeroSettings: Structure.WingStrain, Structure.WingStrength).</summary>
public static class WingStructure
{
    /// <summary>Overloaded wings wear at the root and break off. OFF by default: it destroys players' blocks, the wing
    /// detector's guesses (a flat hull panel taken for a wing) would break them too, and it is not yet tuned in game. The
    /// strain itself is computed either way (the API reports it).</summary>
    public static bool Enabled = false;
    /// <summary>The wings' strength, times this (2: twice the load before they wear).</summary>
    public static float Strength = 1f;
}

/// <summary>
/// WING STRAIN, SERVER (the model: Lift/WingStrain.cs). Each simulation step, per wing: its force across it (lift and
/// induced drag along its normal, times what is left of it and the world's lift multiplier) bends it; strain = its
/// worst station's moment / capacity, smoothed over ~0.15 s (no single-frame spikes). With Structure.WingStrain on,
/// ~4 Hz: a root past strain 1 loses health in proportion to the overshoot and the time (WingStrain.Wear), past WingStrain.BreakStrain
/// its blocks are destroyed - the game splits the wing off itself. Damage through the game's damage receiver, as the
/// Orbital Mod's entry damage (its game-mode rules apply: creative takes none).
///
/// The roots - every span station's section from the blocks there (light or heavy armour), the worst station, and its
/// blocks: the ones that wear and break - are found once per wing set (a rebuild, a wing lost), not per frame. Per
/// frame: a few multiplies per wing, no allocation, no try/catch (only the rare damage call is guarded).
/// </summary>
public partial class AeroGridComponent
{
    private List<LiftingSurface> _strainWings;
    private WingStrain.Root[] _strainRoots = Array.Empty<WingStrain.Root>();
    private List<CubeBlockComponent>[] _strainBlocks = Array.Empty<List<CubeBlockComponent>>();   // [wing * 2 + side]: the worst station's blocks
    private float[] _strainRaw = Array.Empty<float>(), _strainFilt = Array.Empty<float>(), _strainMoment = Array.Empty<float>(),
        _strainCapNow = Array.Empty<float>(), _strainOver = Array.Empty<float>();
    private int[] _strainSide = Array.Empty<int>();
    private bool[] _strainBroken = Array.Empty<bool>();
    private float _strainClock;

    /// <summary>The highest wing strain on the grid (smoothed), last step. 0: no wings.</summary>
    internal float MaxWingStrain;

    /// <summary>Damage at most this often (s).</summary>
    const float StrainDamageInterval = 0.25f;
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
        if (wings == null || forces == null || wings.Count == 0 || forces.Count != wings.Count || !HasResult) { MaxWingStrain = 0f; return; }
        if (!ReferenceEquals(wings, _strainWings) || _strainRoots.Length != wings.Count) RebuildStrainRoots(wings);
        int n = wings.Count;
        if (_strainFilt.Length != n) return;   // (the rebuild failed: none this time)

        float lm = AeroTuning.LiftMultiplier, mult = WingStructure.Strength;
        float a = MathF.Min(1f, dt / StrainSmoothing), max = 0f;
        bool on = WingStructure.Enabled;
        for (int i = 0; i < n; i++)
        {
            var root = _strainRoots[i];
            if (root == null || _strainBroken[i]) { _strainRaw[i] = _strainFilt[i] = 0f; continue; }
            var wf = forces[i];
            float fn = WingStrain.NormalForce(wings[i], wf.LiftForce, wf.InducedDrag) * model.WingRemaining(i) * lm;
            float worst = 0f, wm = 0f, wc = 0f; int ws = 0;
            for (int s = 0; s < 2; s++)
            {
                var side = root.Get(s);
                if (side == null) continue;
                float st = WingStrain.Strain(fn, side, mult);
                if (st >= worst) { worst = st; wm = WingStrain.Moment(fn, side); wc = side.CriticalCapacity * mult; ws = s; }
            }
            _strainRaw[i] = worst; _strainMoment[i] = wm; _strainCapNow[i] = wc; _strainSide[i] = ws;
            float f = _strainFilt[i] += (worst - _strainFilt[i]) * a;
            if (on && f > WingStrain.LimitStrain) _strainOver[i] += (f - WingStrain.LimitStrain) * dt;
            if (f > max) max = f;
        }
        MaxWingStrain = max;

        _strainClock += dt;
        if (_strainClock < StrainDamageInterval) return;
        _strainClock = 0f;
        if (!on) { Array.Clear(_strainOver, 0, _strainOver.Length); return; }
        for (int i = 0; i < n; i++)
        {
            float over = _strainOver[i]; _strainOver[i] = 0f;
            if (_strainBroken[i]) continue;
            bool snap = WingStrain.Breaks(_strainFilt[i]);
            if (!snap && over <= 0f) continue;
            var blocks = _strainBlocks[i * 2 + _strainSide[i]];
            if (blocks == null || blocks.Count == 0) continue;
            DamageRoot(blocks, snap, WingStrain.Wear(over));
            if (snap)
            {
                _strainBroken[i] = true;   // (until the wings are re-detected: what is left is another wing)
                Log.Default?.Info($"[AERO-STRAIN] grid {Entity?.DebugName}: wing {i} broke at strain {_strainFilt[i]:F2} (root moment {_strainMoment[i] / 1000f:F0} kN m, capacity {_strainCapNow[i] / 1000f:F0} kN m, {blocks.Count} root block(s))");
            }
        }
    }

    /// <summary>Wears (fraction of full health) or destroys the root's blocks. Rare: guarded.</summary>
    private void DamageRoot(List<CubeBlockComponent> blocks, bool destroy, float fraction)
    {
        try
        {
            var recv = Entity?.TryGet<GridDamageReceiverComponent>();
            if (recv == null) return;
            foreach (var b in blocks)
            {
                if (b == null || b.HealthIntegrity <= 0f) continue;
                if (destroy) { recv.DealDamage(b, float.MaxValue); continue; }   // (clamped to its full health)
                // (its full health: the definition's MaxHealth is not reachable from mod scripts)
                float full = b.HealthIntegrity > 1e-3f ? b.AbsoluteHealth / b.HealthIntegrity : 0f;
                float hp = fraction * full;
                if (hp > 0f) recv.DealDamage(b, hp);
            }
        }
        catch (Exception e) { Log.Default?.Info($"[AERO-STRAIN] damage failed: {e.Message}"); }
    }

    /// <summary>The wings' roots for a new wing set: their analysis (Lift/WingStrain), one pass over the blocks for every
    /// station's section (light / heavy armour), then the worst stations' blocks among those (the ones to wear or break).
    /// Once per wing set (logged past 5 ms). Rare: guarded.</summary>
    private void RebuildStrainRoots(List<LiftingSurface> wings)
    {
        _strainWings = wings;
        int n = wings.Count;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        WingStrain.Root[] roots = new WingStrain.Root[n];
        var blocks = new List<CubeBlockComponent>[n * 2];
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
            var hits = new List<(CubeBlockComponent block, Vector3I min, Vector3I max, int wing)>();
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
                            if (heavyFlag == 0) heavyFlag = (block.Entity?.DebugName ?? "").IndexOf("Heavy", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 1;
                            sc.Add(g.Min, g.Max, heavyFlag == 2);
                            hits.Add((block, g.Min, g.Max, i));
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
                        blocks[i * 2 + s] = new List<CubeBlockComponent>();
                    }
                // the worst stations' blocks: among the ones that counted (not every block again)
                foreach (var (block, mn, mx, i) in hits)
                    for (int s = 0; s < 2; s++)
                    {
                        var side = roots[i].Get(s);
                        if (side == null) continue;
                        var list = blocks[i * 2 + s];
                        if (secs[i].AtStation(s, side.Critical, mn, mx) && !list.Contains(block)) list.Add(block);
                    }
            }
        }
        catch (Exception e)
        {
            Log.Default?.Info($"[AERO-STRAIN] grid {Entity?.DebugName}: root analysis failed ({e.Message})");
            // (kept for this wing set, all empty: no strain, and no retry - so no exception - each frame)
            _strainRoots = new WingStrain.Root[n]; _strainFilt = Array.Empty<float>(); MaxWingStrain = 0f;
            return;
        }
        if (System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds > 5)
            Log.Default?.Info($"[AERO-STRAIN] grid {Entity?.DebugName}: roots of {n} wings in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:F1} ms");
        _strainRoots = roots; _strainBlocks = blocks;
        _strainRaw = new float[n]; _strainMoment = new float[n]; _strainCapNow = new float[n]; _strainOver = new float[n];
        _strainSide = new int[n]; _strainBroken = new bool[n];
        _strainFilt = new float[n];   // (last: the API reads it as the "ready" mark)
    }
}
