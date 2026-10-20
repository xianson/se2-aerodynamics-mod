#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>What an autopilot holds (several at once): the attitude it was given (ACAH, Attitude), a hover - level on its
/// heading, the horizontal speed held (TRC, Hover) - the place (Position, with Hover) and the height (Height).</summary>
[Flags]
public enum ApGoal : byte { None = 0, Attitude = 1, Hover = 2, Position = 4, Height = 8, External = 16 }

/// <summary>Why an autopilot let go or would not engage (ApText.Say: the player's words).</summary>
public enum ApReason : byte
{
    None, TurnedOff, Steered, DampenersOff, NotAllowed, NoGravity, NoLift, TooFast, Upset, LeftSeat, CantHoldHeight, NoAttitude,
    NeedsDampeners, NoSource, GoalDone,
    /// <summary>The controlling Autopilot block was switched off, lost its power, was damaged or removed (15).</summary>
    BlockOff,
    /// <summary>(16..) The flight pilot (2026-10-19): an airplane needs airspeed (and air) to fly on its wings; a route needs a
    /// waypoint; six-axis flight needs thrust across the main axis; the style asked does not fit the ship.</summary>
    TooSlow, NoAir, NoWaypoints, NoTranslation, WrongStyle,
    /// <summary>(21) A mode chosen on the block and the ship's autopilot step never took it up (ApEngageWatch: a wreck with no
    /// thruster or gyro left - the grid's attitude step does not run for it).</summary>
    NoResponse,
}

/// <summary>What the envelope protection changed this tick (several at once).</summary>
[Flags]
public enum ApProt : ushort
{
    None = 0,
    /// <summary>The lean held to the most lean (Autopilot.MaxLeanDeg).</summary>
    Lean = 1,
    /// <summary>The lean held to what the thrust can lean and still hold the weight.</summary>
    Thrust = 2,
    /// <summary>The attitude held short of the stall angle (the whole ship's best lift, LiftPeak).</summary>
    Alpha = 4,
    /// <summary>The attitude held to the load limit (Autopilot.GLimit).</summary>
    GLimit = 8,
    /// <summary>The attitude step shrunk: the predicted loss of control (Loac) would pass its limit on the way.</summary>
    Loac = 16,
    /// <summary>Over the speed limit: throttle off, the speed command held.</summary>
    Overspeed = 32,
    /// <summary>Near the ground and sinking toward it: climbing, levelled.</summary>
    Floor = 64,
    /// <summary>Out of energy (thrust short of the weight, or near the stall): full thrust, no lean.</summary>
    LowEnergy = 128,
    /// <summary>Terrain ahead within the time to turn away (the look ahead along the path): climbing / pulling up.</summary>
    Terrain = 256,
    /// <summary>Another grid, an asteroid or the target within the look-ahead's closest approach: a sidestep or a pull-up (ApAvoid).</summary>
    Avoid = 512,
}

