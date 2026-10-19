using System.Runtime.CompilerServices;

namespace Aero.Core.Math;

/// <summary>
/// Integer axis-aligned bounding box, parallel to SE2's BoundingBoxI.
/// Min and Max are both inclusive.
/// </summary>
public struct BoundingBoxI : IEquatable<BoundingBoxI>
{
    public Vector3I Min;
    public Vector3I Max;

    public BoundingBoxI(Vector3I min, Vector3I max)
    {
        Min = min;
        Max = max;
    }

    /// <summary>Size including max (Max - Min + 1) per axis.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3I GetSizeIncludingMax() => new(Max.X - Min.X + 1, Max.Y - Min.Y + 1, Max.Z - Min.Z + 1);

    /// <summary>Floor of center: (Min + Max) / 2 with integer division (rounds toward negative infinity).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Vector3I GetCenterFloored() => new(
        FloorDiv(Min.X + Max.X, 2),
        FloorDiv(Min.Y + Max.Y, 2),
        FloorDiv(Min.Z + Max.Z, 2));

    public bool IsValid() => Min.X <= Max.X && Min.Y <= Max.Y && Min.Z <= Max.Z;

    /// <summary>Returns true if the given position is inside the box (inclusive on both ends).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(Vector3I point) =>
        point.X >= Min.X && point.X <= Max.X &&
        point.Y >= Min.Y && point.Y <= Max.Y &&
        point.Z >= Min.Z && point.Z <= Max.Z;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static BoundingBoxI Intersection(in BoundingBoxI a, in BoundingBoxI b) => new(
        Vector3I.Max(in a.Min, in b.Min),
        Vector3I.Min(in a.Max, in b.Max));

    public static BoundingBoxI CreateInvalid() => new(
        new Vector3I(int.MaxValue), new Vector3I(int.MinValue));

    public static bool operator ==(BoundingBoxI a, BoundingBoxI b) => a.Min == b.Min && a.Max == b.Max;
    public static bool operator !=(BoundingBoxI a, BoundingBoxI b) => !(a == b);

    public bool Equals(BoundingBoxI other) => this == other;
    public override bool Equals(object? obj) => obj is BoundingBoxI b && this == b;
    public override int GetHashCode() => HashCode.Combine(Min, Max);
    public override string ToString() => $"[{Min} - {Max}]";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FloorDiv(int a, int b)
    {
        // Integer division that floors toward negative infinity
        return System.Math.DivRem(a, b, out int rem) - (rem != 0 && (rem ^ b) < 0 ? 1 : 0);
    }
}
