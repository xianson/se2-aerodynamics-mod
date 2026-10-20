#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// TEST SPAWNS' DECISIONS (engine-free: Tests/SpawnTests; the engine side is EnemySpawn.cs - the keys, the blueprint's load, the
/// answer - and EnemySpawn.Server.cs - the server's spawn). 2026-10-20, approved by the user: a creative / admin TEST feature like
/// quick launch - Ctrl+M spawns an enemy "missile" that goes for you, Ctrl+B an enemy "bogey" fighter. Plumbing only: a blueprint
/// by name, made the game's hostile NPC's through the Autopilot block's own Enemy switch, its panel set to Engage with the
/// author's slot (Rocket Guidance / Dogfight) and the player as its API target, launched at a speed. No guidance or dogfight
/// logic here - that is the author's (ApRocketGuidance.cs / ApDogfight.cs).
/// </summary>
public static class EnemySpawnRules
{
    /// <summary>What a key asks for.</summary>
    public enum Kind : byte { Missile = 0, Bogey = 1, Despawn = 2 }
    /// <summary>Where it appears, around the target's heading (its velocity's, or its nose's when slower than HeadingMinSpeed).</summary>
    public enum Dir : byte { Random = 0, Behind = 1, Ahead = 2, Side = 3 }
    /// <summary>Who may spawn (world setting Spawn.Allowed).</summary>
    public enum Rule : byte { Off = 0, CreativeOrAdmin = 1, Everyone = 2 }

    public enum Verdict
    {
        Go, Off, NotCreativeOrAdmin, NoPlayer, NoBlueprintName, NoBlueprint, LoadFailed, NoAutopilot, AutopilotOff, Unattended,
        SlotMissing, Busy, NoServer, SpawnFailed, NothingToDespawn,
    }

    /// <summary>The tag every test grid's name carries (the despawn finds them by it, after a reload too).</summary>
    public const string Tag = " [aero test]";
    /// <summary>The most missiles one press fires (Spawn.MissileSalvo's range).</summary>
    public const int SalvoMax = 8;
    /// <summary>m: the least gap over the ground (on top of the ship's own radius); the gap between salvo members.</summary>
    public const double MinClearance = 100.0, SalvoGap = 10.0;
    /// <summary>m/s: slower than this, the target's heading is its nose's, not its velocity's.</summary>
    public const double HeadingMinSpeed = 5.0;
    /// <summary>m/s: the launch speed when neither the setting nor the blueprint's panel gives one.</summary>
    public const double MissileFallbackSpeed = 100.0, BogeyFallbackSpeed = 150.0;
    /// <summary>The bogey's cruise throttle (as quick launch's).</summary>
    public const float CruiseThrottle = 0.75f;
    /// <summary>The most times a spawn point inside a grid is moved up.</summary>
    public const int MaxRaises = 8;
    /// <summary>s: how long the server waits for a spawned ship's Autopilot block to come up before it says so.</summary>
    public const double ConfigureTimeoutS = 10.0;
    /// <summary>s: a press's ask is good this long (the blueprint is loaded first: the ask is made when it is in).</summary>
    public const double AskMaxAgeS = 2.0;
    /// <summary>Blueprint names: at most this long.</summary>
    public const int NameMax = 64;

    /// <summary>May this player spawn? The world's rule; creative (the game's IsCreative: the world's or this player's creative
    /// tools) or an admin (the host: SE2 4.0 has no admin list - the in-process player passes the game's ValidateAdminAccess).</summary>
    public static Verdict Allowed(Rule rule, bool creative, bool admin)
    {
        if (rule == Rule.Off) return Verdict.Off;
        if (rule == Rule.CreativeOrAdmin && !creative && !admin) return Verdict.NotCreativeOrAdmin;
        return Verdict.Go;
    }

