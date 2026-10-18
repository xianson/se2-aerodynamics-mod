#pragma warning disable
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
/// A 3x3x3 airbrake's leaves on a CLIENT (injected into its client prefabs): the two subparts (ConditionalSubParts, at
/// their dummies on the hinge - the upper leaf's 5 mm above the lower's) turned about the block's X by the server's
/// opening (AeroAirbrakeComponent, published in the gear record's progress): the upper one trailing edge up, the lower
/// one down.
/// </summary>
public partial class AeroAirbrakeAnimComponent : Component, IInSceneListener
{
    public static bool Enabled = true;
    /// <summary>The turn's sense (+1 / -1): the upper leaf turns by -Sign x opening about X (checked in game).</summary>
    public static float Sign = 1f;
    sealed class Leaf { public Entity Entity; public RelativeTransform Rest; public bool Up; }
    readonly List<Leaf> _leaves = new();
    int _childCount = -1;
    Vector3I _key; bool _hasKey;
    ThrustTorque.Published _match; long _recheckAt;

    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene() { _leaves.Clear(); _childCount = -1; }

    [After(typeof(RenderSubmissionBegin))]
    private class OnAirbrakeAnim : JobGroup;

    [OnAirbrakeAnim]
    [MustHave(typeof(AeroAirbrakeAnimComponent))]
    private static void AnimJob(AeroAirbrakeAnimComponent c)
    {
        if (!Enabled || !PhysicsHack.Alive(c.Data)) return;
        if (!c._hasKey)
        {
            var block = c.Entity?.TryGet<CubeBlockComponent>();
            if (block == null) return;
            var bb = block.AABB; c._key = bb.Min + bb.Max; c._hasKey = true;
        }
        c.FindLeaves();
        if (c._leaves.Count == 0) return;
        var grid = c.Entity?.GetTopLevelParent();
        if (grid == null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!ThrustTorque.GearState(grid, c._key, ref c._match, ref c._recheckAt, now, out float opening, out _)) opening = 0f;
        float a = Math.Clamp(opening, 0f, 90f) * MathF.PI / 180f;
        foreach (var l in c._leaves)
        {
            if (l.Entity == null || !PhysicsHack.Alive(l.Entity.Data)) { c._childCount = -1; continue; }
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitX, (l.Up ? -Sign : Sign) * a);
            l.Entity.Data.Set(new RelativeTransform(l.Rest.Position, Quaternion.Normalize(q * l.Rest.Orientation)));
        }
    }

    /// <summary>The two spawned children; the upper leaf is the one spawned higher (its dummy 5 mm above the lower's).</summary>
    void FindLeaves()
    {
        var kids = Entity?.TryGet<HierarchyComponent>()?.Children;
        if (kids == null) { _leaves.Clear(); return; }
        int n = 0; foreach (var k in kids) n++;
        if (n == _childCount) return;
        _childCount = n; _leaves.Clear();
        foreach (var k in kids)
            if (k != null && k.Data.TryGet<RelativeTransform>(out var rt)) _leaves.Add(new Leaf { Entity = k, Rest = rt });
        if (_leaves.Count == 2) { bool firstUp = _leaves[0].Rest.Position.Y > _leaves[1].Rest.Position.Y; _leaves[0].Up = firstUp; _leaves[1].Up = !firstUp; }
        else _leaves.Clear();
        if (AeroLog.Detail) Log.Default?.Info($"[AERO-AIRBRAKE] {_key} client: {n} children, {_leaves.Count} leaves");
    }
}
