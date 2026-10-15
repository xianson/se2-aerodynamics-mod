# Blocks

Per-block components and terminals for the mod's own blocks (control surfaces, hinge flaps, gear variants).

| File | What |
|---|---|
| `AeroSurfaceSettingsComponent.cs` | A 3x3x1 control surface's settings (server; saved in the aero settings text). |
| `AeroSurfaceBlockDetailModel.cs` | Its terminal page. |
| `DetailModelPairing.cs` | Lets a mod `[BlockDetailProvider]` open in the control panel without the replication crash (also used by the thruster's model). |
| `ModDetailTypeInfo.cs` | Hand-built member list for a mod panel model (`GetTypeInfo()`). |
| `AeroPanelProbe.cs` | DEV: opens a surface's panel as the terminal does (`aeroset AeroPanelProbe.Gen n`), `[AERO-PANEL]` PASS / FAIL. |
| `AeroFlapComponent.cs` | Client: turns a surface's flap subpart to the deflection `ThrustTorque.PublishFlaps` published. |
| `HingeSurfaces.cs` | Vanilla hinges carrying flaps driven as control surfaces (`Control.HingeSurfaces`); server, from the grid's control step. |
| `AeroGearVariants.cs` | Gear block variants (owned by the gear work: moved here, not edited). |

## Threading
- Server: settings components are read by the grid's control step (`AeroGridComponent`, per grid); `HingeSurfaces.Drive` runs there.
- Client: `AeroFlapComponent`'s job after `RenderSubmissionBegin`. Detail models: the terminal's thread, host / single player.
