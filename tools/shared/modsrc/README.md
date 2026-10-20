# tools/shared/modsrc - source both mods carry, written once

The Aerodynamics Mod and the Orbital Mod compile separately and share no code at run time: each compiles its own copy of
these files. They are written ONCE, here (until 2026-10-19 in the Orbital repo, copied into Aero by its
`tools/sync_settings_ui.sh` - that script is now a wrapper of this one).

- `files/` - every file under it is shared, at the same path in each mod (`files/Scripts/Simulation/Ui/SettingsTab.cs` ->
  `<mod>/Scripts/Simulation/Ui/SettingsTab.cs`). Today: the in-game settings menu (`Ui/AvaloniaReflect.cs`, `SettingsModel.cs`,
  `SettingsTab.cs`) and its offline test (`Tests/SettingsUiTests/Program.cs`); the world settings' carriers
  (`Save/WorldCarriers.cs`: which saved text a world applies, when a world is over - planetless worlds too) and their test
  (`Tests/WorldCarrierTests/Program.cs`). The namespace is a placeholder:
  `namespace __MOD__;` / `using __MOD__;` (those lines only) become `AeroMod` / `OrbitalMod` in the copies.
- Never here: each mod's adapter (`Scripts/Simulation/Ui/SettingsTab.Mod.cs`: its id, title, files, registry calls) and its
  test project files (`*.csproj`). The two adapters' `ModId`s must differ (the check says so).
- More shared files (e.g. the world-switch fix's `Scripts/Simulation/Reload/ScriptResultsCleanup*.cs` and
  `Tests/ReloadTests/Program.cs`): put them under `files/` at their mod path, placeholder namespace, and sync.

| command | what |
|---|---|
| `tools/shared/modsrc/sync.sh` | write both copies: this checkout's mod, and the Orbital Mod at `$ORBITAL_ROOT` (else the sibling `../Orbital Mod`) |
| `tools/shared/modsrc/sync.sh --aero <dir> --orbital <dir>` | write the copies into those checkouts (either one alone: that one) |
| `tools/shared/modsrc/sync.sh --check --aero <dir> [--orbital <dir>]` | compare only, exit 1 on a difference (aero's `tools/test_fast.sh`: aero; orbital's `tools/check_contract.sh`: both) |

Edit here, sync, then run both mods' fast gates. Never point it at the live mod folders from a worktree.
