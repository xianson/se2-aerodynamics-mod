#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>Something near the ship that it must not fly into: another grid, an asteroid, its own target (a sphere moving at a
/// velocity; world).</summary>
public struct ApObstacle { public Vector3D Pos; public Vector3 Vel; public float Radius; public bool Target; }

/// <summary>The obstacles around a ship (fixed capacity: no allocation; the game fills it every few ticks - ApGameObstacles; the
/// offline sim from its scene).</summary>
public sealed class ApObstacles
{
    public readonly ApObstacle[] Items = new ApObstacle[32];
    public int Count;
    public void Clear() => Count = 0;
    public bool Add(Vector3D pos, Vector3 vel, float radius, bool target = false)
    {
        if (Count >= Items.Length || !(radius > 0f)) return false;
        Items[Count++] = new ApObstacle { Pos = pos, Vel = vel, Radius = radius, Target = target };
        return true;
    }
}

/// <summary>
/// SMALL COLLISION AVOIDANCE (engine-free; FlightDirector applies it on top of every mode). Each tick: for every obstacle the
/// closest approach along the CURRENT relative velocity within the look-ahead (s) - no path planning; a conflict is a miss
/// closer than the clearance (the two radii and a gap: Margin plus half the ship's own size; for the ship's own target at most
/// half the behaviour's abort distance - an attack run breaks off by itself first). The most urgent one gets a sidestep: the
/// acceleration that opens the miss to the clearance by the time of closest approach (2 x shortfall / t^2, half again, at most
/// what the style can give), away from where the obstacle will be - straight at it: up (a pull-up), or level when the ground is
/// close. It holds ReleaseS after the last conflict, then lets go. The modes fly it through their own commands (an acceleration,
/// a track's place and velocity, a hover's speed) - the protection and the attitude law after it, as for any goal.
/// </summary>
public sealed class ApAvoid
{
    public const float Margin = 5f, ReleaseS = 1f, MinTca = 0.5f, LookMin = 1f, LookMax = 15f;
    /// <summary>Avoiding now; the push's direction (world unit) and size (m/s2), the clearance's shortfall (m), the time to the
    /// closest approach (s), the obstacle (its index; -1 the target, -2 none) and whether it is the target.</summary>
    public bool Active; public Vector3 Dir; public float Accel, Short, Tca = float.PositiveInfinity; public int Index = -2; public bool OnTarget;
    /// <summary>The nearest predicted miss this tick (m, past the radii; +inf none) - diagnostics.</summary>
    public float MinMiss = float.PositiveInfinity;
    public int Conflicts;
    float _hold;

    /// <summary>The clearance from the TARGET a behaviour allows at most (m): half its own closest distance - an attack run's abort
    /// distance, a hover above / below's height over it, the others' range (Auto: the least of them) - so the behaviour breaks off
    /// first and the avoidance only catches a run that would really hit it.</summary>
    public static float TargetClear(in ApPanel p, ApBehaviour active)
    {
        float abort = p.AbortM, hov = MathF.Abs(p.AltOffsetM), range = p.RangeM;
        float d = active switch
        {
            ApBehaviour.AttackRun or ApBehaviour.AttackRunUTurn => abort,
            ApBehaviour.HoverAbove or ApBehaviour.HoverBelow => hov,
            ApBehaviour.Auto => MathF.Min(abort, MathF.Min(range, MathF.Max(hov, 1f))),
            _ => range,
        };
        return 0.5f * MathF.Max(1f, d);
    }

    public void Reset() { Active = false; Accel = 0f; Short = 0f; Index = -2; _hold = 0f; Tca = float.PositiveInfinity; }

    /// <summary>One tick. pos / vel the ship's (world), shipR its radius (m), lookS the look-ahead (s), aMax the most the style can
    /// push sideways (m/s2), targetClearMax the clearance cap for the target (m; +inf none), up (world unit), agl (m; &lt; 0 not known).</summary>
    public void Step(ApObstacles obs, Vector3D pos, Vector3 vel, float shipR, float lookS, float aMax, float targetClearMax, Vector3 up, float agl, float dt)
        => Step(obs, default, false, pos, vel, shipR, lookS, aMax, targetClearMax, up, agl, dt);

