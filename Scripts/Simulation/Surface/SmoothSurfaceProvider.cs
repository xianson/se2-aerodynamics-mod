#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// Surface provider that smooths face normals to approximate the intended surface
/// of a blocky cube grid, analogous to Phong shading in graphics.
///
/// Ported from Aero.Core.Aero.SmoothSurfaceProvider.
///
/// Algorithm:
///   1. Angle-weighted count² vertex normals
///   2. Hemisphere filtering (prevents opposing faces from cancelling)
///   3. Multi-ring Laplacian diffusion (extends smoothing radius)
///   4. Crease preservation (sharp edges preserved above threshold angle)
///   5. Incremental updates (only recomputes affected faces on block changes)
/// </summary>
public partial class SmoothSurfaceProvider : ISurfaceProvider
{
    /// <summary>Compact fixed-size set of face indices: at most 12 per vertex in a cube grid (3 planes through it, 4
    /// faces each, all there where cells meet only along edges). It held 8 and dropped the rest silently: those faces
    /// were never recomputed when their neighbourhood changed.</summary>
    private struct FaceSet
    {
        private int _f0, _f1, _f2, _f3, _f4, _f5, _f6, _f7, _f8, _f9, _f10, _f11;
        public int Count;

        public void Add(int value)
        {
            // Linear scan for duplicates
            if (Contains(value)) return;
            if (Count >= 12) return; // (cannot happen in a cube grid)
            switch (Count)
            {
                case 0: _f0 = value; break; case 1: _f1 = value; break;
                case 2: _f2 = value; break; case 3: _f3 = value; break;
                case 4: _f4 = value; break; case 5: _f5 = value; break;
                case 6: _f6 = value; break; case 7: _f7 = value; break;
                case 8: _f8 = value; break; case 9: _f9 = value; break;
                case 10: _f10 = value; break; case 11: _f11 = value; break;
            }
            Count++;
        }

        public bool Remove(int value)
        {
            for (int i = 0; i < Count; i++)
            {
                if (Get(i) == value)
                {
                    // Swap with last
                    Count--;
                    if (i < Count) Set(i, Get(Count));
                    return true;
                }
            }
            return false;
        }

        public bool Contains(int value)
        {
            for (int i = 0; i < Count; i++)
                if (Get(i) == value) return true;
            return false;
        }

        public int Get(int index) => index switch
        {
            0 => _f0, 1 => _f1, 2 => _f2, 3 => _f3,
            4 => _f4, 5 => _f5, 6 => _f6, 7 => _f7,
            8 => _f8, 9 => _f9, 10 => _f10, 11 => _f11,
            _ => 0,
        };

        private void Set(int index, int value)
        {
            switch (index)
            {
                case 0: _f0 = value; break; case 1: _f1 = value; break;
                case 2: _f2 = value; break; case 3: _f3 = value; break;
                case 4: _f4 = value; break; case 5: _f5 = value; break;
                case 6: _f6 = value; break; case 7: _f7 = value; break;
                case 8: _f8 = value; break; case 9: _f9 = value; break;
                case 10: _f10 = value; break; case 11: _f11 = value; break;
            }
        }
    }

    private static readonly List<Vector3I> DirOffsets = new List<Vector3I>
    {
        new( 1, 0, 0), new(-1, 0, 0),
        new( 0, 1, 0), new( 0,-1, 0),
        new( 0, 0, 1), new( 0, 0,-1),
    };

    private static readonly List<Vector3> DirNormals = new List<Vector3>
    {
        Vector3.UnitX, -Vector3.UnitX,
        Vector3.UnitY, -Vector3.UnitY,
        Vector3.UnitZ, -Vector3.UnitZ,
    };

    private static readonly List<int> OppositeDir = new List<int> { 1, 0, 3, 2, 5, 4 };

    // ─── Persistent face data (swap-with-last) ─────────────────────
    private readonly List<SurfaceFace> _faces = new();
    private readonly List<NormalGroup> _groups = new();
    private readonly Dictionary<long, int> _faceIndex = new(LongKey.Comparer);
    private readonly List<long> _rawFaceKeys = new();
    private readonly List<int> _rawFaceDirs = new();

