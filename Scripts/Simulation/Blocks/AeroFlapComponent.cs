#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// The moving flap of one CLIENT 3x3x1 control surface (injected into those blocks' client prefabs). The game spawns the
/// flap as a subpart - a child entity at the block's hinge dummy, client side, while the block is functional
/// (ConditionalSubPartsComponent). The server copy's ControlSurface publishes its deflection every control step
/// (ThrustTorque.PublishFlaps, keyed by the block's AABB Min + Max, as the flames); this component finds it, eases toward
/// it and turns the subpart about the hinge - the block's Forward axis (the models' span) - from the pose it was spawned
/// in. Visual only: the flap's air is the server's ControlSurface.
/// </summary>
public partial class AeroFlapComponent : Component, IInSceneListener
{
    /// <summary>The angle shown (rad, eased) and how fast it follows (s).</summary>
    internal float Shown;
    public const float Tau = 0.08f;
    /// <summary>The hinge in the block's frame (the models' span: Blender +Y = the block's Forward).</summary>
    public static readonly Vector3 HingeLocal = new Vector3(0f, 0f, -1f);
    public static bool Probe;
    /// <summary>The flap animation on/off (harness: aeroset AeroFlapComponent.Enabled false), for diagnosis.</summary>
    public static bool Enabled = true;

    internal Vector3I _key; internal bool _hasKey;
    internal ThrustTorque.Published _match; internal long _recheckAt, _tick;
    internal Entity _flap; internal RelativeTransform _rest;
    /// <summary>The subpart last written at neutral (its rest pose): not written again while it stays there.</summary>
    internal Entity _restWritten;

    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene() { _flap = null; _restWritten = null; }

    [After(typeof(RenderSubmissionBegin))]
    private class OnAeroFlap : JobGroup;

    [OnAeroFlap]
    [MustHave(typeof(AeroFlapComponent))]
    private static void FlapJob(AeroFlapComponent c)
    {
        AeroPanelProbe.Tick(c.Entity);   // (DEV: the control panel probe, once per harness request)
        if (!Enabled) return;
        var data = c.Data;
        if (!PhysicsHack.Alive(data)) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp(); double freq = System.Diagnostics.Stopwatch.Frequency;
        float dt = c._tick == 0 ? 0f : (float)Math.Min(0.1, (now - c._tick) / freq);
        c._tick = now;
        if (!c._hasKey)
        {
            var block = c.Entity?.TryGet<Keen.Game2.Simulation.WorldObjects.CubeBlocks.CubeBlockComponent>();
            if (block == null) return;
            var bb = block.AABB; c._key = bb.Min + bb.Max; c._hasKey = true;
        }
        // the subpart: the block's child with a transform (spawned and despawned by the game: found again when gone)
        if (c._flap == null || !PhysicsHack.Alive(c._flap.Data))
        {
            c._flap = null;
            var kids = c.Entity?.TryGet<HierarchyComponent>()?.Children;
            if (kids == null) return;
            foreach (var k in kids)
                if (k != null && k.Data.TryGet<RelativeTransform>(out var rt)) { c._flap = k; c._rest = rt; break; }
            if (c._flap == null) return;
        }
        var grid = c.Entity?.GetTopLevelParent();
        if (grid == null) return;
        float target = ThrustTorque.FlapAngle(grid, c._key, ref c._match, ref c._recheckAt, now);
        if (float.IsNaN(target)) target = 0f;   // (no record: the flap rests at neutral)
        c.Shown += (target - c.Shown) * (dt > 0f ? 1f - MathF.Exp(-dt / Tau) : 1f);
        // turned about the hinge in the block's frame: the rest pose, then the rotation (axis taken into the subpart's frame)
        // (at neutral the pose written is the rest pose the subpart was found in: written once, then left - every parked
        //  ship's flaps wrote their unchanged transform every frame. Any angle, or a subpart found again, writes as before)
        if (c.Shown == 0f && ReferenceEquals(c._restWritten, c._flap)) { }
        else
        {
            var axis = Vector3.Transform(HingeLocal, Quaternion.Inverse(c._rest.Orientation));
            c._flap.Data.Set(new RelativeTransform(c._rest.Position, c._rest.Orientation * Quaternion.CreateFromAxisAngle(axis, c.Shown)));
            c._restWritten = c.Shown == 0f ? c._flap : null;
        }
        if (Probe && (now / (long)(freq * 0.5)) != ((now - (long)(dt * freq)) / (long)(freq * 0.5)))
            Log.Default?.Info($"[AERO-FLAP] key={c._key} target={target * 57.2958f:F1} shown={c.Shown * 57.2958f:F1} deg matched={c._match != null}");
    }
}
