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

/// <summary>A terrain that sees far (the planet's analytic surface): TerrainWatch then looks further ahead and below.</summary>
public interface ILongRangeTerrain { bool LongRange { get; } }

/// <summary>THE PLANET'S SURFACE under a point (engine-free; in game the planet's own height query - Game/PlanetHeight.cs; offline a
/// mock): the planet's centre and the surface's distance from it along the line through p. lod: the detail asked (0 the finest;
/// each step halves it - far samples ask coarser). False: no planet here, or not known.</summary>
public interface IPlanetHeight { bool Surface(Vector3D p, int lod, out Vector3D centre, out double surfaceR); }

/// <summary>
/// THE TERRAIN FROM A PLANET'S HEIGHT (engine-free; ITerrainQuery over IPlanetHeight). Below: the height along the radial (the
/// gravity's line) and the normal from two more samples a span apart across it; Cast: marched along the ray (steps of a 48th of
/// its length, at least 10 m), the first sample under the surface bisected (6 halvings). Far samples ask a coarser detail (lod
/// by their distance from the ship). It knows only the planet's generated shape: what was dug or built is the rays' (CombinedTerrain).
/// </summary>
public sealed class PlanetTerrainQuery : ITerrainQuery
{
    public IPlanetHeight H;
    public int Samples;
    public PlanetTerrainQuery(IPlanetHeight h) { H = h; }

    /// <summary>The detail for a sample this far from the ship (m): the finest within 250 m, one step coarser each doubling, at most 4.</summary>
    public static int LodFor(double dist) => dist < 250 ? 0 : dist < 500 ? 1 : dist < 1000 ? 2 : dist < 2000 ? 3 : 4;

    bool Height(Vector3D q, int lod, out double above, out Vector3D c, out double r)
    {
        Samples++;
        above = 0; r = 0;
        if (H == null || !H.Surface(q, lod, out c, out r) || !(r > 0)) { c = default; return false; }
        above = (q - c).Length() - r;
        return true;
    }

    /// <summary>The surface's normal at the surface point under q (two samples 'span' m across the radial).</summary>
    Vector3 NormalAt(Vector3D q, Vector3D c, double r, int lod, float span)
    {
        Vector3D up = q - c; double len = up.Length();
        if (!(len > 1)) return Vector3.UnitY;
        up /= len;
        Vector3D a = Math.Abs(up.X) < 0.9 ? new Vector3D(1, 0, 0) : new Vector3D(0, 1, 0);
        Vector3D t1 = Vector3D.Normalize(Vector3D.Cross(up, a)), t2 = Vector3D.Cross(up, t1);
        Vector3D s0 = c + up * r;
        Vector3D q1 = q + t1 * span, q2 = q + t2 * span;
        if (!Height(q1, lod, out _, out var c1, out double r1) || !Height(q2, lod, out _, out var c2, out double r2)) return (Vector3)up;
        Vector3D s1 = c1 + Vector3D.Normalize(q1 - c1) * r1, s2 = c2 + Vector3D.Normalize(q2 - c2) * r2;
        Vector3D n = Vector3D.Cross(s1 - s0, s2 - s0);
        double nl = n.Length();
        if (!(nl > 1e-6)) return (Vector3)up;
        n /= nl;
        if (Vector3D.Dot(n, up) < 0) n = -n;
        return (Vector3)n;
    }

    /// <summary>(2026-10-21) A point this far (m) under the generated surface is no ship's: the generated shape is not what is here
    /// (a basin below it, a dug hole) - "not known", the rays' answer stands. In game 2026-10-20 a ship over terrain lower than the
    /// planet's shape read 0 m over the ground (the height clipped to 0) and its terrain minimum climbed it hard; ahead, "a hit at
    /// 0 m" did the same.</summary>
    public const double UnderTolerance = 1.0;

    public bool Below(Vector3D p, Vector3 down, float reach, out float height, out Vector3 normal)
    {
        height = 0f; normal = -down;
        if (!Height(p, 0, out double above, out var c, out double r)) return false;
        if (above > reach || above < -UnderTolerance) return false;
        height = (float)Math.Max(0, above);
        normal = NormalAt(p, c, r, 0, Math.Clamp(height * 0.05f, 10f, 100f));
        return true;
    }