    // ─── Vertex data ───────────────────────────────────────────────
    private Dictionary<long, W6> _vertexDirWeights = new(LongKey.Comparer);

    /// <summary>A vertex's face count per direction, inline (it was a List per vertex: 1.3 million objects per Red Ship
    /// surface, two surfaces a grid, kept for good - every full garbage collection in the game had to walk them).</summary>
    private struct W6
    {
        public float D0, D1, D2, D3, D4, D5;
        public float this[int i]
        {
            readonly get => i switch { 0 => D0, 1 => D1, 2 => D2, 3 => D3, 4 => D4, _ => D5 };
            set { switch (i) { case 0: D0 = value; break; case 1: D1 = value; break; case 2: D2 = value; break; case 3: D3 = value; break; case 4: D4 = value; break; default: D5 = value; break; } }
        }
    }
    private Dictionary<long, FaceSet> _vertexToFaceIndex = new(LongKey.Comparer);

    // ─── Adjacency & crease data ───────────────────────────────────
    // Edges: every edge of a voxel surface is one cell step along an axis, keyed (its lower vertex, axis) with the
    // number of faces sharing it. A vertex's neighbours are the (up to 6) axis steps whose edge is here - no
    // per-vertex neighbour sets (on Red Ship, 666k faces, those were most of a 480 MB, 5 s finalize).
    private Dictionary<long, int> _edgeRefCount;
    private HashSet<long> _creaseEdges;

    private float _blockSize;

    /// <summary>Smoothing strength: 0 = blocky, 1 = fully smoothed.</summary>
    public float Smoothing { get; set; } = 1.0f;

    /// <summary>Weight exponent for vertex direction weights. 2 = count² (default).</summary>
    public float WeightExponent { get; set; } = 2.0f;

    /// <summary>Number of neighborhood rings for Laplacian diffusion.</summary>
    public int SmoothingRings { get; set; } = 2;

    /// <summary>Dihedral angle threshold (degrees) for crease edges. 90 = preserve right angles.</summary>
    public float CreaseAngleDegrees { get; set; } = 90f;

    public string Name => "Smooth";
    public IReadOnlyList<SurfaceFace> Faces => _faces;
    public IReadOnlyList<NormalGroup> NormalGroups => _groups;
    public int FaceCount => _faces.Count;
    public int RawFaceCount => _rawFaceKeys.Count;
    public int GroupCount => _groups.Count;
    public float BlockSize => _blockSize;
    public int Version { get; private set; }

    /// <summary>The size (m) of this surface's cells: 0.25 (the grid's own), or coarser for large-block grids (a
    /// half block: their 2.5 m blocks need no 0.25 m detail, and it is 125x fewer cells).</summary>
    public float CellSize { get; set; } = 0.25f;
    /// <summary>Where a cell's centre sits, in cells, from cell x CellSize (coarse cells are centred on the fine cells
    /// they cover: (k - 1) / 2k).</summary>
    public float CellOffset { get; set; } = 0f;

    // ─── Reuse between builds ────────────────────────────────────
    // A big grid's rebuild made tens of thousands of small objects (a weight list and an adjacency set per vertex,
    // fresh dictionaries for adjacency, edges, creases and normals): ~80 MB a rebuild on the Jetliner, and the
    // garbage collections it caused paused the game. They are kept and reused instead (each of the two surfaces
    // that alternate keeps its own).
    private Dictionary<long, int> _edgeStore;
    private HashSet<long> _creaseStore;
    private Dictionary<long, Vector3> _normA, _normB;

