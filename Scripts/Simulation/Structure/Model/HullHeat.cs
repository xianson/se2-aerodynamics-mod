#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// HULL HEAT - aerodynamic heating of a grid's hull, face by face, pure math, no game (Tests/StructureTests).
///
/// HEAT IN (per face, W/m2): the air brings it at h (T0 - T), T0 the flow's total temperature (T + V^2 / 2cp: 776 K at
/// Mach 3, 2240 K at 2 km/s) and h from the stagnation-point correlation of Sutton and Graves, q = K sqrt(rho / R) V^3
/// for a cold wall, written as a coefficient: h = 2 cp K sqrt(rho / R) V. R is the LOCAL NOSE RADIUS: a sharp nose,
/// a thin leading edge, a corner, small R, much heat; a broad blunt face, large R, little - why capsules are blunt and
/// leading edges are rounded. Windward faces take it times cos(angle to the flow) (Newtonian-like). Every face also
/// has the attached flow along it: a flat plate's, Stanton St rho V cp (Reynolds' analogy; St 0.0015, turbulent) - the
/// wake's half that. At reentry densities the stagnation term rules (it goes as sqrt rho, the plate's as rho); low and
/// fast, the whole hull heats toward T0 (the "thermal thicket"); slow, the plate term is ordinary forced convection
/// (180 W/m2 K at 100 m/s at sea level) and cools a hot hull (T > T0: h (T0 - T) is negative).
///
/// R from the hull's shape (Pass, when the flow direction changes): a depth map of the hull seen from the flow (the
/// drag model's occlusion pass, at the face cells' pitch). From each windward face, along 8 directions, how far the
/// surface keeps within x/2 of the face's own TANGENT PLANE at distance x - a sphere of radius R does out to x = R, a
/// flat or inclined face to its edge, a cylinder (a wing's leading edge) to its own radius across it. The smallest over
/// the directions is R. (Measured from the tangent plane, not the depth: a slope is not a corner. Half a pixel more
/// allowed: a staircase of cells steps that much either side of its smoothed plane.)
///
/// HEAT OUT: each face re-radiates, eps sigma (T^4 - Tamb^4); each face exchanges with its block's CORE (the block's
/// other walls and insides: conduction plus the radiation across the hollow block, 4 eps sigma Tm^3); cores conduct to
/// the blocks they touch. A face's skin is its block's wall (ShipStructure.Wall: its mass over its cells' real surface)
/// over its area; the core is the rest
/// of the block's mass. Semi-implicit steps (any dt).
///
/// OUT: FaceT (skin K), BlockTempK (mass-weighted: what the structure's strength reads), BlockSkinMaxK (the hottest
/// skin: heat damage), FaceHeatW (each face's heat in, W - what a block thermal mod would take as its boundary flux).
/// </summary>
public sealed class HullHeat
{
    /// <summary>Sutton-Graves constant for air (SI: W/m2 with kg/m3, m, m/s).</summary>
    public const float SuttonGraves = 1.7415e-4f;
    public const float CpAir = 1005f, Sigma = 5.670e-8f;
    /// <summary>World multiplier on the heat in.</summary>
    public static float HeatMultiplier = 1f;
    /// <summary>The attached flow's Stanton number on every face (the wake's: LeeShare of it).</summary>
    public static float Stanton = 0.0015f, LeeShare = 0.5f;
    /// <summary>Largest local radius the depth map measures (m) and the conduction of the walls (W/m K).</summary>
    public static float MaxRadius = 20f, Conductivity = 45f;

    // ── faces ──
    public int FaceCount;
    float[] _px, _py, _pz, _nx, _ny, _nz, _area, _cap;
    public int[] FaceBlock;
    public float[] FaceT, FaceRadius, FaceHeatW;
    /// <summary>Each face's share of the flow's push (the last Pass): cos^2 x area if windward and seen, else 0 - the
    /// weights the air's force is spread over (Newtonian).</summary>
    public float[] FaceWind;
    /// <summary>Bumped by every Pass (FaceWind is written there only): the loads' windward set, cached on it
    /// (StructureEval.WindSet), is made again when it moved (2026-10-15).</summary>
    public int PassVersion;
    internal StructureEval.WindSet Wind;
    float[] _geo;   // the face's stagnation coefficient per h0 (1/sqrt(m)): cos / sqrt(R), windward and seen
    float[] _side;  // its attached-flow share: 1, the wake LeeShare
    float _pitch;

