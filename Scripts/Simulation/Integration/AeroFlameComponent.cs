#pragma warning disable
using System;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// The flame of one CLIENT thruster (injected into thruster compositions that draw flames). The aero simulation runs
/// on the server copy of a grid; every frame it publishes each thruster's share of the thrust - the flight controller's
/// sharing (attitude + translation) while it steers, the game's own otherwise, zero at rest (ThrustTorque). This
/// component finds its own share (its grid's record; itself, by its block's cells in the grid), EASES toward it and sets
/// its thruster's override to it: the game draws an overridden thruster's flame at the override power, so a thruster
/// the controller fires harder burns brighter. Visual only: the server copy, which flies the ship, never gets an
/// override. Only a share missing for MissingSeconds removes the override (the game's flames again) - a missed frame
/// never snaps the flame.
/// </summary>
public partial class AeroFlameComponent : Component, IInSceneListener
{
    /// <summary>The flame shown (eased), -1: no override; the override last written, -1: none.</summary>
    internal float Shown = -1f, Written = -1f;
    /// <summary>Easing time constants (s): a flame lights fast and dies a little slower, as a real one.</summary>
    public const float RiseTau = 0.05f, FallTau = 0.15f;
    /// <summary>The override is rewritten only when the shown flame moved this much (a write a frame per thruster).</summary>
    public const float WriteStep = 0.005f;
    public const double MissingSeconds = 0.3;
    internal ThrustTorque.Published _match;   // (the published grid found last time)
    internal ThrustTorque.Table _table; internal int _idx; internal long _recheckAt;
    internal Vector3I _key; internal bool _hasKey;   // (its block's cells in its grid: Min + Max)
    internal long _tick, _missingSince;

    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene() { }

    [After(typeof(RenderSubmissionBegin))]
    private class OnAeroFlame : JobGroup;

    [OnAeroFlame]
    [MustHave(typeof(AeroFlameComponent))]
    private static void FlameJob(AeroFlameComponent flame)
    {
        if (flame.Shown < 0f && (!ThrustTorque.FlamesEnabled || !ThrustTorque.AnyPublished)) return;   // (nothing published anywhere: cheap)
        var data = flame.Data;
        if (!PhysicsHack.Alive(data)) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp(); double freq = System.Diagnostics.Stopwatch.Frequency;
        float dt = flame._tick == 0 ? 0f : (float)Math.Min(0.1, (now - flame._tick) / freq);
        flame._tick = now;
        if (!ThrustTorque.FlamesEnabled) { Release(flame, data); return; }
        float target = ThrustTorque.FlameShare(flame, now);
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
        else flame.Shown += (target - flame.Shown) * (1f - MathF.Exp(-dt / (target > flame.Shown ? RiseTau : FallTau)));
        float w = flame.Shown < 0.002f ? 0f : flame.Shown;
        if (flame.Written >= 0f && MathF.Abs(w - flame.Written) <= WriteStep && !(w == 0f && flame.Written != 0f)) return;
        // (0 would read as no override: a hair above it shows no flame)
        if (PhysicsHack.TrySetThrustOverride(data, Math.Max(w, 0.0001f))) flame.Written = w;
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
        float game = has ? o.OverridePower : -1f;
        bool lit = target > 0.01f || flame.Shown > 0.01f || game > 0.01f;
        bool odd = flame.Written >= 0f && (!has || MathF.Abs(game - Math.Max(flame.Written, 0.0001f)) > 0.01f);
        lock (_probeGrids)
        {
            if (window != _probeWindow)
            {
                foreach (var kv in _probeGrids)
                    Keen.VRage.Library.Diagnostics.Log.Default?.Info($"[AERO-FLAME] w{_probeWindow} grid g{kv.Key:X}: {kv.Value.all} flames, {kv.Value.lit} lit, {kv.Value.odd} override-not-ours, {kv.Value.unmatched} unmatched (the game's own flame)");
                _probeGrids.Clear(); _probeWindow = window; _probeCount = 0;
            }
            _probeGrids.TryGetValue(grid, out var c);
            _probeGrids[grid] = (c.all + 1, c.lit + (lit ? 1 : 0), c.odd + (odd ? 1 : 0), c.unmatched + (target < 0f ? 1 : 0));
            if (!(lit || odd) || _probeCount >= ProbeMax) return;
            _probeCount++;
        }
        Keen.VRage.Library.Diagnostics.Log.Default?.Info(
            $"[AERO-FLAME] w{window} g{grid:X} k{flame._key} share {target:F3} shown {flame.Shown:F3} written {flame.Written:F3} game-override {(has ? game.ToString("F4") : "none")}");
    }

    /// <summary>The thruster's override removed: the game's flame again.</summary>
    static void Release(AeroFlameComponent flame, DEntityContext data)
    {
        if (flame.Written >= 0f || flame.Shown >= 0f) PhysicsHack.TryRemoveThrustOverride(data);
        flame.Shown = -1f; flame.Written = -1f; flame._missingSince = 0;
    }
}
