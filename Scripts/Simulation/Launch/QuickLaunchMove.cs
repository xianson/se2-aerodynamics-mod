#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.VRage.Core;
using Keen.VRage.Physics.Components;
using Keen.VRage.Physics.Data;
namespace AeroMod;

/// <summary>
/// QUICK LAUNCH'S OWN MOVE - only when the Orbital Mod is NOT loaded (with it, OrbitalApi.TryQuickMove moves the ship: aero
/// never teleports a grid behind its back - its frames, berths and pending moves would not know). The Orbital Mod's
/// Havok-safe rules (its Server/HavokMoves.cs, flown by its fly test since 2026-10-12), done the way the game's own fast
/// travel does it (decompiled 2.4 FastTravelServerSessionComponent.CollectEntities / Teleport):
///  - in the teleport phase - before the simulation, where fast travel sets its entities' transforms (QuickLaunch.Server.cs's
///    job); grids joined by constraints carried anywhere else in the frame make Havok's constraint migration assert;
///  - the grid with everything joined to it (IFindConnectedEntities, IncludeConstrained: wheels, rotor and hinge parts,
///    docked or locked ships), all turned rigidly about the grid and moved by the same offset; anything static in the set
///    and nothing moves (a station is never moved: the Havok "still active bodies in native world" crash, 2026-10-17);
///  - a managed world-area entity released from its area first (else the game may unload and respawn it), each grid's
///    contacts masked for a few steps (a teleport is no collision: StructureContactLoads.MaskSteps).
/// The game's fast travel itself is not usable: it only travels between FastTravelField destinations.
/// </summary>
internal static class QuickLaunchMove
{
    /// <summary>The set (server step only - one ask at a time) and the places it goes to, all worked out before any is written.</summary>
    static readonly List<Entity> _set = new();
    static readonly List<(Entity e, WorldTransform to)> _to = new();

    /// <summary>A static body (as fast travel asks it: IPhysicsMotionProvider).</summary>
    internal static bool IsStatic(Entity e)
    {
        var mp = e?.AsInterface<IPhysicsMotionProvider>();
        return mp != null && mp.Motion == BodyArgs.Motion.Static;
    }

    /// <summary>Everything that must move with the grid, the grid first (fast travel's collection: constrained, not the
    /// pilots - they ride their seats). False, set empty: joined to something static (why), or the search failed.</summary>
    internal static bool MoveSet(Session session, Entity grid, List<Entity> set, out string why)
    {
        why = null; set.Clear(); set.Add(grid);
        try
        {
            var svc = session?.Get<Keen.Game2.Simulation.GameSystems.Physicss.IFindConnectedEntities>();
            if (svc == null) { why = "no connection search"; set.Clear(); return false; }
            if (svc.IsConnectedToStaticEntity(grid)) { why = "joined to something static"; set.Clear(); return false; }
            using (var buf = new Keen.VRage.Library.Memory.Buffer<Entity>(Keen.VRage.Library.Memory.Allocator.Pool, "AeroQuickLaunch"))
            {
                var args = new Keen.Game2.Simulation.GameSystems.Physicss.IFindConnectedEntities.SearchArguments { IncludeRoot = false, IncludeConstrained = true };
                svc.FindConnectedEntities(grid, (Keen.VRage.Library.Memory.BufferReference<Entity>)buf, args);
                var en = ((Keen.VRage.Library.Memory.BufferReference<Entity>)buf).GetEnumerator();
                while (en.MoveNext()) if (en.Current != null && !set.Contains(en.Current)) set.Add(en.Current);
                en.Dispose();
            }
            foreach (var e in set)
                if (IsStatic(e)) { why = "joined to something static"; set.Clear(); return false; }
            return true;
        }
        catch (Exception e) { why = "connection search failed: " + e.GetType().Name; set.Clear(); return false; }
    }

    /// <summary>
    /// The grid (its joined set with it) to target, turned to orientation (the grid's new world orientation; the set turns
    /// rigidly about the grid), every body of the set at velocity (world, m/s) and its spin turned with it. Teleport phase,
    /// server. False: nothing moved (why). moved: the entities moved.
    /// </summary>
    internal static bool Move(Session session, Entity grid, Vector3D target, Quaternion orientation, Vector3D velocity, out int moved, out string why)
    {
        moved = 0;
        if (!PhysicsHack.Alive(grid.Data)) { why = "the ship is gone"; return false; }
        if (!Finite(target) || !Finite(velocity)) { why = "no finite place"; return false; }
        var set = _set;
        if (!MoveSet(session, grid, set, out why)) return false;
        var gwt = grid.Data.GetWorldTransform();
        var turn = Quaternion.Normalize(orientation * Quaternion.Inverse(gwt.Orientation));
        Vector3D pivot = gwt.Position;
        var to = _to; to.Clear();
        foreach (var e in set)
        {
            if (!PhysicsHack.Alive(e.Data)) continue;   // (gone since the search: skipped before its transform is read)
            var wt = e.Data.GetWorldTransform();
            Vector3D p = target + Vector3D.Transform(wt.Position - pivot, turn);
            if (!Finite(p)) { why = "no finite place for a joined part"; set.Clear(); return false; }   // (never hand Havok a NaN)
            to.Add((e, new WorldTransform(p, Quaternion.Normalize(turn * wt.Orientation))));
        }
        var areas = session.SessionComponents.TryGet<Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedWorldAreaSessionComponent>();
        foreach (var (e, wt) in to)
        {
            if (areas != null && e.Data.Has<Keen.VRage.Core.Game.GameSystems.ManagedWorldAreas.ManagedByWorldAreaTag>()) areas.TryUnmanage(e);
            e.Data.SetWorldTransform(in wt);
            ref var rb = ref e.Data.TryGetWritePtr<RigidBodyData>();
            if (!Unsafe.IsNullRef(in rb))
            {
                rb.LinearVelocity = (Vector3)velocity;
                rb.AngularVelocity = Vector3.Transform(rb.AngularVelocity, turn);
            }
            e.TryGet<AeroGridComponent>()?.MaskContacts(StructureContactLoads.MaskSteps);   // (its next contacts are the move's)
            moved++;
        }
        set.Clear(); to.Clear();
        why = null;
        return moved > 0;
    }

    static bool Finite(Vector3D v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
}
