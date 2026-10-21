#pragma warning disable
using System;
using System.Globalization;

namespace AeroMod;

/// <summary>
/// The MAGNETIC LANDING GEAR's game-free core (2026-10-20; the game side is Integration/AeroMagGearComponent.cs). The gear block
/// keeps the vanilla suspension and a hidden wheel body on it; its foot is a one-block PAD GRID the mod spawns - a real vanilla
/// magnetic landing gear block - joined rigidly to the hidden wheel. Here: the pad grid's name (how a gear finds its pad again
/// after a load), the pad's life (search, spawn, join, lost, rebuilt), the magnet's gates (never onto its own ship, never while
/// stowed or moving, parking brake and auto-lock), the retraction veto, and which body collides when. Tests/GearTests.
/// </summary>
public static class MagPadNames
{
    /// <summary>The pad grid's name: the tag and the gear's id ("Gear Pad [0123456789abcdef]"). The id is saved with the gear
    /// block (AeroBlockSaveForm.MagGear): a load finds the pad by it (an entity reference to another grid is not saved with the
    /// block - a blueprint or a copy of the ship would carry a reference to a grid it does not hold).</summary>
    public const string Tag = "Gear Pad [";

    public static string Name(ulong uid) => Tag + uid.ToString("x16", CultureInfo.InvariantCulture) + "]";

    public static bool TryParse(string name, out ulong uid)
    {
        uid = 0;
        if (name == null || name.Length != Tag.Length + 17 || !name.StartsWith(Tag, StringComparison.Ordinal) || name[name.Length - 1] != ']') return false;
        return ulong.TryParse(name.AsSpan(Tag.Length, 16), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uid) && uid != 0;
    }

    /// <summary>A fresh id (never 0: 0 is "none yet").</summary>
    public static ulong NewUid(Random rnd)
    {
        Span<byte> b = stackalloc byte[8];
        ulong u;
        do { rnd.NextBytes(b); u = BitConverter.ToUInt64(b); } while (u == 0);
        return u;
    }
}

/// <summary>Where a gear's pad is in its life.</summary>
public enum PadPhase { Start, Searching, NeedSpawn, Spawning, Joining, Joined, Lost }

/// <summary>What the game side must do this step: Search (look for the pad by its id), Spawn (a new pad, id Uid), Join (join the pad
/// found or spawned - report PadWorld.Join), Respawn (remove the pad there; the next steps spawn a new one), Release (the pad is
/// gone: drop the join), GiveUp (lost: remove any pad, drop the join).</summary>
public enum PadDo { Nothing, Search, Spawn, Join, Respawn, Release, GiveUp }

/// <summary>What the game side saw this step (only the fields of the phase asked about are read).</summary>
public struct PadWorld
{
    /// <summary>The hidden wheel exists and its constraint is known (nothing is spawned before it).</summary>
    public bool WheelReady;
    /// <summary>The gear block is functional (built, not broken).</summary>
    public bool Functional;
    /// <summary>The gear is down and still (a pad is spawned only at the bottom of its stroke).</summary>
    public bool Down;
    /// <summary>Nothing but its own ship in the pad's box where it would spawn.</summary>
    public bool Room;
    /// <summary>Searching: the pad grid with this gear's id was found near.</summary>
    public bool Found;
    /// <summary>Spawning: the spawned pad came in / the spawn failed.</summary>
    public bool Arrived, SpawnFailed;
    /// <summary>Joining: the join is made (1), or the pad came in too far from where it should be (2), or not yet (0).</summary>
    public int Join;
    /// <summary>Joined: the pad grid is alive and its block is there.</summary>
    public bool PadAlive;
}

/// <summary>
/// A gear's PAD LIFE (pure; one per gear block). Built: a new id, spawn (when the wheel exists, the gear is down and there is
/// room), join. Loaded with an id: search for it a while (the grids come in over a few ticks), join it; not found (a pasted copy
/// of the ship, a pad removed while the mod was off): a new id, spawn. A pad that came in too far off (the ship moved between the
/// spawn and its arrival): removed and spawned again, MaxTries at most. The pad destroyed or ground down: LOST - saved so, never
/// spawned again for free (grinding the pad would mint its components); the gear block repaired (functional again after it was
/// not) rebuilds it. Lost, the gear still works: the hidden wheel takes the ground (AeroMagGearComponent).
/// </summary>
public sealed class MagPadLife
{
    public PadPhase Phase { get; private set; } = PadPhase.Start;
    public ulong Uid { get; private set; }
    /// <summary>The pad was lost (saved): not spawned again until the gear block is repaired.</summary>
    public bool Lost { get; private set; }
    /// <summary>Why it is where it is (the terminal and the harness).</summary>
    public string Why { get; private set; } = "";
    public int Tries { get; private set; }
    double _since;
    bool _wasFunctional = true;

