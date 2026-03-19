#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Registers the default block-definition → aero-component mappings.
/// Custom aero blocks are matched by GUID; vanilla blocks by component type.
/// </summary>
public static class DefaultMappings
{
    public static void Register(BlockComponentFactory factory)
    {
        // ── Custom aero blocks (matched by definition GUID) ──

        factory.RegisterByGuid(AeroBlockGuids.ControlSurface_1x1, info =>
        {
            // Hinge runs along the span (block's right axis = cross(forward, up))
            var hingeAxis = Vector3.Cross(info.Forward, info.Up);

            // Canonicalize hinge direction: always pick the positive half-space.
            // This prevents mirrored blocks (e.g. two rudder blocks placed back-to-back
            // on a fin) from having opposite hinge axes and cancelling each other's forces.
            float absX = MathF.Abs(hingeAxis.X);
            float absY = MathF.Abs(hingeAxis.Y);
            float absZ = MathF.Abs(hingeAxis.Z);
            float dominant = absY >= absX && absY >= absZ ? hingeAxis.Y
                           : absX >= absZ ? hingeAxis.X
                           : hingeAxis.Z;
            if (dominant < 0) hingeAxis = -hingeAxis;

            return new ControlSurface(
                info.Position,
                info.BlockPosition,
                hingeAxis: hingeAxis,
                chordDirection: info.Forward,
                area: info.FaceArea);
        });

        factory.RegisterByGuid(AeroBlockGuids.Airbrake_1x1, info =>
            new Airbrake(
                info.Position,
                info.BlockPosition,
                facingDirection: info.Forward, // deployed plate faces forward
                area: info.FaceArea));

        factory.RegisterByGuid(AeroBlockGuids.Scoop_1x1, info =>
            new AtmosphericScoop(
                info.Position,
                info.BlockPosition,
                facingDirection: info.Forward, // scoop opening faces forward
                scoopArea: info.FaceArea));

        // ── Vanilla: atmospheric thrusters → AirIntake ──
        // TODO: Uncomment when per-block component access is confirmed.
        // factory.RegisterByComponent<ThrusterComponent>((info, thruster) =>
        //     new AirIntake(
        //         info.Position,
        //         info.BlockPosition,
        //         facingDirection: -info.Forward, // intake faces opposite to thrust direction
        //         captureArea: info.FaceArea));
    }
}
