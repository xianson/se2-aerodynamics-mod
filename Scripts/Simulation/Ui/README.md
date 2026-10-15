# Ui/ - the in-game settings menu (client)

The Options screen's **Mods** tab (pause menu -> Settings -> Mods), generated from the settings registry
(`Runtime/AeroSettings*.cs`). The shared files are COPIES of the Orbital mod's (its `tools/sync_settings_ui.sh` writes them;
only the namespace line differs, its `tools/check_contract.sh` checks them): edit them there, not here.

| File | What | Shared |
|---|---|---|
| `SettingsTab.cs` | The tab: notice the Options screen, add the shared tab / this mod's section (`Tag` contract `ModSettings/v1`), the rows, the pump, the failure toast, the harness's `Dev`. | yes |
| `SettingsModel.cs` | Engine-free: groups and tiers, each row's control, slider steps (linear, or logarithmic over 200 steps), value text, lock reasons, when an edit applies. **The menu's defaults** (the user's answers, 2026-10-15) are at its top. | yes |
| `AvaloniaReflect.cs` | Avalonia and the game's screens by reflection (types, properties, dispatcher, the open screen, Click). | yes |
| `SettingsTab.Mod.cs` | This mod's adapter: its id and title, files, log tag, the registry's API. | no |

**Entry points / threading.** `SettingsTab.Claim` / `Ticked` from `AeroEntryFx.EntryJob` (the client grids' effects jobs: one per 250 ms
claims it; no grid in the world: no aero section) - it finds `SharedUIComponent` and posts `UiTick`. Everything else runs on Avalonia's UI thread: find
the open `OptionsScreen`, attach once per screen instance, and while the tab is selected (~4 Hz) read the controls, `Set`
through the registry, refresh on a `Version` change. Offline tests: `Tests/SettingsUiTests` (`-- dump` prints the menu).
In game (dev): the Orbital mod's DevHarness `settingsui` reports every section, this one included.