    public const double SearchSeconds = 5, SpawnTimeoutSeconds = 15;
    public const int MaxTries = 3;

    /// <summary>A saved gear (before its first step).</summary>
    public void Restore(ulong uid, bool lost) { Uid = uid; Lost = lost; Phase = PadPhase.Start; Tries = 0; }

    /// <summary>One step. newUid: a fresh id, taken only when a spawn starts.</summary>
    public PadDo Step(double now, in PadWorld w, ulong newUid)
    {
        // (repaired: a broken gear block welded back - a lost pad is rebuilt; never for a gear that was functional all along)
        bool repaired = w.Functional && !_wasFunctional;
        _wasFunctional = w.Functional;
        switch (Phase)
        {
            case PadPhase.Start:
                if (!w.WheelReady) { Why = "waiting for the wheel"; return PadDo.Nothing; }
                if (Lost) { Phase = PadPhase.Lost; Why = "pad lost: repair the gear block to rebuild it"; return PadDo.Nothing; }
                if (Uid != 0) { Phase = PadPhase.Searching; _since = now; Why = "looking for its pad"; return PadDo.Search; }
                Phase = PadPhase.NeedSpawn; goto case PadPhase.NeedSpawn;

            case PadPhase.Searching:
                if (w.Found) { Phase = PadPhase.Joining; _since = now; Why = "joining its pad"; return PadDo.Join; }
                if (now - _since < SearchSeconds) return PadDo.Search;
                Phase = PadPhase.NeedSpawn; Why = "its pad was not found: a new one";
                goto case PadPhase.NeedSpawn;

            case PadPhase.NeedSpawn:
                if (!w.WheelReady) { Why = "waiting for the wheel"; return PadDo.Nothing; }
                if (!w.Functional) { Why = "the gear block is not functional"; return PadDo.Nothing; }
                if (!w.Down) { Why = "a pad is made with the gear down"; return PadDo.Nothing; }
                if (!w.Room) { Why = "no room for the pad (something under the gear)"; return PadDo.Nothing; }
                Uid = newUid; Phase = PadPhase.Spawning; _since = now; Why = "spawning its pad";
                return PadDo.Spawn;

            case PadPhase.Spawning:
                if (w.Arrived) { Phase = PadPhase.Joining; _since = now; Why = "joining its pad"; return PadDo.Join; }
                if (!w.SpawnFailed && now - _since < SpawnTimeoutSeconds) return PadDo.Nothing;
                return Retry(w.SpawnFailed ? "the pad's spawn failed" : "the pad never came in", false);

            case PadPhase.Joining:
                if (w.Join == 1) { Phase = PadPhase.Joined; Tries = 0; Why = "joined"; return PadDo.Nothing; }
                if (w.Join == 2) return Retry("the pad came in too far off", true);
                if (now - _since > SpawnTimeoutSeconds) return Retry("the pad could not be joined", true);
                return PadDo.Join;

            case PadPhase.Joined:
                if (w.PadAlive) return PadDo.Nothing;
                Phase = PadPhase.Lost; Lost = true; Why = "pad lost: repair the gear block to rebuild it";
                return PadDo.Release;

            case PadPhase.Lost:
                if (!repaired) return PadDo.Nothing;
                Lost = false; Tries = 0; Phase = PadPhase.NeedSpawn; Why = "repaired: a new pad";
                return PadDo.Nothing;
        }
        return PadDo.Nothing;
    }

