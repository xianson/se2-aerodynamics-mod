#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using Keen.Game2.Simulation.GameSystems;

namespace AeroMod;

/// <summary>
/// Per-block saved data (2026-10-15): each aero block component keeps its own state in the block's own saved entity, on
/// the ENGINE's EntityNameSessionComponentObjectBuilder (one key, AeroBlockSaveForm; the value is the block itself, as
/// the world-settings carrier does - a null value fails the engine's validation). Before this the state was text lines
/// on a planet's carrier matched back to blocks by world position (AeroSurfaceSaved): lost if a grid moved between save
/// and load, and never carried by a blueprint, a copy-paste or a split.
///
/// Why the engine's builder and not a mod-defined one (scratchpad ob/ feasibility, 2026-10-15): a world file is a binary
/// archive whose type table names every builder type; with the mod removed, a mod type is missing and the binary reader
/// throws ("not forwards compatible") - the world no longer loads. The engine's type is always there: with the mod
/// removed, the composite migration drops the thruster's slot (EOBUpdateContext: Removes) and the mod's own blocks are
/// pruned with their definitions, as before.
///
/// Each component is injected with a FIXED component id and this builder (InjectAeroComponents; DCS: the builder's type
/// must be the [Init] / [Serializer] parameter's). Old saves have no slot for the id: the composite migration inserts the
/// prefab default (empty map) and the block takes its value from the old lines, as today (AeroSurfaceSaved, read for one
/// release); the first save then writes the block data and no lines. A block that got its value here
/// (<c>RestoredFromBlockData</c>) is never claimed by an old line.
///
/// Server composition is what is saved. Surfaces, gears and airbrakes exist on the server only (their terminals hand the
/// panel the server object: DetailModelPairing). Thrusters exist in both scenes: a client copy whose own builder brought
/// no value takes its server copy's (AeroSurfaceSaved.Follow: by position, as the old lines did); at save, a server copy
/// still at the default writes a changed client copy's value (the old lines' order: server wins where both changed).
/// </summary>
internal static class AeroBlockSave
{
    // (fixed: saved with the block, so the same every load - never from the composition size, which other mods change;
    //  ...a5 is AeroWorldSettingsComponent, ...a6 the unmerged builder prototype)
    internal static readonly System.Guid SurfaceId = new("ae205e77-1a65-4c0d-9b1e-0000000000b1");
    internal static readonly System.Guid GearId = new("ae205e77-1a65-4c0d-9b1e-0000000000b2");
    internal static readonly System.Guid AirbrakeId = new("ae205e77-1a65-4c0d-9b1e-0000000000b3");
    internal static readonly System.Guid ThrustId = new("ae205e77-1a65-4c0d-9b1e-0000000000b4");

    /// <summary>The build info a saving component is injected with (its fixed id, a fresh default builder).</summary>
    internal static Keen.VRage.DCS.Builders.EntityBuilder.ComponentBuildInfo Info(System.Type component, System.Guid id)
        => new() { Type = component, ComponentId = id, ObjectBuilder = new EntityNameSessionComponentObjectBuilder() };

    /// <summary>A fresh map with the one key (a fresh one each save: the builder must be a complete snapshot).</summary>
    internal static Dictionary<string, Entity> One(string key, Entity self)
    {
        var m = new Dictionary<string, Entity>();
        if (key != null && self != null) m[key] = self;
        return m;
    }
}

public partial class AeroSurfaceSettingsComponent
{
    /// <summary>Took its settings from its own saved data (an old position-matched line leaves it alone).</summary>
    internal bool RestoredFromBlockData;

    [Keen.VRage.DCS.Annotations.Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        if (!AeroBlockSaveForm.TryReadSurface(AeroBlockSaveForm.Find(ob?.NamedEntities, AeroBlockSaveForm.SurfaceKey), out bool p, out bool y, out bool r, out float max, out float trim)) return;
        Pitch = p; Yaw = y; Roll = r; MaxDeflection = max; Trim = trim;
        RestoredFromBlockData = true;
    }