    // ─── Build-only tables, shared by big surfaces ──────────────────
    // A surface needs its faces, their keys and the face index for good; the vertex weights, vertex-to-face index,
    // edges, creases, staging cells and normal buffers only while it is built (and for block-by-block updates, which
    // big surfaces never get: they are rebuilt whole). A big surface hands those to a small shared pool when its build
    // is done - Red Ship's were ~200 MB per surface, two surfaces a grid, on a machine whose memory was full.
    /// <summary>Surfaces over this many faces give their build tables back after a build (the pool keeps one set).</summary>
    public const int ReleaseAboveFaces = 20000;
    private sealed class Scratch
    {
        public Dictionary<long, W6> W; public Dictionary<long, FaceSet> V; public Dictionary<long, int> E;
        public HashSet<long> C; public List<Vector3I> S; public Dictionary<long, Vector3> NA, NB;
    }
    private static readonly Stack<Scratch> _scratchPool = new();
    /// <summary>Build table sets kept (one per background worker: another build meanwhile allocates its own).</summary>
    public const int ScratchPoolSize = 2;
    private bool _released;
    /// <summary>Whether the build tables are handed back (a block-by-block update then rebuilds whole).</summary>
    public bool Released => _released;

    /// <summary>Borrow the build tables from the shared pool and give them back after every build, whatever the
    /// size (the damage updates' surfaces: built again and again, they hold no tables of their own between builds,
    /// and take ones already grown instead of growing their own - 30-100 MB of garbage per update on Red Ship).</summary>
    public bool BorrowScratch { get; set; }

    private void AcquireScratch(IGridAccessor grid)
    {
        if (!_released && !BorrowScratch && (grid == null || grid.CellCount <= ReleaseAboveFaces)) return;   // (small: its own tables)
        if (!_released) ReleaseScratch();   // (a growing grid: trade its own small tables for pooled big ones)
        Scratch sc = null;
        lock (_scratchPool) if (_scratchPool.Count > 0) sc = _scratchPool.Pop();
        _vertexDirWeights = sc?.W ?? new Dictionary<long, W6>(LongKey.Comparer);
        _vertexToFaceIndex = sc?.V ?? new Dictionary<long, FaceSet>(LongKey.Comparer);
        _edgeStore = sc?.E; _creaseStore = sc?.C; _stagingStore = sc?.S; _normA = sc?.NA; _normB = sc?.NB;
        _released = false;
    }

    private void ReleaseScratch()
    {
        var sc = new Scratch { W = _vertexDirWeights, V = _vertexToFaceIndex, E = _edgeRefCount ?? _edgeStore, C = _creaseEdges ?? _creaseStore, S = _stagingCells ?? _stagingStore, NA = _normA, NB = _normB };
        sc.W?.Clear(); sc.V?.Clear(); sc.E?.Clear(); sc.C?.Clear(); sc.S?.Clear(); sc.NA?.Clear(); sc.NB?.Clear();
        lock (_scratchPool) if (_scratchPool.Count < ScratchPoolSize) _scratchPool.Push(sc);   // (a grid's two surfaces build in turn; one per background worker)
        _vertexDirWeights = null; _vertexToFaceIndex = null; _edgeRefCount = null; _edgeStore = null;
        _creaseEdges = null; _creaseStore = null; _stagingCells = null; _stagingStore = null; _normA = null; _normB = null;
        _released = true;
    }

    private void ReleaseIfBig()
    {
        if (BorrowScratch || _rawFaceKeys.Count > ReleaseAboveFaces) ReleaseScratch();
    }

    /// <summary>Everything let go (a big grid flies on its force table between rebuilds; the next build starts
    /// from the pooled build tables).</summary>
    public void ReleaseAll()
    {
        Clear();
        if (!_released) ReleaseScratch();
        _faces.Clear(); _faces.TrimExcess(); _groups.Clear(); _groups.TrimExcess();
        _rawFaceKeys.TrimExcess(); _rawFaceDirs.TrimExcess(); _faceIndex.TrimExcess();
        _stagingStore = null;
        Version++;
    }

    private void RecycleForBuild()
    {
        _vertexDirWeights.Clear();
        if (_edgeRefCount != null) { _edgeRefCount.Clear(); _edgeStore = _edgeRefCount; }
        if (_creaseEdges != null) { _creaseEdges.Clear(); _creaseStore = _creaseEdges; }
    }

    // ─── Staggered build state ──────────────────────────────────
    private List<Vector3I> _stagingCells, _stagingStore;
    private int _stagingOffset;
    private IGridAccessor _stagingGrid;

