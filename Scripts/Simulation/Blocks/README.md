# Blocks - the mod's own blocks: components and terminals

Per-block components and terminals for the mod's own blocks (3x3x1 control surfaces, hinge flaps, gear variants). The
block definitions and models ship in the companion mod "Aerodynamics Mod Blocks"; the GUIDs are in
`Components/AeroBlockGuids.cs`.

| File | What |
|---|---|
| `AeroSurfaceSettingsComponent.cs` | A 3x3x1 control surface's settings (server); `AeroSurfaceSaved`: the old position-matched save lines, read only (one release), and the thrusters' client-copy follow |
| `AeroBlockSave.cs`, `AeroBlockSaveForm.cs` | Per-block saved data (2026-10-15): the surface, gear, airbrake and thruster components' `[Init]` / `[Serializer]` on the engine's `EntityNameSessionComponentObjectBuilder` (fixed component ids), and the keys' text + the old lines' reader (pure: `Tests/BlockSaveTests`). `docs/ARCHITECTURE.md` section 11 |
| `AeroSurfaceBlockDetailModel.cs` | Its terminal page |
| `DetailModelPairing.cs` | Lets a mod `[BlockDetailProvider]` open in the control panel without the replication crash (also used by the thruster's, gear's and airbrake's models) |
| `ModDetailTypeInfo.cs` | Hand-built member list for a mod panel model (`GetTypeInfo()`) |
| `AeroPanelProbe.cs` | DEV: opens a surface's panel as the terminal does (`aeroset AeroPanelProbe.Gen n`), `[AERO-PANEL]` PASS / FAIL |
| `AeroFlapComponent.cs` | Client: turns a surface's flap subpart to the deflection `ThrustTorque.PublishFlaps` published |
| `HingeSurfaces.cs` | Vanilla hinges carrying flaps driven as control surfaces (`Control.HingeSurfaces`, off by default) |
| `AeroGearVariants.cs` | Gear block variants (owned by the gear work: moved here, not edited) |

## Entry points
- Injected by `InjectAeroComponents` (`AeroBlockGuids.SurfaceServerPrefabs` / `FlapClientPrefabs`).
- `AeroSurfaceSaved.ApplyPending` (`TryCompute`, server) and `AeroSurfaceSaved.ClientStep` (`TryCompute`, client); `AeroSurfaceSaved.Load` (`AeroSettings.WorldLoaded`).
- The blocks' `[Init]` / `[Serializer]` (`AeroBlockSave.cs`): the game's entity load / save.
- `HingeSurfaces.Drive` (`AeroGridComponent.Surfaces.cs`); `AeroFlapComponent`'s job (`OnAeroFlap`), which also ticks `AeroPanelProbe`.

## Threading
- Server: settings components are read by the grid's control step (`AeroGridComponent`, per grid); `HingeSurfaces.Drive` runs there.
- Client: `AeroFlapComponent`'s job after `RenderSubmissionBegin`. Detail models: the terminal's thread, host / single player.
- One scene per thread: the component lists (`All`) hold both scenes' copies of a thruster; each step reads its own scene's only
  (`ReferenceEquals(x.Entity.Data.Scene, scene)`) - the client copies' share of a restore runs in `ClientStep`, and the save
  compares a client copy by `SeenAt` (its own step's read). Tests/SceneTests checks it.
