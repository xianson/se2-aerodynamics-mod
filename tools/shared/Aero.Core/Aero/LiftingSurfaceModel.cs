using System.Numerics;

namespace Aero.Core.Aero;

/// <summary>
/// Wrapper drag model that adds circulation lift to an inner drag model.
///
/// Strategy:
///   1. Compute inner model forces (Newtonian pressure + skin friction on all faces)
///   2. Subtract estimated Newtonian pressure force on wing faces
///   3. Add circulation lift + induced drag from wing model
///   4. Enforce drag floor: total drag never falls below skin friction estimate
///
/// The drag floor prevents the exploit where pressure subtraction drives drag
/// to near-zero on all-wing designs, yielding infinite L/D.
/// </summary>
public class LiftingSurfaceModel : AeroDragModelBase
{
    public override string Name => $"Lifting Surface + {InnerModel.Name}";
    public override string Description => $"Circulation lift ({_wings?.Length ?? 0} wings) + {InnerModel.Description}";

    /// <summary>Inner drag model for non-circulatory forces.</summary>
    public AeroDragModelBase InnerModel { get; }

    /// <summary>Wing detector (build-time).</summary>
    public IWingDetector Detector { get; }

    /// <summary>Wing lift model (runtime).</summary>
    public IWingLiftModel LiftModel { get; }

    /// <summary>Detected wings (null until DetectWings is called).</summary>
    public LiftingSurface[]? Wings => _wings;

    /// <summary>Last computed wing force results (for diagnostics).</summary>
    public WingForceResult[]? LastWingForces => _lastWingForces;

    private LiftingSurface[]? _wings;
    private WingForceResult[]? _lastWingForces;

    public LiftingSurfaceModel(AeroDragModelBase innerModel,
        IWingDetector? detector = null,
        IWingLiftModel? liftModel = null)
    {
        InnerModel = innerModel;
        Detector = detector ?? new ConnectedComponentWingDetector();
        LiftModel = liftModel ?? new ThinAirfoilWingModel();
    }

    /// <summary>
    /// Detect wings from the current grid geometry. Call after building the surface.
    /// Results are cached until this is called again.
    /// </summary>
    public void DetectWings(Grids.Grid grid, ISurfaceProvider surface, float blockSize)
    {
        _wings = Detector.Detect(grid, surface, blockSize);
    }

    /// <summary>Clear cached wings (e.g., on grid change).</summary>
    public void InvalidateWings() => _wings = null;

    public override AeroResult Compute(in AeroContext ctx)
    {
        // Always compute inner model forces
        var inner = InnerModel.Compute(ctx);

        if (_wings == null || _wings.Length == 0)
        {
            _lastWingForces = null;
            return inner;
        }

        // Compute wing lift + induced drag
        var wingForces = LiftModel.ComputeWingForces(_wings, ctx);
        _lastWingForces = wingForces;

        float speed = ctx.Velocity.Length();
        Vector3 vHat = speed > 0.01f ? ctx.Velocity / speed : Vector3.UnitX;
        float qf = (float)ctx.Atmosphere.GetDynamicPressure(speed);

        var totalForce = inner.Force;
        var totalTorque = inner.Torque;

        // Extract inner model parameters once
        float innerCdBluff = 1.1f;
        float innerStreamlining = 0f;
        float innerCfSkin = 0.005f;
        switch (InnerModel)
        {
            case DampedShadowedDragModel dsm:
                innerCdBluff = dsm.CdBluff;
                innerStreamlining = dsm.Streamlining;
                innerCfSkin = dsm.CfSkin;
                break;
            case FastDragModel fdm:
                innerCdBluff = fdm.CdBluff;
                innerStreamlining = fdm.Streamlining;
                innerCfSkin = fdm.CfSkin;
                break;
            case ShadowedDragModel sdm:
                innerCdBluff = sdm.CdBluff;
                break;
            case NewtonianDragModel ndm:
                innerCdBluff = ndm.CdBluff;
                break;
        }

        for (int i = 0; i < wingForces.Length; i++)
        {
            ref readonly var wf = ref wingForces[i];
            ref readonly var wing = ref _wings[i];

            float cosAlpha = Vector3.Dot(vHat, wing.Normal);
            if (cosAlpha > 0.01f)
            {
                float cp = innerCdBluff * cosAlpha;

                // Apply the same streamlining reduction the inner model used
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

            // Add circulation lift + induced drag
            var wingTotalForce = wf.LiftForce + wf.InducedDrag;
            totalForce += wingTotalForce;
            totalTorque += Vector3.Cross(wf.ApplicationPoint - ctx.CenterOfMass, wingTotalForce);
        }

        // ── Drag floor ──
        // Total drag must not be less than what the wing models themselves produce.
        // This prevents pressure subtraction from zeroing out drag on all-wing ships.
        // Floor = sum of each wing's (CDi + Cd0) × q × S + skin friction on non-wing area.
        float wingDragFloor = 0;
        for (int i = 0; i < wingForces.Length; i++)
        {
            ref readonly var wf = ref wingForces[i];
            // The wing model's drag magnitude = |inducedDrag| (already includes CDi + Cd0)
            wingDragFloor += wf.InducedDrag.Length();
        }
        // Add minimum skin friction on the entire wetted surface.
        // Estimate wetted area from face count × avg face area.
        float faceCount = ctx.SurfaceCache.FaceCount;
        float avgFaceArea = inner.FrontalArea > 0 && faceCount > 0
            ? inner.FrontalArea * 4f // rough: wetted ≈ 4× frontal
            : faceCount * ctx.BlockSize * ctx.BlockSize * 0.5f;
        wingDragFloor += qf * avgFaceArea * innerCfSkin;

        float forceDotV = Vector3.Dot(totalForce, vHat);
        float dragAlongV = -forceDotV; // positive = drag opposing motion
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
