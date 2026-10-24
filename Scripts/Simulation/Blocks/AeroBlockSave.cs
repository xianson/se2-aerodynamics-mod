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
internal static partial class AeroBlockSave   // (the dev counters and requests: Dev/AeroBlockSaveDev.cs)
{
    // (fixed: saved with the block, so the same every load - never from the composition size, which other mods change;
    //  ...a5 is AeroWorldSettingsComponent, ...a6 the unmerged builder prototype)
    internal static readonly System.Guid SurfaceId = new("ae205e77-1a65-4c0d-9b1e-0000000000b1");
    internal static readonly System.Guid GearId = new("ae205e77-1a65-4c0d-9b1e-0000000000b2");
    internal static readonly System.Guid AirbrakeId = new("ae205e77-1a65-4c0d-9b1e-0000000000b3");
    internal static readonly System.Guid ThrustId = new("ae205e77-1a65-4c0d-9b1e-0000000000b4");
    internal static readonly System.Guid SolidSegmentId = new("ae205e77-1a65-4c0d-9b1e-0000000000b5");   // (2026-10-17: solid rocket segment, nozzle)
    internal static readonly System.Guid SolidNozzleId = new("ae205e77-1a65-4c0d-9b1e-0000000000b6");
    internal static readonly System.Guid BladesId = new("ae205e77-1a65-4c0d-9b1e-0000000000b7");   // (2026-10-17: rotor strikes - the blades' health)
    internal static readonly System.Guid AutopilotId = new("ae205e77-1a65-4c0d-9b1e-0000000000b8");   // (2026-10-19: the Autopilot block's panel)
    internal static readonly System.Guid RotorSpinId = new("ae205e77-1a65-4c0d-9b1e-0000000000b9");   // (2026-10-23: a main rotor's turn)
    internal static readonly System.Guid MortarChargeId = new("ae205e77-1a65-4c0d-9b1e-0000000000ba");   // (2026-10-23: a mortar charge spent,
    internal static readonly System.Guid MortarId = new("ae205e77-1a65-4c0d-9b1e-0000000000bb");         //  a breech fired)

    /// <summary>The build info a saving component is injected with (its fixed id, a fresh default builder).</summary>
    internal static Keen.VRage.DCS.Builders.EntityBuilder.ComponentBuildInfo Info(System.Type component, System.Guid id)
        => new() { Type = component, ComponentId = id, ObjectBuilder = new EntityNameSessionComponentObjectBuilder() };

    /// <summary>A fresh map with the one key (a fresh one each save: the builder must be a complete snapshot). The value is
    /// the block's GRID, never the block: only the key is read back, but the value is an entity reference the save must be
    /// able to resolve. SE2 4.0 checks that (EntityContainerSerializer.Serialize: "Serializing dead Entities") and a block
    /// is not an entity of its own in the saved bundle - it lives inside its grid's builder - so a block placed this
    /// session (not indexed by the load) failed every save of the world (2026-10-19, 1.0.3). The grid is a bundle root.</summary>
    internal static Dictionary<string, Entity> One(string key, Entity self)
    {
        var m = new Dictionary<string, Entity>();
        if (key != null && self != null) m[key] = GridOf(self);
        return m;
    }

    /// <summary>The grid a block is on (up its hierarchy to the entity with the grid's aero component); the block itself if
    /// none is found (then the save may refuse it, as before).</summary>
    internal static Entity GridOf(Entity block)
    {
        var e = block;
        for (int up = 0; e != null && up < 4; up++)
        {
            if (e.TryGet<AeroGridComponent>() != null) return e;
            e = e.TryGet<Keen.VRage.Core.Game.Components.HierarchyComponent>()?.Parent;
        }
        return block;
    }
}

public partial class AeroSurfaceSettingsComponent
{
    /// <summary>Took its settings from its own saved data (an old position-matched line leaves it alone).</summary>
    internal bool RestoredFromBlockData;

    [Keen.VRage.DCS.Annotations.Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Find(ob?.NamedEntities, AeroBlockSaveForm.SurfaceKey);
        if (!AeroBlockSaveForm.TryReadSurface(key, out bool p, out bool y, out bool r, out float max, out float trim)) return;
        Pitch = p; Yaw = y; Roll = r; MaxDeflection = max; Trim = trim;
        RestoredFromBlockData = true; AeroBlockSave.CountRestored(AeroBlockSave.KSurface, key);
    }

    // (every surface, changed or not: the key also says "saved in this form")
    [Keen.VRage.DCS.Annotations.Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Surface(Pitch, Yaw, Roll, MaxDeflection, Trim);
        ob.NamedEntities = AeroBlockSave.One(key, Entity); AeroBlockSave.CountWritten(AeroBlockSave.KSurface, key);
    }
}

