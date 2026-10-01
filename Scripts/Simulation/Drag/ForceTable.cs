#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// A grid's face forces, precomputed for every flow direction: the per-frame cost no longer grows with the grid
/// (a loop over every hull face - 6.6 ms a frame for Red Ship - becomes a table lookup of a few microseconds).
///
/// The face model's force is the dynamic pressure q times a shape term that depends only on the flow direction,
/// except for its speed-of-sound blend (each windward face's Cp goes from bluff to Newtonian as Mach rises: linear in
/// the blend factor t) and the grid's rotation. So per direction it keeps, for unit q: the force and the torque about
/// the grid origin at t = 0 and at t = 1, the frontal area, and (on a coarser map) the torque's linear response to
/// rotation. At run time: F = q * wave * lerp(F0, F1, t); torque about the centre of mass from the origin's
/// (T_com = T_o - com x F); rotation damping from the matrix, scaled by q / speed.
///
/// Directions are a cube map with values at grid VERTICES (edge vertices are shared directions, so the map is
/// continuous across faces), sampled bilinearly. Index by the grid's direction of motion (grid-local).
/// </summary>
public sealed class ForceTable
{
    public readonly int N;            // cells per cube face side (values at N+1 x N+1 vertices)
    public readonly int NJ;           // the rotation map's resolution
    readonly float[] _f;              // per vertex: F0(3) F1(3) T0(3) T1(3) frontal(1) = 13
    readonly float[] _j;              // per vertex of the coarse map: dT/dw (9), for unit q / speed
    public const int Stride = 13;
    public int SurfaceVersion;        // the surface it was built from
    public Vector3 BuildCom;          // the centre of mass the rotation response was taken about
    public double BuildMs;

    public ForceTable(int n, int nj)
    {
        N = n; NJ = nj;
        _f = new float[6 * (n + 1) * (n + 1) * Stride];
        _j = new float[6 * (nj + 1) * (nj + 1) * 9];
    }

    /// <summary>The direction of vertex (face, i, j) on a map of n cells a side.</summary>
    public static Vector3 Direction(int face, int i, int j, int n)
    {
        float a = -1f + 2f * i / n, b = -1f + 2f * j / n;
        Vector3 v = face switch
        {
            0 => new Vector3(1, a, b), 1 => new Vector3(-1, a, b),
            2 => new Vector3(a, 1, b), 3 => new Vector3(a, -1, b),
            4 => new Vector3(a, b, 1), _ => new Vector3(a, b, -1),
        };
        return Vector3.Normalize(v);
    }

    /// <summary>A copy (both maps): an update builds the next table while this one is still read.</summary>
    public ForceTable Clone()
    {
        var t = new ForceTable(N, NJ) { SurfaceVersion = SurfaceVersion, BuildCom = BuildCom, BuildMs = BuildMs };
        System.Array.Copy(_f, t._f, _f.Length);
        System.Array.Copy(_j, t._j, _j.Length);
        return t;
    }

    public int Index(int face, int i, int j) => ((face * (N + 1) + i) * (N + 1) + j);
    public int IndexJ(int face, int i, int j) => ((face * (NJ + 1) + i) * (NJ + 1) + j);
    public Span<float> At(int face, int i, int j) => _f.AsSpan(Index(face, i, j) * Stride, Stride);
    public Span<float> AtJ(int face, int i, int j) => _j.AsSpan(IndexJ(face, i, j) * 9, 9);

    /// <summary>
    /// Damage, at once: a flat patch of faces (unit normal n, centre p grid-local, area - negative to take faces
    /// away) added to every direction, by the face model's own per-face terms (assumed reached by the air: the
    /// background rebuild brings the exact shadowing and fades in). ~0.1 ms for a large block's dozen patches.
    /// </summary>
    public void AddPatch(Vector3 n, Vector3 p, float area, float cdBluff, float cpMax, float cfSkin, float streamlining, float cpBase)
    {
        float baseCp = streamlining > 0f ? cpBase * (1f - 0.7f * streamlining) : cpBase;
        for (int face = 0; face < 6; face++)
            for (int i = 0; i <= N; i++)
                for (int j = 0; j <= N; j++)
                {
                    var v = Direction(face, i, j, N);
                    float cosA = Vector3.Dot(v, n);
                    float cpSub, cpSup, frontal = 0f;
                    if (cosA > 0)
                    {
                        cpSub = cdBluff * cosA; cpSup = cpMax * cosA * cosA;
                        if (streamlining > 0f) { float m = 1f - streamlining * (1f - (0.08f + 0.92f * cosA * cosA)); cpSub *= m; cpSup *= m; }
                        frontal = area * cosA;
                    }
                    else
                    {
                        float ramp = MathF.Min(1f, -cosA * 2f); ramp = ramp * ramp * (3f - 2f * ramp);
                        cpSub = cpSup = baseCp * ramp;
                    }
                    var fric = Vector3.Zero;
                    float sinSq = 1f - cosA * cosA;
                    if (cfSkin > 0 && sinSq > 1e-12f) fric = (v - cosA * n) * (cfSkin * area / MathF.Sqrt(sinSq));
                    var f0 = -cpSub * area * n + fric; var f1 = -cpSup * area * n + fric;
                    var t0 = Vector3.Cross(p, f0); var t1 = Vector3.Cross(p, f1);
                    var e = At(face, i, j);
                    e[0] += f0.X; e[1] += f0.Y; e[2] += f0.Z; e[3] += f1.X; e[4] += f1.Y; e[5] += f1.Z;
                    e[6] += t0.X; e[7] += t0.Y; e[8] += t0.Z; e[9] += t1.X; e[10] += t1.Y; e[11] += t1.Z;
                    e[12] += frontal;
                }
    }

