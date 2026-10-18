# Launch - creative quick launch

Seated in a cockpit in a creative world, `Ctrl` + `T` (client settings `QuickLaunch.Key` / `QuickLaunch.Modifier`) lifts the
ship and everything joined to it to `QuickLaunch.AltitudeM` over the ground, turns it level on its heading and sends it
along the cockpit's nose at `QuickLaunch.SpeedMps` (0: cruise - its top speed at 3/4 throttle; 150 m/s without one).
World switch `QuickLaunch.Creative`. The Orbital mod had it in its 1.0.1; since 2026-10-18 ("ctrl+T should be in aero
creative") aero owns it and the Orbital mod only moves the ship for aero (`OrbitalApi.TryQuickMove`).

| File | What |
|---|---|
| `QuickLaunch.cs` | Client: the settings' fields, the key (seated, not while a screen takes the keyboard, one ask a second), the ask (only the local player's identity), the answer's toast, the cockpit target's turn after aero's own move (`TurnTarget`), `ResetWorld` |
| `QuickLaunch.Server.cs` | Server: the teleport-phase job on the planets' settings carrier (`AeroWorldSettingsComponent.OnQuickLaunch`, before the simulation, as the game's fast travel) - its own character of the asking player, the seat it pilots, that seat's grid; creative (`GameModeSessionComponent.IsCreative`); where, the turn, the speed; the move through `OrbitalLink.TryQuickMove` when the Orbital mod is loaded, else `QuickLaunchMove` |
| `QuickLaunchMove.cs` | Aero's own Havok-safe move, ONLY without the Orbital mod: the joined set (`IFindConnectedEntities`), nothing if any of it is static, every place worked out first, world areas released, velocities set, spin turned, contacts masked |
| `QuickLaunchRules.cs` | Engine-free (`Tests/QuickLaunchTests`): may it go, where it ends (over the ground, never lower, never into it; the clamp room it leaves the Orbital mod's space border), the level turn, the speed, the refusal lines, the binding |

## Entry points
- `QuickLaunch.ClientTick` / `TurnTarget`: `AeroEntryFxComponent.EntryJob` (every client grid's effects job; one claims the tick).
- `AeroWorldSettingsComponent.QuickLaunchJob` (`OnQuickLaunch`): only while an ask waits (a reference read otherwise).
- `AeroApi.RequestQuickLaunch` (the Orbital mod's DevHarness key `quicklaunch`): the key's ask, its checks skipped.

## Threading
The client tick and the target turn run on the client effects jobs (the turn on the seated grid's own job: it writes its
own `TargetControlData`). The server step runs on the server's teleport phase (one planet's job takes the ask) and reads
only server entities; `OrbitalLink.TryQuickMove` is called with no lock of ours held. Across: `QuickLaunch._ask` (the
ask, swapped whole), `PendingSay` (the answer), `_turn` (the target's turn) - all reference swaps, no locks.
