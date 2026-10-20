# Components - block-level aero components

Blocks whose air is modelled on their own (`IAeroBlockComponent`), added on top of the hull and wing model. Pure math
over `BlockInfo` / `AeroContext` (no SE2 types) except the factory, which reads the blocks.

| File | What |
|---|---|
| `IAeroBlockComponent.cs`, `ComponentForceResult.cs`, `BlockInfo.cs`, `IFaceOverride.cs` | The component contract, its result, a block's placement, and components that replace (not add to) the drag on their faces |
| `AeroComponentRegistry.cs` | A grid's components: `EvaluateAll` (force and torque), face-override subtraction |
| `BlockComponentFactory.cs`, `DefaultMappings.cs` | Which block gets which component (by definition GUID; vanilla atmospheric thrusters get an `AirIntake`); `IsSelfModelled` keeps such blocks out of the surface |
| `AeroBlockGuids.cs` | The mod's block and prefab GUIDs (must match the definitions in `Assets/Aero/` - the former "Aerodynamics Mod Blocks") |
| `ControlSurface.cs`, `ControlSurfaceGeometry.cs`, `IControlSurfaceSettings.cs`, `LocalAeroConditions.cs` | Deflectable surfaces (1x1, 3x3x1): thin-airfoil lift with Mach, a flat plate past the stall (the deflection's authority floored), what they sit in, their terminal settings, the local air at a block; the allocator's and mixer's measure (`Measure`, `Refine`, `MixerInput`) |
| `Airbrake.cs`, `GearDrag.cs` | The airbrakes' and retractable gears' drag (state from the gear / airbrake components) |
| `AirIntake.cs`, `AtmosphericScoop.cs` | Ram intake drag and mass flow, the scoop |
| `PatchDrag.cs` | One drag patch's force (other mods' drag: `AeroApi.SetDrag` / `SetPlateDrag`, `Grid/DragPatches.cs`) |

Owned by the gear / airbrake work: `ControlSurface*`, `IControlSurfaceSettings`, `LocalAeroConditions`,
`BlockComponentFactory`, `Airbrake.cs` - change them there.

## Entry points
- `DefaultMappings.Register` (`AeroGridComponent.OnAddedToScene`); `BlockComponentFactory.CreateAll` after every full
  build (`FinalizeStaggeredBuild`), `TryCreate` per added block (`OnBlocksChangedCore`).
- `AeroComponentRegistry.EvaluateAll` (`TryCompute`); the surfaces' inputs from `AeroGridComponent.Surfaces.cs`.
- `AeroApi.GetAeroBlocks` / `GetAeroBlockState` read them. Offline: `Tests/ControlSurfaceTests`, `Tests/SurfaceStallTests`, `Tests/GearTests`.

## Threading
A registry belongs to one grid; created and evaluated on the simulation thread.