    // ── blocks ──
    public int BlockCount;
    float[] _coreC, _blockMassC;
    int[] _mat;
    public float[] CoreT, BlockTempK, BlockSkinMaxK;
    int[] _condA, _condB; float[] _condG;
    public float BodyRadius;

    public Vector3 PassDir;
    public bool HasPass;

    /// <summary>The hull's faces (position, outward normal, area, block) on a structure's blocks. pitch: the face cells'
    /// size (the depth map's pixel).</summary>
    public static HullHeat Build(ShipStructure s, IReadOnlyList<Vector3> pos, IReadOnlyList<Vector3> nrm, IReadOnlyList<float> area, IReadOnlyList<int> block, float pitch, float startK = 290f)
    {
        if (CullHidden && pos.Count > 0)
        {
            // only the hull: faces seen from some direction - a cavity's walls (a third of a real ship's faces) were heated
            // by the flow and radiated to space as if outside (2026-10-14 review: 580,890 faces, 34% never visible)
            var seen = Visible(pos, nrm, MathF.Max(pitch, 0.1f));
            var p2 = new List<Vector3>(); var n2 = new List<Vector3>(); var a2 = new List<float>(); var b2 = new List<int>();
            for (int i = 0; i < pos.Count; i++) if (seen[i]) { p2.Add(pos[i]); n2.Add(nrm[i]); a2.Add(area[i]); b2.Add(block[i]); }
            pos = p2; nrm = n2; area = a2; block = b2;
        }
        var h = new HullHeat();
        int n = pos.Count, nb = s.Count;
        h.FaceCount = n; h.BlockCount = nb; h._pitch = MathF.Max(pitch, 0.1f);
        h._px = new float[n]; h._py = new float[n]; h._pz = new float[n]; h._nx = new float[n]; h._ny = new float[n]; h._nz = new float[n];
        h._area = new float[n]; h._cap = new float[n]; h.FaceBlock = new int[n];
        h.FaceT = new float[n]; h.FaceRadius = new float[n]; h.FaceHeatW = new float[n]; h._geo = new float[n]; h._side = new float[n]; h.FaceWind = new float[n];
        h._mat = new int[nb]; h._coreC = new float[nb]; h._blockMassC = new float[nb];
        h.CoreT = new float[nb]; h.BlockTempK = new float[nb]; h.BlockSkinMaxK = new float[nb];
        var skinMass = new float[nb];
        for (int i = 0; i < n; i++)
        {
            h._px[i] = pos[i].X; h._py[i] = pos[i].Y; h._pz[i] = pos[i].Z;
            h._nx[i] = nrm[i].X; h._ny[i] = nrm[i].Y; h._nz[i] = nrm[i].Z;
            h._area[i] = area[i];
            int b = block[i]; h.FaceBlock[i] = b;
            h.FaceT[i] = startK;
            if (b < 0) { h._cap[i] = 1e9f; continue; }
            float m = s.Wall[b] * area[i] * StructMaterial.SteelDensity;
            skinMass[b] += m;
            h._cap[i] = MathF.Max(m * StructMaterial.SpecificHeat[s.Mat[b]], 1f);
        }
        for (int b = 0; b < nb; b++)
        {
            int mat = s.Mat[b]; float c = StructMaterial.SpecificHeat[mat];
            h._mat[b] = mat;
            // (a block whose exposed walls would be more than its mass - a thin plate seen on both sides - keeps a tenth for its core)
            float core = MathF.Max(s.Mass[b] - skinMass[b], 0.1f * s.Mass[b]);
            h._coreC[b] = core * c;
            h._blockMassC[b] = (core + skinMass[b]) * c;
            h.CoreT[b] = startK; h.BlockTempK[b] = startK; h.BlockSkinMaxK[b] = startK;
        }
        // cores conduct through their joints' walls (patch outline x wall, over the centres' distance)
        var a = new List<int>(); var bb = new List<int>(); var g = new List<float>();
        for (int i = 0; i < nb; i++)
        {
            int p = s.Parent[i];
            if (p < 0) continue;
            float t = MathF.Min(s.Wall[i], s.Wall[p]);
            float d = MathF.Max((s.Center[i] - s.Center[p]).Length(), 0.1f);
            a.Add(i); bb.Add(p); g.Add(Conductivity * 2f * (s.JA[i] + s.JB[i]) * t / d);
        }
        h._condA = a.ToArray(); h._condB = bb.ToArray(); h._condG = g.ToArray();
        // the body's own radius: half its smallest extent across... (the frontal area's equivalent circle, refreshed by Pass)
        var ext = (MaxV(s.Hi, nb) - MinV(s.Lo, nb));
        h.BodyRadius = MathF.Max(0.5f * MathF.Min(ext.X, MathF.Min(ext.Y, ext.Z)), 0.25f);
        return h;
    }

