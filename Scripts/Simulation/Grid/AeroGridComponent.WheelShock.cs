#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System.Threading;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

public partial class AeroGridComponent
{
    /// <summary>The wheel shocks' impulses (AeroWheelShockComponent): after every wheel's own job, before the physics step - one
    /// grid's job a tick takes them all (a grid's body is shared by its wheels' parallel jobs; as OnRotorStrike).</summary>
    [After(typeof(AeroWheelShockComponent.OnWheelShock))]
    [Before(typeof(Keen.Game2.Simulation.Physics))]
    private class OnWheelShockApply : JobGroup;

    [OnWheelShockApply]
    [MustHave(typeof(AeroGridComponent))]
    private static void WheelShockApplyJob(AeroGridComponent aero)
    {
        if (!aero.IsServerScene || Volatile.Read(ref AeroWheelShockComponent.Armed) == 0) return;
        if (Interlocked.Exchange(ref AeroWheelShockComponent.Armed, 0) == 0) return;   // (another grid's job took this tick)
        AeroWheelShockComponent.ApplyAll();
    }
}
