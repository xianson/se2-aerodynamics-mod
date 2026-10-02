#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// Registry for block-level aerodynamic components.
/// Evaluates all components and handles face-override force subtraction.
/// </summary>
public class AeroComponentRegistry
{
    private readonly List<IAeroBlockComponent> _components = new();

    /// <summary>All registered components.</summary>
    public IReadOnlyList<IAeroBlockComponent> Components => _components;

    /// <summary>Number of registered components.</summary>
    public int Count => _components.Count;

    /// <summary>Remove all components.</summary>
    public void Clear() => _components.Clear();

    /// <summary>Add a component to the registry.</summary>
    public void Add(IAeroBlockComponent component)
    {
        _components.Add(component);
    }

    /// <summary>Remove a specific component.</summary>
    public bool Remove(IAeroBlockComponent component)
    {
        return _components.Remove(component);
    }

    /// <summary>Remove all components at a given block position.</summary>
    /// <summary>The components of blocks inside a box of cells (one pass: per cell it was a pass per cell).</summary>
    public int RemoveInBox(Vector3I min, Vector3I max)
    {
        int removed = 0;
        for (int i = _components.Count - 1; i >= 0; i--)
        {
            var p = _components[i].BlockPosition;
            if (p.X >= min.X && p.X <= max.X && p.Y >= min.Y && p.Y <= max.Y && p.Z >= min.Z && p.Z <= max.Z)
            {
                _components.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    public int RemoveBlock(Vector3I blockPos)
    {
        int removed = 0;
        for (int i = _components.Count - 1; i >= 0; i--)
        {
            if (_components[i].BlockPosition == blockPos)
            {
                _components.RemoveAt(i);
                removed++;
            }
        }
        return removed;
    }

    /// <summary>Enumerate components of a specific type.</summary>
    public IEnumerable<T> OfType<T>() where T : class
    {
        for (int i = 0; i < _components.Count; i++)
        {
            if (_components[i] is T t)
                yield return t;
        }
    }

    /// <summary>
    /// Evaluate all components and return total force + torque.
    /// For IFaceOverride components, subtracts estimated Newtonian pressure
    /// on owned cells before adding the component's own force.
    /// </summary>
    public (Vector3 force, Vector3 torque) EvaluateAll(in AeroContext ctx)
    {
        Vector3 totalForce = Vector3.Zero;
        Vector3 totalTorque = Vector3.Zero;

        float speed = ctx.Speed;
        if (speed < 0.01f) return (totalForce, totalTorque);

        Vector3 vHat = ctx.Velocity / speed;
        float q = (float)ctx.Atmosphere.GetDynamicPressure(speed);

        // Extract CdBluff for face-override subtraction (same as LiftingSurfaceModel)
        float cdBluff = 1.1f;
        float streamlining = 0f;

        for (int i = 0; i < _components.Count; i++)
        {
            var comp = _components[i];
            var local = LocalAeroConditions.FromContext(ctx, comp.Position);
            var result = comp.Compute(local);

            // Face override: subtract estimated Newtonian force on owned faces
            if (comp is IFaceOverride faceOverride)
            {
                SubtractFaceForces(ctx, faceOverride.OwnedCells, vHat, q, cdBluff, streamlining,
                    ref totalForce, ref totalTorque);
            }

            totalForce += result.Force;
            totalTorque += result.TorqueAbout(ctx.CenterOfMass);
        }

        return (totalForce, totalTorque);
    }

    /// <summary>
    /// Subtract estimated Newtonian Cp force on owned cells' surface faces.
    /// Same formula as LiftingSurfaceModel: Cp = CdBluff * cos(theta), with streamlining.
    /// </summary>
    private static void SubtractFaceForces(in AeroContext ctx,
        IReadOnlyList<Vector3I> ownedCells, Vector3 vHat, float q,
        float cdBluff, float streamlining,
        ref Vector3 totalForce, ref Vector3 totalTorque)
    {
        if (ctx.SurfaceCache is SmoothSurfaceProvider smooth)
        {
            SubtractFaceForcesIndexed(smooth, ctx, ownedCells, vHat, q, cdBluff, streamlining,
                ref totalForce, ref totalTorque);
            return;
        }

        // Brute-force fallback for non-SmoothSurfaceProvider types
        SubtractFaceForcesBrute(ctx, ownedCells, vHat, q, cdBluff, streamlining,
            ref totalForce, ref totalTorque);
    }

    private static void SubtractFaceForcesIndexed(SmoothSurfaceProvider smooth,
        in AeroContext ctx,
        IReadOnlyList<Vector3I> ownedCells, Vector3 vHat, float q,
        float cdBluff, float streamlining,
        ref Vector3 totalForce, ref Vector3 totalTorque)
    {
        var faces = ctx.SurfaceCache.Faces;

        for (int c = 0; c < ownedCells.Count; c++)
        {
            for (int d = 0; d < 6; d++)
            {
                int fi = smooth.GetFaceIndex(ownedCells[c], d);
                if (fi < 0) continue;

                var face = faces[fi];
                float cosAlpha = Vector3.Dot(vHat, face.Normal);
                if (cosAlpha <= 0.01f) continue;

                float cp = cdBluff * cosAlpha;

                if (streamlining > 0f)
                {
                    float recovery = cosAlpha * cosAlpha;
                    float minFraction = 0.08f;
                    float factor = minFraction + (1f - minFraction) * recovery;
                    cp *= 1f - streamlining * (1f - factor);
                }

                var pressureForce = face.Normal * (-cp * q * face.Area);
                totalForce -= pressureForce;
                totalTorque -= Vector3.Cross(face.Position - ctx.CenterOfMass, pressureForce);
            }
        }
    }

    private static void SubtractFaceForcesBrute(in AeroContext ctx,
        IReadOnlyList<Vector3I> ownedCells, Vector3 vHat, float q,
        float cdBluff, float streamlining,
        ref Vector3 totalForce, ref Vector3 totalTorque)
    {
        var faces = ctx.SurfaceCache.Faces;
        float blockSize = ctx.BlockSize;
        float halfBlock = blockSize * 0.5f;

        for (int c = 0; c < ownedCells.Count; c++)
        {
            Vector3 cellCenter = new Vector3(
                ownedCells[c].X * blockSize,
                ownedCells[c].Y * blockSize,
                ownedCells[c].Z * blockSize);

            // Find surface faces belonging to this cell (within half a block of cell center)
            for (int f = 0; f < faces.Count; f++)
            {
                var face = faces[f];
                float dx = MathF.Abs(face.Position.X - cellCenter.X);
                float dy = MathF.Abs(face.Position.Y - cellCenter.Y);
                float dz = MathF.Abs(face.Position.Z - cellCenter.Z);

                if (dx > halfBlock || dy > halfBlock || dz > halfBlock)
                    continue;

                float cosAlpha = Vector3.Dot(vHat, face.Normal);
                if (cosAlpha <= 0.01f)
                    continue;

                float cp = cdBluff * cosAlpha;

                if (streamlining > 0f)
                {
                    float recovery = cosAlpha * cosAlpha;
                    float minFraction = 0.08f;
                    float factor = minFraction + (1f - minFraction) * recovery;
                    cp *= 1f - streamlining * (1f - factor);
                }

                var pressureForce = face.Normal * (-cp * q * face.Area);
                totalForce -= pressureForce;
                totalTorque -= Vector3.Cross(face.Position - ctx.CenterOfMass, pressureForce);
            }
        }
    }
}