    /// <summary>Start over (hadPad: a pad is there and must be removed first - Respawn); MaxTries at most, then lost (GiveUp: the
    /// caller removes any pad).</summary>
    PadDo Retry(string why, bool hadPad)
    {
        Tries++;
        if (Tries >= MaxTries) { Phase = PadPhase.Lost; Lost = true; Why = why + $" ({Tries} tries): repair the gear block to try again"; return PadDo.GiveUp; }
        Phase = PadPhase.NeedSpawn; Why = why + ": again";
        return hadPad ? PadDo.Respawn : PadDo.Nothing;
    }

    /// <summary>Harness: as if the gear block was repaired (a lost pad is rebuilt).</summary>
    public void Repair() { if (Phase != PadPhase.Lost) return; Lost = false; Tries = 0; Phase = PadPhase.NeedSpawn; Why = "repaired: a new pad"; }

    /// <summary>The pad the gear had is gone without a step seeing it (the gear's own removal): forget it, keep the id.</summary>
    public void Reset() { Phase = PadPhase.Start; Tries = 0; }
}

/// <summary>What the magnet does this step.</summary>
public enum MagAct { None, Lock, Unlock }

/// <summary>The magnet's state as the game side read it this step.</summary>
public struct MagSeen
{
    /// <summary>The pad is joined to its wheel.</summary>
    public bool Joined;
    /// <summary>The gear is down, still and not held up (fully extended, no travel).</summary>
    public bool Down;
    public bool Locked;
    /// <summary>What it is locked to is NOT its own ship (nor a pad or wheel of it).</summary>
    public bool LockedForeign;
    /// <summary>The magnet sees something to lock to, and that it is not its own ship.</summary>
    public bool Ready, ReadyForeign;
    /// <summary>The vanilla auto-lock cooldown is over (it runs after an unlock until the detection is lost).</summary>
    public bool CooldownDone;
    /// <summary>The pad block is functional and working (the vanilla magnet's own condition).</summary>
    public bool Working;
    /// <summary>The player's auto-lock setting (the gear's terminal).</summary>
    public bool AutoLock;
    /// <summary>The gear block's parking brake (its corner module's IsParked - the ship's P toggles it).</summary>
    public bool Parked;
}

/// <summary>
/// The MAGNET'S GATES (pure; one per gear). The vanilla magnet's own auto-lock is never used (it is kept off): its detector
/// ignores only its own grid - the pad's - so it would lock onto the parent ship. Instead, each step:
///  - locked onto its own ship (the vanilla magnet re-locks to whatever its detector sees first while locked): unlocked at once;
///  - not down (retracting, stowed, held up, not yet joined): never locked (and a lock is undone);
///  - the parking brake (P) pressed: lock when something foreign is ready; released: unlock;
///  - auto-lock on: lock when something foreign is ready and the vanilla cooldown is over (after an unlock it runs until the
///    detection is lost - lift off before it locks again).
/// The parking brake follows the magnet: a lock (or unlock) that did not come from P flips the brake to match, so the next P
/// press unlocks (ParkToggle); that flip is not taken for the player's press.
/// </summary>
public sealed class MagGates
{
    bool _hasPark, _lastPark, _expectFlip;
    public int SelfLockVetoes { get; private set; }
    public string Status { get; private set; } = "";

    /// <summary>One step: what to do with the magnet, and parkToggle - flip the parking brake to match the magnet.</summary>
    public MagAct Decide(in MagSeen s, out bool parkToggle)
    {
        parkToggle = false;
        bool pressed = false, released = false;
        if (!_hasPark) { _hasPark = true; _lastPark = s.Parked; }
        else if (s.Parked != _lastPark)
        {
            _lastPark = s.Parked;
            if (_expectFlip) _expectFlip = false;   // (our own flip: not the player's)
            else if (s.Parked) pressed = true; else released = true;
        }

        if (s.Locked && !s.LockedForeign) { SelfLockVetoes++; Status = "unlocked: it had locked onto its own ship"; return MagAct.Unlock; }
        if (s.Locked && (!s.Down || !s.Joined)) { Status = "unlocked: the gear is not down"; return Flip(MagAct.Unlock, s, ref parkToggle); }
        if (s.Locked)
        {
            if (released) { Status = "unlocked (parking brake off)"; return MagAct.Unlock; }
            if (!s.Parked && !_expectFlip) parkToggle = Expect();   // (locked without P - auto-lock: the brake shows it)
            Status = "locked";
            return MagAct.None;
        }
        // (unlocked)
        if (!s.Joined) { Status = "no pad"; return MagAct.None; }
        if (!s.Down) { Status = "stowed"; return MagAct.None; }
        if (!s.Working) { Status = "the pad is off or broken"; return MagAct.None; }
        if (!s.Ready) { Status = s.Parked ? "parking brake on: nothing to lock to" : "nothing to lock to"; return MagAct.None; }
        if (!s.ReadyForeign) { Status = "only its own ship in reach: not locking"; return MagAct.None; }
        if (s.Parked || (s.AutoLock && s.CooldownDone)) { Status = "locking"; return MagAct.Lock; }
        Status = s.AutoLock ? "ready to lock (cooldown: lift off first)" : "ready to lock (parking brake or Lock)";
        return MagAct.None;
    }

