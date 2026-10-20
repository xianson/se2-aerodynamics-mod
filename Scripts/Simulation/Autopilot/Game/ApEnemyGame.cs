#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.Game2.Simulation.GameSystems.Factions;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.Relations;
using Keen.Game2.Simulation.WorldObjects.Weapons;

namespace AeroMod;

/// <summary>
/// OWNERSHIP AND RELATIONS in game (server; SE2 4.0, all public and whitelisted - no reflection). The rules are ApEnemy's and
/// ApRelations' (engine-free); this file reads the facts and makes the one change:
///  - a grid's owner: its OwnershipComponent.Owner (a SyncProperty: it replicates to the clients, and its [Serializer] saves it
///    with the grid);
///  - hostile NPC: IdentitiesSessionComponent.IsHostileNPC(owner) (the game's two NPC identities: neutral and hostile);
///  - the ship: CubeGridComponent.GetAllGridsInShip() - the game's "ship" connection group (vanilla content: rotors, hinges,
///    pistons and their heads give it; connectors only the physical and terminal groups - a docked ship is its own; wheels and
///    merged blocks are part of their grid). It is the group the game itself demands ONE owner for when linking
///    (PermissionsConfig.GridConnectionsRequiringSameOwner = the ship group) and the one the terminal and toolbars treat as one
///    ship; each grid's owner is its own OwnershipComponent, read by that grid's own targeting and turrets - so every grid flips;
///  - the change: OwnershipSessionComponent.TryTransferToNPC(grid, hostile: true) - what the game's own encounters, NPC spawners,
///    mission triggers (SetEntityOwnershipActionComponent) and its admin debug request (RequestSetNPC) call - and back with
///    TryTransferOwnership(grid, owner). Both fire the owner's signal: the grid's TargetingGridComponent rebuilds its target list
///    (OnOwnerChanged), its turrets (AutoTargetingComponent) drop their target, other grids' targeting re-judge it
///    (OnTargetOwnerChanged), and the grid's INPCOwnershipListener blocks learn it (NPC fuel rules);
///  - the relation: IRelations.GetRelationFromOwners (TargetingGridComponent.IsEligibleTarget's), IFactions.InSameFaction
///    (PermissionsSessionComponent.IsFriendly's), PriorityTargetComponent for an unowned target;
///  - who asks: the in-process player (IdentitiesSessionComponent.LocalIdentity - a mod panel reaches only the host or the
///    single player: DetailModelPairing), creative by GameModeSessionComponent.IsCreative(identity), an admin as the game's
///    ValidateAdminAccess judges a local request (always).
/// An IdentityId cannot be made from a number by a mod (its constructor is internal): the owner saved as a number is found
/// again among the game's player identities (GetPlayerIdentityIds).
/// The transfers run on the block's server job, one at a time (a lock: the owner's signal reaches other grids' targeting).
/// </summary>
internal static class ApEnemyGame
{
    static readonly object _transfer = new();

    public static Entity GridOfBlock(Entity block) => block?.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>()?.Grid?.Entity;

    public static bool TryOwner(Entity grid, out IdentityId owner)
    {
        owner = default;
        var oc = grid?.TryGet<OwnershipComponent>();
        if (oc == null) return false;
        owner = oc.Owner;
        return true;
    }

    /// <summary>The grid is the game's hostile NPC's.</summary>
    public static bool IsEnemy(Entity grid)
    {
        if (!TryOwner(grid, out var o)) return false;
        var ids = grid.GetSession()?.TryGet<IdentitiesSessionComponent>();
        return ids != null && ids.IsHostileNPC(o);
    }

    /// <summary>Who asks now (the in-process player) about this grid.</summary>
    public static ApEnemyWho Who(Entity grid, out IdentityId requester)
    {
        requester = default;
        var w = new ApEnemyWho();
        var session = grid?.GetSession();
        var ids = session?.TryGet<IdentitiesSessionComponent>();
        if (ids == null) return w;
        requester = ids.LocalIdentity;
        w.Admin = requester.IsValid();   // (the host: ValidateAdminAccess lets a local request through; 4.0 has no admin list)
        var gm = session.SessionComponents.TryGet<GameModeSessionComponent>();
        w.Creative = gm != null && gm.IsCreative(requester);
        w.Owner = TryOwner(grid, out var o) && requester.IsValid() && o.Equals(requester);
        return w;
    }

    /// <summary>The grid to the hostile NPC; its owner before (as a number) out.</summary>
    public static bool MakeEnemy(Entity grid, out long prevOwner)
    {
        prevOwner = 0;
        var own = grid?.GetSession()?.TryGet<OwnershipSessionComponent>();
        if (own == null || !TryOwner(grid, out var before)) return false;
        prevOwner = before;
        lock (_transfer) return own.TryTransferToNPC(grid, hostile: true);
    }

    /// <summary>One grid to this owner (the game's transfer, under the lock).</summary>
    public static bool GiveTo(Entity grid, IdentityId owner)
    {
        var own = grid?.GetSession()?.TryGet<OwnershipSessionComponent>();
        if (own == null || !owner.IsValid()) return false;
        lock (_transfer) return own.TryTransferOwnership(grid, owner);
    }
}

