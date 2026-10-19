#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Reflection;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>One spawned exhaust effect (AeroExhaustComponent): the game's handle, its boxed user parameters (one box per effect, reused),
/// and what was last written (the writes are skipped when nothing moved).</summary>
internal sealed class ExhaustSlot
{
    public object H, Prm; public object[] Args; public Guid Effect;
    public float Time = -1f, Scale = -1f, Vel = -1f, Size = -1f, R = -1f, G = -1f, B = -1f, A = -1f;
    public Vector3 At; public Quaternion Rot = Quaternion.Identity; public bool Emitting = true, Dead;
}

/// <summary>
/// The exhaust's particle plumbing (client): the game's particle API is in VRage.Game.Client / VRage.Render (not mod references), so it is
/// late-bound ONCE (docs/REFLECTION_AND_WHITELIST.md, "ExhaustParticles"): ParticleEffectsSessionComponent.TrySpawnEffect(RelativeTransform,
/// ...) and a boxed ParticleEffectUserParameters (as FlightField / TearSmoke), and the handle's per-frame members bound as TYPED open
/// delegates over the handle's own type (Access&lt;H&gt;, made generic on it by reflection - as AeroFlameComponent's FlameAccess): the time fix
/// (EffectTime: SE2 4.0 dropped FixEffectTime), UpdateTransform, StopEmitting, ResumeEmitting, DisposeAfterParticlesDie, IsDisposed - no boxing, no argument array, no garbage per frame.
/// SetParameters goes through MethodInfo.Invoke with the slot's own box and argument array (a few small boxes per write) - written only when
/// a parameter moved by more than ParamStep (a lit engine's are steady: none per frame). A handle the game disposed is seen by IsDisposed
/// before any call (no exception on the frame path - docs: an exception costs ~200 ms); the slot is then dead and respawned.
/// </summary>
internal static class ExhaustParticles
{
    const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    /// <summary>An effect's time is written on a change of at least this (of its 0..1 strength); a parameter on a relative change of ParamStep.</summary>
    public static float FixStep = 0.004f, ParamStep = 0.04f;
    public static string Why = "-";
    public static int Spawns, SpawnFails, ParamWrites, TimeWrites, Moves;
    static volatile bool _resolved; static bool _failed;
    static readonly object _lock = new();
    static Type _tSession, _tParams; static MethodInfo _spawn, _setParams;
    static FieldInfo _fScale, _fSize, _fVel, _fColor;
    static Access _access;

    /// <summary>The handle's per-frame members, typed (H: ParticleEffectHandle).</summary>
    abstract class Access
    {
        public abstract void Fix(object h, TimeSpan t);
        public abstract void Move(object h, RelativeTransform at);
        public abstract void Stop(object h);
        public abstract void Resume(object h);
        public abstract void DisposeAfter(object h);
        public abstract bool Gone(object h);
    }
    sealed class Access<H> : Access where H : class
    {
        readonly Action<H, RelativeTransform> _move; readonly Action<H, bool> _stop;
        readonly Action<H> _resume, _dispose; readonly Func<H, bool> _gone;
        public Access(MethodInfo move, MethodInfo stop, MethodInfo resume, MethodInfo dispose, MethodInfo gone)
        {
            _move = (Action<H, RelativeTransform>)move.CreateDelegate(typeof(Action<H, RelativeTransform>));
            _stop = (Action<H, bool>)stop.CreateDelegate(typeof(Action<H, bool>));
            _resume = (Action<H>)resume.CreateDelegate(typeof(Action<H>));
            _dispose = (Action<H>)dispose.CreateDelegate(typeof(Action<H>));
            _gone = (Func<H, bool>)gone.CreateDelegate(typeof(Func<H, bool>));
        }
        public override void Fix(object h, TimeSpan t) => EffectTime.Fix(h, (float)t.TotalSeconds);
        public override void Move(object h, RelativeTransform at) => _move((H)h, at);
        public override void Stop(object h) => _stop((H)h, false);
        public override void Resume(object h) => _resume((H)h);
        public override void DisposeAfter(object h) => _dispose((H)h);
        public override bool Gone(object h) => _gone((H)h);
    }