    // (every surface, changed or not: the key also says "saved in this form")
    [Keen.VRage.DCS.Annotations.Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
        => ob.NamedEntities = AeroBlockSave.One(AeroBlockSaveForm.Surface(Pitch, Yaw, Roll, MaxDeflection, Trim), Entity);
}

public partial class AeroGearComponent
{
    /// <summary>Took down / up and Airshock from its own saved data (an old position-matched line leaves it alone).</summary>
    internal bool RestoredFromBlockData;

    [Keen.VRage.DCS.Annotations.Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        if (!AeroBlockSaveForm.TryReadGear(AeroBlockSaveForm.Find(ob?.NamedEntities, AeroBlockSaveForm.GearKey), out bool down, out bool airshock)) return;
        Seq.Restore(down);   // (down: as built; up: its vanilla wheel slides up on its first steps, as the old line's restore)
        AirshockEnabled = airshock;   // (new 2026-10-15: never saved before - a gear from an old save starts on, the default)
        RestoredFromBlockData = true;
    }

    // (Seq.Down: where it is going - the motion is never saved mid-way, GearSequence.Restore)
    [Keen.VRage.DCS.Annotations.Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
        => ob.NamedEntities = AeroBlockSave.One(AeroBlockSaveForm.Gear(Seq.Down, AirshockEnabled), Entity);
}

public partial class AeroAirbrakeComponent
{
    /// <summary>Took its max opening from its own saved data (an old position-matched line leaves it alone).</summary>
    internal bool RestoredFromBlockData;

    [Keen.VRage.DCS.Annotations.Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        if (!AeroBlockSaveForm.TryReadAirbrake(AeroBlockSaveForm.Find(ob?.NamedEntities, AeroBlockSaveForm.AirbrakeKey), out float max)) return;
        MaxOpening = max;
        RestoredFromBlockData = true;
    }

    [Keen.VRage.DCS.Annotations.Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
        => ob.NamedEntities = AeroBlockSave.One(AeroBlockSaveForm.Airbrake(MaxOpening), Entity);
}

public partial class AeroThrustSettingsComponent
{
    /// <summary>Took its fraction from its own saved data (an old position-matched line leaves it alone).</summary>
    internal bool RestoredFromBlockData;

    [Keen.VRage.DCS.Annotations.Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        if (!AeroBlockSaveForm.TryReadThrust(AeroBlockSaveForm.Find(ob?.NamedEntities, AeroBlockSaveForm.ThrustKey), out float f)) return;
        AttitudeFraction = f;
        RestoredFromBlockData = true;
        // (the client scene's copy of this block: given the value by position once both are in their scenes, unless its
        //  own builder brought it - AeroSurfaceSaved.ApplyPending drops the entry for a client-scene source)
        if (Changed) AeroSurfaceSaved.Follow(this);
    }

    // (only off the default, as the old lines: most thrusters never carry a key. Thousands of thrusters: no per-block
    //  string unless changed.)
    [Keen.VRage.DCS.Annotations.Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
    {
        float f = AttitudeFraction;
        if (!Changed && Server) f = ChangedClientCopy(f);
        ob.NamedEntities = AeroBlockSave.One(f != DefaultAttitudeFraction ? AeroBlockSaveForm.Thrust(f) : null, Entity);
    }

    /// <summary>A changed client-scene copy of this block's fraction, else <paramref name="f"/> (save only).</summary>
    float ChangedClientCopy(float f)
    {
        if (Entity == null) return f;
        lock (All)
        {
            bool any = false;
            foreach (var th in All) if (!th.Server && th.Changed) { any = true; break; }   // (almost always none: no pose reads)
            if (!any) return f;
            var at = Entity.Data.GetWorldTransform().Position;
            foreach (var th in All)
                if (!th.Server && th.Changed && th.Entity != null && (th.Entity.Data.GetWorldTransform().Position - at).Length() < AeroSurfaceSaved.ThrustReach)
                    return th.AttitudeFraction;
        }
        return f;
    }
}
