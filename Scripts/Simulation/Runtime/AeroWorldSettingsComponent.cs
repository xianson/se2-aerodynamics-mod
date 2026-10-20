#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using Keen.Game2.Simulation.GameSystems;
namespace AeroMod;

/// <summary>
/// Carries the world's aero settings in the save: injected into server planet compositions (EntityInjector), with the
/// engine's EntityNameSessionComponentObjectBuilder (a string -&gt; Entity map; a mod cannot define its own builder).
/// Key "aero-settings:&lt;text&gt;" -&gt; the planet itself (a value must exist). Every carrier writes the same text.
/// Since 2026-10-19 the players' server bodies carry it too (AeroWorldBodyComponent: a world with no planet saved nothing);
/// which text is applied, and when the world is over, is Save/WorldCarriers.cs (AeroSettings.Carriers): a body's text wins
/// over a planet's, a planet's is still read (older saves).
/// </summary>
public partial class AeroWorldSettingsComponent : Component, IInSceneListener
{
    [Keen.VRage.DCS.Annotations.Init]
    private void InitSettings(EntityNameSessionComponentObjectBuilder ob)
        => WorldCarrier.Read(ob, WorldCarriers.Kind.Planet, Entity);

    [Keen.VRage.DCS.Annotations.Serializer]
    private void SerializeSettings(EntityNameSessionComponentObjectBuilder ob)
        // (the blocks' state is saved with each block since 2026-10-15 - AeroBlockSave; no more position-matched lines here)
        => WorldCarrier.Write(ob, Entity, Entity);

    void IInSceneListener.OnAddedToScene()
    {
        WorldCarrier.Added(Entity);
        lock (Planets) Planets.Add(this);   // (quick launch's planet centres: Launch/QuickLaunch.Server.cs)
    }
    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        lock (Planets) Planets.Remove(this);
        WorldCarrier.Removed(Entity);
    }
}

/// <summary>
/// THE PLAYERS' BODIES CARRY THE WORLD'S SETTINGS (2026-10-19): injected into the SERVER character and spectator
/// compositions (EntityInjector: CharacterMovementServerComponent / ServerSpectatorComponent - in the server composites only), with a fixed component id and the
/// engine's builder, as the planets' carrier. A planetless save (the game's Empty World) holds only these: one spectator
/// and one character per player, both bundle roots. The key's value is the body's root entity (a seated character's grid -
/// the 4.0 save refuses a reference to an entity it does not hold as its own: AeroBlockSave.One).
/// Removed mid-session (a death, a respawn, a player leaving) it is not the world's end: WorldCarriers.
/// </summary>
public partial class AeroWorldBodyComponent : Component, IInSceneListener
{
    [Keen.VRage.DCS.Annotations.Init]
    private void InitSettings(EntityNameSessionComponentObjectBuilder ob)
        => WorldCarrier.Read(ob, WorldCarriers.Kind.Body, Entity);

    [Keen.VRage.DCS.Annotations.Serializer]
    private void SerializeSettings(EntityNameSessionComponentObjectBuilder ob)
        => WorldCarrier.Write(ob, Entity, WorldCarrier.RootOf(Entity));

    void IInSceneListener.OnAddedToScene() => WorldCarrier.Added(Entity);
    void IInSceneListener.OnBeforeRemovedFromScene() => WorldCarrier.Removed(Entity);
}

/// <summary>The carriers' engine side (both components): the builder's key in and out, the session, the hooks.</summary>
internal static class WorldCarrier
{
    /// <summary>Fixed component ids (saved with the entity: the same every load). ...a5: the planets' (since 2026-10-03).</summary>
    internal static readonly System.Guid PlanetId = new("ae205e77-1a65-4c0d-9b1e-0000000000a5");
    internal static readonly System.Guid BodyId = new("ae205e77-1a65-4c0d-9b1e-0000000000a7");

    /// <summary>The world's end beyond the settings: the API's structure events let go of its entities (2026-10-15 review),
    /// quick launch's unanswered ask, answer and cockpit turn (2026-10-18), the autopilot's toasts.</summary>
    static void Hooks() { AeroApi.ResetStructureEvents(); QuickLaunch.ResetWorld(); ApToasts.ResetWorld(); EnemySpawn.ResetWorld(); }

    internal static void Read(EntityNameSessionComponentObjectBuilder ob, WorldCarriers.Kind kind, Entity self)
    {
        AeroSettings.WorldHooks ??= Hooks;
        string text = AeroSettings.SavedText(ob?.NamedEntities?.Keys);
        if (text.Length > 0) AeroSettings.Carriers.Read(text, kind, SessionOf(self));
    }

    /// <summary>A fresh map: the world's text (off-default values only) -&gt; `value`, an entity the save holds.</summary>
    internal static void Write(EntityNameSessionComponentObjectBuilder ob, Entity self, Entity value)
    {
        ob.NamedEntities = new Dictionary<string, Entity>();
        string text = AeroSettings.Text("world", all: false);
        if (text.Length > 0 && self != null && value != null) ob.NamedEntities[AeroSettings.SaveKey + text] = value;
        AeroSettings.WorldSaved();
    }

    internal static void Added(Entity self)
    {
        AeroSettings.WorldHooks ??= Hooks;
        AeroSettings.CarrierAdded(SessionOf(self));
    }

    internal static void Removed(Entity self)
    {
        var s = SessionOf(self);
        AeroSettings.CarrierRemoved(s, s == null || s.IsUnloading);
    }

    /// <summary>The entity's session; null while it does not know its scene (a carrier's [Init] may run before).</summary>
    internal static Session SessionOf(Entity e)
    {
        try { return e?.Scene?.UserObject as Session; }
        catch { return null; }   // (not a hot path: once per carrier at load and at its scene's changes)
    }

    /// <summary>The top of the entity's hierarchy (a bundle root of the save: a seated character's grid); itself if none.</summary>
    internal static Entity RootOf(Entity e)
    {
        var top = e;
        for (int up = 0; top != null && up < 8; up++)
        {
            var parent = top.TryGet<Keen.VRage.Core.Game.Components.HierarchyComponent>()?.Parent;
            if (parent == null) break;
            top = parent;
        }
        return top ?? e;
    }
}
