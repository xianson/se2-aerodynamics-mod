#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Library.Definitions;
namespace AeroMod;

/// <summary>
/// TORN-EDGE SMOKE (2026-10-14, the user: "a chunk that tears off trails smoke/debris from the broken joint for a few
/// seconds"). The break is the server's (StructureServer.ApplyStructureDamage: the ship's side, where the block was, out
/// of the gap; StructureTear.TryStart: the piece's side when the split follows - light or heavy - at its torn edge, away
/// from its centre of mass); the smoke is the client's. So the server PUBLISHES the event (its grid, the grid-local point
/// and way out, a stamp) and every client grid, while any event waits, checks whether it is that grid's client copy - at
/// the server grid's place (2 m + 0.1 s of its travel), turned the same way (10 deg + 0.1 s of its spin), with the same
/// local bounds (a piece split off a grid shares its frame: the bounds tell them apart). The first match takes the event
/// and plays, parented to the grid's render root at the point (it moves with the grid):
///  - the game's own damaged-block smoke (SmokeMedium, looping: the block template's OnDamagedBlockParticleEffect) - the
///    trail - and its fracture debris (DebrisBurst: OnFractureBlockParticleEffect) - a burst;
///  - both for Seconds, then stopped and disposed once their particles die (StopEmitting + DisposeAfterParticlesDie: a
///    pooled one-shot gets no timeout of the game's) - on the grid's removal from the scene too (EndAll); effects off: ended.
/// An event no client grid takes in KeepSeconds is dropped (a dedicated server: never taken - the list is capped and aged
/// on publish too). Pairing is in-process (single player and a host), as the rest of the mod's server-to-client effects.
/// ROTOR STRIKES (2026-10-17) ride the same pairing: PublishStrike (server, RotorStrikeServer) -> PlayStrike (client) - the game's
/// mesh-collision sparks (+ its voxel-hit burst on rock, the fracture debris on a break) for StrikeSeconds and a crunch at the
/// point (StructureFeel.PlayAt: the game's FMOD impacts by what was hit).
/// The particle API is VRage.Game.Client / VRage.Render (not mod references): late-bound as FlightField.Render.cs does it
/// (docs/REFLECTION_AND_WHITELIST.md).
/// </summary>
public static class TearSmoke
{
    public static bool Enabled = true;
    /// <summary>Seconds the smoke trails (and the debris flies) before it is stopped.</summary>
    public static float Seconds = 4f;
    /// <summary>The effects' emitter scale.</summary>
    public static float Scale = 1f, DebrisScale = 1f;
    /// <summary>Most smoke effects alive at once, every grid together (each torn edge: two).</summary>
    public static int MaxActive = 16;
    /// <summary>Seconds an event waits for its client grid (a split-off piece's copy comes a few frames later).</summary>
    public static float KeepSeconds = 2f;
    /// <summary>SmokeMedium (looping; the game's damaged-block smoke) and DebrisBurst (once; the game's fracture debris).</summary>
    public static Guid SmokeDef = new Guid("62038aaf-8bec-41ff-9639-32ba519f2416"), DebrisDef = new Guid("cf82bb19-55be-4fb8-b251-888f57ded7fd");

    // diagnostics (harness aeroget)
    public static string Status = "-";
    public static int Published, Spawned, Ended, Dropped, Expired, Refused;
    internal static int Active;

    sealed class Ev
    {
        public Entity Grid; public Vector3 Local, Out; public long Stamp;
        // (a ROTOR STRIKE's event - PublishStrike: Kind 1, what was hit - RotorStrike.Material -, its energy share, a break)
        public int Kind, Mat; public float Sev; public bool Broke;
        // the server grid's pose, read on the server (Publish, then the grid's own step while the event waits: ServerPose).
        // The client pairs with this, never with the server grid itself: its alive, transform, velocity and bounds read from
        // the client's job were the cross-thread read the game's DataGuard asserts on ("Concurrent access (read) ...
        // IsEntityAlive" - the Orbital Mod's StationKeepCharge, in game, 2026-10-15)
        public Vector3D Pos; public Quaternion Orient; public float Lin, Ang; public Vector3 BoxMin, BoxMax; public bool HasPose, HasBox;
    }
    static readonly object _lock = new();
    static readonly List<Ev> _events = new();
    /// <summary>Events waiting (client grids look only while some do).</summary>
    internal static volatile int Pending;

