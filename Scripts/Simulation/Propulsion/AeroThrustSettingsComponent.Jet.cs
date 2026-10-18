#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.Game2.Simulation.WorldObjects.CubeBlocks;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Components;
using Keen.VRage.DCS.Annotations;

namespace AeroMod;

/// <summary>
/// The vanilla atmospheric thrusters' MACH CURVE, the game part (JetCurve: the law and its rules). Every thruster block carries
/// this component (EntityInjector, both scenes); on its first tick it looks once whether the curve applies (thrust class
/// Atmospheric, not a propulsor block - JetCurve.Applies) and, if not, never does anything again. If it does: every
/// JetCurve.Period ticks its ThrustData.MaxThrustPower = its rating x ThrustProfiles.Jet at its grid's Mach
/// (AeroGridComponent.LastMach), written only when it moved more than JetCurve.WriteTolerance; the curve lifted (JetCurve /
/// aero / ThrustTorque off, no aero grid) - the rating written back; removed from the scene - the rating written back.
/// The INLET ANGLE (InletAngle.Jet, 2026-10-17) multiplies on top: the angle between its intake (its push direction,
/// ThrustData.Direction in its own grid's frame) and the grid's true airspeed (AeroGridComponent.LastAirVel), faded in from
/// InletAngle.FadeNone to FadeFull m/s - one written maximum, rating x Mach factor x inlet factor (InletAngle.Enabled off:
/// the Mach curve alone).
/// Reports read JetApplied / JetFactor (both factors) / JetMachFactor / JetInletFactor / JetUnshaped (OffsetThrustJob.AppliedProfile,
/// DesignProbe, AeroApi.GetThrusters). No allocation, no exception path per tick.
/// </summary>
public partial class AeroThrustSettingsComponent
{
    /// <summary>0: not looked yet, 1: the curve applies, 2: it never does (hydrogen, ion, a propulsor block).</summary>
    int _jetKind;
    int _jetSkip;
    /// <summary>Its ReversibleThrustComponent, if any (looked once): the intake is its NATURAL push, not its game side now.</summary>
    ReversibleThrustComponent _jetRev;
    internal JetCurve.State Jet = JetCurve.NewState();
    /// <summary>The Jet MACH curve is applied to this thruster now (its maximum is shaped by it; JetCurve.Enabled).</summary>
    internal bool JetApplied => _jetKind == 1 && Jet.Applied && _jetMachOn;
    bool _jetMachOn;
    /// <summary>The factor applied now (1: none): the Mach curve x the inlet angle.</summary>
    internal float JetFactor => _jetKind == 1 && Jet.Applied ? Jet.Factor : 1f;
    /// <summary>Its parts at the last update: the Mach curve's, the inlet angle's (InletAngle.Jet) and the angle (deg).</summary>
    internal float JetMachFactor = 1f, JetInletFactor = 1f, JetInletDeg;
    /// <summary>A live maximum (N) taken back to the unshaped rating (DesignProbe's sea-level, Mach-0 figure).</summary>
    internal float JetUnshaped(float live) => _jetKind == 1 ? JetCurve.Unshaped(in Jet, live) : live;

    [Before(typeof(Keen.VRage.Core.Systems.Simulation))]
    [After(typeof(AeroPropulsorComponent.OnPropulsor))]
    internal class OnJetCurve : JobGroup;   // (ReversibleThrustComponent's step runs after it: both write ThrustData)

    [OnJetCurve]
    [MustHave(typeof(AeroThrustSettingsComponent))]
    private static void JetJob(AeroThrustSettingsComponent c) => c.JetTick();

    void JetTick()
    {
        if (_jetKind == 2) return;
        if (Entity == null || !GridPhysics.Alive(Data)) return;
        if (_jetKind == 0)
        {
            if (!Data.TryGet<ThrustData>(out var td0)) return;
            bool propulsor = Entity.TryGet<AeroPropulsorComponent>() != null || PropulsorBlocks.RowOf(Entity) != null;
            _jetKind = JetCurve.Applies(td0.ThrustClass.ToString(), propulsor) ? 1 : 2;   // (once per block)
            _jetRev = Entity.TryGet<ReversibleThrustComponent>();
            if (_jetKind == 2) return;
        }
        if (++_jetSkip < JetCurve.Period) return;
        _jetSkip = 0;
        if (!Data.TryGet<ThrustData>(out var td)) return;
        // (the air factors below are read whatever the block's state - the API's effectiveness and the readouts; only the write
        //  waits while it is disabled: off, unpowered, damaged - the game gives nothing whatever is written. 2026-10-17: the air
        //  was not read at all then, as AeroPropulsorComponent's)
        bool disabled = Data.Has<DisabledTag>();
        // (the two knobs apart: JetCurve.Enabled the Mach curve, InletAngle.Enabled the inlet angle; both off - the rating)
        bool active = (JetCurve.Enabled || InletAngle.Enabled) && AeroSwitch.Enabled && ThrustTorque.Enabled;
        float factor = 1f;
        JetMachFactor = 1f; JetInletFactor = 1f; JetInletDeg = 0f; _jetMachOn = false;
        if (active)
        {
            var top = Entity.GetTopLevelParent();
            var aero = top?.TryGet<AeroGridComponent>();
            if (aero == null || !aero.AirKnown) active = false;   // (its air not read yet: zeros, not still air - the rating)
            else
            {
                _jetMachOn = JetCurve.Enabled;
                if (_jetMachOn) JetMachFactor = JetCurve.Factor(aero.LastMach);
                if (InletAngle.Enabled)
                {
                    // the inlet angle: its intake (its push - ThrustData.Direction, in its OWN grid's frame: a subgrid's
                    // thruster in its subgrid's) against the true airspeed (the wind out) in that frame
                    var own = new DEntityContext(Data.Scene, td.ParentGrid);
                    var gwt = GridPhysics.Alive(own) ? own.GetWorldTransform() : top.Data.GetWorldTransform();
                    Vector3 vLocal = WorldTransform.TransformDirectionInv(aero.LastAirVel, gwt);
                    Vector3 intake = Base6Directions.GetVector(td.Direction);
                    if (_jetRev != null && _jetRev.Side < 0) intake = -intake;   // (a reversible one's intake stays where it is built)
                    JetInletDeg = InletAngle.AngleDeg(intake, vLocal);
                    JetInletFactor = InletAngle.Factor(InletAngle.Jet, JetInletDeg, vLocal.Length());
                }
                factor = JetMachFactor * JetInletFactor;
            }
        }
        if (disabled) return;
        if (!JetCurve.Step(ref Jet, td.MaxThrustPower, active, factor, out float write)) return;
        td.MaxThrustPower = write;
        Data.Set(td);
    }

    /// <summary>Leaving the scene (removed, the grid cut, the mod unloading it): the rating written back, so a block is never
    /// left with a shaped maximum the curve no longer tends (a shaped value that comes back is read as the game's then).</summary>
    void RestoreJet()
    {
        if (_jetKind != 1 || !Jet.Applied || !(Jet.Rated > 0f) || Entity == null || !GridPhysics.Alive(Data)) return;
        if (!Data.TryGet<ThrustData>(out var td) || td.MaxThrustPower == Jet.Rated) return;
        td.MaxThrustPower = Jet.Rated;
        Data.Set(td);
        Jet = JetCurve.NewState();
    }
}
