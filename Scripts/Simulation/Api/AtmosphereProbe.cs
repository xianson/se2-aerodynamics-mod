#pragma warning disable
using System;
using System.Reflection;
namespace AeroMod;

/// <summary>
/// The air at any world point, for the API (AeroApi.TryGetAtmosphereAt): SE2 only hands a SIMULATED entity its air
/// (AirData.Density, written by the atmosphere generators each tick); a point in space has none. So this re-does the
/// engine's sum (AtmosphereGeneratorComponent.AirDataOperations): every generator adds its density inside its constant
/// distance, then linearly less out to its affect distance. The generators are listed on the simulation thread (every
/// ~10 s, from the session the aero grids live in) into an immutable snapshot the API reads from any thread.
///
/// REFLECTION (docs/REFLECTION_AND_WHITELIST.md §3.9): the generator's density is on its private _definition
/// (AtmosphereGeneratorDefinition.Density, public), its constant distance only in its protected
/// Serialize(AtmosphereGeneratorObjectBuilder) (the same route the Orbital Mod takes for gravity). Read once per
/// generator per refresh, never per tick. Unresolved: no generators, the point query answers false with Why.
/// </summary>
internal static class AtmosphereProbe
{
    /// <summary>Generators: centre (world), full-density radius, fade-out radius (m), density (kg/m3).</summary>
    static (Vector3D at, float constant, float affect, float density)[] _gens = Array.Empty<(Vector3D, float, float, float)>();
    static long _refreshAt;
    static Session _session;
    static FieldInfo _defField;
    static PropertyInfo _densityProp;
    static MethodInfo _serialize;
    static bool _resolved, _ok;
    /// <summary>Diagnostics: why the point query cannot answer ("" when it can).</summary>
    public static string Why = "no aero grid simulated yet";
    public static int Generators => _gens.Length;

    static readonly AtmosphereBridge _bridge = new AtmosphereBridge();

    /// <summary>Sim thread, every aero grid tick: cheap (a timestamp compare); the list again every ~10 s.</summary>
    internal static void Tick(AeroGridComponent aero)
    {
        // (the due time read plainly - a long is read whole on 64-bit: Interlocked.Read is a locked compare-exchange that took
        //  this shared cache line for writing from every server grid's tick on every thread - 1.6 ms/s of a profile, 2026-10-15;
        //  the refresh is still taken by one thread, the compare-exchange below)
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), due = System.Threading.Volatile.Read(ref _refreshAt);
        if (now < due) return;
        // (grid jobs may run in parallel: one of them takes the refresh)
        if (System.Threading.Interlocked.CompareExchange(ref _refreshAt, now + 10 * System.Diagnostics.Stopwatch.Frequency, due) != due) return;
        try
        {
            _session = aero.Entity?.GetSession() ?? _session;
            if (_session == null) return;
            if (!Resolve()) return;
            var l = new List<(Vector3D, float, float, float)>();
            foreach (var e in _session.GetEntitiesOfType<AtmosphereGeneratorComponent>())
            {
                var g = e.TryGet<AtmosphereGeneratorComponent>();
                if (g == null) continue;
                var def = _defField.GetValue(g);
                if (def == null) continue;
                float dens = (float)_densityProp.GetValue(def);
                var ob = new AtmosphereGeneratorObjectBuilder();
                _serialize.Invoke(g, new object[] { ob });
                float affect = g.AffectDistance, constant = MathF.Min(ob.ConstantAffectDistance, affect);
                if (dens <= 0f || affect <= 0f) continue;
                l.Add((e.Data.GetWorldTransform().Position, constant, affect, dens));
            }
            _gens = l.ToArray();
            Why = _gens.Length == 0 ? "no atmosphere generators in the world" : "";
        }
        catch (Exception ex) { Why = "generator read failed: " + ex.GetType().Name; }
    }

    static bool Resolve()
    {
        if (_resolved) return _ok;
        _resolved = true;
        var t = typeof(AtmosphereGeneratorComponent);
        _defField = t.GetField("_definition", BindingFlags.Instance | BindingFlags.NonPublic);
        _densityProp = _defField?.FieldType.GetProperty("Density", BindingFlags.Instance | BindingFlags.Public);
        _serialize = t.GetMethod("Serialize", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, new[] { typeof(AtmosphereGeneratorObjectBuilder) }, null);
        _ok = _defField != null && _densityProp != null && _serialize != null;
        if (!_ok) Why = "the engine's atmosphere generator changed (_definition / Density / Serialize not found)";
        return _ok;
    }

    /// <summary>The density at a world point (the engine's sum over generators, with the upper tail: AtmosphereTail).
    /// False: no generator list (Why).</summary>
    internal static bool DensityAt(in Vector3D p, out float density)
    {
        density = 0f;
        var gens = _gens;
        if (gens.Length == 0) return Why == "no atmosphere generators in the world";   // (a world without air: 0 is the answer)
        for (int i = 0; i < gens.Length; i++)
        {
            var g = gens[i];
            double d = Vector3D.Distance(p, g.at);
            if (d >= AtmosphereTail.Reach(g.constant, g.affect)) continue;
            density += AtmosphereTail.Density(d, g.constant, g.affect, g.density);   // (the game's law, with aero's upper tail)
        }
        return true;
    }

    /// <summary>The air aero flies a grid through: the game's density (AirData) or, where the generators' upper tails
    /// reach farther (AtmosphereTail), theirs - never less than the game's. Any thread.</summary>
    internal static float WithTail(in Vector3D p, float gameDensity)
    {
        if (!AtmosphereTail.Enabled || _gens.Length == 0) return gameDensity;
        return DensityAt(p, out float d) ? MathF.Max(gameDensity, d) : gameDensity;
    }

    /// <summary>The full state at a density (aero's own model: SE2 gives density only).</summary>
    internal static AtmosphereState State(float density) => _bridge.GetState(density);
}
