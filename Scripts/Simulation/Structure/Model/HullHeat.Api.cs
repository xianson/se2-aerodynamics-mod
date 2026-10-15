#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
namespace AeroMod;

/// <summary>
/// HULL HEAT for another heat model (the public API: AeroApi.GetHeatFaceState) - the air's side of each face as a
/// boundary condition: q = h (T0 - Twall) W/m2, h the face's film coefficient and T0 the flow's recovery temperature, both
/// of the last Step. A block thermal mod with its own wall temperature takes the heat from these; FaceHeatW is the same
/// at aero's own wall temperature.
/// </summary>
public sealed partial class HullHeat
{
    /// <summary>The flow's recovery (total) temperature of the last Step (K): T + V^2 / 2cp. 0 before any.</summary>
    public float LastRecoveryK;
    float _lastH0, _lastHPlate;

    /// <summary>A face's film coefficient at the last Step (W/m2 K): its stagnation share and its attached flow's, as Step
    /// heated it (0 out of the air, or before the first Pass).</summary>
    public float FaceFilm(int i) => _lastH0 * _geo[i] + _lastHPlate * _side[i];
}