    /// <summary>The API resolved (once); false: not found - the exhaust is off for the session (Why says what was missing).</summary>
    public static bool Resolve()
    {
        if (_resolved) return !_failed;
        lock (_lock)
        {
            if (_resolved) return !_failed;
            try
            {
                _tSession = EntryFxBridge.FindType("VRage.Game.Client", "Keen.VRage.Game.Client.Effects.Components.ParticleEffectsSessionComponent");
                _tParams = EntryFxBridge.FindType("VRage.Render", "Keen.VRage.Render.Data.ParticleEffectUserParameters");
                if (_tSession != null)
                    foreach (var m in _tSession.GetMethods(AnyInst))
                        if (m.Name == "TrySpawnEffect") { var ps = m.GetParameters(); if (ps.Length > 3 && ps[0].ParameterType == typeof(RelativeTransform)) { _spawn = m; break; } }
                var ht = _spawn?.ReturnType;
                if (_tParams != null)
                {
                    _fScale = _tParams.GetField("EmitterScaleMultiplier"); _fSize = _tParams.GetField("EmitterSizeMultiplier");
                    _fVel = _tParams.GetField("VelocityMultiplier"); _fColor = _tParams.GetField("ColorMultiplier");
                }
                bool fix = EffectTime.Ready;   // (SE2 4.0: FixEffectTime is gone - EffectTime fixes the time)
                MethodInfo move = ht?.GetMethod("UpdateTransform", AnyInst, null, new[] { typeof(RelativeTransform) }, null),
                           stop = ht?.GetMethod("StopEmitting", AnyInst, null, new[] { typeof(bool) }, null),
                           resume = ht?.GetMethod("ResumeEmitting", AnyInst, null, Type.EmptyTypes, null),
                           dispose = ht?.GetMethod("DisposeAfterParticlesDie", AnyInst, null, Type.EmptyTypes, null),
                           gone = ht?.GetProperty("IsDisposed", AnyInst)?.GetGetMethod();
                _setParams = ht?.GetMethod("SetParameters", AnyInst);
                if (_spawn == null || _tParams == null || ht == null || ht.IsValueType || !fix || move == null || stop == null || resume == null || dispose == null || gone == null
                    || _setParams == null || _fScale == null || _fVel == null || _fSize == null || _fColor == null || _fColor.FieldType != typeof(ColorSRGB))
                {
                    _failed = true;
                    Why = $"particle API not found (spawn {_spawn != null}, params {_tParams != null}, handle {ht?.Name ?? "-"}, fix {(fix ? "ok" : EffectTime.Why)}, move {move != null}, stop {stop != null}, resume {resume != null}, dispose {dispose != null}, disposed {gone != null}, set {_setParams != null}, colour {_fColor?.FieldType.Name ?? "-"})";
                }
                else
                {
                    _access = (Access)typeof(Access<>).MakeGenericType(ht).GetConstructors()[0].Invoke(new object[] { move, stop, resume, dispose, gone });
                    Why = "ok";
                }
            }
            catch (Exception e) { _failed = true; Why = "binding: " + (e.InnerException ?? e).Message; }
            if (_failed) Log.Default?.Warning("[AERO-EXHAUST] exhaust effects off: " + Why);
            _resolved = true;
            return !_failed;
        }
    }

    /// <summary>An effect under the render parent (a grid's root render component) at a transform relative to it, its parameters set;
    /// null and why on failure. (Rare: an engine's first light - not on the frame path.)</summary>
    public static ExhaustSlot Spawn(Entity grid, object parent, Guid effect, RelativeTransform at, float scale, float vel, float size, float r, float g, float b, float a, out string why)
    {
        why = null;
        if (!Resolve()) { why = Why; return null; }
        try
        {
            object session = null;
            var comps = grid?.GetSession()?.SessionComponents;
            if (comps != null) foreach (var sc in comps.Components) if (sc != null && _tSession.IsInstanceOfType(sc)) { session = sc; break; }
            if (session == null) { why = "no particle session"; return null; }
            if (!DefinitionManager.Instance.TryGetDefinition(effect, out Definition def) || def == null) { why = "effect not loaded: " + effect + " (the Blocks content not built?)"; return null; }
            var slot = new ExhaustSlot { Effect = effect, Prm = Activator.CreateInstance(_tParams), Args = new object[1], At = at.Position, Rot = at.Orientation };
            Fill(slot, scale, vel, size, r, g, b, a);
            var p = _spawn.GetParameters();
            var args = new object[p.Length];
            for (int i = 0; i < args.Length; i++) args[i] = p[i].HasDefaultValue ? p[i].DefaultValue : null;
            args[0] = at; args[1] = def; args[2] = slot.Prm; args[3] = parent;
            for (int i = 4; i < args.Length; i++) if (p[i].ParameterType == typeof(string)) args[i] = "AeroExhaust";
            slot.H = _spawn.Invoke(session, args);
            if (slot.H == null) { why = "spawn refused (budget or distance)"; SpawnFails++; return null; }
            _access.Fix(slot.H, TimeSpan.Zero); slot.Time = 0f;
            Spawns++;
            return slot;
        }
        catch (Exception e) { why = "spawn: " + (e.InnerException ?? e).Message; SpawnFails++; return null; }
    }

