#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>A target as a sensor gives it (world): where, how fast, its name, and an id the source keeps for it (another id: a new
/// target - its track starts again).</summary>
public struct ApTargetFix { public bool Valid; public Vector3D Pos; public Vector3 Vel; public string Name; public long Id; public float Radius; }

/// <summary>Where the pilot's target comes from (the grid's targeting, a GPS point, a named grid, another mod; offline: a SimTarget).
/// Asked once a tick on the simulation thread; no allocation.</summary>
public interface IApTargetSource { bool Get(in ApSense s, out ApTargetFix t); }

/// <summary>
/// THE TARGET'S TRACK (engine-free): the fix each tick, its acceleration estimated from its velocity's change (filtered - a
/// noisy fix shakes nothing), and the geometry the behaviours read: range, closing speed, the line of sight's rate, the angle off
/// its tail (0: we are behind it), its bearing from our nose.
/// </summary>
public sealed class ApTargetTrack
{
    public bool Valid; public Vector3D Pos; public Vector3 Vel, Acc; public string Name = ""; public long Id; public float Radius;
    /// <summary>Since the source last had it (s); a gap past LoseSeconds drops it.</summary>
    public float Lost; public const float LoseSeconds = 1.5f, AccTau = 0.6f;
    Vector3 _vPrev; bool _hasPrev;
    public float Range, Closing, LosRate, AngleOff;
    public Vector3 Los;
    /// <summary>(2026-10-19) The same target's motion estimated from its observed POSITIONS alone (ApMotionTrack: filtered position,
    /// velocity, acceleration, turn rate, energy, confidence and age) - the formation and the combat slots read this; the
    /// behaviours above keep the source's velocity.</summary>
    public readonly ApMotionTrack Motion = new();

    public void Clear() { Valid = false; _hasPrev = false; Acc = Vector3.Zero; Lost = 0f; Name = ""; Id = 0; Motion.Reset(); }

    public void Update(in ApTargetFix f, in ApSense s, float dt)
    {
        // (the observed-motion track: positions only; a new id starts it again)
        if (f.Valid && Valid && f.Id != Id) Motion.Reset();
        Motion.Step(f.Valid, f.Pos, dt, s.GravityTrue.LengthSquared() > 0f ? s.GravityTrue : s.Gravity);
        if (!f.Valid)
        {
            Lost += dt;
            if (Lost > LoseSeconds) Clear();
            else if (Valid) Pos += (Vector3D)(Vel * dt);   // (coasting through a short gap)
        }
        else
        {
            if (!Valid || f.Id != Id) { _hasPrev = false; Acc = Vector3.Zero; }
            if (_hasPrev) Acc += ((f.Vel - _vPrev) / MathF.Max(dt, 1e-3f) - Acc) * MathF.Min(1f, dt / AccTau);
            _vPrev = f.Vel; _hasPrev = true;
            Valid = true; Lost = 0f; Pos = f.Pos; Vel = f.Vel; Name = f.Name ?? ""; Id = f.Id; Radius = f.Radius;
        }
        if (!Valid) return;
        Vector3 r = (Vector3)(Pos - s.Pos), vr = Vel - s.Vel;
        Range = r.Length(); Los = Range > 1e-3f ? r / Range : Vector3.UnitX;
        Closing = -Vector3.Dot(vr, Los);
        LosRate = Range > 1e-3f ? Vector3.Cross(r, vr).Length() / (Range * Range) : 0f;
        float tv = Vel.Length();
        AngleOff = tv > 1f ? MathF.Acos(Math.Clamp(Vector3.Dot(Vel / tv, Los), -1f, 1f)) / ApMath.Deg : 0f;   // (0: the target flies away along our line of sight)
    }
}

