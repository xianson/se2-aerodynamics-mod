#pragma warning disable
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
namespace AeroMod;

/// <summary>
/// Wrapper drag model that adds circulation lift to an inner drag model.
///
/// Strategy:
///   1. Compute inner model forces (Newtonian pressure + skin friction on all faces)
///   2. Subtract estimated Newtonian pressure force on wing faces
///   3. Add circulation lift + induced drag from wing model
///   4. Enforce drag floor: total drag never falls below skin friction estimate
/// </summary>
public class LiftingSurfaceModel : IAeroDragModel
{
    public string Name => $"Lifting Surface + {InnerModel.Name}";

    /// <summary>Inner drag model for non-circulatory forces.</summary>
    public IAeroDragModel InnerModel { get; }

    /// <summary>Wing detector (build-time).</summary>
    public IWingDetector Detector { get; }

    /// <summary>Wing lift model (runtime).</summary>
    public IWingLiftModel LiftModel { get; }

    /// <summary>Detected wings (null until DetectWings is called).</summary>
    public List<LiftingSurface>? Wings => _wings;

    /// <summary>Last computed wing force results (for diagnostics).</summary>
    public List<WingForceResult>? LastWingForces => _lastWingForces;

    private List<LiftingSurface>? _wings;
    private List<WingForceResult>? _lastWingForces;

    // ─── Face override index (wing + component Cp overrides) ─────
    private List<int>? _faceOwnerIdx;   // face index → owner index (-1 = no override)
    private List<float>? _ownerCp;      // effective Cp per owner
    private int _wingCount;

    public LiftingSurfaceModel(IAeroDragModel innerModel,
        IWingDetector? detector = null,
        IWingLiftModel? liftModel = null)
    {
        InnerModel = innerModel;
        Detector = detector ?? new ConnectedComponentWingDetector();
        LiftModel = liftModel ?? new ThinAirfoilWingModel();
    }

    /// <summary>Full wing detection from scratch.</summary>
    public void DetectWings(IGridAccessor grid, ISurfaceProvider surface, float blockSize)
    {
        _wings = Detector.Detect(grid, surface, blockSize);
        InvalidateLiftModelInfluence();
    }

    /// <summary>Wings detected elsewhere (a background rebuild): installed as they are.</summary>
    public void InstallWings(List<LiftingSurface>? wings)
    {
        _wings = wings;
        InvalidateLiftModelInfluence();
        IndexWingCells();
    }

    // -- Wings lost at once: a destroyed cell of a wing takes its share of that wing's forces the same frame (the
    //    rebuild, seconds later on a big grid, re-detects the wings as they are). --
    private readonly Dictionary<Vector3I, int> _wingOfCell = new();
    private int[] _wingCells = System.Array.Empty<int>(), _wingLeft = System.Array.Empty<int>();

    private void IndexWingCells()
    {
        _wingOfCell.Clear();
        int n = _wings?.Count ?? 0;
        _wingCells = new int[n]; _wingLeft = new int[n];
        for (int i = 0; i < n; i++)
        {
            var cells = _wings[i].Cells;
            if (cells == null) continue;
            foreach (var c in cells) _wingOfCell[c] = i;
            _wingCells[i] = _wingLeft[i] = cells.Count;
        }
    }

    /// <summary>A cell is gone: if a wing owned it, that wing has that much less of itself.</summary>
    public void RemoveWingCell(Vector3I cell)
    {
        if (_wingOfCell.Count == 0 || !_wingOfCell.Remove(cell, out int wi)) return;
        if (wi < _wingLeft.Length && _wingLeft[wi] > 0) _wingLeft[wi]--;
    }

    /// <summary>The fraction of wing i still there.</summary>
    public float WingRemaining(int i) => i < _wingCells.Length && _wingCells[i] > 0 ? _wingLeft[i] / (float)_wingCells[i] : 1f;

    /// <summary>Incremental wing update — only re-processes affected wings.</summary>
    public void UpdateWings(IGridAccessor grid, ISurfaceProvider surface, float blockSize,
        IReadOnlyList<Vector3I> addedCells, IReadOnlyList<Vector3I> removedCells)
    {
        _wings = Detector.Update(grid, surface, blockSize, addedCells, removedCells);
        InvalidateLiftModelInfluence();
    }

    private void InvalidateLiftModelInfluence()
    {
        if (LiftModel is CompressibleWingModel cwm) cwm.InvalidateInfluence();
        else if (LiftModel is ThinAirfoilWingModel tawm) tawm.InvalidateInfluence();
    }

