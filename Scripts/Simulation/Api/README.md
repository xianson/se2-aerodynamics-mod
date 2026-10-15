# Api - what other mods see, and what aero asks of them

| File | What |
|---|---|
| `AeroApi.cs` | THE public API (`AeroMod.AeroApi`), reached by other mods by reflection; documented in `docs/API.md` |
| `AeroApi.Flight.cs` | (version 7) the per-tick flight state, control surfaces, thruster forces, `WatchGrid` |
| `AeroApi.Structure.cs` | (version 7) the structure and hull heat block by block and face by face, the failure event queue, another heat model's temperatures |
| `ApiGridState.cs` | One grid's published copies for version 7 (filled by `Grid/AeroGridComponent.Api.cs` and `Structure/Server/StructureServer.Api.cs`) |
| `SeqRing.cs` | The bounded, numbered event queue (engine-free; `Tests/ApiTests`) |
| `OrbitalLink.cs` | Aero's calls into the Orbital Mod (`OrbitalMod.OrbitalApi`), bound by name and parameter types |
| `AtmosphereProbe.cs` | The air at any world point (`AeroApi.TryGetAtmosphereAt`), from the atmosphere generators |

Threading: `AeroApi` members may be called from any thread; they read published snapshots and take only small locks.
`OrbitalLink` is never called under a lock of ours. `AtmosphereProbe.Tick` runs on the simulation thread.
Version 7's records are published only while read: the tick record at the end of the grid's simulation tick, the
structure's in its background evaluation; readers copy under the grid's `ApiGridState.Lock`.
The member names and signatures are a contract (Orbital's `tools/check_contract.sh`): do not rename.
`Tests/ApiTests` checks the shape from source: no overloads, caller-nameable types, versions 2-6 frozen, every member in
`docs/API.md`.
