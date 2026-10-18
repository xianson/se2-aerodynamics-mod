#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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

    // ── STREAMLINING, per ship and per direction (2026-10-10 - it was a fixed 0.4 for every grid: a brick and a dart met
    //    the same transonic wall, 3.1x at Mach 1.05): how the hull's drag along the flow compares with a flat face of the
    //    same frontal area - a block face-on (StreamCdBrick) streamlining 0, a fine sloped nose (StreamCdNeedle) 1.
    //    (Stepping blocks down does not help: each step is a flat face to the flow; slopes do.) It sets the wave drag: its onset (0.7 + 0.1 st) and its peak, from WavePeakBrick
    //    (st 0) down to WavePeakNeedle (st 1). Read off the table's own subsonic drag - nothing new is built. ──
    public static bool ShapeStreamliningOn = true;
    /// <summary>The Cd (drag / (q x the table's frontal area)) of a plain block face-on - the model's own, measured on a
    /// 4 m cube (AeroBench streamline: 0.87-0.90; a stepped cone of blocks the same: steps are flat faces) - and of a fine
    /// sloped nose (~2:1 slope blocks), the two ends of the scale.</summary>
    public static float StreamCdBrick = 0.88f, StreamCdNeedle = 0.35f, WavePeakBrick = 3.2f, WavePeakNeedle = 1.12f;
    public static float StreamliningOf(float cd) => Math.Clamp((StreamCdBrick - cd) / Math.Max(0.01f, StreamCdBrick - StreamCdNeedle), 0f, 1f);
    /// <summary>The transonic drag peak (x the subsonic drag) of a streamlining: the old law's line from WavePeakBrick to
    /// WavePeakOldNeedle up to SleekFrom, then on to WavePeakNeedle (the sleek hull's wall, below).</summary>
    public static float WavePeak(float st)
    {
        st = Math.Clamp(st, 0f, 1f);
        float lin = WavePeakBrick + (WavePeakOldNeedle - WavePeakBrick) * MathF.Min(st, SleekFrom);
        return st <= SleekFrom ? lin : lin + (WavePeakNeedle - lin) * Sleekness(st);
    }
    /// <summary>
    /// THE SLEEK HULL'S MACH WALL (2026-10-17, the user's speed roles - "Mach 1 is a real wall", a sleek fighter just breaks it
    /// and then cruises supersonic more easily than the crossing; Tests/AeroBench machwall). Before, a sleek hull's drag rose
    /// 3.3x by Mach 1.05 and kept rising to ~4x by Mach 1.2 (the wave peak 1.2 x the full blend to the Newtonian table, ~3.6x
    /// the subsonic drag on the F-6): it needed a static thrust-to-weight of 8.3 to break Mach 1 and topped out at 1.05. Now
    /// the wave drag carries the wall for a sleek hull - a sharp peak (WavePeakNeedle 1.12x; 1.45x until the wall was made
    /// reachable the same day - with the engines' 0.55 pinch that asked a static T/W of 3.4 at sea level and 2.6 at 0.4
    /// kg/m3; now 1.9 and 1.5 with the 0.76 pinch and the ram rise, Tests/AeroBench machwall) AT Mach 1 (WavePeakMachNeedle:
    /// the pinch of the engines' curve), falling fast past it (WaveFallNeedle: (peak / M)^8, ~1.01x by Mach 1.4) - and its
    /// pressures HOLD their subsonic recovery through the jet band (SleekHold: the blend to the Newtonian table is held back
    /// from Mach 0.75-0.85 up to 1.6, released fully by 3.0 - hypersonic flight, entry, keeps the Newtonian law).
    /// It grows from streamlining SleekFrom (0.5, where the wave drag starts at Mach 0.75) to 1 (Sleekness); below SleekFrom
    /// - a brick - everything is the old law (peak by the old line, at Mach 1.05, fall ^1.3, the full blend). So below Mach
    /// 0.75 nothing changed at any streamlining (the subsonic calibrations: the Tu-134's L/D at Mach 0.79 - machwall checks
    /// it). WavePeakNeedle 1.2, WavePeakMachNeedle 1.05, WaveFallNeedle 1.3, SleekHold 0: the old law exactly.
    /// </summary>
    public static float WavePeakOldNeedle = 1.2f, SleekFrom = 0.5f;
    public static float WavePeakMachBrick = 1.05f, WavePeakMachNeedle = 1.0f, WaveFallBrick = 1.3f, WaveFallNeedle = 8f, SleekHold = 1f;
    /// <summary>0 up to SleekFrom, 1 at streamlining 1 (linear): how much of the sleek hull's law a streamlining gets.</summary>
    public static float Sleekness(float st) => Math.Clamp((st - SleekFrom) / MathF.Max(1e-3f, 1f - SleekFrom), 0f, 1f);
    public static float WavePeakMach(float st) => WavePeakMachBrick + (WavePeakMachNeedle - WavePeakMachBrick) * Sleekness(st);
    public static float WaveFall(float st) => WaveFallBrick + (WaveFallNeedle - WaveFallBrick) * Sleekness(st);
    /// <summary>Where the sleek hold acts (Mach): on from HoldOn0 to HoldOn1, off from HoldOff0 to HoldOff1 (smoothsteps).</summary>
    public const float HoldOn0 = 0.75f, HoldOn1 = 0.85f, HoldOff0 = 1.6f, HoldOff1 = 3.0f;
    /// <summary>The share of the Newtonian blend held back at this Mach for this streamlining (0..1: 0 the full blend).</summary>
    public static float BlendHold(float mach, float st)
    {
        float k = SleekHold * Sleekness(st);
        if (k <= 0f || !(mach > HoldOn0) || mach >= HoldOff1) return 0f;
        float a = Math.Clamp((mach - HoldOn0) / (HoldOn1 - HoldOn0), 0f, 1f), b = Math.Clamp((mach - HoldOff0) / (HoldOff1 - HoldOff0), 0f, 1f);
        a = a * a * (3f - 2f * a); b = b * b * (3f - 2f * b);
        return Math.Clamp(k * a * (1f - b), 0f, 1f);
    }
    /// <summary>The hull's wave drag multiplier at a Mach for a streamlining (the one law: the table and the face loop).</summary>
    public static float HullWave(float mach, float st)
        => (float)CompressibleFlow.WaveDragMultiplier(mach, 0.7 + 0.1 * st, WavePeak(st), WavePeakMach(st), WaveFall(st));
    /// <summary>
    /// TRANSONIC ONSET (2026-10-12, the user: a Tu-134 at Mach 0.79 flew at L/D 3.4 where the real one makes ~7): where the
    /// hull's pressures begin to turn from the subsonic table (pressure recovered behind a smooth shape) to the supersonic
    /// one (Newtonian, none recovered). It was the model's SubsonicLimit (0.6) for every shape - the Tu-134's whole drag
    /// doubled from Mach 0.5 to 0.8, before a real airliner's drag has risen at all - and the wave drag then rose from 0.7
    /// to 0.8 on top. Now it starts with the critical Mach: SubsonicLimit for a brick (streamlining 0), TransonicSleek
    /// for a sleek hull (1) - the wave drag's onset moves the same way (0.7 + 0.1 st).
    /// </summary>
    public static float TransonicSleek = 0.8f;
    public static float TransonicOnset(float subsonicLimit, float st) => subsonicLimit + (MathF.Max(subsonicLimit, TransonicSleek) - subsonicLimit) * Math.Clamp(st, 0f, 1f);
    /// <summary>The speed-of-sound blend (0: the subsonic table, 1: the Newtonian one) at a Mach for a streamlining - the
    /// table's one law (Evaluate), read by the wings' wave-drag guard too (2026-10-18, fix a-wing-wave: what the hull already
    /// charges on a wing's edge faces - CompressibleWingModel.WingWaveDrag).</summary>
    public static float NewtonBlend(float mach, float subsonicLimit, float supersonicLimit, float st)
    {
        float lo = TransonicOnset(subsonicLimit, st);
        float t = supersonicLimit > lo ? (mach - lo) / (supersonicLimit - lo) : 0f;
        t = t < 0 ? 0 : t > 1 ? 1 : t;
        t = t * t * (3f - 2f * t);
        return t * (1f - BlendHold(mach, st));   // (a sleek hull keeps its subsonic recovery through the jet band: BlendHold)
    }
    /// <summary>The streamlining along vHat from a sampled table entry (s: Stride values); the fixed one when off or
    /// when there is no frontal area.</summary>
    public static float ShapeStreamlining(ReadOnlySpan<float> s, Vector3 vHat, float fallback)
    {
        if (!ShapeStreamliningOn) return fallback;
        float area = s[12];
        if (!(area > 1e-3f)) return fallback;
        float drag = -(s[0] * vHat.X + s[1] * vHat.Y + s[2] * vHat.Z);
        return StreamliningOf(drag / area);
    }
    /// <summary>The streamlining along a direction (grid-local), for displays.</summary>
    public float StreamliningAlong(Vector3 vHat, float fallback)
    {
        Span<float> s = stackalloc float[Stride];
        Sample(Vector3.Normalize(vHat), s);
        return ShapeStreamlining(s, Vector3.Normalize(vHat), fallback);
    }

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

    /// <summary>Saved (AeroTableCache): both maps.</summary>
    public void Write(System.IO.BinaryWriter w)
    {
        w.Write(N); w.Write(NJ); w.Write(BuildCom.X); w.Write(BuildCom.Y); w.Write(BuildCom.Z);
        foreach (float x in _f) w.Write(x);
        foreach (float x in _j) w.Write(x);
    }

    public static ForceTable Read(System.IO.BinaryReader r)
    {
        int n = r.ReadInt32(), nj = r.ReadInt32();
        if (n < 1 || n > 64 || nj < 1 || nj > 64) throw new System.IO.InvalidDataException("table size");
        var t = new ForceTable(n, nj) { BuildCom = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()) };
        for (int i = 0; i < t._f.Length; i++) t._f[i] = r.ReadSingle();
        for (int i = 0; i < t._j.Length; i++) t._j[i] = r.ReadSingle();
        return t;
    }

    public int Index(int face, int i, int j) => ((face * (N + 1) + i) * (N + 1) + j);
    public int IndexJ(int face, int i, int j) => ((face * (NJ + 1) + i) * (NJ + 1) + j);
    public Span<float> At(int face, int i, int j) => _f.AsSpan(Index(face, i, j) * Stride, Stride);
    public Span<float> AtJ(int face, int i, int j) => _j.AsSpan(IndexJ(face, i, j) * 9, 9);

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
        // (a NaN or infinite velocity made the table index int.MinValue and throw every frame - ~200 ms each)
        if (!float.IsFinite(speed) || speed < 0.01f || ctx.Atmosphere.Density < 1e-8) return default;
        Vector3 vHat = ctx.Velocity / speed;
        Span<float> s = stackalloc float[Stride];
        Sample(vHat, s);

        float halfRho = (float)(0.5 * ctx.Atmosphere.Density);
        float q = halfRho * speed * speed;
        float invSoS = ctx.Atmosphere.SpeedOfSound > 0 ? 1f / (float)ctx.Atmosphere.SpeedOfSound : 0;
        float mach = speed * invSoS;
        float st = ShapeStreamlining(s, vHat, streamlining);
        float t = NewtonBlend(mach, subsonicLimit, supersonicLimit, st);
        float wave = invSoS > 0 ? HullWave(mach, st) : 1f;
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