/// <summary>
/// What the autopilot knows each tick - the ship's own state and the mod's own measures of it, nothing more than a pilot
/// could read off the instruments (world frame unless said; NaN or 0: not known).
/// </summary>
public struct ApSense
{
    /// <summary>The limits and choices it flies with this tick - the controlling Autopilot block's (its panel), the world's caps in
    /// (ApLimits.Of); offline the static defaults (ApLimits.Defaults).</summary>
    public ApLimits Lim;
    public float Dt;
    /// <summary>The ship's orientation (grid local to world), its rate (local rad/s), centre of mass (world) and velocity (world m/s).</summary>
    public Quaternion Orient; public Vector3 AngVel; public Vector3D Pos; public Vector3 Vel;
    /// <summary>The gravity the body really feels (world m/s2: the planet's x the world's gravity multiplier), mass (kg).</summary>
    public Vector3 Gravity; public float Mass;
    /// <summary>The gravity the body falls with when nothing holds it (world m/s2: the planet's x the engine's multiplier) - an
    /// external acceleration goal is flown against it; and how far the ship reaches below its centre of mass (m: its belly).</summary>
    public Vector3 GravityTrue; public float Belly;
    /// <summary>The ship's own radius (m: half its bounds' diagonal) - the collision avoidance's size (0: not known).</summary>
    public float Radius;
    /// <summary>Height over the ground (m; negative: no ground found).</summary>
    public float Agl;
    /// <summary>The thrusters' maxima per local side (N, as magnitudes: the game's MaxThrustData.Regular).</summary>
    public Vector3 ThrustPos, ThrustNeg;
    /// <summary>The translation authority per local side (SideAuthority: 0 none - a single tail rotor - .. 1 through the centre
    /// of mass); the hover's movement keys go only where there is some.</summary>
    public Vector3 SidePos, SideNeg;
    /// <summary>The dampeners are on.</summary>
    public bool Dampeners;
    /// <summary>The pilot is seated (their input below is fresh); their movement keys (local -1..1) and how far they steered
    /// (deg, a leaky sum of their reticle's moves while they turn it).</summary>
    public bool Piloted; public Vector3 PilotMove; public float PilotSteerDeg;
    /// <summary>The angular acceleration the attitude law has about the lean axes (rad/s2, the lesser: its actuators' reach over
    /// the inertia) - how fast the ship tilts, the outer loops' bandwidth. 0: not known.</summary>
    public float AttAccel;
    /// <summary>... and what the attitude law PLANS its braking with (rad/s2: its gyros, thrusters and surfaces - not a rotor's
    /// cyclic, which it gives on top): the pace of a large tilt - the lean is changed no faster than this lets it follow. 0: AttAccel.</summary>
    public float AttAccelPlan;
    /// <summary>The attitude law's error to its target (deg) and its need over its reach (the worst axis; 1: saturated).</summary>
    public float AttErrDeg, CtrlShare;
    /// <summary>The ship's motion through the air (local m/s) and the air's density (kg/m3).</summary>
    public Vector3 AirVel; public float Density;
    /// <summary>The stall evaluation (Hud/StallWarning.cs): it ran (Fresh), 0/1/2, the best lift's angle from the flow now
    /// (deg, + further nose up), the share of it in use, the g it gives, the lift direction it was measured along (local).</summary>
    public bool StallFresh; public int StallState; public float StallMarginDeg, StallLiftInUse, StallG; public Vector3 LiftDir;
    /// <summary>The flow (local unit) the stall evaluation was made at: the protection counts the turn since (it runs a few
    /// times a second; the ship turns between).</summary>
    public Vector3 StallFlow;
    /// <summary>The ship's lift and drag over the angle of attack at the speed and air now (the stall sweep's; null: none) - the
    /// flight performance (FlightPerf.Evaluate; FlightDirector.Perf) and a lift-flown acceleration goal read it.</summary>
    public PerfSamples Perf;
    /// <summary>The predicted loss of control (Hud/Loac.cs) at the autopilot's probe attitude (ApCommand.Probe): it ran,
    /// the worst need / reach on the way there, and holding the attitude now; the attitude it was predicted for.</summary>
    public bool LoacFresh; public float LoacProbe, LoacHere; public Quaternion LoacProbeAt;
    /// <summary>The speed limit (m/s; 0 none) - the world's, or the ship's own design limit.</summary>
    public float SpeedLimit;
    /// <summary>The ship's nose (local unit; zero: not known - the cockpit's forward, -Z) and its fixed forward guns (2026-10-19): their
    /// common firing axis (local unit; zero: none found), their projectile's muzzle speed (m/s; 0 unknown) and range (m; 0 unknown).
    /// SE2 4.0's projectiles fly straight (no drop) at the muzzle speed plus the shooter's velocity.</summary>
    public Vector3 Nose, GunAxis; public float MuzzleSpeed, GunRange;
}

/// <summary>
/// What the autopilot writes - only what a seated pilot writes: the target attitude (their reticle), the movement keys
/// (local, -1..1 per axis) and the dampener switch. NO force, torque, velocity or position ever (the user's rule: no cheats -
/// the ship flies through its real thrusters, gyros, rotors and control surfaces, ThrustTorque).
/// </summary>
public struct ApCommand
{
    public bool Flying; public Quaternion Target;
    public bool SetMove; public Vector3 Move;
    public bool Dampeners;
    /// <summary>What the protection changed this tick, and the attitude it wants the loss of control predicted at next.</summary>
    public ApProt Prot; public Quaternion Probe; public bool HasProbe;
}

/// <summary>Small rotation helpers (engine-free).</summary>
public static class ApMath
{
    public const float Deg = MathF.PI / 180f;