    /// <summary>The spawn may go ahead on the server: allowed, the player found, the world's autopilot on and allowed unattended
    /// (an unpiloted ship lets go otherwise), the blueprint has an Autopilot block, the slot's logic is written.</summary>
    public static Verdict Check(Rule rule, bool creative, bool admin, bool playerFound, bool autopilotOn, bool unattended, bool hasAutopilot, bool slotImplemented)
    {
        var v = Allowed(rule, creative, admin);
        if (v != Verdict.Go) return v;
        if (!playerFound) return Verdict.NoPlayer;
        if (!hasAutopilot) return Verdict.NoAutopilot;
        if (!autopilotOn) return Verdict.AutopilotOff;
        if (!unattended) return Verdict.Unattended;
        if (!slotImplemented) return Verdict.SlotMissing;
        return Verdict.Go;
    }

    /// <summary>The slot a kind flies (Engage's behaviour).</summary>
    public static ApBehaviour Slot(Kind k) => k == Kind.Bogey ? ApBehaviour.Dogfight : ApBehaviour.RocketGuidance;

    public static string KindName(Kind k) => k == Kind.Bogey ? "bogey" : k == Kind.Missile ? "missile" : "despawn";

    /// <summary>The player's line (null: Go).</summary>
    public static string Say(Verdict v, Kind k, string blueprint)
    {
        string bp = string.IsNullOrEmpty(blueprint) ? (k == Kind.Bogey ? "Bogey" : "Missile") : blueprint;
        string slot = k == Kind.Bogey ? "Dogfight" : "Rocket Guidance";
        return v switch
        {
            Verdict.Go => null,
            Verdict.Off => "Test spawn is turned off in this world (Spawn.Allowed)",
            Verdict.NotCreativeOrAdmin => "Test spawn: creative or an admin only in this world",
            Verdict.NoPlayer => "Test spawn: your character was not found on the server",
            Verdict.NoBlueprintName => $"Test spawn: no {KindName(k)} blueprint name set (Spawn.{(k == Kind.Bogey ? "Bogey" : "Missile")}Blueprint)",
            Verdict.NoBlueprint => $"no blueprint '{bp}' - save one with that name",
            Verdict.LoadFailed => $"Test spawn: the blueprint '{bp}' could not be loaded",
            Verdict.NoAutopilot => "the blueprint needs an Autopilot block",
            Verdict.AutopilotOff => "Test spawn: the autopilot is off in this world (Autopilot.Enabled)",
            Verdict.Unattended => "Test spawn: unattended autopilot is off in this world (Autopilot.Unattended) - nobody flies it",
            Verdict.SlotMissing => $"Test spawn: the {slot} slot's logic is not written yet",
            Verdict.Busy => "Test spawn: still loading the last one",
            Verdict.NoServer => "Test spawn: the server did not answer",
            Verdict.SpawnFailed => "Test spawn: the game refused the spawn",
            Verdict.NothingToDespawn => "Test spawn: no test ships to remove",
            _ => "Test spawn: refused",
        };
    }

