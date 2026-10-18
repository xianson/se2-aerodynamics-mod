#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
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
/// Reached by other mods through AeroApi (GetSettings, TryGetSettingInfo, TryGetSettingUi, TryGetSetting, SetSetting,
/// GetSettingsVersion, IsSettingForced, CanEditWorldSettings); documented in docs/API.md.
///
/// THE UI METADATA (2026-10-15, settings DESIGN.md W1): each setting also carries what a generated settings screen needs -
/// a player-facing Label, its Group (the section it shows in; registration order is the screen's order: Order), a Tier
/// (Basic: always shown; Advanced: a collapsed section; Hidden: the files and the API only), a Unit and Step for numbers,
/// Choices (labels for an int's Min..Max values) and ClientVisible (a world value clients draw from too - nothing
/// replicates it yet: SE2 2.4's server shares the process). The Orbital Mod's OrbitalSettings.Setting has the same
/// fields with the same names and types. Never registered: Dev* / Test* / Debug* / Probe* / Trace* / *Log fields
/// (harness and test switches - Tests/SettingsTests scans for them).
///
/// Multiplayer: SE2 2.4 runs its server in the same process as the client (its networking is a mock), so the world
/// values ARE the server's. A process that only ever saw a client scene (a future remote client) refuses world sets
/// (CanEditWorld, AeroSettings.Store.cs).
/// </summary>
public static partial class AeroSettings
{
    /// <summary>Where a setting shows in a settings screen: Basic (always), Advanced (a collapsed section), Hidden (only in
    /// the .cfg files and the API).</summary>
    internal enum Tier { Basic, Advanced, Hidden }

    internal sealed class Setting
    {
        public string Name, Type, Scope, Doc;
        public double Default, Min, Max;
        public bool Rebuild;
        public Func<double> Get;
        public Action<double> Set;
        public Func<double, string> Check;   // (a cross-setting rule: why not, or null)
        /// <summary>The player's value: the last one set through the store (Set, a file, the save) - what the files and the
        /// world save write. A field written directly (a harness command, a dev override) is live but never this, so it is
        /// never persisted by a later, unrelated Set (2026-10-15: a dev override could become the saved setting).</summary>
        public double Stored;
        // ── the UI metadata (the same fields as OrbitalSettings.Setting) ──
        /// <summary>The player-facing name, and the screen section it shows in.</summary>
        public string Label, Group;
        public Tier Tier;
        /// <summary>A number's unit ("" none) and a slider's step (0: the UI picks; an int's is 1).</summary>
        public string Unit = "";
        public double Step;
        /// <summary>For an int shown as a choice: a label for each value Min..Max (null: a number).</summary>
        public string[] Choices;
        /// <summary>The screen's order (registration order).</summary>
        public int Order;
        /// <summary>A world value clients also draw from (the plasma's Mach pair): to be replicated once SE2 can.</summary>
        public bool ClientVisible;

        /// <summary>The UI metadata, fluently after B / I / F.</summary>
        public Setting Ui(string label, string group, Tier tier, string unit = "", double step = 0, string[] choices = null)
        {
            Label = label; Group = group; Tier = tier; Unit = unit ?? ""; Choices = choices;
            if (step > 0) Step = step;
            return this;
        }
        /// <summary>Marks a world setting clients draw from too.</summary>
        public Setting SeenByClients() { ClientVisible = true; return this; }
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

    // the screen's sections, in order
    const string GFlight = "Flight model", GStructure = "Structure and damage", GHeat = "Re-entry heat", GPerf = "Performance (host)",
                 GHud = "HUD", GFeel = "Feel and audio", GFx = "Visual effects", GQuality = "Effects quality",
                 GLaunch = "Quick launch", GControls = "Controls";   // (a group is one scope: the menu keys sections by name)
    const Tier Basic = Tier.Basic, Advanced = Tier.Advanced, Hidden = Tier.Hidden;

