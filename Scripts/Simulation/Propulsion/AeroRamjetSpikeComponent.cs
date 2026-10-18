#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Collections.Generic;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// A RAMJET's inlet spike on a CLIENT (2026-10-17; injected into the ramjets' client prefabs - PropulsorBlocks.RamjetClientPrefabs):
/// visual only. The spike is the block's one subpart (gen_propulsor_defs.py gen_ramjet: Subpart01/<N>_S1_Spike at its dummy
/// "RamjetSpike", entity "RamjetSpike", rest pose = fully forward). It is found by its entity's or dummy's name, else as the block's
/// only child, else the child nearest the spike's point on the axis (the game did not pass the turboprops' prefab names through).
/// Every frame it slides aft along the block's +Z to RamjetLaw.SpikeTravel(Mach, its free travel - PropulsorBlocks.Row.SpikeTravel),
/// rate-limited (RamjetLaw.StepSpike): forward below Mach 1.6, all the way aft from Mach 3.2, never beyond its free travel. The
/// Mach is the grid's published flight Mach (AeroEntryFx.TryGetFlow: the server's true airspeed over the local speed of sound),
/// read ~10 times a second; none published (parked, vacuum, aero off): forward. No allocation per frame.
/// </summary>
public partial class AeroRamjetSpikeComponent : Component, IInSceneListener
{
    public static bool Enabled = true;
    /// <summary>Harness: hold every spike at this Mach (negative: the flight's own).</summary>
    public static float DevMach = -1f;
    /// <summary>The Mach is read every this many frames (the flow table is locked and walked: not every frame).</summary>
    public const int SampleEvery = 6;

    Entity _spike; RelativeTransform _rest; int _childCount = -1;
    float _free = -1f, _hub, _travel, _target, _mach; long _last; int _sample;
    /// <summary>Harness / checks: the spike found, its travel now (m) and the Mach it follows (aeroget reads the last one's).</summary>
    public static float LastTravel, LastMach, LastFree; public static int Found;

    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene() { _spike = null; _childCount = -1; }

    [After(typeof(RenderSubmissionBegin))]
    private class OnRamjetSpike : JobGroup;

    [OnRamjetSpike]
    [MustHave(typeof(AeroRamjetSpikeComponent))]
    private static void SpikeJob(AeroRamjetSpikeComponent c)
    {
        if (!Enabled || !PhysicsHack.Alive(c.Data)) return;
        c.Step();
    }

    void Step()
    {
        if (_free < 0f)
        {
            var row = PropulsorBlocks.RowOf(Entity);
            if (row == null) return;
            _free = MathF.Max(0f, row.SpikeTravel); _hub = row.Hub;
        }
        FindSpike();
        if (_spike == null || !PhysicsHack.Alive(_spike.Data)) { _childCount = -1; return; }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _last == 0 ? 0f : Math.Clamp((float)((now - _last) / (double)System.Diagnostics.Stopwatch.Frequency), 0f, 0.1f);
        _last = now;
        if (--_sample <= 0)
        {
            _sample = SampleEvery;
            float mach = 0f;
            var grid = Entity.GetTopLevelParent();
            if (DevMach >= 0f) mach = DevMach;
            else if (grid != null && AeroEntryFx.TryGetFlow(grid.Data.GetWorldTransform().Position, out _, out float m, out _, out _, out _, out _)) mach = m;
            _mach = mach;
            _target = RamjetLaw.SpikeTravel(mach, _free);
        }
        float next = RamjetLaw.StepSpike(_travel, _target, _free, dt);
        if (next != _travel || dt == 0f)
        {
            _travel = next;
            _spike.Data.Set(new RelativeTransform(_rest.Position + new Vector3(0f, 0f, _travel), _rest.Orientation));
        }
        LastTravel = _travel; LastMach = _mach; LastFree = _free;
    }

    /// <summary>The spike among the block's children (on a change of their count): by name ("RamjetSpike" / "Spike": its prefab
    /// entity's or its dummy's), else the only child, else the child nearest the spike's point on the axis (Hub forward of the
    /// block's centre). Its rest pose is where it spawned (fully forward).</summary>
    void FindSpike()
    {
        var kids = Entity?.TryGet<HierarchyComponent>()?.Children;
        if (kids == null) { _spike = null; return; }
        int n = 0; foreach (var k in kids) n++;
        if (n == _childCount) return;
        _childCount = n; _spike = null;
        Entity named = null, only = null, near = null; RelativeTransform rn = default, ro = default, rr = default; int withPose = 0; float best = float.MaxValue;
        var expect = new Vector3(0f, 0f, -_hub);
        foreach (var k in kids)
        {
            if (k == null || !k.Data.TryGet<RelativeTransform>(out var rt)) continue;
            withPose++; only = k; ro = rt;
            string name = k.DebugName;
            if (named == null && name != null && name.IndexOf("Spike", StringComparison.Ordinal) >= 0) { named = k; rn = rt; }
            // (the axis point, either side of the models' 0.125 m offset)
            float d = MathF.Min((rt.Position - expect).Length(), (rt.Position - expect - new Vector3(0.125f, 0.125f, 0.125f)).Length());
            if (d < best) { best = d; near = k; rr = rt; }
        }
        if (named != null) { _spike = named; _rest = rn; }
        else if (withPose == 1) { _spike = only; _rest = ro; }
        else if (near != null && best < 0.25f + 0.1f * _hub) { _spike = near; _rest = rr; }
        _travel = 0f;
        if (_spike != null) Found++;
        Keen.VRage.Library.Diagnostics.Log.Default?.Info(_spike != null
            ? $"[AERO-RAMJET] client: spike '{_spike.DebugName}' at {_rest.Position} ({(named != null ? "by name" : withPose == 1 ? "the only child" : "by place")}), free travel {_free:F4} m"
            : $"[AERO-RAMJET] client: no spike among {withPose} children - not animated");
    }
}
