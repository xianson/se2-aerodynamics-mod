# Steam Workshop description

The live text of the Workshop page (captured from Steam 2026-10-18, including the author's own edits). Publish it with
`tools/shared/Publish/Uploader/bin/Release/net9.0/Uploader.exe <stage> --update 3822388536 --description-file <this text>`; edit it here first so an
upload never overwrites changes made on the page.

Summary (250 max):

```
Real aerodynamics: every ship flies by its shape - drag, lift, stall, Mach, re-entry heat - and the air loads its structure until it bends or tears. Adds control surfaces, gear, airbrakes, props, rotors, ramjets, solid rockets. Needs -loadScripts.
```

Description:

```
[i]"The real barrier wasn't in the sky, but in our knowledge and experience of supersonic flight."[/i] - Chuck Yeager

Time to learn how to be a real aerospace engineer. Ships fly by their shape, thrust and loads.

[b]Source, wiki and bug reports:[/b] [url=https://github.com/xianson/se2-aerodynamics-mod]GitHub[/url] - [url=https://github.com/xianson/se2-aerodynamics-mod/wiki]Wiki[/url] - [url=https://github.com/xianson/se2-aerodynamics-mod/issues]Issues[/url]

[h2]Requirements[/h2]
[list]
[*][b]Space Engineers 2, version 4.0.x[/b] (made on 4.0.0.3151).
[*][b]The launch option -loadScripts.[/b] Steam > Library > right-click Space Engineers 2 > Properties > General > Launch Options, type [b]-loadScripts[/b]. Without it the game loads the mod's blocks but never runs its scripts: ships fly exactly as in the unmodded game and there is no Aerodynamics section under Settings > Mods.
[*][b]Single player or hosting a game.[/b] Dedicated servers are not supported yet.
[*][b]Works on its own.[/b] Pairs with the [url=https://steamcommunity.com/sharedfiles/filedetails/?id=3822389424][b]Orbital Mechanics Mod[/b][/url]: its flight HUD shows this mod's stall and loss-of-control warnings, and its re-entries heat the hull.
[/list]

[h2]Features[/h2]
[list]
[*][b]Thrust[/b] - Every thruster pushes from where it sits; unbalanced thrust turns the ship.
[*][b]Air[/b] - Drag, lift and stall from the ship's shape; bricks are slow, wings fly.
[*][b]Dampeners[/b] - Hold the ship with real thrust; hovering needs thrust for 2x the planet weight.
[*][b]Structure[/b] - Loads bend, crack and tear weak joints.
[*][b]Engines[/b] - Jets fade at Mach 1 and gain from ram air; turboprops, ramjets, rotors, solid rockets.
[*][b]Re-entry[/b] - Plasma, hull heating and scorch marks.
[*][b]Speed cap[/b] - 1,000 m/s.
[*][b]Creative[/b] - Ctrl+T quick launch from a cockpit.
[/list]

[h2]New blocks[/h2]
Control surfaces, retractable gear, airbrakes, turboprops, ducted fans, helicopter rotors, the Reversible Rotor, ramjets and solid rocket motors (G menu > Aerodynamics).

[h2]Keys[/h2]
[table]
[tr][th]Key[/th][th]Does[/th][/tr]
[tr][td]Ctrl + T[/td][td]quick launch (creative, seated): lifts the ship to a safe altitude and sends it flying along its nose[/td][/tr]
[/table]

[h2]Old worlds and other mods[/h2]
[list]
[*]Back up a world before adding the mod. Removing it later deletes its blocks from your ships.
[*]Mods that change thrusters, flight physics or the speed cap may conflict.
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
[*]Reporting a bug: please attach the newest log from %APPDATA%\SpaceEngineers2\Temp\Logs (the mod's log lines are tagged AERO).
[/list]

[h2]Licence[/h2]
Free to use in any world, server or video, and free to depend on. Re-uploads only unlisted, private or friends-only. Code may be reused except in a competing aerodynamics or orbital mod; credit appreciated, not required. Commercial use (including by game developers) needs a paid licence. Full text: [url=https://github.com/xianson/se2-aerodynamics-mod/blob/master/LICENSE]LICENSE on GitHub[/url].
```
