#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.Game2.Simulation.GameSystems.GPS;
using Keen.Game2.Simulation.GameSystems.Identities;
using Keen.Game2.Simulation.GameSystems.Ownership;
using Keen.Game2.Simulation.GameSystems.Player;
using Keen.Game2.Simulation.GameSystems.ProjectileSystems;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Weapons;
using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.Weapons;
using Keen.Game2.Simulation.WorldObjects.Weapons;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// THE FLIGHT PILOT'S TARGET in game (server; IApTargetSource, asked once a tick by ApPilot), from the Autopilot block's source:
///  TARGETING - the grid's own TargetingGridComponent (SE2 4.0, server only, public: no reflection), the same system the turrets
///   use: what the pilot LOCKED from the cockpit, else the first PRIORITY target, else the NEAREST POTENTIAL target (the list only
///   fills while the grid has a turret - AutoTargetingComponent: a ship with fixed guns alone needs a lock). So a player's drone
///   follows what is locked and an NPC ship with turrets engages what vanilla targeting sees.
///  GPS - a GPS marker of the grid owner's (by name) or coordinates (ApRoute.TryCoords), looked up every 2 s.
///  GRID - a grid by its name, looked up every 2 s.
///  API - another mod's (AeroApi.SetAutopilotTarget / SetAutopilotTargetPoint).
/// THE FILTER (the block's Target Filter: hostile only / + neutral / everything; ApRelations - the game's own relation, the
/// turrets' rule) on every source: the targeting lists are hostile already (and a lock: the game locks only an eligible
/// target); when they are empty - a ship with no turret has none - or the filter takes more, a scan every 2 s finds the nearest
/// grid or player character within the game's targeting reach (ScanRange) the filter takes; a named grid and the API's entity
/// are judged every 2 s (refused: "no target - ... is neutral" on the status line). A GPS or API POINT has no owner: always taken.
/// The target's place is its bounds' centre, its velocity its top grid's (GridPhysics), its radius half its bounds' diagonal.
/// No allocation per tick: names are read when the target changes, lookups every 2 s.
/// </summary>
internal sealed class ApGameTargets : IApTargetSource
{
    readonly AeroGridComponent _aero;
    public ApTargetSrc Src; public string Text = ""; public ApTargetFilter Filter;
    // (the API's: an entity or a point)
    public volatile Entity ApiEntity; public bool ApiPoint; public Vector3D ApiPos; public Vector3 ApiVel;
    Entity _found, _named; string _name = ""; long _nextLook; string _lookFor; Vector3D _gps; bool _gpsOk;
    // (the filter: the relation of the named / the API's / the scan's target, judged every 2 s - never per tick)
    readonly ApRelationReader _rel = new();
    Entity _judged; ApRelation _judgedRel; long _nextJudge; ApTargetFilter _judgedFilter;
    // (the scan - the targeting source's fallback when the game's lists are empty, or the filter takes more than hostile)
    readonly ApTargetPicker _pick = new(); readonly List<Entity> _scanList = new(); readonly List<Entity> _chars = new();
    Entity _scanned; long _nextScan; ApTargetFilter _scanFilter;
    /// <summary>The words of a target the filter refused ("Bob's Miner is neutral (filter: hostile only)"; null none) - the status line.</summary>
    public volatile string Refused;
    /// <summary>How far the scan looks (m): the game's own targeting reach (TargetingGridDefinition.DetectionRange's default).</summary>
    public const double ScanRange = 4000.0;
    /// <summary>A grid of fewer blocks is debris to the scan (every grid has an owner in 4.0 - a loose block too).</summary>
    public const int ScanMinBlocks = 5;
    public ApGameTargets(AeroGridComponent aero) { _aero = aero; }

    public bool Get(in ApSense s, out ApTargetFix f)
    {
        f = default;
        var grid = _aero.Entity;
        if (grid == null) return false;
        Entity e = null;
        switch (Src)
        {
            case ApTargetSrc.Targeting:
            {
                // (the game's own lists are hostile by the turrets' rule - TargetingGridComponent.IsEligibleTarget - and so is a
                //  cockpit lock: every filter takes them. The scan adds what they lack: a ship with no turret has no list, and a
                //  wider filter takes more than hostile)
                var tg = grid.TryGet<TargetingGridComponent>();
                if (tg != null)
                {
                    e = tg.LockedTarget;
                    if (e == null) { var pri = tg.PriorityTargets; if (pri.Count > 0) e = pri[0]; }
                    if (e == null && Filter == ApTargetFilter.Hostile) { var pot = tg.PotentialTargets; if (pot.Count > 0) e = pot[0]; }
                }
                if (e == null) e = Scan(grid, s.Pos);
                if (e != null) Refused = null;
                break;
            }
            case ApTargetSrc.Grid: e = Judge(grid, LookGrid(grid)); break;
            case ApTargetSrc.Gps:
                Look(grid);
                if (!_gpsOk) return false;
                f.Valid = true; f.Pos = _gps; f.Name = _lookFor; f.Id = 1; f.Radius = 5f;
                return true;
            case ApTargetSrc.Api:
                if (ApiPoint) { f.Valid = true; f.Pos = ApiPos; f.Vel = ApiVel; f.Name = "API point"; f.Id = 2; f.Radius = 5f; return true; }
                e = Judge(grid, ApiEntity); break;
        }
        return Fix(e, grid, ref f);
    }

