# Launch - creative quick launch

Seated in a cockpit in a creative world, `Ctrl` + `T` (client settings `QuickLaunch.Key` / `QuickLaunch.Modifier`) lifts the
ship and everything joined to it to `QuickLaunch.AltitudeM` over the ground, turns it level on its heading and sends it
along the cockpit's nose at `QuickLaunch.SpeedMps` (0: cruise - its top speed at 3/4 throttle; 150 m/s without one).
World switch `QuickLaunch.Creative`. The Orbital mod had it in its 1.0.1; since 2026-10-18 ("ctrl+T should be in aero
creative") aero owns it and the Orbital mod only moves the ship for aero (`OrbitalApi.TryQuickMove`).

| File | What |
|---|---|
| `QuickLaunch.cs` | Client: the settings' fields, the key (one ask per press - `QuickLaunchRules.KeyEdge`: up to down, never while held, never a down not seen up first; seated, not while a screen takes the keyboard, one ask a second), the ask (only the local player's identity), the answer's toast, the cockpit target's turn after aero's own move (`TurnTarget`), `ResetWorld` |
| `QuickLaunch.Server.cs` | Server: the teleport-phase job on the planets' settings carrier (`AeroWorldSettingsComponent.OnQuickLaunch`, before the simulation, as the game's fast travel) - its own character of the asking player, the seat it pilots, that seat's grid; creative (`GameModeSessionComponent.IsCreative`); where, the turn, the speed; the move through `OrbitalLink.TryQuickMove` when the Orbital mod is loaded, else `QuickLaunchMove` |
| `QuickLaunchMove.cs` | Aero's own Havok-safe move, ONLY without the Orbital mod: the joined set (`IFindConnectedEntities`), nothing if any of it is static, every place worked out first, world areas released, velocities set, spin turned, contacts masked |
| `QuickLaunchRules.cs` | Engine-free (`Tests/QuickLaunchTests`): the key's press (`KeyEdge`) and an ask's freshness (`Fresh`, `AskMaxAgeS`), may it go, where it ends (over the ground, never lower, never into it; the clamp room it leaves the Orbital mod's space border), the level turn, the speed, the refusal lines, the binding |

## Test spawns (2026-10-20)
Creative / admin test enemies: `Ctrl` + `M` an enemy missile, `Ctrl` + `B` an enemy bogey, `Ctrl` + `N` every test ship removed
(client settings `Spawn.*Key`, `Spawn.Modifier`; world `Spawn.Allowed`, `Spawn.DespawnMinutes`). A blueprint by name
(`Spawn.MissileBlueprint` "Missile", `Spawn.BogeyBlueprint` "Bogey") with an Autopilot block, spawned `Spawn.*Distance` away
around the player, made the hostile NPC's by the block's own Enemy switch, its panel Engage with the author's slot (Rocket
Guidance / Dogfight) and the player as its API target. No guidance or dogfight logic here.

| File | What |
|---|---|
| `EnemySpawn.cs` | Client: the settings' fields, the keys (`QuickLaunchRules.KeyEdge`, not while typing, one ask a second), the blueprint loaded by the game's own service (`IBlueprints.TryLoadAsync`, `Blueprint.TryLoadGridAsync` - the paste tool's path), its builders scanned (the Autopilot block, its saved panel and front, the reach), the ask, the toast; `DevAsk` (`AeroApi.RequestTestSpawn`) |
| `EnemySpawn.Server.cs` | Server (the planets' teleport-phase job, beside quick launch): the checks, where (planet height, `IPhysics.QueryAABB`), the paste's preparation (`IGameMode.PrepareForSpawn`), the bundle moved and launched, `SpawnBundleAsync`; then each Autopilot block's panel, `SetAutopilotTarget`, `AskEnemySpawned`; a bogey's cruise; the despawn timer and key (`IEntitySpawner.DeleteEntity`) |
| `EnemySpawnRules.cs` | Engine-free (`Tests/SpawnTests`): who may, the validation and its words, the names, the heading / direction / place / ground / salvo, the aim, the speed, the panel (`Configure`), the timer |

The server reads only its own entities (the asking identity's character, its seat); the ask carries the identity, the
blueprint's builders (what a paste sends) and the player's choices. `ApEnemySwitch.RequestSpawned` (Ask 3) is the Enemy
switch's test-spawn path: the world's Enemy rule and the asker are not judged (the spawn's own rule was), the construct must
still be all the spawning player's.

## Entry points
- `QuickLaunch.ClientTick`: `AeroClientHost.Frame` (the client's session tick, the render camera's job). `TurnTarget`:
  `AeroEntryFxComponent.EntryJob` (the seated client grid's own effects job).
- `AeroWorldSettingsComponent.QuickLaunchJob` (`OnQuickLaunch`): only while an ask waits (a reference read otherwise).
- `AeroApi.RequestQuickLaunch` (the Orbital mod's DevHarness key `quicklaunch`): the key's ask, its checks skipped.

## Threading
The client tick and the target turn run on the client effects jobs (the turn on the seated grid's own job: it writes its
own `TargetControlData`). The server step runs on the server's teleport phase (one planet's job takes the ask) and reads
only server entities; `OrbitalLink.TryQuickMove` is called with no lock of ours held. Across: `QuickLaunch._ask` (the
ask, swapped whole, stamped: the server drops one older than 1 s), `_devAsk` (the harness's ask: a stamp, taken once, dropped
when older than 1 s), `PendingSay` (the answer), `_turn` (the target's turn) - all swaps, no locks. One effects job at a
time runs the key (`_busy`): the frame's time claim alone let a second job read the same press while a slow first one ran.

2026-10-18: the Orbital mod's copy lifted ships with nobody asking (release in-game run). Every ask here is one press,
taken once, never late - a key held through a load or a seat change, a stale device state, an ask or harness ask nobody
took in time: none of them asks (`Tests/QuickLaunchTests` [one launch per press], [the harness's ask: one-shot]).