    /// <summary>The cube face and (fractional) grid coordinates of a direction.</summary>
    static void Locate(Vector3 d, int n, out int face, out float u, out float w)
    {
        float ax = MathF.Abs(d.X), ay = MathF.Abs(d.Y), az = MathF.Abs(d.Z);
        float a, b;
        if (ax >= ay && ax >= az) { face = d.X >= 0 ? 0 : 1; a = d.Y / ax; b = d.Z / ax; }
        else if (ay >= az) { face = d.Y >= 0 ? 2 : 3; a = d.X / ay; b = d.Z / ay; }
        else { face = d.Z >= 0 ? 4 : 5; a = d.X / az; b = d.Y / az; }
        u = (a + 1f) * 0.5f * n; w = (b + 1f) * 0.5f * n;
        if (u < 0) u = 0; if (u > n) u = n; if (w < 0) w = 0; if (w > n) w = n;
    }

    /// <summary>Bilinear sample of the force map at a (unit) direction, into 13 floats.</summary>
    public void Sample(Vector3 dir, Span<float> into)
    {
        Locate(dir, N, out int face, out float u, out float w);
        int i0 = Math.Min((int)u, N - 1), j0 = Math.Min((int)w, N - 1);
        float fu = u - i0, fw = w - j0;
        var a = At(face, i0, j0); var b = At(face, i0 + 1, j0); var c = At(face, i0, j0 + 1); var d = At(face, i0 + 1, j0 + 1);
        float wa = (1 - fu) * (1 - fw), wb = fu * (1 - fw), wc = (1 - fu) * fw, wd = fu * fw;
        for (int k = 0; k < Stride; k++) into[k] = a[k] * wa + b[k] * wb + c[k] * wc + d[k] * wd;
    }

    /// <summary>Bilinear sample of the rotation map, into 9 floats (row-major dT/dw).</summary>
    public void SampleJ(Vector3 dir, Span<float> into)
    {
        Locate(dir, NJ, out int face, out float u, out float w);
        int i0 = Math.Min((int)u, NJ - 1), j0 = Math.Min((int)w, NJ - 1);
        float fu = u - i0, fw = w - j0;
        var a = AtJ(face, i0, j0); var b = AtJ(face, i0 + 1, j0); var c = AtJ(face, i0, j0 + 1); var d = AtJ(face, i0 + 1, j0 + 1);
        float wa = (1 - fu) * (1 - fw), wb = fu * (1 - fw), wc = (1 - fu) * fw, wd = fu * fw;
        for (int k = 0; k < 9; k++) into[k] = a[k] * wa + b[k] * wb + c[k] * wc + d[k] * wd;
    }

    /// <summary>The face forces for this flight state, as the face model would give them.</summary>
    public AeroResult Evaluate(in AeroContext ctx, float subsonicLimit, float supersonicLimit, float streamlining)
    {
        float speed = ctx.Speed;
        if (speed < 0.01f || ctx.Atmosphere.Density < 1e-8) return default;
        Vector3 vHat = ctx.Velocity / speed;
        Span<float> s = stackalloc float[Stride];
        Sample(vHat, s);

        float halfRho = (float)(0.5 * ctx.Atmosphere.Density);
        float q = halfRho * speed * speed;
        float invSoS = ctx.Atmosphere.SpeedOfSound > 0 ? 1f / (float)ctx.Atmosphere.SpeedOfSound : 0;
        float mach = speed * invSoS;
        float t = supersonicLimit > subsonicLimit ? (mach - subsonicLimit) / (supersonicLimit - subsonicLimit) : 0f;
        t = t < 0 ? 0 : t > 1 ? 1 : t;
        t = t * t * (3f - 2f * t);
        float wave = invSoS > 0 ? (float)CompressibleFlow.WaveDragMultiplier(mach, 0.7 + 0.1 * streamlining, 3.5 - 1.0 * streamlining) : 1f;
        float k = q * wave;

        var f = new Vector3(s[0] + (s[3] - s[0]) * t, s[1] + (s[4] - s[1]) * t, s[2] + (s[5] - s[2]) * t) * k;
        var to = new Vector3(s[6] + (s[9] - s[6]) * t, s[7] + (s[10] - s[7]) * t, s[8] + (s[11] - s[8]) * t) * k;
        var torque = to - Vector3.Cross(ctx.CenterOfMass, f);

        var w = ctx.AngularVelocity;
        if (w.X != 0 || w.Y != 0 || w.Z != 0)
        {
            Span<float> j = stackalloc float[9];
            SampleJ(vHat, j);
            float kj = halfRho * speed * wave;   // (dT/d(w/V) per unit q: x q / V)
            torque += new Vector3(j[0] * w.X + j[1] * w.Y + j[2] * w.Z, j[3] * w.X + j[4] * w.Y + j[5] * w.Z, j[6] * w.X + j[7] * w.Y + j[8] * w.Z) * kj;
        }

        float drag = -Vector3.Dot(f, vHat);
        var lift = f + drag * vHat;
        return new AeroResult(f, torque, MathF.Abs(drag), lift.Length(), s[12] * wave, mach, q);
    }
}