    /// <summary>Clear cached wings and detector state.</summary>
    public void InvalidateWings()
    {
        _wings = null;
        Detector.Invalidate();
    }

    /// <summary>The last drag split (N, along the velocity): faces, wings (incl. pressure removed), floor added.</summary>
    public float LastInnerDrag, LastWingsDrag, LastFloorAdd;

    // ─── Force table ──────────────────────────────────────────────
    private ForceTable _table, _prevTable;
    private int _blendLeft;
    private const int BlendFrames = 30;
    /// <summary>Whether the last Compute used the force table (else the face loop).</summary>
    public bool UsedTable { get; private set; }
    public ForceTable Table => _table;

    /// <summary>The force table for the surface just swapped in; the old one fades out over BlendFrames.</summary>
    public void InstallForceTable(ForceTable table)
    {
        _prevTable = _table;
        _table = table;
        _blendLeft = _prevTable != null ? BlendFrames : 0;
    }

    /// <summary>The faces' forces: the force table if it is for this surface, else the face loop.</summary>
    private AeroResult InnerForces(in AeroContext ctx)
    {
        // (kept current through damage by patches - AeroGridComponent.PatchTable - so no longer tied to a surface version)
        if (_table != null && InnerModel is DampedShadowedDragModel d)
        {
            UsedTable = true;
            var r = _table.Evaluate(ctx, d.SubsonicLimit, d.SupersonicLimit, d.Streamlining);
            if (_blendLeft > 0 && _prevTable != null)
            {
                // (a rebuilt grid's new forces fade in: no jump when damage lands)
                float a = _blendLeft / (float)(BlendFrames + 1);
                var o = _prevTable.Evaluate(ctx, d.SubsonicLimit, d.SupersonicLimit, d.Streamlining);
                r = new AeroResult(r.Force + (o.Force - r.Force) * a, r.Torque + (o.Torque - r.Torque) * a, r.DragMagnitude + (o.DragMagnitude - r.DragMagnitude) * a,
                    r.LiftMagnitude + (o.LiftMagnitude - r.LiftMagnitude) * a, r.FrontalArea, r.Mach, r.DynamicPressure);
                _blendLeft--;
            }
            return r;
        }
        UsedTable = false;
        return InnerModel.Compute(ctx);
    }

    public AeroResult Compute(in AeroContext ctx)
    {
        var inner = InnerForces(ctx);

        if (_wings == null || _wings.Count == 0)
        {
            _lastWingForces = null;
            AeroStats.SetLift(0);
            return inner;
        }

        long liftStart = AeroStats.Timestamp();
        var wingForces = LiftModel.ComputeWingForces(CollectionsMarshal.AsSpan(_wings), ctx);
        _lastWingForces = wingForces;

        float speed = ctx.Velocity.Length();
        Vector3 vHat = speed > 0.01f ? ctx.Velocity / speed : Vector3.UnitX;
        float qf = (float)ctx.Atmosphere.GetDynamicPressure(speed);

        var totalForce = inner.Force;
        var totalTorque = inner.Torque;

        // Extract inner model parameters for pressure subtraction
        float innerCdBluff = 1.1f;
        float innerStreamlining = 0f;
        float innerCfSkin = 0.005f;
        if (InnerModel is DampedShadowedDragModel dsm)
        {
            innerCdBluff = dsm.CdBluff;
            innerStreamlining = dsm.Streamlining;
            innerCfSkin = dsm.CfSkin;
        }
        else if (InnerModel is NewtonianDragModel ndm)
        {
            innerCdBluff = ndm.CdBluff;
            innerCfSkin = ndm.CfSkin;
        }

        for (int i = 0; i < wingForces.Count; i++)
        {
            var wf = wingForces[i];
            var wing = _wings[i];

            // The wing owns its skins (both: the face model leaves them out, ExcludeWingFaces): their drag is
            // skin friction on both sides, here, besides the induced drag.
            float left = WingRemaining(i);
            var profileDrag = -vHat * (2f * innerCfSkin * qf * wing.PlanformArea * left);
            totalForce += profileDrag;
            totalTorque += Vector3.Cross(wing.Centroid - ctx.CenterOfMass, profileDrag);

            var wingTotalForce = (wf.LiftForce + wf.InducedDrag) * left;
            totalForce += wingTotalForce;
            totalTorque += Vector3.Cross(wf.ApplicationPoint - ctx.CenterOfMass, wingTotalForce);
        }

        // (No drag floor: it made up for the estimated skin pressure removed above - up to 850 kN on the
        // Jetliner, switching on and off. The wing's skins are now left out of the face model exactly.)
        float forceDotV0 = Vector3.Dot(totalForce, vHat);
        LastInnerDrag = -Vector3.Dot(inner.Force, vHat);
        LastWingsDrag = -forceDotV0 - LastInnerDrag;
        LastFloorAdd = 0f;

        float forceDotV = forceDotV0;
        Vector3 liftVec = totalForce - forceDotV * vHat;

        AeroStats.SetLift(AeroStats.ElapsedUs(liftStart));

        return new AeroResult(
            totalForce, totalTorque,
            MathF.Abs(forceDotV),
            liftVec.Length(),
            inner.FrontalArea,
            inner.Mach,
            inner.DynamicPressure);
    }

