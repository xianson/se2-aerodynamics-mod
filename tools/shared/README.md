# tools/shared - the dev tools both mods use

Moved here from `D:\aero` on 2026-10-18 (D:\aero is being retired). Everything else under `tools/` is the
Aerodynamics Mod's own; this folder serves the Orbital Mod too.

| path | what |
|---|---|
| `ModCheck/` | the game's script compile + whitelist analyzers, offline (`ModCheck.csproj`, ~5 s) and the game's exact verdict (`gameverdict.sh`); `warnings.sh` lists dead-code warnings the pragmas hide. See `ModCheck/README.md`. |
| `build_mod_content.py` | compiles a mod's `Assets` into its `Content` with the game's own builder (stand-in Vanilla project, texconv from the Mod SDK). `python tools/shared/build_mod_content.py "<mod dir>"` |
| `Publish/` | `stage.py` (stage a script mod for the Workshop) and `Uploader/` (dry run unless approved). See `Publish/README.md`. |
| `modsrc/` | the source BOTH mods carry (the in-game settings menu: `Ui/SettingsTab.cs`, `SettingsModel.cs`, `AvaloniaReflect.cs`, `Tests/SettingsUiTests/Program.cs`), written once in `modsrc/files/` at each file's path in a mod, the namespace a placeholder. `modsrc/sync.sh` writes both mods' copies; `--check` compares them (aero's `tools/test_fast.sh`, orbital's `tools/check_contract.sh`). See `modsrc/README.md`. |
| `launch-se2.bat` | Steam's launch options for SE2: `"<this folder>\launch-se2.bat" %command%` - adds the dev args from one file. |
| `se2-launch-args.example.txt` | the template of that file; the real one is personal and gitignored. |

**The launch args file** (one line: `-start:`, `-projectPaths:`, ...): `SE2_LAUNCH_ARGS` (an env var, a Windows path)
when set, else `se2-launch-args.txt` in this folder. The bat and every harness script that rewrites `-start:` resolve it
the same way, so set `SE2_LAUNCH_ARGS` user-wide (`setx`) if the file lives anywhere else.

**How each mod finds these**
- Aerodynamics Mod: this folder, in the same checkout (`tools/test_fast.sh` builds `tools/shared/ModCheck`).
- Orbital Mod: the sibling checkout, `../Aerodynamics Mod/tools/shared`; `AERO_TOOLS=<path to a tools/shared>`
  overrides it (an Orbital worktree whose sibling is not an Aero checkout with this folder).
- `MODCHECK=<path to a ModCheck.csproj>` still overrides the ModCheck either gate builds (parallel agents: a private
  copy, its own `obj/`).

Keep paths short: the game's content builder cannot open a path of 260+ characters (`stage.py` refuses an out dir
that would make one; keep it like `D:\aero-release\aero-1.0.0`).
