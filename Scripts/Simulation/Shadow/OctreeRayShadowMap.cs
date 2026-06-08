#pragma warning disable
using System;
using System.Runtime.CompilerServices;

namespace AeroMod;

/// <summary>
/// Shadow map using per-face ray march through the grid's cell lookup.
///
/// For each front-facing surface face, march a ray backwards along the flow
/// direction in 1-cell steps. If any cell is occupied, the face is shadowed.
///
/// Cost: O(faces × avg_steps)  — no depth buffer build, no cell enumeration.
/// Direction changes are free (no rebuild needed).
/// </summary>
public class OctreeRayShadowMap : IShadowMap
{
    // ─── Configuration ────────────────────────────────────────────

    /// <summary>Max ray length in cells. Limits worst-case cost.</summary>
    public int MaxRayLength { get; set; } = 40;

    /// <summary>
    /// Wake decay length in block units. Shadowed faces behind wing blocks
    /// recover visibility exponentially. Default 10.
    /// Only applies when WingCells is set.
    /// </summary>
    public float WakeDecayLength { get; set; } = 10f;

    /// <summary>
    /// Set of grid cells that belong to detected wings.
    /// When null or empty, all shadow is binary (no decay).
    /// </summary>
    public HashSet<Vector3I>? WingCells { get; set; }

    // ─── Output ───────────────────────────────────────────────────

    public List<bool> Visibility => _visibility;
    public List<float> VisibilityFactor => _visibilityFactor;
    public int VisibleCount { get; private set; }
    public int ShadowedCount { get; private set; }

    /// <summary>Incremented each time visibility is recomputed.</summary>
    public int Version { get; private set; }

    // ─── State ────────────────────────────────────────────────────

    private List<bool> _visibility = new();
    private List<float> _visibilityFactor = new();
    private Vector3 _cachedDirection;
    private bool _hasCachedDirection;
    private int _cachedSurfaceVersion = -1;

    /// <summary>
    /// Angular threshold (radians) below which a direction change
    /// does not trigger re-evaluation. Default ~3 degrees.
    /// (Cheaper than column map so we can afford tighter threshold)
    /// </summary>
    public float DirectionThreshold { get; set; } = 0.052f; // ~3°

    // ─── IShadowMap ──────────────────────────────────────────────

    public void Update(IGridAccessor grid, ISurfaceProvider provider, Vector3 flowDirection)
    {
        float speed = flowDirection.Length();
        if (speed < 0.001f)
        {
            EnsureLists(provider.FaceCount);
            for (int i = 0; i < _visibility.Count; i++)
            {
                _visibility[i] = false;
                _visibilityFactor[i] = 0f;
            }
            VisibleCount = 0;
            ShadowedCount = provider.FaceCount;
            Version++;
            _hasCachedDirection = false;
            return;
        }

        Vector3 flowDir = flowDirection / speed;

        bool surfaceChanged = _cachedSurfaceVersion != provider.Version;
        if (_hasCachedDirection && !surfaceChanged)
        {
            float dot = Vector3.Dot(flowDir, _cachedDirection);
            if (dot > MathF.Cos(DirectionThreshold))
                return;
        }

        EvaluateFaces(grid, provider, flowDir);
        Version++;

        _cachedDirection = flowDir;
        _hasCachedDirection = true;
        _cachedSurfaceVersion = provider.Version;
    }

    // ─── Core ─────────────────────────────────────────────────────

    private void EvaluateFaces(IGridAccessor grid, ISurfaceProvider provider, Vector3 flowDir)
    {
        var faces = provider.Faces;
        int count = faces.Count;
        EnsureLists(count);
        float blockSize = provider.BlockSize;
        float invBlock = 1f / blockSize;
        int maxSteps = MaxRayLength;
        var wingCells = WingCells;
        float decayLen = WakeDecayLength;

        int visible = 0, shadowed = 0;

        for (int i = 0; i < count; i++)
        {
            var face = faces[i];
            float cosAlpha = -Vector3.Dot(flowDir, face.Normal);

            // Back-facing → always shadowed
            if (cosAlpha <= 0)
            {
                _visibility[i] = false;
                _visibilityFactor[i] = 0f;
                shadowed++;
                continue;
            }

            // Ray origin: face position in cell coordinates
            float ox = face.Position.X * invBlock;
            float oy = face.Position.Y * invBlock;
            float oz = face.Position.Z * invBlock;

            // Step backwards along flow direction (into the wind)
            // Using DDA-like integer stepping for accuracy
            bool hit = false;
            float hitDist = 0f;
            bool hitWing = false;

            // Step size = 1 cell in flow direction
            // We step in the -flowDir direction (upstream)
            float dx = -flowDir.X;
            float dy = -flowDir.Y;
            float dz = -flowDir.Z;

            float cx = ox + dx * 0.6f; // start slightly upstream of face
            float cy = oy + dy * 0.6f;
            float cz = oz + dz * 0.6f;

            for (int step = 0; step < maxSteps; step++)
            {
                int ix = (int)MathF.Floor(cx);
                int iy = (int)MathF.Floor(cy);
                int iz = (int)MathF.Floor(cz);

                var cellPos = new Vector3I(ix, iy, iz);
                if (grid.IsCellOccupied(cellPos))
                {
                    hit = true;
                    hitDist = step + 1;
                    if (wingCells != null && wingCells.Contains(cellPos))
                        hitWing = true;
                    break;
                }

                cx += dx;
                cy += dy;
                cz += dz;
            }

            if (hit)
            {
                if (hitWing && decayLen > 0f)
                {
                    // Wing wake decay — partial visibility
                    float factor = 1f - MathF.Exp(-hitDist / decayLen);
                    _visibilityFactor[i] = factor;
                    _visibility[i] = factor >= 0.5f;
                    if (factor < 0.5f) shadowed++; else visible++;
                }
                else
                {
                    _visibility[i] = false;
                    _visibilityFactor[i] = 0f;
                    shadowed++;
                }
            }
            else
            {
                _visibility[i] = true;
                _visibilityFactor[i] = 1f;
                visible++;
            }
        }

        VisibleCount = visible;
        ShadowedCount = shadowed;
    }

    // ─── Helpers ──────────────────────────────────────────────────

    private void EnsureLists(int count)
    {
        if (_visibility.Count >= count) return;
        _visibility.Clear();
        _visibilityFactor.Clear();
        for (int i = 0; i < count; i++)
        {
            _visibility.Add(false);
            _visibilityFactor.Add(0f);
        }
    }
}
