#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>What a target is to this ship (ApRelations.Classify, from the game's own facts about the two owners).</summary>
public enum ApRelation : byte
{
    /// <summary>This ship itself.</summary>
    Self = 0,
    /// <summary>The same owner (your other ship; an enemy's fellow enemy - both the game's hostile NPC).</summary>
    Own = 1,
    /// <summary>Not hostile, the owner's faction (PermissionsSessionComponent.IsFriendly).</summary>
    Friendly = 2,
    /// <summary>Not hostile, not yours, not your faction's: another player, a neutral NPC, an unowned object.</summary>
    Neutral = 3,
    /// <summary>The game's relation is Hostile (what the turrets shoot at).</summary>
    Hostile = 4,
}

/// <summary>
/// THE TARGET FILTER (engine-free; the game side is Game/ApGameSense.cs). The relation is the GAME's: SE2 4.0's turrets take a
/// target when TargetingGridComponent.IsEligibleTarget says so - with an owner, IRelations.GetRelationFromOwners(ours, theirs)
/// == Hostile (RelationsSessionComponent: hostile when exactly one of the two owners is the hostile NPC); without one, only a
/// PriorityTargetComponent. Not hostile splits by the game's friendliness (PermissionsSessionComponent.IsFriendly: the same
/// owner or the same faction) into own / friendly / neutral.
/// </summary>
public static class ApRelations
{
    /// <summary>The relation from the facts the game gives: the target is this ship (or on it); it has an owner; that owner is
    /// ours; the game's relation of the two owners is Hostile; the owners share a faction; (no owner) it is a priority target.</summary>
    public static ApRelation Classify(bool self, bool hasOwner, bool sameOwner, bool gameHostile, bool sameFaction, bool priorityTarget)
    {
        if (self) return ApRelation.Self;
        if (!hasOwner) return priorityTarget ? ApRelation.Hostile : ApRelation.Neutral;
        if (sameOwner) return ApRelation.Own;
        if (gameHostile) return ApRelation.Hostile;
        return sameFaction ? ApRelation.Friendly : ApRelation.Neutral;
    }

    /// <summary>The filter lets this relation through (never this ship itself).</summary>
    public static bool Passes(ApTargetFilter f, ApRelation r) => f switch
    {
        ApTargetFilter.Everything => r != ApRelation.Self,
        ApTargetFilter.HostileNeutral => r == ApRelation.Hostile || r == ApRelation.Neutral,
        _ => r == ApRelation.Hostile,
    };

    public static string Name(ApRelation r) => r switch
    {
        ApRelation.Self => "this ship", ApRelation.Own => "your own", ApRelation.Friendly => "friendly",
        ApRelation.Neutral => "neutral", _ => "hostile",
    };

    public static string FilterName(ApTargetFilter f) => f switch
    {
        ApTargetFilter.HostileNeutral => "hostile + neutral", ApTargetFilter.Everything => "everything", _ => "hostile only",
    };

    /// <summary>"no target - Bob's Miner is neutral (filter: hostile only)": a refused target's words (built when it changes).</summary>
    public static string Refused(string name, ApRelation r, ApTargetFilter f)
        => $"{(string.IsNullOrEmpty(name) ? "the target" : name)} is {Name(r)} (filter: {FilterName(f)})";
}

/// <summary>
/// A SCAN'S CANDIDATES and the pick (engine-free: the game fills it every 2 s from the grids and characters around the ship -
/// ApGameTargets; the offline sim from its targets). Fixed capacity, no allocation after it is made. The pick is the nearest
/// one the filter lets through - never this ship, never one of the same owner's (a scan never picks your own ships: name one).
/// </summary>
public sealed class ApTargetPicker
{
    public struct Cand { public Vector3D Pos; public ApRelation Rel; public int Index; }
    readonly Cand[] _c;
    public int Count;
    public ApTargetPicker(int capacity = 256) { _c = new Cand[Math.Max(1, capacity)]; }

    public void Clear() => Count = 0;
    /// <summary>Add one (false: full - the rest are not looked at).</summary>
    public bool Add(Vector3D pos, ApRelation rel, int index)
    {
        if (Count >= _c.Length) return false;
        _c[Count++] = new Cand { Pos = pos, Rel = rel, Index = index };
        return true;
    }