    MagAct Flip(MagAct a, in MagSeen s, ref bool parkToggle) { if (s.Parked && !_expectFlip) parkToggle = Expect(); return a; }
    bool Expect() { _expectFlip = true; return true; }

    /// <summary>Retracting is refused while the magnet holds (the reason; null: free to retract).</summary>
    public static string RetractRefusal(bool locked) => locked ? "magnet locked: unlock it before retracting" : null;
}

/// <summary>Which body takes the ground (pure): the PAD once it is joined and out of the well; the hidden WHEEL while there is no
/// pad (none yet, or lost: the gear still carries the ship); neither deep in the well.</summary>
public static class MagPadCollision
{
    public static bool Wheel(bool padJoined, float travel, float offAt) => !padJoined && travel < offAt;
    public static bool Pad(bool padJoined, float travel, float offAt) => padJoined && travel < offAt;
}

/// <summary>
/// THE SUSPENSION'S STIFFNESS CAP (2026-10-20, every gear): the wheel constraint's SuspensionStrengthFactor scales the solver's
/// tau (SolverTau, 0.6) - the effective tau is their product, and past ~1.5-2 the suspension goes unstable (it pumps energy in).
/// The terminal's slider (0..1) x StiffnessScale is capped so the effective tau never passes MaxEffectiveTau. The spring this
/// gives grows with the WHEEL's mass (k ~ strength x tau x m_wheel / dt^2): a light wheel under a heavy ship bottoms out, so the
/// magnetic gear's hidden wheel is made heavy (its definition's WheelMass, gen_maggear_defs.py).
/// </summary>
public static class GearStiffness
{
    public const float SolverTau = 0.6f, MaxEffectiveTau = 1.67f;
    /// <summary>The highest strength factor written (2.78).</summary>
    public const float MaxStrength = MaxEffectiveTau / SolverTau;

    /// <summary>The strength factor for this slider (0..1) and scale, capped.</summary>
    public static float Strength(float slider, float scale) => Math.Clamp(Math.Max(0f, slider) * Math.Max(0f, scale), 0f, MaxStrength);

    /// <summary>The spring rate it gives (N/m): strength x tau x the wheel's (and pad's) mass / dt^2.</summary>
    public static float SpringRate(float strength, float mass, float dt) => strength * SolverTau * mass / (dt * dt);

    /// <summary>The static sag under a load (m) at that spring rate.</summary>
    public static float Sag(float loadKg, float springRate, float g = 9.81f) => springRate > 0f ? loadKg * g / springRate : float.PositiveInfinity;
}

/// <summary>The magnetic gears' sizes (pure: AeroGearVariants uses them, GearTests checks them; tools/meshgen/gen_maggear_defs.py's
/// WHEEL_MASS must be Wheel150 - the definition is what the game weighs).</summary>
public static class MagGearSizes
{
    /// <summary>The 1.5 m gear's hidden wheel (kg; the vanilla 1.5 m wheel is 300) and the LandingGear100 pad (kg).</summary>
    public const float Wheel150 = 600f, Pad100 = 158f;
    /// <summary>The weight one 1.5 m gear is sized for (kg) and its stroke (m, the vanilla 1.5 m suspension's travel).</summary>
    public const float Rated150 = 20000f, Stroke150 = 0.75f;
    /// <summary>The LandingGear100 pad's magnetic face in its grid's frame (m; a guess from its model's lock zone - the first pad in
    /// measures it: AeroMagGearComponent).</summary>
    public const float PadFaceY100 = -0.49f;
}
