#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Threading;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks.Pilotable;
using Keen.VRage.DCS.Annotations;
namespace AeroMod;

/// <summary>
/// QUICK LAUNCH, the server's step: the teleport phase - before the simulation, where the game's fast travel moves ships
/// (FastTravelUpdate.Teleport) - on the server planets' settings carrier (AeroWorldSettingsComponent, one job per planet: the
/// first to see an ask takes it). Nothing runs while nobody asked (a reference read). The same job ticks the atmosphere
/// point query's generator list (AtmosphereProbe: with no grid in the world, AeroApi.TryGetAtmosphereAt answered false).
/// </summary>
public partial class AeroWorldSettingsComponent
{
    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    private class OnQuickLaunch : JobGroup;

    [OnQuickLaunch]
    [MustHave(typeof(AeroWorldSettingsComponent))]
    private static void QuickLaunchJob(AeroWorldSettingsComponent c)
    {
        if (c.Data.Scene?.DebugName == "Client") return;   // (the carrier is the server planets'; never answer from a client copy)
        AtmosphereProbe.Tick(c.Entity);   // (the API's point query with no grid in the world: a timestamp compare; ~10 s its list)
        EnemySpawn.ServerTick(c);   // (test spawns - Launch/EnemySpawn.Server.cs: three reads while there is nothing to do)
        if (!QuickLaunch.Asked) return;
        QuickLaunch.ServerStep(c);
    }

    /// <summary>The server planets (their centres: each carrier's entity is its planet), for quick launch's "up".</summary>
    internal static readonly List<AeroWorldSettingsComponent> Planets = new();
}

public static partial class QuickLaunch
{
    static readonly List<Entity> _chars = new();

