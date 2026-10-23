using Keen.Game2.Simulation.WorldObjects.CubeGrids;
using Keen.VRage.Core.Game.Definitions;

#pragma warning disable
namespace AeroMod;

[DefinitionPostProcessor(ForceInstantiation = true)]
public class EntityInjector : SimpleDefinitionPostProcessor<PrefabDefinition>
{
    /// <summary>Inject AeroFlameComponent into client thrusters: each thruster's flame shows its share of the thrust,
    /// attitude included (ThrustTorque.FlamesEnabled). (Off 2026-10-04 while the shares darkened the game's flames: those
    /// were x efficiency twice - capacities are now x efficiency - and dropped whenever the controller stopped steering.)</summary>
    public static bool InjectFlames = true;

    public override void PostProcess(PrefabDefinition definition)
    {
        ScriptResultsCleanup.Ensure();   // (the world-switch fix: runs on every world load, before any entity - Reload/)
        // (a mod panel button on a toolbar: its model type found by name - Blocks/ToolbarTypeResolve.cs; out when this project unloads)
        if (ToolbarTypeResolve.Hook()) ScriptResultsCleanup.OwnProjectUnloading += ToolbarTypeResolve.Unhook;
        InjectAeroComponents.Please(definition);
    }
}

public class InjectAeroComponents : Injections
{
    private static int _gridCount;
    private static int _thrusterCount;
    private static int _tankCount;
    private static int _flameCount;
    private static int _wheelCount;

