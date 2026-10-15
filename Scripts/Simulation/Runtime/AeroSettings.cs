#pragma warning disable
using System;
namespace AeroMod;

/// <summary>
/// THE AERO MOD'S SETTINGS: one list of named values over the mod's static tuning fields (no reflection: each setting
/// is a getter and a setter on its field), in two scopes:
///  - WORLD (server): the same for every player - the aero strength, entry heating, performance budgets. Saved WITH
///    THE WORLD (AeroWorldSettingsComponent on the server planets: the engine's EntityNameSessionComponentObjectBuilder,
///    the only builder a mod can use - see the Orbital Mod's SavedState). Optional host overrides in
///    %APPDATA%\SpaceEngineers2\ModSettings\AerodynamicsMod.world.cfg (uncommented lines win over the save).
///  - CLIENT (this PC): the visual effects. Saved in %APPDATA%\SpaceEngineers2\ModSettings\AerodynamicsMod.client.cfg.
/// Both files are polled (~2 s): edit them while playing. Every change takes effect at once - the fields are read each
/// tick or frame - and a setting marked Rebuild bumps AeroSwitch.Generation (every grid rebuilds from scratch).
/// Reached by other mods through AeroApi (GetSettings, TryGetSettingInfo, TryGetSetting, SetSetting,
/// GetSettingsVersion); documented in docs/API.md.
///
/// Multiplayer: SE2 2.4 runs its server in the same process as the client (its networking is a mock), so the world
/// values ARE the server's. A process that only ever saw a client scene (a future remote client) refuses world sets.
/// </summary>
public static partial class AeroSettings
{
    internal sealed class Setting
    {
        public string Name, Type, Scope, Doc;
        public double Default, Min, Max;
        public bool Rebuild;
        public Func<double> Get;
        public Action<double> Set;
        public Func<double, string> Check;   // (a cross-setting rule: why not, or null)
    }

    static readonly List<Setting> _all = new();
    static readonly Dictionary<string, Setting> _by = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new();
    static int _version;
    /// <summary>Bumped on every change (API, file, world load).</summary>
    public static int Version => _version;

    /// <summary>Seen in this process: a server scene (aero simulated here) / a client scene (effects drawn here).</summary>
    internal static volatile bool SawServer, SawClient;
    /// <summary>World settings can be set here: the server runs in this process (or nothing is known yet).</summary>
    public static bool WorldWritable => SawServer || !SawClient;

