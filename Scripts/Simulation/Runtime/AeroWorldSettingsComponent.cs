#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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
        // (the blocks' state is saved with each block since 2026-10-15 - AeroBlockSave; no more position-matched lines here)
        string text = AeroSettings.Text("world", all: false);
        if (text.Length > 0 && Entity != null) ob.NamedEntities[AeroSettings.SaveKey + text] = Entity;
        AeroSettings.WorldSaved();
    }

    void IInSceneListener.OnAddedToScene()
    {
        System.Threading.Interlocked.Increment(ref _carriers); AeroSettings.CarrierAdded();
        lock (Planets) Planets.Add(this);   // (quick launch's planet centres: Launch/QuickLaunch.Server.cs)
    }
    void IInSceneListener.OnBeforeRemovedFromScene()
    {
        AeroSettings.CarrierRemoved();
        lock (Planets) Planets.Remove(this);
        // (the last carrier gone - the world unloaded: the API's structure events let go of its entities, 2026-10-15 review;
        //  quick launch's unanswered ask, answer and cockpit turn of that world, 2026-10-18)
        if (System.Threading.Interlocked.Decrement(ref _carriers) <= 0) { AeroApi.ResetStructureEvents(); QuickLaunch.ResetWorld(); }
    }
    static int _carriers;
}
