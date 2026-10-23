#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using System.Threading;

namespace AeroMod;

/// <summary>
/// THE WORLD'S GRAVITY MULTIPLIER (2026-10-23, the user: "have grav default to 1" - "mods settings work, but don't see any grav
/// mult"): the world setting World.GravityMultiplier (AeroSettings; default 1, vanilla's definition 2 - the mod's
/// PhysicsSessionConfiguration override also says 1) set on each physics scene's IPhysics.GravityMultiplier (public, settable;
/// Havok forwards it to its native session) - the engine scales the gravity it applies to every rigid body by it (characters
/// have their own). Tick is called per session from the server's jobs (the planets' carrier job, every aero grid's server
/// step) and the client frame: a reference and an int compare while nothing changed; applied once per scene and session and
/// again on a change of the setting. GravityScale (GridPhysics) is the value applied, so the mod's weights follow it.
/// </summary>
public static partial class WorldGravity
{
    /// <summary>The setting (0..5; 0 = no gravity on bodies).</summary>
    public static float Multiplier { get => _mult; set { _mult = Clamp(value); Interlocked.Increment(ref _version); } }
    static float _mult = 1f;
    static int _version;
    public const float Min = 0f, Max = 5f;
    public static float Clamp(float v) => float.IsFinite(v) ? Math.Clamp(v, Min, Max) : 1f;

    /// <summary>The value last applied to the server scene (NaN: none yet this session) - GridPhysics.GravityScale.</summary>
    public static float AppliedServer = float.NaN;
    /// <summary>Health (harness: aeroget WorldGravity.Status): what was applied where, or why not.</summary>
    public static string Status = "not applied";

    /// <summary>Does this scene need the setting (again)? The pure part of Tick (tests).</summary>
    public static bool Due(object slotSession, int slotVersion, object session, int version)
        => !ReferenceEquals(slotSession, session) || slotVersion != version;
}