    // ─── Face override index ─────────────────────────────────────

    /// <summary>
    /// Build a mapping from face index → override owner index.
    /// Owner indices: [0..wingCount-1] = wings, [wingCount..] = IFaceOverride components.
    /// Must be called after DetectWings() and whenever components change.
    /// </summary>
    public void BuildFaceOverrideIndex(SmoothSurfaceProvider surface,
        IReadOnlyList<IAeroBlockComponent> components)
    {
        int n = surface.FaceCount;
        int padded = (n + 7) & ~7;
        if (_faceOwnerIdx == null || _faceOwnerIdx.Count < padded)
        {
            _faceOwnerIdx = new List<int>(padded);
            for (int fi = 0; fi < padded; fi++) _faceOwnerIdx.Add(-1);
        }
        else
        {
            for (int fi = 0; fi < padded; fi++) _faceOwnerIdx[fi] = -1;
        }

        _wingCount = _wings?.Count ?? 0;

        // Wings: owner indices 0..wingCount-1
        if (_wings != null)
        {
            for (int wi = 0; wi < _wings.Count; wi++)
            {
                var wing = _wings[wi];
                int dir = NormalToDir(wing.Normal);
                int opp = dir ^ 1;
                var step = DirStep(dir);
                foreach (var cell in wing.Cells)
                {
                    int fi = surface.GetFaceIndex(cell, dir);
                    if (fi >= 0) _faceOwnerIdx[fi] = wi;
                    // the other skin: through the wing's thickness, the first face the other way
                    var c = cell;
                    for (int k = 0; k < MaxSkinDepthCells; k++, c -= step)
                    {
                        int fo = surface.GetFaceIndex(c, opp);
                        if (fo >= 0) { _faceOwnerIdx[fo] = wi; break; }
                    }
                }
            }
        }

        // IFaceOverride components: owner indices wingCount..wingCount+N-1
        int ci = 0;
        for (int i = 0; i < components.Count; i++)
        {
            if (components[i] is not IFaceOverride fo) continue;
            int ownerIdx = _wingCount + ci;
            foreach (var cell in fo.OwnedCells)
            {
                // Components can have faces in any direction — check all 6
                for (int d = 0; d < 6; d++)
                {
                    int fi = surface.GetFaceIndex(cell, d);
                    if (fi >= 0) _faceOwnerIdx[fi] = ownerIdx;
                }
            }
            ci++;
        }
    }

    /// <summary>
    /// Override Cp values in the drag model's FaceCp list for faces owned by
    /// wings or components. Wing faces get |CL|, component faces get EffectiveCp.
    /// Call after Compute() and after all component Compute() calls.
    /// </summary>
    public void OverrideFaceCp(DampedShadowedDragModel dsm,
        IReadOnlyList<IAeroBlockComponent> components)
    {
        if (_faceOwnerIdx == null) return;

        var faceCp = dsm.FaceCp;
        if (faceCp == null || faceCp.Count == 0) return;

        // Count IFaceOverride components
        int compCount = 0;
        for (int i = 0; i < components.Count; i++)
            if (components[i] is IFaceOverride) compCount++;

        int totalOwners = _wingCount + compCount;
        if (totalOwners == 0) return;

        if (_ownerCp == null || _ownerCp.Count < totalOwners)
        {
            int sz = Math.Max(totalOwners, 1);
            _ownerCp = new List<float>(sz);
            for (int oi = 0; oi < sz; oi++) _ownerCp.Add(0f);
        }

        // Wing owners: |CL|
        for (int wi = 0; wi < _wingCount; wi++)
        {
            _ownerCp[wi] = (_lastWingForces != null && wi < _lastWingForces.Count)
                ? MathF.Abs(_lastWingForces[wi].CL)
                : 0f;
        }

        // Component owners: EffectiveCp
        int ci = 0;
        for (int i = 0; i < components.Count; i++)
        {
            if (components[i] is IFaceOverride fo)
                _ownerCp[_wingCount + ci++] = fo.EffectiveCp;
        }

        // SIMD override
        OverrideCpSimd(faceCp);
    }

