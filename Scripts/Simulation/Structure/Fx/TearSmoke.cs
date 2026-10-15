#pragma warning disable
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

    sealed class Ev { public Entity Grid; public Vector3 Local, Out; public long Stamp; }
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
        lock (_lock)
        {
            Age(now);
            if (_events.Count >= 16) { Dropped++; return; }
            _events.Add(new Ev { Grid = grid, Local = local, Out = o, Stamp = now });
            Pending = _events.Count; Published++;
        }
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
        if (grid == null || !PhysicsHack.Alive(c.Data)) return;
        List<Ev> mine = null;
        lock (_lock)
        {
            Age(now);
            for (int i = _events.Count - 1; i >= 0; i--)
            {
                var e = _events[i];
                if (!Pairs(e.Grid, grid)) continue;
                (mine ??= new List<Ev>()).Add(e); _events.RemoveAt(i);
            }
            Pending = _events.Count;
        }
        if (mine == null) return;
        long end = now + (long)(Seconds * System.Diagnostics.Stopwatch.Frequency);
        foreach (var e in mine)
        {
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

    /// <summary>The client grid that is this server grid's copy: at its place, turned its way, with its local bounds.</summary>
    static bool Pairs(Entity server, Entity client)
    {
        if (server == null || client == null || ReferenceEquals(server, client) || !PhysicsHack.Alive(server.Data)) return false;
        var sw = server.Data.GetWorldTransform(); var cw = client.Data.GetWorldTransform();
        PhysicsHack.TryGetVelocity(server.Data, out var lin, out var ang);
        if ((sw.Position - cw.Position).Length() > 2.0 + 0.1 * lin.Length()) return false;
        var d = Quaternion.Normalize(Quaternion.Inverse(sw.Orientation) * cw.Orientation);
        double turn = 2.0 * Math.Acos(Math.Min(1.0, Math.Abs((double)d.W)));
        if (turn > 10.0 * Math.PI / 180.0 + 0.1 * ang.Length()) return false;
        if (!server.Data.TryGet<BoundingBoxData>(out var sb) || !client.Data.TryGet<BoundingBoxData>(out var cb)) return false;
        return (sb.BoundingBox.Min - cb.BoundingBox.Min).LengthSquared() + (sb.BoundingBox.Max - cb.BoundingBox.Max).LengthSquared() < 0.5f;
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
