# VrbReader - read a blueprint's `grid.json.vrb` offline

Deserializes a blueprint with the game's own serializer (no game launch) and prints its blocks.

```
VrbReader <blueprint dir | grid.json.vrb> [--json] [--sim]
  --json   blocks as JSON on stdout (def GUID, pos, orient) - the input of tools/gridgen/bp2boxes.py, bp2struct.py, bpinfo.py
  --sim    run the Aero.Core flight sim, write an HTML report
```

## Build

```
dotnet build -c Release tools/shared/VrbReader        # -> bin/Release/net9.0/VrbReader.exe
```

- Compiled against the installed game's DLLs (`Game2\VRage.*.dll`, `Game2.Simulation.dll`), **never copied** into
  `bin/` (`Private=false`). At run time `GameAssemblies.cs` (a module initializer) resolves every game assembly from
  the game folder, so the exe always runs on the game that is installed.
- Game folder: `SE2_GAME_DIR` (env) at run time, else the `SE2Dir` the build used. Another install:
  `dotnet build -c Release -p:SE2Dir="<...>\SpaceEngineers2\Game2"`. Vanilla content is read from
  `<SE2Dir>\..\GameData\Vanilla\Content`.
- `bin/` and `obj/` are gitignored: never commit the exe or game DLLs.

## After a game update

Usually nothing. Rebuild (`dotnet build -c Release --no-incremental`) only when it fails with `MissingMethodException`,
`TypeLoadException` or a compile error - an API it uses changed.

The old build copied the game DLLs into `bin/`; after the 4.0.0.3151 update those stale copies clashed with the game's
own (`FileLoadException ... manifest definition does not match (0x80131040)` at `Assembly.LoadFrom`). Copies left in
an old `bin/` are harmless now (they are not in `VrbReader.deps.json`, so nothing loads them).