    // ─── Full rebuild ─────────────────────────────────────────────

    public void Build(IGridAccessor grid, float blockSize)
    {
        AcquireScratch(grid);
        _blockSize = blockSize;
        Clear();

        // Phase 1: Find all exposed faces
        foreach (var cell in grid.EnumerateOccupiedCells())
        {
            for (int d = 0; d < 6; d++)
            {
                if (!grid.IsCellOccupied(cell + DirOffsets[d]))
                    AddRawFace(cell, d);
            }
        }

        // Phase 2: Build adjacency if multi-ring
        if (SmoothingRings > 1)
            BuildAdjacency();

        // Phase 2b: Detect creases
        if (CreaseAngleDegrees < 180f)
            DetectAllCreases();

        // Phase 3: Compute smoothed normals
        RecomputeAllFaceNormals();

        // Phase 4: Build groups
        RebuildGroups();
        Version++;
        ReleaseIfBig();
    }

    // ─── Staggered full rebuild (spread across ticks) ───────────

    /// <summary>
    /// Begin a staggered rebuild. Materializes all occupied cells into a buffer
    /// but does NOT clear the active face data — old faces remain usable for
    /// force computation until FinalizeBuild() is called.
    /// </summary>
    public void BeginBuild(IGridAccessor grid, float blockSize)
    {
        _blockSize = blockSize;
        AcquireScratch(grid);
        RecycleForBuild();

        // Materialize all cells up front (one octree pass, relatively cheap)
        _stagingCells = _stagingStore ?? new List<Vector3I>();
        _stagingCells.Clear();
        _stagingCells.EnsureCapacity(grid.CellCount);
        foreach (var cell in grid.EnumerateOccupiedCells())
            _stagingCells.Add(cell);
        _stagingOffset = 0;
        _stagingGrid = grid;

        // Clear internal structures but NOT _faces — those are still serving force computation
        _rawFaceKeys.Clear();
        _rawFaceDirs.Clear();
        _faceIndex.Clear();
        _vertexDirWeights.Clear();
        _vertexToFaceIndex.Clear();

        // Sized once, exactly: the faces counted first (a quick pass over the cells). Grown by doubling, a big
        // grid's tables threw away as much again in large arrays - and large-array garbage is what pushes the
        // runtime into a blocking full collection (5 s on Red Ship's first build). A closed surface has about as
        // many vertices as faces.
        int faces = 0;
        for (int i = 0; i < _stagingCells.Count; i++)
        {
            var c = _stagingCells[i];
            for (int d = 0; d < 6; d++)
                if (!grid.IsCellOccupied(c + DirOffsets[d])) faces++;
        }
        _rawFaceKeys.EnsureCapacity(faces);
        _rawFaceDirs.EnsureCapacity(faces);
        _faceIndex.EnsureCapacity(faces);
        _vertexDirWeights.EnsureCapacity(faces + faces / 4 + 16);
        _vertexToFaceIndex.EnsureCapacity(faces + faces / 4 + 16);
        _faces.EnsureCapacity(faces);
        _edgeRefCount = null;
        _creaseEdges = null;
    }

    /// <summary>
    /// Process up to <paramref name="maxCells"/> cells from the staging buffer.
    /// Returns true when all cells have been processed (ready for FinalizeBuild).
    /// </summary>
    public bool AddCellBatch(int maxCells)
    {
        if (_stagingCells == null || _stagingGrid == null)
            return true;

        int end = Math.Min(_stagingOffset + maxCells, _stagingCells.Count);
        for (int i = _stagingOffset; i < end; i++)
        {
            var cell = _stagingCells[i];
            for (int d = 0; d < 6; d++)
            {
                if (!_stagingGrid.IsCellOccupied(cell + DirOffsets[d]))
                    AddRawFace(cell, d);
            }
        }
        _stagingOffset = end;
        return _stagingOffset >= _stagingCells.Count;
    }