    static AeroSettings()
    {
        // ── WORLD ──
        B("Aero.Enabled", "world", false, () => AeroSwitch.Enabled, v => AeroSwitch.Enabled = v,
            "The whole aero simulation: forces, thrust offsets, block tracking. Off: the game's own flight; on again: every grid rebuilds.");
        F("Aero.DragMultiplier", "world", 0, 10, false, () => AeroTuning.DragMultiplier, v => AeroTuning.DragMultiplier = (float)v,
            "The aerodynamic force along the motion (drag), times this.");
        F("Aero.LiftMultiplier", "world", 0, 10, false, () => AeroTuning.LiftMultiplier, v => AeroTuning.LiftMultiplier = (float)v,
            "The aerodynamic force across the motion (lift: wings, control surfaces, the hull's own), times this.");
        B("Aero.ThrustTorque", "world", false, () => ThrustTorque.Enabled, v => ThrustTorque.Enabled = v,
            "Thrusters off the centre of mass turn the ship (and steer to the pilot's target). Off: the older phantom attitude hold.");
        I("Control.HingeSurfaces", "world", 0, 1, false, () => HingeSurfaces.Mode, v => HingeSurfaces.Mode = (int)v,
            "Vanilla hinges carrying flaps as control surfaces, driven from the pilot's input (fly-by-wire mixing). 0 none (a player's hinges are theirs), 1 every hinge on a piloted grid.");
        F("Control.HingeMaxDeflection", "world", 1, 60, false, () => HingeSurfaces.MaxDeflection, v => HingeSurfaces.MaxDeflection = (float)v,
            "Hinge control surfaces: degrees at full command (the hinge's own limits clamp too).");
        I("Model.LargeGridCellScale", "world", 1, 4, true, () => SnapshotGridAccessor.LargeGridCellScale, v => SnapshotGridAccessor.LargeGridCellScale = (int)v,
            "Large-grid surface resolution: 1 = 0.25 m cells (finest, slowest), 2 = 0.5 m, 4 = 1 m. Rebuilds every grid.");
        F("Entry.OnsetMach", "world", 0.5, 25, false, () => AeroEntryFx.OnsetMach, SetOnsetMach,
            "Entry heating (the plasma) starts at this Mach number.").Check = v => v + 0.05 >= FullMach() ? "must stay under Entry.FullMach" : null;
        F("Entry.FullMach", "world", 0.6, 30, false, FullMach, SetFullMach,
            "Entry heating is full at this Mach number.").Check = v => v <= AeroEntryFx.OnsetMach + 0.05 ? "must stay over Entry.OnsetMach" : null;
        F("Entry.HeatLag", "world", 0.05, 30, false, () => AeroEntryFx.HeatLag, v => AeroEntryFx.HeatLag = (float)v,
            "Seconds the hull takes to heat up and cool down (the glow lingers after slowing).");
        F("Perf.FrameBudgetMs", "world", 0.1, 16, false, () => AeroFrameBudget.BudgetMs, v => AeroFrameBudget.BudgetMs = v,
            "The most aero may take of a simulation frame (ms); past it, grids not piloted wait a few frames.");
        I("Perf.Workers", "world", 1, 16, false, () => AeroWork.Workers, v => AeroWork.Workers = (int)v,
            "Background cores aero may keep busy (hull and force-table builds).");
        B("Perf.TableCache", "world", false, () => AeroTableCache.Enabled, v => AeroTableCache.Enabled = v,
            "Keep built force tables for reuse (a grid seen before builds in milliseconds).");
        I("Perf.TableCacheMB", "world", 16, 4096, false, () => (int)(AeroTableCache.MaxBytes >> 20), v => AeroTableCache.MaxBytes = (long)v << 20,
            "The force-table cache's size (MB).");
        B("Perf.LocalUpdates", "world", false, () => AeroGridComponent.LocalUpdates, v => AeroGridComponent.LocalUpdates = v,
            "Update only the changed part of a hull when blocks change (off: rebuild the whole grid).");
        I("Perf.ChunksPerUpdate", "world", 1, 64, false, () => AeroGridComponent.ChunksPerUpdate, v => AeroGridComponent.ChunksPerUpdate = (int)v,
            "Hull chunks updated per pass after a block change.");
        F("Perf.MinSpeedForUpdates", "world", 0, 1000, false, () => AeroGridComponent.MinSpeedForUpdates, v => AeroGridComponent.MinSpeedForUpdates = (float)v,
            "Below this speed (m/s) a damaged or edited hull waits before updating its forces.");
        F("Perf.QuietBeforeFullRebuild", "world", 0.5, 120, false, () => AeroGridComponent.QuietBeforeFullRebuild, v => AeroGridComponent.QuietBeforeFullRebuild = v,
            "Seconds without block changes before a full rebuild cleans up after local updates.");
        B("Weather.Wind", "world", false, () => AeroWind.Enabled, v => AeroWind.Enabled = v,
            "The game's wind in the air the ship flies through (headwinds, crosswinds, gusts), in place of the game's own box-shaped wind push.");
        B("Structure.Loads", "world", false, () => StructureLoads.Enabled, v => StructureLoads.Enabled = v,
            "Work out the loads in every ship's structure (air, thrust, gyros, its own turning) and its hull's heating, a few times a second in the background. Damage is separate.");
        B("Structure.Damage", "world", false, () => StructureLoads.Damage, v => StructureLoads.Damage = v,
            "Overloaded joints of the ship a player flies wear, and break off what hangs from them (pull too many g, whip the ship round too hard). AI ships and wrecks never. On by default.");
        F("Structure.Strength", "world", 0.1, 100, false, () => ShipStructure.Strength, v => ShipStructure.Strength = (float)v,
            "Every block's strength, times this (7: an airliner of small blocks yields near 3 g and sheds parts past 4; a light large-block plane yields near 8 g and breaks past 12).");
        F("Structure.BreakAt", "world", 1.05, 10, false, () => StructureLoads.BreakUtil, v => StructureLoads.BreakUtil = (float)v,
            "Past its yield a joint wears through over a moment, faster the further over: at this many times its yield load it loses Structure.WearRate of its health a second. It lets go when its health runs out (or at once past Structure.SnapUtil).");
        F("Structure.WearRate", "world", 0, 100, false, () => StructureLoads.WearPerSecond, v => StructureLoads.WearPerSecond = (float)v,
            "Health a joint loses per second at Structure.BreakAt (0.45: a hard pull wears a joint through over a second or several; a brief spike only dents it).");
        F("Structure.SnapUtil", "world", 1.05, 100, false, () => StructureFatigue.SnapUtil, v => StructureFatigue.SnapUtil = (float)v,
            "A joint snaps at once at this many times its intact yield load (a genuine overload: a collision, a violent whip); below it, it wears through.");
        B("Structure.BySection", "world", false, () => StructureEval.BySection, v => StructureEval.BySection = v,
            "A ship's weak neck wears and fails across its whole section at once - every block across the overloaded cut together, and what hangs from it leaves in one go. Off: block by block, the most loaded first (a fracture creeps along the neck).");
        B("Structure.Fatigue", "world", false, () => StructureFatigue.Enabled, v => StructureFatigue.Enabled = v,
            "Joints tire under hard flying below their yield too: from Structure.EnduranceUtil of the yield up they lose health slowly, faster the harder they are loaded. Health lost is strength lost, so a tired ship fails at a lower load. Kept in the blocks' health (saved; welding repairs it). Cruising never tires a ship.");
        F("Structure.FatigueRate", "world", 0, 1, false, () => StructureFatigue.Rate, v => StructureFatigue.Rate = (float)v,
            "Health a joint loses per second from fatigue held at its yield (0.005: a tenth in 20 s at the yield, in ~40 s at 90%, ~5 min at 70%).");
        F("Structure.EnduranceUtil", "world", 0, 1, false, () => StructureFatigue.EnduranceUtil, v => StructureFatigue.EnduranceUtil = (float)v,
            "The endurance limit: joints loaded under this share of their yield never tire.");
        B("Structure.SplitBlocks", "world", false, () => AeroGridComponent.SplitBlocks, v => AeroGridComponent.SplitBlocks = v,
            "A joint that fails breaks its block's pieces on the failing side - the block stays, broken, on one side, and the ship parts along the failed face - instead of destroying the whole block (a block of one piece is still destroyed).");
        B("Structure.ThrustLoads", "world", false, () => StructureEval.ThrustLoads, v => StructureEval.ThrustLoads = v,
            "Engine thrust loads the structure (engine mounts, and the push through the ship). Off: engines never strain or break a ship.");
        B("Structure.EntryLoads", "world", false, () => StructureEval.EntryLoads, v => StructureEval.EntryLoads = v,
            "A re-entry flown on rails by the Orbital mod loads the structure with its braking (up to ~5 g), pressed onto the windward faces as the air would: the nose and heat shield carry it. Off: a railed entry heats the hull but never strains it.");
        B("Structure.Tear", "world", false, () => StructureTear.Enabled, v => StructureTear.Enabled = v,
            "A large piece the structure breaks off tears loose over a moment (held at the torn joint, sagging, then free) instead of vanishing off at once. Small bits still pop off.");
        F("Structure.TearSeconds", "world", 0.1, 10, false, () => StructureTear.TearSeconds, v => StructureTear.TearSeconds = (float)v,
            "Seconds a large broken-off piece takes to tear loose.");
        F("Structure.TearMinMass", "world", 0, 1e7, false, () => StructureTear.MinMass, v => StructureTear.MinMass = (float)v,
            "Pieces at least this heavy (kg) tear loose slowly; lighter ones pop off at once.");
        F("Structure.TearHold", "world", 0, 50, false, () => StructureTear.ForceFactor, v => StructureTear.ForceFactor = (float)v,
            "How hard a tearing piece is held at first, in g of its own weight (it eases to nothing over the tear).");
        B("Structure.Bends", "world", false, () => StructureBend.Enabled, v => StructureBend.Enabled = v,
            "An overloaded joint that holds stays bent: the armour outboard of it (a wing, a pod) sets a little the way the load pushed, more the further past its yield, more again after a harder pull. With Structure.Damage.");
        F("Structure.BendPerUtil", "world", 0, 1, false, () => StructureBend.PerUtil, v => StructureBend.PerUtil = (float)v,
            "The permanent turn (radians) at a joint per unit of load over its last peak (0.05: a 10 m wing's tip sets ~10 cm at 1.2 x its yield, ~25 cm just short of breaking).");
        F("Structure.BendMax", "world", 0, 1, false, () => StructureBend.Max, v => StructureBend.Max = (float)v,
            "The most any point of the armour is bent in total (m).");
        I("Structure.BendMaxBlocks", "world", 1, 5000, false, () => StructureBend.MaxBlocks, v => StructureBend.MaxBlocks = (int)v,
            "The most blocks one bend moves; a bigger part past the joint is the ship's body, not a tip, and is not bent.");
        B("Fx.TornSmoke", "client", false, () => TearSmoke.Enabled, v => TearSmoke.Enabled = v,
            "A joint the structure breaks smokes and sheds debris for a few seconds, on the ship and on the piece that tears off.");
        F("Fx.TornSmokeSeconds", "client", 0.5, 20, false, () => TearSmoke.Seconds, v => TearSmoke.Seconds = (float)v,
            "Seconds a torn joint smokes.");
        B("Heat.Damage", "world", false, () => StructureLoads.HeatDamage, v => StructureLoads.HeatDamage = v,
            "Hull blocks hotter than they can take (reentry, very fast low flight) lose health. Heat weakens the structure either way. On by default.");
        F("Heat.Multiplier", "world", 0, 10, false, () => HullHeat.HeatMultiplier, v => HullHeat.HeatMultiplier = (float)v,
            "The air's heating of the hull, times this.");
        B("Structure.WingStrain", "world", false, () => WingStructure.Enabled, v => WingStructure.Enabled = v,
            "Overloaded wings (too many g, too fast) strain at the root, wear, and break off. Off by default: it destroys blocks.");
        F("Structure.WingStrength", "world", 0.1, 100, false, () => WingStructure.Strength, v => WingStructure.Strength = (float)v,
            "Wing strength, times this (1: a light-armour wing 4 blocks long snaps at 40 kPa wing loading; heavy armour 4x).");

        // ── CLIENT ──
        B("Hud.StallWarning", "client", false, () => StallWarning.Enabled, v => StallWarning.Enabled = v,
            "The stall warning, from the whole ship's air force (any shape): STALL when pulling further only loses lift, amber when close to it.");
        B("Hud.Loac", "client", false, () => LoacWarning.Enabled, v => LoacWarning.Enabled = v,
            "The loss-of-control warning, predicted: CONTROL LIMIT / LOSS OF CONTROL when holding the attitude you command needs nearly / more than the torque the gyros, thrusters and control surfaces have at this speed (slow, the surfaces lose to the air).");
        B("Hud.GameStallWarning", "client", false, () => GameStallWarning.On, v => GameStallWarning.On = v,
            "The game's own stall warning (the big red text when the thrusters cannot hold the ship against gravity - it ignores the wings' lift). Off by default: removed.");
        B("Hud.Mach", "client", false, () => MachHud.On, v => MachHud.On = v,
            "Mach in the game's speed readout (\"312 m/s · M 0.92\"), for what you are flying. Off by default: the flight boxes by ALT show it.");
        // (the airframe you can feel - StructureFeel.cs, 2026-10-14: the structure's load, heard and felt in the seat)
        B("Feel.Creaks", "client", false, () => StructureFeel.Creaks, v => StructureFeel.Creaks = v,
            "The airframe creaks and groans as the structure is loaded (from 60% of its strength, more and louder toward 100%), and cracks when a block wears or breaks. The ship you are seated in.");
        F("Feel.CreakVolume", "client", 0, 2, false, () => StructureFeel.Volume, v => StructureFeel.Volume = (float)v,
            "The creaks' and cracks' volume, times this.");
        B("Feel.Shake", "client", false, () => StructureFeel.Shake, v => StructureFeel.Shake = v,
            "The cockpit buzzes as the structure is loaded (from 50% of its strength, noticeable near 100%), with a jolt when a block breaks. The game's own camera shake settings apply.");
        F("Feel.ShakeScale", "client", 0, 5, false, () => StructureFeel.ShakeScale, v => StructureFeel.ShakeScale = (float)v,
            "The load shake's strength, times this.");
        B("Fx.Effects", "client", false, () => AeroEntryFx.AllEffects, v => AeroEntryFx.AllEffects = v,
            "Every aero visual effect (plasma, vapour, tip trails, the plasma light). Off: none at all.");
        B("Fx.FlightField", "client", false, () => FlightField.On, v => FlightField.On = v,
            "The full flight effects (off: the older single plasma at the nose, no vapour or trails).");
        B("Fx.Plasma", "client", false, () => FlightField.PlasmaOn, v => FlightField.PlasmaOn = v, "Entry plasma sparks over the hull.");
        B("Fx.Vapour", "client", false, () => FlightField.VapourOn, v => FlightField.VapourOn = v, "Transonic vapour along the hull's edges.");
        B("Fx.Tips", "client", false, () => FlightField.TipsOn, v => FlightField.TipsOn = v, "Wing-tip vapour trails.");
        B("Fx.SpotLight", "client", false, () => FlightField.SpotOn, v => FlightField.SpotOn = v, "The plasma's light on the hull (one shadowed spot light per glowing grid).");
        I("Fx.MaxGrids", "client", 1, 32, false, () => FlightField.FlightMax, v => FlightField.FlightMax = (int)v,
            "Grids with flight effects at once (the nearest to the camera).");
        F("Fx.Range", "client", 100, 50000, false, () => FlightField.FlightRange, v => FlightField.FlightRange = (float)v,
            "Distance (m) past which a grid shows no flight effects.");
        I("Fx.SpawnPerTick", "client", 1, 200, false, () => FlightField.SpawnPerTick, v => FlightField.SpawnPerTick = (int)v,
            "Effects started per frame while a grid's effects build up (higher: faster, more hitching).");
    }

