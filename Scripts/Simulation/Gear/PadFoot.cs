#pragma warning disable
using System;

namespace AeroMod;

/// <summary>
/// A PAD-FOOT gear's foot (2026-10-20, the user: "No lock, custom wheel model that is the landing gear, and constrained"): the
/// suspension's wheel IS the foot - a vanilla landing-gear mesh on a copy of the vanilla wheel prefab (tools/meshgen/gen_padgear_defs.py,
/// which writes the same numbers to padgear_spec.json: Tests/GearTests checks they match). In the wheel's own frame (turned as its
/// block: the foot spawns level), its top centre at the origin (the rod hub), m.
/// </summary>
public sealed class PadFootSpec
{
    /// <summary>The vanilla landing-gear model it shows and its scale (a placeholder reuse: no kitbash).</summary>
    public string Model; public float Scale = 1f;
    /// <summary>The box the wheel's body gets instead of the corner module's sphere (centre, half extents): the foot's bounds.</summary>
    public Vector3 BoxCenter, BoxHalf;
    /// <summary>The foot's height (m): its sole is this far below the hub.</summary>
    public float Height;
    /// <summary>The wheel body's mass (kg; its WheelColliderDefinition's - the definition is what counts), the weight one gear is sized
    /// for (kg) and the suspension's stroke (m, the housing's line of travel).</summary>
    public float WheelMass, RatedLoad, Stroke;
    /// <summary>Backup to the orientation lock: the axle held this hard (N m) - the vanilla brakes write it only when they change.</summary>
    public float AxleHoldTorque = 1e9f;
}

/// <summary>What the foot's lock must do this step (PadLockLife).</summary>
public enum PadLockDo { Nothing, Create, Destroy }

/// <summary>
/// THE FOOT'S ORIENTATION LOCK's life (pure; one per pad-foot gear). The lock is a 6-DOF constraint between the foot (the wheel body)
/// and the ship that fixes ONLY the rotation about the axle (the wheel constraint already holds the other two; every linear axis is
/// free: the suspension slides). The game saves no constraint a mod made and a wheel body takes its constraints with it, so:
///  - a wheel there and no lock: create (the build, a load, a new wheel the corner module made);
///  - the wheel changed (or went): the old lock went with the old body - forget it (never destroyed twice: the engine asserts), then
///    create on the new one;
///  - the engine destroyed it (a body removed under it): forget, create again;
///  - the block going: destroy it only while both bodies live.
/// Retraction keeps it: the foot stays level in the well.
/// </summary>
public sealed class PadLockLife
{
    public bool Has { get; private set; }
    public int Created { get; private set; }
    public int Lost { get; private set; }
    object _wheel;

    /// <summary>One step. wheel: the wheel body now (null: none); lockDead: the engine destroyed the lock. Create: make it, then
    /// <see cref="Made"/>.</summary>
    public PadLockDo Step(object wheel, bool lockDead)
    {
        if (Has && (!ReferenceEquals(wheel, _wheel) || lockDead)) { Has = false; _wheel = null; Lost++; }
        if (!Has && wheel != null) return PadLockDo.Create;
        return PadLockDo.Nothing;
    }

    /// <summary>The lock was made on this wheel (false: the engine refused - tried again next step).</summary>
    public void Made(object wheel, bool ok) { if (!ok) return; Has = true; _wheel = wheel; Created++; }

    /// <summary>The block is going: destroy the lock if both bodies still live.</summary>
    public PadLockDo Removing(bool bodiesAlive, bool lockDead)
    {
        bool has = Has; Has = false; _wheel = null;
        return has && bodiesAlive && !lockDead ? PadLockDo.Destroy : PadLockDo.Nothing;
    }

    /// <summary>The 6-DOF axes (LinearX..Z, AngularX..Z) the lock fixes: only the twist about its first axis, the axle.</summary>
    public static bool[] FixedAxes(int lockedAxis = 3)
    {
        var a = new bool[6];
        if (lockedAxis >= 3 && lockedAxis < 6) a[lockedAxis] = true;
        else if (lockedAxis == 6) a[3] = a[4] = a[5] = true;   // (6: every angular axis - the foot's whole orientation held to the ship)
        return a;
    }
}
