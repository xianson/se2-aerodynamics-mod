#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// THE TERRAIN the autopilot can see (engine-free). In game: the physics' rays against the planet's voxels and every grid but the
/// ship itself (Autopilot/Game/GridTerrain.cs - rate-limited, its own buffers, no allocation); offline: Tests/AutopilotSim's
/// terrain models (flat, slopes, ridges, a cliff, a canyon, a dumped heightfield). The autopilot asks only what a pilot sees out
/// of the window: the ground under it and what is ahead along its path.
/// </summary>
public interface ITerrainQuery
{
    /// <summary>The ground below p along 'down' (world unit): its distance and its normal (world, pointing up). False: none
    /// within reach (or not known yet).</summary>
    bool Below(Vector3D p, Vector3 down, float reach, out float height, out Vector3 normal);
    /// <summary>The first terrain (or other grid) along the ray from p: dir (world unit) x length - its distance and normal.
    /// False: nothing within length (or not known yet).</summary>
    bool Cast(Vector3D p, Vector3 dir, float length, out float distance, out Vector3 normal);
}

/// <summary>
/// THE AUTOPILOT'S LOOK AT THE TERRAIN, each tick (engine-free): the ground below (height, normal - filtered for a hold, raw for
/// the floor) and a look ahead along the path - the velocity, and the path bent by the acceleration it flies now (two rays,
/// alternating) - as a time to impact. The rays are the caller's (ITerrainQuery) at the cadence asked here: below every
/// BelowEvery ticks, ahead every AheadEvery (sooner when close). Allocation-free.
/// </summary>
public sealed class TerrainWatch
{
    public const int BelowEvery = 3, AheadEvery = 6;
    /// <summary>The ground ray's reach (m) and the look ahead's time (s) and longest ray (m).</summary>
    public const float BelowReach = 2000f, AheadSeconds = 10f, AheadMax = 2000f, AheadMin = 30f;
    /// <summary>The filtered height's time constant (s): a height hold over rough ground follows its shape, not its noise.</summary>
    public const float HoldTau = 0.6f;
    // (the ground's own height along the up, filtered - the hold's height is the ship's height now over it: the ship's motion
    //  seen at once, only the terrain's shape smoothed - a complementary filter)
    double _groundUp; bool _hasGroundUp; int _sinceBelow;

    int _belowIn, _aheadIn, _aheadWhich;
    /// <summary>The ground below: known, its height (raw, m), the hold's filtered height, its normal (world).</summary>
    public bool HasGround; public float Agl = -1f, AglHold = -1f; public Vector3 Normal = Vector3.UnitY;
    /// <summary>The ground's rise under the ship's horizontal motion now (m/s) - a terrain-following hold's feed-forward.</summary>
    public float GroundRate;
    /// <summary>Ahead: the time to impact along the velocity (s; +inf: clear), along the bent path, and the nearer.</summary>
    public float TtiStraight = float.PositiveInfinity, TtiPath = float.PositiveInfinity;
    public float Tti => MathF.Min(TtiStraight, TtiPath);
    /// <summary>The hit ahead (distance m, normal) of the nearer.</summary>
    public float AheadDist = float.PositiveInfinity; public Vector3 AheadNormal;
    public int Casts;

    public void Reset() { _belowIn = _aheadIn = 0; HasGround = false; _hasGroundUp = false; Agl = AglHold = -1f; TtiStraight = TtiPath = float.PositiveInfinity; AheadDist = float.PositiveInfinity; }

    /// <summary>One tick: p the ship's centre (world), vel (world m/s), accel the acceleration it flies now (world m/s2, its
    /// guidance's), down (world unit, the gravity's way), the ship's own extent below its centre (m: the height is to its belly).</summary>
    public void Step(ITerrainQuery t, Vector3D p, Vector3 vel, Vector3 accel, Vector3 down, float belly, float dt)
    {
        if (t == null) return;
        _sinceBelow++;
        if (--_belowIn <= 0)
        {
            _belowIn = Agl >= 0f && Agl < 50f ? 1 : BelowEvery;
            int since = _sinceBelow; _sinceBelow = 0;
            Casts++;
            if (t.Below(p, down, BelowReach, out float h, out Vector3 n))
            {
                float agl = MathF.Max(0f, h - belly);
                double hp = -Vector3D.Dot(p, (Vector3D)down), gnd = hp - agl;
                _groundUp = _hasGroundUp ? _groundUp + (gnd - _groundUp) * Math.Min(1.0, dt * since / HoldTau) : gnd; _hasGroundUp = true;
                Agl = agl; Normal = n; HasGround = true;
            }
            else { HasGround = false; Agl = AglHold = -1f; _hasGroundUp = false; }
        }
        // (the ground's rise under the ship as it moves: its slope - from the normal - along the horizontal velocity, m/s)
        GroundRate = 0f;
        if (HasGround) { float nu = -Vector3.Dot(Normal, down); if (nu > 0.2f) { Vector3 nf = Normal + down * nu, vh = vel - down * Vector3.Dot(vel, down); GroundRate = -Vector3.Dot(nf, vh) / nu; } }
        if (HasGround && _hasGroundUp)
        {
            double hp = -Vector3D.Dot(p, (Vector3D)down);
            AglHold = (float)Math.Max(0.0, hp - _groundUp);
        }
        float speed = vel.Length();
        if (speed < 1f) { TtiStraight = TtiPath = float.PositiveInfinity; AheadDist = float.PositiveInfinity; return; }
        if (--_aheadIn <= 0)
        {
            _aheadIn = Tti < 3f ? 1 : AheadEvery;
            Casts++;
            float len = Math.Clamp(speed * AheadSeconds, AheadMin, AheadMax);
            Vector3 dir = vel / speed;
            bool path = (_aheadWhich++ & 1) == 1 && accel.LengthSquared() > 0.25f;
            if (path)
            {
                // (the path bent: where the acceleration flown now takes the ship in half the look ahead, from here)
                float th = 0.5f * len / speed;
                Vector3 d = vel * th + accel * (0.5f * th * th);
                if (d.LengthSquared() > 1f) dir = Vector3.Normalize(d);
            }
            float tti = float.PositiveInfinity;
            if (t.Cast(p, dir, len, out float dist, out Vector3 nn))
            {
                float along = MathF.Max(1f, speed * MathF.Max(0.2f, Vector3.Dot(dir, vel / speed)));
                tti = MathF.Max(0f, dist - belly) / along;
                if (tti < Tti || dist < AheadDist) { AheadDist = dist; AheadNormal = nn; }
            }
            if (path) TtiPath = tti; else TtiStraight = tti;
            if (float.IsPositiveInfinity(Tti)) AheadDist = float.PositiveInfinity;
        }
    }
}