    /// <summary>The server's answer to an ask (a planet's teleport-phase job). All from the server's own state: its character
    /// of the asking player, the seat that character pilots, that seat's grid.</summary>
    internal static void ServerStep(AeroWorldSettingsComponent carrier)
    {
        var ask = TakeAsk();
        if (ask == null) return;
        // (taken once, and only while fresh - 2026-10-18: an ask no carrier job took in time is dropped, never served later
        //  to whatever ship the player sits in by then)
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!QuickLaunchRules.Fresh(ask.At, now, (long)(System.Diagnostics.Stopwatch.Frequency * QuickLaunchRules.AskMaxAgeS)))
        {
            Log.Default?.Info($"[AERO] quicklaunch: an ask {(now - ask.At) / (double)System.Diagnostics.Stopwatch.Frequency:F1} s old dropped (good for {QuickLaunchRules.AskMaxAgeS:F0} s)");
            Status = "dropped: the ask waited too long";
            return;
        }
        var session = carrier?.Entity?.GetSession();
        if (session == null) return;
        try { Serve(session, ask); }
        catch (Exception e)   // (one ask, never per tick)
        {
            Log.Default?.Info($"[AERO] quicklaunch: failed: {e.GetType().Name}: {e.Message}");
            Answer("Quick launch: failed (" + e.GetType().Name + ")", false);
        }
    }

    static void Serve(Session session, Ask ask)
    {
        // who: the asking player's character here (owned by its identity; the only one if that is not known)
        var ch = Pilot(session, ask);
        bool creative = false; Entity seat = null, grid = null;
        if (ch != null)
        {
            creative = session.SessionComponents.TryGet<Keen.Game2.Simulation.GameSystems.Player.GameModeSessionComponent>()?.IsCreative(ch) ?? false;
            foreach (var se in session.GetEntitiesOfType<SeatComponent>())
                if ((se.TryGet<SeatComponent>() as ISeat)?.Pilot == ch) { seat = se; break; }
            grid = seat?.TryGet<CubeBlockComponent>()?.Grid?.Entity;
        }
        bool alive = grid != null && GridPhysics.Alive(grid.Data) && GridPhysics.Alive(seat.Data);
        bool isStatic = alive && QuickLaunchMove.IsStatic(grid);
        bool joined = alive && !isStatic && session.Get<Keen.Game2.Simulation.GameSystems.Physicss.IFindConnectedEntities>()?.IsConnectedToStaticEntity(grid) == true;
        // where: the nearest server planet whose gravity it is in
        Vector3D pos = default, centre = default;
        bool atPlanet = false;
        if (alive)
        {
            pos = grid.Data.GetWorldTransform().Position;
            atPlanet = GridPhysics.GetGravityDirection(grid.Data).LengthSquared() > 0.01f && NearestPlanet(grid.Data.Scene, pos, out centre);
        }
        var v = QuickLaunchRules.Check(Creative, ch != null, creative, seat != null, alive, isStatic, joined, atPlanet);
        if (v != QuickLaunchRules.Verdict.Go) { Refuse(v, null, grid); return; }

        // how high: over the aero ground ray (the server copy's last tick; -1: no ground within its 2 km), never lower
        double rNow = (pos - centre).Length();
        var aero = grid.TryGet<AeroGridComponent>();
        float gh = aero != null && aero.IsServerScene ? aero.GroundHeight : -1f;
        double rGround = gh >= 0f && float.IsFinite(gh) ? rNow - gh : double.NaN;
        // (its reach about its own origin - the turn is about it: the box's centre's offset plus its half diagonal)
        double shipR = grid.Data.TryGet<BoundingBoxData>(out var bb) ? bb.BoundingBox.Center.Length() + bb.BoundingBox.HalfExtents.Length() : 0;
        v = QuickLaunchRules.Lift(rNow, rGround, AltitudeM, shipR, out double r, out double maxDrop);
        if (v != QuickLaunchRules.Verdict.Go) { Refuse(v, null, grid); return; }

        // level on the cockpit's heading (the server's seat), flying along its nose
        var gq = grid.Data.GetWorldTransform().Orientation;
        var sq = seat.Data.GetWorldTransform().Orientation;
        Vector3D noseW = Vector3D.Transform(-Vector3D.UnitZ, sq), topW = Vector3D.Transform(Vector3D.UnitY, sq);
        Vector3D up = Vector3D.Normalize(pos - centre);
        var orient = QuickLaunchRules.Level(gq, noseW, topW, up, out Vector3D heading);
        Vector3 noseL = (Vector3)Vector3D.Transform(noseW, Quaternion.Inverse(gq));
        float cap = StructureContactLoads.PhysicsSpeedCap;
        double speed = QuickLaunchRules.Speed(SpeedMps, AeroApi.GetTopSpeedAt(grid, noseL, QuickLaunchRules.CruiseThrottle),
                                              QuickLaunchRules.FallbackSpeed, cap > 0f ? cap * QuickLaunchRules.CapShare : 0);
        Vector3D target = centre + up * r, vel = heading * speed;

        // the move: the Orbital Mod's when it is loaded (its safe path and its space border), else aero's own
        Vector3D placed = target; string reason; string by;
        int o = OrbitalLink.TryQuickMove(grid, target, orient, vel, maxDrop, out var oPlaced, out reason);
        bool ok;
        if (o >= 0) { ok = o > 0; by = "orbital"; if (ok) placed = oPlaced; }
        else
        {
            by = "aero";
            ok = QuickLaunchMove.Move(session, grid, target, orient, vel, out int moved, out reason);
            if (ok) { NoteTurn(Quaternion.Normalize(orient * Quaternion.Inverse(gq))); reason = moved + " grid(s) and parts"; }
        }
        if (!ok) { Refuse(QuickLaunchRules.Verdict.Refused, reason, grid); return; }
        string where = QuickLaunchRules.Where((placed - centre).Length(), rNow, rGround);
        Log.Default?.Info($"[AERO] quicklaunch: '{grid.DebugName}' {where}, level, {speed:F0} m/s (moved by {by}: {reason})");
        Interlocked.Increment(ref Launches);
        Answer($"{where}, {speed:F0} m/s", true);
    }

    static void Refuse(QuickLaunchRules.Verdict v, string reason, Entity grid)
    {
        string say = v == QuickLaunchRules.Verdict.Refused && !string.IsNullOrEmpty(reason) ? "Quick launch: " + reason : QuickLaunchRules.Say(v);
        Log.Default?.Info($"[AERO] quicklaunch: '{grid?.DebugName}' not launched: {v}{(reason != null ? " (" + reason + ")" : "")}");
        Interlocked.Increment(ref Refusals);
        Answer(say, false);
    }

    static void Answer(string say, bool ok) { PendingSay = say; Status = (ok ? "launched: " : "refused: ") + say; }

    /// <summary>The asking player's character on this server: the alive character its identity owns; not known (or no owner
    /// match), the only character if there is just one (single player) - never a guess between several.</summary>
    static Entity Pilot(Session session, Ask ask)
    {
        var list = _chars; list.Clear();
        Entity found = null;
        if (session.TryFillAliveCharacters(list))
        {
            if (ask.HasWho)
                foreach (var c in list)
                {
                    var own = c.TryGet<Keen.Game2.Simulation.GameSystems.Ownership.OwnershipComponent>();
                    if (own != null && own.Owner.Equals(ask.Who)) { found = c; break; }
                }
            if (found == null && list.Count == 1) found = list[0];
        }
        list.Clear();
        return found;
    }

    /// <summary>The nearest server planet's centre (false: none in this world).</summary>
    internal static bool NearestPlanet(object scene, Vector3D at, out Vector3D centre)
    {
        centre = default; double best = double.MaxValue; bool any = false;
        lock (AeroWorldSettingsComponent.Planets)
            foreach (var p in AeroWorldSettingsComponent.Planets)
            {
                var e = p.Entity;
                if (e == null || !ReferenceEquals(e.Data.Scene, scene) || !GridPhysics.Alive(e.Data)) continue;   // (this scene's only)
                var c = e.Data.GetWorldTransform().Position;
                double d = (c - at).LengthSquared();
                if (d < best) { best = d; centre = c; any = true; }
            }
        return any;
    }
}
