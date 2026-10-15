#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.VRage.Core;
using Keen.VRage.Core.Game.Systems;
using Keen.VRage.Library.Definitions;

namespace AeroMod;

/// <summary>
/// The grid's render parent for the flight effects (Fx/FlightField spawns under it): the game's render types are
/// outside what mod scripts compile against, so the type is late-bound - found once by its name (client).
/// </summary>
public static class EntryFxBridge
{
    static Type _renderCompType;
    static bool _resolved;
    public static string Problem = "";

    /// <summary>A type by its assembly's simple name and its full name (the game's client / render types are not mod
    /// references); null if that assembly is not loaded. The flight field uses it too.</summary>
    internal static Type FindType(string asm, string name)
    {
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == asm) return a.GetType(name);
        return null;
    }

    /// <summary>The grid's render parent (a client grid's GridRenderComponent, a RootEntityRenderComponent), or null.</summary>
    public static object RenderParent(Entity grid)
    {
        if (!_resolved)
        {
            _resolved = true;
            _renderCompType = FindType("VRage.Game.Client", "Keen.VRage.Game.Client.Render.RootEntityRenderComponent");
            if (_renderCompType == null) { Problem = "RootEntityRenderComponent not found"; Log.Default?.Info("[AERO] flight effects off: " + Problem); }
        }
        if (_renderCompType == null || grid == null) return null;
        // (component lookup is by exact type; a grid's is the derived GridRenderComponent: any instance)
        foreach (var comp in grid.Components)
            if (comp != null && _renderCompType.IsInstanceOfType(comp)) return comp;
        return null;
    }
}
