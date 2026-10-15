# Api - what other mods see, and what aero asks of them

| File | What |
|---|---|
| `AeroApi.cs` | THE public API (`AeroMod.AeroApi`), reached by other mods by reflection; documented in `docs/API.md` |
| `OrbitalLink.cs` | Aero's calls into the Orbital Mod (`OrbitalMod.OrbitalApi.TryGetEntryState2`, `TryGetGridEdits`), bound by name and parameter types |
| `AtmosphereProbe.cs` | The air at any world point (`AeroApi.TryGetAtmosphereAt`) from the atmosphere generators, and the thin tail above the game's air (`WithTail`) |

## Entry points
- Other mods: any `AeroApi` member (the Orbital mod's build hints, HUD, railed entry, harness - see `docs/ARCHITECTURE.md` §8).
- `OrbitalLink.TryGetEntryState2` (`StructureServer.TickStructureCore`), `OrbitalLink.TryGetGridEdits` (`StructureContacts`).
- `AtmosphereProbe.Tick` / `WithTail` (`AeroGridComponent.AeroSimJobCore`).

## Threading
`AeroApi` members may be called from any thread; they read published snapshots and take only small locks. `OrbitalLink`
is never called under a lock of ours. `AtmosphereProbe.Tick` runs on the simulation thread (server copy).
The member names and signatures are a contract (Orbital's `tools/check_contract.sh`): do not rename.
