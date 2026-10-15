#pragma warning disable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AeroMod;

/// <summary>
/// Shadow + Newtonian drag with per-face rotational velocity (ω×r).
/// Direction-grouped SoA: faces sorted by 6 axis normals, only front-facing
/// groups are iterated. AVX2 SIMD: 8 faces per iteration.
/// </summary>
public partial class DampedShadowedDragModel : IAeroDragModel
{
    public string Name => "Newtonian + Shadow + Damping (AVX2)";

    private PrecomputedShadowMap _shadowMap = new();

    /// <summary>A shadow map precomputed for the surface about to go live (a background rebuild).</summary>
    /// <summary>Returns the map it replaces (to be reused for the next rebuild).</summary>
    public PrecomputedShadowMap InstallShadowMap(PrecomputedShadowMap map)
    {
        if (map == null) return null;
        var old = _shadowMap;
        _shadowMap = map;
        _lastShadowVersion = -1; _loopBakeVersion = int.MinValue;   // bake visibility from it on the next compute
        return old;
    }

    // ─── Tuning ─────────────────────────────────────────────────────

    public float CpMax { get; set; } = 2.0f;
    public float CdBluff { get; set; } = 1.1f;
    public float CpBase { get; set; } = -0.15f;
    public float CfSkin { get; set; } = 0.005f;
    public float SubsonicLimit { get; set; } = 0.6f;
    public float SupersonicLimit { get; set; } = 1.2f;
    public float Streamlining { get; set; } = 0.4f;
    /// <summary>The subsonic pressure law (2026-10-10, AeroBench noses / blend): false - Cp = CdBluff x cos(a) (linear: a 4:1
    /// slope still took a quarter of a wall's pressure, sloped noses dragged ~2x the measured), with the Streamlining fudge
    /// on top; true - Cp = CdBluff x cos(a)^2 (the measured forebody pressures: 45 deg ~0.5, 2:1 ~0.2, 4:1 ~0.06 of a
    /// wall's), no fudge. Flat faces alike either way. A static default for the A/B; tables rebuild with it.</summary>
    public static bool SubsonicSquareLaw = true;   // (default since 2026-10-11: AeroBench jetliner L/D at 8 deg 4.7 -> 8.4, lift kept; the in-game flight A/B)

    /// <summary>PRESSURE RECOVERY (2026-10-10, prototype - the bench's A/B): attached flow gives back at the tail the pressure
    /// it put on the nose (d'Alembert - a streamlined body's drag is mostly friction). Per column of air across the flow
    /// (BakeOcclusion's 0.5 m pixels): the subsonic pressure its front faces take, along the travel; the column's exit (its
    /// most downstream face) turned away from the flow by less than SepDeg keeps the flow attached and is pushed forward by
    /// RecoveryFrac of it, fading out to SepDeg + SepFadeDeg; an attached exit has no base suction either. A flat back,
    /// a step, a blunt tail: separated, base suction as before. Subsonic end only (the Newtonian end is the wave drag).
    /// Off: base suction everywhere on the lee side - no shape could get under ~0.3 of a wall, and a block-built
    /// airliner's zero-lift drag came out ~13x the real one.</summary>
    public static bool PressureRecovery = true;   // (default since 2026-10-11: AeroBench jetliner L/D at 8 deg 4.7 -> 8.4, lift kept; the in-game flight A/B)

    /// <summary>REYNOLDS SKIN FRICTION (2026-10-10): the turbulent flat plate's Cf = 0.455 / (log10 Re)^2.58 at the ship's
    /// length (Re at RefSpeed, sea level) instead of the flat CfSkin (0.005 - twice a 30 m airliner's ~0.002, and the
    /// biggest part of a streamlined hull's drag once pressure recovers). Fixed per ship (the table is per unit q):
    /// Cf moves only ~10 % between 50 and 300 m/s. Floor CfMin.</summary>
    public static bool ReynoldsSkin = true;   // (default since 2026-10-11: AeroBench jetliner L/D at 8 deg 4.7 -> 8.4, lift kept; the in-game flight A/B)
    /// <summary>Air-breathing engines' intakes and exhausts are flow-through (SetThroughFaces). On since 2026-10-12 (the
    /// user: the Tu-134 at L/D 3.4 where the real one makes ~7 - its two rear engines' intakes were 9.5 of its 16 m2 of
    /// drag area, as walls): the live grid marks them from its atmospheric thrusters (AeroGridComponent.EngineBoxes).</summary>
    public static bool FlowThroughEngines = true;

