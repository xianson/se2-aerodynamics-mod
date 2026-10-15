#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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

    /// <summary>The list's lock: every change takes it (the simulation thread, rare - rebuilds and block changes), and a
    /// reader on another thread copies under it (AeroApi.GetAeroBlocks / GetAeroBlockState: they iterated the list while
    /// a rebuild cleared and refilled it - a torn list or a throw, 2026-10-15). The simulation thread's own reads need none.</summary>
    internal object SyncRoot => _components;

    /// <summary>Remove all components.</summary>
    public void Clear() { lock (_components) _components.Clear(); }

    /// <summary>Add a component to the registry.</summary>
    public void Add(IAeroBlockComponent component)
    {
        lock (_components) _components.Add(component);
    }

    /// <summary>Remove a specific component.</summary>
    public bool Remove(IAeroBlockComponent component)
    {
        lock (_components) return _components.Remove(component);
    }

    /// <summary>Remove all components at a given block position.</summary>
    /// <summary>The components of blocks inside a box of cells (one pass: per cell it was a pass per cell).</summary>
    public int RemoveInBox(Vector3I min, Vector3I max)
    {
        int removed = 0;
        lock (_components)
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

    /// <summary>A face's Newtonian Cp at cos(theta) to the flow, with streamlining (both subtract paths).</summary>
    private static float FaceCp(float cosAlpha, float cdBluff, float streamlining)
    {
        float cp = DampedShadowedDragModel.SubsonicSquareLaw ? cdBluff * cosAlpha * cosAlpha : cdBluff * cosAlpha;

        if (streamlining > 0f && !DampedShadowedDragModel.SubsonicSquareLaw)
        {
            float recovery = cosAlpha * cosAlpha;
            float minFraction = 0.08f;
            float factor = minFraction + (1f - minFraction) * recovery;
            cp *= 1f - streamlining * (1f - factor);
        }
        return cp;
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

                float cp = FaceCp(cosAlpha, cdBluff, streamlining);

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

                float cp = FaceCp(cosAlpha, cdBluff, streamlining);

                var pressureForce = face.Normal * (-cp * q * face.Area);
                totalForce -= pressureForce;
                totalTorque -= Vector3.Cross(face.Position - ctx.CenterOfMass, pressureForce);
            }
        }
    }
}