    /// <summary>(server) A torn edge on this grid: grid-local point and the way out of the break.</summary>
    internal static void Publish(Entity grid, Vector3 local, Vector3 outward)
    {
        // (no client in this process - a dedicated server: nobody takes them, and the last 16 kept their grids, 2026-10-15)
        if (!Enabled || grid == null || !AeroSettings.SawClient) return;
        var o = outward.LengthSquared() > 1e-6f ? Vector3.Normalize(outward) : Vector3.UnitY;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        var ev = new Ev { Grid = grid, Local = local, Out = o, Stamp = now };
        if (GridPhysics.Alive(grid.Data))   // (the server's own grid, on the server: its pose now)
        {
            var wt = grid.Data.GetWorldTransform();
            GridPhysics.TryGetVelocity(grid.Data, out var lin, out var ang);
            ev.HasBox = grid.Data.TryGet<BoundingBoxData>(out var sb);
            SetPose(ev, wt, lin.Length(), ang.Length(), sb.BoundingBox.Min, sb.BoundingBox.Max);
        }
        lock (_lock)
        {
            Age(now);
            if (_events.Count >= 16) { Dropped++; return; }
            _events.Add(ev);
            Pending = _events.Count; Published++;
        }
    }

    // ── ROTOR STRIKES (2026-10-17): sparks and a crunch at a strike point, through the same server -> client pairing ──
    /// <summary>The strike effects: the game's mesh-collision sparks (MeshCollision: a 360 spark burst and metal debris, once), on rock
    /// its voxel-hit burst too (VoxelMaterialHit), on a break the fracture debris (DebrisBurst).</summary>
    public static Guid SparksDef = new Guid("4d194022-4526-437d-a6cd-27ec21c84bbd"), RockHitDef = new Guid("b9249d0e-5b0c-4c67-95f5-f6cf142b83a3");
    /// <summary>Seconds a strike's effects live before they are stopped; their scale at no energy and at a full-speed strike.</summary>
    public static float StrikeSeconds = 1.5f, StrikeScaleLow = 0.6f, StrikeScaleHigh = 1.6f;
    /// <summary>The crunch (the game's FMOD impacts, by what was hit: rock, metal, a character) and the break.</summary>
    public static string[] StrikeRockSounds = { "event:/SFX/Impacts/ImpactMetalRock" }, StrikeMetalSounds = { "event:/SFX/Impacts/ImpactMetalMetal" },
        StrikeCharacterSounds = { "event:/SFX/Impacts/ImpactCharacterMetal" }, StrikeBreakSounds = { "event:/SFX/Explosions/DestroyMetalBlockSmall" };
    public static int StrikesPublished, StrikesPlayed;

    /// <summary>(server) A rotor strike on this grid: grid-local point, the hit surface's normal (grid frame), what was hit
    /// (RotorStrike.Material as int), the strike's energy share (0..1) and whether the blades broke.</summary>
    internal static void PublishStrike(Entity grid, Vector3 local, Vector3 normal, int mat, float severity, bool broke)
    {
        if (!Enabled || grid == null || !AeroSettings.SawClient) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        var ev = new Ev { Grid = grid, Local = local, Out = normal.LengthSquared() > 1e-6f ? Vector3.Normalize(normal) : Vector3.UnitY, Stamp = now, Kind = 1, Mat = mat, Sev = Math.Clamp(severity, 0f, 1f), Broke = broke };
        if (GridPhysics.Alive(grid.Data))
        {
            var wt = grid.Data.GetWorldTransform();
            GridPhysics.TryGetVelocity(grid.Data, out var lin, out var ang);
            ev.HasBox = grid.Data.TryGet<BoundingBoxData>(out var sb);
            SetPose(ev, wt, lin.Length(), ang.Length(), sb.BoundingBox.Min, sb.BoundingBox.Max);
        }
        lock (_lock)
        {
            Age(now);
            if (_events.Count >= 16) { Dropped++; return; }
            _events.Add(ev);
            Pending = _events.Count; StrikesPublished++;
        }
    }