    /// <summary>Which faces are an engine's intake or exhaust: the cell behind the face in an engine's block box (0.25 m cells,
    /// inclusive) stretched IntakeReach along its axis both ways, and the face along the axis (either way). cellSize: the
    /// surface's cell (m).</summary>
    /// <summary>How far ahead of and behind an engine block its intake and exhaust reach (m): the cowling a builder puts in
    /// front of it (the Tu-134's rear nacelles: a solid face 2.7 m ahead of each engine, ~3 m2 of drag area each).</summary>
    public static float IntakeReach = 3f;
    public static List<bool> ThroughMask(IReadOnlyList<SurfaceFace> faces, float cellSize, IReadOnlyList<(Vector3I lo, Vector3I hi, Vector3 axis)> engines, out int count)
    {
        count = 0;
        var through = new List<bool>(faces.Count);
        for (int i = 0; i < faces.Count; i++)
        {
            var f = faces[i]; bool t = false;
            var c = f.Position - f.RawNormal * (cellSize * 0.5f);   // (the cell's centre behind the face)
            for (int k = 0; k < engines.Count; k++)
            {
                var (lo, hi, ax) = engines[k];
                if (MathF.Abs(Vector3.Dot(f.RawNormal, ax)) < 0.9f) continue;
                // (within the engine's cross-section, and along its axis up to IntakeReach past either end: a cowling built
                //  in front of the thruster is its intake - the game gives it full thrust whatever covers it, so the air goes in)
                float rx = MathF.Abs(ax.X) > 0.5f ? IntakeReach : 0f, ry = MathF.Abs(ax.Y) > 0.5f ? IntakeReach : 0f, rz = MathF.Abs(ax.Z) > 0.5f ? IntakeReach : 0f;
                // (cell i spans (i -+ 0.5) x 0.25 m - centred, CubeGridCoords.GridToLocal; lo*0.25 .. (hi+1)*0.25 took in a
                //  cell's width beyond the engine on every + side)
                if (c.X < (lo.X - 0.5f) * 0.25f - rx || c.Y < (lo.Y - 0.5f) * 0.25f - ry || c.Z < (lo.Z - 0.5f) * 0.25f - rz || c.X > (hi.X + 0.5f) * 0.25f + rx || c.Y > (hi.Y + 0.5f) * 0.25f + ry || c.Z > (hi.Z + 0.5f) * 0.25f + rz) continue;
                t = true; break;
            }
            through.Add(t); if (t) count++;
        }
        return through;
    }
    public static float RefSpeed = 100f, CfMin = 0.0015f;
    public static float TurbulentCf(float length, float speed = 0f)
    {
        double re = Math.Max(1e5, (speed > 0f ? speed : RefSpeed) * Math.Max(0.25f, length) / 1.46e-5);
        return MathF.Max(CfMin, (float)(0.455 / Math.Pow(Math.Log10(re), 2.58)));
    }
    private float _lengthRef = 1f;
    /// <summary>The skin friction coefficient this grid's faces take.</summary>
    public float SkinCf => ReynoldsSkin ? TurbulentCf(_lengthRef) : CfSkin;
    public static float RecoveryFrac = 0.9f, SepDeg = 18f, SepFadeDeg = 12f;
    /// <summary>An exit face facing the flow's way more squarely than this (cos) is a flat back: never attached.</summary>
    public static float FlatExit = 0.95f;
    public static bool ColumnRun = true;
    public static bool BluntPartial = true;   // (the two-loss column: rounded fronts recover, open exits drag BaseLoss - BakeRecovery)
    public static float BaseLoss = 0.3f;
    public static int RecoveryPasses = 4;   // (1, 2 (diagonal) or 4 lattices: BakeRecovery. 4 since 2026-10-11: with 2 a yawed brick and its mirror image
                                            //  binned differently - 380 N m of stray roll against 6 with 4 (AeroBench torque); tables ~30% slower to build)
    public static List<(int u, int w, float d, float sFace, float s, float front, float run)> RecDebug;   // (the bench's: each column's exit)
    public bool SquareLaw => SubsonicSquareLaw;
    /// <summary>The last face-loop evaluation's shape streamlining (displays).</summary>
    public float LastStreamlining { get; private set; } = -1f;

