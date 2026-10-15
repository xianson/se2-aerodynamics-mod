#pragma warning disable
using System;
using Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.Wind;
using Keen.Game2.Simulation.WorldObjects.FloatingObjects;
using Keen.VRage.Core;
using Keen.VRage.DCS.Annotations;
using Keen.VRage.Physics.Data;

namespace AeroMod;

/// <summary>
/// DRAG ON FRACTURE DEBRIS (2026-10-15, the user: "do the fracture pieces that arent grids have drag? can we give them
/// slight drag?"). They had none: a block that breaks (the structure failing it, a collision, a weapon) throws its pieces
/// out as loose bodies, not grids - the game's FractureDebris_Client prefab (FracturedBodyComponent, DebrisComponent, a
/// SimulatedComponent, an 8-10 s TimeoutComponent, at most 100 alive), spawned only in the CLIENT scene by
/// DebrisSpawnerSessionComponent (the server never has them). Its FractureWindBindingComponent gives each piece a
/// BoundingBoxData, an AirData (the atmosphere generator fills its Density) and a WindData, but the only force the air
/// puts on it is the game's wind push (WindGeneratorComponent.ApplyWindForce), which returns at wind speed 0: in still
/// air a chip kept the ship's speed until it hit something. Aero acts on grids only.
/// This job gives each piece quadratic drag against the air (DebrisDragMath: its bounding box seen along its motion,
/// Cd ~1 x DragScale; the wind's velocity taken off, and the game's own wind push counted in, not twice) and a little
/// drag on its tumbling. Keyed on the binding component, so grids and the damaged-model debris of non-breakable blocks
/// (no binding, no air data) are not touched. No debris, no calls.
/// </summary>
public static class AeroDebrisDrag
{
    /// <summary>Drag on loose fracture pieces (AeroSettings "Fx.DebrisDrag").</summary>
    public static bool Enabled = true;
    /// <summary>The pieces' drag coefficient, on their bounding box (1: a blunt chunk; AeroSettings "Fx.DebrisDragScale").</summary>
    public static float DragScale = 1f;
    /// <summary>The drag on a piece's tumbling, times this (0: none).</summary>
    public static float SpinDragScale = 1f;
    /// <summary>Diagnostics (aeroget): piece-steps the drag acted on, and the last step's speed taken off (m/s). Plain
    /// writes from the parallel job - a counter, not a measure.</summary>
    public static int Steps;
    public static float LastDv;

    /// <summary>After the game's wind push (RangedAffectGenerators: WindUpdate.ApplyForceGroup), before the physics step -
    /// as AeroGridComponent.OnWindUndo.</summary>
    [After(typeof(Keen.Game2.Simulation.GameSystems.RangedAffectGenerators.RangedAffectGenerators))]
    [Before(typeof(Keen.Game2.Simulation.Physics))]
    private class OnDebrisDrag : JobGroup;

    [WhenSimulated]
    [OnDebrisDrag]
    [MustHave(typeof(FractureWindBindingComponent))]
    [MustHave(typeof(DynamicTag))]
    internal static void DebrisDragJob(in BoundingBoxData box, in AirData air, in WindData wind, in RigidBodyMassProperties mass, Writable<RigidBodyData> body, WorldTransform wt)
    {
        if (!Enabled || !AeroSwitch.Enabled || !(air.Density > 0f) || !(mass.InvMass > 0f)) return;
        float rho = air.Density, dt = AeroPhysics.Dt;
        RigidBodyData rb = body.Value;
        // the air past it: its velocity less the wind's (WindData.Speed > 0 only while a wind generator pushes it - the
        // game's ApplyWindForce this same step; gone back to 0 when it leaves one)
        float ws = wind.Speed > 0f && wind.Direction.LengthSquared() > 0.25f ? wind.Speed : 0f;
        Vector3 rel = rb.LinearVelocity - (ws > 0f ? wind.Direction * ws : Vector3.Zero);
        float speed = rel.Length();
        bool changed = false;
        if (speed > 0.05f && DragScale > 0f)
        {
            Vector3 dir = rel / speed;
            float area = DebrisDragMath.ProjectedArea(box.BoundingBox.Size, WorldTransform.TransformDirectionInv(dir, wt));
            float dv = DebrisDragMath.DeltaV(speed, rho, DragScale, area, mass.InvMass, ws, dt);
            if (dv > 0f)
            {
                Vector3 v = rb.LinearVelocity - dir * dv;
                if (v.IsValid()) { rb.LinearVelocity = v; changed = true; LastDv = dv; }
            }
        }
        Vector3 spin = rb.AngularVelocity;
        if (SpinDragScale > 0f && spin.LengthSquared() > 1e-4f)
        {
            Vector3 local = DebrisDragMath.SpinDamped(WorldTransform.TransformDirectionInv(spin, wt), box.BoundingBox.Size, 1f / mass.InvMass, rho, SpinDragScale, dt);
            Vector3 w = WorldTransform.TransformDirection(local, wt);
            if (w.IsValid()) { rb.AngularVelocity = w; changed = true; }
        }
        if (!changed) return;   // (a piece at rest in still air: its body left unwritten, asleep)
        body.WritableValue = rb;
        Steps++;
    }
}
