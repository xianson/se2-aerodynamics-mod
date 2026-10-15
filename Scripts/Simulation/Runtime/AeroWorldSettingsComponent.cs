#pragma warning disable
using Keen.Game2.Simulation.GameSystems;
namespace AeroMod;

/// <summary>
/// Carries the world's aero settings in the save: injected into server planet compositions (EntityInjector), with the
/// engine's EntityNameSessionComponentObjectBuilder (a string -&gt; Entity map; a mod cannot define its own builder).
/// Key "aero-settings:&lt;text&gt;" -&gt; the planet itself (a value must exist). Every planet writes the same text; the
/// first non-empty one read on load wins.
/// </summary>
public partial class AeroWorldSettingsComponent : Component, IInSceneListener
{
    [Keen.VRage.DCS.Annotations.Init]
    private void InitSettings(EntityNameSessionComponentObjectBuilder ob)
    {
        if (ob?.NamedEntities == null) return;
        foreach (var kv in ob.NamedEntities)
            if (kv.Key.StartsWith(AeroSettings.SaveKey)) { AeroSettings.WorldLoaded(kv.Key.Substring(AeroSettings.SaveKey.Length)); break; }
    }

    [Keen.VRage.DCS.Annotations.Serializer]
    private void SerializeSettings(EntityNameSessionComponentObjectBuilder ob)
    {
        ob.NamedEntities = new Dictionary<string, Keen.VRage.DCS.Components.Entity>();
        string text = AeroSettings.Text("world", all: false) + AeroSurfaceSaved.Text();
        if (text.Length > 0 && Entity != null) ob.NamedEntities[AeroSettings.SaveKey + text] = Entity;
        AeroSettings.WorldSaved();
    }

    void IInSceneListener.OnAddedToScene() { System.Threading.Interlocked.Increment(ref _carriers); AeroSettings.CarrierAdded(); }
    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        AeroSettings.CarrierRemoved();
        // (the last carrier gone - the world unloaded: the API's structure events let go of its entities, 2026-10-15 review)
        if (System.Threading.Interlocked.Decrement(ref _carriers) <= 0) AeroApi.ResetStructureEvents();
    }
    static int _carriers;
}
