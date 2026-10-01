#pragma warning disable
using System;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// The flame of one CLIENT thruster (injected into thruster compositions that draw flames). The aero simulation runs
/// on the server copy of a grid only; while its flight controller shares the thrust (ThrustTorque), it publishes
/// each thruster's share. This component finds its own share (its grid, by place; itself, by its place in the grid)
/// and sets its thruster's override to it: the game draws an overridden thruster's flame at the override power, so
/// a thruster the controller fires harder burns brighter. Visual only: the server copy, which flies the ship, never
/// gets an override. With no share published (no controller), the override is removed: the game's flames again.
/// </summary>
public partial class AeroFlameComponent : Component, IInSceneListener
{
    internal float Shown = -1f;

    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene() { }

    [After(typeof(RenderSubmissionBegin))]
    private class OnAeroFlame : JobGroup;

    [OnAeroFlame]
    [MustHave(typeof(AeroFlameComponent))]
    private static void FlameJob(AeroFlameComponent flame)
    {
        if (!ThrustTorque.FlamesEnabled && flame.Shown < 0f) return;
        if (!ThrustTorque.AnyPublished && flame.Shown < 0f) return;   // (nothing steering anywhere: cheap)
        var data = flame.Data;
        if (!PhysicsHack.Alive(data)) return;
        var grid = flame.Entity?.GetTopLevelParent();
        float share = -1f;
        if (grid != null && ThrustTorque.FlamesEnabled)
        {
            var gwt = grid.Data.GetWorldTransform();
            var pos = flame.Entity.Data.GetWorldTransform().Position;
            share = ThrustTorque.ShareAt(gwt, pos);
        }
        if (share < 0f)
        {
            if (flame.Shown >= 0f) { PhysicsHack.TryRemoveThrustOverride(data); flame.Shown = -1f; }
            return;
        }
        if (flame.Shown >= 0f && MathF.Abs(share - flame.Shown) < 0.02f) return;
        // (0 would read as no override: a hair above it shows no flame)
        if (PhysicsHack.TrySetThrustOverride(data, Math.Max(share, 0.0001f))) flame.Shown = share;
    }
}
