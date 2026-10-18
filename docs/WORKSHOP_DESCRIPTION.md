# Steam Workshop description

The ModHub upload sends `ModProject.modhub-metadata`'s `Summary` (at most 250 characters) as the item's description.
Paste the BBCode below into the Workshop page's description editor after the first upload. Keep it in step with the
player section of `README.md`.

Summary (250 max):

```
Real aerodynamics: every ship flies by its shape - drag, lift, stall, Mach, re-entry heat - and the air loads its structure until it bends or tears. Adds control surfaces, gear, airbrakes, props, rotors, ramjets, solid rockets. Needs -loadScripts.
```

Description:

```
[h1]Aerodynamics Mod[/h1]
Real aerodynamics for Space Engineers 2. Every ship flies by its own shape: a sleek hull slips through the air, a brick fights it, wings give lift and stall, the sound barrier pushes back, and re-entry heats the hull until it glows. The air loads the ship's structure too, so pulling too hard can bend, crack and tear it apart.

There is nothing to set up on your blocks: the whole ship's shape counts, wings or no wings.

[h2]Requirements[/h2]
[list]
[*][b]Space Engineers 2, version 2.4.x.[/b]
[*][b]Add -loadScripts to the game's launch options[/b]: Steam > Library > Space Engineers 2 > right-click > Properties > General > Launch Options, type [b]-loadScripts[/b]. Without it the game loads the blocks but never runs the mod's scripts, and ships fly exactly as in the unmodded game.
[*]No other mod needed. Works with the [b]Orbital Mod[/b]: its flight HUD shows this mod's stall and loss-of-control warnings, and its re-entries heat the hull.
[/list]

[h2]What it does[/h2]
[list]
[*][b]Drag and lift from the shape[/b] - every exposed face meets the air: pressure, shadowing, skin friction, rotation damping, supersonic wave drag. Wings are found from your blocks, with stall and Mach effects. Wind, ground effect, and damage that changes the forces at once.
[*][b]Flight controls through real parts[/b] - control surfaces move with your steering; gyros, thrusters and surfaces fly together, aware of the air. Thrust acts where each thruster is.
[*][b]Engines that care about speed[/b] - atmospheric thrusters fight the sound barrier and gain from ram air; jets flame out in a sideslip.
[*][b]New parts[/b] (G menu, Aerodynamics): control surfaces, retractable landing gear, airbrakes, turboprops, ducted fans, helicopter rotors, reversible rotors, ramjets (supersonic, hydrogen), solid rocket motors (stack segments on a nozzle).
[*][b]Structure[/b] - air, thrust and contact loads spread through the ship you fly. Overloaded sections wear, fatigue, bend, crack and tear off, with creaks, shake and smoke. Rotor blades break on what they hit.
[*][b]Re-entry[/b] - plasma, vapour cones, wingtip trails, hull heating with heat damage, scorch marks.
[*][b]Warnings[/b] - stall and predicted loss of control; the game's "thrust overload" stays quiet while the wings carry the ship. Optional Mach readout.
[/list]

[h2]Settings[/h2]
In a world: pause menu > Settings > [b]Mods[/b] tab, section Aerodynamics. Basic settings up front, advanced ones per group, reset per group. The host's world settings are saved with the world. Everything can be switched off.

[h2]Performance[/h2]
Small and medium ships cost little. [b]Very large ships[/b] (thousands of blocks) build their drag table in the background for up to about a minute after the world loads or a big change; until then drag and lift are rough. The result is cached, so the next load is instant.

[h2]Known limits[/h2]
[list]
[*]Single player and hosting only (a remote client sees the game's own flight).
[*]English only.
[*]SE2 is in early access: a game update can break the mod until it is updated.
[/list]

[h2]Troubleshooting[/h2]
[list]
[*][b]Blocks are there but nothing flies differently, no Aerodynamics section under Settings > Mods[/b]: -loadScripts is missing.
[*]Reporting a bug: please attach the newest log from %APPDATA%\SpaceEngineers2\Temp\Logs (the mod's lines start with [AERO).
[/list]
```