    /// <summary>The target if the filter takes it (its relation judged when it changes and every 2 s), else null - the refusal said.</summary>
    Entity Judge(Entity self, Entity e)
    {
        if (e == null) { Refused = null; return null; }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!ReferenceEquals(e, _judged) || now >= _nextJudge || Filter != _judgedFilter)
        {
            _judged = e; _judgedFilter = Filter; _nextJudge = now + 2 * System.Diagnostics.Stopwatch.Frequency;
            _judgedRel = _rel.Of(self, e);
            Refused = ApRelations.Passes(Filter, _judgedRel) ? null : ApRelations.Refused(NameOf(e.GetTopLevelParent() ?? e), _judgedRel, Filter);
        }
        return Refused == null ? e : null;
    }

    /// <summary>The nearest grid (of ScanMinBlocks or more) or player character within ScanRange the filter takes - never this ship
    /// or the same owner's (ApTargetPicker). Every 2 s (an enumeration of the session's grids: allocates, never per tick).</summary>
    Entity Scan(Entity self, Vector3D at)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextScan && Filter == _scanFilter && (_scanned == null || GridPhysics.Alive(_scanned.Data))) return _scanned;
        _nextScan = now + 2 * System.Diagnostics.Stopwatch.Frequency; _scanFilter = Filter; _scanned = null;
        var session = self.GetSession();
        if (session == null) return null;
        _pick.Clear(); _scanList.Clear();
        foreach (var ge in session.GetEntitiesOfType<CubeGridComponent>())
        {
            if (ge == null || ReferenceEquals(ge, self) || !GridPhysics.Alive(ge.Data)) continue;
            var cg = ge.TryGet<CubeGridComponent>();
            if (cg == null || cg.CubeBlockStorage.GetAllCubeBlocks().Length < ScanMinBlocks) continue;
            var pos = ge.Data.GetWorldTransform().Position;
            if ((pos - at).LengthSquared() > ScanRange * ScanRange * 1.5) continue;
            if (!_pick.Add(pos, _rel.Of(self, ge), _scanList.Count)) break;
            _scanList.Add(ge);
        }
        _chars.Clear();
        if (session.TryFillAliveCharacters(_chars))
            foreach (var c in _chars)
            {
                if (c == null || ReferenceEquals(c.GetTopLevelParent() ?? c, self)) continue;   // (not our own pilot)
                if (!_pick.Add(c.Data.GetWorldTransform().Position, _rel.Of(self, c), _scanList.Count)) break;
                _scanList.Add(c);
            }
        _chars.Clear();
        int k = _pick.Pick(at, ScanRange, Filter);
        _scanned = k >= 0 ? _scanList[k] : null;
        _scanList.Clear();
        return _scanned;
    }

    bool Fix(Entity e, Entity self, ref ApTargetFix f)
    {
        if (e == null || !GridPhysics.Alive(e.Data)) return false;
        var top = e.GetTopLevelParent() ?? e;
        if (ReferenceEquals(top, self)) return false;   // (never itself)
        var bb = BoundingBoxData.TryGetWorldAABB(e.Data);
        f.Pos = bb.HasValue ? bb.Value.Center : e.Data.GetWorldTransform().Position;
        f.Radius = bb.HasValue ? (float)(bb.Value.Max - bb.Value.Min).Length() * 0.5f : 5f;
        GridPhysics.TryGetVelocity(top.Data, out f.Vel, out _);
        if (!ReferenceEquals(e, _found)) { _found = e; _name = NameOf(top); }
        f.Name = _name; f.Id = e.GetHashCode(); f.Valid = true;
        return true;
    }

    /// <summary>A grid's name (its display name; "" unreadable). Read only when the target changes.</summary>
    static string NameOf(Entity top)
    {
        var cg = top?.TryGet<CubeGridComponent>();
        if (cg == null) return "";
        try { return cg.DisplayName.EvaluateText() ?? ""; } catch { return ""; }
    }

    Entity LookGrid(Entity self)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextLook && string.Equals(_lookFor, Text, StringComparison.Ordinal)) return _named;
        _nextLook = now + 2 * System.Diagnostics.Stopwatch.Frequency; _lookFor = Text; _named = null;
        if (string.IsNullOrWhiteSpace(Text)) return null;
        var session = self.GetSession();
        if (session == null) return null;
        string want = Text.Trim();
        foreach (var ge in session.GetEntitiesOfType<CubeGridComponent>())
        {
            if (ge == null || ReferenceEquals(ge, self)) continue;
            if (string.Equals(NameOf(ge), want, StringComparison.OrdinalIgnoreCase)) { _named = ge; break; }
        }
        return _named;
    }

    void Look(Entity self)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextLook && string.Equals(_lookFor, Text, StringComparison.Ordinal)) return;
        _nextLook = now + 2 * System.Diagnostics.Stopwatch.Frequency; _lookFor = Text;
        if (ApRoute.TryCoords(Text ?? "", out _, out _gps)) { _gpsOk = true; return; }
        _gpsOk = ApGps.Find(self, Text, out _gps);
    }
}

