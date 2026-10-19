#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.VRage.Core.Game.GameSystems.Queries;
using Keen.VRage.Physics;
using Keen.VRage.Physics.Queries;

namespace AeroMod;

/// <summary>
/// THE TERRAIN IN GAME for the autopilot (ITerrainQuery; TerrainWatch asks it a few times a second): the physics' own ray
/// (IPhysics.CastRay, synchronous, CollisionPreset.Closest - the planet's voxels and every grid but this ship and what is
/// joined to it), cast from this grid's attitude job before the physics step, into this grid's own buffers (native, grown once,
/// reused: no allocation per ray - GridPhysics.GroundProbe's pattern). The hit's distance and normal.
/// THE GAME'S LIMIT (SE2 4.0, decompiled VRage.Physics / VRage.Voxels): voxel colliders exist only near entities - built around
/// them, ahead of a moving one along its velocity (VoxelPhysicsComponent: a prediction margin of 40 voxels and a lead time) - so a
/// ray reaching far past that finds nothing to hit even where there is ground: "nothing found" is "not known", never "clear"
/// (TerrainWatch treats a miss as no ground - the height hold falls back to the gravity's). The planet's analytic surface
/// (VoxelPlanetComponent.QuerySurfacePosition) is in VRage.Voxels, which mods cannot reference.
/// </summary>
internal sealed class GridTerrain : ITerrainQuery
{
    readonly GridPhysics.GroundProbe _probe = new GridPhysics.GroundProbe();
    readonly AeroGridComponent _aero;
    public int Casts, Hits;
    public GridTerrain(AeroGridComponent aero) { _aero = aero; }

    public bool Below(Vector3D p, Vector3 down, float reach, out float height, out Vector3 normal)
        => Cast(p, down, reach, out height, out normal);

    public bool Cast(Vector3D p, Vector3 dir, float length, out float distance, out Vector3 normal)
    {
        distance = 0f; normal = Vector3.UnitY;
        var physics = GridPhysics.Physics;
        var grid = _aero.Entity;
        if (physics == null || grid == null || !(length > 0f) || !GridPhysics.Alive(grid.Data)) return false;
        Casts++;
        _probe.Hits.Clear();
        var args = new RayCastArgs(in p, dir * length);
        _probe.Ignore.Clear();
        if (physics.HasBody(grid.DEntity)) physics.GetDirectlyConnectedBodies(grid.DEntity, _probe.Ignore);
        _probe.Ignore.Add(grid.DEntity);
        args.IgnoredEntities = _probe.Ignore;   // (a copy of the buffer handle; the probe owns it - never args.Dispose)
        physics.CastRay(_probe.Hits, in args, CollisionPreset.Closest);
        if (_probe.Hits.Count == 0) return false;
        var h = _probe.Hits[0];
        distance = (float)(h.Fraction * length);
        normal = h.Normal.LengthSquared() > 0.5f ? h.Normal : -dir;
        Hits++;
        return float.IsFinite(distance);
    }

    /// <summary>The ray buffers freed (the grid left the scene).</summary>
    public void Release() => _probe.Release();
}
