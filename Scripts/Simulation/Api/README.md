# Api - what other mods see, and what aero asks of them

| File | What |
|---|---|
| `AeroApi.cs` | THE public API (`AeroMod.AeroApi`, `Version` 9; 9: `RequestQuickLaunch`), reached by other mods by reflection; documented in `docs/API.md`. Versions 2-6 and 8 (the settings screen's members: `TryGetSettingUi`, `IsSettingForced`, `CanEditWorldSettings`, `ResetSettingsGroup`, `AreWorldSettingsUnsaved`) |
| `AeroApi.Flight.cs` | (version 7) the per-tick flight state, control surfaces, thruster forces, `WatchGrid` |
| `AeroApi.Structure.cs` | (version 7) the structure and hull heat block by block and face by face, the failure event queue, another heat model's temperatures |
| `ApiGridState.cs` | One grid's published copies for version 7 (filled by `Grid/AeroGridComponent.Api.cs` and `Structure/Server/StructureServer.Api.cs`); `Release` drops a record's arrays once nobody reads it |
| `ApiGridState.Watch.cs` | Its watch windows, one per record (flight, hull faces, structure blocks, heat faces), and when a record's arrays are due to go (engine-free; `Tests/ApiTests`) |
| `SeqRing.cs` | The bounded, numbered event queue, and `ReaderGate` - recording only while someone polls (engine-free; `Tests/ApiTests`) |
| `OrbitalLink.cs` | Aero's calls into the Orbital Mod (`OrbitalMod.OrbitalApi.TryGetEntryState2`, `TryGetEntryState`, `TryGetGridEdits`, `TryQuickMove`) - every one of them, bound by name and parameter types. The ONLY file that names the Orbital mod's types (one link each way: Orbital's `tools/check_contract.sh` fails on another) |
| `CrossModLink.cs` | The binding rule (engine-free; `Tests/ApiTests`): `LastLoadedType` (the last loaded copy of another mod's type, a generation that moves when it changes) and `CrossModBinding<T>` (one member as a typed delegate: bound again on a new generation, retried while missing, a fault wait of 10 s doubling to 300 s) |
| `AtmosphereProbe.cs` | The air at any world point (`AeroApi.TryGetAtmosphereAt`) from the atmosphere generators, and the thin tail above the game's air (`WithTail`) |

## Entry points
- Other mods: any `AeroApi` member (the Orbital mod's build hints, HUD, railed entry, harness - see `docs/ARCHITECTURE.md` §8).
- `OrbitalLink.TryGetEntryState2` (`StructureServer.TickStructureCore`), `OrbitalLink.TryGetGridEdits` (`StructureContacts`),
  `OrbitalLink.TryGetEntryState` (`AeroEntryFx.EntryStateAt`: `HeatScorch`), `OrbitalLink.TryQuickMove` (`Launch/QuickLaunch.Server.cs`).
- `AtmosphereProbe.Tick` (`AeroGridComponent.AeroSimJobCore`, and the server planets' carrier job - `Launch/QuickLaunch.Server.cs` -
  so the point query answers with no grid) / `WithTail` (`AeroSimJobCore`).

## Threading
`AeroApi` members may be called from any thread; they read published snapshots and take only small locks. `OrbitalLink`
is never called under a lock of ours. `AtmosphereProbe.Tick` runs on the simulation thread (server copy).
Version 7's records are published only while read, each under its own watch window: the tick record and the hull's faces
at the end of the grid's simulation tick, the block state and heat faces in its background evaluation; readers copy under
the grid's `ApiGridState.Lock`. A record unread for 10 s is released by the simulation tick (`AeroGridComponent.ApiRelease`;
the evaluation's records only while no evaluation runs).
The member names and signatures are a contract (Orbital's `tools/check_contract.sh`): do not rename.
`Tests/ApiTests` checks the shape from source: no overloads, caller-nameable types, versions 2-6 frozen, every member in
`docs/API.md`.
