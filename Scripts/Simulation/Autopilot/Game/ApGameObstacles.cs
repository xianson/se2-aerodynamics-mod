#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.GameSystems.Queries;
using Keen.VRage.Physics;

namespace AeroMod;

/// <summary>
/// THE OBSTACLES AROUND A SHIP in game (server; ApObstacles for the collision avoidance, ApAvoid). Every 10th tick of a flying
/// autopilot: the physics' broadphase over a box around the ship (IPhysics.QueryAABB, CollisionPreset.Bodies - what the game's own
/// AI movement asks for its obstacles, AIMovementComponent), reaching the ship's size plus the look-ahead at its speed (at most
/// 3 km), into its own buffer (native, grown once, reused: no allocation per query). Each body found: its world bounds' centre
/// and half diagonal (a sphere - "just a tad": no shape), its top entity's velocity. Not counted: this ship and what is joined to
/// it, the target (the goal source adds it with the behaviour's own clearance), anything over 2 km across (a planet: the terrain
/// watch's). An asteroid is its voxel body's bounds (2 km at most); a moving grid its velocity - the closest approach is relative.
/// </summary>
internal sealed class ApGameObstacles
{
    public readonly ApObstacles Set = new();
    Keen.VRage.Library.Memory.Buffer<Keen.VRage.DCS.Accessors.DEntity> _hits, _joined;
    int _in;
    public const int Every = 10;
    public const double MaxReach = 3000, MaxRadius = 1000;

    public void Update(AeroGridComponent aero, in ApSense s, Entity targetTop)
    {
        if (--_in > 0) return;
        _in = Every;
        Set.Clear();
        var physics = GridPhysics.Physics;
        var grid = aero.Entity;
        if (physics == null || grid == null || !GridPhysics.Alive(grid.Data)) return;
        double reach = Math.Min(MaxReach, s.Radius + s.Vel.Length() * Math.Max(1f, s.Lim.AvoidS) + 100.0);
        var box = new BoundingBoxD(s.Pos - new Vector3D(reach), s.Pos + new Vector3D(reach));
        var args = new AABBQueryArgs(box);
        _hits.Clear(); _joined.Clear();
        if (physics.HasBody(grid.DEntity)) physics.GetDirectlyConnectedBodies(grid.DEntity, _joined);
        foreach (var pad in aero.PadBodies) _joined.Add(pad);   // (its magnetic gears' pads: joined to its wheels, not to it)
        physics.QueryAABB(_hits, in args, CollisionPreset.Bodies);   // (args own no buffer: never args.Dispose)
        var scene = grid.Scene;
        for (int i = 0; i < _hits.Count; i++)
        {
            var de = _hits[i];
            if (de == grid.DEntity || Joined(de)) continue;
            var e = Entity.TryGetFromDataEntity((Scene: scene, Entity: de));
            if (e == null || !GridPhysics.Alive(e.Data)) continue;
            var top = e.GetTopLevelParent() ?? e;
            if (ReferenceEquals(top, grid) || (targetTop != null && ReferenceEquals(top, targetTop))) continue;
            if (top.TryGet<AeroGridComponent>() is { GearPad: true }) continue;   // (any magnetic gear's pad: part of its own ship)
            var bb = BoundingBoxData.TryGetWorldAABB(e.Data);
            if (!bb.HasValue) continue;
            double r = (bb.Value.Max - bb.Value.Min).Length() * 0.5;
            if (!(r > 0.1) || r > MaxRadius) continue;
            GridPhysics.TryGetVelocity(top.Data, out Vector3 v, out _);
            if (!Set.Add(bb.Value.Center, v, (float)r)) break;
        }
    }

    bool Joined(Keen.VRage.DCS.Accessors.DEntity de)
    {
        for (int i = 0; i < _joined.Count; i++) if (_joined[i] == de) return true;
        return false;
    }

    /// <summary>The buffers freed (the grid left the scene).</summary>
    public void Release() { _hits.Dispose(); _joined.Dispose(); }
}