    /// <summary>The orientation that takes the local unit axes X, Y, Z onto the world vectors x, y, z (orthonormal, right-handed).</summary>
    public static Quaternion FromBasis(Vector3 x, Vector3 y, Vector3 z)
    {
        // (rotation matrix columns x, y, z -> quaternion: Shepperd's method)
        float m00 = x.X, m10 = x.Y, m20 = x.Z, m01 = y.X, m11 = y.Y, m21 = y.Z, m02 = z.X, m12 = z.Y, m22 = z.Z;
        float tr = m00 + m11 + m22; Quaternion q;
        if (tr > 0f) { float s = MathF.Sqrt(tr + 1f) * 2f; q = new Quaternion((m21 - m12) / s, (m02 - m20) / s, (m10 - m01) / s, 0.25f * s); }
        else if (m00 > m11 && m00 > m22) { float s = MathF.Sqrt(1f + m00 - m11 - m22) * 2f; q = new Quaternion(0.25f * s, (m01 + m10) / s, (m02 + m20) / s, (m21 - m12) / s); }
        else if (m11 > m22) { float s = MathF.Sqrt(1f + m11 - m00 - m22) * 2f; q = new Quaternion((m01 + m10) / s, 0.25f * s, (m12 + m21) / s, (m02 - m20) / s); }
        else { float s = MathF.Sqrt(1f + m22 - m00 - m11) * 2f; q = new Quaternion((m02 + m20) / s, (m12 + m21) / s, 0.25f * s, (m10 - m01) / s); }
        return Quaternion.Normalize(q);
    }

    /// <summary>The orientation taking local a onto world aW and local b (across a) onto world bW (across aW).</summary>
    public static Quaternion Frame(Vector3 a, Vector3 b, Vector3 aW, Vector3 bW)
        => Quaternion.Normalize(FromBasis(aW, bW, Vector3.Cross(aW, bW)) * Quaternion.Inverse(FromBasis(a, b, Vector3.Cross(a, b))));

    /// <summary>The angle between two orientations (rad, 0..pi).</summary>
    public static float Angle(Quaternion a, Quaternion b) => 2f * MathF.Acos(MathF.Min(1f, MathF.Abs(Quaternion.Dot(Quaternion.Normalize(a), Quaternion.Normalize(b)))));

    /// <summary>The unit axis direction (+-X, Y, Z) of the most thrust (the side's maximum, N, out): the way the ship pushes hardest.</summary>
    public static Vector3 MainAxis(Vector3 pos, Vector3 neg, out float force)
    {
        float best = pos.Y; Vector3 a = Vector3.UnitY;   // (ties: up first)
        if (pos.X > best * 1.0001f) { best = pos.X; a = Vector3.UnitX; }
        if (neg.X > best * 1.0001f) { best = neg.X; a = -Vector3.UnitX; }
        if (neg.Y > best * 1.0001f) { best = neg.Y; a = -Vector3.UnitY; }
        if (pos.Z > best * 1.0001f) { best = pos.Z; a = Vector3.UnitZ; }
        if (neg.Z > best * 1.0001f) { best = neg.Z; a = -Vector3.UnitZ; }
        force = best;
        return a;
    }

    /// <summary>The thrust maximum (N) along a local unit axis direction (one of the six).</summary>
    public static float Along(Vector3 pos, Vector3 neg, Vector3 d)
        => MathF.Max(0f, d.X) * pos.X + MathF.Max(0f, -d.X) * neg.X + MathF.Max(0f, d.Y) * pos.Y + MathF.Max(0f, -d.Y) * neg.Y + MathF.Max(0f, d.Z) * pos.Z + MathF.Max(0f, -d.Z) * neg.Z;

    /// <summary>A local axis across the unit axis a: the cockpit's forward (-Z) unless a is along Z, then up (+Y).</summary>
    public static Vector3 Across(Vector3 a) => MathF.Abs(a.Z) < 0.5f ? Vector3.Normalize(-Vector3.UnitZ - a * -a.Z) : Vector3.Normalize(Vector3.UnitY - a * a.Y);

    /// <summary>v's part across the unit n.</summary>
    public static Vector3 Flat(Vector3 v, Vector3 n) => v - n * Vector3.Dot(v, n);

    /// <summary>v at most len long.</summary>
    public static Vector3 ClampLen(Vector3 v, float len) { float l = v.Length(); return l > len && l > 0f ? v * (len / l) : v; }
}

/// <summary>
/// THE PREDICTED LOSS OF CONTROL's arithmetic (Hud/Loac.cs EvaluateLoac, engine-free so the autopilot's offline sims run the
/// same numbers): per local axis, the torque needed to hold an attitude (the air's torque there with the surfaces at their trim,
/// to cancel) over what can give it - the gyros' and thrusters' reach and the control surfaces' travel that way.
/// </summary>
public static class LoacMath
{
    /// <summary>The worst need / reach over the three axes for the air's torque 'need' (local N m, the surfaces' trim in).</summary>
    public static float Worst(Vector3 need, Vector3 reach, Vector3 surfPush, Vector3 surfPull)
    {
        float w = Axis(need.X, reach.X + (need.X < 0f ? surfPush.X : surfPull.X));
        w = MathF.Max(w, Axis(need.Y, reach.Y + (need.Y < 0f ? surfPush.Y : surfPull.Y)));
        return MathF.Max(w, Axis(need.Z, reach.Z + (need.Z < 0f ? surfPush.Z : surfPull.Z)));
    }

