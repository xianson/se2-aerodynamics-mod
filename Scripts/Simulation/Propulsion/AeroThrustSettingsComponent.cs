#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;

namespace AeroMod;

/// <summary>
/// Per-thruster attitude authority setting. Injected onto thruster entities
/// by EntityInjector. Stores the fraction of thrust capacity reserved for
/// attitude control (0..1, default 0.5 = 50/50 split).
///
/// NOT READ by any controller (2026-10-17): the attitude law (ThrustTorque) never used it, and the old per-thruster path
/// that was meant to went with the legacy thrust path - it is only saved, restored and shown (the slider is off). Wire it
/// into ThrustTorque or remove it with its save key ("aero-thrust:1 f").
/// It also carries the vanilla atmospheric thrusters' Mach curve (AeroThrustSettingsComponent.Jet.cs, JetCurve).
/// Value is set via the terminal slider (AeroThrustBlockDetailModel).
/// Saved with the block, only off the default (AeroBlockSave, 2026-10-15; settings DESIGN.md W5(d) - it was lost on every
/// load; the first fix's "thrust x y z fraction" lines in the world's settings text are still read: AeroSurfaceSaved).
/// </summary>
[WhenSimulated]
public partial class AeroThrustSettingsComponent : Component, IInSceneListener
{
    /// <summary>
    /// Fraction of this thruster's capacity reserved for attitude control.
    /// 0 = all translation, 1 = all attitude, 0.5 = 50/50 split.
    /// </summary>
    public float AttitudeFraction = DefaultAttitudeFraction;
    public const float DefaultAttitudeFraction = 0.5f;
    /// <summary>Off a new thruster's (only those are saved).</summary>
    public bool Changed => AttitudeFraction != DefaultAttitudeFraction;
    /// <summary>In the server scene (the client scene's copy of the same block is not).</summary>
    internal bool Server = true;
    /// <summary>A client-scene copy's place, read by its own scene's step (AeroSurfaceSaved.ClientStep, under All's lock): what
    /// the save compares a server copy with - its transform is never read from the server's thread.</summary>
    internal Vector3D SeenAt; internal bool HasSeenAt;

    /// <summary>Every thruster's settings in the scene (the save walks them).</summary>
    internal static readonly List<AeroThrustSettingsComponent> All = new();

    void IInSceneListener.OnAddedToScene()
    {
        // (BlockDetailProviderIndexer discovers [BlockDetailProvider] types in mod assemblies on its own since 2.4.0.77, so
        //  AeroThrustBlockDetailModel self-registers: nothing to do for it)
        try { Server = !string.Equals(Data.Scene?.DebugName, "Client", StringComparison.Ordinal); } catch { Server = true; }
        lock (All) All.Add(this);
    }

    void IInSceneListener.OnBeforeRemovedFromScene() { lock (All) All.Remove(this); RestoreJet(); }   // (the Mach curve's rating written back: .Jet.cs)
}
