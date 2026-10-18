#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

// ONE SCENE PER THREAD (2026-10-15 scene audit, the biggest case): the design probe's callers - the Orbital Mod's build
// hints through AeroApi (the client's frame), the drag overlay, StructureFeel - read the paired SERVER grid's engine data
// (its transform, mass, gyros, thrusters, block boxes, joint search) from their own thread: the cross-thread read the
// game's DataGuard asserts on. Now each server grid's own step reads them into a DesignSnapshot while the probe asks
// (DesignWatch: a window pushed on by every ask, like ApiGridState.Watch), and the probe reads only snapshots. Engine-free
// (Tests/SceneTests runs the watch, the frames and the sums); DesignProbe.PublishDesign fills it.

/// <summary>One server grid's design figures as its own step read them (DesignProbe.PublishDesign): never changed once
/// published (a new one each refresh) - read by any thread without a lock.</summary>
internal sealed partial class DesignSnapshot
{
    /// <summary>Stopwatch ticks of the step it was taken on.</summary>
    public long At;
    public Vector3D Pos; public Quaternion Orient = Quaternion.Identity;
    /// <summary>The mass properties were readable (RigidBodyMassProperties): Mass (kg; 0 none) and Com (grid-local).</summary>
    public bool HasMass; public float Mass; public Vector3 Com;
    /// <summary>The gyros' maximum torque (N m; -1 unreadable).</summary>
    public float GyroMax = -1f;
    /// <summary>The thrusters (the grid's thruster list on that step): grid-local position and push direction, the maximum
    /// at sea level (N: live maximum x the class's sea-level efficiency) and the profile as applied (null: flat).</summary>
    public Vector3[] ThrPos = Array.Empty<Vector3>(), ThrDir = Array.Empty<Vector3>();
    public float[] ThrSeaMax = Array.Empty<float>();
    public ThrustProfile[] ThrProfile = Array.Empty<ThrustProfile>();
    /// <summary>Each thruster is reversible (ReversibleThrustComponent): it pushes either way along ThrDir - counted both ways
    /// (empty: none).</summary>
    public bool[] ThrRev = Array.Empty<bool>();

    /// <summary>The thrust along a direction at a Mach (N) and its centre (grid-local): each thruster pointing at least half
    /// that way, its sea-level maximum x its profile at the Mach x how much it points that way.</summary>
    public float ThrustAlong(Vector3 dirLocal, float mach, out Vector3 centre)
    {
        centre = default;
        var dHat = Vector3.Normalize(dirLocal);
        float sum = 0f; Vector3 moment = Vector3.Zero;
        for (int i = 0; i < ThrDir.Length; i++)
        {
            float along = Vector3.Dot(ThrDir[i], dHat);
            if (i < ThrRev.Length && ThrRev[i]) along = MathF.Abs(along);   // (reversible: its maximum either way)
            if (along < 0.5f) continue;
            var ap = ThrProfile[i];
            float prof = ap == null ? 1f : (float)ap.Evaluate(mach);
            float f = ThrSeaMax[i] * Math.Max(0f, prof) * along;
            sum += f; moment += ThrPos[i] * f;
        }
        if (sum > 0f) centre = moment / sum;
        return sum;
    }
}

/// <summary>A server grid's design watch: the probe pushes it on with each ask (Ask, any thread); the grid's own step
/// refreshes its snapshot while it is on (Due) - a grid nobody probes costs one read a tick. The first ask after a quiet
/// spell finds no fresh snapshot (Fresh: null - the probe answers as "not ready yet"); the next step publishes one.</summary>
internal sealed class DesignWatch
{
    /// <summary>The window after the last ask, the refresh period, the age a snapshot is still used at, and how long after a
    /// window opened a missing subgrid snapshot means "not ready" rather than "left out" (seconds).</summary>
    public const double WatchSeconds = 2.0, RefreshSeconds = 0.25, FreshSeconds = 1.0, SettleSeconds = 0.5;

    /// <summary>Stopwatch ticks: published while now is under Until; Since: when the current window opened.</summary>
    public long Until, Since;
    /// <summary>The last snapshot (null: none yet).</summary>
    public volatile DesignSnapshot Snap;

    /// <summary>Any thread: push the window on (a racing ask may lose by an ask's worth - the next pushes again).</summary>
    public void Ask(long now, long freq)
    {
        if (now >= System.Threading.Volatile.Read(ref Until)) System.Threading.Volatile.Write(ref Since, now);
        System.Threading.Volatile.Write(ref Until, now + (long)(WatchSeconds * freq));
    }

    /// <summary>The grid's own step: watched now.</summary>
    public bool On(long now) => now < System.Threading.Volatile.Read(ref Until);

    /// <summary>The grid's own step: a new snapshot is due (watched, and none yet or the last one RefreshSeconds old).</summary>
    public bool Due(long now, long freq)
    {
        if (!On(now)) return false;
        var s = Snap;
        return s == null || now - s.At >= (long)(RefreshSeconds * freq);
    }

    /// <summary>The snapshot if FreshSeconds young (else null: the grid stopped stepping, or the window just opened).</summary>
    public DesignSnapshot Fresh(long now, long freq)
    {
        var s = Snap;
        return s != null && now - s.At <= (long)(FreshSeconds * freq) ? s : null;
    }

    /// <summary>The window opened under SettleSeconds ago: a subgrid without a snapshot yet is coming, not missing.</summary>
    public bool Settling(long now, long freq) => now - System.Threading.Volatile.Read(ref Since) < (long)(SettleSeconds * freq);

