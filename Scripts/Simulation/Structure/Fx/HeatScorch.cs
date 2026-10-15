#pragma warning disable
using System;
using System.Collections.Generic;
using Keen.VRage.Core;
using Keen.VRage.Library.Definitions;
using Keen.Game2.Client.GameSystems.Decals;

namespace AeroMod;

/// <summary>
/// HEAT SCORCH (2026-10-14, the user: "really cool scorch mark decals ... apply those from heat", "a few large scorchs"):
/// where the hull got hot enough, the game's own explosion scorch decal (its texture, at our size) - a FEW LARGE marks per
/// ship, not one per block. Server: after each structure evaluation the hull's hot faces (HullHeat skin) are gathered into at
/// most MaxSpots spots per ship; a spot only ever grows and darkens (a burn does not un-burn). Client: the flight field's
/// pairing (FindFlow: the server copy of this grid) gives the spots; each is stamped as one decal sized to it, replaced when
/// it grew or darkened. Decals are the game's - its budget fades the oldest, and they are not saved with the world.
/// </summary>
public static class HeatScorch
{
    public static bool Enabled = true;
    /// <summary>Spots per ship; faces within JoinMeters of a spot (and facing its way) belong to it.</summary>
    public static int MaxSpots = 4;
    public static float JoinMeters = 4f, MinDiameter = 1.5f, MaxDiameter = 12f, Depth = 2f;
    // the game's explosion scorch (DecalInstanceDefinition): dark, lighter
    static readonly Guid DarkDef = new Guid("666f5ef7-ce85-4f25-9518-44a215918c59"), LightDef = new Guid("c8a26f6e-dc09-44c7-ace6-7a854ed89e15");
    public static string Status = "-";
    public static int Placed, Missed;
    /// <summary>DEV: the game's own scorch (5 m) instead of our sized copies; the first spot's placement.</summary>
    public static bool TestOriginal, TestAnyHeat; public static string First = "-";

    public sealed class Spot
    {
        public Vector3 Center, Normal; public float Radius, PeakK; public int Faces;   // (PeakK: the spot's hottest skin, K)
        /// <summary>The hottest face's point and normal: a real hull point, where the mark is stamped.</summary>
        public Vector3 HotPos, HotNormal;
    }

    // -- server: the spots of a grid, from its RE-ENTRY heat --
    // (RE-ENTRY ONLY - the user: "i just want reentry heat to do scorch marks, not aero loads", "use an orbital api instead
    //  of some glow". The hull heat model's skin temperatures - which a railed entry now heats too, from the Orbital mod's
    //  entry STATE (the true air speed and density: AeroApi.RegisterEntryState) - and only while the ship is in a re-entry:
    //  the Orbital mod's entry state, or the ship's own hypersonic heating past the glow's onset. Ordinary flight never.)
    /// <summary>Skin temperature (K) a face must reach to scorch light / dark (a real entry's nose: ~1000 K).</summary>
    public static float LightK = 700f, DarkK = 950f;

    internal static void Update(AeroGridComponent g, HullHeat hh, Vector3 airVelLocal, float dt)
    {
        if (!Enabled || hh == null || hh.FaceCount == 0) return;
        bool reentry = TestAnyHeat || AeroEntryFx.EntryStateAt(g.Entity, out _, out _) || g.EntryHeat >= AeroEntryFx.OnsetHeat;
        if (!reentry) return;
        if (hh.MaxSkin(out _) < LightK) return;
        var spots = g.ScorchSpots;   // (this thread's own working list; published as a copy)
        bool changed = false;
        for (int i = 0; i < hh.FaceCount; i++)
        {
            float t = hh.FaceT[i];
            if (!(t >= LightK)) continue;
            Vector3 p = hh.FacePosition(i), n = hh.FaceNormal(i);
            Spot best = null; float bd = float.MaxValue;
            foreach (var sp in spots)
            {
                float d = Vector3.Distance(sp.Center, p);
                if (d < sp.Radius + JoinMeters && Vector3.Dot(sp.Normal, n) > 0.3f && d < bd) { bd = d; best = sp; }
            }
            if (best == null)
            {
                if (spots.Count >= MaxSpots)
                {
                    // (the nearest spot takes it, whichever way it faces: a few large marks)
                    foreach (var sp in spots) { float d = Vector3.Distance(sp.Center, p); if (d < bd) { bd = d; best = sp; } }
                }
                else { spots.Add(new Spot { Center = p, Normal = n, Radius = 0f, PeakK = t, Faces = 1, HotPos = p, HotNormal = n }); changed = true; continue; }
            }
            // (grows to reach the face - its centre moving a little toward it - never shrinks; darkens to its peak)
            float need = Vector3.Distance(best.Center, p);
            if (need > best.Radius)
            {
                float grow = 0.5f * (need - best.Radius);
                best.Center += (p - best.Center) * (grow / MathF.Max(need, 1e-3f));
                best.Radius += grow; changed = true;
            }
            if (t > best.PeakK)
            {
                bool darker = best.PeakK < DarkK && t >= DarkK;
                best.PeakK = t; best.HotPos = p; best.HotNormal = n; if (darker) changed = true;
            }
            best.Normal = Vector3.Normalize(best.Normal * best.Faces + n); best.Faces++;
        }
        if (changed)
        {
            var copy = new List<Spot>(spots.Count);
            foreach (var sp in spots) copy.Add(new Spot { Center = sp.Center, Normal = sp.Normal, Radius = sp.Radius, PeakK = sp.PeakK, Faces = sp.Faces, HotPos = sp.HotPos, HotNormal = sp.HotNormal });
            System.Threading.Volatile.Write(ref g.ScorchPublished, copy);
            System.Threading.Interlocked.Increment(ref g.ScorchVersion);
        }
    }