    /// <summary>
    /// Finalize the staggered build: adjacency, creases, normals, groups.
    /// After this call, _faces is overwritten with the new surface data.
    /// </summary>
    public void FinalizeBuild()
    {
        if (_stagingCells != null) { _stagingCells.Clear(); _stagingStore = _stagingCells; }
        _stagingCells = null;
        _stagingGrid = null;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (SmoothingRings > 1)
            BuildAdjacency();
        double tAdj = sw.Elapsed.TotalMilliseconds;
        if (CreaseAngleDegrees < 180f)
            DetectAllCreases();
        double tCrease = sw.Elapsed.TotalMilliseconds;
        RecomputeAllFaceNormals();
        double tNormals = sw.Elapsed.TotalMilliseconds;
        RebuildGroups();
        LastFinalizeProfile = $"edges {tAdj:F0} ms ({_edgeRefCount?.Count ?? 0}), creases {tCrease - tAdj:F0} ms ({_creaseEdges?.Count ?? 0}), normals {tNormals - tCrease:F0} ms, groups {sw.Elapsed.TotalMilliseconds - tNormals:F0} ms";
        Version++;
        ReleaseIfBig();
    }

    /// <summary>This surface's last FinalizeBuild stage times.</summary>
    public string LastFinalizeProfile = "";

    // ─── Public face lookup ────────────────────────────────────────

    /// <summary>
    /// Returns the face index for a given cell and direction, or -1 if no such face exists.
    /// Used by face override systems to map cell+direction to face indices.
    /// </summary>
    public int GetFaceIndex(Vector3I cell, int dir)
    {
        long key = PackCellDir(cell, dir);
        return _faceIndex.TryGetValue(key, out int index) ? index : -1;
    }

    /// <summary>
    /// Get the cell position and direction for a face by raw index.
    /// Used by ManifoldClassifier for face adjacency computation.
    /// </summary>
    public void GetFaceCellDir(int rawIndex, out Vector3I cell, out int dir)
    {
        long key = _rawFaceKeys[rawIndex];
        cell = UnpackCell(key);
        dir = (int)(key & 0xF);
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private void Clear()
    {
        _faces.Clear();
        _groups.Clear();
        _rawFaceKeys.Clear();
        _rawFaceDirs.Clear();
        _faceIndex.Clear();
        _vertexDirWeights?.Clear();
        _vertexToFaceIndex?.Clear();
        // (kept for the next build, emptied: dropping them made the next one allocate them anew)
        if (_edgeRefCount != null) { _edgeRefCount.Clear(); _edgeStore = _edgeRefCount; }
        if (_creaseEdges != null) { _creaseEdges.Clear(); _creaseStore = _creaseEdges; }
        _edgeRefCount = null;
        _creaseEdges = null;
    }

    private static Vector3I GetFaceVertex(Vector3I cell, int dir, int vertexIndex)
    {
        int a = vertexIndex & 1;
        int b = (vertexIndex >> 1) & 1;

        if (dir == 0) return new Vector3I(cell.X + 1, cell.Y + a, cell.Z + b);
        if (dir == 1) return new Vector3I(cell.X,     cell.Y + a, cell.Z + b);
        if (dir == 2) return new Vector3I(cell.X + a, cell.Y + 1, cell.Z + b);
        if (dir == 3) return new Vector3I(cell.X + a, cell.Y,     cell.Z + b);
        if (dir == 4) return new Vector3I(cell.X + a, cell.Y + b, cell.Z + 1);
        return          new Vector3I(cell.X + a, cell.Y + b, cell.Z);
    }

    private static long PackVertex(Vector3I cell, int dir, int vertexIndex)
    {
        var v = GetFaceVertex(cell, dir, vertexIndex);
        return ((long)(v.X & 0xFFFFF) << 40)
             | ((long)(v.Y & 0xFFFFF) << 20)
             | (long)(v.Z & 0xFFFFF);
    }

    private Vector3 CellFaceCenter(Vector3I cell, Vector3I faceDir)
    {
        float half = CellSize * 0.5f, off = CellOffset;
        return new Vector3(
            (cell.X + off) * CellSize + faceDir.X * half,
            (cell.Y + off) * CellSize + faceDir.Y * half,
            (cell.Z + off) * CellSize + faceDir.Z * half);
    }
}
