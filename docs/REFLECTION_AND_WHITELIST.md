# Reflection in the Aerodynamics mod, and what the SE2 mod whitelist would need

**Game version:** SE2 2.4.0.77 (`D:\aero\decompiled\SE2_VERSION.txt`). **Written:** 2026-10-03 (moved here from `D:\aero\docs\` the same day).

**Companion document:** the Orbital Mod's `docs/REFLECTION_AND_WHITELIST.md` holds the Orbital inventory, Orbital's calls into this mod (its §3.10), and the **shared** sections: how the mod compiler gates code (its §2), the combined assembly ask to Keen (its §5.0) and the general risks (its §6). This file covers only the Aero mod's own reflection.

**Keep current:** every new or changed reflection use in Aero goes into this file in the same change; a change to a member other mods reach (`AeroApi`, `AeroEntryFx`) also goes into Orbital's §3.10.

**Path prefixes used below:**
- `O/` = `C:\Users\slob\Documents\SpaceEngineers2\Mods\Orbital Mod\Scripts\Simulation\`
- `A/` = `C:\Users\slob\Documents\SpaceEngineers2\Mods\Aerodynamics Mod\Scripts\Simulation\`
- `PRB` = `O/Render/PlanetRenderBridge.cs`
- Engine paths are relative to `D:\aero\decompiled\`.

**How to read the line numbers:** they are a snapshot of the working tree on 2026-10-03, so re-check before quoting them to anyone.

**What could not be verified:**
- VRage.UI is not decompiled, so view-model base types and `OnPropertyChanged` were not resolved.
- `VRage.Scripting.dll`, which holds `ScriptWhitelist` / `WhitelistDiagnosticAnalyzer`, is not decompiled either. Its namespace-matching semantics are therefore inferred from ModCheck behaviour (see Orbital's §2).

---

## 1. Summary

- About 40 source lines call a reflection API, in 5 files. Grep also hits `AeroWork.cs` and `DampedShadowedDragModel.cs`, but those are plain delegate `.Invoke`.
- 12 distinct engine members are reflected, in VRage.Game.Client (particle effects, the grid's render parent), VRage.Render (effect parameters, spot light, render roots), VRage.DCS (protected `Component.SetData<T>`, internal `EntityCompositeDefinition.Assign`), VRage.Core.Game (private `PrefabDefinition._entity`) and Game2.Simulation (private block `_definition` fields, `CubeBlockComponent.Definition` to dodge a CS0012).
- **Shipped features that live on reflection:** re-entry plasma and the flight field (sparks, vapour, tip trails, spot light), definition injection of the aero components, thrust torque (thruster / gyro definitions), engine control overrides.
- **The Aero public API (`AeroApi`, `docs/API.md`) has no engine reflection.** Other mods reach it by reflection on their side; Orbital's calls are inventoried in Orbital's §3.10.
- **Why reflection at all:** see Orbital's §2. In short, mod scripts compile only against VRage.Core, VRage.Library, VRage.DCS, VRage.Core.Game, Game2.Simulation, Game2.Client and VRage.Physics (+ CoreLib); the namespace whitelist allows `System.Reflection`; private, protected and internal members need reflection regardless.

---

## 3. Inventory
### 3.9 Aerodynamics Mod (all shipped unless marked)

| site | target | assembly | access | purpose | failure |
|---|---|---|---|---|---|
| A/Integration/EntryFxBridge.cs:37-41, 52, 57-58, 108-124 | `Keen.VRage.Game.Client.Effects.Components.ParticleEffectsSessionComponent.TrySpawnEffect(RelativeTransform, ParticleEffectBaseDefinition, ParticleEffectUserParameters, IRenderParent, int = -1, EntityType = Default, string? = null, bool = true)` (`VRage.Game.Client/.../ParticleEffectsSessionComponent.cs:703`). Found by assembly scan; the session component is found with `IsInstanceOfType` on every spawn. | VRage.Game.Client | public / public | Spawn the re-entry plasma on the grid | Type missing: `Fail` (56), logged once, **plasma off for the session**. A null handle retries in 60 frames (AeroEntryFx.cs:259). |
| EntryFxBridge.cs:53, 77-89 | `Keen.VRage.Game.Client.Render.RootEntityRenderComponent` (the `IRenderParent`) | VRage.Game.Client | public | Render parent | Grid marked `NoRender` (AeroEntryFx.cs:255), counter only |
| EntryFxBridge.cs:54, 64-68, 116, 141-146 | `Keen.VRage.Render.Data.ParticleEffectUserParameters` fields `ColorMultiplier`, `GravityForce`, `EmitterScaleMultiplier`, `EmitterSizeMultiplier`, `VelocityMultiplier` (`VelocityDirection` is resolved but unused) | VRage.Render | public struct / public fields | Effect size, colour and streak speed (**about every frame during entry**) | Required fields: `Fail`. Optional ones (`?.`): **silent**. Boxed struct mutated in place. |
| EntryFxBridge.cs:55, 59-63, 130-161 | `ParticleEffectHandle.UpdateTransform(RelativeTransform)`, `SetParameters(in ParticleEffectUserParameters)`, `FixEffectTime(bool, TimeSpan)`, `StopEmitting(bool)`, `DisposeAfterParticlesDie()` (`ParticleEffectHandle.cs:130-257`) | VRage.Game.Client | public / public | Move, update, hold the effect time, stop | caught: `Fail`, logged once. **After `Fail`, existing effects are still driven and can throw every frame** (each throw costs about 200 ms in SE2, per `D:\aero` memory). |
| A/Integration/EngineOverrides.cs:116-170 (callers PhysicsHack.cs:341, 362; AeroSimJob.cs:124, 208; OffsetThrustJob.cs:339-341) | `Keen.VRage.DCS.Components.Component.SetData<T>(T)` (**protected**, MakeGenericMethod on every call; `Unsafe.As<Array, object[]>` to dodge the `T[]` ban) | VRage.DCS | **protected** | Write `OverriddenThrustData` / `ControlData` | Logged on **every** exception (156). **Not needed: the body is `Data.Set(value)`** (`VRage.DCS/Keen.VRage.DCS.Components/Component.cs:48-52`). |
| A/Integration/OffsetThrustJob.cs:779-804, 689, 844-855 | `ThrusterComponent._definition` → `ThrusterDefinition.ThrustPower` / `.ThrustDirection` / `.ThrustClass` | Game2.Simulation | **private** field | Thruster power and direction for thrust torque | One log line (798). Then **thrust torque is silently inert** (maxPower = 0). |
| OffsetThrustJob.cs:861-886, 916-920 | `GyroscopeComponent._definition` → `GyroscopeDefinition.MaxTorque` | Game2.Simulation | **private** field | Gyro torque | Logged (880); `catch {}` (920) |
| A/Components/BlockComponentFactory.cs:18-19, 133-144 | `CubeBlockComponent.Definition` → `Definition.Guid` | Game2.Simulation / VRage.Library | public / public | Match custom aero blocks. Reflected **only to dodge CS0012** (`CubeBlockDefinition : MaxHealthComponentDefinition` is in **VRage.Game**). | **Silent**: the custom blocks get no aero component and nothing is logged |
| A/Injection/Injections.cs:40, 45 | `EntityCompositeDefinition.Assign` (**internal**), `PrefabDefinition._entity` (**private**). Same as Orbital §3.8. | VRage.DCS / VRage.Core.Game | internal / private | Inject the Aero components into prefabs | Same as Orbital: silent and inconsistent, or an NRE |
| EngineOverrides.cs:41-75 (**dead**: `PhysicsHack.UncapSpeed`, 151-154, has no callers) | `VelocityLimitProvider._linearVelocityLimit` | Game2.Simulation | **private** | Speed cap | logged |
| EngineOverrides.cs:80-113 (**unreachable**: the only caller, AeroSpeedUncap.cs:29, passes `apply:false`) | `PhysicsSessionConfiguration.MaximumSpeedLinear` / `.GravityMultiplier` private setters | Game2.Simulation | **private set** | Config override | logged |
| A/Fx/FlightFieldHost.cs `SpawnEffect`, `FixTime`, `DisposeEffect` (added 2026-10-03) | The same particle API as EntryFxBridge: `ParticleEffectsSessionComponent.TrySpawnEffect(...)`, `ParticleEffectUserParameters` fields (`EmitterScaleMultiplier`, `VelocityMultiplier`, `ColorMultiplier`, `EmitterSizeMultiplier`, `ParticleAspectRatio`, `VelocityDirection`), handle `SetParameters`, `FixEffectTime(bool, TimeSpan)`, `Dispose(bool)`. `MethodInfo`s cached statically after the first resolve. | VRage.Game.Client / VRage.Render | public | The flight field: plasma sparks, transonic vapour, tip trails (hundreds of effects per grid; strengths every 0.25 s) | Spawn failure: the field's "why" in its status, spawning stops for that field. |
| A/Fx/FlightFieldHost.cs `ResolveRender`, `SpawnSpot`, `StopSpot`, `UpdateRootTransform` (added 2026-10-03) | `RenderEngineComponent.Instance.RenderContracts` (public static field + property), `RenderContracts.CreateRootEntity(string, WorldTransform, bool)` and `CreateSpotLightEntity(...)` (named-parameter fill), the root's `UpdateTransform(WorldTransform)`, `Dispose()` | VRage.Render | public | The plasma's spot light (one per field, on its own render root moved with the grid every frame) | Not resolved: no spot, "why" in the status; the sparks still run. |

**Stale comments in Aero:**
- `EntryFxBridge.cs:11` says it is "THE AERO MOD'S ONLY REFLECTION" (the flight field, Fx/FlightFieldHost.cs, now reflects too), and `EngineOverrides.cs:18` says there are "three" holdouts. There are 12 targets.
- `PhysicsHack.cs:3` has an unused `using System.Reflection`. `PhysicsHack.cs` itself has **no** reflection: it is a typed facade over the public `DEntityContext` API.

### 3.10 Cross-mod
- Aero itself reflects into no other mod. Orbital's calls into Aero (the shipped entry-source hook through `AeroApi`, dev reads of `AeroEntryFx`, `aeroset` / `aeroget`) are in the Orbital Mod's `docs/REFLECTION_AND_WHITELIST.md` (§3.10).

### 3.x Changes since the first audit (kept current per the standing rule)

- **2026-10-03 Aero `AeroEntryFx.TryGetFlow` (now with lift outs) / `Flow` / `WingTips` / `EntryStrength`** (plus internal `AeroGridComponent.FxWings` / `FxCellGeo`) are new members written for the cross-mod read in §3.10 (not reflection inside Aero).
- **2026-10-03 flight effects moved into Aero (`Fx/FlightField.cs`, `Fx/FlightFieldHost.cs`)**: the dev spike's plasma, vapour and tip trails, per grid on the client, regime-gated. Aero now reflects the particle API (as EntryFxBridge) and `RenderContracts` root + spot light itself (§3.9 rows); it reads its own aero model directly (no cross-mod reflection for the effects). Orbital's PlasmaSpike remains as a dev tool until approved for removal.
- **2026-10-03 `AeroMod.AeroApi` (Aero's public API, version 2)**: one versioned surface for other mods - flight (airspeed, Mach, density, q), forces, lift, entry heating, wing tips, entry sources, effect suppression - all signatures in .NET / engine types for reflection callers. Orbital's EntryHost now registers through it; its other Aero reads are dev-only (PlasmaSpike).
- **2026-10-03 cross-mod hook hardened**: `AeroEntryFx.External` (a single settable static) is **gone**, replaced by the entry-source contract (§3.10); Orbital registers through it. `tools/check_contract.sh` in Orbital's fast gate fails on a contract or shape mismatch.
- **2026-10-03 vapour effects (Aero `tools/particles/gen_vapor.py`, Orbital PlasmaSpike Mode 6)** use the same particle APIs as §5.5 (`TrySpawnEffect`, `SetParameters`, `FixEffectTime`); no new engine members.

---

## 5. Whitelist demands (for Keen)

The combined assembly ask (VRage.Render first, then VRage.Game.Client, VRage.Game, ...) is in Orbital's §5.0. Aero's part of it is **VRage.Game.Client**, **VRage.Render** and **VRage.Game**.

### 5.5 Particle effects (shipped re-entry plasma in Aero; dev plasma in Orbital)
- **(R) VRage.Game.Client + VRage.Render.** The minimal set:
  - `ParticleEffectsSessionComponent.TrySpawnEffect(RelativeTransform, ParticleEffectBaseDefinition, ParticleEffectUserParameters, IRenderParent, …)` (`ParticleEffectsSessionComponent.cs:703`), or the `IParticleEffects` interface (`VRage.Game.Client/.../IParticleEffects.cs`);
  - `ParticleEffectHandle`: `SetParameters(in …)`, `FixEffectTime(bool, TimeSpan)`, `UpdateTransform(RelativeTransform)`, `StopEmitting(bool)`, `DisposeAfterParticlesDie()`, `Dispose(bool)` (`ParticleEffectHandle.cs:130-257`);
  - `ParticleEffectUserParameters` (VRage.Render) and its public fields;
  - `RootEntityRenderComponent` as the `IRenderParent` of a grid;
  - (dev) `ParticleEmitterDefinition.AnimationParameters` / `ParticleAnimationParameters`.
- **(N) Smaller alternative:** a façade in Game2.Client or VRage.Core.Game that takes only referenceable types. For example, `Entity.SpawnEffect(StringId effect, RelativeTransform, float scale, ColorSRGB, Vector3 gravity) → handle { SetTransform, SetParams, HoldTime, Stop }`.
- **Without Keen:** nothing. Game2.Client's `BlockEffectsComponent` and similar are definition-driven and expose VRage.Game.Client types.
- **Why:** the Aero mod's shipped features - re-entry plasma, the flight field's sparks, vapour and tip trails - depend on it entirely.

### 5.6 Spot lights (shipped: the flight field's plasma light, Aero)
- **(R) VRage.Render**: `RenderContracts.CreateSpotLightEntity(...)` and its handle. It is already public.

### 5.11 Definitions at load time
- Same as Orbital's §5.11: `EntityCompositeDefinition.Assign(Builder)` is **internal** and `PrefabDefinition._entity` is **private**; we need a supported way for a definition post-processor to add components to a prefab's composition.

### 5.12 Block and component definitions (shipped in Aero and Orbital)
- **(P) Public `Definition` getters** on `ThrusterComponent`, `GyroscopeComponent`, `KillingFieldComponent` and `GeneratorBaseComponent`.
- **Without Keen (Aero):**
  - `Entity.Data.TryGet<ThrustData>()` gives `MaxThrustPower`, `Direction` and `ThrustClass` (Game2.Simulation, public). Caveat: power is 0 while the thruster is unpowered.
  - `GyroscopeData.MaxTorque` replaces the gyro `_definition`.
  - `Component.SetData<T>` can be replaced by `Entity.Data.Set(value)` because it is identical (`Component.cs:48-52`).
- **(R) VRage.Game** would let `CubeBlockComponent.Definition.Guid` compile. Without it, `((object)block.Definition) is Definition d ? d.Guid : …` may avoid the CS0012, the same way `def is ThrusterDefinition` works at OffsetThrustJob.cs:692. *Not compiled.*

---

## 6. Risks (Aero)

The general picture (private and internal members, boxed-struct mutation, `Activator.CreateInstance` on structs) is in Orbital's §6. Aero-specific:
- **Load-time injection** (`A/Injection/Injections.cs:40-45`): the `TryInvokeMethod` result for the internal `Assign` is ignored, and a missing `_entity` throws an NRE that is not caught.
- **Boxed `ParticleEffectUserParameters`** is mutated in place and passed on; `Activator.CreateInstance` gives it its defaults (EntryFxBridge).
- **Matching by name or shape:** `TrySpawnEffect` is matched by first-parameter type (EntryFxBridge:57-58); `GetMethod` by name (EntryFxBridge:59-63) throws `AmbiguousMatchException` if Keen adds an overload; `CreateSpotLightEntity` (FlightFieldHost) is filled by parameter name.
- **`MakeGenericMethod` every call:** `Component.SetData<T>` (EngineOverrides.cs), guarded against the `T[]` ban by `Unsafe.As<Array, object[]>`.
- **Per-frame reflection:** `SetParameters` / `UpdateTransform` during entry; the flight field's root `UpdateTransform` every frame.
- **Repeat-throw paths:** effects after `Fail` are still driven; `SetData` has no alive check (EngineOverrides.cs:156 logs every time).
- **Silent failures:** `BlockComponentFactory` GUID (aero blocks get no component); `OffsetThrustJob` `_definition` (thrust torque inert after a single log line).

---

## 7. Quick wins that need nothing from Keen
1. Replace `Component.SetData<T>` reflection with `Entity.Data.Set`, and thruster and gyro `_definition` with `ThrustData` / `GyroscopeData`. Delete the dead `UncapSpeed` and `TrySetConfigProperty`. Fix the stale "only reflection" comments.
2. Check the result of `TryInvokeMethod(... "Assign" ...)` and abort the injection consistently. Wrap `_entity` in a null check.
3. `((object)block.Definition) is Definition d` may avoid the CS0012 without reflection (§5.12, not compiled).

*Items marked "not compiled" should be run through ModCheck before relying on them.*