    // -- client: the decals of a grid, from its server copy's spots --
    // (the game's own placement - IDecals.TryPlaceDecal on the grid: it finds the block at the point and parents the mark to
    //  it, with the render parenting a mod cannot reach (VRage.Render). The size is the definition's: a shallow copy per size
    //  step and shade, its Size set - reflection, docs/REFLECTION_AND_WHITELIST.md)
    static readonly object _lock = new object();
    static DecalInstanceDefinition _dark, _light; static bool _resolved;
    public static readonly float[] Steps = { 2f, 3f, 4f, 6f, 8f, 10f, 12f };
    static readonly Dictionary<(int step, bool dark), DecalInstanceDefinition> _sized = new Dictionary<(int step, bool dark), DecalInstanceDefinition>();
    static System.Reflection.PropertyInfo _sizeProp; static System.Reflection.MethodInfo _clone;

    static DecalInstanceDefinition Sized(int step, bool dark)
    {
        if (_sized.TryGetValue((step, dark), out var d)) return d;
        var src = dark ? _dark : _light;
        _clone ??= typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        _sizeProp ??= typeof(DecalInstanceDefinition).GetProperty("Size", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        d = (DecalInstanceDefinition)_clone.Invoke(src, null);
        float dia = Steps[step];
        _sizeProp.SetValue(d, new Vector3(dia, dia, Depth));
        _sized[(step, dark)] = d;
        return d;
    }

    internal static void Sync(AeroEntryFxComponent c, AeroGridComponent server)
    {
        if (!Enabled || server == null) return;
        int ver = System.Threading.Volatile.Read(ref server.ScorchVersion);
        if (ver == c.ScorchVersion) return;
        var spots = System.Threading.Volatile.Read(ref server.ScorchPublished);
        if (spots == null) return;
        c.ScorchVersion = ver;
        var grid = c.Entity; if (grid == null) return;
        lock (_lock)
        {
            try
            {
                if (!_resolved)
                {
                    _resolved = true;
                    DefinitionManager.Instance.TryGetDefinition(DarkDef, out _dark);
                    DefinitionManager.Instance.TryGetDefinition(LightDef, out _light);
                    if (_dark == null || _light == null) Status = "scorch decals not found";
                }
                if (_dark == null || _light == null) return;
                var decals = grid.GetSession()?.SessionComponents.TryGet<DecalSessionComponent>();
                if (decals == null) { Status = "no decal system"; return; }
                c.ScorchStamped ??= new List<(int step, bool dark)>();
                for (int i = 0; i < spots.Count; i++)
                {
                    var s = spots[i];
                    float dia = Math.Clamp(2f * s.Radius + 1f, MinDiameter, MaxDiameter);
                    int step = 0; while (step < Steps.Length - 1 && Steps[step + 1] <= dia) step++;
                    bool dark = s.PeakK >= DarkK;
                    if (i < c.ScorchStamped.Count && c.ScorchStamped[i].step >= step && (c.ScorchStamped[i].dark || !dark)) continue;   // (no bigger, no darker: kept)
                    var n = s.HotNormal.LengthSquared() > 1e-6f ? Vector3.Normalize(s.HotNormal) : Vector3.Up;
                    // (on the BLOCK at the hot face, in its own frame - as the game's projectile hits are placed. Through the grid
                    //  the mark went nowhere: the grid's receiver reads the point as a world position in one step and as a
                    //  grid one in the next. The block: the cell just inside the face)
                    var octree = grid.TryGet<Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees.BlockOctreeComponent>();
                    Vector3 inside = s.HotPos - n * 0.125f;
                    var block = octree?.TryGetCubeBlock(AeroGridComponent.CellOf(inside));
                    if (block?.Entity == null) { Missed++; continue; }
                    var onGrid = new DecalPlacementParameters(s.HotPos - n * 0.02f, n);
                    var onBlock = onGrid.InvTransformBy(block.Entity.Data.GetRelativeTransform());
                    decals.TryPlaceDecal(TestOriginal ? (dark ? _dark : _light) : Sized(step, dark), block.Entity, onBlock);
                    if (i == 0) First = $"spot 0 at {s.HotPos} n {n} dia {Steps[step]} dark {dark} peak {s.PeakK:F0} K; grid box {grid.Data.GetWorldTransform().Position}";
                    if (i < c.ScorchStamped.Count) c.ScorchStamped[i] = (step, dark); else c.ScorchStamped.Add((step, dark));
                    Placed++;
                }
                Status = $"{grid.DebugName}: {spots.Count} spot(s), {Placed} placed, {Missed} missed (no block at the spot)";
            }
            catch (Exception e) { Status = "scorch failed: " + e.GetType().Name + ": " + e.Message; Enabled = false; }
        }
    }
}