    static void Fill(ExhaustSlot s, float scale, float vel, float size, float r, float g, float b, float a)
    {
        _fScale.SetValue(s.Prm, scale); _fVel.SetValue(s.Prm, vel); _fSize.SetValue(s.Prm, size);
        // (ColorSRGB asserts each channel in [0, 1] - the unstart pop's glow went above 1 (in game, 2026-10-17: three engine
        //  asserts per pop): clamped here for every caller; brightness above 1 has to come from the effect time instead)
        r = Math.Clamp(r, 0f, 1f); g = Math.Clamp(g, 0f, 1f); b = Math.Clamp(b, 0f, 1f); a = Math.Clamp(a, 0f, 1f);
        _fColor.SetValue(s.Prm, new ColorSRGB(r, g, b, a));
        s.Scale = scale; s.Vel = vel; s.Size = size; s.R = r; s.G = g; s.B = b; s.A = a;
    }

    /// <summary>The handle is gone (the game disposed it): the slot is dead (no call made on it again).</summary>
    public static bool Alive(ExhaustSlot s)
    {
        if (s == null || s.Dead || s.H == null) return false;
        if (_access.Gone(s.H)) { s.Dead = true; return false; }
        return true;
    }

    /// <summary>Its strength (0..1 of its timeline), written when it moved by FixStep (and always to exactly 0).</summary>
    public static void Time(ExhaustSlot s, float strength)
    {
        float t = strength > 0f ? (strength < 1f ? strength : 1f) : 0f;
        if (MathF.Abs(t - s.Time) < FixStep && !(t == 0f && s.Time != 0f)) return;
        s.Time = t;
        _access.Fix(s.H, TimeSpan.FromSeconds(ExhaustBlocks.Duration * t));
        TimeWrites++;
    }

    static bool Moved(float was, float now) => MathF.Abs(now - was) > ParamStep * MathF.Max(0.05f, MathF.Abs(was));
    /// <summary>Its user parameters, written only when one moved by ParamStep (relative).</summary>
    public static void Params(ExhaustSlot s, float scale, float vel, float size, float r, float g, float b, float a)
    {
        if (!Moved(s.Scale, scale) && !Moved(s.Vel, vel) && !Moved(s.Size, size) && !Moved(s.R, r) && !Moved(s.G, g) && !Moved(s.B, b) && !Moved(s.A, a)) return;
        Fill(s, scale, vel, size, r, g, b, a);
        s.Args[0] = s.Prm;
        _setParams.Invoke(s.H, s.Args);
        ParamWrites++;
    }

    /// <summary>Its place (relative to its render parent), written when it moved 1 mm or turned.</summary>
    public static void Move(ExhaustSlot s, Vector3 at, Quaternion rot)
    {
        if ((at - s.At).LengthSquared() < 1e-6f && MathF.Abs(Quaternion.Dot(rot, s.Rot)) > 0.99999f) return;
        s.At = at; s.Rot = rot;
        _access.Move(s.H, new RelativeTransform(at, rot));
        Moves++;
    }

    /// <summary>Emission stopped (its particles live out their lives: the smoke lingers) / resumed.</summary>
    public static void Stop(ExhaustSlot s) { if (!s.Emitting) return; s.Emitting = false; _access.Stop(s.H); }
    public static void Resume(ExhaustSlot s) { if (s.Emitting) return; s.Emitting = true; _access.Resume(s.H); }

    /// <summary>Ended: stopped, disposed once its particles have died (a lingering trail is left to fade).</summary>
    public static void End(ExhaustSlot s)
    {
        if (s == null || s.H == null) return;
        if (!s.Dead && !_access.Gone(s.H)) _access.DisposeAfter(s.H);
        s.Dead = true; s.H = null;
    }
}