public partial class AeroGearComponent
{
    /// <summary>Took down / up and Airshock from its own saved data (an old position-matched line leaves it alone).</summary>
    internal bool RestoredFromBlockData;

    [Keen.VRage.DCS.Annotations.Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Find(ob?.NamedEntities, AeroBlockSaveForm.GearKey);
        if (!AeroBlockSaveForm.TryReadGear(key, out bool down, out bool airshock)) return;
        Seq.Restore(down);   // (down: as built; up: its vanilla wheel slides up on its first steps, as the old line's restore)
        AirshockEnabled = airshock;   // (new 2026-10-15: never saved before - a gear from an old save starts on, the default)
        RestoredFromBlockData = true; AeroBlockSave.CountRestored(AeroBlockSave.KGear, key);
    }

    // (Seq.Down: where it is going - the motion is never saved mid-way, GearSequence.Restore)
    [Keen.VRage.DCS.Annotations.Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Gear(Seq.Down, AirshockEnabled);
        ob.NamedEntities = AeroBlockSave.One(key, Entity); AeroBlockSave.CountWritten(AeroBlockSave.KGear, key);
    }
}

public partial class AeroAirbrakeComponent
{
    /// <summary>Took its max opening from its own saved data (an old position-matched line leaves it alone).</summary>
    internal bool RestoredFromBlockData;

    [Keen.VRage.DCS.Annotations.Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Find(ob?.NamedEntities, AeroBlockSaveForm.AirbrakeKey);
        if (!AeroBlockSaveForm.TryReadAirbrake(key, out float max)) return;
        MaxOpening = max;
        RestoredFromBlockData = true; AeroBlockSave.CountRestored(AeroBlockSave.KAirbrake, key);
    }

    [Keen.VRage.DCS.Annotations.Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Airbrake(MaxOpening);
        ob.NamedEntities = AeroBlockSave.One(key, Entity); AeroBlockSave.CountWritten(AeroBlockSave.KAirbrake, key);
    }
}

public partial class AeroThrustSettingsComponent
{
    /// <summary>Took its fraction from its own saved data (an old position-matched line leaves it alone).</summary>
    internal bool RestoredFromBlockData;

    [Keen.VRage.DCS.Annotations.Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Find(ob?.NamedEntities, AeroBlockSaveForm.ThrustKey);
        if (!AeroBlockSaveForm.TryReadThrust(key, out float f, out float gim)) return;
        AttitudeFraction = f; GimbalDeg = gim;
        RestoredFromBlockData = true; AeroBlockSave.CountRestored(AeroBlockSave.KThrust, key);
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
        string key = f != DefaultAttitudeFraction || GimbalDeg > 0f ? AeroBlockSaveForm.Thrust(f, GimbalDeg) : null;
        ob.NamedEntities = AeroBlockSave.One(key, Entity); AeroBlockSave.CountWritten(AeroBlockSave.KThrust, key);
    }

    /// <summary>A changed client-scene copy of this block's fraction, else <paramref name="f"/> (save only).</summary>
    float ChangedClientCopy(float f)
    {
        if (Entity == null) return f;
        lock (All)
        {
            bool any = false;
            foreach (var th in All) if (!th.Server && th.Changed && th.HasSeenAt) { any = true; break; }   // (almost always none: no pose reads)
            if (!any) return f;
            var at = Entity.Data.GetWorldTransform().Position;
            // (the client copies' places as their own step last read them, SeenAt - never their transforms from here: this is the
            //  server copy's save, and a client copy read from it is the cross-scene read the game's DataGuard asserts on -
            //  AeroSurfaceSaved, 2026-10-15)
            foreach (var th in All)
                if (!th.Server && th.Changed && th.HasSeenAt && (th.SeenAt - at).Length() < AeroSurfaceSaved.ThrustReach)
                    return th.AttitudeFraction;
        }
        return f;
    }
}

public partial class AeroSolidSegmentComponent
{
    // (nothing of its own to save or read: the game saves the tank - an empty map, the builder still complete. The retired
    //  "aero-solid:1 fraction" key a few hours of 2026-10-17 builds wrote is no longer read: it was ignored anyway - the tank
    //  decides - and the next save writes this empty map over it)
    [Keen.VRage.DCS.Annotations.Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
    {
        ob.NamedEntities = AeroBlockSave.One(null, Entity);
    }
}

public partial class AeroSolidNozzleComponent
{
    /// <summary>Burning when saved: burning again at its power level (the game restores its switch and override with the block).</summary>
    [Keen.VRage.DCS.Annotations.Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Find(ob?.NamedEntities, AeroBlockSaveForm.SolidNozzleKey);
        if (!AeroBlockSaveForm.TryReadSolidNozzle(key, out bool lit, out float level, out bool gimbal, out float cone)) return;
        RestoredLit = lit; RestoredLevel = level; GimbalOn = gimbal; GimbalDeg = cone;
        AeroBlockSave.CountRestored(AeroBlockSave.KSolidNozzle, key);
    }

