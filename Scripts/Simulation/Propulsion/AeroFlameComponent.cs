#pragma warning disable
using System;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// The flame of one CLIENT thruster (injected into thruster compositions that draw flames). The aero simulation runs
/// on the server copy of a grid; every frame it publishes each thruster's share of the thrust - the flight controller's
/// sharing (attitude + translation) while it steers, the game's own otherwise, zero at rest (ThrustTorque). This
/// component finds its own share (its grid's record; itself, by its block's cells in the grid), EASES toward it and draws
/// its thruster's flames at it: after the game's own flame animator each frame (ThrusterEffectsComponent sets each flame
/// effect's time from the thrust; we run after DebugDrawCommit, which that animator precedes), each of the thruster's
/// flame effects is set to share x efficiency of its timeline, as the game would for that power. Visual only and NOTHING
/// of the game's written: until 2026-10-13 this set the thruster's ThrusterOverrideData, which the game also treats as
/// gameplay - the client's MaxThrustData lost the overridden thrusters and got them back only if not disabled, any override
/// forced IsThrusting, and a pilot's own terminal override was overwritten - flames went missing (an independent review).
/// A share missing for MissingSeconds gives the flame back to the game (its own value written once, so a flame we drew
/// long never stays frozen where the game would not redraw it) - a missed frame never snaps the flame.
/// </summary>
public partial class AeroFlameComponent : Component, IInSceneListener
{
    /// <summary>The flame shown (eased), -1: the game's own; the flame scale last drawn (share x efficiency), -1: none.</summary>
    internal float Shown = -1f, Written = -1f;
    /// <summary>Easing time constants (s): a flame lights fast and dies a little slower, as a real one.</summary>
    public const float RiseTau = 0.05f, FallTau = 0.15f;
    public const double MissingSeconds = 0.3;
    // the thruster's flame effects (its ThrusterEffectsComponent's handles, by reflection - VRage.Game.Client is not
    // referenceable), their timelines, its efficiency (the game's private EfficiencyData) and flicker, re-read now and then
    internal object _effects; internal readonly List<(object handle, float duration)> _flames = new();
    internal long _flamesAt; internal float _efficiency = 1f, _randomScale, _jitter = 1f; internal int _jitterPhase; internal bool _randomRead;
    internal ThrustTorque.Published _match;   // (the published grid found last time)
    internal ThrustTorque.Table _table; internal int _idx; internal long _recheckAt;
    internal Vector3I _key; internal bool _hasKey;   // (its block's cells in its grid: Min + Max)
    internal long _tick, _missingSince;

    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        // (out of the scene: its record, key and handles forgotten - a split or a re-add finds them again)
        _match = null; _table = null; _hasKey = false; _effects = null; _flames.Clear(); _flamesAt = 0;
        Shown = -1f; Written = -1f; _missingSince = 0;
    }

    // after the game's thruster flame animator (ThrusterEffectsComponent.ThrustFlameAnimator: After RenderSubmissionBegin,
    // Before DebugDrawCommit): ours is the last word on the flame's length this frame
    [After(typeof(RenderSubmissionBegin))]
    [After(typeof(Keen.Game2.Simulation.DebugDrawCommit))]
    private class OnAeroFlame : JobGroup;

    [OnAeroFlame]
    [MustHave(typeof(AeroFlameComponent))]
    private static void FlameJob(AeroFlameComponent flame)
    {
        if (flame.Shown < 0f && (!ThrustTorque.FlamesEnabled || !ThrustTorque.AnyPublished)) return;   // (nothing published anywhere: cheap)
        long now = System.Diagnostics.Stopwatch.GetTimestamp(); double freq = System.Diagnostics.Stopwatch.Frequency;
        // (the game's own flame, no record matched, its next search not due: nothing this frame can change - FlameShare would
        //  answer -1 without a look and the flame stays the game's. Returned before the scene's alive check (a shared lock
        //  word on every call) and the override read: every frame, every such thruster - 2026-10-15)
        if (flame.Shown < 0f && flame._match == null && now < flame._recheckAt && !Probe && !_reflectFailed) { flame._tick = now; return; }
        var data = flame.Data;
        if (!PhysicsHack.Alive(data)) return;
        float dt = flame._tick == 0 ? 0f : (float)Math.Min(0.1, (now - flame._tick) / freq);
        flame._tick = now;
        if (!ThrustTorque.FlamesEnabled || _reflectFailed) { Release(flame, data); return; }
        // (a pilot's own override on this thruster: the game draws it - ours is the controller's share, not theirs)
        float target = data.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.ThrusterOverrideData>(out _) ? -1f : ThrustTorque.FlameShare(flame, now);
        if (Probe) ProbeLog(flame, data, target, now, freq);
        if (target < 0f)
        {
            // missing: the flame held as it is, and given back to the game only after MissingSeconds
            if (flame.Shown < 0f) return;
            if (flame._missingSince == 0) flame._missingSince = now;
            else if (now - flame._missingSince > (long)(freq * MissingSeconds)) Release(flame, data);
            return;
        }
        flame._missingSince = 0;
        if (flame.Shown < 0f) flame.Shown = target;   // (first light: the share itself - the game's own was drawing about that)
        else if (target != flame.Shown) flame.Shown += (target - flame.Shown) * (1f - MathF.Exp(-dt / (target > flame.Shown ? RiseTau : FallTau)));   // (equal: it adds nothing - no exp)
        float w = flame.Shown < 0.002f ? 0f : flame.Shown;
        RefreshFlames(flame, data, now, freq);
        if (_reflectFailed) { Release(flame, data); return; }
        // (the game's flicker: its random 1-FlameRandomScale..1 every 3rd frame)
        if (++flame._jitterPhase >= 3) { flame._jitterPhase = 0; flame._jitter = 1f - flame._randomScale * Random.Shared.NextSingle(); }
        float scale = Math.Clamp(w * flame._efficiency * flame._jitter, 0f, 1f);
        // (every frame: the game's animator rewrites the flame every 3rd frame whenever its own value is lit or changes)
        DrawFlames(flame, scale);
        flame.Written = scale;
    }

    // ── the thruster's flame effects, by reflection ──
    // (resolved once, under a lock, into locals; published together by the volatile _ready flag - FlameJob runs for many
    //  thrusters in parallel: a half-made set read by another thread failed and switched flames off for the session)
    // TYPED (2026-10-15, a profile: the flames' refresh allocated 115 KB/s - PropertyInfo.GetValue boxing every IsDisposed
    // and EffectDuration, Convert.ToSingle, TryGet<EfficiencyData>.Invoke boxing the context and the data): the handle's
    // members are bound once as typed open delegates over the game's own types (FlameAccess<H, D>, made generic on them by
    // reflection - VRage.Game.Client is not referenceable), the efficiency read through a generic TryGet<E> on the private
    // EfficiencyData (EfficiencyReader<E>: its one float field), and FixEffectTime called typed with a TimeSpan made from the
    // same 1/TimeSteps steps the boxed table held (no box, no argument array). Nothing changes but the garbage.
    static readonly object _reflectLock = new object();
    static volatile bool _ready, _reflectFailed;
    static System.Reflection.FieldInfo _handlesField, _defField;
    static System.Reflection.PropertyInfo _randomProp;
    static FlameAccess _access; static EfficiencyReader _effReader;
    /// <summary>A flame's timeline is set in TimeSteps steps (the same times the boxed table had).</summary>
    const int TimeSteps = 256;

    /// <summary>The handle's members, typed (H: ParticleEffectHandle, D: its ResolvedDefinition's type).</summary>
    abstract class FlameAccess
    {
        /// <summary>Each live handle of the list (the thruster's _flameEffectHandles) with a timeline, and its duration.</summary>
        public abstract void ReadHandles(object list, List<(object handle, float duration)> into);
        public abstract void Fix(object handle, TimeSpan time);
    }
    sealed class FlameAccess<H, D> : FlameAccess where H : class where D : class
    {
        readonly Func<H, D> _resolved; readonly Func<D, float> _duration; readonly Func<H, bool> _disposed; readonly Action<H, bool, TimeSpan> _fix;
        public FlameAccess(System.Reflection.MethodInfo resolved, System.Reflection.MethodInfo duration, System.Reflection.MethodInfo disposed, System.Reflection.MethodInfo fix)
        {
            _resolved = (Func<H, D>)resolved.CreateDelegate(typeof(Func<H, D>));
            _duration = (Func<D, float>)duration.CreateDelegate(typeof(Func<D, float>));
            _disposed = disposed != null ? (Func<H, bool>)disposed.CreateDelegate(typeof(Func<H, bool>)) : null;
            _fix = (Action<H, bool, TimeSpan>)fix.CreateDelegate(typeof(Action<H, bool, TimeSpan>));
        }
        public override void ReadHandles(object list, List<(object handle, float duration)> into)
        {
            // (the game's PooledList through its IList<T>: by index, no enumerator box - the same order)
            if (list is not IList<H> l) return;
            for (int i = 0; i < l.Count; i++)
            {
                var h = l[i];
                if (h == null || (_disposed != null && _disposed(h))) continue;
                var rd = _resolved(h);
                float dur = rd != null ? _duration(rd) : 0f;
                if (dur > 0f) into.Add((h, dur));
            }
        }
        public override void Fix(object handle, TimeSpan time) => _fix((H)handle, true, time);
    }
    /// <summary>The thruster's private EfficiencyData read typed: E is it (one float field, Efficiency - checked).</summary>
    abstract class EfficiencyReader { public abstract bool Read(DEntityContext data, out float efficiency); }
    sealed class EfficiencyReader<E> : EfficiencyReader where E : unmanaged
    {
        public override bool Read(DEntityContext data, out float efficiency)
        {
            if (data.TryGet<E>(out var v)) { efficiency = System.Runtime.CompilerServices.Unsafe.As<E, float>(ref v); return true; }
            efficiency = 0f; return false;
        }
    }

    static bool Resolve(object effects)
    {
        if (_ready) return true;
        if (_reflectFailed) return false;
        lock (_reflectLock)
        {
            if (_ready) return true;
            const System.Reflection.BindingFlags F = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
            var et = effects.GetType();
            var handles = et.GetField("_flameEffectHandles", F);
            var def = et.GetField("_definition", F);
            var effData = et.GetNestedType("EfficiencyData", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            // (its one field a float, Efficiency: read as a float - anything else, no efficiency read: 1, as with none found)
            var effFields = effData?.GetFields(F);
            bool effOk = effData != null && effData.IsValueType && effFields.Length == 1 && effFields[0].Name == "Efficiency" && effFields[0].FieldType == typeof(float);
            var random = def?.FieldType.GetProperty("FlameRandomScale", F);
            // the handle's type and its definition's, from the game's assembly by name
            var ht = et.Assembly.GetType("Keen.VRage.Game.Client.Effects.Components.ParticleEffectHandle")
                     ?? AppDomain.CurrentDomain.GetAssemblies().Select(x => x.GetType("Keen.VRage.Game.Client.Effects.Components.ParticleEffectHandle")).FirstOrDefault(x => x != null);
            var fix = ht?.GetMethod("FixEffectTime", new[] { typeof(bool), typeof(TimeSpan) });
            var resolved = ht?.GetProperty("ResolvedDefinition");
            var disposed = ht?.GetProperty("IsDisposed");
            var duration = resolved?.PropertyType.GetProperty("EffectDuration", F);
            // (bound typed: the handle's ResolvedDefinition, IsDisposed and FixEffectTime, the definition's EffectDuration)
            FlameAccess access = null; EfficiencyReader effReader = null; string why = null;
            if (handles != null && fix != null && resolved?.GetGetMethod() != null && duration?.GetGetMethod(true) != null && duration.PropertyType == typeof(float) && !ht.IsValueType && !resolved.PropertyType.IsValueType)
            {
                try
                {
                    access = (FlameAccess)typeof(FlameAccess<,>).MakeGenericType(ht, resolved.PropertyType).GetConstructors()[0]
                        .Invoke(new object[] { resolved.GetGetMethod(), duration.GetGetMethod(true), disposed?.PropertyType == typeof(bool) ? disposed.GetGetMethod() : null, fix });
                    if (effOk) effReader = (EfficiencyReader)typeof(EfficiencyReader<>).MakeGenericType(effData).GetConstructors()[0].Invoke(null);
                }
                catch (Exception e) { access = null; why = $"{e.GetType().Name}: {(e.InnerException ?? e).Message}"; }
            }
            if (access == null)
            {
                _reflectFailed = true;
                Keen.VRage.Library.Diagnostics.Log.Default?.Warning($"[AERO] flames: the thruster's flame effects not found (handles {handles != null}, FixEffectTime {fix != null}, ResolvedDefinition {resolved != null}, EffectDuration {duration != null}{(why != null ? ", binding: " + why : "")}) - the game's own flames");
                return false;
            }
            _handlesField = handles; _defField = def; _randomProp = random;
            _access = access; _effReader = effReader;
            _ready = true;
            return true;
        }
    }

    /// <summary>Every 0.5 s (now 0: at once): the thruster's flame effects (created by the game when it first works, kept
    /// after), each one's timeline, the flicker, and its efficiency (the game's private EfficiencyData: share x efficiency
    /// is what the game would draw for that power). A failure here is this thruster's only (it tries again later).</summary>
    static void RefreshFlames(AeroFlameComponent flame, DEntityContext data, long now, double freq)
    {
        if (_reflectFailed || (now != 0 && now < flame._flamesAt)) return;
        flame._flamesAt = (now != 0 ? now : System.Diagnostics.Stopwatch.GetTimestamp()) + (long)(System.Diagnostics.Stopwatch.Frequency * 0.5);
        try
        {
            var ent = flame.Entity;
            if (ent == null) return;
            if (flame._effects == null)
                foreach (var c in ent.Components)
                    if (c != null && c.GetType().Name == "ThrusterEffectsComponent") { flame._effects = c; break; }
            var eff = flame._effects;
            if (eff == null || !Resolve(eff)) return;
            // (the flicker's range is the definition's: read once per thruster - its one box; the rest typed, no garbage)
            if (!flame._randomRead && _randomProp != null && _defField.GetValue(eff) is object def) { flame._randomScale = Convert.ToSingle(_randomProp.GetValue(def)); flame._randomRead = true; }
            if (_effReader != null && _effReader.Read(data, out float ef)) flame._efficiency = ef;
            flame._flames.Clear();
            _access.ReadHandles(_handlesField.GetValue(eff), flame._flames);   // (the list: a class - no box)
        }
        catch (Exception e)
        {
            flame._flames.Clear();   // (this thruster's: the game's flame until the next try)
            if (!_warned) { _warned = true; Keen.VRage.Library.Diagnostics.Log.Default?.Warning($"[AERO] flames: reading a thruster's flame effects failed (that thruster: the game's flame; tried again): {e.GetType().Name}: {e.Message}"); }
        }
    }
    static bool _warned;

    /// <summary>Each of the thruster's flame effects at this fraction of its timeline (what the game's animator does).</summary>
    static void DrawFlames(AeroFlameComponent flame, float scale)
    {
        if (!_ready || flame._flames.Count == 0) return;
        int step = (int)MathF.Round(Math.Clamp(scale, 0f, 1f) * TimeSteps);
        var access = _access;
        foreach (var (h, duration) in flame._flames)
        {
            // (the step's time as the table made it: duration x step / TimeSteps in float, then seconds)
            var time = TimeSpan.FromSeconds(duration * step / TimeSteps);
            try { access.Fix(h, time); }
            catch { flame._flamesAt = 0; }   // (a handle given back since: re-read next frame)
        }
    }

    /// <summary>The game's own flame value for this thruster now: its direction's share of the grid's voluntary thrust x
    /// efficiency (ThrusterEffectsComponent.UpdateThrusterAnimationData) - drawn once when we let go.</summary>
    static float GameScale(AeroFlameComponent flame, DEntityContext data)
    {
        try
        {
            // (a pilot's own override: the game draws OverridePower x efficiency)
            if (data.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.ThrusterOverrideData>(out var po)) return Math.Clamp(po.OverridePower, 0f, 1f) * flame._efficiency;
            if (!data.TryGet<Keen.Game2.Simulation.WorldObjects.Movement.ThrustData>(out var td)) return 0f;
            // (its own grid - a subgrid's thruster reads its subgrid, as the game's ParentData does)
            var gridData = new DEntityContext(data.Scene, td.ParentGrid);
            if (!gridData.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.VoluntaryThrustData>(out var vt)) return 0f;
            var v = vt.VoluntaryThrust; float n;
            switch (td.Direction)
            {
                case Base6Directions.Direction.Backward: n = v.Z; break;
                case Base6Directions.Direction.Forward: n = -v.Z; break;
                case Base6Directions.Direction.Right: n = v.X; break;
                case Base6Directions.Direction.Left: n = -v.X; break;
                case Base6Directions.Direction.Up: n = v.Y; break;
                default: n = -v.Y; break;
            }
            return Math.Clamp(n, 0f, 1f) * flame._efficiency;
        }
        catch { return 0f; }
    }

    // ── the PROBE (harness: aeroset AeroFlameComponent.Probe true): twice a second, per grid, how many flames it has and how
    //    many are lit; and up to ProbeMax lines of the flames worth seeing - lit (share, shown or the game's override over
    //    0.01) or the game's override not what was written (replication overwriting it) - logged as [AERO-FLAME] lines ──
    public static bool Probe;
    public static int ProbeMax = 12;
    static long _probeWindow; static int _probeCount;
    static readonly System.Collections.Generic.Dictionary<int, (int all, int lit, int odd, int unmatched)> _probeGrids = new();
    static void ProbeLog(AeroFlameComponent flame, DEntityContext data, float target, long now, double freq)
    {
        long window = now / (long)(freq * 0.5);
        int grid = flame.Entity?.GetTopLevelParent()?.GetHashCode() ?? 0;
        bool has = data.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.Movement.ThrusterOverrideData>(out var o);
        float game = has ? o.OverridePower : -1f;   // (a pilot's own override now - this no longer writes one)
        bool lit = target > 0.01f || flame.Shown > 0.01f;
        bool odd = flame.Shown > 0.01f && flame._flames.Count == 0;   // (lit by us, but no flame effects found: an idle thruster has none until it first works)
        lock (_probeGrids)
        {
            if (window != _probeWindow)
            {
                foreach (var kv in _probeGrids)
                    Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-FLAME] w{_probeWindow} grid g{kv.Key:X}: {kv.Value.all} flames, {kv.Value.lit} lit, {kv.Value.odd} lit without flame effects, {kv.Value.unmatched} unmatched (the game's own flame)");
                _probeGrids.Clear(); _probeWindow = window; _probeCount = 0;
            }
            _probeGrids.TryGetValue(grid, out var c);
            _probeGrids[grid] = (c.all + 1, c.lit + (lit ? 1 : 0), c.odd + (odd ? 1 : 0), c.unmatched + (target < 0f ? 1 : 0));
            if (!(lit || odd) || _probeCount >= ProbeMax) return;
            _probeCount++;
        }
        Keen.VRage.Library.Diagnostics.Log.Default?.Info(
            $"[AERO-FLAME] w{window} g{grid:X} k{flame._key} share {target:F3} shown {flame.Shown:F3} drawn {flame.Written:F3} eff {flame._efficiency:F2} effects {flame._flames.Count} record-age {(flame._match != null ? ((now - flame._match.TableStamp) / freq).ToString("F2") : "-")} s pilot-override {(has ? game.ToString("F2") : "none")}");
    }

    /// <summary>Given back to the game: its own value drawn once (its animator redraws only while its flame is lit or
    /// changes - one we drew would otherwise stay as we left it).</summary>
    static void Release(AeroFlameComponent flame, DEntityContext data)
    {
        if (flame.Written >= 0f && !_reflectFailed) { RefreshFlames(flame, data, 0, 0); DrawFlames(flame, GameScale(flame, data)); }
        flame.Shown = -1f; flame.Written = -1f; flame._missingSince = 0;
    }
}