    // ── the drag overlay's block boxes (O(blocks): read on the grid's own step only when the overlay asks for a shape) ──

    /// <summary>A shape's block boxes as the grid's step read them, with the overlay key they were asked for.</summary>
    public sealed class Boxes { public long Key; public List<(Vector3I, Vector3I)> List; }
    /// <summary>The key the overlay wants boxes for (any thread), the key last read (the step), and the boxes read.</summary>
    public long BoxesWant = long.MinValue, BoxesRead = long.MinValue;
    public Boxes BoxesPub;   // (Volatile / Interlocked: published by the step, let go by the overlay)

    /// <summary>The overlay: the boxes of the shape with this key - null until the grid's step has read them (asked here).</summary>
    public Boxes BoxesFor(long key)
    {
        var b = System.Threading.Volatile.Read(ref BoxesPub);
        if (b != null && b.Key == key) return b;
        System.Threading.Volatile.Write(ref BoxesWant, key);
        return null;
    }

    /// <summary>The grid's step: the key whose boxes are to be read now (false: none asked, or read already).</summary>
    public bool BoxesDue(out long key)
    {
        key = System.Threading.Volatile.Read(ref BoxesWant);
        return key != long.MinValue && key != BoxesRead;
    }

    /// <summary>The grid's step: publish the boxes read for key.</summary>
    public void PublishBoxes(long key, List<(Vector3I, Vector3I)> list) { BoxesRead = key; System.Threading.Volatile.Write(ref BoxesPub, new Boxes { Key = key, List = list }); }

    /// <summary>The overlay, its build done: the boxes let go (unless newer ones replaced them).</summary>
    public void ReleaseBoxes(Boxes b) { System.Threading.Interlocked.CompareExchange(ref BoxesPub, null, b); }
}

/// <summary>THE SHIP from snapshots: the aimed grid's (index 0) and each jointed member's, every figure turned into the
/// aimed grid's axes (WorldTransform's own rules: direction by the orientation, back by its inverse).</summary>
internal sealed partial class DesignShip
{
    public readonly List<DesignSnapshot> S = new();
    public long At;

    public Vector3 ToRootDir(int m, Vector3 d) => m == 0 ? d : Vector3.Transform(Vector3.Transform(d, S[m].Orient), Quaternion.Inverse(S[0].Orient));
    public Vector3 ToMemberDir(int m, Vector3 d) => m == 0 ? d : Vector3.Transform(Vector3.Transform(d, S[0].Orient), Quaternion.Inverse(S[m].Orient));
    public Vector3 ToRootPoint(int m, Vector3 p) => m == 0 ? p
        : Vector3.Transform((Vector3)(S[m].Pos + Vector3D.Transform((Vector3D)p, S[m].Orient) - S[0].Pos), Quaternion.Inverse(S[0].Orient));

    /// <summary>The ship's mass (kg) and centre of mass (the aimed grid's axes): members with mass only.</summary>
    public float Mass(out Vector3 com)
    {
        com = default; float total = 0f; Vector3 moment = Vector3.Zero;
        for (int m = 0; m < S.Count; m++)
        {
            var s = S[m];
            if (!s.HasMass || !(s.Mass > 0f)) continue;
            total += s.Mass; moment += ToRootPoint(m, s.Com) * s.Mass;
        }
        if (total > 0f) com = moment / total;
        return total;
    }

    /// <summary>The thrust along dirLocal (the aimed grid's axes) at a Mach and its centre: each member's thrusters along the
    /// direction turned into its axes, their centre back into the aimed grid's. One grid: its own sum as it is.</summary>
    public float ThrustAlong(Vector3 dirLocal, float mach, out Vector3 centre)
    {
        if (S.Count == 1) return S[0].ThrustAlong(dirLocal, mach, out centre);
        float sum = 0f; Vector3 moment = Vector3.Zero;
        for (int m = 0; m < S.Count; m++)
        {
            float f = S[m].ThrustAlong(ToMemberDir(m, dirLocal), mach, out var c);
            if (f > 0f) { sum += f; moment += ToRootPoint(m, c) * f; }
        }
        centre = sum > 0f ? moment / sum : default;
        return sum;
    }

    /// <summary>The torque the thrusters give about axis a through com (the aimed grid's axes) at their sea-level maxima:
    /// pos the sum of those turning it positively, neg of those turning it the other way (positive N m).</summary>
    public void ThrustHold(Vector3 a, Vector3 com, out float pos, out float neg)
    {
        pos = 0f; neg = 0f;
        for (int m = 0; m < S.Count; m++)
        {
            var s = S[m];
            for (int i = 0; i < s.ThrDir.Length; i++)
            {
                var r = ToRootPoint(m, s.ThrPos[i]) - com;
                float tq = Vector3.Dot(Vector3.Cross(r, ToRootDir(m, s.ThrDir[i]) * s.ThrSeaMax[i]), a);
                if (i < s.ThrRev.Length && s.ThrRev[i]) { pos += MathF.Abs(tq); neg += MathF.Abs(tq); }   // (reversible: both ways)
                else if (tq > 0f) pos += tq; else neg -= tq;
            }
        }
    }

    /// <summary>The gyros' maximum torque, every member's that has one (N m).</summary>
    public float GyroMax()
    {
        float g = 0f;
        foreach (var s in S) if (s.GyroMax > 0f) g += s.GyroMax;
        return g;
    }
}
