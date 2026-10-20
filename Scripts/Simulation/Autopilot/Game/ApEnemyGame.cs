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

    /// <summary>The grid back to the saved owner (a player the game still knows), else to the requester; who got it out.</summary>
    public static bool Restore(Entity grid, long savedPrev, IdentityId requester, out long to)
    {
        to = 0;
        var session = grid?.GetSession();
        var own = session?.TryGet<OwnershipSessionComponent>();
        var ids = session?.TryGet<IdentitiesSessionComponent>();
        if (own == null || ids == null) return false;
        IdentityId target = requester; bool known = false;
        if (savedPrev != 0)
            foreach (var id in ids.GetPlayerIdentityIds())   // (a few players; once per switch, never per tick)
                if ((long)id == savedPrev) { target = id; known = true; break; }
        long pick = ApEnemy.RestoreTo(savedPrev, known, requester);
        if (pick == 0 || !target.IsValid()) return false;
        to = target;
        lock (_transfer) return own.TryTransferOwnership(grid, target);
    }
}

/// <summary>The Enemy switch's view of the game (ApEnemySwitch): the block's grid, the in-process player, the transfers.</summary>
internal sealed class ApEnemyOwnership : IApOwnership
{
    public Entity Block;
    Entity Grid => ApEnemyGame.GridOfBlock(Block);
    public bool IsEnemy(out bool hasGrid) { var g = Grid; hasGrid = g != null && ApEnemyGame.TryOwner(g, out _); return hasGrid && ApEnemyGame.IsEnemy(g); }
    public ApEnemyWho Who(out long requester) { var w = ApEnemyGame.Who(Grid, out var r); requester = r; return w; }
    public bool MakeEnemy(out long prevOwner) => ApEnemyGame.MakeEnemy(Grid, out prevOwner);
    public bool Restore(long savedPrev, long requester, out long to)
    {
        to = 0;
        ApEnemyGame.Who(Grid, out var r);   // (the requester as an IdentityId: the in-process player)
        return (long)r == requester && ApEnemyGame.Restore(Grid, savedPrev, r, out to);
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
