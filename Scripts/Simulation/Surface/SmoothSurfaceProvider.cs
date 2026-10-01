#pragma warning disable
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
public class SmoothSurfaceProvider : ISurfaceProvider
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

    /// <summary>True while a staggered build is in progress (between BeginBuild and FinalizeBuild).</summary>
    public bool IsBuilding => _stagingCells != null;

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
    private bool _released;
    /// <summary>Whether the build tables are handed back (a block-by-block update then rebuilds whole).</summary>
    public bool Released => _released;

    private void AcquireScratch(IGridAccessor grid)
    {
        if (!_released && (grid == null || grid.CellCount <= ReleaseAboveFaces)) return;   // (small: its own tables)
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
        lock (_scratchPool) if (_scratchPool.Count < 1) _scratchPool.Push(sc);   // (one: a grid's two surfaces build in turn; another big build meanwhile allocates its own)
        _vertexDirWeights = null; _vertexToFaceIndex = null; _edgeRefCount = null; _edgeStore = null;
        _creaseEdges = null; _creaseStore = null; _stagingCells = null; _stagingStore = null; _normA = null; _normB = null;
        _released = true;
    }

    private void ReleaseIfBig()
    {
        if (_rawFaceKeys.Count > ReleaseAboveFaces) ReleaseScratch();
    }

    private void RecycleForBuild()
    {
        _vertexDirWeights.Clear();
        if (_edgeRefCount != null) { _edgeRefCount.Clear(); _edgeStore = _edgeRefCount; }
        if (_creaseEdges != null) { _creaseEdges.Clear(); _creaseStore = _creaseEdges; }
    }



    /// <summary>The cells a staggered build is working through (null when not building): its snapshot of the grid.</summary>
    public IReadOnlyList<Vector3I> StagedCells => _stagingCells;

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

    /// <summary>Abort a staggered build in progress (e.g., grid topology changed mid-build).</summary>
    public void AbortBuild()
    {
        _stagingCells = null;
        _stagingGrid = null;
    }

    // ─── Incremental updates ─────────────────────────────────────

    public void OnBlocksChanged(IGridAccessor grid, in BlocksChangedArgs args)
    {
        if (args.AllBlocksRemoved)
        {
            Clear();
            return;
        }

        // If too many changes or first build, do full rebuild (and always once the build tables are handed back)
        int changedCells = args.AddedCells.Count + args.RemovedCells.Count;
        if (_released || _rawFaceKeys.Count == 0 || changedCells > _rawFaceKeys.Count / 5)
        {
            Build(grid, _blockSize);
            return;
        }

        var dirtyVertices = new HashSet<long>(LongKey.Comparer);

        // Process removals
        for (int ri = 0; ri < args.RemovedCells.Count; ri++)
        {
            var cell = args.RemovedCells[ri];
            // Remove all faces of this cell
            for (int d = 0; d < 6; d++)
            {
                long key = PackCellDir(cell, d);
                if (_faceIndex.ContainsKey(key))
                {
                    for (int v = 0; v < 4; v++)
                        dirtyVertices.Add(PackVertex(cell, d, v));
                    RemoveRawFace(cell, d);
                }
            }
            // Expose neighbor faces
            for (int d = 0; d < 6; d++)
            {
                var neighborCell = cell + DirOffsets[d];
                if (grid.IsCellOccupied(neighborCell))
                {
                    int oppositeD = OppositeDir[d];
                    AddRawFace(neighborCell, oppositeD);
                    for (int v = 0; v < 4; v++)
                        dirtyVertices.Add(PackVertex(neighborCell, oppositeD, v));
                }
            }
        }

        // Process additions
        for (int ai = 0; ai < args.AddedCells.Count; ai++)
        {
            var cell = args.AddedCells[ai];
            for (int d = 0; d < 6; d++)
            {
                var neighborCell = cell + DirOffsets[d];
                if (!grid.IsCellOccupied(neighborCell))
                {
                    AddRawFace(cell, d);
                    for (int v = 0; v < 4; v++)
                        dirtyVertices.Add(PackVertex(cell, d, v));
                }
                else
                {
                    int oppositeD = OppositeDir[d];
                    long nkey = PackCellDir(neighborCell, oppositeD);
                    if (_faceIndex.ContainsKey(nkey))
                    {
                        for (int v = 0; v < 4; v++)
                            dirtyVertices.Add(PackVertex(neighborCell, oppositeD, v));
                        RemoveRawFace(neighborCell, oppositeD);
                    }
                }
            }
        }

        // Creases at the changed vertices: an edge's crease depends on the faces sharing it, and only edges at a
        // changed vertex can have changed faces. (Only full builds found creases before: damage that exposed a new
        // corner left it smoothed over, unlike a fresh build of the same shape.)
        if (_creaseEdges != null && _edgeRefCount != null && CreaseAngleDegrees < 180f)
            UpdateCreasesAt(dirtyVertices);

        // Expand dirty set for multi-ring diffusion
        if (SmoothingRings > 1 && _edgeRefCount != null)
            dirtyVertices = ExpandDirtyVertices(dirtyVertices, SmoothingRings - 1);

        RecomputeDirtyFaces(dirtyVertices);
        RebuildGroups();
        Version++;
    }

    // ─── Raw face management (swap-with-last) ─────────────────────

    private static long PackCellDir(Vector3I cell, int dir)
    {
        return ((long)(cell.X & 0xFFFF) << 36)
             | ((long)(cell.Y & 0xFFFF) << 20)
             | ((long)(cell.Z & 0xFFFF) << 4)
             | (long)(dir & 0xF);
    }

    private static Vector3I UnpackCell(long key)
    {
        int cx = (int)((key >> 36) & 0xFFFF);
        int cy = (int)((key >> 20) & 0xFFFF);
        int cz = (int)((key >> 4) & 0xFFFF);
        if (cx >= 0x8000) cx -= 0x10000;
        if (cy >= 0x8000) cy -= 0x10000;
        if (cz >= 0x8000) cz -= 0x10000;
        return new Vector3I(cx, cy, cz);
    }

    private void AddRawFace(Vector3I cell, int dir)
    {
        long key = PackCellDir(cell, dir);
        if (_faceIndex.ContainsKey(key))
            return;

        int index = _rawFaceKeys.Count;
        _rawFaceKeys.Add(key);
        _rawFaceDirs.Add(dir);
        _faceIndex[key] = index;

        for (int v = 0; v < 4; v++)
        {
            long vkey = PackVertex(cell, dir, v);
            ref var weights = ref CollectionsMarshal.GetValueRefOrAddDefault(_vertexDirWeights, vkey, out _);
            // A count of the faces in each direction at the vertex (whole numbers: exact under add and remove).
            // It added pi/2 (each face's angle there): repeated adds and removes drifted, and at a vertex whose
            // opposite faces should cancel the residue (~1e-6) was normalized into a full normal - a damaged
            // surface's normals then differed from a fresh build's. Every use squares and normalizes: the scale
            // never mattered.
            weights[dir] += 1f;

            var faceSet = _vertexToFaceIndex.TryGetValue(vkey, out var existing)
                ? existing : default;
            faceSet.Add(index);
            _vertexToFaceIndex[vkey] = faceSet;
        }

        if (_edgeRefCount != null)
            AddFaceEdges(cell, dir);
    }

    private void RemoveRawFace(Vector3I cell, int dir)
    {
        long key = PackCellDir(cell, dir);
        if (!_faceIndex.TryGetValue(key, out int index))
            return;

        if (_edgeRefCount != null)
            RemoveFaceEdges(cell, dir);

        for (int v = 0; v < 4; v++)
        {
            long vkey = PackVertex(cell, dir, v);
            ref var weights = ref CollectionsMarshal.GetValueRefOrNullRef(_vertexDirWeights, vkey);
            if (!System.Runtime.CompilerServices.Unsafe.IsNullRef(ref weights))
            {
                weights[dir] -= 1f;
                if (weights[dir] < 0.5f) weights[dir] = 0f;
            }
            if (_vertexToFaceIndex.TryGetValue(vkey, out var faceSet))
            {
                faceSet.Remove(index);
                if (faceSet.Count == 0)
                {
                    _vertexToFaceIndex.Remove(vkey);
                    _vertexDirWeights.Remove(vkey);
                }
                else
                {
                    _vertexToFaceIndex[vkey] = faceSet;
                }
            }
        }

        // Swap-with-last removal
        int lastIndex = _rawFaceKeys.Count - 1;
        if (index != lastIndex)
        {
            var movedKey = _rawFaceKeys[lastIndex];
            int movedDir = _rawFaceDirs[lastIndex];
            _rawFaceKeys[index] = movedKey;
            _rawFaceDirs[index] = movedDir;
            _faceIndex[movedKey] = index;

            // Also move the SurfaceFace data so _faces[index] stays consistent
            if (index < _faces.Count && lastIndex < _faces.Count)
                _faces[index] = _faces[lastIndex];

            var movedCell = UnpackCell(movedKey);
            for (int v = 0; v < 4; v++)
            {
                long vkey = PackVertex(movedCell, movedDir, v);
                if (_vertexToFaceIndex.TryGetValue(vkey, out var faceSet))
                {
                    faceSet.Remove(lastIndex);
                    faceSet.Add(index);
                    _vertexToFaceIndex[vkey] = faceSet;
                }
            }
        }

        _rawFaceKeys.RemoveAt(lastIndex);
        _rawFaceDirs.RemoveAt(lastIndex);
        _faceIndex.Remove(key);

        // Keep _faces in sync with _rawFaceKeys
        if (_faces.Count > _rawFaceKeys.Count)
            _faces.RemoveAt(_faces.Count - 1);
    }

    // ─── Normal computation ────────────────────────────────────────

    private void RecomputeAllFaceNormals()
    {
        float faceArea = CellSize * CellSize;

        Dictionary<long, Vector3> vertexNormals = null;
        if (SmoothingRings > 1)
            vertexNormals = ComputeDiffusedVertexNormals();

        _faces.Clear();

        for (int i = 0; i < _rawFaceKeys.Count; i++)
        {
            var cell = UnpackCell(_rawFaceKeys[i]);
            int dir = _rawFaceDirs[i];
            _faces.Add(ComputeFaceNormal(cell, dir, faceArea, vertexNormals));
        }
    }

    private void RecomputeDirtyFaces(HashSet<long> dirtyVertices)
    {
        if (dirtyVertices.Count == 0) return;

        float faceArea = CellSize * CellSize;

        var dirtyFaceIndices = new HashSet<int>();
        foreach (long vkey in dirtyVertices)
        {
            if (_vertexToFaceIndex.TryGetValue(vkey, out var faceSet))
            {
                for (int fi = 0; fi < faceSet.Count; fi++)
                    dirtyFaceIndices.Add(faceSet.Get(fi));
            }
        }

        Dictionary<long, Vector3> vertexNormals = null;
        if (SmoothingRings > 1)
            vertexNormals = ComputeDiffusedVertexNormals(dirtyVertices);

        // Sync _faces size
        while (_faces.Count < _rawFaceKeys.Count)
            _faces.Add(default);
        while (_faces.Count > _rawFaceKeys.Count)
            _faces.RemoveAt(_faces.Count - 1);

        // Only iterate dirty faces instead of all faces
        foreach (int i in dirtyFaceIndices)
        {
            if (i >= _rawFaceKeys.Count) continue;
            var cell = UnpackCell(_rawFaceKeys[i]);
            int dir = _rawFaceDirs[i];
            _faces[i] = ComputeFaceNormal(cell, dir, faceArea, vertexNormals);
        }
    }

    private SurfaceFace ComputeFaceNormal(Vector3I cell, int dir, float faceArea,
        Dictionary<long, Vector3> diffusedVertexNormals)
    {
        Vector3 originalNormal = DirNormals[dir];
        Vector3 pos = CellFaceCenter(cell, DirOffsets[dir]);

        Vector3 smoothNormal = Vector3.Zero;
        for (int v = 0; v < 4; v++)
        {
            long vkey = PackVertex(cell, dir, v);

            Vector3 vertexN;
            if (diffusedVertexNormals != null && diffusedVertexNormals.TryGetValue(vkey, out var diffused))
            {
                vertexN = Vector3.Dot(originalNormal, diffused) >= 0 ? diffused : Vector3.Zero;
            }
            else
            {
                if (!_vertexDirWeights.TryGetValue(vkey, out var vw))
                    continue;

                vertexN = Vector3.Zero;
                for (int d = 0; d < 6; d++)
                {
                    if (vw[d] > 0 && Vector3.Dot(originalNormal, DirNormals[d]) >= 0)
                        vertexN = vertexN + DirNormals[d] * ApplyWeight(vw[d]);
                }
            }

            float vLen = vertexN.Length();
            if (vLen > 1e-6f)
                smoothNormal = smoothNormal + vertexN / vLen;
        }

        float sLen = smoothNormal.Length();
        if (sLen > 1e-6f)
            smoothNormal = smoothNormal / sLen;
        else
            smoothNormal = originalNormal;

        Vector3 finalNormal;
        if (Smoothing >= 1f)
            finalNormal = smoothNormal;
        else if (Smoothing <= 0f)
            finalNormal = originalNormal;
        else
            finalNormal = Vector3.Normalize(
                originalNormal * (1f - Smoothing) + smoothNormal * Smoothing);

        return new SurfaceFace(pos, finalNormal, faceArea);
    }

    private float ApplyWeight(float w)
    {
        if (WeightExponent == 2f) return w * w;
        if (WeightExponent == 1f) return w;
        return MathF.Pow(w, WeightExponent);
    }

    // ─── Multi-ring Laplacian diffusion ────────────────────────────

    private Dictionary<long, Vector3> ComputeDiffusedVertexNormals()
        => ComputeDiffusedVertexNormals(null);

    /// <summary>
    /// Compute diffused vertex normals. When dirtyVertices is non-null, only computes
    /// normals for the dirty set expanded by SmoothingRings hops (localized diffusion).
    /// </summary>
    private Dictionary<long, Vector3> ComputeDiffusedVertexNormals(HashSet<long> dirtyVertices)
    {
        // Determine which vertices to process
        Dictionary<long, W6> source;
        int capacity;

        if (dirtyVertices != null)
        {
            // Expand dirty set to cover diffusion neighborhood
            // The faces recomputed touch the dirty vertices; their far corners are 2 hops away (a quad's diagonal),
            // and each of the SmoothingRings - 1 passes reaches one hop further: raw normals are needed out to
            // SmoothingRings + 1 hops for those corners to come out exactly as a full build's. (SmoothingRings hops
            // left the window's edge short of neighbours: damaged surfaces drifted from a fresh build's normals.)
            var expanded = _edgeRefCount != null && SmoothingRings > 1
                ? ExpandDirtyVertices(dirtyVertices, SmoothingRings + 1)
                : dirtyVertices;

            capacity = expanded.Count;
            var filtered = new Dictionary<long, W6>(capacity, LongKey.Comparer);
            foreach (long vkey in expanded)
            {
                if (_vertexDirWeights.TryGetValue(vkey, out var weights))
                    filtered[vkey] = weights;
            }
            source = filtered;
        }
        else
        {
            capacity = _vertexDirWeights.Count;
            source = _vertexDirWeights;
        }

        bool full = dirtyVertices == null;   // (the full pass reuses two dictionaries; the local pass is small)
        var vertexNormals = full ? (_normA ??= new Dictionary<long, Vector3>(capacity, LongKey.Comparer)) : new Dictionary<long, Vector3>(capacity, LongKey.Comparer);
        if (full) vertexNormals.Clear();
        foreach (var kvp in source)
        {
            var vw = kvp.Value;
            Vector3 n = Vector3.Zero;
            for (int d = 0; d < 6; d++)
            {
                if (vw[d] > 0)
                    n = n + DirNormals[d] * ApplyWeight(vw[d]);
            }
            float len = n.Length();
            vertexNormals[kvp.Key] = len > 1e-6f ? n / len : Vector3.Zero;
        }

        if (_edgeRefCount == null || SmoothingRings <= 1)
            return vertexNormals;

        Span<long> nb = stackalloc long[6];
        Span<long> ek = stackalloc long[6];

        for (int ring = 1; ring < SmoothingRings; ring++)
        {
            var newNormals = full
                ? (ReferenceEquals(vertexNormals, _normA) ? (_normB ??= new Dictionary<long, Vector3>(vertexNormals.Count, LongKey.Comparer)) : _normA)
                : new Dictionary<long, Vector3>(vertexNormals.Count, LongKey.Comparer);
            if (full) newNormals.Clear();
            foreach (var kvp in vertexNormals)
            {
                var key = kvp.Key;
                var normal = kvp.Value;
                Vector3 sum = normal;
                int count = 1;

                int nn = Neighbors(key, nb, ek);
                for (int k = 0; k < nn; k++)
                {
                    if (_creaseEdges != null && _creaseEdges.Contains(ek[k])) continue;
                    if (vertexNormals.TryGetValue(nb[k], out var neighborNormal))
                    {
                        sum = sum + neighborNormal;
                        count++;
                    }
                }

                float len = sum.Length();
                newNormals[key] = len > 1e-6f ? sum / len : normal;
            }
            vertexNormals = newNormals;
        }

        return vertexNormals;
    }

    // ─── Adjacency graph ──────────────────────────────────────────

    private void BuildAdjacency()
    {
        _edgeRefCount = _edgeStore ?? new Dictionary<long, int>(LongKey.Comparer); _edgeStore = null;
        _edgeRefCount.EnsureCapacity(_rawFaceKeys.Count * 2);

        for (int i = 0; i < _rawFaceKeys.Count; i++)
        {
            var cell = UnpackCell(_rawFaceKeys[i]);
            AddFaceEdges(cell, _rawFaceDirs[i]);
        }
    }

    private void AddFaceEdges(Vector3I cell, int dir)
    {
        if (_edgeRefCount == null) return;

        long v0 = PackVertex(cell, dir, 0);
        long v1 = PackVertex(cell, dir, 1);
        long v2 = PackVertex(cell, dir, 2);
        long v3 = PackVertex(cell, dir, 3);

        AddEdge(v0, v1); AddEdge(v1, v3); AddEdge(v3, v2); AddEdge(v2, v0);
    }

    private void RemoveFaceEdges(Vector3I cell, int dir)
    {
        if (_edgeRefCount == null) return;

        long v0 = PackVertex(cell, dir, 0);
        long v1 = PackVertex(cell, dir, 1);
        long v2 = PackVertex(cell, dir, 2);
        long v3 = PackVertex(cell, dir, 3);

        RemoveEdge(v0, v1); RemoveEdge(v1, v3); RemoveEdge(v3, v2); RemoveEdge(v2, v0);
    }

    // A vertex key's 20-bit fields (X << 40 | Y << 20 | Z, two's complement): stepped by unpacking, never by adding
    // to the key (a negative coordinate's field would carry into the next one).
    private static int VField(long v, int shift) => ((int)((v >> shift) & 0xFFFFF) << 12) >> 12;
    private static long VPack(int x, int y, int z) => ((long)(x & 0xFFFFF) << 40) | ((long)(y & 0xFFFFF) << 20) | (long)(z & 0xFFFFF);
    private static long VStep(long v, int axis, int d)
    {
        int x = VField(v, 40), y = VField(v, 20), z = VField(v, 0);
        if (axis == 0) x += d; else if (axis == 1) y += d; else z += d;
        return VPack(x, y, z);
    }
    private static long EdgeKey(long lowVertex, int axis) => (lowVertex << 2) | (long)axis;

    /// <summary>The key of the unit edge between two vertices one axis step apart.</summary>
    private static long EdgeKeyOf(long a, long b)
    {
        int dx = VField(b, 40) - VField(a, 40), dy = VField(b, 20) - VField(a, 20), dz = VField(b, 0) - VField(a, 0);
        int axis = dx != 0 ? 0 : dy != 0 ? 1 : 2;
        int d = dx + dy + dz;
        return EdgeKey(d > 0 ? a : b, axis);
    }

    /// <summary>A vertex's neighbours along the surface's edges (at most 6), and those edges' keys.</summary>
    private int Neighbors(long v, Span<long> neighbors, Span<long> edges)
    {
        int n = 0;
        for (int axis = 0; axis < 3; axis++)
        {
            long up = EdgeKey(v, axis);
            if (_edgeRefCount.ContainsKey(up)) { neighbors[n] = VStep(v, axis, 1); edges[n] = up; n++; }
            long lowV = VStep(v, axis, -1);
            long down = EdgeKey(lowV, axis);
            if (_edgeRefCount.ContainsKey(down)) { neighbors[n] = lowV; edges[n] = down; n++; }
        }
        return n;
    }

    private void AddEdge(long a, long b)
    {
        ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(_edgeRefCount, EdgeKeyOf(a, b), out _);
        count++;
    }

    private void RemoveEdge(long a, long b)
    {
        long key = EdgeKeyOf(a, b);
        if (!_edgeRefCount.TryGetValue(key, out int count))
            return;

        if (count <= 1)
        {
            _edgeRefCount.Remove(key);
            _creaseEdges?.Remove(key);
        }
        else
        {
            _edgeRefCount[key] = count - 1;
        }
    }

    // ─── Crease detection ─────────────────────────────────────────

    private void DetectAllCreases()
    {
        _creaseEdges = _creaseStore ?? new HashSet<long>(LongKey.Comparer); _creaseStore = null;
        _creaseEdges.Clear();
        if (_edgeRefCount == null) return;

        float cosThreshold = MathF.Cos(CreaseAngleDegrees * MathF.PI / 180f);

        // Every edge once
        foreach (var kvp in _edgeRefCount)
        {
            long key = kvp.Key;
            long vA = key >> 2;
            long vB = VStep(vA, (int)(key & 3), 1);
            if (IsCreaseEdge(vA, vB, cosThreshold))
                _creaseEdges.Add(key);
        }
    }

    private void UpdateCreasesAt(HashSet<long> vertices)
    {
        float cosThreshold = MathF.Cos(CreaseAngleDegrees * MathF.PI / 180f);
        Span<long> nb = stackalloc long[6];
        Span<long> ek = stackalloc long[6];
        foreach (long v in vertices)
        {
            int nn = Neighbors(v, nb, ek);
            for (int k = 0; k < nn; k++)
            {
                if (IsCreaseEdge(v, nb[k], cosThreshold)) _creaseEdges.Add(ek[k]);
                else _creaseEdges.Remove(ek[k]);
            }
        }
    }

    private bool IsCreaseEdge(long va, long vb, float cosThreshold)
    {
        if (!_vertexToFaceIndex.TryGetValue(va, out var facesA) ||
            !_vertexToFaceIndex.TryGetValue(vb, out var facesB))
            return false;

        // Every pair of the faces on the edge (2, or 4 where cells meet only along it): a crease if any pair bends
        // past the threshold. (The first two found decided before - their order is the order faces were added, so a
        // surface updated by damage and a fresh build of the same shape could disagree.)
        int dirMask = 0;
        for (int ai = 0; ai < facesA.Count; ai++)
        {
            int fi = facesA.Get(ai);
            if (!facesB.Contains(fi)) continue;
            if (fi >= _rawFaceDirs.Count) continue;
            dirMask |= 1 << _rawFaceDirs[fi];
        }
        for (int d1 = 0; d1 < 6; d1++)
        {
            if ((dirMask & (1 << d1)) == 0) continue;
            for (int d2 = d1 + 1; d2 < 6; d2++)
                if ((dirMask & (1 << d2)) != 0 && Vector3.Dot(DirNormals[d1], DirNormals[d2]) <= cosThreshold)
                    return true;
        }
        return false;
    }

    // ─── Dirty vertex expansion ──────────────────────────────────

    private HashSet<long> _frontierA = new(LongKey.Comparer);
    private HashSet<long> _frontierB = new(LongKey.Comparer);

    private HashSet<long> ExpandDirtyVertices(HashSet<long> dirty, int hops)
    {
        if (_edgeRefCount == null || hops <= 0) return dirty;
        Span<long> nb = stackalloc long[6];
        Span<long> ek = stackalloc long[6];

        var expanded = new HashSet<long>(dirty, LongKey.Comparer);

        _frontierA.Clear();
        foreach (long v in dirty) _frontierA.Add(v);

        for (int h = 0; h < hops; h++)
        {
            _frontierB.Clear();
            foreach (long v in _frontierA)
            {
                int nn = Neighbors(v, nb, ek);
                for (int k = 0; k < nn; k++)
                {
                    if (expanded.Add(nb[k]))
                        _frontierB.Add(nb[k]);
                }
            }
            if (_frontierB.Count == 0) break;
            // Swap
            (_frontierA, _frontierB) = (_frontierB, _frontierA);
        }

        return expanded;
    }

    // ─── Groups (persistent accumulators) ────────────────────────

    private readonly List<float> _grpArea = new() { 0f, 0f, 0f, 0f, 0f, 0f };
    private readonly List<Vector3> _grpWeightedPos = new() { Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero };
    private readonly List<Vector3> _grpNormalSum = new() { Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero, Vector3.Zero };
    private readonly List<int> _grpCount = new() { 0, 0, 0, 0, 0, 0 };
    private readonly List<float> _grpSxx = new() { 0f, 0f, 0f, 0f, 0f, 0f }, _grpSyy = new() { 0f, 0f, 0f, 0f, 0f, 0f }, _grpSzz = new() { 0f, 0f, 0f, 0f, 0f, 0f };
    private readonly List<float> _grpSxy = new() { 0f, 0f, 0f, 0f, 0f, 0f }, _grpSxz = new() { 0f, 0f, 0f, 0f, 0f, 0f }, _grpSyz = new() { 0f, 0f, 0f, 0f, 0f, 0f };

    private void RebuildGroups()
    {
        for (int d = 0; d < 6; d++)
        {
            _grpArea[d] = 0f; _grpCount[d] = 0;
            _grpSxx[d] = 0f; _grpSyy[d] = 0f; _grpSzz[d] = 0f;
            _grpSxy[d] = 0f; _grpSxz[d] = 0f; _grpSyz[d] = 0f;
            _grpWeightedPos[d] = Vector3.Zero; _grpNormalSum[d] = Vector3.Zero;
        }

        for (int i = 0; i < _faces.Count; i++)
        {
            var face = _faces[i];
            int dir = _rawFaceDirs[i];

            _grpArea[dir] += face.Area;
            _grpWeightedPos[dir] += face.Position * face.Area;
            _grpNormalSum[dir] += face.Normal;
            _grpCount[dir]++;
            _grpSxx[dir] += face.Area * face.Position.X * face.Position.X;
            _grpSyy[dir] += face.Area * face.Position.Y * face.Position.Y;
            _grpSzz[dir] += face.Area * face.Position.Z * face.Position.Z;
            _grpSxy[dir] += face.Area * face.Position.X * face.Position.Y;
            _grpSxz[dir] += face.Area * face.Position.X * face.Position.Z;
            _grpSyz[dir] += face.Area * face.Position.Y * face.Position.Z;
        }

        FinalizeGroups();
    }

    private void FinalizeGroups()
    {
        _groups.Clear();

        for (int d = 0; d < 6; d++)
        {
            if (_grpCount[d] == 0) continue;

            float a = _grpArea[d];
            var centroid = _grpWeightedPos[d] / a;

            Vector3 avgNormal = _grpNormalSum[d];
            float avgLen = avgNormal.Length();
            Vector3 groupNormal = avgLen > 1e-6f ? avgNormal / avgLen : DirNormals[d];

            float sxx = _grpSxx[d] - a * centroid.X * centroid.X;
            float syy = _grpSyy[d] - a * centroid.Y * centroid.Y;
            float szz = _grpSzz[d] - a * centroid.Z * centroid.Z;
            float sxy = _grpSxy[d] - a * centroid.X * centroid.Y;
            float sxz = _grpSxz[d] - a * centroid.X * centroid.Z;
            float syz = _grpSyz[d] - a * centroid.Y * centroid.Z;

            _groups.Add(new NormalGroup(
                groupNormal, a, centroid, _grpCount[d],
                sxx, syy, szz, sxy, sxz, syz));
        }
    }

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