    /// <summary>The time the style takes to build its push (s): an airplane banks into it first.</summary>
    public float React;

    /// <summary>... with the goal source's target as one more obstacle (the scan leaves the target out: its clearance is the behaviour's).</summary>
    public void Step(ApObstacles obs, in ApObstacle target, bool hasTarget, Vector3D pos, Vector3 vel, float shipR, float lookS, float aMax, float targetClearMax, Vector3 up, float agl, float dt)
    {
        float look = Math.Clamp(float.IsFinite(lookS) ? lookS : 4f, LookMin, LookMax);
        int best = -2; float bestT = float.PositiveInfinity, bestShort = 0f; Vector3 bestM = Vector3.Zero, bestVr = Vector3.Zero; bool bestTarget = false;
        MinMiss = float.PositiveInfinity;
        int n = obs?.Count ?? 0;
        for (int i = hasTarget ? -1 : 0; i < n; i++)
        {
            ApObstacle o = i < 0 ? target : obs.Items[i];
            if (i < 0) o.Target = true;
            Vector3 r = (Vector3)(o.Pos - pos);          // (the obstacle from the ship)
            Vector3 vr = vel - o.Vel;                     // (the ship's velocity relative to it)
            float need = Margin + 0.5f * MathF.Max(0f, shipR);
            if (o.Target && targetClearMax < need) need = MathF.Max(1f, targetClearMax);
            float radii = MathF.Max(0f, shipR) + o.Radius;
            float v2 = vr.LengthSquared();
            float t = v2 > 1e-4f ? Vector3.Dot(r, vr) / v2 : 0f;
            bool closing = t > 0f;
            t = Math.Clamp(t, 0f, look);
            Vector3 m = vr * t - r;                       // (the ship from the obstacle at the closest approach)
            float miss = m.Length() - radii;
            if (miss < MinMiss) MinMiss = miss;
            float now = r.Length() - radii;
            bool inside = now < need;
            if (!(miss < need) || (!closing && !inside)) continue;
            if (!closing) { m = -r; t = 0f; miss = now; }   // (already too near and not closing: open it, no hurry)
            if (t < bestT) { bestT = t; best = i; bestShort = need - miss; bestM = m; bestVr = vr; bestTarget = o.Target; }
        }
        Conflicts = best > -2 ? Conflicts + 1 : Conflicts;
        if (best == -2)
        {
            if (Active) { _hold -= dt; if (_hold <= 0f) { Active = false; Index = -2; Accel = 0f; } else Accel *= MathF.Max(0f, 1f - dt / ReleaseS); }
            Tca = float.PositiveInfinity;
            return;
        }
        // the way out: away from where it will be; straight at it - up (or level near the ground)
        Vector3 dir;
        float ml = bestM.Length();
        if (ml > 0.5f) dir = bestM / ml;
        else
        {
            float vl = bestVr.Length();
            Vector3 vh = vl > 1e-3f ? bestVr / vl : Vector3.Zero;
            dir = up - vh * Vector3.Dot(up, vh);
            dir = dir.LengthSquared() > 1e-4f ? Vector3.Normalize(dir) : ApMath.Across(vh);
        }
        float clearNeed = Margin + 0.5f * MathF.Max(0f, shipR);
        if (agl >= 0f && agl < 3f * clearNeed && Vector3.Dot(dir, up) < 0f)
        {
            Vector3 flat = dir - up * Vector3.Dot(dir, up);
            dir = flat.LengthSquared() > 1e-4f ? Vector3.Normalize(flat) : up;
        }
        float tt = MathF.Max(bestT - MathF.Max(0f, React), MinTca);
        float a = 1.5f * 2f * MathF.Max(0f, bestShort) / (tt * tt) + 0.5f;
        Active = true; _hold = ReleaseS; Index = best; OnTarget = bestTarget; Short = bestShort; Tca = bestT;
        // (a direction kept for a turn: a flip across the obstacle's line from one tick to the next would cancel itself)
        Dir = Vector3.Dot(dir, Dir) < -0.5f && Accel > 0f ? Dir : dir;
        Accel = MathF.Min(a, MathF.Max(0.5f, aMax));
    }
}
