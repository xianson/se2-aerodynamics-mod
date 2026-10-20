using System;
using System.Collections.Generic;

#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// THE WORLD'S SETTINGS CARRIERS (2026-10-19, planetless worlds): which saved entities hold the mod's world settings, which
/// one's text is applied on load, and when the world is over. No engine here (a session is an opaque object): the offline
/// tests (Tests/WorldCarrierTests) run it with mock sessions.
///
/// WHY: the world settings were saved only on the SERVER PLANETS (a component with the engine's
/// EntityNameSessionComponentObjectBuilder, injected into the planet prefabs). A world with no planet (space only, the
/// game's Empty World) saved nothing, and the world-reset hooks run by "the last planet left" never ran there. A planetless
/// save holds only the players' BODIES (EmptyWorld/savegame.vrb: one spectator and one character, both bundle roots), so the
/// bodies carry the text too: the server character and spectator compositions get a carrier (a fixed component id and the
/// same builder). Every carrier writes the same text at a save.
///
/// PRECEDENCE (one text applied per world; never two sources fighting):
///  1. a BODY carrier's text (the new carrier) wins over a PLANET's (the old one, still read: saves made before this);
///  2. among carriers of one kind, the first non-empty one read wins (they all wrote the same text);
///  3. a body's text read after a planet's replaces it only while no world setting was changed since the load (`edited`):
///     a body loaded later in the session never undoes the player's change;
///  4. the host's overrides file wins over both (the caller's apply re-reads it: AeroSettings / OrbitalSettings).
///
/// THE WORLD'S END, one reset per world (the caller's `reset`: world settings to defaults, then the per-world hooks):
///  - the last carrier of the world's session leaves the scene while that session unloads (Session.IsUnloading) - a body
///    removed mid-session (a death, a respawn, a player leaving) is not the world's end even when it was the last carrier;
///  - or a carrier shows a session other than the world's (that world's unload was not seen): reset first, then the new
///    world's texts are applied.
/// A text read with no session known yet (a carrier's [Init] before its entity knows its scene) waits for that carrier's
/// OnAddedToScene, so a reset for a new session can never wipe the text it just read.
/// SHARED: written once in the Aerodynamics Mod's tools/shared/modsrc/files/ (this path under it), copied into both mods by
/// tools/shared/modsrc/sync.sh; edit it THERE.
/// </summary>
public sealed class WorldCarriers
{
    public enum Kind { Planet = 1, Body = 2 }

    readonly object _lock = new object();
    readonly Action<string, Kind> _apply;
    readonly Action<string, bool> _reset;
    readonly Func<bool> _edited;
    object _session;
    int _count;
    Kind _source;   // (0: nothing applied in this world)
    readonly List<(string text, Kind kind)> _pending = new List<(string, Kind)>();

    /// <summary>Diagnostics: the carriers in the world's scene, what was applied (none / planet / body), the resets so far
    /// and the last one's reason.</summary>
    public int Count { get { lock (_lock) return _count; } }
    public string Source { get { lock (_lock) return _source == 0 ? "none" : _source.ToString().ToLowerInvariant(); } }
    public int Resets;
    public string LastReset = "none";

    /// <param name="apply">The world's settings from a saved text, and the kind of carrier it came from (the caller resets its
    /// world scope first; a planet's text may hold an older format's extra lines).</param>
    /// <param name="reset">The world is over (why, unloading: true at its unload, false when a new session showed first):
    /// world settings to defaults and the per-world hooks.</param>
    /// <param name="edited">A world setting changed since the load (the store's unsaved flag).</param>
    public WorldCarriers(Action<string, Kind> apply, Action<string, bool> reset, Func<bool> edited)
    { _apply = apply; _reset = reset; _edited = edited; }

    /// <summary>A carrier's [Init] read its saved text (empty: none). `session`: its session, null if not known yet.</summary>
    public void Read(string text, Kind kind, object session)
    {
        if (string.IsNullOrEmpty(text)) return;
        Action after;
        lock (_lock)
        {
            after = session != null ? See(session) : null;
            _pending.Add((text, kind));
            if (session != null) after += Process();
        }
        after?.Invoke();
    }

    /// <summary>A carrier joined its session's scene.</summary>
    public void Added(object session)
    {
        if (session == null) return;
        Action after;
        lock (_lock)
        {
            after = See(session);
            _count++;
            after += Process();
        }
        after?.Invoke();
    }

    /// <summary>A carrier is leaving its session's scene; `unloading`: that session is unloading (the world closes).</summary>
    public void Removed(object session, bool unloading)
    {
        Action after = null;
        lock (_lock)
        {
            if (session == null || !ReferenceEquals(session, _session)) return;   // (an older world's: its reset is done)
            if (_count > 0) _count--;
            if (_count == 0 && unloading)
            {
                _session = null; _source = 0; _pending.Clear();   // (nothing of the next world is read yet)
                after = Reset("the world unloaded", true);
            }
        }
        after?.Invoke();
    }

    /// <summary>The world's session is `session`: another one than the world's - its unload unseen - resets first.</summary>
    Action See(object session)
    {
        if (ReferenceEquals(session, _session)) return null;
        bool other = _session != null;
        _session = session; _count = 0; _source = 0;
        // (the texts waiting are this new world's: kept - an older world's were applied at its carriers' OnAddedToScene)
        return other ? Reset("another world's carrier (its unload not seen)", false) : null;
    }

    Action Reset(string why, bool unloading)
    {
        Resets++; LastReset = why;
        return () => _reset(why, unloading);
    }

    /// <summary>The texts waiting, by the precedence above (under the lock; the applies run after it).</summary>
    Action Process()
    {
        Action after = null;
        foreach (var (text, kind) in _pending)
        {
            bool take = _source == 0 || (kind == Kind.Body && _source == Kind.Planet && !_edited());
            if (!take) continue;
            _source = kind;
            string t = text; Kind k = kind;
            after += () => _apply(t, k);
        }
        _pending.Clear();
        return after;
    }
}