    public PrecomputedShadowMap ShadowMap => _shadowMap;

    /// <summary>Per-face Cp from the last Compute() call (for debug draw).
    /// Indexed by ORIGINAL face index (not grouped order).</summary>
    public List<float> FaceCp => _cpOut;

    // ─── Direction-grouped SoA ──────────────────────────────────────
    // Faces sorted into 6 groups by dominant normal: +X,-X,+Y,-Y,+Z,-Z
    // Within each group, padded to multiple of 8 for AVX2.

    private List<float> _px = new(), _py = new(), _pz = new();
    private List<float> _nx = new(), _ny = new(), _nz = new();
    private List<float> _area = new(), _visArea = new();
    private List<float> _cpGrouped = new();
    private List<int> _origIndex = new();
    private List<Vector3> _raw = new();   // (each face's cell direction before smoothing: PressureRecovery's step faces)
    private List<float> _cpOut = new(); // Cp in original face order

    // Per-direction group: start index and count in the SoA arrays
    private readonly List<int> _dirStart = new() { 0, 0, 0, 0, 0, 0 };
    private readonly List<int> _dirCount = new() { 0, 0, 0, 0, 0, 0 };
    private readonly List<int> _dirPadded = new() { 0, 0, 0, 0, 0, 0 };
    private int _totalPadded;

    private int _faceCount;
    private int _lastShadowVersion = -1;
    private int _lastSurfaceVersion = -1;
    private ManifoldClassifier _manifold;
    private IReadOnlyList<SurfaceFace> _lastFaces;

