#pragma warning disable
using System;
namespace AeroMod;

// Incremental updates after block changes, and the raw faces kept by swap-with-last.
public partial class SmoothSurfaceProvider
{
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
}