    /// <summary>Drop the faces never seen from outside (off: every face, as before).</summary>
    public static bool CullHidden = true;

    /// <summary>Which faces some direction sees: for each of the 26 axis, edge and corner directions, a depth map (pixel P)
    /// of the faces along it, and a face facing that way within its pixel's front (Pass's own test) is seen.</summary>
    public static bool[] Visible(IReadOnlyList<Vector3> pos, IReadOnlyList<Vector3> nrm, float P)
    {
        int n = pos.Count; var seen = new bool[n]; float inv = 1f / P;
        var zb = Array.Empty<float>(); var pix = new int[n];
        for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++)
        {
            if (dx == 0 && dy == 0 && dz == 0) continue;
            var v = Vector3.Normalize(new Vector3(dx, dy, dz));   // (the way the eye looks from outside: faces facing -v... see below)
            var seed = MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var e1 = Vector3.Normalize(Vector3.Cross(v, seed)); var e2 = Vector3.Cross(v, e1);
            float umin = float.MaxValue, umax = float.MinValue, wmin = float.MaxValue, wmax = float.MinValue;
            for (int i = 0; i < n; i++)
            {
                float u = Vector3.Dot(pos[i], e1), w = Vector3.Dot(pos[i], e2);
                if (u < umin) umin = u; if (u > umax) umax = u; if (w < wmin) wmin = w; if (w > wmax) wmax = w;
            }
            umin = MathF.Floor(umin * inv) * P; wmin = MathF.Floor(wmin * inv) * P;
            int W = (int)((umax - umin) * inv) + 1, H = (int)((wmax - wmin) * inv) + 1;
            if (zb.Length < W * H) zb = new float[W * H];
            Array.Fill(zb, float.MinValue, 0, W * H);
            // (as Pass: the flow travels along v, and the face whose depth along v is greatest in its pixel is the one it meets -
            //  here the "flow" stands for an eye looking along v)
            for (int i = 0; i < n; i++)
            {
                int iu = (int)((Vector3.Dot(pos[i], e1) - umin) * inv), iw = (int)((Vector3.Dot(pos[i], e2) - wmin) * inv);
                int px = iw * W + iu; pix[i] = px;
                float d = Vector3.Dot(pos[i], v);
                if (d > zb[px]) zb[px] = d;
            }
            for (int i = 0; i < n; i++)
            {
                if (seen[i]) continue;
                float c = Vector3.Dot(nrm[i], v);
                if (!(c > 0.02f)) continue;
                float tol = 0.4f * P + P * MathF.Sqrt(MathF.Max(0f, 1f - c * c)) / c;
                if (Vector3.Dot(pos[i], v) >= zb[pix[i]] - tol) seen[i] = true;
            }
        }
        return seen;
    }

    static Vector3 MaxV(Vector3[] v, int n) { var r = new Vector3(float.MinValue); for (int i = 0; i < n; i++) r = Vector3.Max(r, v[i]); return r; }
    static Vector3 MinV(Vector3[] v, int n) { var r = new Vector3(float.MaxValue); for (int i = 0; i < n; i++) r = Vector3.Min(r, v[i]); return r; }

    // ── the geometry pass ──
    float[] _zb = Array.Empty<float>();
    int[] _pix = Array.Empty<int>();

    /// <summary>Whether the flow direction moved more than maxDeg from the last pass.</summary>
    public bool NeedsPass(Vector3 dir, float maxDeg = 4f) => !HasPass || Vector3.Dot(Vector3.Normalize(dir), PassDir) < MathF.Cos(maxDeg * MathF.PI / 180f);

    /// <summary>The faces' heat coefficients for travel along dir (grid-local): visibility, local radius, angle.</summary>
    public void Pass(Vector3 dir)
    {
        var v = Vector3.Normalize(dir);
        PassDir = v; HasPass = true; PassVersion++;
        var seed = MathF.Abs(v.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var e1 = Vector3.Normalize(Vector3.Cross(v, seed)); var e2 = Vector3.Cross(v, e1);
        float P = _pitch, inv = 1f / P;
        int n = FaceCount;
        float umin = float.MaxValue, umax = float.MinValue, wmin = float.MaxValue, wmax = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            float u = _px[i] * e1.X + _py[i] * e1.Y + _pz[i] * e1.Z, w = _px[i] * e2.X + _py[i] * e2.Y + _pz[i] * e2.Z;
            if (u < umin) umin = u; if (u > umax) umax = u; if (w < wmin) wmin = w; if (w > wmax) wmax = w;
        }
        if (n == 0) return;
        umin = MathF.Floor(umin * inv) * P; wmin = MathF.Floor(wmin * inv) * P;
        int W = (int)((umax - umin) * inv) + 1, H = (int)((wmax - wmin) * inv) + 1;
        if (_zb.Length < W * H) _zb = new float[W * H];
        if (_pix.Length < n) _pix = new int[n];
        Array.Fill(_zb, float.MinValue, 0, W * H);
        for (int i = 0; i < n; i++)
        {
            int iu = (int)((_px[i] * e1.X + _py[i] * e1.Y + _pz[i] * e1.Z - umin) * inv), iw = (int)((_px[i] * e2.X + _py[i] * e2.Y + _pz[i] * e2.Z - wmin) * inv);
            int px = iw * W + iu; _pix[i] = px;
            float d = _px[i] * v.X + _py[i] * v.Y + _pz[i] * v.Z;
            if (d > _zb[px]) _zb[px] = d;
        }
        // the body's radius: its frontal area's equivalent circle
        int lit = 0; for (int k = 0; k < W * H; k++) if (_zb[k] > float.MinValue) lit++;
        BodyRadius = MathF.Max(MathF.Sqrt(lit * P * P / MathF.PI), P);
        for (int i = 0; i < n; i++)
        {
            float c = _nx[i] * v.X + _ny[i] * v.Y + _nz[i] * v.Z;
            float g = 0f, r = BodyRadius;
            FaceWind[i] = 0f;
            _side[i] = c < -0.02f ? LeeShare : 1f;
            if (c > 0.02f)
            {
                float d = _px[i] * v.X + _py[i] * v.Y + _pz[i] * v.Z;
                float tol = 0.4f * P + P * MathF.Sqrt(MathF.Max(0f, 1f - c * c)) / c;
                if (d >= _zb[_pix[i]] - tol)
                {
                    // the tangent plane's depth slope across the map, per metre along e1 and e2
                    float s1 = -Math.Clamp((_nx[i] * e1.X + _ny[i] * e1.Y + _nz[i] * e1.Z) / c, -5f, 5f);
                    float s2 = -Math.Clamp((_nx[i] * e2.X + _ny[i] * e2.Y + _nz[i] * e2.Z) / c, -5f, 5f);
                    r = LocalRadius(_pix[i], W, H, P, d, s1, s2);
                    g = c / MathF.Sqrt(r);
                    FaceWind[i] = c * c * _area[i];
                }
            }
            _geo[i] = g; FaceRadius[i] = r;
        }
    }

    static readonly int[] DU = { 1, -1, 0, 0, 1, 1, -1, -1 }, DW = { 0, 0, 1, -1, 1, -1, 1, -1 };

    /// <summary>How far the surface keeps within x/2 (+ half a pixel) of the face's tangent plane at distance x - the
    /// face at depth z0 in pixel px, the plane's depth slopes s1, s2 along the map's axes - the least over 8 directions (m).</summary>
    float LocalRadius(int px, int W, int H, float P, float z0, float s1, float s2)
    {
        int iu = px % W, iw = px / W;
        float best = MaxRadius;
        for (int d = 0; d < 8; d++)
        {
            float step = (d < 4 ? 1f : 1.41421356f) * P;
            int maxS = (int)(best / step) + 1;
            for (int s = 1; s <= maxS; s++)
            {
                int u = iu + DU[d] * s, w = iw + DW[d] * s;
                float x = s * step;
                bool fell = u < 0 || w < 0 || u >= W || w >= H;
                if (!fell)
                {
                    float z = _zb[w * W + u];
                    float plane = z0 + (DU[d] * s1 + DW[d] * s2) * s * P;
                    fell = z == float.MinValue || plane - z > 0.5f * x + 0.5f * P;
                }
                if (fell) { if (x < best) best = x; break; }
            }
        }
        // (a pixel's own half-width is the least we can tell)
        return MathF.Max(best - 0.5f * P, 0.5f * P);
    }

    /// <summary>One step: air at density rho (kg/m3), static temperature tAir (K), the grid's airspeed (m/s), ambient
    /// radiation temperature tAmb (K: the air's in an atmosphere, ~3 in space). dt in seconds (any).</summary>
    public void Step(float rho, float tAir, float speed, float tAmb, float dt)
    {
        float t0 = tAir + speed * speed / (2f * CpAir);
        float h0 = HasPass && rho > 0f ? 2f * CpAir * SuttonGraves * MathF.Sqrt(rho) * speed * HeatMultiplier : 0f;
        float hPlate = HasPass && rho > 0f ? Stanton * rho * speed * CpAir * HeatMultiplier : 0f;
        float amb4 = tAmb * tAmb * tAmb * tAmb;
        var em = StructMaterial.Emissivity;
        var coreIn = _coreIn ??= new float[BlockCount]; var coreG = _coreG ??= new float[BlockCount];
        Array.Clear(coreIn, 0, BlockCount); Array.Clear(coreG, 0, BlockCount);
        for (int i = 0; i < FaceCount; i++)
        {
            int b = FaceBlock[i];
            if (b < 0) continue;
            float T = FaceT[i], A = _area[i], e = em[_mat[b]];
            float h = h0 * _geo[i] + hPlate * _side[i];
            float Tc = CoreT[b], Tm = 0.5f * (T + Tc);
            // skin to core: the wall's own conduction (a plate's edge into the next walls) plus radiation across the hollow
            float hInt = Conductivity * 0.004f / 0.5f + 4f * e * Sigma * Tm * Tm * Tm;
            float T3 = T * T * T;
            float qIn = h * (t0 - T);
            FaceHeatW[i] = qIn * A;
            float net = A * (qIn - e * Sigma * (T3 * T - amb4) - hInt * (T - Tc));
            float lin = A * (h + 4f * e * Sigma * T3 + hInt);   // d(-net)/dT
            float dT = dt * net / (_cap[i] + dt * lin);
            FaceT[i] = MathF.Max(T + dT, 3f);
            coreIn[b] += A * hInt * (FaceT[i] - Tc);
            coreG[b] += A * hInt;
        }
        // cores: what their skins pass them, and their neighbours' conduction (semi-implicit on their own temperature)
        for (int k = 0; k < _condA.Length; k++)
        {
            int a = _condA[k], c = _condB[k]; float g = _condG[k];
            float q = g * (CoreT[c] - CoreT[a]);
            coreIn[a] += q; coreIn[c] -= q; coreG[a] += g; coreG[c] += g;
        }
        for (int b = 0; b < BlockCount; b++)
            CoreT[b] += dt * coreIn[b] / (_coreC[b] + dt * coreG[b]);
        // per block: its mass-weighted temperature and its hottest skin
        var acc = _acc ??= new float[BlockCount];
        for (int b = 0; b < BlockCount; b++) { acc[b] = _coreC[b] * CoreT[b]; BlockSkinMaxK[b] = CoreT[b]; }
        for (int i = 0; i < FaceCount; i++)
        {
            int b = FaceBlock[i]; if (b < 0) continue;
            acc[b] += _cap[i] * FaceT[i];
            if (FaceT[i] > BlockSkinMaxK[b]) BlockSkinMaxK[b] = FaceT[i];
        }
        for (int b = 0; b < BlockCount; b++) BlockTempK[b] = acc[b] / MathF.Max(_blockMassC[b], 1e-3f);
    }

    float[] _coreIn, _coreG, _acc;

    /// <summary>The hottest skin (K) and its face.</summary>
    public float MaxSkin(out int face)
    {
        face = -1; float m = 0f;
        for (int i = 0; i < FaceCount; i++) if (FaceT[i] > m) { m = FaceT[i]; face = i; }
        return m;
    }

    public Vector3 FacePosition(int i) => new Vector3(_px[i], _py[i], _pz[i]);
    public Vector3 FaceNormal(int i) => new Vector3(_nx[i], _ny[i], _nz[i]);
    public float FaceArea(int i) => _area[i];
}