    // Direction normals
    private static readonly List<Vector3> DirNormals = new()
    {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DominantDir(Vector3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        if (ax >= ay && ax >= az) return n.X >= 0 ? 0 : 1;
        if (ay >= az) return n.Y >= 0 ? 2 : 3;
        return n.Z >= 0 ? 4 : 5;
    }

    private void EnsureSoA(IReadOnlyList<SurfaceFace> faces, int surfaceVersion, ManifoldClassifier manifold = null)
    {
        int n = faces.Count;

        // (the same surface - not just the same count and version: versions are per surface, so two ships with as many
        //  faces both read "version 1", and a pooled builder kept the first one's faces - a brick turned 90 deg got the
        //  other's drag. Tests/AeroBench: torque)
        if (_faceCount == n && _lastSurfaceVersion == surfaceVersion && ReferenceEquals(faces, _lastFaces) && ReferenceEquals(manifold, _manifold))
            return;

        _manifold = manifold;
        _lastFaces = faces;
        _lastSurfaceVersion = surfaceVersion;
        _faceCount = n;
        bool hasManifold = manifold != null && manifold.IsClassified;

        // Count HULL faces per direction (skip cavity faces)
        for (int d = 0; d < 6; d++) _dirCount[d] = 0;
        for (int i = 0; i < n; i++)
        {
            if (hasManifold && !manifold.IsHull(i)) continue;
            _dirCount[DominantDir(faces[i].Normal)]++;
        }

        // Compute padded starts
        int offset = 0;
        for (int d = 0; d < 6; d++)
        {
            _dirStart[d] = offset;
            _dirPadded[d] = (_dirCount[d] + 7) & ~7;
            offset += _dirPadded[d];
        }
        _totalPadded = offset;

        // Resize lists if needed
        EnsureListSize(_px, _totalPadded); EnsureListSize(_py, _totalPadded); EnsureListSize(_pz, _totalPadded);
        EnsureListSize(_nx, _totalPadded); EnsureListSize(_ny, _totalPadded); EnsureListSize(_nz, _totalPadded);
        EnsureListSize(_area, _totalPadded); EnsureListSize(_visArea, _totalPadded);
        EnsureListSize(_cpGrouped, _totalPadded);
        EnsureListSizeI(_origIndex, _totalPadded);
        while (_raw.Count < _totalPadded) _raw.Add(default);

        // Zero all
        for (int j = 0; j < _totalPadded; j++)
        {
            _px[j] = 0; _py[j] = 0; _pz[j] = 0;
            _nx[j] = 0; _ny[j] = 0; _nz[j] = 0;
            _area[j] = 0; _visArea[j] = 0; _cpGrouped[j] = 0;
            _origIndex[j] = -1;
        }

        var lo = new Vector3(float.MaxValue); var hi = new Vector3(float.MinValue);
        // Fill grouped SoA with HULL faces only
        Span<int> cursor = stackalloc int[6];
        for (int d = 0; d < 6; d++) cursor[d] = _dirStart[d];

        for (int i = 0; i < n; i++)
        {
            if (hasManifold && !manifold.IsHull(i)) continue;

            var f = faces[i];
            int d = DominantDir(f.Normal);
            int j = cursor[d]++;
            _px[j] = f.Position.X; _py[j] = f.Position.Y; _pz[j] = f.Position.Z;
            _nx[j] = f.Normal.X;   _ny[j] = f.Normal.Y;   _nz[j] = f.Normal.Z;
            _area[j] = f.Area;
            _visArea[j] = f.Area;
            _origIndex[j] = i;
            _raw[j] = f.RawNormal;
            lo = Vector3.Min(lo, f.Position); hi = Vector3.Max(hi, f.Position);
        }
        { var ext = hi - lo; _lengthRef = lo.X <= hi.X ? MathF.Max(ext.X, MathF.Max(ext.Y, ext.Z)) : 1f; }   // (the ship's length: its longest extent - ReynoldsSkin)

        // Ensure _cpOut is large enough (indexed by original face index)
        while (_cpOut.Count < n) _cpOut.Add(0f);
        // Zero cavity faces in _cpOut
        if (hasManifold)
            for (int i = 0; i < n; i++)
                if (!manifold.IsHull(i)) _cpOut[i] = 0f;

        _lastShadowVersion = -1; _loopBakeVersion = int.MinValue;
    }

    private List<bool> _excluded;
    private int _excludedVersion = -1;

    /// <summary>Faces another model owns (a wing's skins): left out of this one. For the surface version given only.</summary>
    public void SetExcludedFaces(List<bool> excluded, int surfaceVersion)
    {
        _wingMask = excluded; _wingMaskVersion = surfaceVersion;
        CombineExcluded();
    }

    /// <summary>FLOW-THROUGH faces (2026-10-10): an air-breathing engine's intake and exhaust - the air goes through it,
    /// it is no wall at the front and no flat back behind (an airliner's two rear nacelles were ~16 kN of a Tu-134's
    /// ~44 kN hull drag, as closed boxes). Left out of the face model like a wing's skins. For the surface version given.</summary>
    public void SetThroughFaces(List<bool> through, int surfaceVersion)
    {
        _throughMask = through; _throughVersion = surfaceVersion;
        CombineExcluded();
    }

    private List<bool> _wingMask, _throughMask; private int _wingMaskVersion = -1, _throughVersion = -1;
    private void CombineExcluded()
    {
        int ver = Math.Max(_wingMask != null ? _wingMaskVersion : -1, _throughMask != null ? _throughVersion : -1);
        var a = _wingMask != null && _wingMaskVersion == ver ? _wingMask : null;
        var b = _throughMask != null && _throughVersion == ver ? _throughMask : null;
        if (a == null || b == null) _excluded = a ?? b;
        else
        {
            var c = new List<bool>(Math.Max(a.Count, b.Count));
            for (int i = 0; i < c.Capacity; i++) c.Add((i < a.Count && a[i]) || (i < b.Count && b[i]));
            _excluded = c;
        }
        _excludedVersion = ver;
        _lastShadowVersion = -1; _loopBakeVersion = int.MinValue;   // re-bake with it
    }

    // Exactly `size` long: the face loops run to the lists' Count. (They only grew: a model that had held a bigger grid's
    // faces kept them past the new grid's end - live, _origIndex >= 0 - and every table it built afterwards summed the
    // old ship's faces too. The table builders are pooled across grids: the Hercules flew on part of a jetliner, eight
    // times its drag, its centre of pressure 50 m outside it. Tests/AeroBench: gamecache, POOL=)
    private static void EnsureListSize(List<float> list, int size)
    {
        if (list.Count > size) list.RemoveRange(size, list.Count - size);
        while (list.Count < size) list.Add(0f);
    }

    private static void EnsureListSizeI(List<int> list, int size)
    {
        if (list.Count > size) list.RemoveRange(size, list.Count - size);
        while (list.Count < size) list.Add(0);
    }
}
