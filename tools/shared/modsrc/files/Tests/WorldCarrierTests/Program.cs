using System.Collections.Generic;
using __MOD__;

// SHARED by both mods' Tests/WorldCarrierTests: written once in the Aerodynamics Mod's tools/shared/modsrc/files/, copied by
// tools/shared/modsrc/sync.sh (only the 'using' of the mod's namespace differs; edit it there).
namespace WorldCarrierTests
{
    /// <summary>
    /// Offline checks of the world settings' carriers (Scripts/Simulation/Save/WorldCarriers.cs) with mock sessions: a
    /// planetless world's bodies carry the text through a save and a load; a body's text wins over a planet's (old saves
    /// still load); a body removed mid-session is not the world's end, the last carrier at the unload is (once); a new
    /// session with its unload unseen resets before its own text and never wipes it; a text read before its session is
    /// known waits for the carrier's scene. Exit 0 = all pass.
    /// </summary>
    internal static class Program
    {
        static int _passed, _failed;
        static void Ok(string name, bool ok, string detail = "")
        {
            if (ok) _passed++;
            else { _failed++; System.Console.WriteLine("FAIL  " + name + (detail.Length > 0 ? ": " + detail : "")); }
        }

        /// <summary>A mod's world settings in miniature: the applied text, an edit flag, the resets seen.</summary>
        sealed class Mock
        {
            public string World = "";   // (what the mod's world settings hold, as its saved text)
            public bool Edited;
            public readonly List<string> Log = new List<string>();
            public readonly WorldCarriers C;
            public Mock()
            {
                C = new WorldCarriers(
                    apply: (t, kind) => { World = t; Edited = false; Log.Add("apply " + t); },
                    reset: (why, unloading) => { World = ""; Edited = false; Log.Add(unloading ? "reset unload" : "reset unseen"); },
                    edited: () => Edited);
            }
            /// <summary>The player changes a world setting.</summary>
            public void Edit(string t) { World = t; Edited = true; }
            /// <summary>What every carrier writes at a save (the mod's text; empty: nothing off its defaults).</summary>
            public string Save() { Edited = false; return World; }
            public int Count(string what) { int n = 0; foreach (var l in Log) if (l == what) n++; return n; }
        }

