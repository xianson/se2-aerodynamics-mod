#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Atmosphere;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Wind;
using Keen.VRage.Core;
using Keen.VRage.DCS.Accessors;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// WIND (2026-10-12, the user: "yes wire in wind"). The game's weather blows: every grid carries a WindData (the wind's
/// direction and speed where it is, gusts and terrain shelter in). Aero flies the grid through the AIR, so the air it
/// meets is its velocity less the wind's (At): drag, lift, Mach, the flight effects and the design readouts all follow
/// the air - a headwind lifts, a tailwind drops you, a crosswind weathervanes you, gusts bump you.
/// The game also pushes every dynamic body with the wind itself (WindGeneratorComponent.ApplyWindForce: a bounding-box
/// drag, 0.5 rho A Speed |wind - velocity| along the air past it - on a plane at speed a large extra drag, whichever
/// way the wind blows). On a grid whose air aero computes, that is the same air counted twice: UndoGameForce takes
/// back exactly that impulse (its formula, at its point), after the game's wind and before the physics step.
/// </summary>
public static class AeroWind
{
    /// <summary>Wind in the aero model (and the game's own wind push taken back where aero applies its forces).</summary>
    public static bool Enabled = true;

    /// <summary>The wind's velocity at the grid (world m/s; zero: none, or off).</summary>
    public static Vector3 At(DEntityContext data)
    {
        if (!Enabled || !data.TryGet<WindData>(out var w) || !(w.Speed > 0f)) return Vector3.Zero;
        Vector3 v = w.Direction * w.Speed;
        return v.IsValid() && w.Direction.LengthSquared() > 0.25f ? v : Vector3.Zero;
    }

    /// <summary>Take back the game's wind push this frame (WindGeneratorComponent.ApplyWindForce, the same impulse at the
    /// same point; the velocity read now, after it - a few hundredths of a percent off). Returns the impulse removed (world, N s).</summary>
    public static Vector3 UndoGameForce(DEntityContext data, in WorldTransform wt)
    {
        if (!data.Has<WindGeneratorComponent.AffectingWindGeneratorsData>() || !data.Has<DynamicTag>()) return Vector3.Zero;
        if (!data.TryGet<WindData>(out var w) || w.Speed <= 0f) return Vector3.Zero;
        if (!data.TryGet<RigidBodyMassProperties>(out var mass) || mass.InvMass <= 0f) return Vector3.Zero;
        if (!data.TryGet<AirData>(out var air) || air.Density <= 0f || !data.TryGet<BoundingBoxData>(out var bb)) return Vector3.Zero;
        ref RigidBodyData rb = ref data.TryGetWritePtr<RigidBodyData>();
        if (Unsafe.IsNullRef(in rb)) return Vector3.Zero;
        Vector3 rel = w.Direction * w.Speed - rb.LinearVelocity;
        float num = rel.Length();
        if (num < 0.001f) return Vector3.Zero;
        Vector3 dir = rel / num;
        Vector3 local = WorldTransform.TransformDirectionInv(dir, wt);
        Vector3 s = bb.BoundingBox.Size;
        float area = MathF.Abs(local.X) * s.Y * s.Z + MathF.Abs(local.Y) * s.X * s.Z + MathF.Abs(local.Z) * s.X * s.Y;
        Vector3 imp = dir * (0.5f * air.Density * area * w.Speed * num * AeroPhysics.Dt);
        float cap = 5f / mass.InvMass, il = imp.Length();
        if (il > cap) imp *= cap / il;
        if (!imp.IsValid()) return Vector3.Zero;
        Vector3 at = w.HasImpulseCenter ? w.ImpulseCenterLocal : mass.CenterOfMass;
        rb.ApplyImpulseAt(in mass, in wt, WorldTransform.Transform((Vector3D)at, in wt), -imp);
        return imp;
    }
}
