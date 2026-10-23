#pragma warning disable
using System;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.UI.PropertyInspector;

namespace AeroMod;

/// <summary>
/// A main helicopter rotor's TURN (2026-10-23, the user: "a terminal option to flip the rotor torque"): injected into the main
/// rotors' SERVER prefabs (PropulsorBlocks.MainRotorServerPrefabs - not the Reversible Rotor, not the turboprops), saved with
/// the block (AeroBlockSave: "aero-rotorspin:1 1", only when counter-rotating). Off (the default): clockwise seen from above,
/// as every rotor before. On: COUNTER-ROTATING - the mirror image (RotorKinematics.MirrorX):
///  - its reaction torque acts the other way (RotorCyclic.ReactionTorque, RotorKinematics.ReactionAxis): a pair of opposite
///    rotors, coaxial or tandem, cancels - and the limiter counts the opposite one as the answer (RotorLimiter.Targets);
///  - its blades move the other way in a strike (AeroRotorStrikeComponent) and their angular momentum is dumped the other way;
///  - its client copy draws it mirrored, with the mirrored blade models (AeroRotorAnimComponent: the published record's turn,
///    ThrustTorque.PublishRotor).
/// Its propulsor reads it each tick (AeroPropulsorComponent.CounterRotating, server); the client copy's from the record.
/// </summary>
public partial class AeroRotorSpinComponent : Component
{
    /// <summary>Counter-clockwise seen from above (false: clockwise, the default). Saved (AeroBlockSave).</summary>
    public bool CounterRotating;
}

/// <summary>
/// A main helicopter rotor's terminal (2026-10-23): the Counter-rotating switch. Host / single player, as every mod panel
/// (DetailModelPairing; see AeroSurfaceBlockDetailModel).
/// </summary>
[BlockDetailProvider]
[BlockDetailDisplayMode(DisplayMode.Controls)]
// SE2 4.0 runs the serializer generators on mod scripts: [NoSerialize] keeps them off a mod model (AeroAirbrakeBlockDetailModel).
[Keen.VRage.Library.Serialization.NoSerialize]
public partial class AeroRotorBlockDetailModel : BlockDetailModel
{
    static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo _typeInfo = ModDetailTypeInfo.Build<AeroRotorBlockDetailModel>(
        "Helicopter rotor: Counter-rotating turns it the other way, so its torque on the ship flips - pair it with a normal rotor (coaxial or tandem) and they cancel, no tail rotor needed.",
        nameof(CounterRotating));
    public override Keen.VRage.Library.Serialization.AccessibleTypeInfo GetTypeInfo() => _typeInfo;
    /// <summary>The panel's order as listed (the game finds a type's member order here - ModDetailTypeInfo).</summary>
    public static class TypeInfoHolder { public static readonly Keen.VRage.Library.Serialization.AccessibleTypeInfo Value = _typeInfo; }

    [Keen.VRage.DCS.Annotations.Component]
    private AeroRotorSpinComponent _spin;

    public AeroRotorBlockDetailModel() { DetailModelPairing.Pair(this, _spin); }

    [Label("Counter-rotating")]
    public bool CounterRotating { get => _spin?.CounterRotating ?? false; set { if (_spin != null) _spin.CounterRotating = value; } }
}
