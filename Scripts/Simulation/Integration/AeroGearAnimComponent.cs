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
/// A retractable gear's moving parts on a CLIENT (injected into the gear blocks' client prefabs). The game spawns each
/// part as a subpart at its dummy (ConditionalSubPartsComponent, while the block is functional) - visual only, no
/// collision: the leg, the oleo's rod, the actuator's cylinder and rod, the doors. Which spawned child is which is told by
/// where it spawned (each dummy sits at its own point of the variant's GearLayout: the leg at the hinge, the rod at the
/// hub, the actuator at its two anchors, each door at its hinge). Every frame the server's gear state (progress, the
/// suspension's compression - AeroGearComponent publishes it, ThrustTorque.GearState reads it) sets them: the leg turned by
/// the fold, the rod slid up it by the compression, the actuator aimed between its anchors, the doors opened. The wheel
/// is never a subpart - it is always a real one (the vanilla wheel down, ours while folding).
/// </summary>
public partial class AeroGearAnimComponent : Component, IInSceneListener
{
    enum Role { Leg, Rod, ActuatorCylinder, ActuatorRod, Door, Foot }

    sealed class Part { public Entity Entity; public RelativeTransform Rest; public Role Role; public int Door; }

    public static bool Enabled = true;
    internal AeroGearVariant _variant; bool _looked;
    readonly List<Part> _parts = new();
    int _childCount = -1;
    internal Vector3I _key; internal bool _hasKey;
    internal ThrustTorque.Published _match; internal long _recheckAt;
    // the pad foot's ankle (rad, FootIk) and the frame clock it follows on
    internal float _ankle; long _ankleTick;
    /// <summary>Harness: the last foot's ankle angle (deg) and its target (deg).</summary>
    public static float LastAnkleDeg, LastAnkleWantDeg;

    void IInSceneListener.OnAddedToScene() { }
    void IInSceneListener.OnBeforeRemovedFromScene() { _parts.Clear(); _childCount = -1; }

    [After(typeof(RenderSubmissionBegin))]
    private class OnAeroGearAnim : JobGroup;

