# Api - what other mods see, and what aero asks of them

| File | What |
|---|---|
| `AeroApi.cs` | THE public API (`AeroMod.AeroApi`), reached by other mods by reflection; documented in `docs/API.md` |
| `OrbitalLink.cs` | Aero's calls into the Orbital Mod (`OrbitalMod.OrbitalApi`), bound by name and parameter types |
| `AtmosphereProbe.cs` | The air at any world point (`AeroApi.TryGetAtmosphereAt`), from the atmosphere generators |

Threading: `AeroApi` members may be called from any thread; they read published snapshots and take only small locks.
`OrbitalLink` is never called under a lock of ours. `AtmosphereProbe.Tick` runs on the simulation thread.
The member names and signatures are a contract (Orbital's `tools/check_contract.sh`): do not rename.
