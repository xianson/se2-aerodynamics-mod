#pragma warning disable
using System;
using System.Threading;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// Where the aero mod's time goes, per second (thread-safe; jobs run in parallel): the block-change handler, the
/// sim job and the draw job, each its total and its worst single call. Logged as [AERO-COST] once a second.
/// </summary>
public sealed class AeroCost
{
    public static readonly AeroCost Patch = new AeroCost("patch");
    public static readonly AeroCost Blocks = new AeroCost("blocks"), Sim = new AeroCost("sim"), Draw = new AeroCost("draw"),
        Thrusters = new AeroCost("thrCache"), Gyros = new AeroCost("gyroCache"), Sched = new AeroCost("sched"),
        Flush = new AeroCost("flush"), Wings = new AeroCost("wings"), Compute = new AeroCost("compute"),
        Pre = new AeroCost("pre"), Thrust = new AeroCost("thrust"), Apply = new AeroCost("apply"),
        Begin = new AeroCost("bBegin"), Batch = new AeroCost("bBatch"), FinSurface = new AeroCost("fSurf"), FinWings = new AeroCost("fWings"),
        FinClassify = new AeroCost("fClass"), FinComponents = new AeroCost("fComp"), FinShadow = new AeroCost("fShadow"),
        TSetup = new AeroCost("tSetup"), TLoop = new AeroCost("tLoop"), TAtt = new AeroCost("tAtt"), TWrite = new AeroCost("tWrite");
    public static bool Log = false;
    /// <summary>Milliseconds since a Start() timestamp.</summary>
    public static double Ms(long t0) => (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
    public static volatile string RebuildAlloc = "-";   // (the Orbital Mod harness: aerocost on|off)
    readonly string _name;
    long _ticks, _worst, _calls;
    AeroCost(string name) { _name = name; }

    public static long Start() => System.Diagnostics.Stopwatch.GetTimestamp();

    // Which threads ran the sim job this second (are grids' jobs parallel?), and the most at once.
    static readonly System.Collections.Generic.HashSet<int> _threads = new();
    static int _inSim, _maxInSim;
    public static void EnterSim()
    {
        lock (_threads) _threads.Add(Environment.CurrentManagedThreadId);
        int n = Interlocked.Increment(ref _inSim), m;
        while (n > (m = Volatile.Read(ref _maxInSim)) && Interlocked.CompareExchange(ref _maxInSim, n, m) != m) { }
    }
    static int ThreadCount() { lock (_threads) return _threads.Count; }
    public static void ExitSim() => Interlocked.Decrement(ref _inSim);
    public static int SimServer, SimClient;

    // The grid whose thrust torque is largest this second (ThrustTorque's report).
    static AeroGridComponent _thr; static float _thrMag;
    internal static void WatchThrust(AeroGridComponent g)
    {
        var r = g.LastThrust;
        float m = r.OffsetTorque.Length() + r.RcsTorque.Length() + (r.Mode == "off" || r.Mode == "free" ? 0f : 1f) + r.Error.Length();
        if (m >= _thrMag || ReferenceEquals(g, _thr)) { _thr = g; _thrMag = m; }
    }
    static string Thr()
    {
        var g = _thr; _thr = null; _thrMag = 0f;
        if (g == null) return "thrust -";
        var r = g.LastThrust;
        return $"thrust '{g.Entity?.DebugName}' v={g.LastSpeed:F1} w={g.LastAngVel.Length():F3} {r.Mode} err={r.Error.Length() * 57.2958f:F2}deg offsetT={r.OffsetTorque.Length():F0} rcsT={r.RcsTorque.Length():F0} rcsF={r.RcsForce.Length():F0} use={r.RcsUse:P0} e=({r.Error.X:F3},{r.Error.Y:F3},{r.Error.Z:F3}) oT=({r.OffsetTorque.X:F0},{r.OffsetTorque.Y:F0},{r.OffsetTorque.Z:F0}) rT=({r.RcsTorque.X:F0},{r.RcsTorque.Y:F0},{r.RcsTorque.Z:F0}) wl=({LocalW(g).X:F4},{LocalW(g).Y:F4},{LocalW(g).Z:F4}) at {g.Entity?.Data.GetWorldTransform().Position}";
    }
    static Vector3 LocalW(AeroGridComponent g)
    {
        var wt = g.Entity.Data.GetWorldTransform();
        return WorldTransform.TransformDirectionInv(g.LastAngVel, wt);
    }

    // The busiest grid this second (most faces): is aero actually working on it?
    static AeroGridComponent _top; static float _topFaces;
    /// <summary>The heaviest grid (by mass: a grid's first build has no faces yet, and it is the big one that matters).</summary>
    internal static void Watch(AeroGridComponent g)
    {
        if (g.LastDensity <= 0f) return;   // (in air only: a station in space has no aero to watch)
        float f = g.LastMass;
        if (f >= _topFaces || ReferenceEquals(g, _top)) { _top = g; _topFaces = f; }
    }
    static string Top()
    {
        var g = _top; _top = null; _topFaces = 0;
        if (g == null) return "top -";
        return $"top grid {g.Entity?.DebugName} {g.LastMass / 1000f:F0} t faces={g.FacesNow} table={g.UsesTable} rebuilding={g.Rebuilding} v={g.LastSpeed:F0} d={g.LastDensity:F3} |F|={g.LastResult.Force.Length():F0}N hasResult={g.HasResult} {g.ShadowNote}";
    }

    public void Stop(long t0)
    {
        long d = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        Interlocked.Add(ref _ticks, d);
        Interlocked.Increment(ref _calls);
        long w;
        while (d > (w = Interlocked.Read(ref _worst)) && Interlocked.CompareExchange(ref _worst, d, w) != w) { }
    }

    string Take()
    {
        double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        long t = Interlocked.Exchange(ref _ticks, 0), w = Interlocked.Exchange(ref _worst, 0), c = Interlocked.Exchange(ref _calls, 0);
        return $"{_name} {t * f:F1}ms/s worst {w * f:F1}ms x{c}";
    }

    static long _next;
    public static void MaybeLog()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), n = Interlocked.Read(ref _next);
        if (now < n || Interlocked.CompareExchange(ref _next, now + System.Diagnostics.Stopwatch.Frequency, n) != n) return;
        if (Log) Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-COST] {Blocks.Take()} | {Patch.Take()} | {Sim.Take()} | {Draw.Take()} || {Thrusters.Take()} | {Gyros.Take()} | {Compute.Take()} | {Sched.Take()} | {Flush.Take()} | {Wings.Take()} || {Pre.Take()} | {Thrust.Take()} | {Apply.Take()} || {TSetup.Take()} | {TLoop.Take()} | {TAtt.Take()} | {TWrite.Take()} || caught {System.Threading.Interlocked.Exchange(ref PhysicsHack.Caught, 0)} || threads {ThreadCount()} concurrent {Interlocked.Exchange(ref _maxInSim, 0)} || {Top()} || {Thr()} || {ThrustTorque.ClientNote} calls server {Interlocked.Exchange(ref ThrustTorque.ServerCalls, 0)} client {Interlocked.Exchange(ref ThrustTorque.ClientCalls, 0)} flames lit {Interlocked.Exchange(ref ThrustTorque.FlameLit, 0)} matched {Interlocked.Exchange(ref ThrustTorque.FlameHits, 0)}/{Interlocked.Exchange(ref ThrustTorque.FlameLookups, 0)} grids {Interlocked.Exchange(ref SimServer, 0)}/{Interlocked.Exchange(ref SimClient, 0)} || {Begin.Take()} | {Batch.Take()} | {FinSurface.Take()} | {FinWings.Take()} | {FinClassify.Take()} | {FinComponents.Take()} | {FinShadow.Take()} || {RebuildAlloc}");
        lock (_threads) _threads.Clear();
    }
}