    /// <summary>(client) A strike's sparks and crunch on its client grid.</summary>
    static void PlayStrike(AeroEntryFxComponent c, Entity grid, Ev e, long now)
    {
        long end = now + (long)(StrikeSeconds * System.Diagnostics.Stopwatch.Frequency);
        var at = new RelativeTransform(e.Local, Quaternion.CreateFromTwoVectors(Vector3.UnitZ, e.Out));
        float scale = StrikeScaleLow + (StrikeScaleHigh - StrikeScaleLow) * e.Sev;
        for (int k = 0; k < 3; k++)
        {
            Guid def = k == 0 ? SparksDef : k == 1 ? (e.Mat == 0 ? RockHitDef : Guid.Empty) : (e.Broke ? DebrisDef : Guid.Empty);
            if (def == Guid.Empty) continue;
            if (System.Threading.Volatile.Read(ref Active) >= MaxActive) { Refused++; Status = $"{MaxActive} effects already"; break; }
            var h = Spawn(grid, def, at, scale, out string why);
            if (h == null) { Refused++; Status = "strike spawn: " + why; continue; }
            (c.TornFx ??= new List<(object h, long end)>()).Add((h, end));
            System.Threading.Interlocked.Increment(ref Active); Spawned++;
        }
        var gw = grid.Data.GetWorldTransform();
        var world = new WorldTransform(WorldTransform.Transform((Vector3D)e.Local, in gw), gw.Orientation);
        StructureFeel.PlayAt(e.Mat == 0 ? StrikeRockSounds : e.Mat == 1 ? StrikeMetalSounds : StrikeCharacterSounds, world);
        if (e.Broke) StructureFeel.PlayAt(StrikeBreakSounds, world);
        StrikesPlayed++;
        Status = $"{grid.DebugName}: rotor strike at {e.Local} ({(e.Mat == 0 ? "rock" : e.Mat == 1 ? "metal" : "character")}{(e.Broke ? ", broke" : "")})";
    }

    static void SetPose(Ev e, in WorldTransform wt, float lin, float ang, Vector3 min, Vector3 max)
    {
        e.Pos = wt.Position; e.Orient = wt.Orientation; e.Lin = lin; e.Ang = ang; e.BoxMin = min; e.BoxMax = max; e.HasPose = true;
    }

    /// <summary>(server, a grid's own step, only while events wait) the pose of this grid's waiting events brought up to now:
    /// the client pairs with it as it did with the live grid (a split-off piece's copy comes a few frames later).</summary>
    internal static void ServerPose(Entity grid, in WorldTransform wt, Vector3 lin, Vector3 ang)
    {
        if (Pending == 0 || grid == null) return;
        bool mine = false;
        lock (_lock) foreach (var e in _events) if (ReferenceEquals(e.Grid, grid)) { mine = true; break; }
        if (!mine) return;
        bool box = grid.Data.TryGet<BoundingBoxData>(out var sb);
        lock (_lock)
            foreach (var e in _events)
                if (ReferenceEquals(e.Grid, grid)) { e.HasBox = box; SetPose(e, wt, lin.Length(), ang.Length(), sb.BoundingBox.Min, sb.BoundingBox.Max); }
    }

    /// <summary>(_lock) events older than KeepSeconds dropped.</summary>
    static void Age(long now)
    {
        long keep = (long)(KeepSeconds * System.Diagnostics.Stopwatch.Frequency);
        for (int i = _events.Count - 1; i >= 0; i--) if (now - _events[i].Stamp > keep || _events[i].Grid == null) { _events.RemoveAt(i); Expired++; }
    }