    /// <summary>A blueprint name as the setting holds it: trimmed; null when unusable (empty, too long, a path, a file name the
    /// game's blueprint folder cannot hold).</summary>
    public static string CleanName(string name)
    {
        if (name == null) return null;
        string n = name.Trim();
        if (n.Length == 0 || n.Length > NameMax || n == "." || n == "..") return null;
        foreach (char c in n) if (c < ' ' || c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|') return null;
        return n;
    }

    /// <summary>Spawn.*Blueprint's rule (null: fine).</summary>
    public static string NameRule(string name) => CleanName(name) == null ? $"a blueprint's folder name: 1..{NameMax} characters, no / \\ : * ? \" < > |" : null;

    /// <summary>A grid's display name with the test tag (once).</summary>
    public static string Tagged(string name) => (name ?? "").EndsWith(Tag, StringComparison.Ordinal) ? name : (name ?? "") + Tag;
    public static bool IsTagged(string name) => name != null && name.EndsWith(Tag, StringComparison.Ordinal);

    // ── where ──

    /// <summary>The target's horizontal heading (unit, across up): its velocity's when faster than HeadingMinSpeed, else its
    /// nose's, else any horizontal direction.</summary>
    public static Vector3D Heading(Vector3D up, Vector3D velocity, Vector3D nose)
    {
        Vector3D h = Flat(velocity, up);
        if (velocity.Length() < HeadingMinSpeed || h.LengthSquared() < 1e-6) h = Flat(nose, up);
        if (h.LengthSquared() < 1e-6) h = Flat(Math.Abs(up.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitZ, up);
        return Vector3D.Normalize(h);
    }

    static Vector3D Flat(Vector3D v, Vector3D up) => v - up * Vector3D.Dot(v, up);

    /// <summary>The direction (unit, horizontal) from the target to the spawn: behind / ahead along the heading, a side (r01 &lt;
    /// 0.5 left, else right), random (r01 is the angle round the target as a share of a turn).</summary>
    public static Vector3D Direction(Dir d, Vector3D up, Vector3D heading, double r01)
    {
        Vector3D right = Vector3D.Normalize(Vector3D.Cross(heading, up));
        switch (d)
        {
            case Dir.Behind: return -heading;
            case Dir.Ahead: return heading;
            case Dir.Side: return r01 < 0.5 ? -right : right;
            default:
                double a = (double.IsFinite(r01) ? r01 : 0) * 2 * Math.PI;
                return Vector3D.Normalize(heading * Math.Cos(a) + right * Math.Sin(a));
        }
    }

    /// <summary>The spawn point: `distance` along dir from the target, at the target's height over the planet (centre given:
    /// the point put back on the target's sphere; no planet - centre NaN: straight).</summary>
    public static Vector3D Place(Vector3D target, Vector3D centre, Vector3D dir, double distance)
    {
        Vector3D p = target + dir * Math.Max(0, distance);
        if (!IsFinite(centre)) return p;
        double r = (target - centre).Length();
        Vector3D n = p - centre;
        return n.LengthSquared() > 1e-6 ? centre + Vector3D.Normalize(n) * r : p;
    }

    /// <summary>Out of the ground: at least the ship's radius plus MinClearance over the surface there (surfaceR: the ground's
    /// distance from the centre under p; NaN: not known - p stands).</summary>
    public static Vector3D OverGround(Vector3D p, Vector3D centre, double surfaceR, double shipRadius)
    {
        if (!IsFinite(centre) || !double.IsFinite(surfaceR) || surfaceR <= 0) return p;
        Vector3D n = p - centre;
        double r = n.Length();
        double want = surfaceR + Math.Max(0, shipRadius) + MinClearance;
        return r < want && r > 1e-6 ? centre + n / r * want : p;
    }

    /// <summary>m: how far a spawn point inside a grid is moved up per try.</summary>
    public static double RaiseStep(double shipRadius) => 2 * Math.Max(1, shipRadius) + 20;

    /// <summary>The i-th of n salvo members' offset (m) across the line of approach: centred, a ship's width and SalvoGap apart.</summary>
    public static double SalvoOffset(int i, int n, double shipRadius)
        => (i - (n - 1) * 0.5) * (2 * Math.Max(1, shipRadius) + SalvoGap);

    /// <summary>The salvo's size (Spawn.MissileSalvo clamped; a bogey is one).</summary>
    public static int SalvoCount(Kind k, int salvo) => k == Kind.Missile ? Math.Clamp(salvo, 1, SalvoMax) : 1;

    // ── how it points and flies ──

    /// <summary>The direction it flies off in: a missile straight at the target, a bogey level toward it.</summary>
    public static Vector3D Nose(Kind k, Vector3D from, Vector3D target, Vector3D up)
    {
        Vector3D d = target - from;
        if (k == Kind.Bogey) d = Flat(d, up);
        if (d.LengthSquared() < 1e-6) d = Flat(target - from, up);
        if (d.LengthSquared() < 1e-6) d = Math.Abs(up.X) < 0.9 ? Vector3D.Cross(up, Vector3D.UnitX) : Vector3D.Cross(up, Vector3D.UnitZ);
        return Vector3D.Normalize(d);
    }

    /// <summary>The grid's world orientation that puts its nose (the Autopilot block's front, in the grid's frame: noseL, with
    /// the block's top topL) along noseW, its top as near the world's up as the nose allows.</summary>
    public static Quaternion Aim(Vector3 noseL, Vector3 topL, Vector3D noseW, Vector3D up)
    {
        Vector3D top = up - noseW * Vector3D.Dot(up, noseW);
        if (top.LengthSquared() < 1e-6) top = Vector3D.Cross(noseW, Math.Abs(noseW.X) < 0.9 ? Vector3D.UnitX : Vector3D.UnitZ);
        top = Vector3D.Normalize(top);
        var have = Quaternion.CreateFromForwardUp(Vector3.Normalize(noseL), Vector3.Normalize(topL));
        var want = Quaternion.CreateFromForwardUp((Vector3)noseW, (Vector3)top);
        return Quaternion.Normalize(want * Quaternion.Inverse(have));
    }

    /// <summary>The launch speed (m/s): the setting when above 0; else the cruise (a bogey's: its top speed at CruiseThrottle - NaN
    /// not known yet), else the blueprint panel's own speed hold (Flight Hold's speed), else the kind's fallback; at most cap (0:
    /// none).</summary>
    public static double Speed(Kind k, double setting, double cruise, double panelSpeed, double cap)
    {
        double v = setting > 0 && double.IsFinite(setting) ? setting
            : k == Kind.Bogey && cruise > 1 && double.IsFinite(cruise) ? cruise
            : panelSpeed > 0 && double.IsFinite(panelSpeed) ? panelSpeed
            : k == Kind.Bogey ? BogeyFallbackSpeed : MissileFallbackSpeed;
        if (cap > 0 && v > cap) v = cap;
        return Math.Max(0, v);
    }

    /// <summary>The bogey's speed waits for its cruise (the aero probe on the spawned grid): only when no setting and no panel
    /// speed gives one.</summary>
    public static bool WantsCruise(Kind k, double setting, double panelSpeed) => k == Kind.Bogey && !(setting > 0) && !(panelSpeed > 0);

    /// <summary>THE PANEL a spawned ship flies with: the blueprint's own (its tactical numbers, its slot's parameters, its limits)
    /// with Engage on, the kind's slot, the target from the API (the player: AeroApi.SetAutopilotTarget), every relation taken
    /// (the player is the one target named - also before the owner change lands), kept with nobody seated, no return point.
    /// The Enemy switch is not set here (its own step does it and writes it back).</summary>
    public static ApPanel Configure(in ApPanel blueprint, Kind k)
    {
        var p = blueprint;
        p.Mode = ApMode.Engage;
        p.Behaviour = Slot(k);
        p.TargetSrc = ApTargetSrc.Api; p.TargetText = "";
        p.TargetFilter = ApTargetFilter.Everything;
        p.Unattended = true;
        p.HasHome = false; p.ReturnHome = false; p.HomeX = p.HomeY = p.HomeZ = 0;
        p.DfSaved = ""; p.RgSaved = "";   // (a fresh fight: no slot state from the blueprint's last one)
        return p.Clamped();
    }

    // ── cleanup ──

    /// <summary>A test ship spawned at `at` (ticks) is due for removal at `now` (minutes 0: never).</summary>
    public static bool Expired(long at, long now, double minutes, long frequency)
        => minutes > 0 && double.IsFinite(minutes) && now - at >= (long)(minutes * 60.0 * frequency);

    // ── the keys (client settings Spawn.MissileKey / BogeyKey / DespawnKey, one modifier Spawn.Modifier) ──

    /// <summary>A binding as the player reads it ("Ctrl+M"; key 0: "off").</summary>
    public static string BindingText(int key, int modifier) => QuickLaunchRules.BindingText(key, modifier);

    static bool IsFinite(Vector3D v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
}
