#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
using System;
using Keen.Game2.Simulation.StreamedUI.Terminal.ControlPanel.BlockDetails;
using Keen.VRage.Library.UI.PropertyInspector;
using Keen.VRage.Library.Serialization.Validation;

namespace AeroMod;

// ════════════════════════════════════════════════════════════════════
// TODO [MULTIPLAYER]
//
// [Replicate] and [SyncFromClients] from Keen.VRage.Multiplayer.Annotations
// are not in the mod script whitelist. Without them:
//   - Slider works for host / single-player only.
//   - Value won't replicate to clients in MP.
// ════════════════════════════════════════════════════════════════════

/// <summary>
/// Terminal slider for per-thruster attitude authority.
/// </summary>
// A [BlockDetailProvider] again (2026-10-12): it crashed the game when a thruster was selected in the control panel
// (KeyNotFoundException 'AeroMod.AeroThrustBlockDetailModel', 2026-10-04) - the panel translates every detail model through
// the replication mapping, which a mod's model is never in. DetailModelPairing pairs it with itself in the ctor (see there).
// The attribute stays OFF until one in-game check (open a thruster's control panel) passes: the user plays this build.
// [BlockDetailProvider]
// SE2 4.0 runs the serializer generators on mod scripts: a partial BlockDetailModel gets a generated named-object
// serializer whose code needs System.ObjectModel (INotifyPropertyChanged), which mod compiles do not reference (CS0012).
// [NoSerialize] turns those generators off for the model - mod models were never serialized (DetailModelPairing).
[Keen.VRage.Library.Serialization.NoSerialize]
public partial class AeroThrustBlockDetailModel : BlockDetailModel
{
    [Keen.VRage.DCS.Annotations.Component]
    private AeroThrustSettingsComponent _settings;

    public AeroThrustBlockDetailModel()
    {
        // (runs inside the game's Allocate, before RegisterObservable: the pairing must exist by then)
        DetailModelPairing.Pair(this, _settings);
        if (AeroLog.Detail) Log.Default?.Info($"[AERO] AeroThrustBlockDetailModel CTOR settings={_settings != null} {DetailModelPairing.Status}");
    }

    [Slider(null)]
    [Range<float>(0f, 1f, RangeMode.InIn)]
    [Label("Attitude Authority")]
    [Increment(0.01f, 0.05f, 0.1f)]
    public float AttitudeFraction
    {
        get => _settings?.AttitudeFraction ?? 0.5f;
        set
        {
            if (_settings != null)
                _settings.AttitudeFraction = Math.Clamp(value, 0f, 1f);
        }
    }
}

// ────────────────────────────────────────────────────────────────────
// The manual BlockDetailProviderIndexer registration that used to live here is GONE.
//
// It existed because "the core game indexer runs AfterInit() before mod assemblies are loaded,
// so our [BlockDetailProvider] is never discovered", and it rebuilt BlockDetailViewModelInfo
// by reflection: Activator for the info object, a hand-made SubclassOf<BlockDetailModel>, a
// ValueTuple of (FieldInfo, DependencyKind), ImmutableArray.Create by MethodInfo, then a
// dictionary Add. Roughly 170 lines and 17 reflection sites.
//
// On 2.4.0.77 that premise is false. The indexer DOES see mod assemblies -- proven at runtime,
// where our own registration failed with
//     An item with the same key has already been added. Key: AeroMod.AeroThrustBlockDetailModel
// because the game had already registered the type.
//
// BlockDetailProviderIndexer.AfterInit scans [BlockDetailProvider] types, requires a public
// default ctor, and builds Dependencies itself from fields marked [Component], [Definition] or
// [Configuration] -- exactly what the manual code was assembling by hand. The attributes on
// AeroThrustBlockDetailModel above are all that is needed.
// ────────────────────────────────────────────────────────────────────