    private void OverrideCpSimd(List<float> faceCp)
    {
        var cpSpan = CollectionsMarshal.AsSpan(faceCp);
        int n = Math.Min(cpSpan.Length, _faceOwnerIdx!.Count);

        if (Vector256.IsHardwareAccelerated && n >= 8 && _ownerCp != null)
        {
            var idxSpan = CollectionsMarshal.AsSpan(_faceOwnerIdx);
            var ownerSpan = CollectionsMarshal.AsSpan(_ownerCp);
            ref int idxRef = ref MemoryMarshal.GetReference(idxSpan);
            ref float cpRef = ref MemoryMarshal.GetReference(cpSpan);
            ref float ownerRef = ref MemoryMarshal.GetReference(ownerSpan);

            var negOne = Vector256.Create(-1);
            int i = 0;
            for (; i + 7 < n; i += 8)
            {
                var idx = Vector256.LoadUnsafe(ref Unsafe.Add(ref idxRef, i));
                var mask = Vector256.GreaterThan(idx, negOne);

                if (mask == Vector256<int>.Zero)
                {
                    continue;
                }

                // Scalar gather (no Avx2.GatherVector256 needed — avoids unsafe)
                var safeIdx = Vector256.Max(idx, Vector256<int>.Zero);
                var ownerCp = Vector256.Create(
                    Unsafe.Add(ref ownerRef, safeIdx.GetElement(0)),
                    Unsafe.Add(ref ownerRef, safeIdx.GetElement(1)),
                    Unsafe.Add(ref ownerRef, safeIdx.GetElement(2)),
                    Unsafe.Add(ref ownerRef, safeIdx.GetElement(3)),
                    Unsafe.Add(ref ownerRef, safeIdx.GetElement(4)),
                    Unsafe.Add(ref ownerRef, safeIdx.GetElement(5)),
                    Unsafe.Add(ref ownerRef, safeIdx.GetElement(6)),
                    Unsafe.Add(ref ownerRef, safeIdx.GetElement(7)));

                var cp = Vector256.LoadUnsafe(ref Unsafe.Add(ref cpRef, i));
                var result = Vector256.ConditionalSelect(mask.AsSingle(), ownerCp, cp);
                result.StoreUnsafe(ref Unsafe.Add(ref cpRef, i));
            }

            // Scalar tail
            for (; i < n; i++)
            {
                int oi = _faceOwnerIdx[i];
                if (oi >= 0)
                {
                    cpSpan[i] = _ownerCp[oi];
                }
            }
        }
        else
        {
            for (int i = 0; i < n; i++)
            {
                int oi = _faceOwnerIdx![i];
                if (oi >= 0 && _ownerCp != null)
                {
                    cpSpan[i] = _ownerCp[oi];
                }
            }
        }
    }

    /// <summary>How deep (cells) the other skin is looked for under a wing's skin: 4 m.</summary>
    private const int MaxSkinDepthCells = 16;

    private static Vector3I DirStep(int dir) => dir switch
    {
        0 => new Vector3I(1, 0, 0), 1 => new Vector3I(-1, 0, 0),
        2 => new Vector3I(0, 1, 0), 3 => new Vector3I(0, -1, 0),
        4 => new Vector3I(0, 0, 1), _ => new Vector3I(0, 0, -1),
    };

    /// <summary>The faces the wings own (both skins), for the face model to leave out, and the surface version
    /// they index.</summary>
    public void ExcludeWingFaces(DampedShadowedDragModel dsm, int surfaceVersion)
    {
        if (_faceOwnerIdx == null) { dsm.SetExcludedFaces(null, surfaceVersion); return; }
        var mask = _wingFaceMask ??= new List<bool>();
        mask.Clear();
        for (int i = 0; i < _faceOwnerIdx.Count; i++) { int o = _faceOwnerIdx[i]; mask.Add(o >= 0 && o < _wingCount); }
        dsm.SetExcludedFaces(mask, surfaceVersion);
    }
    private List<bool> _wingFaceMask;

    private static int NormalToDir(Vector3 n)
    {
        if (n.X >  0.5f) return 0;
        if (n.X < -0.5f) return 1;
        if (n.Y >  0.5f) return 2;
        if (n.Y < -0.5f) return 3;
        if (n.Z >  0.5f) return 4;
        return 5;
    }
}