    /// <summary>The nearest to 'from' within 'range' (m) that the filter takes and is not our own: its Index, or -1.</summary>
    public int Pick(Vector3D from, double range, ApTargetFilter f)
    {
        int best = -1; double bd = range * range;
        for (int i = 0; i < Count; i++)
        {
            ref var c = ref _c[i];
            if (c.Rel == ApRelation.Own || !ApRelations.Passes(f, c.Rel)) continue;
            double d = (c.Pos - from).LengthSquared();
            if (d <= bd) { bd = d; best = c.Index; }
        }
        return best;
    }
}

/// <summary>Who asks to change the Enemy toggle (or an enemy ship's panel): owns the grid, is in creative (the game's
/// GameModeSessionComponent.IsCreative: the world's or this player's), is an admin (SE2 4.0 has no admin list:
/// CoreValidators.HasAdminAccess returns true and ValidateAdminAccess passes a local - in-process - request: the host).</summary>
public struct ApEnemyWho { public bool Owner, Creative, Admin; }

/// <summary>Why an Enemy toggle or an enemy ship's panel edit was refused (None: allowed).</summary>
public enum ApEnemyRefusal : byte { None, WorldOff, NotCreativeOrAdmin, NotOwner, Locked, NoGrid, AlreadyThat, TransferFailed }

/// <summary>
/// THE ENEMY TOGGLE'S RULES (engine-free; the game side: Game/ApEnemyGame.cs). On: the block's grid is handed to the game's
/// own hostile NPC owner (OwnershipSessionComponent.TryTransferToNPC(grid, hostile: true) - what the game's encounters, NPC
/// spawners and mission triggers do), so its turrets and its Engage fight players. Off: back to the owner it had (saved with
/// the block). The world's rule (Autopilot.EnemyRule) says who may switch it on; only the grid's owner can. Once it is an enemy,
/// the game's ownership no longer stops a player at its panel (SE2 4.0 checks no ownership on a control panel - its validator
/// ValidateGamePlayer is empty - and a mod panel's setters are the mod's own), so the mod does: switching it off and every edit
/// of an enemy ship's panel are for creative or an admin only - else a player could flip an enemy back.
/// </summary>
public static class ApEnemy
{
    public static ApEnemyRefusal CanTurnOn(ApEnemyRule rule, in ApEnemyWho who, bool gridIsEnemy)
    {
        if (gridIsEnemy) return ApEnemyRefusal.AlreadyThat;
        if (rule == ApEnemyRule.Off) return ApEnemyRefusal.WorldOff;
        if (rule == ApEnemyRule.CreativeOrAdmin && !who.Creative && !who.Admin) return ApEnemyRefusal.NotCreativeOrAdmin;
        if (!who.Owner) return ApEnemyRefusal.NotOwner;
        return ApEnemyRefusal.None;
    }

    /// <summary>Off (whatever the world's rule: a host can always undo an enemy): creative or an admin.</summary>
    public static ApEnemyRefusal CanTurnOff(in ApEnemyWho who, bool gridIsEnemy)
    {
        if (!gridIsEnemy) return ApEnemyRefusal.AlreadyThat;
        if (!who.Creative && !who.Admin) return ApEnemyRefusal.Locked;
        return ApEnemyRefusal.None;
    }

    /// <summary>An edit of the panel: any on a ship that is not an enemy (the game's own rules then); an enemy's: creative or an admin.</summary>
    public static bool CanEdit(bool gridIsEnemy, in ApEnemyWho who) => !gridIsEnemy || who.Creative || who.Admin;

    /// <summary>Who gets the grid back: the owner saved when it was made an enemy if the game still knows that player, else the one
    /// switching it off (they are creative or an admin). 0: nobody (refused).</summary>
    public static long RestoreTo(long savedPrev, bool prevKnown, long requester)
        => savedPrev != 0 && prevKnown ? savedPrev : requester;

    public static string Say(ApEnemyRefusal r) => r switch
    {
        ApEnemyRefusal.WorldOff => "Enemy: not allowed in this world (Autopilot.EnemyRule)",
        ApEnemyRefusal.NotCreativeOrAdmin => "Enemy: creative or an admin only in this world",
        ApEnemyRefusal.NotOwner => "Enemy: only the ship's owner can make it an enemy",
        ApEnemyRefusal.Locked => "An enemy ship: only creative or an admin can change its autopilot",
        ApEnemyRefusal.NoGrid => "Enemy: no ship found for this block",
        ApEnemyRefusal.AlreadyThat => "",
        ApEnemyRefusal.TransferFailed => "Enemy: the game refused the ownership change",
        _ => "",
    };
}