    public bool Cast(Vector3D p, Vector3 dir, float length, out float distance, out Vector3 normal)
    {
        distance = 0f; normal = -dir;
        if (!(length > 0f) || !Height(p, 0, out double a0, out var c, out double r)) return false;
        if (a0 < -UnderTolerance) return false;   // (under the generated shape: not what is here - the rays')
        if (a0 <= 0) { normal = NormalAt(p, c, r, 0, 10f); return true; }
        double step = Math.Max(10.0, length / 48.0), prev = 0;
        for (double t = step; t <= length + 1e-3; t += step)
        {
            var q = p + (Vector3D)dir * t;
            int lod = LodFor(t);
            if (!Height(q, lod, out double a, out c, out r)) return false;   // (left the planet's reach: not known)
            if (a <= 0)
            {
                double lo = prev, hi = t;
                for (int b = 0; b < 6; b++)
                {
                    double m = 0.5 * (lo + hi);
                    if (Height(p + (Vector3D)dir * m, lod, out double am, out _, out _) && am <= 0) hi = m; else lo = m;
                }
                distance = (float)hi;
                var hit = p + (Vector3D)dir * hi;
                normal = NormalAt(hit, c, r, lod, (float)Math.Clamp(step, 10, 100));
                return true;
            }
            prev = t;
        }
        return false;
    }
}

/// <summary>
/// THE RAYS AND THE PLANET TOGETHER (engine-free): the nearer of the two answers. The rays see grids, what was dug and built,
/// but only where the game built the colliders (near entities); the planet's height sees its generated shape at any range. A
/// miss of one is "not known" - the other's answer stands.
/// </summary>
public sealed class CombinedTerrain : ITerrainQuery, ILongRangeTerrain
{
    public ITerrainQuery Near, Far;
    /// <summary>The far source answered its last ground ask: the look ahead may reach far.</summary>
    public bool FarAlive;
    public CombinedTerrain(ITerrainQuery near, ITerrainQuery far) { Near = near; Far = far; }
    public bool LongRange => Far != null && FarAlive;

    public bool Below(Vector3D p, Vector3 down, float reach, out float height, out Vector3 normal)
    {
        height = 0f; normal = Vector3.UnitY;
        float h1 = 0f, h2 = 0f; Vector3 n1 = Vector3.UnitY, n2 = Vector3.UnitY;
        bool a = Near != null && Near.Below(p, down, reach, out h1, out n1);
        bool b = Far != null && Far.Below(p, down, reach, out h2, out n2);
        if (Far != null) FarAlive = b;
        if (a && (!b || h1 <= h2)) { height = h1; normal = n1; return true; }
        if (b) { height = h2; normal = n2; return true; }
        return false;
    }

    public bool Cast(Vector3D p, Vector3 dir, float length, out float distance, out Vector3 normal)
    {
        distance = 0f; normal = -dir;
        float d1 = 0f, d2 = 0f; Vector3 n1 = -dir, n2 = -dir;
        bool a = Near != null && Near.Cast(p, dir, length, out d1, out n1);
        bool b = Far != null && Far.Cast(p, dir, a ? d1 : length, out d2, out n2);
        if (a && (!b || d1 <= d2)) { distance = d1; normal = n1; return true; }
        if (b) { distance = d2; normal = n2; return true; }
        return false;
    }
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
    /// <summary>With a long-range terrain (the planet's height: ILongRangeTerrain) the ground below and the look ahead reach this far (m).</summary>
    public const float BelowReachLong = 20000f, AheadMaxLong = 6000f;
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
        bool far = t is ILongRangeTerrain lr && lr.LongRange;
        _sinceBelow++;
        if (--_belowIn <= 0)
        {
            _belowIn = Agl >= 0f && Agl < 50f ? 1 : BelowEvery;
            int since = _sinceBelow; _sinceBelow = 0;
            Casts++;
            if (t.Below(p, down, far ? BelowReachLong : BelowReach, out float h, out Vector3 n))
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
            float len = Math.Clamp(speed * AheadSeconds, AheadMin, far ? AheadMaxLong : AheadMax);
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