    static AeroSettings()
    {
        // ══ WORLD ══

        // ── flight model ──
        B("Aero.Enabled", "world", false, () => AeroSwitch.Enabled, v => AeroSwitch.Enabled = v,
            "The whole aero simulation: forces, thrust offsets, block tracking. Off: the game's own flight; on again: every grid rebuilds.")
            .Ui("Aerodynamics", GFlight, Basic);
        F("Aero.DragMultiplier", "world", 0, 10, false, () => AeroTuning.DragMultiplier, v => AeroTuning.DragMultiplier = (float)v,
            "The aerodynamic force along the motion (drag), times this.")
            .Ui("Drag strength", GFlight, Basic, "×", 0.05);
        F("Aero.LiftMultiplier", "world", 0, 10, false, () => AeroTuning.LiftMultiplier, v => AeroTuning.LiftMultiplier = (float)v,
            "The aerodynamic force across the motion (lift: wings, control surfaces, the hull's own), times this.")
            .Ui("Lift strength", GFlight, Basic, "×", 0.05);
        B("Aero.ThrustTorque", "world", false, () => ThrustTorque.Enabled, v => ThrustTorque.Enabled = v,
            "Thrusters off the centre of mass turn the ship (and steer to the pilot's target). Off: the game's own thrust, untouched.")
            .Ui("Off-centre thrust turns ships", GFlight, Basic);
        B("Weather.Wind", "world", false, () => AeroWind.Enabled, v => AeroWind.Enabled = v,
            "The game's wind in the air the ship flies through (headwinds, crosswinds, gusts), in place of the game's own box-shaped wind push.")
            .Ui("Wind", GFlight, Basic);
        I("Control.HingeSurfaces", "world", 0, 1, false, () => HingeSurfaces.Mode, v => HingeSurfaces.Mode = (int)v,
            "Vanilla hinges carrying flaps as control surfaces, driven from the pilot's input (fly-by-wire mixing). 0 none (a player's hinges are theirs), 1 every hinge on a piloted grid.")
            .Ui("Hinge control surfaces", GFlight, Basic, choices: new[] { "Off", "Piloted grids" });
        F("Control.HingeMaxDeflection", "world", 1, 60, false, () => HingeSurfaces.MaxDeflection, v => HingeSurfaces.MaxDeflection = (float)v,
            "Hinge control surfaces: degrees at full command (the hinge's own limits clamp too).")
            .Ui("Hinge flap travel", GFlight, Advanced, "°", 1);
        I("Model.LargeGridCellScale", "world", 1, 4, true, () => SnapshotGridAccessor.LargeGridCellScale, v => SnapshotGridAccessor.LargeGridCellScale = (int)v,
            "Large-grid surface resolution: 1 = 0.25 m cells (finest, slowest), 2 = 0.5 m, 4 = 1 m. Rebuilds every grid.")
            .Ui("Hull detail (large grid)", GFlight, Advanced, choices: new[] { "0.25 m (finest)", "0.5 m", "0.75 m", "1 m (fastest)" });
        B("Aero.GroundEffect", "world", false, () => CompressibleWingModel.GroundEffect, v => CompressibleWingModel.GroundEffect = v,
            "Ground effect: wings close to the ground (within about a span) get more lift and less drag.")
            .Ui("Ground effect", GFlight, Advanced);
        B("Aero.UpperAtmosphere", "world", false, () => AtmosphereTail.Enabled, v => AtmosphereTail.Enabled = v,
            "The air thins out exponentially past the planet's visible atmosphere edge, so a fast ship meets faint drag there instead of a wall. Off: the game's linear fade, the air ending at the edge.")
            .Ui("Thin upper air", GFlight, Advanced);
        F("Control.ThrustTurnRate", "world", 0.1, 3, false, () => ThrustTorque.MaxRate, v => ThrustTorque.MaxRate = (float)v,
            "The fastest turn (rad/s) thruster steering asks for when turning the ship to the pilot's target.")
            .Ui("Thruster steering max turn rate", GFlight, Advanced, "rad/s", 0.05);
        B("Physics.HonestDampeners", "world", false, () => HonestHold.Enabled, v => HonestHold.Enabled = v,
            "Dampeners hold a ship up with real thrust against the gravity it really feels (the world's gravity multiplier included), and super dampeners brake with the thrusters' real thrust. A ship then needs thrust for its full weight to hover, and what holds it up is not there to climb with. Off: the game's own dampeners (they cancel twice the gravity with one weight of thrust).")
            .Ui("Honest dampeners", GFlight, Advanced);

        // ── structure and damage ──
        B("Structure.Loads", "world", false, () => StructureLoads.Enabled, v => StructureLoads.Enabled = v,
            "Work out the loads in every ship's structure (air, thrust, gyros, its own turning) and its hull's heating, a few times a second in the background. Damage is separate.")
            .Ui("Structural loads", GStructure, Basic).SeenByClients();   // (StructureFeel: the creaks and shake)
        B("Structure.Damage", "world", false, () => StructureLoads.Damage, v => StructureLoads.Damage = v,
            "Overloaded joints of the ship a player flies wear, and break off what hangs from them (pull too many g, whip the ship round too hard). AI ships and wrecks never. On by default.")
            .Ui("Structural damage", GStructure, Basic);
        F("Structure.Strength", "world", 0.1, 100, false, () => ShipStructure.Strength, v => ShipStructure.Strength = (float)v,
            "Every block's strength, times this (7: an airliner of small blocks yields near 3 g and sheds parts past 4; a light large-block plane yields near 8 g and breaks past 12).")
            .Ui("Structural strength", GStructure, Basic, "×", 0.1);
        B("Structure.Fatigue", "world", false, () => StructureFatigue.Enabled, v => StructureFatigue.Enabled = v,
            "Joints tire under hard flying below their yield too: from Structure.EnduranceUtil of the yield up they lose health slowly, faster the harder they are loaded. Health lost is strength lost, so a tired ship fails at a lower load. Kept in the blocks' health (saved; welding repairs it). Cruising never tires a ship.")
            .Ui("Metal fatigue", GStructure, Basic);
        B("Structure.Tear", "world", false, () => StructureTear.Enabled, v => StructureTear.Enabled = v,
            "A large piece the structure breaks off tears loose over a moment (held at the torn joint, sagging, then free) instead of vanishing off at once. Small bits still pop off.")
            .Ui("Slow tearing", GStructure, Basic);
        B("Structure.Bends", "world", false, () => StructureBend.Enabled, v => StructureBend.Enabled = v,
            "An overloaded joint that holds stays bent: the armour outboard of it (a wing, a pod) sets a little the way the load pushed, more the further past its yield, more again after a harder pull. With Structure.Damage.")
            .Ui("Permanent bends", GStructure, Basic);
        B("Structure.Dents", "world", false, () => AeroGridComponent.Dents, v => AeroGridComponent.Dents = v,
            "Overloaded armour dents as its joints wear (the game's deformation, more the further over). With Structure.Damage.")
            .Ui("Dents", GStructure, Basic);
        B("Structure.Crack", "world", false, () => FractureMath.Crack, v => FractureMath.Crack = v,
            "A failing section cracks across over a moment (Structure.CrackSeconds): the most pulled side tears first, the pushed side - the hinge - crumples and lets go last. Off: every block across the break lets go in the same instant.")
            .Ui("Cracks run across the break", GStructure, Basic);
        B("Structure.Jagged", "world", false, () => FractureMath.Jagged, v => FractureMath.Jagged = v,
            "The break line steps a block either side of the failing section here and there (the least loaded blocks most), never through a cockpit or a much heavier block. Off: ruler-straight breaks.")
            .Ui("Jagged break lines", GStructure, Basic);
        B("Structure.SplitBlocks", "world", false, () => AeroGridComponent.SplitBlocks, v => AeroGridComponent.SplitBlocks = v,
            "A joint that fails breaks its block's pieces on the failing side - the block stays, broken, on one side, and the ship parts along the failed face - instead of destroying the whole block (a block of one piece is still destroyed).")
            .Ui("Split broken blocks", GStructure, Advanced);
        B("Structure.BySection", "world", false, () => StructureEval.BySection, v => StructureEval.BySection = v,
            "A ship's weak neck wears and fails across its whole section at once - every block across the overloaded cut together, and what hangs from it leaves in one go. Off: block by block, the most loaded first (a fracture creeps along the neck).")
            .Ui("Fail whole sections", GStructure, Advanced);
        // (2026-10-15: a change rebuilds every grid - the structure's joints are made at its build)
        B("Structure.MountJoints", "world", true, () => ShipStructure.MountJoints, v => ShipStructure.MountJoints = v,
            "Blocks join at full strength only where their mount points mate (the game's own connection test); faces that only touch - armour pressed against a tank's or a thruster's side - carry Structure.TouchingShare of that. Off: every touching face at full strength. Rebuilds every grid.")
            .Ui("Joints by mount points", GStructure, Advanced);
        F("Structure.TouchingShare", "world", 0, 1, true, () => ShipStructure.TouchingShare, v => ShipStructure.TouchingShare = (float)v,
            "With Structure.MountJoints: the strength (and stiffness) of a face that only touches, with no mated mount point, as a share of a mated one's (0: carries nothing; 1: as strong as mated). Rebuilds every grid.")
            .Ui("Touching face strength", GStructure, Advanced, "", 0.05);
        F("Structure.CrackSeconds", "world", 0.05, 3, false, () => FractureMath.CrackSeconds, v => FractureMath.CrackSeconds = (float)v,
            "Seconds a crack takes to run across a failing section, from its first block letting go to the last (the hinge). With Structure.Crack.")
            .Ui("Crack run time", GStructure, Advanced, "s", 0.05);
        F("Structure.JaggedShare", "world", 0, 1, false, () => FractureMath.JaggedShare, v => FractureMath.JaggedShare = (float)v,
            "How rough a jagged break is: the chance the section's least loaded block steps off the break line (the most loaded step less). With Structure.Jagged.")
            .Ui("Break line roughness", GStructure, Advanced, "", 0.05);
        F("Structure.CrushUtil", "world", 1.05, 10, false, () => FractureMath.CrushUtil, v => FractureMath.CrushUtil = (float)v,
            "A block on the pushed side of a break is crushed - it goes whole - at this many times its compressive strength; below it, it crumples as the hinge and stays as a bent stub.")
            .Ui("Crush load", GStructure, Advanced, "× strength", 0.05);
        F("Structure.CrumpleDepth", "world", 0, 0.25, false, () => AeroGridComponent.CrumpleDepth, v => AeroGridComponent.CrumpleDepth = (float)v,
            "How far (m) a crumpling hinge's armour folds in at a break (0: no crumple).")
            .Ui("Hinge crumple depth", GStructure, Advanced, "m", 0.01);
        I("Structure.CrackSmokes", "world", 1, 10, false, () => AeroGridComponent.CrackSmokes, v => AeroGridComponent.CrackSmokes = (int)v,
            "Smoke puffs along a crack as it runs (at its first release, then evenly to the hinge). Picked on the server; Fx.TornSmoke hides the smoke on this PC.")
            .Ui("Smoke puffs per crack", GStructure, Advanced);
        B("Structure.ThrustLoads", "world", false, () => StructureEval.ThrustLoads, v => StructureEval.ThrustLoads = v,
            "Engine thrust loads the structure (engine mounts, and the push through the ship). Off: engines never strain or break a ship.")
            .Ui("Engine mount loads", GStructure, Advanced);
        B("Structure.EntryLoads", "world", false, () => StructureEval.EntryLoads, v => StructureEval.EntryLoads = v,
            "A re-entry flown on rails by the Orbital mod loads the structure with its braking (up to ~5 g), pressed onto the windward faces as the air would: the nose and heat shield carry it. Off: a railed entry heats the hull but never strains it.")
            .Ui("Re-entry braking loads", GStructure, Advanced);
        B("Structure.GyroLoads", "world", false, () => StructureEval.GyroLoads, v => StructureEval.GyroLoads = v,
            "Gyroscopes' torque loads the structure (their mounts, and the turn through the ship). Off: gyros never strain a ship.")
            .Ui("Gyro loads", GStructure, Advanced);
        B("Structure.ComponentLoads", "world", false, () => StructureEval.ComponentLoadsAtBlocks, v => StructureEval.ComponentLoadsAtBlocks = v,
            "Each block component's air loads its own block: a control surface's lift its hinge, an open airbrake's drag its mount, a gear leg's, an intake's or scoop's drag, another mod's drag patch at its point - taken off the rest of the air, which is spread over the windward faces. The ship's total load is unchanged; only where it acts. Off: the components' air spread over the windward faces with the hull's (before 2026-10-17).")
            .Ui("Component loads at their blocks", GStructure, Advanced);
        B("Structure.Contacts", "world", false, () => StructureContactLoads.Enabled, v => StructureContactLoads.Enabled = v,
            "Collisions and touchdowns load the structure (the contact's push, from the ship's change of momentum). Off: only flight loads it.")
            .Ui("Impact and landing loads", GStructure, Advanced);
        F("Structure.GearSturdiness", "world", 0, 5, false, () => AeroGridComponent.GearContactShare, v => AeroGridComponent.GearContactShare = (float)v,
            "How much of a touchdown's load landing gear takes, times this (1: as any block; higher: gear is sturdier than its block).")
            .Ui("Landing gear sturdiness", GStructure, Advanced, "×", 0.1);
        B("Control.RespectStructure", "world", false, () => StructureLoads.SteerRespectsStructure, v => StructureLoads.SteerRespectsStructure = v,
            "The flight controller limits its own steering to what the structure carries: it holds back before it overstresses the ship.")
            .Ui("Protective autopilot", GStructure, Advanced);
        F("Structure.BreakAt", "world", 1.05, 10, false, () => StructureLoads.BreakUtil, v => StructureLoads.BreakUtil = (float)v,
            "Past its yield a joint wears through over a moment, faster the further over: at this many times its yield load it loses Structure.WearRate of its health a second. It lets go when its health runs out (or at once past Structure.SnapUtil).")
            .Ui("Wear-through load", GStructure, Advanced, "× yield", 0.05).SeenByClients();   // (StructureFeel: the groans)
        F("Structure.WearRate", "world", 0, 100, false, () => StructureLoads.WearPerSecond, v => StructureLoads.WearPerSecond = (float)v,
            "Health a joint loses per second at Structure.BreakAt (0.45: a hard pull wears a joint through over a second or several; a brief spike only dents it).")
            .Ui("Wear rate", GStructure, Advanced, "/s", 0.05);
        F("Structure.SnapUtil", "world", 1.05, 100, false, () => StructureFatigue.SnapUtil, v => StructureFatigue.SnapUtil = (float)v,
            "A joint snaps at once at this many times its intact yield load (a genuine overload: a collision, a violent whip); below it, it wears through.")
            .Ui("Instant snap load", GStructure, Advanced, "× yield", 0.05);
        F("Structure.FatigueRate", "world", 0, 1, false, () => StructureFatigue.Rate, v => StructureFatigue.Rate = (float)v,
            "Health a joint loses per second from fatigue held at its yield (0.005: a tenth in 20 s at the yield, in ~40 s at 90%, ~5 min at 70%).")
            .Ui("Fatigue speed", GStructure, Advanced, "/s", 0.001);
        F("Structure.EnduranceUtil", "world", 0, 1, false, () => StructureFatigue.EnduranceUtil, v => StructureFatigue.EnduranceUtil = (float)v,
            "The endurance limit: joints loaded under this share of their yield never tire.")
            .Ui("Fatigue-free load", GStructure, Advanced, "× yield", 0.05);
        F("Structure.CompressionFatigueShare", "world", 0, 1, false, () => StructureFatigue.CompressionFatigueShare, v => StructureFatigue.CompressionFatigueShare = (float)v,
            "Fatigue grows cracks only while they are pulled open: a joint tires at the full rate for its tension, and a SOLID (stocky) member at this share of it for what compression adds (0.2: a pure compression member lasts about five times as long). Thin walls that buckle use Structure.BucklingFatigueShare.")
            .Ui("Compression fatigue (solid members)", GStructure, Advanced, "× tension", 0.05);
        F("Structure.BucklingFatigueShare", "world", 0, 1, false, () => StructureFatigue.BucklingFatigueShare, v => StructureFatigue.BucklingFatigueShare = (float)v,
            "Fatigue from compression in a THIN, buckling-limited member (most light armour walls), as a share of the tension rate: a plate that buckles and straightens every pull cracks at its edges, so 1 - the full rate.")
            .Ui("Compression fatigue (buckling walls)", GStructure, Advanced, "× tension", 0.05);
        F("Structure.TearSeconds", "world", 0.1, 10, false, () => StructureTear.TearSeconds, v => StructureTear.TearSeconds = (float)v,
            "Seconds a large broken-off piece takes to tear loose.")
            .Ui("Tear duration", GStructure, Advanced, "s", 0.1);
        F("Structure.TearMinMass", "world", 0, 1e7, false, () => StructureTear.MinMass, v => StructureTear.MinMass = (float)v,
            "Pieces at least this heavy (kg) tear loose slowly; lighter ones pop off at once.")
            .Ui("Slow-tear minimum mass", GStructure, Advanced, "kg", 100);
        I("Structure.TearAnchors", "world", 3, 8, false, () => StructureTear.MaxAnchors, v => StructureTear.MaxAnchors = (int)v,
            "The most points a tearing piece hangs from, spread over the broken section; each lets go in the crack's order.")
            .Ui("Tear anchor points", GStructure, Advanced);
        F("Structure.TearPeelFirst", "world", 0.05, 1, false, () => StructureTear.PeelFirst, v => StructureTear.PeelFirst = (float)v,
            "A tear peels: its first anchor (where the crack started) lets go at this share of Structure.TearSeconds, the hinge's at the end (1: all together).")
            .Ui("Tear peel start", GStructure, Advanced, "× tear time", 0.05);
        F("Structure.TearHold", "world", 0, 50, false, () => StructureTear.ForceFactor, v => StructureTear.ForceFactor = (float)v,
            "How hard a tearing piece is held at first, in g of its own weight (it eases to nothing over the tear).")
            .Ui("Tear hold", GStructure, Hidden, "g", 0.5);
        F("Structure.BendPerUtil", "world", 0, 1, false, () => StructureBend.PerUtil, v => StructureBend.PerUtil = (float)v,
            "The permanent turn (radians) at a joint per unit of load over its last peak (0.05: a 10 m wing's tip sets ~10 cm at 1.2 x its yield, ~25 cm just short of breaking).")
            .Ui("Bend amount", GStructure, Advanced, "rad", 0.01);
        F("Structure.BendMax", "world", 0, 1, false, () => StructureBend.Max, v => StructureBend.Max = (float)v,
            "The most any point of the armour is bent in total (m).")
            .Ui("Maximum bend", GStructure, Advanced, "m", 0.01);
        I("Structure.BendMaxBlocks", "world", 1, 5000, false, () => StructureBend.MaxBlocks, v => StructureBend.MaxBlocks = (int)v,
            "The most blocks one bend moves; a bigger part past the joint is the ship's body, not a tip, and is not bent.")
            .Ui("Largest bendable part", GStructure, Advanced, "blocks", 10);
        F("Structure.HingeToCompression", "world", 0, 1, false, () => StructureBend.HingeToCompression, v => StructureBend.HingeToCompression = (float)v,
            "Where a permanent bend pivots: 1 about the section's most pushed edge (a plastic hinge on the crushed face, the pulled face opening), 0 about the section's centre, or between.")
            .Ui("Bends fold at the pushed side", GStructure, Advanced, "", 0.05);
        // (Structure.WingStrain / Structure.WingStrength, the legacy wing-root wear, went 2026-10-17: old files still load)

        // ── re-entry heat ──
        F("Heat.Multiplier", "world", 0, 10, false, () => HullHeat.HeatMultiplier, v => HullHeat.HeatMultiplier = (float)v,
            "The air's heating of the hull, times this.")
            .Ui("Re-entry heating", GHeat, Basic, "×", 0.05);
        B("Heat.Damage", "world", false, () => StructureLoads.HeatDamage, v => StructureLoads.HeatDamage = v,
            "Hull blocks hotter than they can take (reentry, very fast low flight) lose health. Heat weakens the structure either way. On by default.")
            .Ui("Heat damage", GHeat, Basic);
        B("Heat.Scorch", "world", false, () => HeatScorch.Enabled, v => HeatScorch.Enabled = v,
            "Heat leaves a few large burn marks on the hull where it got hot in a re-entry. Worked out on the server; Fx.ScorchVisible hides them on this PC.")
            .Ui("Scorch marks", GHeat, Basic).SeenByClients();   // (HeatScorch.Sync: the client stamps them)
        F("Entry.OnsetMach", "world", 0.5, 25, false, () => AeroEntryFx.OnsetMach, SetOnsetMach,
            "Entry heating (the plasma) starts at this Mach number.")
            .Ui("Heating starts at Mach", GHeat, Advanced, "Mach", 0.1).SeenByClients().Check = v => v + 0.05 >= FullMach() ? "must stay under Entry.FullMach" : null;
        F("Entry.FullMach", "world", 0.6, 30, false, FullMach, SetFullMach,
            "Entry heating is full at this Mach number.")
            .Ui("Full heating at Mach", GHeat, Advanced, "Mach", 0.1).SeenByClients().Check = v => v <= AeroEntryFx.OnsetMach + 0.05 ? "must stay over Entry.OnsetMach" : null;
        F("Entry.HeatLag", "world", 0.05, 30, false, () => AeroEntryFx.HeatLag, v => AeroEntryFx.HeatLag = (float)v,
            "Seconds the hull takes to heat up and cool down (the glow lingers after slowing).")
            .Ui("Heat-up / cool-down time", GHeat, Advanced, "s", 0.05).SeenByClients();
        I("Heat.ScorchSpots", "world", 0, 16, false, () => HeatScorch.MaxSpots, v => HeatScorch.MaxSpots = (int)v,
            "The most scorch marks one ship gets (the hot faces are gathered into this many spots).")
            .Ui("Scorch marks per ship", GHeat, Advanced);

        // ── quick launch (2026-10-18: the Orbital Mod's creative Ctrl+T, moved here - Launch/QuickLaunch.cs; the server reads these) ──
        B("QuickLaunch.Creative", "world", false, () => QuickLaunch.Creative, v => QuickLaunch.Creative = v,
            "In creative, a seated pilot's quick launch key (Controls) lifts the ship over the ground, turns it level and sends it flying along its nose. Survival never has it. Off: not in this world either.")
            .Ui("Quick launch in creative", GLaunch, Basic);
        F("QuickLaunch.AltitudeM", "world", 100, 5000, false, () => QuickLaunch.AltitudeM, v => QuickLaunch.AltitudeM = v,
            "Quick launch: how high over the ground (m; with no ground found under the ship, over where it is). Never lower than the ship is, never into the ground; with the Orbital Mod, always under the planet's space border.")
            .Ui("Launch altitude", GLaunch, Basic, "m", 100);
        F("QuickLaunch.SpeedMps", "world", 0, 1000, false, () => QuickLaunch.SpeedMps, v => QuickLaunch.SpeedMps = v,
            "Quick launch: the speed along the nose (m/s). 0: the ship's cruise speed (its top speed at 3/4 throttle; 150 m/s when it has none). At most just under the world's speed cap.")
            .Ui("Launch speed (0: cruise)", GLaunch, Advanced, "m/s", 10);

        // ── performance (the host's) ──
        F("Perf.FrameBudgetMs", "world", 0.1, 16, false, () => AeroFrameBudget.BudgetMs, v => AeroFrameBudget.BudgetMs = v,
            "The most aero may take of a simulation frame (ms); past it, grids not piloted wait a few frames.")
            .Ui("Aero time budget per frame", GPerf, Advanced, "ms", 0.1);
        I("Perf.Workers", "world", 1, 16, false, () => AeroWork.Workers, v => AeroWork.Workers = (int)v,
            "Background cores aero may keep busy (hull and force-table builds).")
            .Ui("Background threads", GPerf, Advanced);
        B("Perf.TableCache", "world", false, () => AeroTableCache.Enabled, v => AeroTableCache.Enabled = v,
            "Keep built force tables for reuse (a grid seen before builds in milliseconds).")
            .Ui("Reuse force tables", GPerf, Advanced);
        I("Perf.TableCacheMB", "world", 16, 4096, false, () => (int)(AeroTableCache.MaxBytes >> 20), v => AeroTableCache.MaxBytes = (long)v << 20,
            "The force-table cache's size (MB).")
            .Ui("Force-table cache size", GPerf, Advanced, "MB", 16);
        B("Perf.LocalUpdates", "world", false, () => AeroGridComponent.LocalUpdates, v => AeroGridComponent.LocalUpdates = v,
            "Update only the changed part of a hull when blocks change (off: rebuild the whole grid).")
            .Ui("Partial hull updates", GPerf, Advanced);
        F("Structure.Rate", "world", 1, 20, false, () => StructureLoads.Rate, v => StructureLoads.Rate = (float)v,
            "Structure evaluations per second of a player's ship (higher: quicker to react, more background work).")
            .Ui("Structure updates per second", GPerf, Advanced, "Hz", 0.5);
        I("Perf.ChunksPerUpdate", "world", 1, 64, false, () => AeroGridComponent.ChunksPerUpdate, v => AeroGridComponent.ChunksPerUpdate = (int)v,
            "Hull chunks updated per pass after a block change.")
            .Ui("Hull chunks per update", GPerf, Hidden);
        F("Perf.MinSpeedForUpdates", "world", 0, 1000, false, () => AeroGridComponent.MinSpeedForUpdates, v => AeroGridComponent.MinSpeedForUpdates = (float)v,
            "Below this speed (m/s) a damaged or edited hull waits before updating its forces.")
            .Ui("Hull update minimum speed", GPerf, Hidden, "m/s", 1);
        F("Perf.QuietBeforeFullRebuild", "world", 0.5, 120, false, () => AeroGridComponent.QuietBeforeFullRebuild, v => AeroGridComponent.QuietBeforeFullRebuild = v,
            "Seconds without block changes before a full rebuild cleans up after local updates.")
            .Ui("Quiet time before a full rebuild", GPerf, Hidden, "s", 0.5);

        // ══ CLIENT ══

        // ── HUD ──
        B("Hud.StallWarning", "client", false, () => StallWarning.Enabled, v => StallWarning.Enabled = v,
            "The stall warning, from the whole ship's air force (any shape): STALL when pulling further only loses lift, amber when close to it.")
            .Ui("Stall warning", GHud, Basic);
        B("Hud.Loac", "client", false, () => LoacWarning.Enabled, v => LoacWarning.Enabled = v,
            "The loss-of-control warning, predicted: CONTROL LIMIT / LOSS OF CONTROL when holding the attitude you command needs nearly / more than the torque the gyros, thrusters and control surfaces have at this speed (slow, the surfaces lose to the air).")
            .Ui("Loss-of-control warning", GHud, Basic);
        B("Hud.Mach", "client", false, () => MachHud.On, v => MachHud.On = v,
            "Mach in the game's speed readout (\"312 m/s · M 0.92\"), for what you are flying. Off by default: the flight boxes by ALT show it.")
            .Ui("Mach in speed readout", GHud, Basic);
        B("Hud.GameStallWarning", "client", false, () => GameStallWarning.On, v => GameStallWarning.On = v,
            "The game's own stall warning (the big red text when the thrusters cannot hold the ship against gravity - it ignores the wings' lift). Off by default: removed.")
            .Ui("Game's own stall text", GHud, Basic);

        // ── feel and audio (the airframe you can feel - StructureFeel.cs, 2026-10-14: the structure's load, heard and felt in the seat) ──
        B("Feel.Creaks", "client", false, () => StructureFeel.Creaks, v => StructureFeel.Creaks = v,
            "The airframe creaks and groans as the structure is loaded (from 60% of its strength, more and louder toward 100%), and cracks when a block wears or breaks. The ship you are seated in.")
            .Ui("Airframe creaks", GFeel, Basic);
        F("Feel.CreakVolume", "client", 0, 2, false, () => StructureFeel.Volume, v => StructureFeel.Volume = (float)v,
            "The creaks' and cracks' volume, times this.")
            .Ui("Creak volume", GFeel, Basic, "×", 0.05);
        B("Feel.Shake", "client", false, () => StructureFeel.Shake, v => StructureFeel.Shake = v,
            "The cockpit buzzes as the structure is loaded (from 50% of its strength, noticeable near 100%), with a jolt when a block breaks. The game's own camera shake settings apply.")
            .Ui("Load shake", GFeel, Basic);
        F("Feel.ShakeScale", "client", 0, 5, false, () => StructureFeel.ShakeScale, v => StructureFeel.ShakeScale = (float)v,
            "The load shake's strength, times this.")
            .Ui("Load shake strength", GFeel, Basic, "×", 0.05);
        F("Feel.CreakFrom", "client", 0.3, 1, false, () => StructureFeel.CreakFrom, v => StructureFeel.CreakFrom = (float)v,
            "The creaks start at this share of the structure's strength (0.6: at 60%).")
            .Ui("Creaks start at load", GFeel, Advanced, "× strength", 0.05);

        // ── visual effects ──
        B("Fx.Effects", "client", false, () => AeroEntryFx.AllEffects, v => AeroEntryFx.AllEffects = v,
            "Every aero visual effect (plasma, vapour, tip trails, the plasma light). Off: none at all.")
            .Ui("Aero visual effects", GFx, Basic);
        B("Fx.FlightField", "client", false, () => FlightField.On, v => FlightField.On = v,
            "The full flight effects (off: the older single plasma at the nose, no vapour or trails).")
            .Ui("Full flight effects", GFx, Advanced);
        B("Fx.Plasma", "client", false, () => FlightField.PlasmaOn, v => FlightField.PlasmaOn = v, "Entry plasma sparks over the hull.")
            .Ui("Re-entry plasma", GFx, Basic);
        B("Fx.Vapour", "client", false, () => FlightField.VapourOn, v => FlightField.VapourOn = v, "Transonic vapour along the hull's edges.")
            .Ui("Transonic vapour", GFx, Basic);
        B("Fx.Tips", "client", false, () => FlightField.TipsOn, v => FlightField.TipsOn = v, "Wing-tip vapour trails.")
            .Ui("Wingtip trails", GFx, Basic);
        B("Fx.SpotLight", "client", false, () => FlightField.SpotOn, v => FlightField.SpotOn = v, "The plasma's light on the hull (one shadowed spot light per glowing grid).")
            .Ui("Plasma glow light", GFx, Basic);
        B("Fx.TornSmoke", "client", false, () => TearSmoke.Enabled, v => TearSmoke.Enabled = v,
            "A joint the structure breaks smokes and sheds debris for a few seconds, on the ship and on the piece that tears off.")
            .Ui("Smoke from breaks", GFx, Basic);
        F("Fx.TornSmokeSeconds", "client", 0.5, 20, false, () => TearSmoke.Seconds, v => TearSmoke.Seconds = (float)v,
            "Seconds a torn joint smokes.")
            .Ui("Break smoke duration", GFx, Advanced, "s", 0.5);
        B("Fx.VapourCone", "client", false, () => FlightField.ConeOn, v => FlightField.ConeOn = v,
            "The transonic shock's vapour cone round the body (experimental: off until tuned).")
            .Ui("Vapour cone (experimental)", GFx, Advanced);
        F("Fx.Brightness", "client", 0, 3, false, () => FlightField.Bright, v => FlightField.Bright = (float)v,
            "The plasma's and glow's brightness, times this.")
            .Ui("Plasma brightness", GFx, Advanced, "×", 0.05);
        B("Fx.ScorchVisible", "client", false, () => HeatScorch.Visible, v => HeatScorch.Visible = v,
            "Draw the hull's scorch marks on this PC (Heat.Scorch, the world's, decides whether there are any). Turning it off stops new marks only: those already placed stay.")
            .Ui("Show scorch marks", GFx, Advanced);
        B("Fx.ThrusterFlamesFollow", "client", false, () => ThrustTorque.FlamesEnabled, v => ThrustTorque.FlamesEnabled = v,
            "Thruster flames show the share each thruster gives to steering (off: the game's own flames).")
            .Ui("Thruster flames show attitude thrust", GFx, Advanced);
        F("Fx.DebrisScale", "client", 0, 3, false, () => TearSmoke.DebrisScale, v => TearSmoke.DebrisScale = (float)v,
            "The debris a break sheds, times this.")
            .Ui("Break debris amount", GFx, Advanced, "×", 0.05);
        B("Debris.Drag", "client", false, () => AeroDebrisDrag.Enabled, v => AeroDebrisDrag.Enabled = v,
            "Loose fracture pieces (a broken block's chips, not grids) slow in the air: drag on their bounding box against the air and the wind, and a little on their tumbling. The pieces live on this PC only.")
            .Ui("Debris drag", GFx, Advanced);
        F("Debris.DragScale", "client", 0, 10, false, () => AeroDebrisDrag.DragScale, v => AeroDebrisDrag.DragScale = (float)v,
            "The pieces' drag coefficient on their bounding box (1: a blunt chunk; 0.4: slight).")
            .Ui("Debris drag strength", GFx, Advanced, "×", 0.05);

        // ── effects quality ──
        I("Fx.MaxGrids", "client", 1, 32, false, () => FlightField.FlightMax, v => FlightField.FlightMax = (int)v,
            "Grids with flight effects at once (the nearest to the camera).")
            .Ui("Ships with effects", GQuality, Basic);
        F("Fx.Range", "client", 100, 50000, false, () => FlightField.FlightRange, v => FlightField.FlightRange = (float)v,
            "Distance (m) past which a grid shows no flight effects.")
            .Ui("Effect distance", GQuality, Basic, "m", 100);
        I("Fx.SpawnPerTick", "client", 1, 200, false, () => FlightField.SpawnPerTick, v => FlightField.SpawnPerTick = (int)v,
            "Effects started per frame while a grid's effects build up (higher: faster, more hitching).")
            .Ui("Effect build-up speed", GQuality, Advanced);

        // ── controls ──
        I("QuickLaunch.Key", "client", 0, 26, false, () => QuickLaunch.Key, v => QuickLaunch.Key = (int)v,
            "The quick launch key (seated, in creative worlds that allow it): a letter, with the modifier below. Off: no key.")
            .Ui("Quick launch key", GControls, Basic, choices: QuickLaunchRules.KeyChoices());
        I("QuickLaunch.Modifier", "client", 0, 3, false, () => QuickLaunch.Modifier, v => QuickLaunch.Modifier = (int)v,
            "Held with the quick launch key. None: the key alone, with no Ctrl, Shift or Alt held.")
            .Ui("Quick launch modifier", GControls, Basic, choices: new[] { "None", "Ctrl", "Shift", "Alt" });
    }