    // (only while it burns or its thrust vectoring is off the default; the client copy - injected with the same builder - writes nothing)
    [Keen.VRage.DCS.Annotations.Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = _server ? AeroBlockSaveForm.SolidNozzle(SolidRocket.SavedLit(Motor.Phase), Motor.Level, GimbalOn, GimbalDeg) : null;
        ob.NamedEntities = AeroBlockSave.One(key, Entity); AeroBlockSave.CountWritten(AeroBlockSave.KSolidNozzle, key);
    }
}

public partial class AeroRotorStrikeComponent
{
    /// <summary>The blades' health as saved (worn or broken); whole blades carry no key. The block's integrity is the game's: a worn
    /// rotor loads with its block still under full integrity, and welding it up repairs the blades (RotorStrike.Repaired).</summary>
    [Keen.VRage.DCS.Annotations.Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Find(ob?.NamedEntities, AeroBlockSaveForm.BladesKey);
        if (!AeroBlockSaveForm.TryReadBlades(key, out float h)) return;
        Health = h;
        // (per-blade breaks, 2026-10-23: each blade's state and wear when saved so; an older key's one health is mapped onto the
        //  blades once the rotor's row is known - AeroRotorStrikeComponent.InitBlades)
        CutsLoaded = AeroBlockSaveForm.TryReadBladeCuts(key, Cuts, Wear);
        AeroBlockSave.CountRestored(AeroBlockSave.KBlades, key);
    }

    [Keen.VRage.DCS.Annotations.Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = PerBladeOn ? AeroBlockSaveForm.Blades(Health, Cuts, Wear) : AeroBlockSaveForm.Blades(Health);
        ob.NamedEntities = AeroBlockSave.One(key, Entity); AeroBlockSave.CountWritten(AeroBlockSave.KBlades, key);
    }
}

public partial class AeroRotorSpinComponent
{
    /// <summary>Counter-rotating as saved; a clockwise rotor (the default) carries no key.</summary>
    [Keen.VRage.DCS.Annotations.Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Find(ob?.NamedEntities, AeroBlockSaveForm.RotorSpinKey);
        if (!AeroBlockSaveForm.TryReadRotorSpin(key, out bool counter)) return;
        CounterRotating = counter;
        AeroBlockSave.CountRestored(AeroBlockSave.KRotorSpin, key);
    }

    [Keen.VRage.DCS.Annotations.Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.RotorSpin(CounterRotating);
        ob.NamedEntities = AeroBlockSave.One(key, Entity); AeroBlockSave.CountWritten(AeroBlockSave.KRotorSpin, key);
    }
}

public partial class AeroAutopilotComponent
{
    /// <summary>Took its panel from its own saved data.</summary>
    internal bool RestoredFromBlockData;

    [Keen.VRage.DCS.Annotations.Init]
    private void InitBlockSave(EntityNameSessionComponentObjectBuilder ob)
    {
        string key = AeroBlockSaveForm.Find(ob?.NamedEntities, AeroBlockSaveForm.AutopilotKey);
        // (the Enemy switch's owners before, one per grid of the ship: kept until the block's first step maps them to its grids)
        if (ob?.NamedEntities != null)
            foreach (var kv in ob.NamedEntities)
                if (kv.Value != null && AeroBlockSaveForm.TryReadEnemyPrev(kv.Key, out long owner)) _enemyLoaded.Add((kv.Value, owner));
        if (!AeroBlockSaveForm.TryReadAutopilot(key, out var p)) return;
        SetPanel(in p);
        RestoredFromBlockData = true; AeroBlockSave.CountRestored(AeroBlockSave.KAutopilot, key);
    }

    [Keen.VRage.DCS.Annotations.Serializer]
    private void SaveBlock(EntityNameSessionComponentObjectBuilder ob)
    {
        var p = Panel;
        string key = AeroBlockSaveForm.Autopilot(in p);
        ob.NamedEntities = AeroBlockSave.One(key, Entity); AeroBlockSave.CountWritten(AeroBlockSave.KAutopilot, key);
        // (each living grid of an enemy ship with its owner before - a grid gone is not written: the save refuses a dead entity)
        int i = 0;
        foreach (var (grid, owner) in EnemyPrevForSave()) ob.NamedEntities[AeroBlockSaveForm.EnemyPrev(i++, owner)] = grid;
    }
}
