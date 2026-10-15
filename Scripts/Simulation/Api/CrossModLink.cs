#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;
using System.Threading;

namespace AeroMod;

/// <summary>
/// ONE RULE for finding another mod's type (engine-free: Tests/ApiTests). The assembly is the LAST loaded one that has the
/// type - a hot-reloaded mod loads after its stale copy, which stays loaded - looked for again whenever the number of loaded
/// assemblies changes (checked at most every <see cref="CheckSeconds"/>). <see cref="Generation"/> counts the times that
/// assembly changed (found, reloaded, gone): a binding keeps the generation it bound at and binds again when it moves.
/// The Orbital Mod's Api/AeroLink.cs is the same rule the other way.
///
/// (2026-10-15: OrbitalLink and AeroEntryFx.EntryStateAt each scanned every assembly on their own and, once bound, kept
///  that delegate for good - after an Orbital hot reload the railed entry's heat and the contact balance read the stale
///  copy, whose frames no longer move.)
/// </summary>
internal sealed class LastLoadedType
{
    readonly string _fullName;
    readonly Func<Assembly[]> _assemblies;
    readonly object _lock = new object();
    Type _type;
    int _count = -1, _gen;
    long _checkAt;
    public double CheckSeconds = 5.0;

    /// <param name="assemblies">The loaded assemblies (default: the app domain's; a test passes its own).</param>
    public LastLoadedType(string fullName, Func<Assembly[]> assemblies = null)
    {
        _fullName = fullName;
        _assemblies = assemblies ?? AppDomain.CurrentDomain.GetAssemblies;
    }

    /// <summary>Bumped each time the assembly with the type changes. Cheap between checks: a timestamp compare.</summary>
    public int Generation => GenerationAt(System.Diagnostics.Stopwatch.GetTimestamp());

    /// <summary>The type in the current assembly; null: not loaded. Read after <see cref="Generation"/>.</summary>
    public Type Type => Volatile.Read(ref _type);

    internal int GenerationAt(long now)
    {
        if (now >= Volatile.Read(ref _checkAt)) Refresh(now);
        return Volatile.Read(ref _gen);
    }

    void Refresh(long now)
    {
        // (one thread scans; the others go on with the current binding - every thread reaching the expiry queued on the lock
        //  while it ran AppDomain.GetAssemblies, 2026-10-15 review)
        if (!Monitor.TryEnter(_lock)) return;
        try
        {
            if (now < _checkAt) return;
            _checkAt = now + (long)(CheckSeconds * System.Diagnostics.Stopwatch.Frequency);
            var asms = _assemblies();
            if (asms.Length == _count) return;
            _count = asms.Length;
            Type found = null;
            foreach (var a in asms)
            {
                Type t;
                try { t = a.GetType(_fullName); } catch { continue; }   // (a dynamic or half-loaded assembly may throw)
                if (t != null) found = t;   // (the last one: the newest load)
            }
            if (ReferenceEquals(found, _type)) return;
            Volatile.Write(ref _type, found);
            Interlocked.Increment(ref _gen);
        }
        finally { Monitor.Exit(_lock); }
    }
}

/// <summary>
/// One static member of another mod's API, bound as a typed delegate T: looked up by its name AND parameter types (a later
/// overload can never make it ambiguous), in the type <see cref="LastLoadedType"/> finds; bound again at once when that
/// assembly changes; looked up again every few seconds while missing; dropped when a call throws and looked up
/// again after a wait - 10 s, doubling to 300 s a fault (2026-10-15: a throwing member bound again at once threw every tick,
/// ~200 ms an exception in SE2). Any thread: the races are benign (two threads may bind the same member twice). No
/// allocation per call; the status strings are made only when the binding changes.
/// </summary>
internal sealed class CrossModBinding<T> where T : Delegate
{
    readonly LastLoadedType _api;
    readonly string _name, _who;
    readonly Type[] _params;
    T _fn;
    int _gen = -1, _faults;
    long _retryAt;
    readonly Func<double> _retrySeconds;

    /// <param name="retrySeconds">The wait (s) between lookups while the member is missing, read at each lookup (a knob the
    /// harness may set); null: 5 s.</param>
    public CrossModBinding(LastLoadedType api, string apiName, string name, Type[] parameterTypes, Func<double> retrySeconds = null)
    {
        _api = api; _name = name; _params = parameterTypes; _who = apiName + "." + name; _retrySeconds = retrySeconds;
    }

    /// <summary>The bound delegate, or null (not there, not yet, or waiting after a fault); <paramref name="status"/> says
    /// why whenever that changes.</summary>
    public T Get(ref string status) => GetAt(System.Diagnostics.Stopwatch.GetTimestamp(), ref status);

    internal T GetAt(long now, ref string status)
    {
        int gen = _api.GenerationAt(now);
        if (gen != Volatile.Read(ref _gen))
        {
            // (the other mod loaded, reloaded or went: what was bound is the stale copy's - bind anew now, faults forgotten)
            Volatile.Write(ref _fn, null);
            Volatile.Write(ref _retryAt, 0L);
            _faults = 0;
            Volatile.Write(ref _gen, gen);
        }
        var fn = Volatile.Read(ref _fn);
        if (fn != null) return fn;
        if (now < Volatile.Read(ref _retryAt)) return null;
        Volatile.Write(ref _retryAt, now + (long)((_retrySeconds?.Invoke() ?? 5.0) * System.Diagnostics.Stopwatch.Frequency));
        var t = _api.Type;
        if (t == null) { status = _who + " not found (the mod is not loaded)"; return null; }
        MethodInfo mi;
        try { mi = t.GetMethod(_name, _params); } catch (Exception e) { status = _who + " lookup failed: " + e.GetType().Name; return null; }
        if (mi == null) { status = _who + " not found (an older version of the mod)"; return null; }
        // (the shape checked first, then the non-throwing CreateDelegate: null on a mismatch)
        fn = mi.IsStatic ? Delegate.CreateDelegate(typeof(T), mi, false) as T : null;
        if (fn == null) { status = _who + ": unexpected signature"; FaultWait(now); return null; }
        Volatile.Write(ref _fn, fn);
        status = "bound to " + _who;
        return fn;
    }

    /// <summary>The call threw: dropped, looked up again after the wait (10 s, doubling up to 300 s a fault).</summary>
    public void Fault(Exception e, ref string status)
    {
        status = _who + " threw: " + e.GetType().Name;
        Volatile.Write(ref _fn, null);
        FaultWait(System.Diagnostics.Stopwatch.GetTimestamp());
    }

    void FaultWait(long now)
    {
        int n = Interlocked.Increment(ref _faults);
        Volatile.Write(ref _retryAt, now + (long)(FaultSeconds(n) * System.Diagnostics.Stopwatch.Frequency));
    }

    /// <summary>The wait after the n-th fault in a row (s): 10, 20, 40 ... up to 300.</summary>
    internal static double FaultSeconds(int n) => Math.Min(10.0 * (1 << Math.Min(Math.Max(n, 1) - 1, 5)), 300.0);
}
