using System.Runtime.CompilerServices;

namespace Aero.Core.Math;

/// <summary>
/// Integer 3D vector, parallel to SE2's Vector3I.
/// </summary>
public struct Vector3I : IEquatable<Vector3I>
{
    public int X, Y, Z;

    public Vector3I(int x, int y, int z) { X = x; Y = y; Z = z; }
    public Vector3I(int value) { X = value; Y = value; Z = value; }

    public static Vector3I Zero => new(0, 0, 0);
    public static Vector3I One => new(1, 1, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3I Min(in Vector3I a, in Vector3I b) =>
        new(System.Math.Min(a.X, b.X), System.Math.Min(a.Y, b.Y), System.Math.Min(a.Z, b.Z));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3I Max(in Vector3I a, in Vector3I b) =>
        new(System.Math.Max(a.X, b.X), System.Math.Max(a.Y, b.Y), System.Math.Max(a.Z, b.Z));

    public static Vector3I operator +(Vector3I a, Vector3I b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3I operator -(Vector3I a, Vector3I b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3I operator -(Vector3I a) => new(-a.X, -a.Y, -a.Z);
    public static bool operator ==(Vector3I a, Vector3I b) => a.X == b.X && a.Y == b.Y && a.Z == b.Z;
    public static bool operator !=(Vector3I a, Vector3I b) => !(a == b);

    public bool Equals(Vector3I other) => this == other;
    public override bool Equals(object? obj) => obj is Vector3I v && this == v;
    public override int GetHashCode() => HashCode.Combine(X, Y, Z);
    public override string ToString() => $"({X}, {Y}, {Z})";
}
