# Injection - the mod's components into the game's prefabs

| File | What |
|---|---|
| `EntityInjector.cs` | The `[DefinitionPostProcessor]` over every `PrefabDefinition`; `InjectAeroComponents.Please` decides what each prefab gets (grids, thrusters, client thrusters, tanks, server planets, the client's render camera, the mod's surface / gear / airbrake / autopilot prefabs). `EntityInjector.InjectFlames` gates the flames |
| `Injections.cs` | The composition patch itself (`Injections.Add`; reflection on `EntityCompositeDefinition.Assign` and the prefab's `_entity` - see `docs/REFLECTION_AND_WHITELIST.md`) |

The table of what goes where: `docs/ARCHITECTURE.md` §2. `AeroWorldSettingsComponent` gets a fixed component id
(its settings are saved with it), and so do the saving block components (`AeroBlockSave.Info`: surface, gear, airbrake, autopilot,
thruster - `docs/ARCHITECTURE.md` section 11).

## Entry points
The game's definition loading (`EntityInjector.PostProcess`), once per prefab at load.

## Threading
Definition load (before any scene). Logs `[AERO] Injected ...` for the first few of each kind.
