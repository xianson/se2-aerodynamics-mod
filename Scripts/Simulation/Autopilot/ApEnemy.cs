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
public enum ApEnemyRefusal : byte { None, WorldOff, NotCreativeOrAdmin, NotOwner, Locked, NoGrid, AlreadyThat, TransferFailed, GroupNotOwned }

/// <summary>
/// THE ENEMY TOGGLE'S RULES (engine-free; the game side: Game/ApEnemyGame.cs). On: the block's SHIP - its grid and every grid
/// joined to it by a rotor, hinge or piston (the game's own "ship": CubeGridComponent.GetAllGridsInShip) - is handed to the game's
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

    /// <summary>The game's hostile NPC identity (IdentitiesSessionComponent.HostileNPCIdentity: 2 - internal; the game side asks IsHostileNPC).</summary>
    public const long HostileNpc = 2;

    public static string Say(ApEnemyRefusal r) => r switch
    {
        ApEnemyRefusal.WorldOff => "Enemy: not allowed in this world (Autopilot.EnemyRule)",
        ApEnemyRefusal.NotCreativeOrAdmin => "Enemy: creative or an admin only in this world",
        ApEnemyRefusal.NotOwner => "Enemy: only the ship's owner can make it an enemy",
        ApEnemyRefusal.Locked => "An enemy ship: only creative or an admin can change its autopilot",
        ApEnemyRefusal.NoGrid => "Enemy: no ship found for this block",
        ApEnemyRefusal.AlreadyThat => "",
        ApEnemyRefusal.TransferFailed => "Enemy: the game refused the ownership change",
        ApEnemyRefusal.GroupNotOwned => "Enemy: a grid joined to this ship (rotor, hinge, piston) belongs to someone else",
        _ => "",
    };
}

/// <summary>One grid of the ship and its owner (an identity's number), keyed by the caller (the game: the grid entity's own key).</summary>
public struct ApGridOwner { public long Grid; public long Owner; }

/// <summary>The game's ownership as the Enemy switch needs it (the game: ApEnemyOwnership over OwnershipSessionComponent; offline:
/// a mock). Identities as numbers (IdentityId's own: 1 the game's neutral NPC, 2 its hostile NPC, players above). Grids as keys.</summary>
public interface IApOwnership
{
    /// <summary>The block's grid exists and is the game's hostile NPC's.</summary>
    bool IsEnemy(out bool hasGrid);
    /// <summary>Who asks now, about the block's grid; their identity out.</summary>
    ApEnemyWho Who(out long requester);
    /// <summary>The block's grid's key.</summary>
    long BlockGrid { get; }
    /// <summary>The ship now: the block's grid and every grid joined to it (the game's "ship" connection), with their owners.</summary>
    void Ship(List<ApGridOwner> into);
    /// <summary>The game knows this player.</summary>
    bool KnownPlayer(long owner);
    /// <summary>One grid to the hostile NPC.</summary>
    bool MakeEnemy(long grid);
    /// <summary>One grid to this owner.</summary>
    bool GiveTo(long grid, long owner);
}

/// <summary>
/// THE ENEMY SWITCH'S STATE (engine-free; one per Autopilot block). The panel asks (checked at once, the refusal's words then), the
/// block's server step does it (checked again - owners may have changed in between). THE WHOLE SHIP flips: every grid joined to
/// the block's grid by the game's "ship" connection (rotors, hinges, pistons - the links the game itself requires one owner for:
/// PermissionsConfig.GridConnectionsRequiringSameOwner; a connector's dock is not one - a docked ship keeps its owner; wheels and
/// merged blocks are part of their grid). Every grid's owner before is kept (Prev, by grid) and given back to it on switch-off.
/// Refused when a grid of the ship belongs to someone else (never taken from them). While it is an enemy (each step): a grid
/// joined later is flipped too (its owner before kept); a grid that left the ship (detached, destroyed, split off and away) is
/// left as it is - still the hostile NPC's - and forgotten. Off: each grid still in the ship gets its own owner before back (a
/// player the game still knows), else the block grid's, else the one switching it off; a grid made by a split while it was an
/// enemy has no entry - the block grid's owner before. Made an enemy by someone else (an encounter): no owners before - off
/// gives the ship to the one who switches it off.
/// </summary>
public sealed class ApEnemySwitch
{
    /// <summary>0 none, 1 on asked, 2 off asked.</summary>
    public volatile int Ask;
    /// <summary>The last word ("" none): a refusal, or what was done when it was not the plain result.</summary>
    public volatile string Note = "";
    /// <summary>The block's grid is the hostile NPC's (as the last step read it).</summary>
    public volatile bool GridIsEnemy;
    /// <summary>Each grid's owner before it was made an enemy (by grid key; the save keeps it with each grid).</summary>
    public readonly Dictionary<long, long> Prev = new();
    readonly List<ApGridOwner> _ship = new();
    readonly List<long> _drop = new();
    /// <summary>Grids flipped / given back by the last switch, and joined later while an enemy (diagnostics, the tests).</summary>
    public int Flipped, Restored, JoinedLater;

    /// <summary>The ship's grids all the switcher's (or the hostile NPC's already): none taken from someone else.</summary>
    static bool AllOwned(List<ApGridOwner> ship, long owner)
    {
        foreach (var g in ship) if (g.Owner != owner && g.Owner != ApEnemy.HostileNpc) return false;
        return true;
    }

