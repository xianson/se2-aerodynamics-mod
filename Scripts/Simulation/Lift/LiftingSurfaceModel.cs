#pragma warning disable
using System;
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
    }

    /// <summary>Incremental wing update — only re-processes affected wings.</summary>
    public void UpdateWings(IGridAccessor grid, ISurfaceProvider surface, float blockSize,
        IReadOnlyList<Vector3I> addedCells, IReadOnlyList<Vector3I> removedCells)
    {
        _wings = Detector.Update(grid, surface, blockSize, addedCells, removedCells);
    }

    /// <summary>Clear cached wings and detector state.</summary>
    public void InvalidateWings()
    {
        _wings = null;
        Detector.Invalidate();
    }

    public AeroResult Compute(in AeroContext ctx)
    {
        var inner = InnerModel.Compute(ctx);

        if (_wings == null || _wings.Count == 0)
        {
            _lastWingForces = null;
            return inner;
        }

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

            float cosAlpha = Vector3.Dot(vHat, wing.Normal);
            if (cosAlpha > 0.01f)
            {
                float cp = innerCdBluff * cosAlpha;

                if (innerStreamlining > 0f)
                {
                    float recovery = cosAlpha * cosAlpha;
                    float minFraction = 0.08f;
                    float factor = minFraction + (1f - minFraction) * recovery;
                    cp *= 1f - innerStreamlining * (1f - factor);
                }

                var pressureForce = wing.Normal * (-cp * qf * wing.PlanformArea);

                totalForce -= pressureForce;
                totalTorque -= Vector3.Cross(wing.Centroid - ctx.CenterOfMass, pressureForce);
            }

            var wingTotalForce = wf.LiftForce + wf.InducedDrag;
            totalForce += wingTotalForce;
            totalTorque += Vector3.Cross(wf.ApplicationPoint - ctx.CenterOfMass, wingTotalForce);
        }

        // ── Drag floor ──
        float wingDragFloor = 0;
        for (int i = 0; i < wingForces.Count; i++)
        {
            var wf = wingForces[i];
            wingDragFloor += wf.InducedDrag.Length();
        }
        float faceCount = ctx.SurfaceCache.FaceCount;
        float avgFaceArea = inner.FrontalArea > 0 && faceCount > 0
            ? inner.FrontalArea * 4f
            : faceCount * ctx.BlockSize * ctx.BlockSize * 0.5f;
        wingDragFloor += qf * avgFaceArea * innerCfSkin;

        float forceDotV = Vector3.Dot(totalForce, vHat);
        float dragAlongV = -forceDotV;
        if (dragAlongV < wingDragFloor)
        {
            float addDrag = wingDragFloor - dragAlongV;
            totalForce -= vHat * addDrag;
        }

        // Recompute after floor
        forceDotV = Vector3.Dot(totalForce, vHat);
        Vector3 liftVec = totalForce - forceDotV * vHat;

        return new AeroResult(
            totalForce, totalTorque,
            MathF.Abs(forceDotV),
            liftVec.Length(),
            inner.FrontalArea,
            inner.Mach,
            inner.DynamicPressure);
    }
}