/// <summary>
/// AIMING FIXED GUNS (engine-free): the lead point for a projectile fired at muzzle speed u along the aim plus the shooter's own
/// velocity (SE2 4.0: the shot inherits the ship's velocity and flies straight - no drop; a gravity drop gp is still taken if
/// given), against a target moving at its velocity and acceleration - the intercept time iterated (t = |R + Vr t + (At - gp) t^2/2| / u);
/// and how far the gun axis is off it. Allocation-free.
/// </summary>
public static class ApAim
{
    /// <summary>R: target - shooter (world m), vr: target velocity - shooter velocity, at: target acceleration (world), gp: the
    /// projectile's gravity (zero in SE2 4.0), u: muzzle speed (0: unknown - straight at the target). dir: the aim (unit), tof: s.</summary>
    public static bool Lead(Vector3 R, Vector3 vr, Vector3 at, Vector3 gp, float u, out Vector3 dir, out float tof)
    {
        float range = R.Length();
        dir = range > 1e-3f ? R / range : Vector3.UnitX; tof = 0f;
        if (!(u > 1f) || range < 1e-3f) return false;
        Vector3 ar = at - gp;
        float t = range / u;
        Vector3 p = R;
        for (int i = 0; i < 8; i++)
        {
            p = R + vr * t + ar * (0.5f * t * t);
            float tn = p.Length() / u;
            if (!float.IsFinite(tn) || tn > 60f) { dir = R / range; tof = range / u; return false; }
            if (MathF.Abs(tn - t) < 1e-4f) { t = tn; break; }
            t = tn;
        }
        p = R + vr * t + ar * (0.5f * t * t);
        float l = p.Length();
        if (l < 1e-3f) return false;
        dir = p / l; tof = t;
        return true;
    }

    /// <summary>The angle (deg) between the gun axis (world unit) and the aim.</summary>
    public static float ErrorDeg(Vector3 gunWorld, Vector3 aim) => MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(gunWorld), Vector3.Normalize(aim)), -1f, 1f)) / ApMath.Deg;

    /// <summary>In the cone: the error under the cone angle, or the miss it makes at the range under the target's radius.</summary>
    public static bool InCone(float errDeg, float coneDeg, float range, float radius)
        => errDeg <= coneDeg || (radius > 0f && range * MathF.Sin(errDeg * ApMath.Deg) <= radius);
}

/// <summary>
/// THE FLIGHT STYLE from the ship (engine-free): SIX-AXIS - thrust on every side, each enough to move it across (and up in
/// gravity), cleanly through the centre of mass; HELICOPTER - one lift axis that can hold the weight, not the nose (it leans to
/// move); AIRPLANE - the rest: the thrust along the nose, the wings hold it up. No gravity and not six-axis: six-axis anyway
/// (points its main thrust - the six-axis branch keys what it has).
/// </summary>
public static class ApStyleDetect
{
    /// <summary>A side counts for six-axis from this share of the main thrust; the lift axis must hold the weight x HoverMargin.</summary>
    public const float SideShare = 0.15f, HoverMargin = 1.15f, CleanSide = 0.5f;

    public static ApStyle Of(in ApSense s)
    {
        Vector3 main = ApMath.MainAxis(s.ThrustPos, s.ThrustNeg, out float mainF);
        float g = s.Gravity.Length(), weight = s.Mass * g;
        if (mainF < 1f) return g > 0.01f ? ApStyle.Airplane : ApStyle.SixAxis;
        bool every = true;
        for (int k = 0; k < 6; k++)
        {
            float f = Comp(k < 3 ? s.ThrustPos : s.ThrustNeg, k % 3);
            float auth = s.SidePos == Vector3.Zero && s.SideNeg == Vector3.Zero ? 1f : Comp(k < 3 ? s.SidePos : s.SideNeg, k % 3);
            if (f < SideShare * mainF || auth < CleanSide) { every = false; break; }
        }
        bool hover = g < 0.01f || mainF > HoverMargin * weight;
        if (every && hover) return ApStyle.SixAxis;
        if (g < 0.01f) return ApStyle.SixAxis;
        Vector3 nose = s.Nose.LengthSquared() > 0.5f ? s.Nose : -Vector3.UnitZ;
        bool alongNose = MathF.Abs(Vector3.Dot(main, nose)) > 0.7f;
        if (hover && !alongNose) return ApStyle.Helicopter;
        return ApStyle.Airplane;
    }

    static float Comp(Vector3 v, int i) => i == 0 ? v.X : i == 1 ? v.Y : v.Z;
}