    [OnAeroGearAnim]
    [MustHave(typeof(AeroGearAnimComponent))]
    private static void GearAnimJob(AeroGearAnimComponent c)
    {
        if (!Enabled) return;
        if (!GridPhysics.Alive(c.Data)) return;
        if (!c._looked) { c._looked = true; c._variant = AeroGearVariants.Of(c.Entity); }
        var layout = c._variant?.Layout;
        if (layout == null || !layout.AnimatedParts) return;   // (a gear without its own model yet: nothing to move)
        if (!c._hasKey)
        {
            var block = c.Entity?.TryGet<CubeBlockComponent>();
            if (block == null) return;
            var bb = block.AABB; c._key = bb.Min + bb.Max; c._hasKey = true;
        }
        c.FindParts(layout);
        if (c._parts.Count == 0) return;
        var grid = c.Entity?.GetTopLevelParent();
        if (grid == null) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!ThrustTorque.GearState(grid, c._key, ref c._match, ref c._recheckAt, now, out float progress, out float compression, out Vector3 groundGrid))
            progress = 1f;   // (no state yet: as built - down)
        float dt = c._ankleTick == 0 ? 0f : (float)((now - c._ankleTick) / (double)System.Diagnostics.Stopwatch.Frequency); c._ankleTick = now;
        float fold = GearSequence.Fold(progress), doors = GearSequence.DoorsAt(progress, true);
        foreach (var p in c._parts)
        {
            if (p.Entity == null || !GridPhysics.Alive(p.Entity.Data)) { c._childCount = -1; continue; }
            var rest = new GearPose(p.Rest.Position, p.Rest.Orientation);
            GearPose pose = p.Role switch
            {
                Role.Leg => layout.RakedSlide ? layout.RakedLeg(rest, compression) : layout.OnLeg(rest, fold),
                Role.Rod => layout.RakedSlide ? layout.RakedRod(rest, compression) : layout.OnRod(rest, fold, compression),
                Role.Door => layout.Door(p.Door, rest, doors),
                _ => rest,
            };
            if (p.Role == Role.ActuatorCylinder || p.Role == Role.ActuatorRod)
            {
                var other = c._parts.Find(q => q.Role == (p.Role == Role.ActuatorCylinder ? Role.ActuatorRod : Role.ActuatorCylinder));
                var cylRest = p.Role == Role.ActuatorCylinder ? rest : other != null ? new GearPose(other.Rest.Position, other.Rest.Orientation) : rest;
                var rodRest = p.Role == Role.ActuatorRod ? rest : other != null ? new GearPose(other.Rest.Position, other.Rest.Orientation) : rest;
                var act = layout.Actuator(cylRest, rodRest, fold);
                pose = p.Role == Role.ActuatorCylinder ? act.cylinder : act.rod;
            }
            if (p.Role == Role.Foot)
            {
                // on the rod (as the rod's own parts), then turned about its ankle (its own origin) by the IK angle
                pose = layout.RakedSlide ? layout.RakedRod(rest, compression) : layout.OnRod(rest, fold, compression);
                var q = layout.RakedSlide ? layout.RakedTurn(compression) : layout.LegTurn(fold);
                Vector3 axis = Vector3.Transform(layout.HingeAxis, q);
                Vector3 up = Vector3.Transform(Vector3.Transform(Vector3.UnitY, rest.Orientation), q);
                Vector3 n = Vector3.Zero;
                if (groundGrid.LengthSquared() > 0.5f && c.Entity.Data.TryGet<RelativeTransform>(out var blockRel))
                    n = Vector3.Transform(groundGrid, Quaternion.Inverse(blockRel.Orientation));
                float want = FootIk.Want(up, n, axis, progress);
                c._ankle = FootIk.Follow(c._ankle, want, dt);
                pose = new GearPose(pose.Position, Quaternion.Normalize(FootIk.Turn(axis, c._ankle) * pose.Orientation));
                LastAnkleDeg = c._ankle * 180f / MathF.PI; LastAnkleWantDeg = want * 180f / MathF.PI;
            }
            p.Entity.Data.Set(new RelativeTransform(pose.Position, pose.Orientation));
        }
    }

    /// <summary>The spawned children told apart by where they spawned (each within 5 cm of its own layout point) - again
    /// whenever the children change (the game despawns them when the block stops being functional).</summary>
    void FindParts(GearLayout layout)
    {
        var kids = Entity?.TryGet<HierarchyComponent>()?.Children;
        if (kids == null) { _parts.Clear(); return; }
        int n = 0; foreach (var k in kids) n++;
        if (n == _childCount) return;
        _childCount = n; _parts.Clear();
        foreach (var k in kids)
        {
            if (k == null || !k.Data.TryGet<RelativeTransform>(out var rt)) continue;
            var part = new Part { Entity = k, Rest = rt };
            Vector3 at = rt.Position;
            bool Near(Vector3 v) => (v - at).LengthSquared() < 0.05f * 0.05f;
            if (layout.HasFoot && Near(layout.FootPoint)) part.Role = Role.Foot;
            else if (Near(layout.HingePoint)) part.Role = Role.Leg;
            else if (Near(layout.DeployedHub)) part.Role = Role.Rod;
            else if (Near(layout.ActuatorBase)) part.Role = Role.ActuatorCylinder;
            else if (Near(layout.ActuatorLegEnd)) part.Role = Role.ActuatorRod;
            else
            {
                int d = Array.FindIndex(layout.Doors, x => Near(x.HingePoint));
                if (d < 0) continue;   // (not one of the gear's parts)
                part.Role = Role.Door; part.Door = d;
            }
            _parts.Add(part);
        }
        // (once per change of children: what spawned where - the layout's points are checked against it)
        var sb = new System.Text.StringBuilder();
        foreach (var k in kids)
            if (k != null && k.Data.TryGet<RelativeTransform>(out var rt))
                sb.Append($" ({rt.Position.X:F3}, {rt.Position.Y:F3}, {rt.Position.Z:F3})");
        if (AeroLog.Detail) Log.Default?.Info($"[AERO-GEAR] {_key} client: {n} children at{sb}; {_parts.Count} gear parts");
    }
}
