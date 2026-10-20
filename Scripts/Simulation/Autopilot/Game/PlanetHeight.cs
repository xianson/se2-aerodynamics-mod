#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// THE PLANET'S HEIGHT in game (IPlanetHeight - the autopilot's long-range terrain: PlanetTerrainQuery over it, CombinedTerrain
/// with the rays). SE2 4.0's own analytic surface, as the game's PlanetTrackerSessionComponent.TryFindPlanetAndSurface and its
/// encounters (MoveToPlanetSurfaceEncounterProcessorComponent) ask it: VoxelPlanetComponent.QuerySurfacePosition(Vector3 localPos,
/// out Vector3 surfacePos, int lod, bool pinned) - the planet's generated heightmap (its base cubemap and detail) at the line
/// through localPos, in the planet's local voxel units (WorldToLocalPosition: the planet's transform inverted, / VoxelSize).
/// BY REFLECTION: VoxelPlanetComponent is in VRage.Voxels, which mod scripts cannot reference (docs/REFLECTION_AND_WHITELIST.md).
/// Resolved once per planet: the component found on the planet's entity by its type's name (the server planets: the settings
/// carriers, AeroWorldSettingsComponent.Planets - the planet's DiscoverablePlanetComponent and its VoxelPlanetComponent are one
/// entity), the method made a CLOSED DELEGATE over it (no boxing, no allocation per call), VoxelSize and RadiusWithMaxHills read
/// once. Anything missing: logged once, no planet height - the autopilot keeps its rays.
/// WHAT IT KNOWS (decompiled VRage.Voxels): pinned = true pins the heightmap's tile at that detail (and its parents) and waits for
/// it (PlanetTextureStreamingManager.WaitForPinnedResource) - the data is always the real heightmap, coarser with a higher lod, never
/// "unloaded"; the first ask of a far tile may wait for its load. It is the GENERATED shape only (PlanetShapeProvider.SamplePlanetPosition
/// samples the heightmaps, not the voxel storage): a hole dug, a voxel placed, a grid - the rays' (they are kept for those near).
/// Cached: the last answer per caller is reused within 2 m.
/// </summary>
internal sealed class GamePlanetHeight : IPlanetHeight
{
    delegate bool SurfaceFn(Vector3 localPos, out Vector3 surfacePos, int lod, bool pinned);

    sealed class Planet
    {
        public Entity E; public SurfaceFn Fn; public float VoxelSize = 1f; public double Reach;
    }

    static readonly object _lock = new();
    static Planet[] _planets = Array.Empty<Planet>();
    static long _nextScan;
    static bool _warned;
    /// <summary>Harness / logs: how the resolution went ("" not yet).</summary>
    public static string Status = "";
    public static long Queries;

    // (this caller's last answer)
    Vector3D _lastP; int _lastLod = -1; Vector3D _lastC; double _lastR; bool _lastOk;

    public bool Surface(Vector3D p, int lod, out Vector3D centre, out double surfaceR)
    {
        if (_lastLod == lod && (p - _lastP).LengthSquared() < 4.0) { centre = _lastC; surfaceR = _lastR; return _lastOk; }
        centre = default; surfaceR = 0;
        Planet[] list = Planets();
        Planet best = null; double bd = double.MaxValue; WorldTransform bwt = default;
        for (int i = 0; i < list.Length; i++)
        {
            var pl = list[i];
            var e = pl.E;
            if (e == null || !GridPhysics.Alive(e.Data)) continue;
            var wt = e.Data.GetWorldTransform();
            double d = (p - wt.Position).LengthSquared();
            if (d < bd && d < pl.Reach * pl.Reach) { bd = d; best = pl; bwt = wt; }
        }
        bool ok = false;
        if (best != null)
        {
            Vector3 local = (Vector3)WorldTransform.TransformInv(p, bwt) / best.VoxelSize;
            System.Threading.Interlocked.Increment(ref Queries);
            if (best.Fn(local, out Vector3 surf, Math.Clamp(lod, 0, 8), true))
            {
                centre = bwt.Position;
                surfaceR = surf.Length() * best.VoxelSize;
                ok = surfaceR > 0 && double.IsFinite(surfaceR);
            }
        }
        _lastP = p; _lastLod = lod; _lastC = centre; _lastR = surfaceR; _lastOk = ok;
        return ok;
    }

    /// <summary>The server planets with a resolved height query (refreshed every 10 s; copy-on-write).</summary>
    static Planet[] Planets()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < System.Threading.Volatile.Read(ref _nextScan)) return _planets;
        lock (_lock)
        {
            if (now < _nextScan) return _planets;
            _nextScan = now + 10 * System.Diagnostics.Stopwatch.Frequency;
            var keep = new List<Planet>();
            List<Entity> carriers = new();
            lock (AeroWorldSettingsComponent.Planets)
                foreach (var c in AeroWorldSettingsComponent.Planets) if (c?.Entity != null) carriers.Add(c.Entity);
            foreach (var e in carriers)
            {
                Planet known = null;
                foreach (var pl in _planets) if (ReferenceEquals(pl.E, e)) { known = pl; break; }
                known ??= Resolve(e);
                if (known != null) keep.Add(known);
            }
            _planets = keep.ToArray();
            return _planets;
        }
    }

    /// <summary>The planet entity's VoxelPlanetComponent, its query as a closed delegate, its voxel size and reach (once a planet).</summary>
    static Planet Resolve(Entity e)
    {
        try
        {
            object vpc = null;
            foreach (var c in e.Components)
                if (c != null && c.GetType().FullName == "Keen.VRage.Voxels.Components.VoxelPlanetComponent") { vpc = c; break; }
            if (vpc == null) { Warn("no VoxelPlanetComponent on the planet entity"); return null; }
            var t = vpc.GetType();
            const BindingFlags pub = BindingFlags.Public | BindingFlags.Instance;
            var m = t.GetMethod("QuerySurfacePosition", pub, null, new[] { typeof(Vector3), typeof(Vector3).MakeByRefType(), typeof(int), typeof(bool) }, null);
            if (m == null || m.ReturnType != typeof(bool)) { Warn("VoxelPlanetComponent.QuerySurfacePosition(Vector3, out Vector3, int, bool) not found"); return null; }
            var fn = (SurfaceFn)Delegate.CreateDelegate(typeof(SurfaceFn), vpc, m, false);
            if (fn == null) { Warn("QuerySurfacePosition: no delegate"); return null; }
            float vs = t.GetProperty("VoxelSize", pub)?.GetValue(vpc) is float v && v > 0f && float.IsFinite(v) ? v : 1f;
            double reach = t.GetProperty("RadiusWithMaxHills", pub)?.GetValue(vpc) is float rh && rh > 0f ? rh * 3.0 : 1e6;   // (three radii: the gravity's reach and more)
            Status = $"resolved ({t.Assembly.GetName().Name} {t.Assembly.GetName().Version}; voxel {vs} m)";
            return new Planet { E = e, Fn = fn, VoxelSize = vs, Reach = reach };
        }
        catch (Exception ex)   // (once a planet, every 10 s at most - never per tick)
        {
            Warn(ex.GetType().Name + ": " + ex.Message);
            return null;
        }
    }

    static void Warn(string why)
    {
        Status = "unavailable: " + why;
        if (_warned) return;
        _warned = true;
        Log.Default?.Warning("[AERO] autopilot: the planet's height query is unavailable (" + why + ") - the terrain is the rays' only");
    }
}