    /// <summary>The panel's switch (true: on).</summary>
    public void Request(bool on, IApOwnership o, ApEnemyRule rule)
    {
        bool enemy = o.IsEnemy(out bool hasGrid);
        if (!hasGrid) { Note = ApEnemy.Say(ApEnemyRefusal.NoGrid); return; }
        var who = o.Who(out _);
        var why = on ? ApEnemy.CanTurnOn(rule, in who, enemy) : ApEnemy.CanTurnOff(in who, enemy);
        // (the ship's other grids are checked by the step, on the simulation's thread - never walked from the terminal's)
        if (why != ApEnemyRefusal.None) { if (why != ApEnemyRefusal.AlreadyThat) Note = ApEnemy.Say(why); return; }
        Ask = on ? 1 : 2; Note = on ? "Enemy: switching..." : "Enemy off: switching...";
    }

    /// <summary>An edit of the panel by whoever asks now (refused: the words said).</summary>
    public bool MayEdit(IApOwnership o)
    {
        if (!GridIsEnemy) return true;
        bool enemy = o.IsEnemy(out _);
        var who = o.Who(out _);
        if (ApEnemy.CanEdit(enemy, in who)) return true;
        Note = ApEnemy.Say(ApEnemyRefusal.Locked);
        return false;
    }

    /// <summary>The block's server step (the asked switch done; the ship kept whole while it is an enemy; the saved state kept with
    /// the grids'). Returns true when panelEnemy / panelPrev (the block grid's owner before) or Prev changed.</summary>
    public bool Step(IApOwnership o, ApEnemyRule rule, ref bool panelEnemy, ref long panelPrev)
    {
        bool enemy = o.IsEnemy(out bool hasGrid);
        int ask = Ask; Ask = 0;
        bool wrote = false;
        long block = hasGrid ? o.BlockGrid : 0;
        if (hasGrid && ask == 1)
        {
            var who = o.Who(out long requester);
            var why = ApEnemy.CanTurnOn(rule, in who, enemy);
            _ship.Clear(); o.Ship(_ship);
            if (why == ApEnemyRefusal.None && !AllOwned(_ship, requester)) why = ApEnemyRefusal.GroupNotOwned;
            if (why != ApEnemyRefusal.None) { if (why != ApEnemyRefusal.AlreadyThat) Note = ApEnemy.Say(why); }
            else
            {
                // (the block's grid first: it decides; then the rest - a grid the game refuses stays its owner's and is said)
                Prev.Clear(); Flipped = 0; int failed = 0;
                long blockOwner = 0;
                foreach (var g in _ship) if (g.Grid == block) blockOwner = g.Owner;
                if (o.MakeEnemy(block))
                {
                    Prev[block] = blockOwner; Flipped = 1;
                    foreach (var g in _ship)
                    {
                        if (g.Grid == block || g.Owner == ApEnemy.HostileNpc) continue;
                        if (o.MakeEnemy(g.Grid)) { Prev[g.Grid] = g.Owner; Flipped++; } else failed++;
                    }
                    panelEnemy = true; panelPrev = blockOwner; wrote = true; enemy = true;
                    Note = failed > 0 ? $"Enemy: {failed} joined grid(s) could not be changed" : "";
                }
                else Note = ApEnemy.Say(ApEnemyRefusal.TransferFailed);
            }
        }
        else if (hasGrid && ask == 2)
        {
            var who = o.Who(out long requester);
            var why = ApEnemy.CanTurnOff(in who, enemy);
            if (why != ApEnemyRefusal.None) { if (why != ApEnemyRefusal.AlreadyThat) Note = ApEnemy.Say(why); }
            else
            {
                _ship.Clear(); o.Ship(_ship);
                long main = Prev.TryGetValue(block, out long pb) ? pb : panelPrev;
                long mainTo = ApEnemy.RestoreTo(main, main != 0 && o.KnownPlayer(main), requester);
                Restored = 0; int failed = 0; bool anyLost = mainTo != main;
                foreach (var g in _ship)
                {
                    if (g.Owner != ApEnemy.HostileNpc) continue;
                    long to = Prev.TryGetValue(g.Grid, out long pg) && pg != 0 && pg != ApEnemy.HostileNpc && o.KnownPlayer(pg) ? pg : mainTo;
                    if (o.GiveTo(g.Grid, to)) Restored++; else failed++;
                }
                if (o.IsEnemy(out _)) Note = ApEnemy.Say(ApEnemyRefusal.TransferFailed);
                else
                {
                    Prev.Clear(); panelEnemy = false; panelPrev = 0; wrote = true; enemy = false;
                    Note = failed > 0 ? $"Enemy off: {failed} joined grid(s) could not be given back" : anyLost ? "Enemy off: the owner before is not known - the ship is yours" : "";
                }
            }
        }
        else if (hasGrid && enemy)
        {
            // (kept whole: a grid joined later flipped too; a grid gone from the ship forgotten - left as it is)
            _ship.Clear(); o.Ship(_ship);
            foreach (var g in _ship)
                if (g.Owner != ApEnemy.HostileNpc && o.MakeEnemy(g.Grid)) { if (!Prev.ContainsKey(g.Grid)) Prev[g.Grid] = g.Owner; JoinedLater++; wrote = true; }
            _drop.Clear();
            foreach (var k in Prev.Keys) { bool there = false; foreach (var g in _ship) if (g.Grid == k) { there = true; break; } if (!there) _drop.Add(k); }
            foreach (var k in _drop) { Prev.Remove(k); wrote = true; }
        }
        // (the saved state follows the block grid's real owner)
        if (hasGrid && enemy != panelEnemy) { panelEnemy = enemy; if (!enemy) { panelPrev = 0; Prev.Clear(); } wrote = true; }
        GridIsEnemy = hasGrid && enemy;
        return wrote;
    }

    /// <summary>The panel's Enemy line.</summary>
    public string Text()
    {
        string note = Note;
        if (!string.IsNullOrEmpty(note)) return note;
        return GridIsEnemy ? "Enemy ship (the game's hostile NPC): its turrets and Engage fight players" : "Not an enemy (its owner's ship)";
    }
}
