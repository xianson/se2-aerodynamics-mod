#pragma warning disable
using System.IO;
using System.Text.RegularExpressions;
using AeroMod;

/// <summary>The magnetic gear's game-free core (Gear/MagPad.cs): the pad grid's name, its life (spawn / search / join / lost /
/// repaired), the magnet's gates (no self-lock, never stowed, P and auto-lock, the brake following the magnet), the retraction
/// veto, which body collides, and the stiffness cap with the hidden wheel's mass.</summary>
static class MagPadTests
{
    static int _checks, _fails;
    public static (int checks, int fails) Run()
    {
        Names(); Life(); Gates(); Collision(); Stiffness();
        return (_checks, _fails);
    }
    static void Check(bool ok, string what) { _checks++; if (!ok) { _fails++; if (_fails <= 20) Console.WriteLine("FAIL mag: " + what); } }

    static void Names()
    {
        var rnd = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            ulong u = MagPadNames.NewUid(rnd);
            Check(u != 0, "a fresh id is 0");
            string n = MagPadNames.Name(u);
            Check(MagPadNames.TryParse(n, out ulong back) && back == u, $"name round trip {n}");
        }
        Check(MagPadNames.Name(1) == "Gear Pad [0000000000000001]", "name shape " + MagPadNames.Name(1));
        foreach (var bad in new[] { null, "", "Gear Pad []", "Gear Pad [0000000000000000]", "Gear Pad [zz00000000000001]", "Gear Pad [00000000000000011]", "My Ship", "gear pad [0000000000000001]", "Gear Pad [0000000000000001" })
            Check(!MagPadNames.TryParse(bad, out _), $"'{bad}' read as a pad");
    }

    static PadWorld W(bool wheel = true, bool func = true, bool down = true, bool room = true) => new PadWorld { WheelReady = wheel, Functional = func, Down = down, Room = room };

    static void Life()
    {
        double t = 0;
        // built: waits for the wheel, then the gear down and room; spawns once with the id given; joins; stays joined while alive
        {
            var l = new MagPadLife();
            Check(l.Step(t, W(wheel: false), 11) == PadDo.Nothing && l.Phase == PadPhase.Start, "spawned before the wheel");
            Check(l.Step(t, W(down: false), 11) == PadDo.Nothing && l.Phase == PadPhase.NeedSpawn, "spawned with the gear up: " + l.Phase);
            Check(l.Step(t, W(room: false), 11) == PadDo.Nothing, "spawned with no room");
            Check(l.Step(t, W(func: false), 11) == PadDo.Nothing, "spawned on a broken block");
            Check(l.Step(t, W(), 11) == PadDo.Spawn && l.Uid == 11 && l.Phase == PadPhase.Spawning, "no spawn when ready");
            Check(l.Step(t + 1, W(), 12) == PadDo.Nothing && l.Uid == 11, "spawned twice / id changed while spawning");
            var a = W(); a.Arrived = true;
            Check(l.Step(t + 2, a, 13) == PadDo.Join && l.Phase == PadPhase.Joining, "no join on arrival");
            Check(l.Step(t + 2.1, W(), 13) == PadDo.Join, "gave up the join before its time");
            var j = W(); j.Join = 1;
            Check(l.Step(t + 2.2, j, 13) == PadDo.Nothing && l.Phase == PadPhase.Joined && !l.Lost, "not joined");
            var alive = W(); alive.PadAlive = true;
            for (int i = 0; i < 100; i++) Check(l.Step(t + 3 + i, alive, 14) == PadDo.Nothing && l.Phase == PadPhase.Joined, "a live pad left");
            // destroyed: released, lost (saved), never respawned - not while the gear stays functional
            Check(l.Step(t + 200, W(), 15) == PadDo.Release && l.Lost && l.Phase == PadPhase.Lost, "a dead pad not released");
            for (int i = 0; i < 50; i++) Check(l.Step(t + 201 + i, W(), 16) == PadDo.Nothing && l.Phase == PadPhase.Lost, "a lost pad respawned for free");
            // the gear block broken, then repaired: a new pad
            Check(l.Step(t + 300, W(func: false), 17) == PadDo.Nothing, "lost: broken");
            Check(l.Step(t + 301, W(), 17) == PadDo.Nothing && l.Phase == PadPhase.NeedSpawn && !l.Lost, "repair did not rebuild: " + l.Phase);
            Check(l.Step(t + 302, W(), 18) == PadDo.Spawn && l.Uid == 18, "rebuilt without a new id");
        }
        // loaded with an id: searches, joins the one found; not found in time: a new id, spawns
        {
            var l = new MagPadLife(); l.Restore(42, false);
            Check(l.Step(0, W(), 99) == PadDo.Search && l.Phase == PadPhase.Searching && l.Uid == 42, "a loaded gear did not search");
            var f = W(); f.Found = true;
            Check(l.Step(1, f, 99) == PadDo.Join && l.Uid == 42, "found, not joined (or id changed)");
            var l2 = new MagPadLife(); l2.Restore(42, false);
            l2.Step(0, W(), 99);
            Check(l2.Step(MagPadLife.SearchSeconds - 0.1, W(), 99) == PadDo.Search, "stopped searching early");
            Check(l2.Step(MagPadLife.SearchSeconds + 0.1, W(), 99) == PadDo.Spawn && l2.Uid == 99, "not found: no new pad with a new id (a pasted copy would share the old one)");
            // not found and the gear up (a ship saved in flight whose pad was removed): waits, spawns once it is down
            var l3 = new MagPadLife(); l3.Restore(42, false);
            l3.Step(0, W(down: false), 99);
            Check(l3.Step(10, W(down: false), 99) == PadDo.Nothing && l3.Phase == PadPhase.NeedSpawn, "spawned up");
            Check(l3.Step(11, W(), 98) == PadDo.Spawn, "never spawned once down");
            // loaded lost: stays lost
            var l4 = new MagPadLife(); l4.Restore(42, true);
            Check(l4.Step(0, W(), 99) == PadDo.Nothing && l4.Phase == PadPhase.Lost, "a lost pad came back on load");
        }
        // a pad that comes in too far off: removed and respawned, MaxTries at most, then lost
        {
            var l = new MagPadLife();
            int respawns = 0, giveups = 0;
            for (int k = 0; k < 10 && l.Phase != PadPhase.Lost; k++)
            {
                var d = l.Step(k * 10, W(), (ulong)(100 + k));
                if (d != PadDo.Spawn) { Check(false, "no spawn on try " + k + ": " + d + " " + l.Phase); break; }
                var a = W(); a.Arrived = true; l.Step(k * 10 + 1, a, 0);
                var bad = W(); bad.Join = 2;
                var r = l.Step(k * 10 + 2, bad, 0);
                if (r == PadDo.Respawn) respawns++; else if (r == PadDo.GiveUp) giveups++;
            }
            Check(respawns == MagPadLife.MaxTries - 1 && giveups == 1 && l.Lost, $"bad joins: {respawns} respawns {giveups} give-ups, lost {l.Lost}");
        }
        // a spawn that never arrives: retried (no pad to remove), then lost
        {
            var l = new MagPadLife();
            l.Step(0, W(), 1);
            Check(l.Step(MagPadLife.SpawnTimeoutSeconds + 1, W(), 2) == PadDo.Nothing && l.Phase == PadPhase.NeedSpawn && l.Tries == 1, "a lost spawn not retried: " + l.Phase);
            var fail = W(); fail.SpawnFailed = true;
            l.Step(100, W(), 3);
            Check(l.Step(101, fail, 4) == PadDo.Nothing && l.Tries == 2, "a failed spawn not counted");
            l.Step(200, W(), 5);
            Check(l.Step(201, fail, 6) == PadDo.GiveUp && l.Lost, "never gave up");
        }
    }

    static MagSeen S(bool locked = false, bool lockedForeign = true, bool ready = false, bool readyForeign = true, bool down = true, bool auto = true,
                     bool parked = false, bool cooldown = true, bool working = true, bool joined = true)
        => new MagSeen { Joined = joined, Down = down, Locked = locked, LockedForeign = lockedForeign, Ready = ready, ReadyForeign = readyForeign,
                         CooldownDone = cooldown, Working = working, AutoLock = auto, Parked = parked };

    static void Gates()
    {
        // never onto its own ship: a ready self, auto-lock on or P - no lock; a lock onto itself undone at once
        {
            var g = new MagGates();
            Check(g.Decide(S(ready: true, readyForeign: false), out _) == MagAct.None, "auto-locked onto its own ship");
            Check(g.Decide(S(ready: true, readyForeign: false, parked: true), out _) == MagAct.None, "P locked onto its own ship");
            Check(g.Decide(S(locked: true, lockedForeign: false), out _) == MagAct.Unlock && g.SelfLockVetoes == 1, "a self-lock was kept");
        }
        // never stowed / moving / without a pad / on a broken pad
        foreach (var s in new[] { S(ready: true, down: false), S(ready: true, joined: false), S(ready: true, working: false), S(ready: true, down: false, parked: true) })
            Check(new MagGates().Decide(s, out _) == MagAct.None, $"locked when it must not (down {s.Down} joined {s.Joined} working {s.Working})");
        Check(new MagGates().Decide(S(locked: true, down: false), out _) == MagAct.Unlock, "kept a lock with the gear not down");
        // auto-lock: locks when ready and the cooldown is over; not during the cooldown; auto off: waits for P
        Check(new MagGates().Decide(S(ready: true), out _) == MagAct.Lock, "auto-lock did not lock");
        Check(new MagGates().Decide(S(ready: true, cooldown: false), out _) == MagAct.None, "auto-lock ignored the cooldown");
        Check(new MagGates().Decide(S(ready: true, auto: false), out _) == MagAct.None, "locked with auto-lock off and no P");
        Check(new MagGates().Decide(S(ready: true, auto: false, parked: true), out _) == MagAct.Lock, "P did not lock");
        Check(new MagGates().Decide(S(ready: true, auto: false, parked: true, cooldown: false), out _) == MagAct.Lock, "P waited for the cooldown (the vanilla P does not)");
        // P released while locked: unlock
        {
            var g = new MagGates();
            g.Decide(S(locked: true, parked: true), out _);
            Check(g.Decide(S(locked: true, parked: false), out bool flip) == MagAct.Unlock && !flip, "P off did not unlock");
        }
        // the brake follows an auto-lock - once - and that flip is not taken for the player's press
        {
            var g = new MagGates();
            Check(g.Decide(S(locked: true, parked: false), out bool f1) == MagAct.None && f1, "auto-lock: the brake not flipped on");
            Check(g.Decide(S(locked: true, parked: false), out bool f2) == MagAct.None && !f2, "flipped twice before the first took");
            Check(g.Decide(S(locked: true, parked: true), out bool f3) == MagAct.None && !f3, "our own flip unlocked it");
            Check(g.Decide(S(locked: true, parked: false), out bool f4) == MagAct.Unlock && !f4, "the player's P after an auto-lock did not unlock");
        }
        // an unlock that is not the player's (the gear went up) turns the brake off too
        {
            var g = new MagGates();
            g.Decide(S(locked: true, parked: true), out _);
            Check(g.Decide(S(locked: true, parked: true, down: false), out bool f) == MagAct.Unlock && f, "the brake left on after a forced unlock");
            Check(g.Decide(S(parked: false, down: false), out _) == MagAct.None, "our flip read as a press");
        }
        Check(MagGates.RetractRefusal(true) != null && MagGates.RetractRefusal(false) == null, "retract veto");
    }

    static void Collision()
    {
        const float off = 1.6f;
        Check(!MagPadCollision.Wheel(true, 0f, off) && MagPadCollision.Pad(true, 0f, off), "joined, down: the pad takes the ground");
        Check(MagPadCollision.Wheel(false, 0f, off) && !MagPadCollision.Pad(false, 0f, off), "no pad: the wheel takes the ground");
        Check(!MagPadCollision.Wheel(false, 2f, off) && !MagPadCollision.Pad(true, 2f, off), "stowed: something still collides");
        Check(MagPadCollision.Pad(true, 1.5f, off) && !MagPadCollision.Pad(true, 1.7f, off), "the pad's collision mark");
    }

    static void Stiffness()
    {
        Check(GearStiffness.MaxStrength * GearStiffness.SolverTau <= GearStiffness.MaxEffectiveTau + 1e-5f && GearStiffness.MaxEffectiveTau <= 1.67f, "cap above 1.67 effective");
        Check(GearStiffness.Strength(1f, 3f) == GearStiffness.MaxStrength, "the slider's top x3 not capped: " + GearStiffness.Strength(1f, 3f));
        Check(GearStiffness.Strength(0.5f, 3f) == 1.5f && GearStiffness.Strength(0f, 3f) == 0f && GearStiffness.Strength(-1f, 3f) == 0f, "below the cap changed");
        for (float sl = 0f; sl <= 1f; sl += 0.05f) Check(GearStiffness.Strength(sl, 3f) * GearStiffness.SolverTau <= GearStiffness.MaxEffectiveTau + 1e-5f, "over the cap at " + sl);
        // the hidden wheel's mass carries the rated load: at the generator's default slider the sag is within half the stroke, at the
        // top a few cm; the vanilla 300 kg wheel alone would not (that is why it is heavier)
        string py = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "meshgen", "gen_maggear_defs.py");
        string src = File.Exists(py) ? File.ReadAllText(py) : "";
        Check(src.Length > 0, "gen_maggear_defs.py not found at " + Path.GetFullPath(py));
        var mw = Regex.Match(src, @"WHEEL_MASS\s*=\s*([0-9.]+)"); var ms = Regex.Match(src, @"DEFAULT_STIFFNESS\s*=\s*([0-9.]+)");
        Check(mw.Success && float.Parse(mw.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) == MagGearSizes.Wheel150, "the definition's WHEEL_MASS is not MagGearSizes.Wheel150");
        float slider = ms.Success ? float.Parse(ms.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0.08f;
        Check(ms.Success, "no DEFAULT_STIFFNESS in gen_maggear_defs.py");
        float m = MagGearSizes.Wheel150 + MagGearSizes.Pad100, dt = 1f / 60f;
        float kDef = GearStiffness.SpringRate(GearStiffness.Strength(slider, 3f), m, dt), kTop = GearStiffness.SpringRate(GearStiffness.MaxStrength, m, dt);
        float sagDef = GearStiffness.Sag(MagGearSizes.Rated150, kDef), sagTop = GearStiffness.Sag(MagGearSizes.Rated150, kTop);
        Check(sagDef <= 0.5f * MagGearSizes.Stroke150, $"default slider {slider}: sag {sagDef:F2} m of a {MagGearSizes.Stroke150} m stroke");
        Check(sagTop < 0.1f, $"slider top: sag {sagTop:F3} m");
        float sagVanilla = GearStiffness.Sag(MagGearSizes.Rated150, GearStiffness.SpringRate(GearStiffness.Strength(slider, 3f), 300f, dt));
        Check(sagVanilla > sagDef * 2f, $"the heavier wheel buys nothing ({sagVanilla:F2} vs {sagDef:F2} m)");
    }
}