    // ── the entry heating's Mach pair (stored as heat: OnsetHeat (M / OnsetMach)^3; FullMach is where it is full) ──
    static double FullMach() => AeroEntryFx.OnsetMach * Math.Cbrt(AeroEntryFx.FullHeat / AeroEntryFx.OnsetHeat);
    static void SetFullMach(double m) => AeroEntryFx.FullHeat = (float)(AeroEntryFx.OnsetHeat * Math.Pow(m / AeroEntryFx.OnsetMach, 3));
    static void SetOnsetMach(double m) { double full = FullMach(); AeroEntryFx.OnsetMach = (float)m; SetFullMach(full); }   // (the full point stays put)

    static Setting Add(string name, string type, string scope, double min, double max, bool rebuild, Func<double> get, Action<double> set, string doc)
    {
        var s = new Setting { Name = name, Type = type, Scope = scope, Min = min, Max = max, Rebuild = rebuild, Get = get, Set = set, Doc = doc };
        s.Default = get();   // (the field's own initial value)
        _all.Add(s); _by[name] = s;
        return s;
    }
    static Setting B(string n, string scope, bool rebuild, Func<bool> get, Action<bool> set, string doc) => Add(n, "bool", scope, 0, 1, rebuild, () => get() ? 1 : 0, v => set(v != 0), doc);
    static Setting I(string n, string scope, double min, double max, bool rebuild, Func<int> get, Action<double> set, string doc) => Add(n, "int", scope, min, max, rebuild, () => get(), set, doc);
    static Setting F(string n, string scope, double min, double max, bool rebuild, Func<double> get, Action<double> set, string doc) => Add(n, "float", scope, min, max, rebuild, get, set, doc);
    static Setting F(string n, string scope, double min, double max, bool rebuild, Func<float> get, Action<double> set, string doc) => Add(n, "float", scope, min, max, rebuild, () => get(), set, doc);
}
