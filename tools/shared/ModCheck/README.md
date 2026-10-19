# ModCheck — compile SE2 mod scripts offline, the way the game does

Launching the game to find a compile error costs minutes. ModCheck reproduces the in-game script
compile of SE2 **2.4.0.95** (`Game2/VRage.Scripting.dll`, `SpaceEngineers2.dll`) in about five
seconds. Two tools:

| | what | time | use |
|---|---|---|---|
| `ModCheck.csproj` | `dotnet build` with the game's references, generators, whitelist analyzer | ~5 s | every change |
| `gameverdict.sh` (`GameVerdict/`) | the game's own compile code, run offline: its exact pass/fail | ~10 s | before a release, after a game update, when in doubt |

```
dotnet build tools/ModCheck/ModCheck.csproj                                  # Orbital Mod (default)
dotnet build tools/ModCheck/ModCheck.csproj "-p:ModDir=C:\...\Mods\Aerodynamics Mod"
bash tools/ModCheck/gameverdict.sh "C:\...\Mods\Aerodynamics Mod" AeroMod    # exit 1 = the game rejects it
```

## ModCheck.csproj

- **References:** exactly `ModCompilationDescriptor.MetadataReferences`:
  `GameCompilationDescriptor.MetaDatas` (VRage.Core, VRage.Library, VRage.DCS, VRage.Core.Game,
  Game2.Simulation, Game2.Client, VRage.Physics, and since 0.95 System.Text.Json,
  System.Collections.Concurrent, System.Linq.Expressions) plus
  `CompilationDescriptorDefaults.DefaultMetadataReferences` (CoreLib, System.Runtime, System.Linq,
  System.Collections, System.Collections.Immutable). Nothing else, so a type from an unreferenced engine
  assembly fails with CS0012 here exactly as in game. The three 0.95 additions are *referenced, not
  whitelisted*: their types resolve, and using one is VRS1001 (namespaces that also live in CoreLib, such
  as `System.Collections.Concurrent`, are allowed).
- **Generators:** Keen's `VRage.DCS.Generator` and `VRage.Library.Generator` (job groups, `[Component]`
  wiring), which need `VRageBundleName` / `VRageBundleVersion`.
- **Whitelist:** Keen's own `WhitelistDiagnosticAnalyzer` (VRS1001) from `VRage.Scripting.dll`, fed
  `AllowedSymbols.Mod.txt`. `WhitelistGen` writes that file with the game's own code (see below); it is
  byte-identical to the text the game feeds its analyzer (`gameverdict.sh <mod> --dump-whitelist=<file>`).
- **Pragma-suppressed errors fail the build** (see the next section).
- **Injected attribute:** `InjectedScriptingAssembly.cs` is what `ScriptingAssemblyInjectorPreProcessor`
  adds in game. Without it the analyzer flags the mod's own types.
- **MODVAR1 (ours, `VarArrayAnalyzer/`):** a `var` whose type is an array of a type declared in the mod
  (the 2026-10-02 Orbital `EncounterFrames` incident). The suppressed-error check below now catches the
  underlying VRS1001 as well; MODVAR1 stays as the clearer message.

Not run by ModCheck: the KCG analyzers (`KN*`, Keen code-style rules, all warnings in 0.95) and the
documentation-comment diagnostics (`DocumentationMode.Diagnose` in game: CS1570/1573/1574/0419/1734,
warnings). GameVerdict runs both.

Known gaps: only `Scripts/Simulation` is compiled (the game compiles it as `<bundle>.Simulation`; add
`Scripts/Client` if a mod ever uses it), and everything is regenerated from the installed DLLs, so it
matches the installed game version only.

## `#pragma warning disable` and what the game does with it

The mods open every file with a blanket `#pragma warning disable`. That pragma hides **analyzer errors**
too: VRS1001 (banned symbol) is an analyzer diagnostic of Error severity, and a plain `dotnet build`
drops it as suppressed. The game does not: `CompilationDescriptorDefaults` compiles with
`reportSuppressedDiagnostics: true`, and `ScriptCompiler` fails the mod (`ScriptLoadingException`) when
`DiagnosticBag.HasErrors` - which counts every Error diagnostic, suppressed or not. So a pragma never
makes banned code legal; it only made the old ModCheck blind to it (that is how `EphemerisSample[]` once
passed ModCheck and broke the world load).

ModCheck now matches the game: `ModCheck.csproj` sets `ErrorLog` (csc then reports suppressed
diagnostics into `obj/modcheck.sarif`, marked `"suppressions"`), and the `ModCheckSuppressedErrors`
target, after `CoreCompile`, turns every suppressed Error in that log back into a build error:

```
Control.cs(6,54): ModCheck error VRS1001: The symbol System.Text.Json.JsonTokenType is banned for use in
  scripts [hidden here by #pragma warning disable, but the game fails the compile on it]
```

Compiler errors (CSxxxx) were never suppressible, so they always showed.

**Recommended pragma** for mod files - a narrow list instead of the blanket one:

```
#pragma warning disable CS0649, CS8632, CS1591, CS1573, CS1570, CS1574, CS0419, CS1734
```

CS0649 (field never assigned - serialized/default-valued fields), CS8632 (nullable annotation outside a
`#nullable` context), and the doc-comment set the game reports under `DocumentationMode.Diagnose`:
CS1591 (missing XML comment), CS1573 (param without `<param>`), CS1570 (badly formed XML), CS1574 /
CS0419 (unresolvable / ambiguous `cref`), CS1734 (`<paramref>` to a missing parameter). Verified with
GameVerdict on the integrated Aero (182 files) and Orbital (212 files) trees: with this list in place of
the blanket pragma, the only warnings left in pragma'd files are dead code (CS0169, CS0414, CS0219,
CS8321 - meant to stay visible), Keen's KCG style rules (KN*), and AD0001 (a Keen analyzer crashing,
not location-bound). Both trees compile with 0 errors either way.

## GameVerdict

`GameVerdict/Program.cs` sets the `GameApp.AddScripting` statics verbatim, builds the real
`ModCompilationDescriptor` (references, options, whitelist via the real `GameWhitelistProvider`, every
analyzer and generator the game attaches - 45 analyzers, 2 generators), runs the body of
`ScriptCompiler.CompileAsync` (copied; only the parallel scheduling is left out) and asks the real
`DiagnosticBag.HasErrors`. Errors are printed with `[pragma-suppressed]` where a pragma hid them;
`--all` adds every warning. Exit 0 = PASS, 1 = FAIL. It loads the game's DLLs from `Game2` (override
with `SE2_GAME2`) but starts nothing of the game.

## After a game update

1. Diff `GameApp.AddScripting` (decompile `SpaceEngineers2.dll`) against the two arrays in
   `WhitelistGen/Program.cs` and `GameVerdict/Program.cs`, and `ScriptCompiler.CompileAsync` against
   GameVerdict's `Run`.
2. Regenerate the whitelist; WhitelistGen prints the seed assemblies and the reference list, which
   `ModCheck.csproj`'s `<Reference>`s must match. Bump `NetRt` to the newest installed 9.0.x runtime (the
   game rolls forward to it).

```
dotnet run --project tools/ModCheck/WhitelistGen -- "<Game2 dir>" tools/ModCheck/AllowedSymbols.Mod.txt
```

3. Check: `gameverdict.sh <mod> --dump-whitelist=x.txt` must equal the new `AllowedSymbols.Mod.txt`, and
   ModCheck and GameVerdict must agree on both mods.

**2.4.0.95 changes found this way:** a 10th mod whitelist seed `typeof(ImmutableArray)`
(System.Collections.Immutable and System.Collections.Frozen allowed); three new compile references (above).
Also corrected: `typeof(Blob)` is `Keen.VRage.Core.Game.Data.Blob` in **VRage.Core.Game**, not a
VRage.Library duplicate as the old WhitelistGen assumed - so 33 `Keen.VRage.Core.Game.*` namespaces
(Session, GetWorldTransform, HierarchyComponent, VolumeDefinition, ...) were missing from the old
whitelist, and stripping the pragmas used to show false VRS1001s for them. Since WhitelistGen now calls
the game's `ConfigureWhitelist` instead of re-implementing it, its namespace choice (one type per
namespace per assembly, skipped if that type is `[EditorBrowsable(Never)]`) is the game's by
construction.

**WhitelistPatcher is not needed on 0.95.** `ScriptWhitelist.AllowNamespace` now ignores a namespace
allowed twice with the same rule (it only throws on conflicting rules), so the duplicate-namespace crash
that `tools/WhitelistPatcher` patched out is gone in Keen's code; the installed 0.95
`VRage.Scripting.dll` is unpatched (the patcher's version-keyed backups in `Game2` stop at 2.4.0.77),
and the real `ConfigureWhitelist` runs cleanly over the 10 seeds.

## Dead-code warnings

The blanket pragma hides unused locals and fields, and ModCheck's `NoWarn` hides some too.
`bash tools/ModCheck/warnings.sh "<mod dir>"` compiles a scratch copy with all `#pragma warning disable`
lines stripped and lists only those warnings (CS0168/0219/0169/0414/0649/0162/0067).
