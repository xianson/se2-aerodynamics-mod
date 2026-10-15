#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;

namespace AeroMod;

/// <summary>
/// Per-thruster attitude authority setting. Injected onto thruster entities
/// by EntityInjector. Stores the fraction of thrust capacity reserved for
/// attitude control (0..1, default 0.5 = 50/50 split).
///
/// Value is read by OffsetThrustJob each physics frame.
/// Value is set via the terminal slider (AeroThrustBlockDetailModel).
/// Saved with the world (AeroSurfaceSaved: "thrust x y z fraction", only those off the default; 2026-10-15, settings
/// DESIGN.md W5(d) - it was lost on every load).
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

    /// <summary>Every thruster's settings in the scene (the save walks them).</summary>
    internal static readonly List<AeroThrustSettingsComponent> All = new();

    void IInSceneListener.OnAddedToScene()
    {
        // (BlockDetailProviderIndexer discovers [BlockDetailProvider] types in mod assemblies on its own since 2.4.0.77, so
        //  AeroThrustBlockDetailModel self-registers: nothing to do for it)
        try { Server = !string.Equals(Data.Scene?.DebugName, "Client", StringComparison.Ordinal); } catch { Server = true; }
        lock (All) All.Add(this);
    }

    void IInSceneListener.OnBeforeRemovedFromScene() { lock (All) All.Remove(this); }

    /// <summary>One line of the save: "x y z fraction" (world position of the block, its attitude fraction).</summary>
    internal string SaveLine()
    {
        var p = Entity.Data.GetWorldTransform().Position;
        var c = System.Globalization.CultureInfo.InvariantCulture;
        return string.Join(" ", p.X.ToString("R", c), p.Y.ToString("R", c), p.Z.ToString("R", c), AttitudeFraction.ToString("R", c));
    }
}
