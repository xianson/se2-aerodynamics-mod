#pragma warning disable
using System;
using System.Reflection;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Library.Definitions;

namespace AeroMod;

/// <summary>
/// THE AERO MOD'S ONLY REFLECTION: the game's particle effects (VRage.Game.Client, VRage.Render) are outside the
/// assemblies mod scripts compile against, so the entry plasma's calls are late-bound here, as the game's own
/// MeteorEffectsRenderComponent makes them: IParticleEffects.TrySpawnEffect(local transform, definition, user
/// parameters, the grid's IRenderParent), then the handle's UpdateTransform / SetParameters / FixEffectTime /
/// StopEmitting / DisposeAfterParticlesDie. Everything is resolved once (client); any failure is logged once and
/// turns the effect off - never thrown at a frame. No allocation per call beyond the reused argument arrays.
/// </summary>
public static class EntryFxBridge
{
    const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static bool _resolved, _ok;
    static Type _sessionCompType, _userParamsType, _renderCompType;
    static MethodInfo _spawn, _updateTransform, _setParameters, _fixTime, _stopEmitting, _disposeAfter;
    static ParameterInfo[] _spawnParams;
    static FieldInfo _fColor, _fGravity, _fScale, _fSize, _fVelMul, _fVelDir;
    static Definition _effect;
    public static string Problem = "";

    /// <summary>A live effect on one grid: the handle and its own (boxed) user parameters.</summary>
    public sealed class Fx
    {
        internal object Handle, Params;
        internal string Key;   // (AeroEntryFx.TestEffect it was spawned with)
        internal readonly object[] Arg1 = new object[1], Arg2 = new object[2];
    }