        static int Main()
        {
            // ── 1. a planetless world: the bodies carry it (save, unload, load) ──
            {
                var m = new Mock();
                var s1 = new object();
                m.C.Read("", WorldCarriers.Kind.Body, s1);   // (a new world: the bodies' builders are the prefab's, empty)
                m.C.Added(s1); m.C.Added(s1);   // (the spectator and the character)
                Ok("planetless: nothing to apply in a new world", m.World == "" && m.Log.Count == 0, string.Join(",", m.Log));
                m.Edit("Aero.Drag = 2");
                string saved = m.Save();
                m.C.Removed(s1, unloading: true); m.C.Removed(s1, unloading: true);
                Ok("planetless: one reset at the unload (the last carrier)", m.Count("reset unload") == 1 && m.World == "", string.Join(",", m.Log));
                var s2 = new object();
                m.C.Read(saved, WorldCarriers.Kind.Body, s2); m.C.Read(saved, WorldCarriers.Kind.Body, s2);
                m.C.Added(s2); m.C.Added(s2);
                Ok("planetless: the save's text back after the reload", m.World == "Aero.Drag = 2", m.World);
                Ok("planetless: applied once (two bodies, one text)", m.Count("apply Aero.Drag = 2") == 1, string.Join(",", m.Log));
                Ok("planetless: no reset at the reload (the unload was seen)", m.Count("reset unseen") == 0, string.Join(",", m.Log));
                Ok("planetless: the source is a body", m.C.Source == "body", m.C.Source);
            }
            // ── 2. a body leaving mid-session is not the world's end ──
            {
                var m = new Mock(); var s = new object();
                m.C.Read("x = 1", WorldCarriers.Kind.Body, s);
                m.C.Added(s);
                m.C.Removed(s, unloading: false);   // (the only body: a death, a respawn coming)
                Ok("mid-session: the last body gone is no reset", m.Count("reset unload") == 0 && m.World == "x = 1", string.Join(",", m.Log));
                m.C.Added(s);   // (the respawned character: its builder is the prefab's - no text)
                Ok("mid-session: the respawn keeps the world's settings", m.World == "x = 1" && m.C.Count == 1, m.World);
                m.C.Removed(s, unloading: true);
                Ok("mid-session: the unload still resets", m.Count("reset unload") == 1, string.Join(",", m.Log));
            }
            // ── 3. precedence: an old save (planets only), a new one (both), the player's change ──
            {
                var m = new Mock(); var s = new object();
                m.C.Read("old = 1", WorldCarriers.Kind.Planet, s); m.C.Read("old = 1", WorldCarriers.Kind.Planet, s);
                m.C.Read("", WorldCarriers.Kind.Body, s);
                m.C.Added(s); m.C.Added(s); m.C.Added(s);
                Ok("old save: the planets' text loads", m.World == "old = 1" && m.C.Source == "planet", m.World);
            }
            {
                var m = new Mock(); var s = new object();
                m.C.Read("planet = 1", WorldCarriers.Kind.Planet, s);
                m.C.Read("body = 2", WorldCarriers.Kind.Body, s);
                m.C.Added(s); m.C.Added(s);
                Ok("planet then body: the body wins", m.World == "body = 2" && m.C.Source == "body", m.World);
            }
            {
                var m = new Mock(); var s = new object();
                m.C.Read("body = 2", WorldCarriers.Kind.Body, s);
                m.C.Read("planet = 1", WorldCarriers.Kind.Planet, s);
                m.C.Added(s); m.C.Added(s);
                Ok("body then planet: the body stays", m.World == "body = 2" && m.Count("apply planet = 1") == 0, string.Join(",", m.Log));
            }
            {
                var m = new Mock(); var s = new object();
                m.C.Read("planet = 1", WorldCarriers.Kind.Planet, s); m.C.Added(s);
                m.Edit("mine = 3");
                m.C.Read("late = 4", WorldCarriers.Kind.Body, s); m.C.Added(s);   // (a body loaded later in the session)
                Ok("a later body never undoes the player's change", m.World == "mine = 3", m.World);
            }
            // ── 4. a new world whose unload was not seen ──
            {
                var m = new Mock(); var a = new object(); var b = new object();
                m.C.Read("a = 1", WorldCarriers.Kind.Body, a); m.C.Added(a);
                m.C.Read("b = 2", WorldCarriers.Kind.Body, b);   // (world B's body, its session known at [Init])
                Ok("unseen unload: reset, then the new world's text", m.World == "b = 2" && m.Log.IndexOf("reset unseen") >= 0 && m.Log.IndexOf("reset unseen") < m.Log.IndexOf("apply b = 2"), string.Join(",", m.Log));
                m.C.Added(b);
                Ok("unseen unload: its carrier's scene does not reset again", m.Count("reset unseen") == 1 && m.World == "b = 2", string.Join(",", m.Log));
                m.C.Removed(a, unloading: true);   // (world A's carrier, late)
                Ok("an older world's carrier leaving later is ignored", m.Count("reset unload") == 0 && m.World == "b = 2", string.Join(",", m.Log));
            }
            {
                var m = new Mock(); var a = new object(); var b = new object();
                m.C.Read("a = 1", WorldCarriers.Kind.Body, a); m.C.Added(a);
                m.C.Read("b = 2", WorldCarriers.Kind.Body, null);   // (world B's [Init] before its scene is known)
                Ok("no session at [Init]: the text waits", m.World == "a = 1", m.World);
                m.C.Added(b);
                Ok("no session at [Init]: reset first, then applied at the scene (never wiped)", m.World == "b = 2" && m.Log.IndexOf("reset unseen") < m.Log.IndexOf("apply b = 2"), string.Join(",", m.Log));
            }
            // ── 5. a planet world: the planets and bodies together, one reset at the unload ──
            {
                var m = new Mock(); var s = new object();
                m.C.Read("w = 1", WorldCarriers.Kind.Planet, s); m.C.Read("w = 1", WorldCarriers.Kind.Body, s);
                m.C.Added(s); m.C.Added(s);
                m.C.Removed(s, unloading: true);
                Ok("planet world: no reset before the last carrier", m.Count("reset unload") == 0, string.Join(",", m.Log));
                m.C.Removed(s, unloading: true);
                Ok("planet world: one reset at the last", m.Count("reset unload") == 1 && m.C.Resets == 1, string.Join(",", m.Log));
            }

            System.Console.WriteLine(_failed == 0 ? $"WorldCarrierTests: {_passed}/{_passed} passed" : $"WorldCarrierTests: {_failed} of {_passed + _failed} FAILED");
            return _failed == 0 ? 0 : 1;
        }
    }
}