    /// <summary>(client, every frame per grid: AeroEntryFxComponent.EntryJob) this grid's smoke aged out; while events
    /// wait, the ones for this grid taken and played.</summary>
    internal static void Tick(AeroEntryFxComponent c)
    {
        var fx = c.TornFx;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool on = Enabled && AeroEntryFx.AllEffects;
        if (fx != null && fx.Count > 0)
            for (int i = fx.Count - 1; i >= 0; i--) if (!on || now >= fx[i].end) { End(fx[i].h); fx.RemoveAt(i); }
        if (Pending == 0 || !on) return;
        var grid = c.Entity;
        if (grid == null || !GridPhysics.Alive(c.Data)) return;
        List<Ev> mine = null;
        lock (_lock)
        {
            Age(now);
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                var e = _events[i];
                if (!Pairs(e, grid)) continue;
                (mine ??= new List<Ev>()).Add(e); _events.RemoveAt(i);
            }
            Pending = _events.Count;
        }
        if (mine == null) return;
        long end = now + (long)(Seconds * System.Diagnostics.Stopwatch.Frequency);
        foreach (var e in mine)
        {
            if (e.Kind == 1) { PlayStrike(c, grid, e, now); continue; }
            // (UnitZ out of the break: the mod's effects emit along their Z)
            var at = new RelativeTransform(e.Local, Quaternion.CreateFromTwoVectors(Vector3.UnitZ, e.Out));
            foreach (var (def, scale) in new[] { (SmokeDef, Scale), (DebrisDef, DebrisScale) })
            {
                if (System.Threading.Volatile.Read(ref Active) >= MaxActive) { Refused++; Status = $"{MaxActive} smokes already"; break; }
                var h = Spawn(grid, def, at, scale, out string why);
                if (h == null) { Refused++; Status = "spawn: " + why; continue; }
                (c.TornFx ??= new List<(object h, long end)>()).Add((h, end));
                System.Threading.Interlocked.Increment(ref Active); Spawned++;
                Status = $"{grid.DebugName}: smoke at {e.Local} out {e.Out}";
            }
        }
    }

    /// <summary>(client) The grid leaves the scene: its smoke ended.</summary>
    internal static void EndAll(AeroEntryFxComponent c)
    {
        var fx = c.TornFx; if (fx == null) return;
        foreach (var f in fx) End(f.h);
        fx.Clear();
    }

    /// <summary>The client grid that is this event's server grid's copy: at its place, turned its way, with its local bounds -
    /// the server's pose as the event carries it (ServerPose); the client grid read here, on its own job.</summary>
    static bool Pairs(Ev e, Entity client)
    {
        if (e.Grid == null || client == null || ReferenceEquals(e.Grid, client) || !e.HasPose) return false;
        var cw = client.Data.GetWorldTransform();
        if ((e.Pos - cw.Position).Length() > 2.0 + 0.1 * e.Lin) return false;
        var d = Quaternion.Normalize(Quaternion.Inverse(e.Orient) * cw.Orientation);
        double turn = 2.0 * Math.Acos(Math.Min(1.0, Math.Abs((double)d.W)));
        if (turn > 10.0 * Math.PI / 180.0 + 0.1 * e.Ang) return false;
        if (!e.HasBox || !client.Data.TryGet<BoundingBoxData>(out var cb)) return false;
        return (e.BoxMin - cb.BoundingBox.Min).LengthSquared() + (e.BoxMax - cb.BoundingBox.Max).LengthSquared() < 0.5f;
    }

    // ── the particle API, late-bound (VRage.Game.Client / VRage.Render are not mod references), resolved once ──
    const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static Type _tSession, _tParams; static MethodInfo _spawn; static FieldInfo _scaleField; static bool _resolved;
    static MethodInfo _stop, _disposeAfter, _dispose; static PropertyInfo _isDisposed;

    /// <summary>An effect under the grid's render root at a grid-local transform (it follows the grid); null and why on failure.</summary>
    static object Spawn(Entity grid, Guid effect, RelativeTransform at, float scale, out string why)
    {
        why = null;
        try
        {
            if (!_resolved)
            {
                _resolved = true;
                _tSession = EntryFxBridge.FindType("VRage.Game.Client", "Keen.VRage.Game.Client.Effects.Components.ParticleEffectsSessionComponent");
                _tParams = EntryFxBridge.FindType("VRage.Render", "Keen.VRage.Render.Data.ParticleEffectUserParameters");
                if (_tSession != null)
                    foreach (var m in _tSession.GetMethods(AnyInst))
                        if (m.Name == "TrySpawnEffect") { var ps = m.GetParameters(); if (ps.Length > 3 && ps[0].ParameterType == typeof(RelativeTransform)) { _spawn = m; break; } }
                _scaleField = _tParams?.GetField("EmitterScaleMultiplier");
                if (_spawn == null || _tParams == null) Log.Default?.Info("[AERO-TEAR] torn-edge smoke off: the particle API was not found");
            }
            if (_spawn == null || _tParams == null) { why = "particle API not found"; return null; }
            var parent = EntryFxBridge.RenderParent(grid);
            if (parent == null) { why = "no render parent"; return null; }
            object session = null;
            var comps = grid.GetSession()?.SessionComponents;
            if (comps != null) foreach (var sc in comps.Components) if (sc != null && _tSession.IsInstanceOfType(sc)) { session = sc; break; }
            if (session == null) { why = "no particle session"; return null; }
            if (!DefinitionManager.Instance.TryGetDefinition(effect, out Definition def) || def == null) { why = "effect not loaded: " + effect; return null; }
            object prm = Activator.CreateInstance(_tParams);   // (a boxed struct, its defaults: the set sticks)
            _scaleField?.SetValue(prm, Math.Max(0.05f, scale));
            var p = _spawn.GetParameters();
            var args = new object[p.Length];
            for (int i = 0; i < args.Length; i++) args[i] = p[i].HasDefaultValue ? p[i].DefaultValue : null;
            args[0] = at; args[1] = def; args[2] = prm; args[3] = parent;
            for (int i = 4; i < args.Length; i++) if (p[i].ParameterType == typeof(string)) args[i] = "AeroTornEdge";
            var h = _spawn.Invoke(session, args);
            if (h == null) why = "spawn refused (budget or distance)";
            return h;
        }
        catch (Exception e) { why = (e.InnerException ?? e).Message; return null; }
    }

    /// <summary>Stopped, then disposed once its particles have died (Dispose(bool) as the fallback); a handle the game
    /// already disposed is left be.</summary>
    static void End(object h)
    {
        if (h == null) return;
        System.Threading.Interlocked.Decrement(ref Active); Ended++;
        try
        {
            var t = h.GetType();
            _isDisposed ??= t.GetProperty("IsDisposed", AnyInst);
            if (_isDisposed?.GetValue(h) is bool gone && gone) return;
            _stop ??= t.GetMethod("StopEmitting", AnyInst, null, new[] { typeof(bool) }, null);
            _disposeAfter ??= t.GetMethod("DisposeAfterParticlesDie", AnyInst, null, Type.EmptyTypes, null);
            if (_stop != null && _disposeAfter != null) { _stop.Invoke(h, new object[] { false }); _disposeAfter.Invoke(h, null); return; }
            _dispose ??= t.GetMethod("Dispose", AnyInst, null, new[] { typeof(bool) }, null);
            _dispose?.Invoke(h, new object[] { true });
        }
        catch (Exception) { }
    }
}

public partial class AeroEntryFxComponent
{
    /// <summary>This grid's torn-edge smoke: each effect and when it ends (TearSmoke; client).</summary>
    internal List<(object h, long end)> TornFx;
}
