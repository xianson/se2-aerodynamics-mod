#pragma warning disable
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

    public SurfaceFace(Vector3 position, Vector3 normal, float area)
    {
        Position = position;
        Normal = normal;
        Area = area;
    }
}