    // ── the entry heating's Mach pair (stored as heat: OnsetHeat (M / OnsetMach)^3; FullMach is where it is full) ──
    static double FullMach() => AeroEntryFx.OnsetMach * Math.Cbrt(AeroEntryFx.FullHeat / AeroEntryFx.OnsetHeat);
    static void SetFullMach(double m) => AeroEntryFx.FullHeat = (float)(AeroEntryFx.OnsetHeat * Math.Pow(m / AeroEntryFx.OnsetMach, 3));
    static void SetOnsetMach(double m) { double full = FullMach(); AeroEntryFx.OnsetMach = (float)m; SetFullMach(full); }   // (the full point stays put)

    static Setting Add(string name, string type, string scope, double min, double max, bool rebuild, Func<double> get, Action<double> set, string doc)
    {
        var s = new Setting { Name = name, Type = type, Scope = scope, Min = min, Max = max, Rebuild = rebuild, Get = get, Set = set, Doc = doc };
        s.Default = s.Stored = get();   // (the field's own initial value)
        s.Order = _all.Count;
        if (type != "float") s.Step = 1;
        _all.Add(s); _by[name] = s;
        return s;
    }
    static Setting B(string n, string scope, bool rebuild, Func<bool> get, Action<bool> set, string doc) => Add(n, "bool", scope, 0, 1, rebuild, () => get() ? 1 : 0, v => set(v != 0), doc);
    static Setting I(string n, string scope, double min, double max, bool rebuild, Func<int> get, Action<double> set, string doc) => Add(n, "int", scope, min, max, rebuild, () => get(), set, doc);
    static Setting F(string n, string scope, double min, double max, bool rebuild, Func<double> get, Action<double> set, string doc) => Add(n, "float", scope, min, max, rebuild, get, set, doc);
    static Setting F(string n, string scope, double min, double max, bool rebuild, Func<float> get, Action<double> set, string doc) => Add(n, "float", scope, min, max, rebuild, () => get(), set, doc);
}