    static Type FindType(string asm, string name)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            if (a.GetName().Name == asm) return a.GetType(name);
        return null;
    }

    static void Fail(string why) { if (Problem.Length == 0) { Problem = why; Log.Default?.Info($"[AERO] entry plasma off: {why}"); } _ok = false; }

    static bool Resolve()
    {
        if (_resolved) return _ok;
        _resolved = true; _ok = true;
        try
        {
            _sessionCompType = FindType("VRage.Game.Client", "Keen.VRage.Game.Client.Effects.Components.ParticleEffectsSessionComponent");
            _renderCompType = FindType("VRage.Game.Client", "Keen.VRage.Game.Client.Render.RootEntityRenderComponent");
            _userParamsType = FindType("VRage.Render", "Keen.VRage.Render.Data.ParticleEffectUserParameters");
            var handleType = FindType("VRage.Game.Client", "Keen.VRage.Game.Client.Effects.Components.ParticleEffectHandle");
            if (_sessionCompType == null || _renderCompType == null || _userParamsType == null || handleType == null) { Fail("particle types not found"); return false; }
            foreach (var m in _sessionCompType.GetMethods(Inst))
                if (m.Name == "TrySpawnEffect") { var p = m.GetParameters(); if (p.Length > 0 && p[0].ParameterType == typeof(RelativeTransform)) { _spawn = m; _spawnParams = p; break; } }
            _updateTransform = handleType.GetMethod("UpdateTransform", Inst);
            _setParameters = handleType.GetMethod("SetParameters", Inst);
            _fixTime = handleType.GetMethod("FixEffectTime", Inst);
            _stopEmitting = handleType.GetMethod("StopEmitting", Inst);
            _disposeAfter = handleType.GetMethod("DisposeAfterParticlesDie", Inst);
            _fColor = _userParamsType.GetField("ColorMultiplier"); _fGravity = _userParamsType.GetField("GravityForce");
            _fScale = _userParamsType.GetField("EmitterScaleMultiplier"); _fSize = _userParamsType.GetField("EmitterSizeMultiplier");
            _fVelMul = _userParamsType.GetField("VelocityMultiplier"); _fVelDir = _userParamsType.GetField("VelocityDirection");
            if (_spawn == null || _updateTransform == null || _setParameters == null || _fixTime == null || _stopEmitting == null || _disposeAfter == null
                || _fScale == null || _fGravity == null || _fColor == null) { Fail("particle methods not found"); return false; }
            if (!DefinitionManager.Instance.TryGetDefinition(AeroEntryFx.EffectGuid, out _effect) || _effect == null) { Fail("the entry effect definition is not loaded"); return false; }
        }
        catch (Exception e) { Fail("resolve: " + e.Message); }
        return _ok;
    }


    /// <summary>The grid's render parent (a client grid's GridRenderComponent, a RootEntityRenderComponent), or null.</summary>
    public static object RenderParent(Entity grid)
    {
        if (!Resolve() || grid == null) return null;
        try
        {
            // (a grid's is the derived GridRenderComponent - component lookup is by exact type; other roots fall back)
            // (Entity.TryGet<T> matches the exact type; a grid's is the derived GridRenderComponent: any instance)
            foreach (var comp in grid.Components)
                if (comp != null && _renderCompType.IsInstanceOfType(comp)) return comp;
            return null;
        }
        catch (Exception e) { Fail("render parent: " + e.Message); return null; }
    }

    private static string _testKey; private static Definition _testDef;
    /// <summary>The effect to spawn: the entry effect, or (test) the one TestEffect names by Guid.</summary>
    private static object Effect(string key)
    {
        if (string.IsNullOrEmpty(key)) return _effect;
        if (ReferenceEquals(key, _testKey)) return _testDef;
        _testKey = key; _testDef = null;
        if (!Guid.TryParse(key, out var g) || !DefinitionManager.Instance.TryGetDefinition(g, out _testDef)) { _testDef = null; Problem = "test effect not found: " + key; }   // (a test knob: not Fail, which turns plasma off)
        return _testDef;
    }

    /// <summary>A new effect under the grid's render root at a grid-local transform (null: none).</summary>
    public static Fx Spawn(Entity grid, object renderParent, in RelativeTransform at)
    {
        if (!_ok || renderParent == null) return null;
        try
        {
            var comps = grid.GetSession()?.SessionComponents;
            if (comps == null) return null;
            object effects = null;
            foreach (var comp in comps.Components) if (comp != null && _sessionCompType.IsInstanceOfType(comp)) { effects = comp; break; }
            if (effects == null) { Fail("no particle effects session component"); return null; }
            string key = AeroEntryFx.TestEffect;
            object def = Effect(key);
            if (def == null) return null;
            var fx = new Fx { Params = Activator.CreateInstance(_userParamsType), Key = key };
            var args = new object[_spawnParams.Length];
            for (int i = 0; i < args.Length; i++) args[i] = _spawnParams[i].HasDefaultValue ? _spawnParams[i].DefaultValue : null;
            args[0] = at; args[1] = def; args[2] = fx.Params; args[3] = renderParent;
            for (int i = 4; i < args.Length; i++) if (_spawnParams[i].ParameterType == typeof(string)) args[i] = "AeroEntryFx";
            fx.Handle = _spawn.Invoke(effects, args);
            return fx.Handle != null ? fx : null;
        }
        catch (Exception e) { Fail("spawn: " + (e.InnerException ?? e).Message); return null; }
    }

    public static void Move(Fx fx, in RelativeTransform at)
    {
        if (fx?.Handle == null) return;
        try { fx.Arg1[0] = at; _updateTransform.Invoke(fx.Handle, fx.Arg1); }
        catch (Exception e) { Fail("move: " + (e.InnerException ?? e).Message); }
    }

    /// <summary>Size (the emitter and its particles), the push downwind (world, m/s^2), colour x brightness, speed of
    /// the spray (streak length), and strength 0..1 (the effect's fixed time: spawn rate and light follow it).</summary>
    public static void Set(Fx fx, float scale, Vector3 push, float r, float g, float b, float velocityMul, float strength)
    {
        if (fx?.Handle == null) return;
        try
        {
            _fScale.SetValue(fx.Params, scale);
            _fSize?.SetValue(fx.Params, 1f);
            _fGravity.SetValue(fx.Params, push);
            _fColor.SetValue(fx.Params, new ColorSRGB(r, g, b, 1f));
            _fVelMul?.SetValue(fx.Params, velocityMul);
            fx.Arg1[0] = fx.Params; _setParameters.Invoke(fx.Handle, fx.Arg1);
            if (AeroEntryFx.FixTime)
            {
                fx.Arg2[0] = true; fx.Arg2[1] = TimeSpan.FromSeconds(Math.Clamp(strength, 0f, 0.999f));
                _fixTime.Invoke(fx.Handle, fx.Arg2);
            }
        }
        catch (Exception e) { Fail("set: " + (e.InnerException ?? e).Message); }
    }

    /// <summary>Stop emitting; the sparks in flight live out their life, then the effect goes.</summary>
    public static void Stop(Fx fx)
    {
        if (fx?.Handle == null) return;
        try { fx.Arg1[0] = false; _stopEmitting.Invoke(fx.Handle, fx.Arg1); _disposeAfter.Invoke(fx.Handle, null); }
        catch (Exception e) { Fail("stop: " + (e.InnerException ?? e).Message); }
        fx.Handle = null;
    }

    public static bool Usable => !_resolved || _ok;
}
