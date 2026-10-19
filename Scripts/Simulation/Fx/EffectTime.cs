#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;

namespace AeroMod;

/// <summary>
/// An effect's time held fixed (the exhaust strength, the plasma / vapour hold, a thruster flame's length).
///
/// SE2 4.0 dropped ParticleEffectHandle.FixEffectTime(bool, TimeSpan), which the mod used for all of these. Its render half
/// lives on as ParticleEffectEntity.SetParameter(p): the render side sets ShouldFixEffectTime and FixedEffectTime = p x the
/// effect's EffectDuration (Render12 BaseParticleEffectEntityComponent.SetParameter) - exactly what FixEffectTime(true, t)
/// set - for any effect, parameter-driven or not. (The game's own ParameterDrivenParticleEffectHandle.SetParameter is the
/// same call, but only parameter-driven definitions may be spawned as such - a mismatch asserts.) So: the handle's
/// ParticleEffectEntity (a struct of VRage.Render, which mods do not reference) and its ResolvedDefinition's EffectDuration,
/// bound ONCE as typed open delegates over the runtime types (Binder&lt;H, E, D&gt;, made generic by reflection): no boxing,
/// no argument array, no garbage per call (docs/REFLECTION_AND_WHITELIST.md, "EffectTime").
/// </summary>
internal static class EffectTime
{
    const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly object _lock = new object();
    static volatile Binder _binder;
    static volatile bool _failed;
    /// <summary>"ok", or what was not found.</summary>
    public static string Why = "not bound";

    abstract class Binder
    {
        public abstract void Fix(object handle, float seconds);
        public abstract void Parameter(object handle, float p);
    }
    delegate void RefParam<E>(ref E entity, float p) where E : struct;
    sealed class Binder<H, E, D> : Binder where H : class where E : struct where D : class
    {
        readonly Func<H, E> _entity; readonly RefParam<E> _set; readonly Func<H, D> _resolved; readonly Func<D, float> _duration;
        public Binder(MethodInfo entity, MethodInfo set, MethodInfo resolved, MethodInfo duration)
        {
            _entity = (Func<H, E>)entity.CreateDelegate(typeof(Func<H, E>));
            _set = (RefParam<E>)set.CreateDelegate(typeof(RefParam<E>));
            _resolved = (Func<H, D>)resolved.CreateDelegate(typeof(Func<H, D>));
            _duration = (Func<D, float>)duration.CreateDelegate(typeof(Func<D, float>));
        }
        public override void Fix(object handle, float seconds)
        {
            var h = (H)handle;
            var d = _resolved(h);
            float dur = d != null ? _duration(d) : 0f;
            var e = _entity(h);
            _set(ref e, dur > 0f ? seconds / dur : 0f);
        }
        public override void Parameter(object handle, float p)
        {
            var e = _entity((H)handle);
            _set(ref e, p);
        }
    }

    /// <summary>Bound (once); false: the API is not there (Why says what) - callers then leave the effect's time alone.</summary>
    public static bool Ready => _binder != null || (!_failed && Bind());

    /// <summary>The effect's time fixed at this many seconds of its timeline (as FixEffectTime(true, t) did).</summary>
    public static void Fix(object handle, float seconds)
    {
        var b = _binder;
        if (b == null) { if (_failed || !Bind()) return; b = _binder; }
        b.Fix(handle, seconds);
    }

    /// <summary>The effect's time fixed at this fraction (0..1) of its timeline (a thruster flame's scale).</summary>
    public static void Parameter(object handle, float p)
    {
        var b = _binder;
        if (b == null) { if (_failed || !Bind()) return; b = _binder; }
        b.Parameter(handle, p);
    }

    static bool Bind()
    {
        lock (_lock)
        {
            if (_binder != null) return true;
            if (_failed) return false;
            try
            {
                Type ht = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    if (a.GetName().Name == "VRage.Game.Client") { ht = a.GetType("Keen.VRage.Game.Client.Effects.Components.ParticleEffectHandle"); break; }
                var entityProp = ht?.GetProperty("ParticleEffectEntity", AnyInst);
                var et = entityProp?.PropertyType;
                var set = et?.GetMethod("SetParameter", AnyInst, null, new[] { typeof(float) }, null);
                var resolved = ht?.GetProperty("ResolvedDefinition", AnyInst);
                var duration = resolved?.PropertyType.GetProperty("EffectDuration", AnyInst);
                if (ht == null || ht.IsValueType || entityProp?.GetGetMethod() == null || et == null || !et.IsValueType || set == null
                    || resolved?.GetGetMethod() == null || resolved.PropertyType.IsValueType || duration?.GetGetMethod(true) == null || duration.PropertyType != typeof(float))
                {
                    Why = $"not found (handle {ht != null}, ParticleEffectEntity {entityProp != null}, SetParameter {set != null}, ResolvedDefinition {resolved != null}, EffectDuration {duration != null})";
                    _failed = true;
                }
                else
                {
                    _binder = (Binder)typeof(Binder<,,>).MakeGenericType(ht, et, resolved.PropertyType).GetConstructors()[0]
                        .Invoke(new object[] { entityProp.GetGetMethod(), set, resolved.GetGetMethod(), duration.GetGetMethod(true) });
                    Why = "ok";
                }
            }
            catch (Exception e) { _failed = true; Why = "binding: " + (e.InnerException ?? e).Message; }
            if (_failed) Keen.VRage.Library.Diagnostics.Log.Default?.Warning("[AERO] effect time: " + Why + " - effects run on their own clock");
            return _binder != null;
        }
    }
}
