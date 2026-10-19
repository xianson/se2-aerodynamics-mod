using System.Numerics;

namespace Aero.Core.Components;

/// <summary>
/// Registers the default block-definition -> aero-component mappings.
/// Custom aero blocks are matched by definition ID string.
/// </summary>
public static class DefaultMappings
{
    public static void Register(BlockComponentFactory factory)
    {
        // -- Control Surface --
        factory.Register(AeroBlockGuids.ControlSurface_1x1, info =>
        {
            // Hinge runs along the span (block's right axis = cross(forward, up))
            var hingeAxis = Vector3.Cross(info.Forward, info.Up);

            // Canonicalize hinge direction: always pick the positive half-space.
            // Prevents mirrored blocks from having opposite hinge axes and cancelling forces.
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

        // -- Airbrake --
        factory.Register(AeroBlockGuids.Airbrake_1x1, info =>
            new Airbrake(
                info.Position,
                info.BlockPosition,
                facingDirection: info.Forward,
                area: info.FaceArea));

        // -- Atmospheric Scoop --
        factory.Register(AeroBlockGuids.Scoop_1x1, info =>
            new AtmosphericScoop(
                info.Position,
                info.BlockPosition,
                facingDirection: info.Forward,
                scoopArea: info.FaceArea));

        // -- Helicopter Rotor CCW (standard) --
        factory.Register(AeroBlockGuids.HelicopterRotor_1x1, info =>
            new HelicopterRotor(
                info.Position,
                info.BlockPosition,
                discAxis: info.Up,
                ratedThrust: 500_000f,
                discRadius: 5f,
                spinSign: +1f));

        // -- Helicopter Rotor CW (counter-rotating) --
        factory.Register(AeroBlockGuids.HelicopterRotorCW_1x1, info =>
            new HelicopterRotor(
                info.Position,
                info.BlockPosition,
                discAxis: info.Up,
                ratedThrust: 500_000f,
                discRadius: 5f,
                spinSign: -1f));
    }
}
