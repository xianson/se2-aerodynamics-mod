#pragma warning disable
using System;
using Keen.VRage.Core;
using Keen.VRage.Core.Render;
using Keen.VRage.Core.Systems;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// The magnetic gear's HIDDEN WHEEL on a client (2026-10-20; injected into its wheel's client prefab - AeroBlockGuids.HiddenWheelClientPrefabs).
/// The wheel is the vanilla 1.5 m wheel's prefab with a heavier collider (gen_maggear_defs.py) - the suspension needs a wheel body
/// (the vanilla corner module spawns one and asserts on a collider it cannot fit a cylinder to) - but the pad is the gear's foot, so
/// the tyre is never drawn: its render entity's Visible flag off (RotorRenderBridge, as the rotor blades' fades - reflection on the
/// public render types, docs/REFLECTION_AND_WHITELIST.md). Set again once a second (a render entity remade - a LOD, a re-add - is
/// hidden again); nothing per frame. Not found: the tyre shows (Problem in RotorRenderBridge says why).
/// </summary>
public partial class AeroHiddenWheelComponent : Component, IInSceneListener
{
    public static bool Enabled = true;
    object _render;
    long _next;
    readonly object[] _a1 = new object[1], _a2 = new object[2];
    public static int Hidden;

    void IInSceneListener.OnAddedToScene() { _render = null; _next = 0; }
    void IInSceneListener.OnBeforeRemovedFromScene() { _render = null; }

    [After(typeof(RenderSubmissionBegin))]
    private class OnHiddenWheel : JobGroup;

    [OnHiddenWheel]
    [MustHave(typeof(AeroHiddenWheelComponent))]
    private static void HideJob(AeroHiddenWheelComponent c)
    {
        if (!Enabled) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < c._next) return;
        c._next = now + System.Diagnostics.Stopwatch.Frequency;
        c._render ??= RotorRenderBridge.RenderOf(c.Entity);
        if (c._render != null && RotorRenderBridge.Set(c._render, 0f, c._a1, c._a2)) System.Threading.Interlocked.Increment(ref Hidden);
    }
}