/// <summary>
/// GPS MARKERS (server): a name looked up in the grid owner's GPS list (SE2 4.0: IPerPlayerData's GPSMarkersData per identity,
/// whitelisted), then every player's - for a grid with no owner. Allocates (an enumerator): asked every few seconds, never per tick.
/// </summary>
internal static class ApGps
{
    public static bool Find(Entity grid, string name, out Vector3D pos)
    {
        pos = default;
        if (grid == null || string.IsNullOrWhiteSpace(name)) return false;
        var session = grid.GetSession();
        var ppd = session?.TryGet<IPerPlayerData>();
        if (ppd == null) return false;
        string want = name.Trim();
        var own = grid.TryGet<OwnershipComponent>();
        if (own != null && In(ppd, own.Owner, want, out pos)) return true;
        var ids = session.TryGet<IdentitiesSessionComponent>();
        if (ids == null) return false;
        foreach (var id in ids.GetPlayerIdentityIds()) if (In(ppd, id, want, out pos)) return true;
        return false;
    }

    static bool In(IPerPlayerData ppd, IdentityId id, string want, out Vector3D pos)
    {
        pos = default;
        GPSMarkersData data;
        try { data = ppd.GetPerPlayerData<GPSMarkersData>(id); } catch { return false; }   // (an identity with no data yet; seconds apart, never per tick)
        // (GPSMarkers is an ObservableList - its base type lives in an assembly mod scripts do not reference: read as an IEnumerable)
        object list = data?.GPSMarkers;
        if (list is not IEnumerable<GPSMarker> markers) return false;
        foreach (var m in markers) if (m != null && string.Equals(m.Name?.Trim(), want, StringComparison.OrdinalIgnoreCase)) { pos = m.Position; return true; }
        return false;
    }
}

/// <summary>
/// THE GRID'S FIXED GUNS (server, every few seconds): every weapon block (ProjectileWeaponComponent) that is not a turret (no
/// AutoTargetingComponent) - its firing axis (its world transform x GetProjectileTransform's forward, as the game fires it) in the
/// grid's frame, averaged; its loaded projectile's muzzle speed (SimulatedProjectileDefinition.Velocity) and range (the least).
/// The nose without guns: the Autopilot block's own front. No allocation (the game's block span).
/// </summary>
internal sealed class ApGunScan
{
    public Vector3 Axis, Nose; public float Muzzle, Range; public int Guns;
    long _next;

    public void Update(AeroGridComponent aero, in WorldTransform wt, AeroAutopilotComponent block)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _next) return;
        _next = now + 3 * System.Diagnostics.Stopwatch.Frequency;
        var cg = aero.Entity?.TryGet<CubeGridComponent>();
        Vector3 sum = Vector3.Zero; float muzzle = 0f, range = float.MaxValue; int n = 0;
        if (cg != null)
        {
            var blocks = cg.CubeBlockStorage.GetAllCubeBlocks();
            for (int i = 0; i < blocks.Length; i++)
            {
                var b = blocks[i];
                var be = b?.Entity;
                if (be == null) continue;
                var w = be.TryGet<ProjectileWeaponComponent>();
                if (w == null || be.TryGet<AutoTargetingComponent>() != null) continue;
                var shot = be.Data.GetWorldTransform() * w.GetProjectileTransform();
                Vector3 dirW = Vector3.Transform(Vector3.Forward, shot.Orientation);
                sum += WorldTransform.TransformDirectionInv(dirW, wt); n++;
                if (w.LoadedProjectile is SimulatedProjectileDefinition sp && sp.Velocity > muzzle) muzzle = sp.Velocity;
                if (w.LoadedProjectile != null && w.LoadedProjectile.Range > 0f) range = MathF.Min(range, w.LoadedProjectile.Range);
            }
        }
        Guns = n;
        Axis = n > 0 && sum.LengthSquared() > 1e-4f ? Vector3.Normalize(sum) : Vector3.Zero;
        Muzzle = muzzle; Range = range < float.MaxValue ? range : 0f;
        var bk = block?.Entity;
        Nose = bk != null ? Vector3.Normalize(WorldTransform.TransformDirectionInv(Vector3.Transform(Vector3.Forward, bk.Data.GetWorldTransform().Orientation), wt)) : Vector3.Zero;

    }
}
