#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// QUICK LAUNCH's decisions (engine-free: Tests/QuickLaunchTests; the engine side is QuickLaunch.cs - the key, the answer -
/// and QuickLaunch.Server.cs - the server's step). 2026-10-18, the user: "if the game is in creative, allow quick launch in
/// cockpit as an actual keybind" (shipped in the Orbital Mod's 1.0.1), then "ctrl+T should be in aero creative": the aero
/// mod owns it now. The server decides from its own state whether a seated pilot's ship may be lifted, where to and how fast.
/// The Orbital Mod, when it is loaded, does the move (OrbitalApi.TryQuickMove: its safe move path, and its planet's space
/// border - the ship never ends past it); without it aero moves the ship itself (QuickLaunchMove).
/// </summary>
public static class QuickLaunchRules
{
    public enum Verdict { Go, Off, NotCreative, NotSeated, Gone, Static, JoinedToStatic, NotAtPlanet, Refused }

    /// <summary>m: the least gap between the ground (the reference under the ship) and the ship's own bounding radius
    /// after the lift - it is turned level in place, so a long ship needs its length clear.</summary>
    public const double MinClearance = 100.0;
    /// <summary>The cruise throttle (the speed with QuickLaunch.SpeedMps 0) and the speed when the ship has no cruise.</summary>
    public const float CruiseThrottle = 0.75f;
    public const double FallbackSpeed = 150.0;
    /// <summary>The launch speed's cap, as a share of the physics' speed cap (the Orbital Mod's arrival cap: 990 of 1000).</summary>
    public const double CapShare = 0.99;

    /// <summary>
    /// May this ask go ahead? In this order: the world allows it (QuickLaunch.Creative); the server knows the asking
    /// player's character; it is in creative (GameModeSessionComponent.IsCreative: the world's creative mode, or this
    /// player's creative tools); the server sees it seated (its own seat - the client names no grid, so a stale or forged
    /// one moves nothing); the seat's grid is alive, not static, not joined to anything static (a station is never
    /// teleported: the Havok rule, docs/ARCHITECTURE.md); it is near a planet.
    /// </summary>
    public static Verdict Check(bool enabled, bool pilotFound, bool creative, bool seated, bool gridAlive, bool isStatic,
                                bool joinedToStatic, bool atPlanet)
    {
        if (!enabled) return Verdict.Off;
        if (!pilotFound) return Verdict.NotSeated;
        if (!creative) return Verdict.NotCreative;
        if (!seated) return Verdict.NotSeated;
        if (!gridAlive) return Verdict.Gone;
        if (isStatic) return Verdict.Static;
        if (joinedToStatic) return Verdict.JoinedToStatic;
        if (!atPlanet) return Verdict.NotAtPlanet;
        return Verdict.Go;
    }

    /// <summary>
    /// Where the ship ends, as a distance from the planet's centre (r). rNow: where it is; rGround: the ground under it
    /// (NaN: not known - no ground under the aero ground ray's reach; then the ship's own place stands in for the ground,
    /// so it is lifted by the altitude from where it is and never into a hill nobody measured). Never lower than it is now,
    /// never closer to the ground than its radius plus MinClearance. maxDrop: how far straight down from r a clamp may
    /// still put it with that clearance kept (the Orbital Mod's space border clamp, OrbitalApi.TryQuickMove - aero knows
    /// no border; 0 when only staying where it is keeps the clearance).
    /// </summary>
    public static Verdict Lift(double rNow, double rGround, double altitude, double shipRadius, out double r, out double maxDrop)
    {
        r = rNow; maxDrop = 0;
        if (!(rNow > 0) || double.IsInfinity(rNow)) return Verdict.NotAtPlanet;
        // (a ground above the ship is a bad ray - inside a cave or a ray through the ship: use the ship's place)
        double floor = double.IsNaN(rGround) || double.IsInfinity(rGround) || rGround > rNow ? rNow : rGround;
        double clear = Math.Max(0, double.IsNaN(shipRadius) ? 0 : shipRadius) + MinClearance;
        double want = floor + Math.Max(double.IsNaN(altitude) ? 0 : altitude, clear);
        if (want < rNow) want = rNow;   // (lifted, never dropped)
        r = want;
        maxDrop = Math.Max(0, want - (floor + clear));
        return Verdict.Go;
    }

    /// <summary>The launch speed (m/s, along the nose): the setting when above 0, else the cruise speed (the aero top speed
    /// at CruiseThrottle; NaN or under 1 m/s: fallback), at most cap (0: none).</summary>
    public static double Speed(double setting, double cruise, double fallback, double cap)
    {
        double v = setting > 0 ? setting : (cruise > 1 && !double.IsInfinity(cruise) ? cruise : fallback);
        if (cap > 0 && v > cap) v = cap;
        return Math.Max(0, v);
    }