    /// <summary>One axis: what it needs over what it has - none to hold, nothing; none to give, everything (10).</summary>
    public static float Axis(float need, float have) => MathF.Abs(need) < 1f ? 0f : have <= 1f ? 10f : MathF.Abs(need) / have;
}

/// <summary>What an external goal source gives (ApExternal.Kind).</summary>
public enum ApExternalKind : byte
{
    None,
    /// <summary>A place to hold and its velocity (world, the centre of mass; fed forward) - a hover's formation slot, a moving
    /// landing pad, a waypoint held. Needs ApGoal.Hover (its height held there too).</summary>
    Track,
    /// <summary>An acceleration to fly (world m/s2, the gravity NOT in it - the autopilot adds what holds against it): the main
    /// thrust pointed along it, the throttle for it.</summary>
    Accel,
    /// <summary>An attitude to fly (world), with a throttle.</summary>
    Attitude,
}

/// <summary>An external goal for one tick (navigation legs, formation keeping, a landing pad - later items; their protection, the
/// attitude law and the allocator are the autopilot's own).</summary>
public struct ApExternal
{
    public ApExternalKind Kind;
    public Vector3D Position; public Vector3 Velocity;
    public Vector3 Accel;
    public Quaternion Attitude;
    /// <summary>The throttle along the main thrust (0..1; NaN: the autopilot's own for Accel, the pilot's for Attitude).</summary>
    public float Throttle;
    /// <summary>(2026-10-19) Track: the heading the nose is turned to (world; zero: the one held), the most speed the gap is closed at
    /// (m/s; 0: the hover speed), the place's own acceleration (Accel, fed forward). A Track without the hover goal is flown
    /// SIX-AXIS: translated on the movement keys, no lean, the attitude level on the heading (or aimed).</summary>
    public Vector3 Heading; public float MaxSpeed;
    /// <summary>AIM (any kind): this local axis turned toward this world direction - the share Blend (0 none .. 1 all) of the way
    /// from the attitude the goal flies (the lift's, the hover's lean, six-axis level) to the aimed one; the protection still clips.</summary>
    public Vector3 AimAxis, AimDir; public float Blend;
}

/// <summary>An external goal source, asked every tick while ApGoal.External is held. False: done (the goal lets go: GoalDone).</summary>
public interface IApGoalSource
{
    bool Step(in ApSense s, ref ApExternal goal);
}

/// <summary>
/// TRANSLATION AUTHORITY per local side (engine-free): how cleanly the thrusters pushing that way move the ship without turning it
/// - their summed maximum's torque over its force is the arm it acts at; 0 m: through the centre of mass (a VTOL's side thrusters,
/// a pair of tails), past ArmLimit: none (a single tail rotor - its push turns the ship, and the attitude law keeps it for the yaw).
/// 1 full, 0 none. The hover's movement keys go only where there is some; elsewhere the lean does the work.
/// </summary>
public static class SideAuthority
{
    public const float ArmLimit = 2f;
    /// <summary>arms: torque per newton (local), dirs: push (local unit), caps (N), rev: both ways (null none); out per side 0..1.</summary>
    public static void Of(Vector3[] arms, Vector3[] dirs, float[] caps, bool[] rev, int n, out Vector3 pos, out Vector3 neg)
    {
        Span<float> f = stackalloc float[6];
        Span<Vector3> t = stackalloc Vector3[6];
        for (int i = 0; i < n && i < arms.Length && i < dirs.Length && i < caps.Length; i++)
        {
            float c = caps[i]; if (!(c > 0f)) continue;
            int k = ThrustAllocatorDir(dirs[i]);
            f[k] += c; t[k] += arms[i] * c;
            if (rev != null && i < rev.Length && rev[i]) { int o = k ^ 1; f[o] += c; t[o] -= arms[i] * c; }
        }
        Span<float> a = stackalloc float[6];
        for (int k = 0; k < 6; k++) a[k] = f[k] > 1f ? Math.Clamp(1f - t[k].Length() / f[k] / ArmLimit, 0f, 1f) : 0f;
        pos = new Vector3(a[0], a[2], a[4]); neg = new Vector3(a[1], a[3], a[5]);
    }
    /// <summary>+X 0, -X 1, +Y 2, -Y 3, +Z 4, -Z 5 (ThrustAllocator.DirIndex's order).</summary>
    static int ThrustAllocatorDir(Vector3 d)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        if (ax >= ay && ax >= az) return d.X >= 0f ? 0 : 1;
        if (ay >= az) return d.Y >= 0f ? 2 : 3;
        return d.Z >= 0f ? 4 : 5;
    }
}