/// <summary>The Enemy switch's view of the game (ApEnemySwitch): the block's grid and its SHIP (CubeGridComponent.GetAllGridsInShip:
/// the game's "ship" connection - rotors, hinges, pistons, the links PermissionsConfig requires one owner for), the in-process player,
/// the transfers. Grids as keys handed out here (kept: a key always means the same grid entity).</summary>
internal sealed class ApEnemyOwnership : IApOwnership
{
    public Entity Block;
    readonly Dictionary<Entity, long> _keys = new();
    readonly Dictionary<long, Entity> _grids = new();
    long _next = 1;
    Entity Grid => ApEnemyGame.GridOfBlock(Block);

    public long KeyOf(Entity grid)
    {
        if (grid == null) return 0;
        if (_keys.TryGetValue(grid, out long k)) return k;
        k = _next++; _keys[grid] = k; _grids[k] = grid;
        return k;
    }
    public Entity GridOf(long key) => _grids.TryGetValue(key, out var g) ? g : null;

    public long BlockGrid => KeyOf(Grid);
    public bool IsEnemy(out bool hasGrid) { var g = Grid; hasGrid = g != null && ApEnemyGame.TryOwner(g, out _); return hasGrid && ApEnemyGame.IsEnemy(g); }
    public ApEnemyWho Who(out long requester) { var w = ApEnemyGame.Who(Grid, out var r); requester = r; return w; }

    public void Ship(List<ApGridOwner> into)
    {
        var grid = Grid;
        if (grid == null) return;
        var ids = grid.GetSession()?.TryGet<IdentitiesSessionComponent>();
        bool self = false;
        var cg = grid.TryGet<Keen.Game2.Simulation.WorldObjects.CubeGrids.CubeGridComponent>();
        if (cg != null)
            foreach (var g in cg.GetAllGridsInShip())   // (an enumeration: once a second, never per tick)
            {
                if (g == null || !GridPhysics.Alive(g.Data) || !ApEnemyGame.TryOwner(g, out var ow)) continue;
                if (ReferenceEquals(g, grid)) self = true;
                into.Add(new ApGridOwner { Grid = KeyOf(g), Owner = ids != null && ids.IsHostileNPC(ow) ? ApEnemy.HostileNpc : (long)ow });
            }
        if (!self && ApEnemyGame.TryOwner(grid, out var own))
            into.Add(new ApGridOwner { Grid = KeyOf(grid), Owner = ids != null && ids.IsHostileNPC(own) ? ApEnemy.HostileNpc : (long)own });
    }

    public bool KnownPlayer(long owner)
    {
        var ids = Grid?.GetSession()?.TryGet<IdentitiesSessionComponent>();
        if (ids == null || owner == 0) return false;
        foreach (var id in ids.GetPlayerIdentityIds()) if ((long)id == owner) return true;
        return false;
    }

    public bool MakeEnemy(long grid) => ApEnemyGame.MakeEnemy(GridOf(grid), out _);

    public bool GiveTo(long grid, long owner)
    {
        var g = GridOf(grid);
        var ids = g?.GetSession()?.TryGet<IdentitiesSessionComponent>();
        if (g == null || ids == null) return false;
        IdentityId target = default; bool found = false;
        foreach (var id in ids.GetPlayerIdentityIds()) if ((long)id == owner) { target = id; found = true; break; }
        if (!found) return false;
        return ApEnemyGame.GiveTo(g, target);
    }
}

/// <summary>THE RELATION of a target to a grid, by the game's own rules (ApRelations.Classify; the services cached per scan).</summary>
internal sealed class ApRelationReader
{
    IRelations _rel; IFactions _fac; object _session;

    public ApRelation Of(Entity self, Entity target)
    {
        if (target == null || self == null) return ApRelation.Neutral;
        var top = target.GetTopLevelParent() ?? target;
        if (ReferenceEquals(top, self)) return ApRelation.Self;
        var session = self.GetSession();
        if (!ReferenceEquals(session, _session)) { _session = session; _rel = session?.TryGet<IRelations>(); _fac = session?.TryGet<IFactions>(); }
        var mine = self.TryGet<OwnershipComponent>();
        // (the target's owner: its own, else its top grid's - a block, a seated character)
        var theirs = target.TryGet<OwnershipComponent>() ?? top.TryGet<OwnershipComponent>();
        if (theirs == null || mine == null)
            return ApRelations.Classify(false, false, false, false, false, target.TryGet<PriorityTargetComponent>() != null || top.TryGet<PriorityTargetComponent>() != null);
        IdentityId a = mine.Owner, b = theirs.Owner;
        bool same = a.Equals(b);
        bool hostile = !same && _rel != null && _rel.GetRelationFromOwners(a, b) == RelationState.Hostile;
        bool faction = !same && !hostile && _fac != null && _fac.InSameFaction(a, b);
        return ApRelations.Classify(false, true, same, hostile, faction, false);
    }
}
