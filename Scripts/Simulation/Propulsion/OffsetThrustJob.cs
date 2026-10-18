#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.Game2.Simulation.WorldObjects.CubeGrids.BlockOctrees;
using Keen.Game2.Simulation.WorldObjects.Movement;
using Keen.VRage.Core;

namespace AeroMod;

/// <summary>
/// Cached info about a thruster on the grid.
/// </summary>
public struct ThrusterInfo
{
    public Entity ThrusterEntity;
    public Vector3 GridLocalPosition;  // block center in grid-local space (meters): CellCoords.BlockCentre, the game's
    public Vector3 ThrustDirection;    // unit vector in grid-local space: the definition's thrust direction - the FLAME's way;
                                       // the force pushes the ship along -ThrustDirection (a reversible one's game side: Rev.Side)
    public float MaxPower;             // Newtons (impulse per frame = MaxPower / 60)
    public ThrustProfile Profile;      // its thrust-vs-Mach curve (null = rocket/flat): Jet for a vanilla atmospheric thruster,
                                       // the propulsor blocks' own (Turboprop, HelicopterRotor; null for a fan - its curve is by
                                       // airspeed); what is APPLIED: OffsetThrustJob.AppliedProfile
    public bool AirBreathing;          // thrust class Atmospheric (the propulsor blocks too): flow-through engine boxes
    public bool Propulsor;             // one of our propulsor blocks (AeroPropulsorComponent shapes its maximum itself)
    public AeroThrustSettingsComponent Settings; // live reference for reading slider changes
    /// <summary>The block's place in its grid (its cell box's Min + Max, 0.25 m cells): the same in the server and client
    /// copies of a grid, at any speed - the key its flame share is published under (ThrustTorque.Publish).</summary>
    public Vector3I Cell;
    /// <summary>The block's cell box (0.25 m cells, inclusive): flow-through engines (AeroGridComponent.EngineBoxes).</summary>
    public Vector3I CellMin, CellMax;
    /// <summary>Its ReversibleThrustComponent (null: a one-way thruster) - cached with the list: it pushes either way along
    /// ThrustDirection (its definition's way, the + of its signed forces); its game side is Rev.Side.</summary>
    public ReversibleThrustComponent Rev;
    public bool Reversible => Rev != null;
}

/// <summary>
/// Cached info about a gyroscope on the grid.
/// </summary>
public struct GyroInfo
{
    public Entity GyroEntity;
    public float MaxTorque;            // N·m
}

/// <summary>The grid's thruster and gyro caches (OffsetThrustJob.Cache.cs: RebuildThrusterCache / RebuildGyroCache, read by
/// ThrustTorque, the structure, the API and the design probe) and what a thruster's thrust-vs-Mach profile is as applied.
/// (The class name is the old per-thruster path's, kept for the harness and the tests' source checks; that path - the
/// legacy Execute with its Mach scaling, per-thruster overrides and phantom attitude torque - was removed 2026-10-17:
/// ThrustTorque is the only thrust path.)</summary>
public static partial class OffsetThrustJob
{
    /// <summary>The thrust-vs-Mach profile the simulation applies to this thruster: Jet for a vanilla atmospheric thruster
    /// while its Mach curve is on (AeroThrustSettingsComponent.JetApplied: its game maximum is shaped - JetCurve), else none
    /// (hydrogen, ion; a propulsor block - its own airspeed curve is in its live maximum, AeroPropulsorComponent).
    /// What reports read (DesignProbe, AeroApi.GetThrusters), not ThrusterInfo.Profile - 2026-10-15: they showed every
    /// atmospheric thruster Mach-scaled (Turbofan: 0.6 at Mach 0.8) while the simulation applied no such scaling.</summary>
    internal static ThrustProfile AppliedProfile(in ThrusterInfo t)
        => t.Settings != null && t.Settings.JetApplied ? ThrustProfiles.Jet : null;
}