    public static void Please(PrefabDefinition entity)
    {
        var composition = entity.Composition;
        bool hasCubeGrid = false;
        bool hasThruster = false;
        bool hasTank = false;
        bool hasFlame = false;   // a client thruster (it draws flames)
        bool isServerPlanet = false;   // (carries the world's aero settings in the save)
        bool isRenderCamera = false;   // (the client's render camera: the client's session tick, AeroClientHost)
        bool isServerBody = false;   // (a player's server character or spectator: carries the world's aero settings too)
        bool hasBodyCarrier = false;
        bool hasWheel = false, hasShock = false;   // (a vanilla suspension: the game's wheels and the mod's gears)

        foreach (var type in composition.Types)
        {
            if (type == typeof(CubeGridComponent))
                hasCubeGrid = true;
            if (type.Name == "ThrusterComponent")
                hasThruster = true;
            if (type.Name == "ResourceContainerComponent")
                hasTank = true;
            if (type.Name == "ThrusterEffectsComponent")
                hasFlame = true;
            if (type == typeof(Keen.Game2.Simulation.GameSystems.Discoveries.Discoverables.DiscoverablePlanetComponent))
                isServerPlanet = true;
            if (type == typeof(Keen.Game2.Client.GameSystems.CameraSystems.CameraComponent))
                isRenderCamera = true;
            if (type == typeof(Keen.Game2.Simulation.WorldObjects.Characters.CharacterMovementServerComponent)
                || type == typeof(Keen.Game2.Simulation.WorldObjects.Spectators.ServerSpectatorComponent))
                isServerBody = true;
            if (type == typeof(AeroWorldBodyComponent))
                hasBodyCarrier = true;   // (a composition two prefabs share: once)
            if (type == typeof(Keen.Game2.Simulation.WorldObjects.CubeBlocks.MechanicalBlocks.Wheels.WheelCornerModuleComponent))
                hasWheel = true;
            if (type == typeof(AeroWheelShockComponent))
                hasShock = true;   // (already injected: a shared composition)
        }

        if (isRenderCamera)
        {
            // (the client scene's one camera entity - ClientPlayersSessionConfiguration.RenderCamera: there with no grid, on
            //  foot, in spectator; the settings menu, the files' poll and the local player's HUD and feel run from it)
            Add(entity, typeof(AeroClientHostComponent));
            Log.Default?.Info($"[AERO] Injected AeroClientHostComponent into camera prefab '{entity.DebugName}'");
        }

        if (isServerPlanet)
        {
            // (a fixed component id and an object builder: the settings are saved with it, so the id must be the same
            //  every load - not derived from the composition size, which depends on what other mods inject)
            Add(entity, new Keen.VRage.DCS.Builders.EntityBuilder.ComponentBuildInfo
            {
                Type = typeof(AeroWorldSettingsComponent),
                ComponentId = WorldCarrier.PlanetId,
                ObjectBuilder = new Keen.Game2.Simulation.GameSystems.EntityNameSessionComponentObjectBuilder(),
            });
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroWorldSettingsComponent into server planet prefab '{entity.DebugName}'");
        }

        if (isServerBody && !hasBodyCarrier)
        {
            // (2026-10-19, planetless worlds: a space-only save holds only the players' bodies - the world's settings ride
            //  on them too; the SERVER compositions only (CharacterMovementServerComponent, ServerSpectatorComponent are in no client one),
            //  a fixed id and the engine's builder as the planets' carrier - Runtime/AeroWorldSettingsComponent.cs)
            Add(entity, new Keen.VRage.DCS.Builders.EntityBuilder.ComponentBuildInfo
            {
                Type = typeof(AeroWorldBodyComponent),
                ComponentId = WorldCarrier.BodyId,
                ObjectBuilder = new Keen.Game2.Simulation.GameSystems.EntityNameSessionComponentObjectBuilder(),
            });
            Log.Default?.Info($"[AERO] Injected AeroWorldBodyComponent (world settings carrier) into server body prefab '{entity.DebugName}'");
        }

        if (hasCubeGrid)
        {
            Add(entity, typeof(AeroGridComponent));
            Add(entity, typeof(AeroEntryFxComponent));

            if (_gridCount < 3)
                Log.Default?.Info($"[AERO] Injected AeroGridComponent into prefab #{_gridCount}");
            _gridCount++;
        }

        if (hasThruster)
        {
            // (2026-10-15: a fixed id and the engine's builder - its attitude fraction is saved with the block, AeroBlockSave)
            Add(entity, AeroBlockSave.Info(typeof(AeroThrustSettingsComponent), AeroBlockSave.ThrustId));

            if (_thrusterCount < 3)
                Log.Default?.Info($"[AERO] Injected AeroThrustSettingsComponent into thruster prefab #{_thrusterCount}");
            _thrusterCount++;
        }

        if (hasFlame && EntityInjector.InjectFlames)
        {
            Add(entity, typeof(AeroFlameComponent));
            if (_flameCount < 3)
                Log.Default?.Info($"[AERO] Injected AeroFlameComponent into client thruster prefab #{_flameCount}");
            _flameCount++;
        }

        // the 3x3x1 control surfaces' client prefabs: their flap subpart turned by the server's deflection
        if (AeroBlockGuids.FlapClientPrefabs.Contains(entity.Guid))
        {
            Add(entity, typeof(AeroFlapComponent));
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroFlapComponent into control surface client prefab '{entity.DebugName}'");
        }

        // their server prefabs: the terminal's settings (axes, max deflection, trim), read by the grid's control
        if (AeroBlockGuids.SurfaceServerPrefabs.Contains(entity.Guid))
        {
            Add(entity, AeroBlockSave.Info(typeof(AeroSurfaceSettingsComponent), AeroBlockSave.SurfaceId));   // (saved with the block)
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroSurfaceSettingsComponent into control surface server prefab '{entity.DebugName}'");
        }

        // the retractable gears: the server drives the gear (its vanilla wheel made and unmade), the client animates it
        if (AeroBlockGuids.GearServerPrefabs.Contains(entity.Guid))
        {
            Add(entity, AeroBlockSave.Info(typeof(AeroGearComponent), AeroBlockSave.GearId));   // (saved with the block)
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroGearComponent into gear server prefab '{entity.DebugName}'");
        }
        // WHEEL SHOCKS (2026-10-20): every suspension - the game's and the mod's gears - gets the airshock and the oleo strut
        // (AeroWheelShockComponent: server only, at run time; not saved - the world's Wheels.* settings drive it)
        if (hasWheel && !hasShock)
        {
            Add(entity, typeof(AeroWheelShockComponent));
            if (_wheelCount < 3) Log.Default?.Info($"[AERO] Injected AeroWheelShockComponent into suspension prefab '{entity.DebugName}'");
            _wheelCount++;
        }
        // the 3x3x3 airbrakes: the server opens them on the parking signal, the client turns their leaves
        if (AeroBlockGuids.AirbrakeServerPrefabs.Contains(entity.Guid))
        {
            Add(entity, AeroBlockSave.Info(typeof(AeroAirbrakeComponent), AeroBlockSave.AirbrakeId));   // (saved with the block)
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroAirbrakeComponent into airbrake server prefab '{entity.DebugName}'");
        }
        if (AeroBlockGuids.AirbrakeClientPrefabs.Contains(entity.Guid))
        {
            Add(entity, typeof(AeroAirbrakeAnimComponent));
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroAirbrakeAnimComponent into airbrake client prefab '{entity.DebugName}'");
        }
        // the Autopilot block (2026-10-19): the server keeps its panel, flies its grid through it and saves it
        if (AeroBlockGuids.AutopilotServerPrefabs.Contains(entity.Guid))
        {
            Add(entity, AeroBlockSave.Info(typeof(AeroAutopilotComponent), AeroBlockSave.AutopilotId));   // (saved with the block)
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroAutopilotComponent into autopilot server prefab '{entity.DebugName}'");
        }
        // the propulsors (turboprops, rotors, ducted fans - vanilla atmospheric thrusters underneath): both scenes shape their
        // maximum with the air (AeroPropulsorComponent); the rotors' cyclic reads it (RotorCyclic)
        if (PropulsorBlocks.ServerPrefabs.Contains(entity.Guid) || PropulsorBlocks.ClientPrefabs.Contains(entity.Guid))
        {
            Add(entity, typeof(AeroPropulsorComponent));
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroPropulsorComponent into propulsor prefab '{entity.DebugName}'");
        }
        // ROTOR STRIKES (2026-10-17): the striking propulsors' server copies probe their disc and keep the blades' saved health
        if (PropulsorBlocks.StrikeServerPrefabs.Contains(entity.Guid))
        {
            Add(entity, AeroBlockSave.Info(typeof(AeroRotorStrikeComponent), AeroBlockSave.BladesId));   // (saved with the block)
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroRotorStrikeComponent into propulsor server prefab '{entity.DebugName}'");
        }
        // a main helicopter rotor's turn (2026-10-23): the Counter-rotating switch on its terminal, saved with the block
        if (PropulsorBlocks.MainRotorServerPrefabs.Contains(entity.Guid))
        {
            Add(entity, AeroBlockSave.Info(typeof(AeroRotorSpinComponent), AeroBlockSave.RotorSpinId));   // (saved with the block)
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroRotorSpinComponent into main rotor server prefab '{entity.DebugName}'");
        }
        // a reversible thruster (thrust either way, in the game's movement and the attitude law): any thruster block whose
        // prefab is listed (ReversibleThrustComponent.Prefabs - the propulsor table's reversible rows and Extra)
        if (ReversibleThrustComponent.Prefabs.Contains(entity.Guid))
        {
            Add(entity, typeof(ReversibleThrustComponent));
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected ReversibleThrustComponent into thruster prefab '{entity.DebugName}'");
        }
        if (PropulsorBlocks.RotorClientPrefabs.Contains(entity.Guid))
        {
            Add(entity, typeof(AeroRotorAnimComponent));   // (the hub, blades and blur disc, client only)
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroRotorAnimComponent into rotor client prefab '{entity.DebugName}'");
        }
        if (PropulsorBlocks.RamjetClientPrefabs.Contains(entity.Guid))
        {
            Add(entity, typeof(AeroRamjetSpikeComponent));   // (the inlet spike slid aft with the Mach, client only)
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroRamjetSpikeComponent into ramjet client prefab '{entity.DebugName}'");
        }
        // the solid rocket motors (2026-10-17): a segment's tank read, weighed and kept on Auto on its server copy; the nozzle in both
        // scenes (the server ignites and burns it; both report its thrust to ThrustTorque) - the same saving id and builder in both
        // (2026-10-23: a nozzle's server copy too - its own grain's tank)
        if (SolidRocketBlocks.SegmentServerPrefabs.Contains(entity.Guid) || SolidRocketBlocks.NozzleServerPrefabs.Contains(entity.Guid))
        {
            Add(entity, AeroBlockSave.Info(typeof(AeroSolidSegmentComponent), AeroBlockSave.SolidSegmentId));
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroSolidSegmentComponent into solid rocket segment prefab '{entity.DebugName}'");
        }
        if (SolidRocketBlocks.NozzleServerPrefabs.Contains(entity.Guid) || SolidRocketBlocks.NozzleClientPrefabs.Contains(entity.Guid))
        {
            Add(entity, AeroBlockSave.Info(typeof(AeroSolidNozzleComponent), AeroBlockSave.SolidNozzleId));
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroSolidNozzleComponent into solid rocket nozzle prefab '{entity.DebugName}'");
        }
        // the exhaust (2026-10-17): the ramjets' blue-violet flame and shock diamonds, the solid nozzles' plume, balloon and smoke trail -
        // the mod's own particle effects, client only (Fx/AeroExhaustComponent; the blocks' own flame definitions carry no effect)
        if (PropulsorBlocks.RamjetClientPrefabs.Contains(entity.Guid) || SolidRocketBlocks.NozzleClientPrefabs.Contains(entity.Guid))
        {
            Add(entity, typeof(AeroExhaustComponent));
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroExhaustComponent into client prefab '{entity.DebugName}'");
        }
        if (AeroBlockGuids.GearClientPrefabs.Contains(entity.Guid))
        {
            Add(entity, typeof(AeroGearAnimComponent));
            if (AeroLog.Detail) Log.Default?.Info($"[AERO] Injected AeroGearAnimComponent into gear client prefab '{entity.DebugName}'");
        }

        // (a solid rocket segment's tank - Solid Propellant, the conveyor version 2026-10-17 - weighs through AeroSolidSegmentComponent:
        //  one mass provider per block)
        if (hasTank && !SolidRocketBlocks.SegmentServerPrefabs.Contains(entity.Guid) && !SolidRocketBlocks.NozzleServerPrefabs.Contains(entity.Guid))
        {
            Add(entity, typeof(AeroFuelMassComponent));

            if (_tankCount < 3)
                Log.Default?.Info($"[AERO] Injected AeroFuelMassComponent into tank prefab #{_tankCount}");
            _tankCount++;
        }
    }
}