    /// <summary>
    /// The grid's world orientation after the turn to level, about its own place: the cockpit (noseW, topW: its forward and
    /// up in the world) turned so that its nose lies horizontal on its heading and its top points away from the planet (up),
    /// the grid turned with it. heading: that horizontal nose (unit) - the launch's direction. A nose straight up or down
    /// takes its top's heading.
    /// </summary>
    public static Quaternion Level(Quaternion grid, Vector3D noseW, Vector3D topW, Vector3D up, out Vector3D heading)
    {
        Vector3D h = noseW - up * Vector3D.Dot(noseW, up);
        if (h.LengthSquared() < 1e-6) h = Vector3D.Cross(up, Vector3D.Cross(topW, up));
        h = Vector3D.Normalize(h);
        heading = h;
        var have = Quaternion.CreateFromForwardUp((Vector3)noseW, (Vector3)topW);
        var want = Quaternion.CreateFromForwardUp((Vector3)h, (Vector3)up);
        return Quaternion.Normalize(Quaternion.Normalize(want * Quaternion.Inverse(have)) * grid);
    }

    /// <summary>The answer's "where" (rPlaced: where it went; rGround NaN: no ground known - said as the lift, not a height
    /// over a ground nobody measured).</summary>
    public static string Where(double rPlaced, double rNow, double rGround)
        => double.IsNaN(rGround) ? $"{rPlaced - rNow:F0} m up" : $"{rPlaced - rGround:F0} m over the ground";

    /// <summary>The player's line for a refusal (null: none - Go). Refused: the Orbital Mod's own reason is said instead.</summary>
    public static string Say(Verdict v) => v switch
    {
        Verdict.Go => null,
        Verdict.Off => "Quick launch is turned off in this world",
        Verdict.NotCreative => "Quick launch works in creative only",
        Verdict.NotSeated => "Quick launch: sit in a cockpit first",
        Verdict.Gone => "Quick launch: the ship is gone",
        Verdict.Static => "Quick launch: a station cannot be launched",
        Verdict.JoinedToStatic => "Quick launch: the ship is docked or locked to a station",
        Verdict.NotAtPlanet => "Quick launch: not near a planet",
        _ => "Quick launch: refused",
    };

    // ── the binding (client settings QuickLaunch.Key / QuickLaunch.Modifier) ──

    /// <summary>The modifier held as the binding wants: 0 none (no Ctrl, Shift or Alt held - so Ctrl+T bound elsewhere never
    /// also fires a bare T), 1 Ctrl, 2 Shift, 3 Alt.</summary>
    public static bool ModifierHeld(int modifier, bool ctrl, bool shift, bool alt) => modifier switch
    {
        0 => !ctrl && !shift && !alt,
        1 => ctrl,
        2 => shift,
        3 => alt,
        _ => false,
    };

    /// <summary>The binding as a player reads it ("Ctrl+T"; key 0: "off"; 1..26 the letters A..Z).</summary>
    public static string BindingText(int key, int modifier)
    {
        if (key < 1 || key > 26) return "off";
        string k = ((char)('A' + key - 1)).ToString();
        return modifier switch { 1 => "Ctrl+" + k, 2 => "Shift+" + k, 3 => "Alt+" + k, _ => k };
    }

    // ── the key's press and the asks (QuickLaunch.cs; here so Tests/QuickLaunchTests can play key sequences) ──
    // (2026-10-18, the in-game run of the Orbital Mod's copy: ships lifted with nobody asking. One ask per press, taken once,
    //  never late: a key down from before - held while sitting down, a stale device state after a load or a lost device - is
    //  not a press; an ask or a harness ask nobody took in time is dropped, never served later to whatever ship you sit in.)

    /// <summary>The binding's press: true on the one read where its key goes from up to down with its modifier held - never
    /// while it stays down, never for a down not seen up first (a new edge, Disarm: a load, a lost device). The key going
    /// down without its modifier uses the press up (adding Ctrl to a held T is no press).</summary>
    public sealed class KeyEdge
    {
        bool _armed;   // (the key was read up since the last down or disarm - false at first: a key down at load asks nothing)
        public bool Step(bool down, bool modifierHeld)
        {
            if (!down) { _armed = true; return false; }
            if (!_armed) return false;
            _armed = false;
            return modifierHeld;
        }
        /// <summary>The next press needs the key read up first (a world load, a device looked up again).</summary>
        public void Disarm() => _armed = false;
    }

    /// <summary>s: the longest an ask (the key's, or the harness's AeroApi.RequestQuickLaunch) may wait to be taken.</summary>
    public const double AskMaxAgeS = 1.0;

    /// <summary>An ask stamped at (ticks; 0: none) is still good at now: not from the future, at most maxAge old.</summary>
    public static bool Fresh(long at, long now, long maxAge) => at != 0 && now >= at && now - at <= maxAge;

    /// <summary>QuickLaunch.Key's labels: "Off", then the letters A..Z.</summary>
    public static string[] KeyChoices()
    {
        var c = new string[27];
        c[0] = "Off";
        for (int k = 1; k < c.Length; k++) c[k] = ((char)('A' + k - 1)).ToString();
        return c;
    }
}
