#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
namespace AeroMod;

/// <summary>
/// An exposed face on the grid surface.
/// </summary>
public readonly struct SurfaceFace
{
    /// <summary>World-space center of the face (meters).</summary>
    public readonly Vector3 Position;

    /// <summary>Outward unit normal.</summary>
    public readonly Vector3 Normal;

    /// <summary>Face area in m².</summary>
    public readonly float Area;

    /// <summary>The cell face's own axis direction, before smoothing (zero: not known).</summary>
    public readonly Vector3 RawNormal;

    public SurfaceFace(Vector3 position, Vector3 normal, float area) : this(position, normal, area, Vector3.Zero) { }

    public SurfaceFace(Vector3 position, Vector3 normal, float area, Vector3 rawNormal)
    {
        Position = position;
        Normal = normal;
        Area = area;
        RawNormal = rawNormal;
    }
}
